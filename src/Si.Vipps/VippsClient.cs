using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Si.Vipps;

/// <summary>The Vipps Recurring, ePayment, Userinfo and Webhooks APIs.</summary>
public interface IVippsClient
{
    /// <summary>Whether calls that create something new go to the test environment now.</summary>
    Task<bool> IsTestAsync(CancellationToken ct = default);

    Task<VippsCreateAgreementResponse> CreateAgreementAsync(VippsCreateAgreementRequest request, string idempotencyKey, CancellationToken ct = default);
    Task<VippsAgreement> GetAgreementAsync(string agreementId, bool? isTest = null, CancellationToken ct = default);
    Task StopAgreementAsync(string agreementId, string idempotencyKey, bool? isTest = null, CancellationToken ct = default);
    Task UpdateAgreementMaxAmountAsync(string agreementId, int maxAmountInOre, string idempotencyKey, bool? isTest = null, CancellationToken ct = default);
    Task<VippsCreateChargeResponse> CreateChargeAsync(string agreementId, VippsCreateChargeRequest request, string idempotencyKey, bool? isTest = null, CancellationToken ct = default);

    Task<VippsCreatePaymentResponse> CreatePaymentAsync(VippsCreatePaymentRequest request, string idempotencyKey, CancellationToken ct = default);
    Task<VippsPayment> GetPaymentAsync(string reference, bool? isTest = null, CancellationToken ct = default);
    Task<VippsPayment> CapturePaymentAsync(string reference, int amountInOre, string idempotencyKey, bool? isTest = null, CancellationToken ct = default);
    Task CancelPaymentAsync(string reference, string idempotencyKey, bool? isTest = null, CancellationToken ct = default);
    Task<VippsPayment> RefundPaymentAsync(string reference, int amountInOre, string idempotencyKey, bool? isTest = null, CancellationToken ct = default);

    /// <summary>The profile a user shared, or null for an empty <paramref name="profileSub"/>.</summary>
    Task<VippsUserInfo?> GetUserInfoAsync(string profileSub, bool? isTest = null, CancellationToken ct = default);

    Task<VippsRegisteredWebhook> RegisterWebhookAsync(string url, IReadOnlyCollection<string> events, bool isTest, CancellationToken ct = default);
    Task<IReadOnlyList<VippsWebhook>> ListWebhooksAsync(bool isTest, CancellationToken ct = default);
    Task DeleteWebhookAsync(string webhookId, bool isTest, CancellationToken ct = default);
}

