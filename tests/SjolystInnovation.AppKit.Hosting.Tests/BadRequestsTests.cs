using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace SjolystInnovation.AppKit.Hosting.Tests;

public sealed record NameBody(string Name);

/// <summary>A controller next to the minimal API endpoints, to show MVC keeps its own answers.</summary>
[ApiController]
[Route("mvc")]
public sealed class NameController : ControllerBase
{
    [HttpPut]
    public ActionResult<string> Put(NameBody body) => Ok(body.Name);
}

/// <summary>Answers every exception with 500, as the apps' own exception handlers do for anything they do not know.</summary>
internal sealed class EverythingIs500 : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return ValueTask.FromResult(false);
    }
}

public sealed class BadRequestsTests
{
    private static readonly byte[] InvalidUtf8InString = [.. "{\"name\":\""u8.ToArray(), 0xFF, 0xFE, .. "\"}"u8.ToArray()];
    private static readonly byte[] InvalidUtf8InName = [.. "{\"name\":\"x\",\""u8.ToArray(), 0xC3, 0x28, .. "\":1}"u8.ToArray()];
    private static readonly byte[] Malformed = "{\"name\":"u8.ToArray();
    private const string MvcTitle = "Not a type this controller reads";

    private sealed class Calls
    {
        public int Count;
    }

    private static Task<TestApp> StartAsync(string environment = "Production", bool problemDetails = true, Calls? calls = null,
        Action<IEndpointRouteBuilder>? endpoints = null, FakeLogCollector? logs = null) =>
        TestApp.StartAsync(
            a =>
            {
                // UseExceptionHandler needs AddProblemDetails when it has no handler path.
                if (problemDetails)
                    a.UseExceptionHandler();
            },
            s =>
            {
                if (problemDetails)
                    s.AddProblemDetails();
                s.AddExceptionHandler<EverythingIs500>();
                s.AddControllers().AddApplicationPart(typeof(NameController).Assembly)
                    .ConfigureApiBehaviorOptions(o => o.ClientErrorMapping[StatusCodes.Status415UnsupportedMediaType].Title = MvcTitle);
                if (logs is not null)
                    s.AddLogging(b => b.AddProvider(new FakeLoggerProvider(logs)));
            },
            environment: environment,
            endpoints: e =>
            {
                e.MapPut("/record", (NameBody body) =>
                {
                    if (calls is not null)
                        calls.Count++;
                    return Results.Text(body.Name);
                });
                e.MapPut("/element", (JsonElement body) =>
                {
                    if (calls is not null)
                        calls.Count++;
                    return Results.Text(body.GetProperty("name").GetString());
                });
                e.MapPost("/raw", async (HttpRequest request) =>
                {
                    using var copy = new MemoryStream();
                    await request.Body.CopyToAsync(copy);
                    return Results.Text(Convert.ToHexString(copy.ToArray()));
                });
                e.MapControllers();
                endpoints?.Invoke(e);
            },
            afterRouting: a => a.UseAppKitBadRequests());

