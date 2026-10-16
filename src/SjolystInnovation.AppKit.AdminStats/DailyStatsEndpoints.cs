using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SjolystInnovation.AppKit.AdminStats;

/// <summary>The options for <see cref="DailyStatsEndpointExtensions.MapAppKitDailyStats{TContext, TSnapshot}"/>.</summary>
public sealed class AppKitDailyStatsEndpointOptions
{
    /// <summary>The authorization policy that the history requires. Defaults to <c>Admin</c>.</summary>
    public string Policy { get; set; } = "Admin";
}

/// <summary>Maps the daily statistics endpoint.</summary>
public static class DailyStatsEndpointExtensions
{
    /// <summary>
    /// Maps <c>GET history</c> under <paramref name="prefix"/>, behind
    /// <see cref="AppKitDailyStatsEndpointOptions.Policy"/>. It answers the stored days and a live row for
    /// today as <c>[{ "date": "yyyy-MM-dd", ...counters }]</c>, oldest first. The counters are the snapshot's
    /// public <c>int</c> and <c>long</c> properties with a public getter and setter, apart from <c>Id</c>, named
    /// after the property in camel case. Other types, such as <c>int?</c>, <c>short</c>, <c>decimal</c>, and
    /// <c>bool</c>, and computed properties are left out. The optional
    /// <c>from</c> query parameter, a <c>yyyy-MM-dd</c> date, sets the first day. The endpoint does not write.
    /// </summary>
    /// <typeparam name="TContext">Your app's <c>DbContext</c>.</typeparam>
    /// <typeparam name="TSnapshot">Your app's snapshot entity, registered with <see cref="DailyStatsServiceCollectionExtensions.AddAppKitDailyStats{TContext, TSnapshot}"/>.</typeparam>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="prefix">The route prefix. Defaults to <c>/api/admin/stats</c>.</param>
    /// <param name="configure">A callback that sets the endpoint options.</param>
    /// <returns>The route group, for further conventions.</returns>
    /// <exception cref="ArgumentException">When the policy is null or blank.</exception>
    public static RouteGroupBuilder MapAppKitDailyStats<TContext, TSnapshot>(this IEndpointRouteBuilder endpoints,
        string prefix = "/api/admin/stats", Action<AppKitDailyStatsEndpointOptions>? configure = null)
        where TContext : DbContext
        where TSnapshot : class, IAppKitDailyStat
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = new AppKitDailyStatsEndpointOptions();
        configure?.Invoke(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Policy, nameof(configure));

        var group = endpoints.MapGroup(prefix);
        group.MapGet("history", async (HttpContext http, DateOnly? from) =>
        {
            var services = http.RequestServices;
            var rows = await services.GetRequiredService<AppKitDailyStats<TContext, TSnapshot>>()
                .GetHistoryAsync(services.GetRequiredService<TContext>(), from, http.RequestAborted);
            return TypedResults.Ok(DailyStatColumns<TSnapshot>.ToJson(rows));
        }).RequireAuthorization(options.Policy);
        return group;
    }
}

/// <summary>The counters of a snapshot type: its public read and write <c>int</c> and <c>long</c> properties, apart from <c>Id</c>.</summary>
internal static class DailyStatColumns<TSnapshot>
    where TSnapshot : IAppKitDailyStat
{
    private static readonly (string Name, PropertyInfo Property)[] Counters = typeof(TSnapshot)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetGetMethod() is not null && p.GetSetMethod() is not null && p.GetIndexParameters().Length == 0
            && (p.PropertyType == typeof(int) || p.PropertyType == typeof(long))
            && !string.Equals(p.Name, "Id", StringComparison.OrdinalIgnoreCase))
        .Select(p => (JsonNamingPolicy.CamelCase.ConvertName(p.Name), p))
        .ToArray();

    internal static bool IsZero(TSnapshot row) => Counters.All(c => Convert.ToInt64(c.Property.GetValue(row), CultureInfo.InvariantCulture) == 0);

    internal static JsonArray ToJson(IEnumerable<TSnapshot> rows)
    {
        var array = new JsonArray();
        foreach (var row in rows)
        {
            var item = new JsonObject { ["date"] = row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
            foreach (var (name, property) in Counters)
            {
                item[name] = property.GetValue(row) switch
                {
                    int i => JsonValue.Create(i),
                    long l => JsonValue.Create(l),
                    _ => null,
                };
            }
            array.Add(item);
        }
        return array;
    }
}
