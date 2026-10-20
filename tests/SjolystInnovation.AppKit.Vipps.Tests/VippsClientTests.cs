using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace SjolystInnovation.AppKit.Vipps.Tests;

public class VippsClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Answers like Vipps and keeps every request it got.</summary>
    private sealed class FakeVipps : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Sent { get; } = [];
        public Func<HttpRequestMessage, (HttpStatusCode, string)>? Respond { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Sent.Add((request, body));
            var (status, json) = Respond?.Invoke(request) ?? (HttpStatusCode.OK, Default(request));
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        private static string Default(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
        {
            "/accesstoken/get" => $$"""{"access_token":"token-{{request.RequestUri.Host}}","expires_in":"3600"}""",
            "/recurring/v3/agreements" => """{"agreementId":"agr_1","vippsConfirmationUrl":"https://vipps.no/x"}""",
            "/epayment/v1/payments" => """{"redirectUrl":"https://landing.vipps.no/x","reference":"order-42"}""",
            var p when p.EndsWith("/charges", StringComparison.Ordinal) => """{"chargeId":"chr_1"}""",
            _ => "{}",
        };

        public IEnumerable<(HttpRequestMessage Request, string Body)> Api => Sent.Where(s => s.Request.RequestUri!.AbsolutePath != "/accesstoken/get");
        public JsonElement Body(string path) => JsonDocument.Parse(Sent.Single(s => s.Request.RequestUri!.AbsolutePath == path).Body).RootElement;
    }

    private sealed class ListLogger : ILogger<VippsClient>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add($"{logLevel}: {formatter(state, exception)}");
    }

    private sealed class SwitchEnvironment : IVippsEnvironment
    {
        public bool IsTest { get; set; }
        public Task<bool> IsTestAsync(CancellationToken cancellationToken = default) => Task.FromResult(IsTest);
    }

    private static (VippsClient Client, FakeVipps Vipps, SwitchEnvironment Env, FakeTimeProvider Time, ListLogger Log) Create()
    {
        var vipps = new FakeVipps();
        var options = Options.Create(new VippsOptions
        {
            Live = new VippsCredentials { BaseUrl = "https://api.vipps.no", ClientId = "id", ClientSecret = "secret", SubscriptionKey = "key", MerchantSerialNumber = "111111" },
            Test = new VippsCredentials { BaseUrl = "https://apitest.vipps.no", ClientId = "tid", ClientSecret = "tsecret", SubscriptionKey = "tkey", MerchantSerialNumber = "222222" },
            SystemName = "garge-api",
            SystemVersion = "1.4.2+0123456789abcdef",
        });
        var env = new SwitchEnvironment();
        var time = new FakeTimeProvider(Now);
        var log = new ListLogger();
        return (new VippsClient(new HttpClient(vipps), options, env, log, time), vipps, env, time, log);
    }

    private static VippsCreateAgreementRequest Agreement(string name = "Garge", int max = 29900) => new()
    {
        ProductName = name,
        ProductDescription = "Sensor subscription",
        SuggestedMaxAmountInOre = max,
        InitialChargeInOre = max,
        InitialChargeDescription = name,
        Interval = VippsInterval.Month,
        MerchantRedirectUrl = "https://www.garge.no/r",
        MerchantAgreementUrl = "https://www.garge.no/terms",
        PhoneNumber = "4791234567",
    };

    private static VippsCreatePaymentRequest Payment(params VippsReceiptLine[] lines) => new()
    {
        Reference = "order-42",
        AmountInOre = lines.Sum(l => l.UnitPriceInOre * l.Quantity),
        PhoneNumber = "4712345678",
        ReturnUrl = "https://www.garge.no/shop/return?orderId=42",
        Description = "Garge order #42",
        ReceiptLines = lines,
    };

    [Fact]
    public async Task CreateAgreement_SendsVariablePricingWithTheCeilingAndTheFirstCharge()
    {
        var (client, vipps, _, _, _) = Create();
        var created = await client.CreateAgreementAsync(Agreement() with { Interval = VippsInterval.Year }, "key-1");

        var body = vipps.Body("/recurring/v3/agreements");
        Assert.Equal("VARIABLE", body.GetProperty("pricing").GetProperty("type").GetString());
        Assert.Equal(29900, body.GetProperty("pricing").GetProperty("suggestedMaxAmount").GetInt32());
        Assert.Equal("NOK", body.GetProperty("pricing").GetProperty("currency").GetString());
        Assert.Equal("YEAR", body.GetProperty("interval").GetProperty("unit").GetString());
        Assert.Equal(1, body.GetProperty("interval").GetProperty("count").GetInt32());
        Assert.Equal(29900, body.GetProperty("initialCharge").GetProperty("amount").GetInt32());
        Assert.Equal("DIRECT_CAPTURE", body.GetProperty("initialCharge").GetProperty("transactionType").GetString());
        Assert.Equal("https://www.garge.no/terms", body.GetProperty("merchantAgreementUrl").GetString());
        Assert.Equal("4791234567", body.GetProperty("phoneNumber").GetString());
        Assert.Equal("name address email phoneNumber", body.GetProperty("scope").GetString());
        Assert.Equal(new VippsCreateAgreementResponse { AgreementId = "agr_1", VippsConfirmationUrl = "https://vipps.no/x", IsTest = false }, created);
    }

    [Fact]
    public async Task CreateAgreement_InTestMode_GoesToTheTestEnvironment_AndSaysSo()
    {
        var (client, vipps, env, _, _) = Create();
        env.IsTest = true;
        var created = await client.CreateAgreementAsync(Agreement(), "key-1");
        Assert.True(created.IsTest);
        Assert.Equal("apitest.vipps.no", vipps.Api.Single().Request.RequestUri!.Host);
    }

    [Fact]
    public async Task CreateAgreement_CutsTheNameAndDescriptionToTheVippsLimits()
    {
        var (client, vipps, _, _, _) = Create();
        await client.CreateAgreementAsync(Agreement(new string('x', 60)) with { ProductDescription = new string('d', 150) }, "key-1");

        var body = vipps.Body("/recurring/v3/agreements");
        Assert.Equal(new string('x', 45), body.GetProperty("productName").GetString());
        Assert.Equal(new string('d', 100), body.GetProperty("productDescription").GetString());
    }

    [Fact]
    public async Task CreateAgreement_WithoutADescription_SendsAnEmptyOne()
    {
        var (client, vipps, _, _, _) = Create();
        await client.CreateAgreementAsync(Agreement() with { ProductDescription = null }, "key-1");
        Assert.Equal("", vipps.Body("/recurring/v3/agreements").GetProperty("productDescription").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(VippsLimits.MaxAgreementAmountInOre + 1)]
    public async Task CreateAgreement_AmountOutsideTheVippsLimit_SendsNothing(int amount)
    {
        var (client, vipps, _, _, _) = Create();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.CreateAgreementAsync(Agreement(max: amount), "key-1"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.CreateAgreementAsync(Agreement() with { InitialChargeInOre = amount }, "key-1"));
        Assert.Empty(vipps.Sent);
    }

    [Fact]
    public async Task CreateAgreement_AtTheVippsLimit_IsSent()
    {
        var (client, vipps, _, _, _) = Create();
        await client.CreateAgreementAsync(Agreement(max: VippsLimits.MaxAgreementAmountInOre), "key-1");
        Assert.Equal(VippsLimits.MaxAgreementAmountInOre, vipps.Body("/recurring/v3/agreements").GetProperty("pricing").GetProperty("suggestedMaxAmount").GetInt32());
    }

    [Fact]
    public async Task UpdateAgreementMaxAmount_PatchesTheCeiling_OrSendsNothingAboveTheLimit()
    {
        var (client, vipps, _, _, _) = Create();
        await client.UpdateAgreementMaxAmountAsync("agr_1", 59800, "key-2");
        var (request, body) = vipps.Api.Single();
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal("/recurring/v3/agreements/agr_1", request.RequestUri!.AbsolutePath);
        Assert.Equal(59800, JsonDocument.Parse(body).RootElement.GetProperty("pricing").GetProperty("suggestedMaxAmount").GetInt32());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.UpdateAgreementMaxAmountAsync("agr_1", VippsLimits.MaxAgreementAmountInOre + 1, "key-3"));
        Assert.Single(vipps.Api);
    }

    [Fact]
    public async Task GetAgreement_ReadsTheStartAndStopAsUtcWhenVippsSendsNoOffset()
    {
        var (client, vipps, _, _, _) = Create();
        vipps.Respond = r => (HttpStatusCode.OK, r.RequestUri!.AbsolutePath == "/accesstoken/get"
            ? """{"access_token":"t","expires_in":3600}"""
            : """{"id":"agr_1","status":"STOPPED","start":"2026-10-01T08:00:00Z","stop":"2026-10-15T13:00:00"}""");

        var agreement = await client.GetAgreementAsync("agr_1");

        Assert.Equal(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero), agreement.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 13, 0, 0, TimeSpan.Zero), agreement.Stop);
        Assert.Equal(TimeSpan.Zero, agreement.Stop!.Value.Offset);
    }

    [Fact]
    public async Task StopAgreement_PatchesTheStatus()
    {
        var (client, vipps, _, _, _) = Create();
        await client.StopAgreementAsync("agr_1", "key-4");
        var (request, body) = vipps.Api.Single();
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal("""{"status":"STOPPED"}""", body);
        Assert.Equal("key-4", request.Headers.GetValues("Idempotency-Key").Single());
    }

    [Fact]
    public async Task CreateCharge_SendsADirectCaptureRecurringChargeDueOnTheDate()
    {
        var (client, vipps, _, _, _) = Create();
        var charge = await client.CreateChargeAsync("agr_1", new VippsCreateChargeRequest
        {
            AmountInOre = 29900, Due = new DateOnly(2026, 11, 1), Description = "Garge November", OrderId = "garge-sub-1-2026-11",
        }, "garge-sub-1-2026-11");

        var body = vipps.Body("/recurring/v3/agreements/agr_1/charges");
        Assert.Equal(29900, body.GetProperty("amount").GetInt32());
        Assert.Equal("DIRECT_CAPTURE", body.GetProperty("transactionType").GetString());
        Assert.Equal("RECURRING", body.GetProperty("type").GetString());
        Assert.Equal("2026-11-01", body.GetProperty("due").GetString());
        Assert.Equal(5, body.GetProperty("retryDays").GetInt32());
        Assert.Equal("garge-sub-1-2026-11", body.GetProperty("orderId").GetString());
        Assert.Equal("chr_1", charge.ChargeId);
    }

    [Fact]
    public async Task CreateCharge_MovesADueDateSoonerThanVippsAllowsToTheEarliestItAccepts()
    {
        var (client, vipps, _, _, _) = Create();
        await client.CreateChargeAsync("agr_1", new VippsCreateChargeRequest { AmountInOre = 100, Due = DateOnly.FromDateTime(Now.UtcDateTime), Description = "x", OrderId = "o" }, "k");
        Assert.Equal("2026-10-13", vipps.Body("/recurring/v3/agreements/agr_1/charges").GetProperty("due").GetString());
    }

    [Fact]
    public async Task CreateCharge_CutsTheOrderIdTo50Characters()
    {
        var (client, vipps, _, _, _) = Create();
        await client.CreateChargeAsync("agr_1", new VippsCreateChargeRequest { AmountInOre = 100, Due = new DateOnly(2026, 12, 1), Description = "x", OrderId = new string('o', 60) }, "k");
        Assert.Equal(new string('o', 50), vipps.Body("/recurring/v3/agreements/agr_1/charges").GetProperty("orderId").GetString());
    }

    [Fact]
    public async Task RefundCharge_PostsTheAmountAndDescriptionToTheCharge()
    {
        var (client, vipps, _, _, _) = Create();

        await client.RefundChargeAsync("agr_1", "chr/7", 12500, "Refund of October", "refund-41-0");

        var (request, body) = vipps.Api.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/recurring/v3/agreements/agr_1/charges/chr%2F7/refund", request.RequestUri!.AbsolutePath);
        Assert.Equal("""{"amount":12500,"description":"Refund of October"}""", body);
        Assert.Equal("refund-41-0", request.Headers.GetValues("Idempotency-Key").Single());
    }

    [Fact]
    public async Task RefundCharge_CutsTheDescriptionTo100Characters()
    {
        var (client, vipps, _, _, _) = Create();

        await client.RefundChargeAsync("agr_1", "chr_1", 100, new string('d', 120), "k");

        Assert.Equal(new string('d', 100), vipps.Body("/recurring/v3/agreements/agr_1/charges/chr_1/refund").GetProperty("description").GetString());
    }

    [Fact]
    public async Task CreatePayment_SendsAReservedWalletPaymentWithAReceipt()
    {
        var (client, vipps, env, _, _) = Create();
        env.IsTest = true;
        var created = await client.CreatePaymentAsync(Payment(
            new VippsReceiptLine { Id = "1", Name = "Sensor", UnitPriceInOre = 37375, Quantity = 2, TaxRateBasisPoints = 2500 }), "order-42");

        var body = vipps.Body("/epayment/v1/payments");
        Assert.Equal(74750, body.GetProperty("amount").GetProperty("value").GetInt32());
        Assert.Equal("WALLET", body.GetProperty("paymentMethod").GetProperty("type").GetString());
        Assert.Equal("RESERVE_CAPTURE", body.GetProperty("captureType").GetString());
        Assert.Equal("WEB_REDIRECT", body.GetProperty("userFlow").GetString());
        Assert.Equal("https://www.garge.no/shop/return?orderId=42", body.GetProperty("returnUrl").GetString());
        Assert.Equal("order-42", body.GetProperty("receipt").GetProperty("bottomLine").GetProperty("receiptNumber").GetString());
        var line = body.GetProperty("receipt").GetProperty("orderLines")[0];
        Assert.Equal(74750, line.GetProperty("totalAmount").GetInt32());
        Assert.Equal(59800, line.GetProperty("totalAmountExcludingTax").GetInt32());
        Assert.Equal(14950, line.GetProperty("totalTaxAmount").GetInt32());
        Assert.Equal(2500, line.GetProperty("taxRate").GetInt32());
        // taxPercentage takes 0 to 100 and is deprecated, so it is never sent.
        Assert.False(line.TryGetProperty("taxPercentage", out _));
        Assert.Equal("2", line.GetProperty("unitInfo").GetProperty("quantity").GetString());
        Assert.True(created.IsTest);
        Assert.StartsWith("https://apitest.vipps.no/", vipps.Api.Single().Request.RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreatePayment_AsksForTheDefaultProfileScope()
    {
        var (client, vipps, _, _, _) = Create();
        await client.CreatePaymentAsync(Payment(new VippsReceiptLine { Id = "1", Name = "Sensor", UnitPriceInOre = 100, Quantity = 1 }), "o");
        Assert.Equal("name address email phoneNumber", vipps.Body("/epayment/v1/payments").GetProperty("profile").GetProperty("scope").GetString());
    }

    [Fact]
    public async Task CreatePayment_SendsTheGivenProfileScope()
    {
        var (client, vipps, _, _, _) = Create();
        await client.CreatePaymentAsync(Payment(new VippsReceiptLine { Id = "1", Name = "Sensor", UnitPriceInOre = 100, Quantity = 1 }) with { Scope = "name" }, "o");
        Assert.Equal("name", vipps.Body("/epayment/v1/payments").GetProperty("profile").GetProperty("scope").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task CreatePayment_WithoutAScope_SendsNoProfile(string? scope)
    {
        var (client, vipps, _, _, _) = Create();
        await client.CreatePaymentAsync(Payment(new VippsReceiptLine { Id = "1", Name = "Sensor", UnitPriceInOre = 100, Quantity = 1 }) with { Scope = scope }, "o");
        var body = vipps.Body("/epayment/v1/payments");
        Assert.False(body.TryGetProperty("profile", out _));
        Assert.Equal(100, body.GetProperty("amount").GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task CreatePayment_WithoutVat_SendsAZeroTaxRateAndTheWholeAmountExcludingTax()
    {
        var (client, vipps, _, _, _) = Create();
        await client.CreatePaymentAsync(Payment(new VippsReceiptLine { Id = "1", Name = "Sensor", UnitPriceInOre = 29900, Quantity = 1 }), "o");
        var line = vipps.Body("/epayment/v1/payments").GetProperty("receipt").GetProperty("orderLines")[0];
        Assert.Equal(0, line.GetProperty("taxRate").GetInt32());
        Assert.Equal(29900, line.GetProperty("totalAmountExcludingTax").GetInt32());
        Assert.Equal(0, line.GetProperty("totalTaxAmount").GetInt32());
    }

    [Theory]
    [InlineData(12500, 2500, 10000)]
    [InlineData(1, 2500, 1)]
    [InlineData(3, 2500, 2)]
    [InlineData(11500, 1500, 10000)]
    [InlineData(999, 0, 999)]
    [InlineData(5, 10000, 3)]
    [InlineData(7, 10000, 4)]
    public void ExcludingTax_SplitsTheVatOutRoundingHalfAwayFromZero(int total, int basisPoints, int expected) =>
        Assert.Equal(expected, VippsClient.ExcludingTax(total, basisPoints));

    [Fact]
    public async Task PaymentOperations_GoToTheEnvironmentTheyArePassed_WithAmountsInOre()
    {
        var (client, vipps, env, _, _) = Create();
        env.IsTest = false;
        vipps.Respond = r => r.RequestUri!.AbsolutePath == "/accesstoken/get"
            ? (HttpStatusCode.OK, """{"access_token":"t","expires_in":3600}""")
            : (HttpStatusCode.OK, """{"reference":"order-42","state":"AUTHORIZED","profile":{"sub":"sub-1"},"aggregate":{"capturedAmount":{"value":500},"refundedAmount":{"value":200},"cancelledAmount":{"value":0}}}""");

        var payment = await client.GetPaymentAsync("order-42", isTest: true);
        await client.CapturePaymentAsync("order-42", 500, "cap-1", isTest: true);
        await client.RefundPaymentAsync("order-42", 200, "ref-1", isTest: true);
        await client.CancelPaymentAsync("order-42", "can-1", isTest: true);

        Assert.Equal(new VippsPayment { Reference = "order-42", State = "AUTHORIZED", ProfileSub = "sub-1", CapturedAmountInOre = 500, RefundedAmountInOre = 200 }, payment);
        Assert.All(vipps.Sent, s => Assert.Equal("apitest.vipps.no", s.Request.RequestUri!.Host));
        var paths = vipps.Api.Select(s => $"{s.Request.Method} {s.Request.RequestUri!.AbsolutePath}").ToList();
        Assert.Equal(["GET /epayment/v1/payments/order-42", "POST /epayment/v1/payments/order-42/capture", "POST /epayment/v1/payments/order-42/refund", "POST /epayment/v1/payments/order-42/cancel"], paths);
        Assert.Equal("""{"modificationAmount":{"value":500,"currency":"NOK"}}""", vipps.Api.ElementAt(1).Body);
    }

    [Fact]
    public async Task GetPayment_ReadsCaptureGuaranteedUntil_AsUtcWhenItHasNoOffset()
    {
        var (client, vipps, _, _, _) = Create();
        vipps.Respond = r => r.RequestUri!.AbsolutePath == "/accesstoken/get"
            ? (HttpStatusCode.OK, """{"access_token":"t","expires_in":3600}""")
            : (HttpStatusCode.OK, """{"reference":"order-42","state":"AUTHORIZED","captureGuaranteedUntil":"2026-10-25T12:00:00"}""");

        var payment = await client.GetPaymentAsync("order-42");

        Assert.Equal(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero), payment.CaptureGuaranteedUntil);
    }

    [Fact]
    public async Task GetPayment_LeavesCaptureGuaranteedUntilNull_WhenVippsLeavesItOut()
    {
        var (client, vipps, _, _, _) = Create();
        vipps.Respond = r => r.RequestUri!.AbsolutePath == "/accesstoken/get"
            ? (HttpStatusCode.OK, """{"access_token":"t","expires_in":3600}""")
            : (HttpStatusCode.OK, """{"reference":"order-42","state":"AUTHORIZED"}""");

        var payment = await client.GetPaymentAsync("order-42");

        Assert.Null(payment.CaptureGuaranteedUntil);
    }

    [Fact]
    public async Task EveryRequest_CarriesTheEnvironmentsCredentialsAndTheSystemHeaders()
    {
        var (client, vipps, env, _, _) = Create();
        await client.CreateAgreementAsync(Agreement(), "key-1");
        env.IsTest = true;
        await client.CreateAgreementAsync(Agreement(), "key-2");

        var (live, test) = (vipps.Api.First().Request, vipps.Api.Last().Request);
        Assert.Equal(("Bearer", "token-api.vipps.no"), (live.Headers.Authorization!.Scheme, live.Headers.Authorization.Parameter));
        Assert.Equal("111111", live.Headers.GetValues("Merchant-Serial-Number").Single());
        Assert.Equal("key", live.Headers.GetValues("Ocp-Apim-Subscription-Key").Single());
        Assert.Equal("token-apitest.vipps.no", test.Headers.Authorization!.Parameter);
        Assert.Equal("222222", test.Headers.GetValues("Merchant-Serial-Number").Single());
        Assert.Equal("tkey", test.Headers.GetValues("Ocp-Apim-Subscription-Key").Single());
        foreach (var request in new[] { live, test })
        {
            Assert.Equal("garge-api", request.Headers.GetValues("Vipps-System-Name").Single());
            Assert.Equal("1.4.2", request.Headers.GetValues("Vipps-System-Version").Single());
        }
        Assert.Equal("key-1", live.Headers.GetValues("Idempotency-Key").Single());
    }

    [Fact]
    public async Task AccessToken_IsFetchedWithTheClientCredentials_AndReusedUntilAMinuteBeforeItExpires()
    {
        var (client, vipps, _, time, _) = Create();
        await client.GetAgreementAsync("agr_1");
        await client.GetAgreementAsync("agr_1");
        var tokenRequests = vipps.Sent.Where(s => s.Request.RequestUri!.AbsolutePath == "/accesstoken/get").ToList();
        Assert.Single(tokenRequests);
        var token = tokenRequests[0].Request;
        Assert.Equal(HttpMethod.Post, token.Method);
        Assert.Equal("id", token.Headers.GetValues("client_id").Single());
        Assert.Equal("secret", token.Headers.GetValues("client_secret").Single());
        Assert.Equal("key", token.Headers.GetValues("Ocp-Apim-Subscription-Key").Single());

        time.Advance(TimeSpan.FromSeconds(3600 - 61));
        await client.GetAgreementAsync("agr_1");
        Assert.Single(vipps.Sent, s => s.Request.RequestUri!.AbsolutePath == "/accesstoken/get");
        time.Advance(TimeSpan.FromSeconds(2));
        await client.GetAgreementAsync("agr_1");
        Assert.Equal(2, vipps.Sent.Count(s => s.Request.RequestUri!.AbsolutePath == "/accesstoken/get"));
    }

    [Fact]
    public async Task AccessToken_IsKeptPerEnvironment()
    {
        var (client, vipps, _, _, _) = Create();
        await client.GetAgreementAsync("a", isTest: false);
        await client.GetAgreementAsync("a", isTest: true);
        await client.GetAgreementAsync("a", isTest: false);
        Assert.Equal(["api.vipps.no", "apitest.vipps.no"], vipps.Sent.Where(s => s.Request.RequestUri!.AbsolutePath == "/accesstoken/get").Select(s => s.Request.RequestUri!.Host));
        Assert.Equal(["token-api.vipps.no", "token-apitest.vipps.no", "token-api.vipps.no"], vipps.Api.Select(s => s.Request.Headers.Authorization!.Parameter));
    }

    [Theory]
    [InlineData("\"30\"", 60)]
    [InlineData("\"soon\"", 3540)]
    public async Task AccessToken_WithAShortOrUnreadableLifetime_IsKeptAtLeastAMinute(string expiresIn, int keptSeconds)
    {
        var (client, vipps, _, time, _) = Create();
        vipps.Respond = r => (HttpStatusCode.OK, r.RequestUri!.AbsolutePath == "/accesstoken/get" ? $$"""{"access_token":"t","expires_in":{{expiresIn}}}""" : "{}");
        await client.GetAgreementAsync("a");
        time.Advance(TimeSpan.FromSeconds(keptSeconds - 1));
        await client.GetAgreementAsync("a");
        time.Advance(TimeSpan.FromSeconds(1));
        await client.GetAgreementAsync("a");
        Assert.Equal(2, vipps.Sent.Count(s => s.Request.RequestUri!.AbsolutePath == "/accesstoken/get"));
    }

    [Fact]
    public async Task PathSegments_AreEscaped()
    {
        var (client, vipps, _, _, _) = Create();
        await client.GetPaymentAsync("a/b?c");
        Assert.Equal("/epayment/v1/payments/a%2Fb%3Fc", vipps.Api.Single().Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task UserInfo_MapsTheProfile_AndIsNullWithoutASub()
    {
        var (client, vipps, _, _, _) = Create();
        Assert.Null(await client.GetUserInfoAsync(""));
        Assert.Empty(vipps.Sent);

        vipps.Respond = r => (HttpStatusCode.OK, r.RequestUri!.AbsolutePath == "/accesstoken/get"
            ? """{"access_token":"t"}"""
            : """{"name":"Ola","email":"ola@example.no","phone_number":"4791234567","address":{"street_address":"Gate 1","postal_code":"0001","region":"Oslo","country":"NO","formatted":"Gate 1, 0001 Oslo"}}""");
        var info = await client.GetUserInfoAsync("sub-1");
        Assert.Equal("/vipps-userinfo-api/userinfo/sub-1", vipps.Api.Single().Request.RequestUri!.AbsolutePath);
        Assert.Equal(new VippsUserInfo
        {
            Name = "Ola", Email = "ola@example.no", PhoneNumber = "4791234567",
            Address = new VippsAddress { StreetAddress = "Gate 1", PostalCode = "0001", Region = "Oslo", Country = "NO", Formatted = "Gate 1, 0001 Oslo" },
        }, info);
    }

    [Fact]
    public async Task Webhooks_AreRegisteredListedAndDeleted()
    {
        var (client, vipps, _, _, _) = Create();
        vipps.Respond = r => (HttpStatusCode.OK, (r.Method.Method, r.RequestUri!.AbsolutePath) switch
        {
            (_, "/accesstoken/get") => """{"access_token":"t"}""",
            ("POST", _) => """{"id":"wh_1","secret":"s3cret"}""",
            ("GET", _) => """{"webhooks":[{"id":"wh_1","url":"https://x/a"},{"url":"https://x/b"}]}""",
            _ => "",
        });

        Assert.Equal(new VippsRegisteredWebhook("wh_1", "s3cret"), await client.RegisterWebhookAsync("https://x/a", VippsEvents.Payments, isTest: true));
        Assert.Equal([new VippsWebhook { Id = "wh_1", Url = "https://x/a" }, new VippsWebhook { Url = "https://x/b" }], await client.ListWebhooksAsync(isTest: true));
        await client.DeleteWebhookAsync("wh_1", isTest: true);

        var register = vipps.Api.First();
        Assert.Equal("https://x/a", JsonDocument.Parse(register.Body).RootElement.GetProperty("url").GetString());
        Assert.Equal(VippsEvents.Payments, JsonDocument.Parse(register.Body).RootElement.GetProperty("events").EnumerateArray().Select(e => e.GetString()!));
        Assert.True(Guid.TryParse(register.Request.Headers.GetValues("Idempotency-Key").Single(), out _));
        Assert.Equal("DELETE /webhooks/v1/webhooks/wh_1", $"{vipps.Api.Last().Request.Method} {vipps.Api.Last().Request.RequestUri!.AbsolutePath}");
    }

    [Fact]
    public async Task AFailure_LogsVippsErrorIdsInPlaceOfTheBody_AndThrows()
    {
        var (client, vipps, _, _, log) = Create();
        vipps.Respond = r => r.RequestUri!.AbsolutePath == "/accesstoken/get"
            ? (HttpStatusCode.OK, """{"access_token":"t"}""")
            : (HttpStatusCode.BadRequest, """{"errorCode":"E1","errorRef":"R1","traceId":"T1","phoneNumber":"4791234567"}""");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAgreementAsync("agr_1"));
        var line = Assert.Single(log.Lines, l => l.StartsWith("Error", StringComparison.Ordinal));
        Assert.Contains("ErrorCode=E1 ErrorRef=R1 TraceId=T1 BodyLength=", line, StringComparison.Ordinal);
        Assert.DoesNotContain("4791234567", line, StringComparison.Ordinal);
        Assert.Contains("get-agreement failed: 400", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailure_WithoutVippsErrorIds_LogsOnlyTheBodyLength()
    {
        var (client, vipps, _, _, log) = Create();
        vipps.Respond = r => r.RequestUri!.AbsolutePath == "/accesstoken/get" ? (HttpStatusCode.BadRequest, """{"phoneNumber":"4791234567"}""") : (HttpStatusCode.OK, "{}");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAgreementAsync("agr_1"));
        var line = Assert.Single(log.Lines, l => l.StartsWith("Error", StringComparison.Ordinal));
        Assert.Contains("accesstoken/get failed: 400 ErrorCode=- ErrorRef=- TraceId=- BodyLength=28", line, StringComparison.Ordinal);
        Assert.DoesNotContain("4791234567", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailure_WithoutVippsErrorIds_LogsTheBody_WhenTheAppTurnsThatOn()
    {
        var vipps = new FakeVipps { Respond = r => r.RequestUri!.AbsolutePath == "/accesstoken/get" ? (HttpStatusCode.Unauthorized, "not json") : (HttpStatusCode.OK, "{}") };
        var log = new ListLogger();
        var options = Options.Create(new VippsOptions { LogFailedResponseBodies = true, SystemName = "x" });
        var client = new VippsClient(new HttpClient(vipps), options, new FixedVippsEnvironment(false), log, new FakeTimeProvider(Now));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAgreementAsync("agr_1"));
        Assert.Contains(log.Lines, l => l.Contains("accesstoken/get failed: 401 body=not json", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", null, null, null)]
    [InlineData("[]", null, null, null)]
    [InlineData("{\"errorCode\":5}", null, null, null)]
    [InlineData("{\"errorRef\":\"R\"}", null, "R", null)]
    public void ErrorIds_ReadsOnlyStringIds(string body, string? code, string? reference, string? trace) =>
        Assert.Equal((code, reference, trace), VippsClient.ErrorIds(body));
}
