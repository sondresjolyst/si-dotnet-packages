namespace Si.Vipps;

/// <summary>
/// Decides whether a call that creates something new goes to the test environment. A call on an
/// existing payment or agreement passes the environment it was created in instead.
/// </summary>
public interface IVippsEnvironment
{
    Task<bool> IsTestAsync(CancellationToken cancellationToken = default);
}

/// <summary>An environment that never changes, such as from configuration.</summary>
public sealed class FixedVippsEnvironment(bool isTest) : IVippsEnvironment
{
    public Task<bool> IsTestAsync(CancellationToken cancellationToken = default) => Task.FromResult(isTest);
}
