using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SjolystInnovation.AppKit.Vipps;

/// <summary>A client for the Vipps Recurring, ePayment, Userinfo, and Webhooks APIs.</summary>
public interface IVippsClient
{
    /// <summary>Checks whether calls that create something new currently go to the test environment.</summary>
    Task<bool> IsTestAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a recurring agreement in the current environment.</summary>
    Task<VippsCreateAgreementResponse> CreateAgreementAsync(VippsCreateAgreementRequest request, string idempotencyKey, CancellationToken cancellationToken = default);
    /// <summary>Gets a recurring agreement.</summary>
    Task<VippsAgreement> GetAgreementAsync(string agreementId, bool? isTest = null, CancellationToken cancellationToken = default);
    /// <summary>Stops a recurring agreement.</summary>
    Task StopAgreementAsync(string agreementId, string idempotencyKey, bool? isTest = null, CancellationToken cancellationToken = default);
    /// <summary>Changes the ceiling the user approved for a recurring agreement.</summary>
    Task UpdateAgreementMaxAmountAsync(string agreementId, int maxAmountInOre, string idempotencyKey, bool? isTest = null, CancellationToken cancellationToken = default);
    /// <summary>Creates a charge on a recurring agreement.</summary>
    Task<VippsCreateChargeResponse> CreateChargeAsync(string agreementId, VippsCreateChargeRequest request, string idempotencyKey, bool? isTest = null, CancellationToken cancellationToken = default);
    /// <summary>
    /// Refunds part or all of a captured recurring charge. Vipps returns no body and sends no webhook,
    /// so the caller records the refund when this call returns.
    /// </summary>
    Task RefundChargeAsync(string agreementId, string chargeId, int amountInOre, string description, string idempotencyKey, bool? isTest = null, CancellationToken cancellationToken = default);

    /// <summary>Creates an ePayment in the current environment.</summary>
    Task<VippsCreatePaymentResponse> CreatePaymentAsync(VippsCreatePaymentRequest request, string idempotencyKey, CancellationToken cancellationToken = default);
    /// <summary>Gets an ePayment.</summary>
    Task<VippsPayment> GetPaymentAsync(string reference, bool? isTest = null, CancellationToken cancellationToken = default);
    /// <summary>Captures part or all of a reserved ePayment.</summary>
    Task<VippsPayment> CapturePaymentAsync(string reference, int amountInOre, string idempotencyKey, bool? isTest = null, CancellationToken cancellationToken = default);
    /// <summary>Cancels an ePayment.</summary>
    Task CancelPaymentAsync(string reference, string idempotencyKey, bool? isTest = null, CancellationToken cancellationToken = default);
    /// <summary>Refunds part or all of a captured ePayment.</summary>
    Task<VippsPayment> RefundPaymentAsync(string reference, int amountInOre, string idempotencyKey, bool? isTest = null, CancellationToken cancellationToken = default);

    /// <summary>Gets the profile a user shared. Returns null for an empty <paramref name="profileSub"/>.</summary>
    Task<VippsUserInfo?> GetUserInfoAsync(string profileSub, bool? isTest = null, CancellationToken cancellationToken = default);

    /// <summary>Registers a webhook for the events and returns its id and signing secret.</summary>
    Task<VippsRegisteredWebhook> RegisterWebhookAsync(string url, IReadOnlyCollection<string> events, bool isTest, CancellationToken cancellationToken = default);
    /// <summary>Lists the registered webhooks.</summary>
    Task<IReadOnlyList<VippsWebhook>> ListWebhooksAsync(bool isTest, CancellationToken cancellationToken = default);
    /// <summary>Deletes a webhook.</summary>
    Task DeleteWebhookAsync(string webhookId, bool isTest, CancellationToken cancellationToken = default);
}

