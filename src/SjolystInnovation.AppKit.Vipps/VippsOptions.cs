namespace SjolystInnovation.AppKit.Vipps;

/// <summary>The credentials for one Vipps environment, from the merchant portal.</summary>
public sealed class VippsCredentials
{
    /// <summary>The API root, for example <c>https://api.vipps.no</c> or <c>https://apitest.vipps.no</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;
    /// <summary>The client id.</summary>
    public string ClientId { get; set; } = string.Empty;
    /// <summary>The client secret.</summary>
    public string ClientSecret { get; set; } = string.Empty;
    /// <summary>The <c>Ocp-Apim-Subscription-Key</c>.</summary>
    public string SubscriptionKey { get; set; } = string.Empty;
    /// <summary>The merchant serial number, sent as <c>Merchant-Serial-Number</c>.</summary>
    public string MerchantSerialNumber { get; set; } = string.Empty;

    /// <summary>Whether every credential is set.</summary>
    public bool IsConfigured =>
        !string.IsNullOrEmpty(BaseUrl) && !string.IsNullOrEmpty(ClientId) && !string.IsNullOrEmpty(ClientSecret)
        && !string.IsNullOrEmpty(SubscriptionKey) && !string.IsNullOrEmpty(MerchantSerialNumber);
}

/// <summary>The live and test credentials, and the system that the requests name.</summary>
public sealed class VippsOptions
{
    /// <summary>The live credentials. The base URL defaults to <c>https://api.vipps.no</c>.</summary>
    public VippsCredentials Live { get; set; } = new() { BaseUrl = "https://api.vipps.no" };
    /// <summary>The test credentials. The base URL defaults to <c>https://apitest.vipps.no</c>.</summary>
    public VippsCredentials Test { get; set; } = new() { BaseUrl = "https://apitest.vipps.no" };

    /// <summary>The <c>Vipps-System-Name</c> header, for example the app's name. At most 30 characters are sent.</summary>
    public string SystemName { get; set; } = string.Empty;
    /// <summary>The <c>Vipps-System-Version</c> header. Build metadata after a <c>+</c> is left out, and at most 30 characters are sent. Defaults to <c>1.0.0</c>.</summary>
    public string SystemVersion { get; set; } = "1.0.0";

    /// <summary>
    /// Whether to log the body of a failed response that carries no Vipps error ids. For debugging only,
    /// because a body may echo a phone number, a name, or an agreement id. Defaults to false.
    /// </summary>
    public bool LogFailedResponseBodies { get; set; }

    /// <summary>Gets the credentials for an environment.</summary>
    public VippsCredentials For(bool isTest) => isTest ? Test : Live;
}
