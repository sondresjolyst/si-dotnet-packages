using System.Buffers;
using System.Text.Unicode;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace SjolystInnovation.AppKit.Hosting;

/// <summary>Answers a request the app cannot read with a client error instead of a 500.</summary>
public static partial class BadRequestsExtensions
{
    internal const string LogCategory = "SjolystInnovation.AppKit.Hosting.BadRequests";
    internal const int ChunkSize = 16 * 1024;

    /// <summary>
    /// Answers 400 with a problem details body when a JSON request body is not valid UTF-8, before the
    /// endpoint reads it. A <see cref="BadHttpRequestException"/> from later in the pipeline gets its own
    /// status code, such as 400 for a body that is not valid JSON in Development or 413 for a body over the
    /// size limit, instead of reaching the exception handler as a 500. Only requests that matched an
    /// endpoint are checked, so call it after <c>UseRouting</c>, and after <c>UseAuthorization</c> so a
    /// caller without access still gets 401 or 403.
    /// </summary>
    public static IApplicationBuilder UseAppKitBadRequests(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory);
        return app.Use(async (context, next) =>
        {
            try
            {
                if (context.GetEndpoint() is not null && IsUtf8Json(context.Request.ContentType)
                    && !await IsValidUtf8Async(context.Request, context.RequestAborted))
                {
                    Log.InvalidUtf8(logger);
                    await WriteProblemAsync(context, StatusCodes.Status400BadRequest);
                    return;
                }
                await next(context);
            }
            catch (BadHttpRequestException ex) when (!context.Response.HasStarted)
            {
                Log.Unreadable(logger, ex.StatusCode, ex.Message);
                context.Response.Clear();
                await WriteProblemAsync(context, ex.StatusCode);
            }
        });
    }

    /// <summary>Whether the content type is JSON in UTF-8, which is JSON with no charset or a UTF-8 one.</summary>
    internal static bool IsUtf8Json(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var media))
            return false;
        var json = media.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || media.Suffix.Equals("json", StringComparison.OrdinalIgnoreCase);
        return json && (!media.Charset.HasValue || HeaderUtilities.RemoveQuotes(media.Charset).Equals("utf-8", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reads the whole body in chunks and rewinds it, so the endpoint reads it again from the start.</summary>
    internal static async Task<bool> IsValidUtf8Async(HttpRequest request, CancellationToken cancellationToken)
    {
        request.EnableBuffering();
        var bytes = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var chars = ArrayPool<char>.Shared.Rent(ChunkSize);
        try
        {
            // A sequence cut at the end of a chunk is carried over to the start of the next.
            var carried = 0;
            while (true)
            {
                var read = await request.Body.ReadAsync(bytes.AsMemory(carried, ChunkSize - carried), cancellationToken);
                var last = read == 0;
                var length = carried + read;
                var status = Utf8.ToUtf16(bytes.AsSpan(0, length), chars.AsSpan(0, ChunkSize), out var consumed, out _,
                    replaceInvalidSequences: false, isFinalBlock: last);
                if (status == OperationStatus.InvalidData)
                    return false;
                if (last)
                    return true;
                carried = length - consumed;
                bytes.AsSpan(consumed, carried).CopyTo(bytes);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
            ArrayPool<char>.Shared.Return(chars);
            request.Body.Position = 0;
        }
    }

    private static async Task WriteProblemAsync(HttpContext context, int status)
    {
        context.Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status, Title = ReasonPhrases.GetReasonPhrase(status) };
        var writer = context.RequestServices.GetService<IProblemDetailsService>();
        if (writer is null || !await writer.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem }))
            await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", context.RequestAborted);
    }

    internal static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Refused a JSON request body that is not valid UTF-8.")]
        public static partial void InvalidUtf8(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Refused an unreadable request with {StatusCode}: {Reason}")]
        public static partial void Unreadable(ILogger logger, int statusCode, string reason);
    }
}