/// <summary>
/// Sends each call to the environment it belongs to, with a cached access token and the headers
/// Vipps asks for. A call on an existing payment or agreement uses the environment it was created
/// in. A call that creates something new asks <see cref="IVippsEnvironment"/>.
/// </summary>
internal sealed partial class VippsClient : IVippsClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly VippsOptions _options;
    private readonly IVippsEnvironment _environment;
    private readonly TimeProvider _time;
    private readonly ILogger<VippsClient> _logger;
    private readonly ConcurrentDictionary<bool, (string Token, DateTimeOffset Expires)> _tokens = new();

    private readonly record struct Target(VippsCredentials Credentials, string Token, bool IsTest);

    /// <summary>Creates the client. The DI container calls this.</summary>
    public VippsClient(HttpClient http, IOptions<VippsOptions> options, IVippsEnvironment environment, ILogger<VippsClient> logger, TimeProvider? time = null)
    {
        _http = http;
        _options = options.Value;
        _environment = environment;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public Task<bool> IsTestAsync(CancellationToken cancellationToken = default) => _environment.IsTestAsync(cancellationToken);

    public async Task<VippsCreateAgreementResponse> CreateAgreementAsync(VippsCreateAgreementRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var suggestedMaxAmount = CheckedAgreementAmount(request.SuggestedMaxAmountInOre);
        var initialAmount = CheckedAgreementAmount(request.InitialChargeInOre);
        var target = await TargetAsync(null, cancellationToken);
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
        var json = await SendAsync(target, HttpMethod.Post, "/recurring/v3/agreements", body, idempotencyKey, "create-agreement", cancellationToken);
        return Deserialize<VippsCreateAgreementResponse>(json) with { IsTest = target.IsTest };
    }

    public async Task<VippsAgreement> GetAgreementAsync(string agreementId, bool? isTest = null, CancellationToken cancellationToken = default)
    {
        var target = await TargetAsync(isTest, cancellationToken);
        var json = await SendAsync(target, HttpMethod.Get, $"/recurring/v3/agreements/{Escape(agreementId)}", null, null, "get-agreement", cancellationToken);
        return Deserialize<VippsAgreement>(json);
    }

    public async Task StopAgreementAsync(string agreementId, string idempotencyKey, bool? isTest = null, CancellationToken cancellationToken = default)
    {
        var target = await TargetAsync(isTest, cancellationToken);
        await SendAsync(target, HttpMethod.Patch, $"/recurring/v3/agreements/{Escape(agreementId)}", new { status = "STOPPED" }, idempotencyKey, "stop-agreement", cancellationToken);
    }

    public async Task UpdateAgreementMaxAmountAsync(string agreementId, int maxAmountInOre, string idempotencyKey, bool? isTest = null, CancellationToken cancellationToken = default)
    {
        var suggestedMaxAmount = CheckedAgreementAmount(maxAmountInOre);
        var target = await TargetAsync(isTest, cancellationToken);
        await SendAsync(target, HttpMethod.Patch, $"/recurring/v3/agreements/{Escape(agreementId)}",
            new { pricing = new { suggestedMaxAmount } }, idempotencyKey, "update-agreement-max-amount", cancellationToken);
    }

    public async Task<VippsCreateChargeResponse> CreateChargeAsync(string agreementId, VippsCreateChargeRequest request, string idempotencyKey, bool? isTest = null, CancellationToken cancellationToken = default)
    {
        var target = await TargetAsync(isTest, cancellationToken);
        var earliest = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime).AddDays(VippsLimits.MinChargeDueDays);
        var due = request.Due < earliest ? earliest : request.Due;
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
        var json = await SendAsync(target, HttpMethod.Post, $"/recurring/v3/agreements/{Escape(agreementId)}/charges", body, idempotencyKey, "create-charge", cancellationToken);
        return Deserialize<VippsCreateChargeResponse>(json);
    }

    public async Task RefundChargeAsync(string agreementId, string chargeId, int amountInOre, string description, string idempotencyKey, bool? isTest = null, CancellationToken cancellationToken = default)
    {
        var target = await TargetAsync(isTest, cancellationToken);
        var body = new { amount = amountInOre, description = VippsLimits.Truncate(description, VippsLimits.RefundDescriptionLength) };
        await SendAsync(target, HttpMethod.Post,
            $"/recurring/v3/agreements/{Escape(agreementId)}/charges/{Escape(chargeId)}/refund", body, idempotencyKey, "refund-charge", cancellationToken);
    }

    public async Task<VippsCreatePaymentResponse> CreatePaymentAsync(VippsCreatePaymentRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var target = await TargetAsync(null, cancellationToken);
        var body = new Dictionary<string, object?>
        {
            ["amount"] = new { value = request.AmountInOre, currency = "NOK" },
            ["paymentMethod"] = new { type = "WALLET" },
            ["customer"] = new { phoneNumber = request.PhoneNumber },
            ["reference"] = request.Reference,
            ["returnUrl"] = request.ReturnUrl,
            ["userFlow"] = "WEB_REDIRECT",
            ["paymentDescription"] = request.Description,
            ["captureType"] = "RESERVE_CAPTURE",
            ["receipt"] = new
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
        };
        if (!string.IsNullOrWhiteSpace(request.Scope))
            body["profile"] = new { scope = request.Scope };
        var json = await SendAsync(target, HttpMethod.Post, "/epayment/v1/payments", body, idempotencyKey, "create-payment", cancellationToken);
        return Deserialize<VippsCreatePaymentResponse>(json) with { IsTest = target.IsTest };
    }

    public async Task<VippsPayment> GetPaymentAsync(string reference, bool? isTest = null, CancellationToken cancellationToken = default)
    {
        var target = await TargetAsync(isTest, cancellationToken);
        return ToPayment(await SendAsync(target, HttpMethod.Get, $"/epayment/v1/payments/{Escape(reference)}", null, null, "get-payment", cancellationToken));
    }

    public async Task<VippsPayment> CapturePaymentAsync(string reference, int amountInOre, string idempotencyKey, bool? isTest = null, CancellationToken cancellationToken = default)
    {
        var target = await TargetAsync(isTest, cancellationToken);
        var body = new { modificationAmount = new { value = amountInOre, currency = "NOK" } };
        return ToPayment(await SendAsync(target, HttpMethod.Post, $"/epayment/v1/payments/{Escape(reference)}/capture", body, idempotencyKey, "capture-payment", cancellationToken));
    }

    public async Task CancelPaymentAsync(string reference, string idempotencyKey, bool? isTest = null, CancellationToken cancellationToken = default)
    {
        var target = await TargetAsync(isTest, cancellationToken);
        await SendAsync(target, HttpMethod.Post, $"/epayment/v1/payments/{Escape(reference)}/cancel", new { }, idempotencyKey, "cancel-payment", cancellationToken);
    }

    public async Task<VippsPayment> RefundPaymentAsync(string reference, int amountInOre, string idempotencyKey, bool? isTest = null, CancellationToken cancellationToken = default)
    {
        var target = await TargetAsync(isTest, cancellationToken);
        var body = new { modificationAmount = new { value = amountInOre, currency = "NOK" } };
        return ToPayment(await SendAsync(target, HttpMethod.Post, $"/epayment/v1/payments/{Escape(reference)}/refund", body, idempotencyKey, "refund-payment", cancellationToken));
    }

    public async Task<VippsUserInfo?> GetUserInfoAsync(string profileSub, bool? isTest = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(profileSub)) return null;
        var target = await TargetAsync(isTest, cancellationToken);
        var json = await SendAsync(target, HttpMethod.Get, $"/vipps-userinfo-api/userinfo/{Escape(profileSub)}", null, null, "get-userinfo", cancellationToken);
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

    public async Task<VippsRegisteredWebhook> RegisterWebhookAsync(string url, IReadOnlyCollection<string> events, bool isTest, CancellationToken cancellationToken = default)
    {
        var target = await TargetAsync(isTest, cancellationToken);
        var json = await SendAsync(target, HttpMethod.Post, "/webhooks/v1/webhooks", new { url, events }, Guid.NewGuid().ToString(), "register-webhook", cancellationToken);
        using var doc = JsonDocument.Parse(json);
        return new VippsRegisteredWebhook(doc.RootElement.GetProperty("id").GetString()!, doc.RootElement.GetProperty("secret").GetString()!);
    }

    public async Task<IReadOnlyList<VippsWebhook>> ListWebhooksAsync(bool isTest, CancellationToken cancellationToken = default)
    {
        var target = await TargetAsync(isTest, cancellationToken);
        var json = await SendAsync(target, HttpMethod.Get, "/webhooks/v1/webhooks", null, null, "list-webhooks", cancellationToken);
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

    public async Task DeleteWebhookAsync(string webhookId, bool isTest, CancellationToken cancellationToken = default)
    {
        var target = await TargetAsync(isTest, cancellationToken);
        await SendAsync(target, HttpMethod.Delete, $"/webhooks/v1/webhooks/{Escape(webhookId)}", null, null, "delete-webhook", cancellationToken);
    }

    /// <summary>Computes the part of a total that excludes VAT, rounded half away from zero, as an invoice splits it.</summary>
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
            AuthorizedAmountInOre = dto.Aggregate?.AuthorizedAmount?.Value ?? 0,
            CapturedAmountInOre = dto.Aggregate?.CapturedAmount?.Value ?? 0,
            RefundedAmountInOre = dto.Aggregate?.RefundedAmount?.Value ?? 0,
            CancelledAmountInOre = dto.Aggregate?.CancelledAmount?.Value ?? 0,
            CaptureGuaranteedUntil = dto.CaptureGuaranteedUntil,
        };
    }

    private async Task<Target> TargetAsync(bool? isTest, CancellationToken cancellationToken)
    {
        var test = isTest ?? await _environment.IsTestAsync(cancellationToken);
        var credentials = _options.For(test);
        return new Target(credentials, await TokenAsync(test, credentials, cancellationToken), test);
    }

    private async Task<string> TokenAsync(bool isTest, VippsCredentials credentials, CancellationToken cancellationToken)
    {
        if (_tokens.TryGetValue(isTest, out var cached) && cached.Expires > _time.GetUtcNow()) return cached.Token;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{credentials.BaseUrl}/accesstoken/get");
        request.Headers.Add("client_id", credentials.ClientId);
        request.Headers.Add("client_secret", credentials.ClientSecret);
        request.Headers.Add("Ocp-Apim-Subscription-Key", credentials.SubscriptionKey);
        request.Content = new StringContent(string.Empty, Encoding.UTF8, "application/json");

        // A failure here is told apart from a failure of the call the token was for.
        string json;
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            json = await ReadAsync(response, "accesstoken/get", cancellationToken);
        }
        catch (HttpRequestException e)
        {
            throw new VippsAccessTokenException("Vipps did not issue an access token.", e, e.StatusCode);
        }
        using var doc = JsonDocument.Parse(json);
        var token = doc.RootElement.GetProperty("access_token").GetString()!;
        var expiresIn = 3600;
        if (doc.RootElement.TryGetProperty("expires_in", out var exp))
        {
            var text = exp.ValueKind == JsonValueKind.String ? exp.GetString() : exp.GetRawText();
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out expiresIn)) expiresIn = 3600;
        }

        // Renew a minute early, so a token never runs out during a call.
        _tokens[isTest] = (token, _time.GetUtcNow().AddSeconds(Math.Max(60, expiresIn - 60)));
        LogTokenRefreshed(_logger, isTest ? "test" : "live", expiresIn);
        return token;
    }

    private async Task<string> SendAsync(Target target, HttpMethod method, string path, object? body, string? idempotencyKey, string operation, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, target.Credentials.BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.Token);
        request.Headers.Add("Ocp-Apim-Subscription-Key", target.Credentials.SubscriptionKey);
        request.Headers.Add("Merchant-Serial-Number", target.Credentials.MerchantSerialNumber);
        request.Headers.Add("Vipps-System-Name", VippsLimits.SystemHeader(_options.SystemName));
        request.Headers.Add("Vipps-System-Version", VippsLimits.SystemHeader(_options.SystemVersion));
        if (!string.IsNullOrEmpty(idempotencyKey)) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, cancellationToken);
        return await ReadAsync(response, operation, cancellationToken);
    }

    private async Task<string> ReadAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode) return body;

        // A failed body may echo a phone number, a name, or an agreement id, so log Vipps's own error ids
        // instead. Log the body only when the app turns that on and the response has no error ids.
        var ids = ErrorIds(body);
        // Whether the body carries error ids is read from the body itself, so an id left out of the log as unsafe
        // never turns on the body logging.
        if (!HasErrorIds(body) && _options.LogFailedResponseBodies)
            LogFailedWithBody(_logger, operation, (int)response.StatusCode, body);
        else
            LogFailed(_logger, operation, (int)response.StatusCode, ids.ErrorCode ?? "-", ids.ErrorRef ?? "-", ids.TraceId ?? "-",
                ids.Title ?? "-", ids.Fields ?? "-", body.Length);
        response.EnsureSuccessStatusCode();
        return body;
    }

    // Reads the parts of an error body that carry no personal data. An RFC 7807 problem, which the Recurring
    // and ePayment APIs answer with, names its problem in the type URL and gives a title. Its detail,
    // instance, and extra detail reasons can echo the request, so only the names of the fields it blames
    // are kept. Every value is checked, and one that could carry request data, such as a run of digits, is
    // left out.
    internal static (string? ErrorCode, string? ErrorRef, string? TraceId, string? Title, string? Fields) ErrorIds(string body)
    {
        if (string.IsNullOrEmpty(body)) return default;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return default;
            string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var fields = root.TryGetProperty("extraDetails", out var extra) && extra.ValueKind == JsonValueKind.Array
                ? extra.EnumerateArray()
                    .Where(d => d.ValueKind == JsonValueKind.Object)
                    .Select(d => ProblemName(Text(d, "name")))
                    .OfType<string>()
                    .Distinct()
                    .Take(MaxProblemFields)
                    .ToList()
                : [];
            return (PlainId(Text(root, "errorCode")) ?? ProblemType(Text(root, "type")),
                PlainId(Text(root, "errorRef")),
                PlainId(Text(root, "traceId")) ?? PlainId(Text(root, "contextId")),
                ProblemTitle(Text(root, "title")),
                fields.Count == 0 ? null : string.Join(',', fields));
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private const int MaxProblemTitleLength = 100;
    private const int MaxProblemFields = 10;

    // The problem id at the end of a type URL, such as charge-due-too-soon in ...recurring-api-problems#charge-due-too-soon.
    private static string? ProblemType(string? type)
    {
        if (string.IsNullOrEmpty(type)) return null;
        var hash = type.LastIndexOf('#');
        var id = hash >= 0 ? type[(hash + 1)..] : type[(type.TrimEnd('/').LastIndexOf('/') + 1)..].TrimEnd('/');
        return ProblemName(id);
    }

    // A name made of letters, digits, dots, dashes, and underscores, with fewer than seven digits in all, so it
    // cannot carry a phone number, an account number, or a name with spaces.
    private static string? ProblemName(string? value) =>
        value is { Length: > 0 and <= 64 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') && value.Count(char.IsAsciiDigit) < 7
            ? value
            : null;

    // A GUID, or a plain name as above.
    private static string? PlainId(string? value) =>
        value is { Length: 36 } && Guid.TryParseExact(value, "D", out _) ? value : ProblemName(value);

    // A short sentence of letters, spaces, and plain punctuation, without digits, such as "Charge not creatable".
    private static string? ProblemTitle(string? value) =>
        value is { Length: > 0 and <= MaxProblemTitleLength }
        && value.Any(char.IsAsciiLetter)
        && value.All(c => char.IsAsciiLetter(c) || c is ' ' or '.' or ',' or '\'' or '-' or '(' or ')' or ':')
            ? value
            : null;

    private static readonly string[] ErrorIdNames =
        ["errorCode", "errorRef", "traceId", "type", "title", "detail", "instance", "contextId", "extraDetails"];

    // Whether the body has error ids or is an RFC 7807 problem, whatever the values and their JSON types. Such a
    // body is never logged whole, since a problem's detail and reasons can echo the request.
    internal static bool HasErrorIds(string body)
    {
        if (string.IsNullOrEmpty(body)) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && ErrorIdNames.Any(n => doc.RootElement.TryGetProperty(n, out _));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Vipps access token refreshed ({Environment}), expires in {ExpiresIn}s")]
    private static partial void LogTokenRefreshed(ILogger logger, string environment, int expiresIn);

    [LoggerMessage(Level = LogLevel.Error, Message = "Vipps {Operation} failed: {Status} body={Body}")]
    private static partial void LogFailedWithBody(ILogger logger, string operation, int status, string body);

    [LoggerMessage(Level = LogLevel.Error, Message = "Vipps {Operation} failed: {Status} ErrorCode={ErrorCode} ErrorRef={ErrorRef} TraceId={TraceId} Title={Title} Fields={Fields} BodyLength={BodyLength}")]
    private static partial void LogFailed(ILogger logger, string operation, int status, string errorCode, string errorRef, string traceId, string title, string fields, int bodyLength);

    private sealed class PaymentDto
    {
        public string? Reference { get; set; }
        public string? State { get; set; }
        public ProfileDto? Profile { get; set; }
        public AggregateDto? Aggregate { get; set; }
        [JsonConverter(typeof(VippsUtcDateTimeOffsetConverter))]
        public DateTimeOffset? CaptureGuaranteedUntil { get; set; }
    }

    private sealed class AggregateDto
    {
        public AmountDto? AuthorizedAmount { get; set; }
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
