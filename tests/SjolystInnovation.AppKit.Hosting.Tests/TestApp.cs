using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SjolystInnovation.AppKit.Hosting.Tests;

/// <summary>A test server whose requests carry a chosen remote address, answering with what the app saw.</summary>
internal sealed class TestApp : IAsyncDisposable
{
    private readonly IHost _host;

    private TestApp(IHost host) => _host = host;

    public IServiceProvider Services => _host.Services;

    public static async Task<TestApp> StartAsync(
        Action<IApplicationBuilder> pipeline,
        Action<IServiceCollection>? services = null,
        Dictionary<string, string?>? config = null,
        string environment = "Production",
        Action<IEndpointRouteBuilder>? endpoints = null)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .UseEnvironment(environment)
                .ConfigureAppConfiguration(c => c.AddInMemoryCollection(config ?? []))
                .ConfigureServices(s =>
                {
                    s.AddRouting();
                    services?.Invoke(s);
                })
                .Configure(app =>
                {
                    pipeline(app);
                    app.UseRouting();
                    app.UseEndpoints(e => endpoints?.Invoke(e));
                    app.Run(c => c.Response.WriteAsync($"{c.Connection.RemoteIpAddress}|{c.Request.Scheme}"));
                }))
            .Build();
        await host.StartAsync();
        return new TestApp(host);
    }

    public Task<HttpContext> SendAsync(string path, string remoteIp = "203.0.113.9", string method = "GET",
        Dictionary<string, string>? headers = null, string scheme = "http", string host = "api.example.com") =>
        _host.GetTestServer().SendAsync(c =>
        {
            c.Request.Method = method;
            c.Request.Scheme = scheme;
            c.Request.Host = new HostString(host);
            c.Request.Path = path;
            c.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
            foreach (var (name, value) in headers ?? [])
                c.Request.Headers[name] = value;
        });

    public static async Task<string> BodyAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}