    private static ByteArrayContent Body(byte[] bytes, string contentType = "application/json")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return content;
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("title").GetString()));
        Assert.False(problem.TryGetProperty("detail", out _));
    }

    public static TheoryData<string, string> EnvironmentsAndEndpoints()
    {
        var data = new TheoryData<string, string>();
        foreach (var environment in new[] { "Production", "Development" })
            foreach (var path in new[] { "/record", "/element" })
                data.Add(environment, path);
        return data;
    }

    [Theory]
    [MemberData(nameof(EnvironmentsAndEndpoints))]
    public async Task Invalid_utf8_in_a_json_body_gets_400_before_the_endpoint_runs(string environment, string path)
    {
        var calls = new Calls();
        await using var app = await StartAsync(environment, calls: calls);

        foreach (var bytes in new[] { InvalidUtf8InString, InvalidUtf8InName })
            await AssertProblemAsync(await app.CreateClient().PutAsync(path, Body(bytes)), HttpStatusCode.BadRequest);

        Assert.Equal(0, calls.Count);
    }

    [Theory]
    [MemberData(nameof(EnvironmentsAndEndpoints))]
    public async Task Malformed_json_gets_a_problem_in_every_environment(string environment, string path)
    {
        var calls = new Calls();
        await using var app = await StartAsync(environment, calls: calls);

        var response = await app.CreateClient().PutAsync(path, Body(Malformed));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(0, calls.Count);
    }

    [Theory]
    [MemberData(nameof(EnvironmentsAndEndpoints))]
    public async Task A_missing_required_body_gets_a_problem_in_every_environment(string environment, string path)
    {
        var calls = new Calls();
        await using var app = await StartAsync(environment, calls: calls);

        var response = await app.CreateClient().PutAsync(path, Body([]));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(0, calls.Count);
    }

    [Fact]
    public async Task An_endpoint_in_a_group_gets_a_problem_for_malformed_json()
    {
        await using var app = await StartAsync(endpoints: e => e.MapGroup("/api").MapPut("/record", (NameBody body) => Results.Text(body.Name)));

        var response = await app.CreateClient().PutAsync("/api/record", Body(Malformed));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task A_query_value_that_cannot_be_parsed_gets_a_problem(string environment)
    {
        await using var app = await StartAsync(environment, endpoints: e => e.MapGet("/days", (int days) => Results.Text($"{days}")));

        await AssertProblemAsync(await app.CreateClient().GetAsync("/days?days=many"), HttpStatusCode.BadRequest);
        Assert.Equal("7", await app.CreateClient().GetStringAsync("/days?days=7"));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task A_400_the_endpoint_answers_itself_is_left_alone(string environment)
    {
        await using var app = await StartAsync(environment, endpoints: e =>
        {
            e.MapPut("/own", (NameBody body) => Results.BadRequest(new { message = $"No {body.Name}." }));
            e.MapPut("/own-empty", (NameBody body) => Results.BadRequest());
        });

        var own = await app.CreateClient().PutAsync("/own", Body("{\"name\":\"x\"}"u8.ToArray()));
        var empty = await app.CreateClient().PutAsync("/own-empty", Body("{\"name\":\"x\"}"u8.ToArray()));

        Assert.Equal(HttpStatusCode.BadRequest, own.StatusCode);
        Assert.Equal("application/json", own.Content.Headers.ContentType?.MediaType);
        Assert.Equal("{\"message\":\"No x.\"}", await own.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Null(empty.Content.Headers.ContentType);
        Assert.Equal("", await empty.Content.ReadAsStringAsync());
    }

    public static TheoryData<string, int> EnvironmentsAndPaddings()
    {
        var data = new TheoryData<string, int>();
        foreach (var environment in new[] { "Production", "Development" })
            foreach (var padding in new[] { 0, BadRequestsExtensions.ChunkSize - 11, BadRequestsExtensions.ChunkSize - 10, BadRequestsExtensions.ChunkSize - 9, 3 * BadRequestsExtensions.ChunkSize })
                data.Add(environment, padding);
        return data;
    }

    [Theory]
    [MemberData(nameof(EnvironmentsAndPaddings))]
    public async Task A_valid_body_reaches_the_endpoint_from_the_start(string environment, int padding)
    {
        // The padding moves a three byte character across the end of the first chunk.
        var name = new string('a', padding) + "€ Mårvegen";
        var calls = new Calls();
        await using var app = await StartAsync(environment, calls: calls);

        foreach (var path in new[] { "/record", "/element" })
        {
            var response = await app.CreateClient().PutAsync(path, Body(JsonSerializer.SerializeToUtf8Bytes(new { name })));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(name, await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task A_character_cut_off_at_the_end_of_the_body_is_refused()
    {
        await using var app = await StartAsync();

        var response = await app.CreateClient().PostAsync("/raw", Body([.. "{\"name\":\"x\"}"u8.ToArray(), 0xE2, 0x82]));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("application/merge-patch+json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("Application/JSON; charset=UTF-8")]
    public async Task Every_json_type_in_utf8_is_checked(string contentType)
    {
        await using var app = await StartAsync();

        var response = await app.CreateClient().PostAsync("/raw", Body(InvalidUtf8InString, contentType));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/octet-stream")]
    [InlineData("application/json; charset=iso-8859-1")]
    public async Task Other_types_and_charsets_reach_the_endpoint_unread(string contentType)
    {
        await using var app = await StartAsync();

        var response = await app.CreateClient().PostAsync("/raw", Body(InvalidUtf8InString, contentType));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Convert.ToHexString(InvalidUtf8InString), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_request_without_an_endpoint_is_not_read()
    {
        await using var app = await StartAsync();

        var response = await app.CreateClient().PostAsync("/unmapped", Body(InvalidUtf8InString));

        // The test app answers unmatched requests itself.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status413PayloadTooLarge)]
    public async Task A_bad_request_exception_keeps_its_status_code(int status)
    {
        await using var app = await StartAsync(endpoints: e => e.MapGet("/throws", (HttpContext http) =>
        {
            http.Response.Headers["X-Partial"] = "yes";
            throw new BadHttpRequestException("Unreadable.", status);
        }));

        var response = await app.CreateClient().GetAsync("/throws");

        await AssertProblemAsync(response, (HttpStatusCode)status);
        Assert.False(response.Headers.Contains("X-Partial"));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task Each_refusal_is_logged_with_its_reason(string environment)
    {
        var logs = new FakeLogCollector();
        await using var app = await StartAsync(environment, logs: logs);

        await app.CreateClient().PutAsync("/record", Body(InvalidUtf8InString));
        await app.CreateClient().PutAsync("/record", Body(Malformed));

        var refusals = logs.GetSnapshot().Where(r => r.Category == BadRequestsExtensions.LogCategory).ToList();
        Assert.Equal(2, refusals.Count);
        Assert.All(refusals, r => Assert.Equal(LogLevel.Information, r.Level));
        Assert.Equal("Refused a JSON request body that is not valid UTF-8.", refusals[0].Message);
        Assert.StartsWith("Refused an unreadable request with 400: Failed to read parameter", refusals[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bad_request_exception_after_the_response_started_is_left_alone()
    {
        await using var app = await StartAsync(endpoints: e => e.MapGet("/streams", async (HttpContext http) =>
        {
            await http.Response.WriteAsync("partial");
            await http.Response.Body.FlushAsync();
            throw new BadHttpRequestException("Unreadable.");
        }));

        var thrown = await Record.ExceptionAsync(async () => await (await app.CreateClient().GetAsync("/streams")).Content.ReadAsStringAsync());

        var chain = new List<Exception>();
        for (var e = thrown; e is not null; e = e.InnerException)
            chain.Add(e);
        Assert.Contains(chain, e => e is BadHttpRequestException);
    }

    [Fact]
    public async Task Other_exceptions_still_reach_the_exception_handler()
    {
        await using var app = await StartAsync(endpoints: e => e.MapGet("/throws", IResult () => throw new InvalidOperationException("Broken.")));

        var response = await app.CreateClient().GetAsync("/throws");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    public static TheoryData<string, string> EnvironmentsAndOtherTypes()
    {
        var data = new TheoryData<string, string>();
        foreach (var environment in new[] { "Production", "Development" })
            foreach (var contentType in new[] { "text/plain", "application/x-www-form-urlencoded", "multipart/form-data; boundary=x" })
                data.Add(environment, contentType);
        return data;
    }

    [Theory]
    [MemberData(nameof(EnvironmentsAndOtherTypes))]
    public async Task A_content_type_routing_does_not_accept_gets_a_415_problem(string environment, string contentType)
    {
        var calls = new Calls();
        await using var app = await StartAsync(environment, calls: calls);

        foreach (var path in new[] { "/record", "/element" })
            await AssertProblemAsync(await app.CreateClient().PutAsync(path, Body("{\"name\":\"x\"}"u8.ToArray(), contentType)), HttpStatusCode.UnsupportedMediaType);

        Assert.Equal(0, calls.Count);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task A_json_body_to_a_form_endpoint_gets_a_415_problem(string environment)
    {
        await using var app = await StartAsync(environment, endpoints: e => e.MapPost("/form", ([FromForm] string name) => Results.Text(name)).DisableAntiforgery());

        var json = await app.CreateClient().PostAsync("/form", Body("{\"name\":\"x\"}"u8.ToArray()));
        var form = await app.CreateClient().PostAsync("/form", new FormUrlEncodedContent([new("name", "x")]));

        await AssertProblemAsync(json, HttpStatusCode.UnsupportedMediaType);
        Assert.Equal("x", await form.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task A_body_without_a_content_type_gets_a_problem(string environment)
    {
        var calls = new Calls();
        await using var app = await StartAsync(environment, calls: calls);

        foreach (var path in new[] { "/record", "/element" })
        {
            await AssertProblemAsync(await app.CreateClient().PutAsync(path, new ByteArrayContent("{\"name\":\"x\"}"u8.ToArray())), HttpStatusCode.UnsupportedMediaType);
            await AssertProblemAsync(await app.CreateClient().PutAsync(path, null), HttpStatusCode.BadRequest);
        }
        Assert.Equal(0, calls.Count);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task A_method_routing_does_not_take_gets_a_405_problem_with_its_allow_header(string environment)
    {
        await using var app = await StartAsync(environment);

        foreach (var path in new[] { "/record", "/mvc" })
        {
            var response = await app.CreateClient().DeleteAsync(path);

            await AssertProblemAsync(response, HttpStatusCode.MethodNotAllowed);
            Assert.Equal(["PUT"], response.Content.Headers.Allow);
        }
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task A_415_or_405_the_endpoint_answers_itself_is_left_alone(string environment)
    {
        await using var app = await StartAsync(environment, endpoints: e =>
        {
            e.MapPut("/own-415", (NameBody body) => Results.StatusCode(StatusCodes.Status415UnsupportedMediaType));
            e.MapPut("/own-405", (NameBody body) => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
        });

        foreach (var (path, status) in new[] { ("/own-415", HttpStatusCode.UnsupportedMediaType), ("/own-405", HttpStatusCode.MethodNotAllowed) })
        {
            var response = await app.CreateClient().PutAsync(path, Body("{\"name\":\"x\"}"u8.ToArray()));

            Assert.Equal(status, response.StatusCode);
            Assert.Null(response.Content.Headers.ContentType);
            Assert.Equal("", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task A_routing_rejection_that_a_later_middleware_answers_is_left_alone()
    {
        await using var app = await TestApp.StartAsync(_ => { }, afterRouting: a =>
        {
            a.UseAppKitBadRequests();
            a.Use(async (context, next) =>
            {
                if (context.Request.Headers["X-Answer"] == "body")
                {
                    context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                    await context.Response.WriteAsync("own");
                    return;
                }
                if (context.Request.Headers["X-Answer"] == "status")
                {
                    context.Response.StatusCode = StatusCodes.Status418ImATeapot;
                    return;
                }
                await next(context);
            });
        }, endpoints: e => e.MapPut("/record", (NameBody body) => Results.Text(body.Name)));
        var client = app.CreateClient();

        HttpRequestMessage Request(string answer) => new(HttpMethod.Put, "/record") { Content = Body("x"u8.ToArray(), "text/plain"), Headers = { { "X-Answer", answer } } };
        var body = await client.SendAsync(Request("body"));
        var status = await client.SendAsync(Request("status"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, body.StatusCode);
        Assert.Equal("own", await body.Content.ReadAsStringAsync());
        Assert.Equal((HttpStatusCode)StatusCodes.Status418ImATeapot, status.StatusCode);
        Assert.Equal("", await status.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task A_controller_keeps_its_own_answers(string environment)
    {
        await using var app = await StartAsync(environment);

        var json = await app.CreateClient().PutAsync("/mvc", Body("{\"name\":\"x\"}"u8.ToArray()));
        var text = await app.CreateClient().PutAsync("/mvc", Body("x"u8.ToArray(), "text/plain"));

        Assert.Equal("x", await json.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, text.StatusCode);
        var problem = JsonDocument.Parse(await text.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(MvcTitle, problem.GetProperty("title").GetString());
    }

    [Fact]
    public void Only_the_endpoints_routing_rejects_with_count_as_rejections()
    {
        static Endpoint Plain(string name, params object[] metadata) => new(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), name);
        var pattern = Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse("/x");

        Assert.Equal(415, BadRequestsExtensions.RoutingRejectionStatus(Plain("415 HTTP Unsupported Media Type")));
        Assert.Equal(405, BadRequestsExtensions.RoutingRejectionStatus(Plain("405 HTTP Method Not Supported")));
        Assert.Null(BadRequestsExtensions.RoutingRejectionStatus(null));
        Assert.Null(BadRequestsExtensions.RoutingRejectionStatus(Plain("404 Not Found")));
        Assert.Null(BadRequestsExtensions.RoutingRejectionStatus(Plain("415 HTTP Unsupported Media Type", new object())));
        Assert.Null(BadRequestsExtensions.RoutingRejectionStatus(new RouteEndpoint(_ => Task.CompletedTask, pattern, 0, EndpointMetadataCollection.Empty, "415 HTTP Unsupported Media Type")));
    }

    [Fact]
    public async Task Without_problem_details_registered_the_body_is_still_a_problem()
    {
        await using var app = await StartAsync(problemDetails: false);

        var response = await app.CreateClient().PutAsync("/record", Body(InvalidUtf8InString));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("application/json", true)]
    [InlineData("application/problem+json", true)]
    [InlineData("application/json; charset=\"utf-8\"", true)]
    [InlineData("application/json; charset=utf-16", false)]
    [InlineData("application/jsonx", false)]
    [InlineData("text/json", false)]
    [InlineData("multipart/form-data; boundary=x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_json_in_utf8_counts(string? contentType, bool expected) =>
        Assert.Equal(expected, BadRequestsExtensions.IsUtf8Json(contentType));

    [Fact]
    public async Task The_check_rewinds_a_valid_body_for_the_next_reader()
    {
        var context = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes("{\"name\":\"Ærlig\"}");
        context.Request.Body = new MemoryStream(bytes);

        Assert.True(await BadRequestsExtensions.IsValidUtf8Async(context.Request, CancellationToken.None));

        Assert.Equal(0, context.Request.Body.Position);
    }
}
