namespace Si.Vipps;

/// <summary>The credentials for one Vipps environment, from the merchant portal.</summary>
public sealed class VippsCredentials
{
    /// <summary>The API root, such as <c>https://api.vipps.no</c> or <c>https://apitest.vipps.no</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    /// <summary>The <c>Ocp-Apim-Subscription-Key</c>.</summary>
    public string SubscriptionKey { get; set; } = string.Empty;
    public string MerchantSerialNumber { get; set; } = string.Empty;

    /// <summary>Whether every credential is set.</summary>
    public bool IsConfigured =>
        !string.IsNullOrEmpty(BaseUrl) && !string.IsNullOrEmpty(ClientId) && !string.IsNullOrEmpty(ClientSecret)
        && !string.IsNullOrEmpty(SubscriptionKey) && !string.IsNullOrEmpty(MerchantSerialNumber);
}

/// <summary>The live and test credentials, and the system the requests name.</summary>
public sealed class VippsOptions
{
    public VippsCredentials Live { get; set; } = new() { BaseUrl = "https://api.vipps.no" };
    public VippsCredentials Test { get; set; } = new() { BaseUrl = "https://apitest.vipps.no" };

    /// <summary>The <c>Vipps-System-Name</c> header, such as the app's name. At most 30 characters are sent.</summary>
    public string SystemName { get; set; } = string.Empty;
    /// <summary>The <c>Vipps-System-Version</c> header. Build metadata after a <c>+</c> is left out.</summary>
    public string SystemVersion { get; set; } = "1.0.0";

    /// <summary>
    /// Logs the body of a failed response that carries no Vipps error ids. Off by default, since a body
    /// may echo a phone number, a name or an agreement id. For debugging only.
    /// </summary>
    public bool LogFailedResponseBodies { get; set; }

    /// <summary>The credentials for an environment.</summary>
    public VippsCredentials For(bool isTest) => isTest ? Test : Live;
}