/// <summary>
/// Sends each call to the environment it belongs to, with a cached access token and the headers
/// Vipps asks for. A call on an existing payment or agreement takes the environment it was created
/// in, and a call that creates something new asks <see cref="IVippsEnvironment"/>.
/// </summary>
public sealed partial class VippsClient : IVippsClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly VippsOptions _options;
    private readonly IVippsEnvironment _environment;
    private readonly TimeProvider _time;
    private readonly ILogger<VippsClient> _logger;
    private readonly ConcurrentDictionary<bool, (string Token, DateTimeOffset Expires)> _tokens = new();

    private readonly record struct Target(VippsCredentials Credentials, string Token, bool IsTest);

    public VippsClient(HttpClient http, IOptions<VippsOptions> options, IVippsEnvironment environment, ILogger<VippsClient> logger, TimeProvider? time = null)
    {
        _http = http;
        _options = options.Value;
        _environment = environment;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public Task<bool> IsTestAsync(CancellationToken ct = default) => _environment.IsTestAsync(ct);

    public async Task<VippsCreateAgreementResponse> CreateAgreementAsync(VippsCreateAgreementRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        var suggestedMaxAmount = CheckedAgreementAmount(request.SuggestedMaxAmountInOre);
        var initialAmount = CheckedAgreementAmount(request.InitialChargeInOre);
        var target = await TargetAsync(null, ct);
        var body = new
        {
            pricing = new { type = "VARIABLE", suggestedMaxAmount, currency = "NOK" },
            interval = new { unit = request.Interval == VippsInterval.Month ? "MONTH" : "YEAR", count = request.IntervalCount },
            initialCharge = new { amount = initialAmount, description = request.InitialChargeDescription, transactionType = "DIRECT_CAPTURE" },
            merchantRedirectUrl = request.MerchantRedirectUrl,
            merchantAgreementUrl = request.MerchantAgreementUrl,
            productName = VippsLimits.Truncate(request.ProductName, VippsLimits.ProductNameLength),
            productDescription = VippsLimits.Truncate(request.ProductDescription ?? string.Empty, VippsLimits.ProductDescriptionLength),
            phoneNumber = request.PhoneNumber,
            scope = request.Scope,
        };
        var json = await SendAsync(target, HttpMethod.Post, "/recurring/v3/agreements", body, idempotencyKey, "create-agreement", ct);
        return Deserialize<VippsCreateAgreementResponse>(json) with { IsTest = target.IsTest };
    }

    public async Task<VippsAgreement> GetAgreementAsync(string agreementId, bool? isTest = null, CancellationToken ct = default)
    {
        var target = await TargetAsync(isTest, ct);
        var json = await SendAsync(target, HttpMethod.Get, $"/recurring/v3/agreements/{Escape(agreementId)}", null, null, "get-agreement", ct);
        return Deserialize<VippsAgreement>(json);
    }

    public async Task StopAgreementAsync(string agreementId, string idempotencyKey, bool? isTest = null, CancellationToken ct = default)
    {
        var target = await TargetAsync(isTest, ct);
        await SendAsync(target, HttpMethod.Patch, $"/recurring/v3/agreements/{Escape(agreementId)}", new { status = "STOPPED" }, idempotencyKey, "stop-agreement", ct);
    }

    public async Task UpdateAgreementMaxAmountAsync(string agreementId, int maxAmountInOre, string idempotencyKey, bool? isTest = null, CancellationToken ct = default)
    {
        var suggestedMaxAmount = CheckedAgreementAmount(maxAmountInOre);
        var target = await TargetAsync(isTest, ct);
        await SendAsync(target, HttpMethod.Patch, $"/recurring/v3/agreements/{Escape(agreementId)}",
            new { pricing = new { suggestedMaxAmount } }, idempotencyKey, "update-agreement-max-amount", ct);
    }

    public async Task<VippsCreateChargeResponse> CreateChargeAsync(string agreementId, VippsCreateChargeRequest request, string idempotencyKey, bool? isTest = null, CancellationToken ct = default)
    {
        var target = await TargetAsync(isTest, ct);
        var earliest = _time.GetUtcNow().UtcDateTime.Date.AddDays(VippsLimits.MinChargeDueDays);
        var due = request.Due.Date < earliest ? earliest : request.Due.Date;
        var body = new
        {
            amount = request.AmountInOre,
            transactionType = "DIRECT_CAPTURE",
            type = "RECURRING",
            description = request.Description,
            due = due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            retryDays = request.RetryDays,
            orderId = request.OrderId.Length <= VippsLimits.ChargeOrderIdLength ? request.OrderId : request.OrderId[..VippsLimits.ChargeOrderIdLength],
        };
        var json = await SendAsync(target, HttpMethod.Post, $"/recurring/v3/agreements/{Escape(agreementId)}/charges", body, idempotencyKey, "create-charge", ct);
        return Deserialize<VippsCreateChargeResponse>(json);
    }

    public async Task<VippsCreatePaymentResponse> CreatePaymentAsync(VippsCreatePaymentRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        var target = await TargetAsync(null, ct);
        var body = new
        {
            amount = new { value = request.AmountInOre, currency = "NOK" },
            paymentMethod = new { type = "WALLET" },
            customer = new { phoneNumber = request.PhoneNumber },
            reference = request.Reference,
            returnUrl = request.ReturnUrl,
            userFlow = "WEB_REDIRECT",
            paymentDescription = request.Description,
            captureType = "RESERVE_CAPTURE",
            receipt = new
            {
                orderLines = request.ReceiptLines.Select(l =>
                {
                    var total = l.UnitPriceInOre * l.Quantity;
                    var excludingTax = ExcludingTax(total, l.TaxRateBasisPoints);
                    return new
                    {
                        name = l.Name,
                        id = l.Id,
                        totalAmount = total,
                        totalAmountExcludingTax = excludingTax,
                        totalTaxAmount = total - excludingTax,
                        taxRate = l.TaxRateBasisPoints,
                        unitInfo = new { unitPrice = l.UnitPriceInOre, quantity = l.Quantity.ToString(CultureInfo.InvariantCulture), quantityUnit = "PCS" },
                        discount = 0,
                        isReturn = false,
                        isShipping = false,
                    };
                }),
                bottomLine = new { currency = "NOK", receiptNumber = request.Reference },
            },
            profile = new { scope = request.Scope },
        };
        var json = await SendAsync(target, HttpMethod.Post, "/epayment/v1/payments", body, idempotencyKey, "create-payment", ct);
        return Deserialize<VippsCreatePaymentResponse>(json) with { IsTest = target.IsTest };
    }

    public async Task<VippsPayment> GetPaymentAsync(string reference, bool? isTest = null, CancellationToken ct = default)
    {
        var target = await TargetAsync(isTest, ct);
        return ToPayment(await SendAsync(target, HttpMethod.Get, $"/epayment/v1/payments/{Escape(reference)}", null, null, "get-payment", ct));
    }

    public async Task<VippsPayment> CapturePaymentAsync(string reference, int amountInOre, string idempotencyKey, bool? isTest = null, CancellationToken ct = default)
    {
        var target = await TargetAsync(isTest, ct);
        var body = new { modificationAmount = new { value = amountInOre, currency = "NOK" } };
        return ToPayment(await SendAsync(target, HttpMethod.Post, $"/epayment/v1/payments/{Escape(reference)}/capture", body, idempotencyKey, "capture-payment", ct));
    }

    public async Task CancelPaymentAsync(string reference, string idempotencyKey, bool? isTest = null, CancellationToken ct = default)
    {
        var target = await TargetAsync(isTest, ct);
        await SendAsync(target, HttpMethod.Post, $"/epayment/v1/payments/{Escape(reference)}/cancel", new { }, idempotencyKey, "cancel-payment", ct);
    }

    public async Task<VippsPayment> RefundPaymentAsync(string reference, int amountInOre, string idempotencyKey, bool? isTest = null, CancellationToken ct = default)
    {
        var target = await TargetAsync(isTest, ct);
        var body = new { modificationAmount = new { value = amountInOre, currency = "NOK" } };
        return ToPayment(await SendAsync(target, HttpMethod.Post, $"/epayment/v1/payments/{Escape(reference)}/refund", body, idempotencyKey, "refund-payment", ct));
    }

    public async Task<VippsUserInfo?> GetUserInfoAsync(string profileSub, bool? isTest = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(profileSub)) return null;
        var target = await TargetAsync(isTest, ct);
        var json = await SendAsync(target, HttpMethod.Get, $"/vipps-userinfo-api/userinfo/{Escape(profileSub)}", null, null, "get-userinfo", ct);
        var dto = JsonSerializer.Deserialize<UserInfoDto>(json, JsonOptions);
        if (dto == null) return null;
        return new VippsUserInfo
        {
            Name = dto.Name,
            Email = dto.Email,
            PhoneNumber = dto.PhoneNumber,
            Address = dto.Address == null ? null : new VippsAddress
            {
                StreetAddress = dto.Address.StreetAddress,
                PostalCode = dto.Address.PostalCode,
                Region = dto.Address.Region,
                Country = dto.Address.Country,
                Formatted = dto.Address.Formatted,
            },
        };
    }

    public async Task<VippsRegisteredWebhook> RegisterWebhookAsync(string url, IReadOnlyCollection<string> events, bool isTest, CancellationToken ct = default)
    {
        var target = await TargetAsync(isTest, ct);
        var json = await SendAsync(target, HttpMethod.Post, "/webhooks/v1/webhooks", new { url, events }, Guid.NewGuid().ToString(), "register-webhook", ct);
        using var doc = JsonDocument.Parse(json);
        return new VippsRegisteredWebhook(doc.RootElement.GetProperty("id").GetString()!, doc.RootElement.GetProperty("secret").GetString()!);
    }

    public async Task<IReadOnlyList<VippsWebhook>> ListWebhooksAsync(bool isTest, CancellationToken ct = default)
    {
        var target = await TargetAsync(isTest, ct);
        var json = await SendAsync(target, HttpMethod.Get, "/webhooks/v1/webhooks", null, null, "list-webhooks", ct);
        using var doc = JsonDocument.Parse(json);
        var list = new List<VippsWebhook>();
        if (doc.RootElement.TryGetProperty("webhooks", out var hooks) && hooks.ValueKind == JsonValueKind.Array)
        {
            foreach (var hook in hooks.EnumerateArray())
            {
                list.Add(new VippsWebhook
                {
                    Id = hook.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                    Url = hook.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty,
                });
            }
        }
        return list;
    }

    public async Task DeleteWebhookAsync(string webhookId, bool isTest, CancellationToken ct = default)
    {
        var target = await TargetAsync(isTest, ct);
        await SendAsync(target, HttpMethod.Delete, $"/webhooks/v1/webhooks/{Escape(webhookId)}", null, null, "delete-webhook", ct);
    }

    /// <summary>The VAT-free part of a VAT-included total, rounded half away from zero, as an invoice splits it.</summary>
    internal static int ExcludingTax(int totalInOre, int taxRateBasisPoints) =>
        taxRateBasisPoints == 0
            ? totalInOre
            : (int)Math.Round(totalInOre * 10_000m / (10_000 + taxRateBasisPoints), MidpointRounding.AwayFromZero);

    private static int CheckedAgreementAmount(long amountInOre) =>
        amountInOre is > 0 and <= VippsLimits.MaxAgreementAmountInOre
            ? (int)amountInOre
            : throw new ArgumentOutOfRangeException(nameof(amountInOre), amountInOre,
                $"A Vipps agreement amount must be between 1 and {VippsLimits.MaxAgreementAmountInOre} øre.");

    private static string Escape(string segment) => Uri.EscapeDataString(segment);

    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions)!;

    private static VippsPayment ToPayment(string json)
    {
        var dto = JsonSerializer.Deserialize<PaymentDto>(json, JsonOptions) ?? new PaymentDto();
        return new VippsPayment
        {
            Reference = dto.Reference ?? string.Empty,
            State = dto.State ?? string.Empty,
            ProfileSub = dto.Profile?.Sub,
            CapturedAmountInOre = dto.Aggregate?.CapturedAmount?.Value ?? 0,
            RefundedAmountInOre = dto.Aggregate?.RefundedAmount?.Value ?? 0,
            CancelledAmountInOre = dto.Aggregate?.CancelledAmount?.Value ?? 0,
        };
    }

    private async Task<Target> TargetAsync(bool? isTest, CancellationToken ct)
    {
        var test = isTest ?? await _environment.IsTestAsync(ct);
        var credentials = _options.For(test);
        return new Target(credentials, await TokenAsync(test, credentials, ct), test);
    }

    private async Task<string> TokenAsync(bool isTest, VippsCredentials credentials, CancellationToken ct)
    {
        if (_tokens.TryGetValue(isTest, out var cached) && cached.Expires > _time.GetUtcNow()) return cached.Token;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{credentials.BaseUrl}/accesstoken/get");
        request.Headers.Add("client_id", credentials.ClientId);
        request.Headers.Add("client_secret", credentials.ClientSecret);
        request.Headers.Add("Ocp-Apim-Subscription-Key", credentials.SubscriptionKey);
        request.Content = new StringContent(string.Empty, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        var json = await ReadAsync(response, "accesstoken/get", ct);
        using var doc = JsonDocument.Parse(json);
        var token = doc.RootElement.GetProperty("access_token").GetString()!;
        var expiresIn = 3600;
        if (doc.RootElement.TryGetProperty("expires_in", out var exp))
        {
            var text = exp.ValueKind == JsonValueKind.String ? exp.GetString() : exp.GetRawText();
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out expiresIn)) expiresIn = 3600;
        }

        // Renewed a minute early, so a token never runs out during a call.
        _tokens[isTest] = (token, _time.GetUtcNow().AddSeconds(Math.Max(60, expiresIn - 60)));
        LogTokenRefreshed(_logger, isTest ? "test" : "live", expiresIn);
        return token;
    }

    private async Task<string> SendAsync(Target target, HttpMethod method, string path, object? body, string? idempotencyKey, string operation, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, target.Credentials.BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.Token);
        request.Headers.Add("Ocp-Apim-Subscription-Key", target.Credentials.SubscriptionKey);
        request.Headers.Add("Merchant-Serial-Number", target.Credentials.MerchantSerialNumber);
        request.Headers.Add("Vipps-System-Name", VippsLimits.SystemHeader(_options.SystemName));
        request.Headers.Add("Vipps-System-Version", VippsLimits.SystemHeader(_options.SystemVersion));
        if (!string.IsNullOrEmpty(idempotencyKey)) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        return await ReadAsync(response, operation, ct);
    }

    private async Task<string> ReadAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode) return body;

        // A failed body may echo a phone number, a name or an agreement id. Vipps's own error ids are
        // logged in its place, and the body only when it carries none.
        var (errorCode, errorRef, traceId) = ErrorIds(body);
        if (errorCode == null && errorRef == null && traceId == null)
            LogFailedWithBody(_logger, operation, (int)response.StatusCode, body);
        else
            LogFailed(_logger, operation, (int)response.StatusCode, errorCode ?? "-", errorRef ?? "-", traceId ?? "-");
        response.EnsureSuccessStatusCode();
        return body;
    }

    internal static (string? ErrorCode, string? ErrorRef, string? TraceId) ErrorIds(string body)
    {
        if (string.IsNullOrEmpty(body)) return (null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, null, null);
            string? Text(string name) => doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return (Text("errorCode"), Text("errorRef"), Text("traceId"));
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Vipps access token refreshed ({Environment}), expires in {ExpiresIn}s")]
    private static partial void LogTokenRefreshed(ILogger logger, string environment, int expiresIn);

    [LoggerMessage(Level = LogLevel.Error, Message = "Vipps {Operation} failed: {Status} body={Body}")]
    private static partial void LogFailedWithBody(ILogger logger, string operation, int status, string body);

    [LoggerMessage(Level = LogLevel.Error, Message = "Vipps {Operation} failed: {Status} ErrorCode={ErrorCode} ErrorRef={ErrorRef} TraceId={TraceId}")]
    private static partial void LogFailed(ILogger logger, string operation, int status, string errorCode, string errorRef, string traceId);

    private sealed class PaymentDto
    {
        public string? Reference { get; set; }
        public string? State { get; set; }
        public ProfileDto? Profile { get; set; }
        public AggregateDto? Aggregate { get; set; }
    }

    private sealed class AggregateDto
    {
        public AmountDto? CapturedAmount { get; set; }
        public AmountDto? RefundedAmount { get; set; }
        public AmountDto? CancelledAmount { get; set; }
    }

    private sealed class AmountDto
    {
        public int Value { get; set; }
    }

    private sealed class ProfileDto
    {
        public string? Sub { get; set; }
    }

    private sealed class UserInfoDto
    {
        public string? Name { get; set; }
        public string? Email { get; set; }
        [JsonPropertyName("phone_number")]
        public string? PhoneNumber { get; set; }
        public AddressDto? Address { get; set; }
    }

    private sealed class AddressDto
    {
        [JsonPropertyName("street_address")]
        public string? StreetAddress { get; set; }
        [JsonPropertyName("postal_code")]
        public string? PostalCode { get; set; }
        public string? Region { get; set; }
        public string? Country { get; set; }
        public string? Formatted { get; set; }
    }
}
