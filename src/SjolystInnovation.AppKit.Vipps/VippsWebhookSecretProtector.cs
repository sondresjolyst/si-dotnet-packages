using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace SjolystInnovation.AppKit.Vipps;

/// <summary>Protects webhook secrets at rest with ASP.NET Core Data Protection.</summary>
public interface IVippsWebhookSecretProtector
{
    /// <summary>Protects a secret for storage. An empty secret stays empty.</summary>
    /// <param name="plaintext">The secret Vipps returned.</param>
    /// <returns>The protected secret, starting with <see cref="VippsWebhookOptions.SecretPrefix"/>.</returns>
    string Protect(string plaintext);

    /// <summary>
    /// Reads a stored secret. A value without <see cref="VippsWebhookOptions.SecretPrefix"/> is plain text and
    /// comes back as it is.
    /// </summary>
    /// <param name="stored">The stored secret.</param>
    /// <returns>The secret.</returns>
    /// <exception cref="CryptographicException">When the keys that protected the secret are gone or the value is damaged.</exception>
    string Unprotect(string stored);

    /// <summary>Reads a stored secret without throwing.</summary>
    /// <param name="stored">The stored secret.</param>
    /// <param name="secret">The secret, or an empty string when it cannot be read.</param>
    /// <returns>True when the secret could be read and is not empty.</returns>
    bool TryUnprotect(string? stored, out string secret);
}

/// <summary>
/// Protects secrets with the purpose and prefix in <see cref="VippsWebhookOptions"/>. An app that moves from
/// its own protector keeps its purpose and prefix, so the stored secrets stay readable.
/// </summary>
internal sealed class VippsWebhookSecretProtector : IVippsWebhookSecretProtector
{
    private readonly IDataProtector _protector;
    private readonly string _prefix;

    /// <summary>Creates the protector. The DI container calls this.</summary>
    public VippsWebhookSecretProtector(IDataProtectionProvider provider, IOptions<VippsWebhookOptions> options)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);
        _protector = provider.CreateProtector(options.Value.SecretPurpose);
        _prefix = options.Value.SecretPrefix;
    }

    public string Protect(string plaintext) =>
        string.IsNullOrEmpty(plaintext) ? plaintext : _prefix + _protector.Protect(plaintext);

    public string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored;
        return stored.StartsWith(_prefix, StringComparison.Ordinal) ? _protector.Unprotect(stored[_prefix.Length..]) : stored;
    }

    public bool TryUnprotect(string? stored, out string secret)
    {
        secret = string.Empty;
        if (string.IsNullOrEmpty(stored)) return false;
        try
        {
            secret = Unprotect(stored);
            return secret.Length > 0;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
