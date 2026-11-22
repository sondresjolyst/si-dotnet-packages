using System.Net;

namespace SjolystInnovation.AppKit.Vipps;

/// <summary>
/// Thrown when a Vipps API answers with an error status. It is an <see cref="HttpRequestException"/> with the status
/// code, so <see cref="VippsFailures"/> and any <c>catch (HttpRequestException)</c> sort it as before. It also carries
/// the parts of the error body that hold no request data, such as the ePayment error code. A body that is not a
/// Vipps error, such as the 404 page of a proxy, leaves them null.
/// </summary>
public sealed class VippsApiException : HttpRequestException
{
    /// <summary>Creates the exception with a default message.</summary>
    public VippsApiException() : base("A Vipps call failed.") { }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public VippsApiException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The exception that caused it.</param>
    public VippsApiException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates the exception for a call that failed with a status code.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The exception that caused it, if any.</param>
    /// <param name="statusCode">The status code of the response.</param>
    public VippsApiException(string message, Exception? innerException, HttpStatusCode? statusCode)
        : base(message, innerException, statusCode) { }

    /// <summary>The name of the call that failed, such as <c>get-payment</c>.</summary>
    public string? Operation { get; init; }

    /// <summary>
    /// The Vipps error code, such as <c>5090</c> for an ePayment reference that Vipps does not know. The ePayment API
    /// sends it as the <c>ErrorCode</c> entry of <c>extraDetails</c>. Null when the body has none.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// The problem id at the end of the RFC 7807 <c>type</c> URL, such as <c>charge-due-too-soon</c> from the
    /// Recurring API. Null when the body has none.
    /// </summary>
    public string? ProblemType { get; init; }

    /// <summary>The problem <c>title</c>, such as <c>Reference not found</c>, when it is plain words without digits.</summary>
    public string? Title { get; init; }

    /// <summary>The <c>traceId</c> or <c>contextId</c> that Vipps support can look up.</summary>
    public string? TraceId { get; init; }

    /// <summary>The names of the fields that <c>extraDetails</c> blames, without their reasons.</summary>
    public IReadOnlyList<string> Fields { get; init; } = [];
}
