using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SjolystInnovation.AppKit.Consent;

/// <summary>A consent choice as the browser sends it.</summary>
/// <param name="ConsentId">The browser's consent id, a GUID like <c>3f2a6c1e-0b7d-4e8a-9c55-1d2e3f4a5b6c</c>. Required, and not all zeros.</param>
/// <param name="Marketing">Whether the visitor allowed marketing cookies. Required.</param>
/// <param name="NoticeVersion">The version of the notice the visitor answered. Required, at most 64 of the characters <c>A-Z a-z 0-9 . _ -</c>.</param>
/// <param name="SchemaVersion">The version of the browser's stored consent record. Required, at least 1.</param>
/// <param name="Source">Where the choice was made, <c>banner</c> or <c>settings</c>. Required.</param>
public sealed record ConsentBody(string? ConsentId, bool? Marketing, string? NoticeVersion, int? SchemaVersion, string? Source);

/// <summary>A logged consent choice as the history endpoint returns it.</summary>
/// <param name="Id">The row id.</param>
/// <param name="ConsentId">The browser's consent id.</param>
/// <param name="Marketing">Whether the visitor allowed marketing cookies.</param>
/// <param name="NoticeVersion">The version of the notice the visitor answered.</param>
/// <param name="SchemaVersion">The version of the browser's stored consent record.</param>
/// <param name="DecidedAt">When the server received the choice, in UTC.</param>
/// <param name="UserId">The id of the signed-in user who made the choice, or null.</param>
/// <param name="Source">Where the choice was made.</param>
public sealed record ConsentRecordInfo(long Id, Guid ConsentId, bool Marketing, string NoticeVersion, int SchemaVersion, DateTime DecidedAt, string? UserId, string Source);

/// <summary>
/// The stable codes for the field errors of <c>POST consent</c>. The validation problem holds them in its
/// <c>codes</c> extension, keyed by field like <c>errors</c>, for example
/// <c>{"codes": {"Source": ["invalidSource"]}}</c>. A client shows its own text for a code it knows and the
/// English message from <c>errors</c> for one it does not.
/// </summary>
public static class ConsentErrorCodes
{
    /// <summary>The field is missing or blank and must have a value.</summary>
    public const string Required = "required";

    /// <summary>The text is longer than its limit. The <c>limits</c> extension holds the limit for the field.</summary>
    public const string TooLong = "tooLong";

    /// <summary>The consent id is not a GUID in the form <c>xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</c>, or it is all zeros.</summary>
    public const string InvalidConsentId = "invalidConsentId";

    /// <summary>The notice version has a character other than <c>A-Z a-z 0-9 . _ -</c>.</summary>
    public const string InvalidNoticeVersion = "invalidNoticeVersion";

    /// <summary>The schema version is less than 1.</summary>
    public const string InvalidSchemaVersion = "invalidSchemaVersion";

    /// <summary>The source is not one of <see cref="ConsentSources.All"/>.</summary>
    public const string InvalidSource = "invalidSource";
}

/// <summary>Registers, maps, and configures the consent log.</summary>
public static class ConsentExtensions
{
    /// <summary>
    /// Registers <see cref="AppKitConsentOptions"/> and a hosted service that deletes rows older than
    /// <see cref="AppKitConsentOptions.KeepFor"/> when the app starts and once a day after that. The app
    /// fails to start when <see cref="AppKitConsentOptions.KeepFor"/> is not positive.
    /// </summary>
    public static IServiceCollection AddAppKitConsent<TContext>(this IServiceCollection services, Action<AppKitConsentOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.AddOptions<AppKitConsentOptions>();
        if (configure is not null)
            options.Configure(configure);
        options.Validate(o => o.KeepFor > TimeSpan.Zero, "The consent KeepFor must be positive.").ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddHostedService<ConsentRetentionService<TContext>>();
        return services;
    }

    /// <summary>
    /// Maps <see cref="ConsentRecord"/> to the <c>ConsentRecords</c> table, with an index on
    /// <see cref="ConsentRecord.ConsentId"/> for the history and one on <see cref="ConsentRecord.DecidedAt"/>
    /// for the daily delete. The table has no column for an IP address or a user agent.
    /// </summary>
    public static ModelBuilder AddAppKitConsent(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<ConsentRecord>(e =>
        {
            e.ToTable("ConsentRecords");
            e.HasKey(r => r.Id);
            e.Property(r => r.NoticeVersion).IsRequired().HasMaxLength(ConsentLimits.NoticeVersion);
            e.Property(r => r.Source).IsRequired().HasMaxLength(ConsentLimits.Source);
            e.Property(r => r.UserId).HasMaxLength(450);
            e.HasIndex(r => r.ConsentId);
            e.HasIndex(r => r.DecidedAt);
        });
        return modelBuilder;
    }

    /// <summary>
    /// Maps the consent endpoints under <paramref name="prefix"/>. <c>POST consent</c> is open to everyone.
    /// It checks the <see cref="ConsentBody"/>, adds one <see cref="ConsentRecord"/> with the server's time
    /// and, for a signed-in visitor, the user id, and answers 204. A refused body answers 400 with the
    /// field errors and their <see cref="ConsentErrorCodes"/>. It never stores the IP address or the user
    /// agent. It applies <see cref="AppKitConsentEndpointOptions.RateLimitPolicy"/> when one is set.
    /// <c>GET consent/{consentId}</c> lists the choices of one consent id, newest first, behind
    /// <see cref="AppKitConsentEndpointOptions.Policy"/>.
    /// </summary>
    public static RouteGroupBuilder MapAppKitConsent<TContext>(this IEndpointRouteBuilder endpoints,
        string prefix = "/api", Action<AppKitConsentEndpointOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = new AppKitConsentEndpointOptions();
        configure?.Invoke(options);

        var group = endpoints.MapGroup(prefix);
        var post = group.MapPost("consent", (Func<ConsentBody, HttpContext, Task<IResult>>)ConsentHandlers<TContext>.PostAsync).AllowAnonymous();
        if (options.RateLimitPolicy is { } policy)
            post.RequireRateLimiting(policy);
        group.MapGet("consent/{consentId:guid}", (Func<Guid, HttpContext, Task<IResult>>)ConsentHandlers<TContext>.HistoryAsync)
            .RequireAuthorization(options.Policy);
        return group;
    }
}

