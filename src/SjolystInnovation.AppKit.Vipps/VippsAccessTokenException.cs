using System.Net;

namespace SjolystInnovation.AppKit.Vipps;

/// <summary>
/// Thrown when Vipps does not issue an access token, so the call never reached the API it was for. Its status
/// code, if any, is the one the token request got, so a 400 or a 404 here says nothing about a payment or an
/// agreement. It is an <see cref="HttpRequestException"/>, so <see cref="VippsFailures"/> sorts it as before.
/// </summary>
public sealed class VippsAccessTokenException : HttpRequestException
{
    /// <summary>Creates the exception with a default message.</summary>
    public VippsAccessTokenException() : base("Vipps did not issue an access token.") { }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public VippsAccessTokenException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The exception that caused it.</param>
    public VippsAccessTokenException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates the exception for a token request that failed with a status code.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The exception that caused it.</param>
    /// <param name="statusCode">The status code of the token request, or null when it got no answer.</param>
    public VippsAccessTokenException(string message, Exception? innerException, HttpStatusCode? statusCode)
        : base(message, innerException, statusCode) { }
}
