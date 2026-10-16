using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SjolystInnovation.AppKit.Vipps;

/// <summary>Maps the webhook endpoints.</summary>
public static class VippsWebhookEndpointExtensions
{
    /// <summary>
    /// Maps <c>POST</c> on the path of <paramref name="slot"/>, open to everyone. The endpoint checks the
    /// signature against the slot's live and test secrets, skips an event it already processed, checks the
    /// merchant serial number of the environment that signed it, and passes the event to
    /// <see cref="IVippsPaymentWebhookHandler"/> or <see cref="IVippsRecurringWebhookHandler"/>. It answers
    /// 401 for a bad signature, 400 for a body it cannot read, 500 when the handler asks for a retry or
    /// fails, and 200 otherwise. It answers 401 before reading the body when the signature headers are
    /// missing or the signed date is stale, and 413 for a body over <see cref="VippsWebhookOptions.MaxBodyBytes"/>.
    /// </summary>
    /// <typeparam name="TContext">The app's DbContext, which holds the processed events and the handler's changes.</typeparam>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="slot">The name of a slot added in <see cref="VippsWebhookServiceCollectionExtensions.AddAppKitVippsWebhooks{TContext}"/>.</param>
    /// <returns>The endpoint builder, to add rate limiting or other conventions.</returns>
    /// <exception cref="InvalidOperationException">When the slot is unknown or its handler is not registered.</exception>
    public static RouteHandlerBuilder MapAppKitVippsWebhook<TContext>(this IEndpointRouteBuilder endpoints, string slot)
        where TContext : DbContext, IVippsWebhookDbContext
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<VippsWebhookOptions>>().Value;
        var target = options.Slot(slot);
        var handlerType = target.Kind == VippsWebhookKind.Payment ? typeof(IVippsPaymentWebhookHandler) : typeof(IVippsRecurringWebhookHandler);
        var isService = endpoints.ServiceProvider.GetService<IServiceProviderIsService>();
        if (isService is not null && !isService.IsService(handlerType))
            throw new InvalidOperationException($"Vipps webhook slot '{slot}' needs a registered {handlerType.Name}.");

        return endpoints.MapPost(target.Path, (Func<HttpContext, Task<IResult>>)(http => VippsWebhookEndpoint<TContext>.HandleAsync(http, target)))
            .AllowAnonymous()
            .ExcludeFromDescription();
    }
}