/// <summary>The column lengths, shared by the model and the checks.</summary>
internal static class ConsentLimits
{
    public const int NoticeVersion = 64;
    public const int Source = 32;
}

internal static partial class ConsentHandlers<TContext>
    where TContext : DbContext
{
    public static async Task<IResult> PostAsync(ConsentBody body, HttpContext http)
    {
        var problem = Validate(body, out var consentId);
        if (problem.Errors.Count > 0)
            return problem.ToResult();

        var db = http.RequestServices.GetRequiredService<TContext>();
        var now = (http.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        db.Set<ConsentRecord>().Add(new ConsentRecord
        {
            ConsentId = consentId,
            Marketing = body.Marketing!.Value,
            NoticeVersion = body.NoticeVersion!,
            SchemaVersion = body.SchemaVersion!.Value,
            DecidedAt = now,
            UserId = UserId(http.User),
            Source = body.Source!,
        });
        await db.SaveChangesAsync(http.RequestAborted);
        return TypedResults.NoContent();
    }

    public static async Task<IResult> HistoryAsync(Guid consentId, HttpContext http)
    {
        var list = await http.RequestServices.GetRequiredService<TContext>().Set<ConsentRecord>().AsNoTracking()
            .Where(r => r.ConsentId == consentId)
            .OrderByDescending(r => r.DecidedAt).ThenByDescending(r => r.Id)
            .Select(r => new ConsentRecordInfo(r.Id, r.ConsentId, r.Marketing, r.NoticeVersion, r.SchemaVersion, r.DecidedAt, r.UserId, r.Source))
            .ToListAsync(http.RequestAborted);
        return TypedResults.Ok(list);
    }

    internal static ConsentProblem Validate(ConsentBody body, out Guid consentId)
    {
        var problem = new ConsentProblem();
        consentId = Guid.Empty;

        if (string.IsNullOrWhiteSpace(body.ConsentId))
            problem.Set("ConsentId", ConsentErrorCodes.Required, "Required.");
        else if (!Guid.TryParseExact(body.ConsentId, "D", out consentId) || consentId == Guid.Empty)
            problem.Set("ConsentId", ConsentErrorCodes.InvalidConsentId, "Not a valid consent id.");

        if (body.Marketing is null)
            problem.Set("Marketing", ConsentErrorCodes.Required, "Required.");

        if (string.IsNullOrEmpty(body.NoticeVersion))
            problem.Set("NoticeVersion", ConsentErrorCodes.Required, "Required.");
        else if (body.NoticeVersion.Length > ConsentLimits.NoticeVersion)
            problem.Set("NoticeVersion", ConsentErrorCodes.TooLong, $"At most {ConsentLimits.NoticeVersion} characters.", ConsentLimits.NoticeVersion);
        else if (!NoticeVersionPattern().IsMatch(body.NoticeVersion))
            problem.Set("NoticeVersion", ConsentErrorCodes.InvalidNoticeVersion, "Only letters, digits, '.', '_' and '-'.");

        if (body.SchemaVersion is null)
            problem.Set("SchemaVersion", ConsentErrorCodes.Required, "Required.");
        else if (body.SchemaVersion < 1)
            problem.Set("SchemaVersion", ConsentErrorCodes.InvalidSchemaVersion, "At least 1.");

        if (string.IsNullOrEmpty(body.Source))
            problem.Set("Source", ConsentErrorCodes.Required, "Required.");
        else if (!ConsentSources.All.Contains(body.Source, StringComparer.Ordinal))
            problem.Set("Source", ConsentErrorCodes.InvalidSource, $"One of {string.Join(", ", ConsentSources.All)}.");

        return problem;
    }

    // The user id claim that AppKit.Auth writes, read without depending on that package. A visitor who is
    // not signed in has none.
    private static string? UserId(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
            ? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub")
            : null;

    [GeneratedRegex(@"^[A-Za-z0-9._-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex NoticeVersionPattern();
}

/// <summary>The field errors of one request, with a code for each and the limit for each <c>tooLong</c>.</summary>
internal sealed record ConsentProblem(
    Dictionary<string, string[]> Errors,
    Dictionary<string, string[]> Codes,
    Dictionary<string, int> Limits)
{
    public ConsentProblem() : this([], [], [])
    {
    }

    /// <summary>Gives the field its one error, with the limit when the code is <c>tooLong</c>.</summary>
    public void Set(string field, string code, string message, int? limit = null)
    {
        Errors[field] = [message];
        Codes[field] = [code];
        if (limit is { } max)
            Limits[field] = max;
    }

    /// <summary>A validation problem with the English messages in <c>errors</c> and the <c>codes</c> and <c>limits</c> extensions.</summary>
    public ValidationProblem ToResult() =>
        TypedResults.ValidationProblem(Errors, extensions: new Dictionary<string, object?>
        {
            ["codes"] = Codes,
            ["limits"] = Limits,
        });
}
