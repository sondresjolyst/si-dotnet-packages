namespace SjolystInnovation.AppKit.Vipps;

/// <summary>What a failed Vipps call tells the caller about the operation it asked for.</summary>
public enum VippsFailureKind
{
    /// <summary>The exception did not come from the call to Vipps, for example a bug in the caller.</summary>
    None,
    /// <summary>Vipps answered and refused the call with a 4xx other than 408, 409, and 429. The operation did not happen in this call.</summary>
    Refused,
    /// <summary>The call timed out, failed in transport, or got a 5xx, 408, 409, or 429. The operation may have gone through.</summary>
    Uncertain,
    /// <summary>The caller's own token was canceled. The operation may have gone through.</summary>
    CallerCancelled,
}

/// <summary>
/// Tells apart the failures of <see cref="IVippsClient"/> calls such as capture, cancel, refund, and get payment.
/// A failed answer throws <see cref="HttpRequestException"/> with its status code. The client's own timeout throws
/// a cancellation while the caller's token is not canceled.
/// </summary>
public static class VippsFailures
{
    /// <summary>
    /// The timeout to set on the client's <see cref="HttpClient"/>, 15 seconds. It is short enough that a request that
    /// holds a row lock while it waits does not hang, and long enough for a capture or a refund.
    /// </summary>
    public static readonly TimeSpan RecommendedTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Whether Vipps answered and refused the call with a 4xx status. 408, 409, and 429 are left out, since they can
    /// follow an earlier try that went through, and 409 can be an idempotency conflict. A refusal does not prove an
    /// earlier try failed, so a caller that must know reads the payment.
    /// </summary>
    /// <param name="exception">The exception the call threw.</param>
    /// <returns>Whether it is a refusal.</returns>
    public static bool IsRefusal(Exception exception) =>
        exception is HttpRequestException { StatusCode: { } status } && (int)status is >= 400 and < 500 and not (408 or 409 or 429);

    /// <summary>
    /// Whether Vipps could not be reached or answered with an error. <see cref="HttpClient"/> reports its own timeout as a
    /// cancellation, so a cancellation counts only while <paramref name="cancellationToken"/>, the caller's token, is not canceled.
    /// </summary>
    /// <param name="exception">The exception the call threw.</param>
    /// <param name="cancellationToken">The token the caller passed to the call.</param>
    /// <returns>Whether it is a failure of the call to Vipps.</returns>
    public static bool IsProviderFailure(Exception exception, CancellationToken cancellationToken) =>
        Classify(exception, cancellationToken) is VippsFailureKind.Refused or VippsFailureKind.Uncertain;

    /// <summary>Whether the call stopped because the caller's own token was canceled, and not because of the client's timeout.</summary>
    /// <param name="exception">The exception the call threw.</param>
    /// <param name="cancellationToken">The token the caller passed to the call.</param>
    /// <returns>Whether the caller canceled.</returns>
    public static bool IsCallerCancellation(Exception exception, CancellationToken cancellationToken) =>
        Classify(exception, cancellationToken) == VippsFailureKind.CallerCancelled;

    /// <summary>Sorts the exception a call threw into what it says about the operation.</summary>
    /// <param name="exception">The exception the call threw.</param>
    /// <param name="cancellationToken">The token the caller passed to the call.</param>
    /// <returns>The kind of failure.</returns>
    public static VippsFailureKind Classify(Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            HttpRequestException when IsRefusal(exception) => VippsFailureKind.Refused,
            HttpRequestException => VippsFailureKind.Uncertain,
            OperationCanceledException when cancellationToken.IsCancellationRequested => VippsFailureKind.CallerCancelled,
            OperationCanceledException => VippsFailureKind.Uncertain,
            _ => VippsFailureKind.None,
        };
    }
}
