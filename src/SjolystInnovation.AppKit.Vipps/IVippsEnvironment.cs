namespace SjolystInnovation.AppKit.Vipps;

/// <summary>
/// Decides whether a call that creates something new goes to the test environment. A call on an
/// existing payment or agreement passes the environment it was created in instead.
/// </summary>
public interface IVippsEnvironment
{
    /// <summary>Checks whether new payments and agreements go to the test environment.</summary>
    Task<bool> IsTestAsync(CancellationToken cancellationToken = default);
}

/// <summary>An environment choice that never changes, for example one read from configuration.</summary>
public sealed class FixedVippsEnvironment(bool isTest) : IVippsEnvironment
{
    /// <summary>Returns the fixed environment choice.</summary>
    public Task<bool> IsTestAsync(CancellationToken cancellationToken = default) => Task.FromResult(isTest);
}