internal static partial class VippsWebhookEndpoint<TContext>
    where TContext : DbContext, IVippsWebhookDbContext
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Older signatures are accepted, and logged so the logs show how Vipps signs its retries.
    private static readonly TimeSpan FreshSignatureAge = TimeSpan.FromMinutes(5);

    public static async Task<IResult> HandleAsync(HttpContext http, VippsWebhookSlot slot)
    {
        var services = http.RequestServices;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("SjolystInnovation.AppKit.Vipps.VippsWebhook");
        var cancellationToken = http.RequestAborted;
        var options = services.GetRequiredService<IOptions<VippsWebhookOptions>>().Value;
        var now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        var request = http.Request;
        var host = request.Headers.Host.ToString();
        var signed = new VippsWebhookRequest
        {
            Method = request.Method,
            // The signature covers the path the sender called, which includes any path base.
            PathAndQuery = request.PathBase.Value + request.Path.Value + request.QueryString.Value,
            Host = string.IsNullOrEmpty(host) ? request.Host.Value ?? string.Empty : host,
            Authorization = request.Headers.Authorization.ToString(),
            Date = request.Headers["x-ms-date"].ToString(),
            ContentSha256 = request.Headers["x-ms-content-sha256"].ToString(),
        };

        // Turn away an unsigned or stale request before reading its body or any secret.
        var headers = VippsWebhookSignature.CheckHeaders(signed, now);
        if (!headers.IsValid)
        {
            Log.VerifyFailed(logger, slot.Name, headers.Result);
            return TypedResults.Unauthorized();
        }

        var body = await ReadBodyAsync(request, options.MaxBodyBytes, cancellationToken);
        if (body is null)
        {
            Log.TooLarge(logger, slot.Name, options.MaxBodyBytes);
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var (verify, isTest) = await VerifyAsync(http, slot, signed with { RawBody = body }, now, logger);
        if (verify != VippsWebhookSignatureResult.Valid)
        {
            Log.VerifyFailed(logger, slot.Name, verify);
            return TypedResults.Unauthorized();
        }

        var json = WithoutBom(body);
        try
        {
            return slot.Kind == VippsWebhookKind.Payment
                ? await DeliverAsync(http, slot, isTest, JsonSerializer.Deserialize<VippsPaymentEvent>(json, JsonOptions), PaymentKey, e => e.Msn, false, logger)
                : await DeliverAsync(http, slot, isTest, JsonSerializer.Deserialize<VippsRecurringEvent>(json, JsonOptions), RecurringKey, e => e.Msn,
                    options.AllowMissingRecurringMsn, logger);
        }
        catch (JsonException ex)
        {
            // A signed body that is not an event gets 400, as the apps answered before they moved here.
            Log.Unreadable(logger, ex, slot.Name);
            return TypedResults.BadRequest();
        }
    }

    /// <summary>Reads the body. Returns null when it is longer than <paramref name="maxBytes"/>.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int maxBytes, CancellationToken cancellationToken)
    {
        if (request.ContentLength > maxBytes) return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Checks the signature against the live secret, then the test secret. Returns the environment of the
    /// registration whose secret signed it.
    /// </summary>
    private static async Task<(VippsWebhookSignatureResult Result, bool IsTest)> VerifyAsync(HttpContext http, VippsWebhookSlot slot, VippsWebhookRequest signed, DateTimeOffset now, ILogger logger)
    {
        var services = http.RequestServices;
        var secrets = await services.GetRequiredService<VippsWebhookSecretCache>().GetAsync(slot.Name,
            services.GetRequiredService<IVippsWebhookRegistrationStore>(), services.GetRequiredService<IVippsWebhookSecretProtector>(), http.RequestAborted);

        var result = VippsWebhookSignatureResult.MissingSecret;
        foreach (var (isTest, secret) in new[] { (false, secrets.Live), (true, secrets.Test) })
        {
            if (secret is null) continue;
            var verification = VippsWebhookSignature.Verify(signed, secret, now);
            result = verification.Result;
            if (verification.IsValid)
            {
                if (verification.Age > FreshSignatureAge)
                    Log.OldSignature(logger, slot.Name, verification.Age.TotalMinutes);
                return (result, isTest);
            }
        }
        return (result, false);
    }

    /// <summary>The key of an ePayment event: its operation's reference, or the payment reference and event name without one.</summary>
    internal static string PaymentKey(VippsPaymentEvent e) =>
        !string.IsNullOrEmpty(e.PspReference) ? e.PspReference : $"{e.Reference}:{e.Name}";

    /// <summary>
    /// The key of a Recurring event. Vipps documents no event id for Recurring events, so without one the
    /// key is built from the agreement, the event type, the charge, and the time it happened.
    /// </summary>
    // Keep this format as it is. Apps that moved here keep their processed keys, so a changed key, for
    // example one with the charge's transactionId, would let a redelivery of an old event through.
    // The time is in UTC ticks, so the key does not depend on the server's time zone. On a server that
    // runs in UTC, as the containers do, that equals the ticks garge-api used before.
    internal static string RecurringKey(VippsRecurringEvent e) =>
        !string.IsNullOrEmpty(e.EventId) ? e.EventId : $"{e.AgreementId}:{e.EventType}:{e.ChargeId}:{UtcTicks(e.Occurred)}";

    /// <summary>The UTC ticks of a time.</summary>
    internal static long? UtcTicks(DateTimeOffset? time) => time?.UtcTicks;

    /// <summary>
    /// The key an event is marked processed under. Test and live keep separate keys, so an event from one
    /// environment cannot mark an event of the other as processed.
    /// </summary>
    internal static string EventKey(string key, bool isTest) => isTest ? $"test:{key}" : key;

    private static async Task<IResult> DeliverAsync<TEvent>(HttpContext http, VippsWebhookSlot slot, bool isTest, TEvent? payload,
        Func<TEvent, string> key, Func<TEvent, string?> msn, bool allowMissingMsn, ILogger logger)
        where TEvent : class
    {
        if (payload is null) return TypedResults.BadRequest();

        var services = http.RequestServices;
        var cancellationToken = http.RequestAborted;
        var db = services.GetRequiredService<TContext>();
        var eventKey = EventKey(key(payload), isTest);

        if (await db.ProcessedWebhookEvents.AsNoTracking().AnyAsync(e => e.Id == eventKey, cancellationToken))
        {
            Log.Duplicate(logger, slot.Name, eventKey);
            return TypedResults.Ok();
        }

        var expectedMsn = services.GetRequiredService<IOptions<VippsOptions>>().Value.For(isTest).MerchantSerialNumber;
        var payloadMsn = msn(payload);
        var msnMatches = string.IsNullOrEmpty(payloadMsn)
            ? allowMissingMsn
            : !string.IsNullOrEmpty(expectedMsn) && string.Equals(payloadMsn, expectedMsn, StringComparison.Ordinal);
        if (!msnMatches)
        {
            Log.MsnMismatch(logger, slot.Name, eventKey, payloadMsn ?? "-", expectedMsn);
            return await RejectAsync(db, slot, eventKey, services, logger, cancellationToken);
        }

        VippsWebhookOutcome outcome;
        try
        {
            outcome = payload switch
            {
                VippsPaymentEvent e => await services.GetRequiredService<IVippsPaymentWebhookHandler>().HandleAsync(
                    new VippsWebhookDelivery<VippsPaymentEvent> { Event = e, IsTest = isTest, EventKey = eventKey, Slot = slot.Name }, cancellationToken),
                VippsRecurringEvent e => await services.GetRequiredService<IVippsRecurringWebhookHandler>().HandleAsync(
                    new VippsWebhookDelivery<VippsRecurringEvent> { Event = e, IsTest = isTest, EventKey = eventKey, Slot = slot.Name }, cancellationToken),
                _ => throw new InvalidOperationException("Unknown Vipps webhook event type."),
            };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.HandlerFailed(logger, ex, slot.Name, eventKey);
            return TypedResults.StatusCode(StatusCodes.Status500InternalServerError);
        }

        switch (outcome.Kind)
        {
            case VippsWebhookOutcomeKind.Retry:
                Log.Retry(logger, slot.Name, eventKey);
                return TypedResults.StatusCode(StatusCodes.Status500InternalServerError);
            case VippsWebhookOutcomeKind.Rejected:
                Log.Rejected(logger, slot.Name, eventKey);
                return await RejectAsync(db, slot, eventKey, services, logger, cancellationToken);
        }

        bool saved;
        try
        {
            saved = await SaveProcessedAsync(db, slot, eventKey, services, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Neither the changes nor the marker are stored, so the redelivery is handled again.
            Log.SaveFailed(logger, ex, slot.Name, eventKey);
            return TypedResults.StatusCode(StatusCodes.Status500InternalServerError);
        }
        if (!saved)
        {
            Log.Duplicate(logger, slot.Name, eventKey);
            return TypedResults.Ok();
        }

        if (outcome.AfterCommit is { } afterCommit)
        {
            try
            {
                await afterCommit(cancellationToken);
            }
            catch (Exception ex)
            {
                Log.AfterCommitFailed(logger, ex, slot.Name, eventKey);
            }
        }
        return TypedResults.Ok();
    }

    /// <summary>
    /// Drops whatever the handler changed and stores only the marker, so the event is not handled again. It
    /// answers 200, because Vipps retries any 4xx or 5xx for seven days and holds back the payment's later
    /// events until one succeeds.
    /// </summary>
    private static async Task<IResult> RejectAsync(TContext db, VippsWebhookSlot slot, string eventKey, IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        try
        {
            await SaveProcessedAsync(db, slot, eventKey, services, cancellationToken);
            return TypedResults.Ok();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.SaveFailed(logger, ex, slot.Name, eventKey);
            return TypedResults.StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Saves the event's changes together with its processed marker, in one SaveChanges, so either both are
    /// stored or neither is. Returns false when a concurrent delivery of the same event saved first, in which
    /// case nothing from this delivery is stored.
    /// </summary>
    internal static async Task<bool> SaveProcessedAsync(TContext db, VippsWebhookSlot slot, string eventKey, IServiceProvider services, CancellationToken cancellationToken)
    {
        var now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        db.ProcessedWebhookEvents.Add(new VippsProcessedWebhookEvent { Id = eventKey, Source = slot.Name, ProcessedAt = now });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        // A relational database reports the duplicate marker as a DbUpdateException, the in-memory provider
        // as an ArgumentException. Either way it only counts as a lost race once the other marker is found.
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or ArgumentException)
        {
            var raceLost = await db.ProcessedWebhookEvents.AsNoTracking().AnyAsync(e => e.Id == eventKey, cancellationToken);
            if (!raceLost) throw;
            db.ChangeTracker.Clear();
            return false;
        }
    }

    private static ReadOnlySpan<byte> WithoutBom(byte[] body) =>
        body.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? body.AsSpan(3) : body;

    private static partial class Log
    {
        // Anyone can call the endpoint, so a refused signature is routine and not a warning.
        [LoggerMessage(Level = LogLevel.Information, Message = "Vipps {Slot} webhook verify failed: {Reason}")]
        public static partial void VerifyFailed(ILogger logger, string slot, VippsWebhookSignatureResult reason);

        [LoggerMessage(Level = LogLevel.Information, Message = "Vipps {Slot} webhook refused: the body exceeds {MaxBytes} bytes")]
        public static partial void TooLarge(ILogger logger, string slot, int maxBytes);

        [LoggerMessage(Level = LogLevel.Error, Message = "Vipps {Slot} webhook: a signed body is not an event")]
        public static partial void Unreadable(ILogger logger, Exception exception, string slot);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Vipps {Slot} webhook signed {AgeMinutes:F0} minutes ago accepted, as a retry may keep its first signature")]
        public static partial void OldSignature(ILogger logger, string slot, double ageMinutes);

        [LoggerMessage(Level = LogLevel.Information, Message = "Vipps {Slot} webhook duplicate {EventKey} skipped")]
        public static partial void Duplicate(ILogger logger, string slot, string eventKey);

        [LoggerMessage(Level = LogLevel.Error, Message = "Vipps {Slot} webhook {EventKey} rejected: MSN mismatch (got {Got}, expected {Expected})")]
        public static partial void MsnMismatch(ILogger logger, string slot, string eventKey, string got, string expected);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Vipps {Slot} webhook {EventKey} rejected by the handler")]
        public static partial void Rejected(ILogger logger, string slot, string eventKey);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Vipps {Slot} webhook {EventKey} left for a retry by the handler")]
        public static partial void Retry(ILogger logger, string slot, string eventKey);

        [LoggerMessage(Level = LogLevel.Error, Message = "Vipps {Slot} webhook {EventKey} handler failed")]
        public static partial void HandlerFailed(ILogger logger, Exception exception, string slot, string eventKey);

        [LoggerMessage(Level = LogLevel.Error, Message = "Vipps {Slot} webhook {EventKey} could not be saved")]
        public static partial void SaveFailed(ILogger logger, Exception exception, string slot, string eventKey);

        [LoggerMessage(Level = LogLevel.Error, Message = "Vipps {Slot} webhook {EventKey} side effects after saving failed")]
        public static partial void AfterCommitFailed(ILogger logger, Exception exception, string slot, string eventKey);
    }
}
