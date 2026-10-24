using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SjolystInnovation.AppKit.Consent.Tests;

/// <summary>
/// The body size limit is applied by the server, which the test server does not do, so these tests run on
/// Kestrel on a free local port.
/// </summary>
public sealed class ConsentKestrelTests : IAsyncLifetime
{
    private const string Id = "3f2a6c1e-0b7d-4e8a-9c55-1d2e3f4a5b6c";
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private WebApplication _app = default!;
    private HttpClient _client = default!;

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContext<TestDbContext>(o => o.UseSqlite(_connection));
        builder.Services.AddAuthorization();
        _app = builder.Build();
        _app.MapAppKitConsent<TestDbContext>();
        using (var scope = _app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<TestDbContext>().Database.EnsureCreatedAsync();
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static string Json(int padding) =>
        $$"""{"consentId":"{{Id}}","marketing":true,"noticeVersion":"v1","schemaVersion":1,"source":"banner","pad":"{{new string('x', padding)}}"}""";

    private Task<HttpResponseMessage> PostAsync(string json, bool chunked = false)
    {
        HttpContent content = new StringContent(json, Encoding.UTF8, "application/json");
        if (chunked)
            content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(json))) { Headers = { { "Content-Type", "application/json" } } };
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/consent") { Content = content };
        if (chunked)
            request.Headers.TransferEncodingChunked = true;
        return _client.SendAsync(request);
    }

    private async Task<int> RowsAsync()
    {
        using var scope = _app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TestDbContext>().ConsentRecords.CountAsync();
    }

    [Fact]
    public async Task A_body_of_1_KB_is_accepted_and_one_byte_more_is_refused()
    {
        var empty = Json(0).Length;

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(Json(ConsentExtensions.MaxBodyBytes - empty))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await PostAsync(Json(ConsentExtensions.MaxBodyBytes - empty + 1))).StatusCode);
        Assert.Equal(1, await RowsAsync());
    }

    [Fact]
    public async Task A_chunked_body_over_1_KB_is_refused()
    {
        var response = await PostAsync(Json(4096), chunked: true);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, await RowsAsync());
    }

    [Fact]
    public void Only_the_post_carries_the_size_limit()
    {
        var endpoints = _app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        Assert.Equal(1024, endpoints.Single(e => e.RoutePattern.RawText == "/api/consent").Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize);
        Assert.Null(endpoints.Single(e => e.RoutePattern.RawText != "/api/consent").Metadata.GetMetadata<IRequestSizeLimitMetadata>());
    }
}
