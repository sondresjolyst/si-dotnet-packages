using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace SjolystInnovation.AppKit.Vipps.Tests;

public class VippsWebhookEndpointTests
{
    private const string Shop = "/api/shop/webhook";
    private const string Subscription = "/api/subscriptions/webhook";
    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private static string PaymentBody(string? msn = WebhookTestApp.LiveMsn, string psp = "psp-1", string name = "AUTHORIZED", string reference = "order-1") =>
        JsonSerializer.Serialize(new { msn, reference, pspReference = psp, name, amount = new { value = 10000, currency = "NOK" }, success = true });

    private static string RecurringBody(string? msn = WebhookTestApp.LiveMsn, string? eventId = null, string? chargeId = null,
        string eventType = VippsEvents.AgreementActivated, DateTime? occurred = null) =>
        JsonSerializer.Serialize(new { msn, agreementId = "agr_1", eventId, eventType, chargeId, occurred = occurred ?? new DateTime(2026, 10, 15, 11, 0, 0, DateTimeKind.Utc), amount = 300 });

    // Signature and environment

    [Fact]
    public async Task An_event_signed_with_the_live_secret_is_applied_as_live_and_saved_with_its_marker()
    {
        await using var app = await WebhookTestApp.StartAsync();

        var response = await app.PostAsync(Shop, PaymentBody());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var delivery = Assert.Single(app.Script.Payments);
        Assert.False(delivery.IsTest);
        Assert.Equal("psp-1", delivery.EventKey);
        Assert.Equal("shop", delivery.Slot);
        Assert.Equal("order-1", delivery.Event.Reference);
        Assert.Equal(10000, delivery.Event.Amount?.Value);
        Assert.True(delivery.Event.Success);
        var marker = Assert.Single(await app.MarkersAsync());
        Assert.Equal(("psp-1", "shop", new DateTime(2026, 10, 15, 12, 0, 0)), (marker.Id, marker.Source, marker.ProcessedAt));
        Assert.Equal(1, await app.AppliedAsync());
        Assert.Equal(1, app.Script.AfterCommits);
    }

    [Fact]
    public async Task An_event_signed_with_the_test_secret_is_test_and_keyed_apart()
    {
        await using var app = await WebhookTestApp.StartAsync();

        var response = await app.PostAsync(Shop, PaymentBody(WebhookTestApp.TestMsn), WebhookTestApp.TestSecret);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var delivery = Assert.Single(app.Script.Payments);
        Assert.True(delivery.IsTest);
        Assert.Equal("test:psp-1", delivery.EventKey);
        Assert.Equal("test:psp-1", Assert.Single(await app.MarkersAsync()).Id);
    }

    [Fact]
    public async Task The_same_event_from_live_and_test_is_handled_once_in_each()
    {
        await using var app = await WebhookTestApp.StartAsync();

        await app.PostAsync(Shop, PaymentBody());
        await app.PostAsync(Shop, PaymentBody(WebhookTestApp.TestMsn), WebhookTestApp.TestSecret);

        Assert.Equal(["psp-1", "test:psp-1"], (await app.MarkersAsync()).Select(m => m.Id));
    }

    [Theory]
    [InlineData("other-secret")]
    [InlineData("")]
    public async Task A_bad_signature_is_401_and_nothing_is_handled(string secret)
    {
        await using var app = await WebhookTestApp.StartAsync();

        var response = await app.PostAsync(Shop, PaymentBody(), secret);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(app.Script.Payments);
        Assert.Empty(await app.MarkersAsync());
    }

    [Fact]
    public async Task Without_stored_secrets_every_event_is_401()
    {
        await using var app = await WebhookTestApp.StartAsync(liveSecret: false, testSecret: false);

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.PostAsync(Shop, PaymentBody())).StatusCode);
    }

    [Fact]
    public async Task A_slot_only_accepts_its_own_secrets()
    {
        await using var app = await WebhookTestApp.StartAsync();
        await app.DbAsync(async db =>
        {
            var entry = await db.Set<VippsWebhookRegistrationEntry>().SingleAsync(r => r.Slot == "subscription" && !r.IsTest);
            entry.ProtectedSecret = "subscription-only";
            return await db.SaveChangesAsync();
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.PostAsync(Subscription, RecurringBody())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.PostAsync(Subscription, RecurringBody(), "subscription-only")).StatusCode);
    }

    [Fact]
    public async Task The_hash_covers_the_raw_bytes_so_a_body_with_a_byte_order_mark_passes()
    {
        await using var app = await WebhookTestApp.StartAsync();
        byte[] body = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(PaymentBody())];

        var response = await app.PostAsync(Shop, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("psp-1", Assert.Single(app.Script.Payments).Event.PspReference);
    }

    [Fact]
    public async Task A_hash_of_the_text_without_its_byte_order_mark_does_not_pass()
    {
        await using var app = await WebhookTestApp.StartAsync();
        var text = PaymentBody();
        var request = new HttpRequestMessage(HttpMethod.Post, Shop) { Content = new ByteArrayContent([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)]) };
        var signed = await SignedHeadersAsync(app, Encoding.UTF8.GetBytes(text));
        foreach (var (name, value) in signed) request.Headers.TryAddWithoutValidation(name, value);

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.SendAsync(request)).StatusCode);
    }

    private static Task<(string, string)[]> SignedHeadersAsync(WebhookTestApp app, byte[] body)
    {
        var date = app.Time.GetUtcNow().ToString("R");
        var hash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(body));
        var signature = Convert.ToBase64String(System.Security.Cryptography.HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(WebhookTestApp.LiveSecret), Encoding.UTF8.GetBytes($"POST\n{Shop}\n{date};localhost;{hash}")));
        return Task.FromResult<(string, string)[]>([
            ("x-ms-date", date), ("x-ms-content-sha256", hash),
            ("Authorization", $"HMAC-SHA256 SignedHeaders=x-ms-date;host;x-ms-content-sha256&Signature={signature}")]);
    }

    [Fact]
    public async Task An_unreadable_stored_secret_counts_as_missing_instead_of_failing_every_delivery()
    {
        await using var app = await WebhookTestApp.StartAsync();
        await app.DbAsync(async db =>
        {
            var entry = await db.Set<VippsWebhookRegistrationEntry>().SingleAsync(r => r.Slot == "shop" && !r.IsTest);
            entry.ProtectedSecret = "enc:v1:not-a-protected-value";
            return await db.SaveChangesAsync();
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.PostAsync(Shop, PaymentBody())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.PostAsync(Shop, PaymentBody(WebhookTestApp.TestMsn), WebhookTestApp.TestSecret)).StatusCode);
    }

    [Fact]
    public async Task A_body_that_is_not_an_event_is_400()
    {
        await using var app = await WebhookTestApp.StartAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostAsync(Shop, "not json")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostAsync(Shop, "null")).StatusCode);
        Assert.Empty(await app.MarkersAsync());
    }

    [Fact]
    public async Task A_request_without_signature_headers_or_with_a_stale_date_is_401_before_the_body_is_read()
    {
        // The body is over the limit, so a 401 instead of a 413 shows the headers were checked first.
        await using var app = await WebhookTestApp.StartAsync(o => o.MaxBodyBytes = 10);
        var body = Encoding.UTF8.GetBytes(PaymentBody());
        var unsigned = new HttpRequestMessage(HttpMethod.Post, Shop) { Content = new ByteArrayContent(body) };
        var stale = app.Signed(Shop, new ByteArrayContent(body), body, signedAt: app.Time.GetUtcNow().AddDays(-9));
        var badDate = app.Signed(Shop, new ByteArrayContent(body), body);
        badDate.Headers.Remove("x-ms-date");
        badDate.Headers.TryAddWithoutValidation("x-ms-date", "yesterday");

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.SendAsync(unsigned)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.SendAsync(stale)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.SendAsync(badDate)).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await app.PostAsync(Shop, body)).StatusCode);
        Assert.Empty(app.Script.Payments);
    }

    /// <summary>A body without a length, so only the read itself can enforce the limit.</summary>
    private sealed class UnknownLengthContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(body).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    [Fact]
    public async Task A_body_over_the_limit_is_413_with_or_without_a_length()
    {
        await using var app = await WebhookTestApp.StartAsync(o => o.MaxBodyBytes = 100);
        var body = Encoding.UTF8.GetBytes(PaymentBody() + new string(' ', 200));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await app.PostAsync(Shop, body)).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await app.Client.SendAsync(app.Signed(Shop, new UnknownLengthContent(body), body))).StatusCode);
        Assert.Empty(app.Script.Payments);
    }

    [Fact]
    public async Task The_default_limit_is_64_KB()
    {
        await using var app = await WebhookTestApp.StartAsync();
        var body = Encoding.UTF8.GetBytes(PaymentBody() + new string(' ', 64 * 1024));

        Assert.Equal(64 * 1024, new VippsWebhookOptions().MaxBodyBytes);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await app.PostAsync(Shop, body)).StatusCode);
    }

    [Fact]
    public async Task The_signature_covers_the_path_base()
    {
        await using var app = await WebhookTestApp.StartAsync(pathBase: "/svc");

        Assert.Equal(HttpStatusCode.OK, (await app.PostAsync("/svc" + Shop, PaymentBody())).StatusCode);
        Assert.Single(app.Script.Payments);
    }

    [Fact]
    public async Task The_secrets_are_cached_for_the_configured_time()
    {
        await using var app = await WebhookTestApp.StartAsync(o => o.SecretCacheDuration = TimeSpan.FromSeconds(30));
        await app.PostAsync(Shop, PaymentBody(psp: "a"));
        await app.DbAsync(async db =>
        {
            var entry = await db.Set<VippsWebhookRegistrationEntry>().SingleAsync(r => r.Slot == "shop" && !r.IsTest);
            entry.ProtectedSecret = "rotated";
            return await db.SaveChangesAsync();
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.PostAsync(Shop, Encoding.UTF8.GetBytes(PaymentBody(psp: "b")), "rotated")).StatusCode);
        app.Time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(HttpStatusCode.OK, (await app.PostAsync(Shop, Encoding.UTF8.GetBytes(PaymentBody(psp: "c")), "rotated")).StatusCode);
    }

    // Duplicates

    [Fact]
    public async Task A_redelivered_event_is_skipped()
    {
        await using var app = await WebhookTestApp.StartAsync();

        await app.PostAsync(Shop, PaymentBody());
        var again = await app.PostAsync(Shop, PaymentBody());

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Single(app.Script.Payments);
        Assert.Equal(1, await app.AppliedAsync());
    }

    [Fact]
    public async Task Two_concurrent_deliveries_of_one_event_store_it_once()
    {
        await using var app = await WebhookTestApp.StartAsync();
        // Both deliveries pass the duplicate check before either saves.
        using var bothHandling = new Barrier(2);
        app.Script.Handle = _ =>
        {
            bothHandling.SignalAndWait(TimeSpan.FromSeconds(10));
            return Task.FromResult(VippsWebhookOutcome.Applied(_ => { Interlocked.Increment(ref app.Script.AfterCommits); return Task.CompletedTask; }));
        };

        var responses = await Task.WhenAll(
            Task.Run(() => app.PostAsync(Subscription, RecurringBody(eventId: "evt-race"))),
            Task.Run(() => app.PostAsync(Subscription, RecurringBody(eventId: "evt-race"))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(2, app.Script.Recurring.Count);
        Assert.Equal("evt-race", Assert.Single(await app.MarkersAsync()).Id);
        Assert.Equal(1, await app.AppliedAsync());
        Assert.Equal(1, app.Script.AfterCommits);
    }

    [Fact]
    public async Task Losing_a_concurrent_delivery_that_saves_first_saves_nothing_and_skips_the_side_effects()
    {
        await using var app = await WebhookTestApp.StartAsync();
        app.Script.Handle = async _ =>
        {
            // The other delivery saves its marker while this one is handling the event.
            await app.DbAsync(db =>
            {
                db.ProcessedWebhookEvents.Add(new VippsProcessedWebhookEvent { Id = "psp-1", Source = "shop" });
                return db.SaveChangesAsync();
            });
            return VippsWebhookOutcome.Applied(_ => { Interlocked.Increment(ref app.Script.AfterCommits); return Task.CompletedTask; });
        };

        var response = await app.PostAsync(Shop, PaymentBody());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(await app.MarkersAsync());
        Assert.Equal(0, await app.AppliedAsync());
        Assert.Equal(0, app.Script.AfterCommits);
    }

    // Merchant serial number

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("msn-other")]
    [InlineData(WebhookTestApp.TestMsn)]
    public async Task A_payment_event_for_another_merchant_is_acknowledged_but_not_handled(string? msn)
    {
        await using var app = await WebhookTestApp.StartAsync();

        var response = await app.PostAsync(Shop, PaymentBody(msn));

        // 200, so Vipps does not retry it for a week and hold back the payment's later events.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(app.Script.Payments);
        Assert.Single(await app.MarkersAsync());
    }

    [Theory]
    [InlineData("msn-other")]
    [InlineData(WebhookTestApp.TestMsn)]
    public async Task A_recurring_event_for_another_merchant_is_acknowledged_but_not_handled(string msn)
    {
        await using var app = await WebhookTestApp.StartAsync();

        var response = await app.PostAsync(Subscription, RecurringBody(msn));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(app.Script.Recurring);
        Assert.Single(await app.MarkersAsync());
    }

    [Fact]
    public async Task A_test_event_must_carry_the_test_merchant()
    {
        await using var app = await WebhookTestApp.StartAsync();

        await app.PostAsync(Subscription, RecurringBody(WebhookTestApp.LiveMsn, eventId: "a"), WebhookTestApp.TestSecret);
        await app.PostAsync(Subscription, RecurringBody(WebhookTestApp.TestMsn, eventId: "b"), WebhookTestApp.TestSecret);

        Assert.True(Assert.Single(app.Script.Recurring).IsTest);
        Assert.Equal("test:b", app.Script.Recurring[0].EventKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_recurring_event_without_a_merchant_is_handled_by_default(string? msn)
    {
        await using var app = await WebhookTestApp.StartAsync();

        await app.PostAsync(Subscription, RecurringBody(msn));
        await app.PostAsync(Subscription, RecurringBody("msn-other", eventId: "other"));

        Assert.Single(app.Script.Recurring);
        Assert.Equal(2, (await app.MarkersAsync()).Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_recurring_event_without_a_merchant_is_rejected_when_the_option_is_off(string? msn)
    {
        await using var app = await WebhookTestApp.StartAsync(o => o.AllowMissingRecurringMsn = false);

        var response = await app.PostAsync(Subscription, RecurringBody(msn));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(app.Script.Recurring);
        Assert.Single(await app.MarkersAsync());
    }

    [Fact]
    public async Task A_payment_event_without_a_merchant_is_rejected_even_when_recurring_ones_are_allowed()
    {
        await using var app = await WebhookTestApp.StartAsync(o => o.AllowMissingRecurringMsn = true);

        await app.PostAsync(Shop, PaymentBody(msn: null));

        Assert.Empty(app.Script.Payments);
    }

    // Outcomes

    [Fact]
    public async Task A_rejected_event_drops_the_handlers_changes_and_is_marked_processed()
    {
        await using var app = await WebhookTestApp.StartAsync();
        app.Script.Handle = _ => Task.FromResult(VippsWebhookOutcome.Rejected);

        var response = await app.PostAsync(Shop, PaymentBody());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await app.AppliedAsync());
        Assert.Single(await app.MarkersAsync());
        Assert.Equal(HttpStatusCode.OK, (await app.PostAsync(Shop, PaymentBody())).StatusCode);
        Assert.Single(app.Script.Payments);
    }

    [Fact]
    public async Task An_event_left_for_a_retry_is_500_and_the_redelivery_is_handled()
    {
        await using var app = await WebhookTestApp.StartAsync();
        app.Script.Handle = _ => Task.FromResult(VippsWebhookOutcome.Retry);

        Assert.Equal(HttpStatusCode.InternalServerError, (await app.PostAsync(Shop, PaymentBody())).StatusCode);
        Assert.Empty(await app.MarkersAsync());
        Assert.Equal(0, await app.AppliedAsync());

        app.Script.Handle = null;
        Assert.Equal(HttpStatusCode.OK, (await app.PostAsync(Shop, PaymentBody())).StatusCode);
        Assert.Equal(1, await app.AppliedAsync());
    }

    [Fact]
    public async Task A_handler_that_throws_is_500_and_nothing_is_saved()
    {
        await using var app = await WebhookTestApp.StartAsync();
        app.Script.Handle = _ => throw new InvalidOperationException("boom");

        Assert.Equal(HttpStatusCode.InternalServerError, (await app.PostAsync(Shop, PaymentBody())).StatusCode);
        Assert.Empty(await app.MarkersAsync());
    }

    [Fact]
    public async Task A_failed_save_is_not_marked_processed_and_the_redelivery_is_applied()
    {
        await using var app = await WebhookTestApp.StartAsync();
        app.Faults.FailuresLeft = 1;

        Assert.Equal(HttpStatusCode.InternalServerError, (await app.PostAsync(Shop, PaymentBody())).StatusCode);
        Assert.Empty(await app.MarkersAsync());
        Assert.Equal(0, app.Script.AfterCommits);

        Assert.Equal(HttpStatusCode.OK, (await app.PostAsync(Shop, PaymentBody())).StatusCode);
        Assert.Single(await app.MarkersAsync());
        Assert.Equal(1, await app.AppliedAsync());
    }

    [Fact]
    public async Task The_side_effects_run_after_the_commit_and_their_failure_does_not_fail_the_delivery()
    {
        await using var app = await WebhookTestApp.StartAsync();
        int? markersSeen = null;
        app.Script.Handle = _ => Task.FromResult(VippsWebhookOutcome.Applied(async _ =>
        {
            markersSeen = (await app.MarkersAsync()).Count;
            throw new InvalidOperationException("email down");
        }));

        Assert.Equal(HttpStatusCode.OK, (await app.PostAsync(Shop, PaymentBody())).StatusCode);
        Assert.Equal(1, markersSeen);
    }

    // Event keys

    [Fact]
    public async Task A_recurring_event_is_keyed_by_its_event_id_or_else_by_agreement_type_charge_and_time()
    {
        await using var app = await WebhookTestApp.StartAsync();
        var occurred = new DateTime(2026, 10, 15, 11, 0, 0, DateTimeKind.Utc);

        await app.PostAsync(Subscription, RecurringBody(eventId: "evt-1"));
        await app.PostAsync(Subscription, RecurringBody(eventType: VippsEvents.ChargeCaptured, chargeId: "chr-1", occurred: occurred));
        await app.PostAsync(Subscription, RecurringBody(WebhookTestApp.TestMsn, eventType: VippsEvents.ChargeFailed, chargeId: "chr-2", occurred: occurred), WebhookTestApp.TestSecret);

        Assert.Equal(
            ["evt-1", $"agr_1:{VippsEvents.ChargeCaptured}:chr-1:{occurred.Ticks}", $"test:agr_1:{VippsEvents.ChargeFailed}:chr-2:{occurred.Ticks}"],
            app.Script.Recurring.Select(d => d.EventKey));
        Assert.Equal(300, app.Script.Recurring[1].Event.Amount);
    }

    [Fact]
    public void A_payment_event_without_a_psp_reference_is_keyed_by_reference_and_name()
    {
        Assert.Equal("order-1:CREATED", VippsWebhookEndpoint<WebhookDbContext>.PaymentKey(new VippsPaymentEvent { Reference = "order-1", Name = "CREATED" }));
        Assert.Equal("psp", VippsWebhookEndpoint<WebhookDbContext>.PaymentKey(new VippsPaymentEvent { Reference = "order-1", Name = "CREATED", PspReference = "psp" }));
        Assert.Equal("agr:::", VippsWebhookEndpoint<WebhookDbContext>.RecurringKey(new VippsRecurringEvent { AgreementId = "agr" }));
    }

    [Theory]
    [InlineData("2026-10-15T11:00:00Z")]
    [InlineData("2026-10-15T13:00:00+02:00")]
    [InlineData("2026-10-15T11:00:00")]
    public void The_recurring_key_uses_utc_ticks_whatever_the_server_time_zone(string occurred)
    {
        // The same ticks DateTime gave before Occurred became a DateTimeOffset, so stored keys still match.
        var utc = new DateTime(2026, 10, 15, 11, 0, 0, DateTimeKind.Utc);
        var e = JsonSerializer.Deserialize<VippsRecurringEvent>($$"""{"agreementId":"agr","eventType":"t","occurred":"{{occurred}}"}""", CaseInsensitive)!;

        Assert.Equal(utc.Ticks, VippsWebhookEndpoint<WebhookDbContext>.UtcTicks(e.Occurred));
        Assert.Equal($"agr:t::{utc.Ticks}", VippsWebhookEndpoint<WebhookDbContext>.RecurringKey(e));
    }

    [Fact]
    public void A_recurring_event_without_a_time_has_no_ticks()
    {
        var e = JsonSerializer.Deserialize<VippsRecurringEvent>("""{"agreementId":"agr","eventType":"t","occurred":null}""", CaseInsensitive)!;

        Assert.Null(e.Occurred);
        Assert.Null(VippsWebhookEndpoint<WebhookDbContext>.UtcTicks(null));
    }

    // Setup

    [Fact]
    public async Task The_retention_must_exceed_the_signature_max_age()
    {
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => WebhookTestApp.StartAsync(o => o.Retention = VippsWebhookSignature.MaxAge));
        Assert.Contains("retention", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/shop/webhook")]
    [InlineData("/API/Shop/Webhook/")]
    public async Task Two_slots_on_one_path_fail_at_startup(string path)
    {
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            WebhookTestApp.StartAsync(o => o.AddSlot("other", VippsWebhookKind.Recurring, path)));
        Assert.Contains("path", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mapping_an_unknown_slot_or_a_slot_without_a_handler_fails()
    {
        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => StartBareAsync(e => e.MapAppKitVippsWebhook<WebhookDbContext>("nope")));
        Assert.Contains("nope", unknown.Message, StringComparison.Ordinal);
        var noHandler = await Assert.ThrowsAsync<InvalidOperationException>(() => StartBareAsync(e => e.MapAppKitVippsWebhook<WebhookDbContext>("shop")));
        Assert.Contains(nameof(IVippsPaymentWebhookHandler), noHandler.Message, StringComparison.Ordinal);
    }

    private static async Task StartBareAsync(Action<Microsoft.AspNetCore.Routing.IEndpointRouteBuilder> map)
    {
        using var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddDbContext<WebhookDbContext>(o => o.UseSqlite("Data Source=:memory:"));
                    s.AddAppKitVipps(_ => { });
                    s.AddAppKitVippsWebhooks<WebhookDbContext>(o => { o.AddSlot("shop", VippsWebhookKind.Payment, "/hook"); o.RegisterOnStart = false; });
                    s.AddRouting();
                })
                .Configure(web => { web.UseRouting(); web.UseEndpoints(map); }))
            .Build();
        await host.StartAsync();
        await host.StopAsync();
    }
}
