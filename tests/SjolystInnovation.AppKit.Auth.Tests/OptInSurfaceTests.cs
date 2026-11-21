using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

/// <summary>Proves that the opt-in sign-up adds types and leaves what an app already uses alone.</summary>
public class OptInSurfaceTests
{
    // The public types before the sign-up work, by name without the generic arity.
    private static readonly string[] Before =
    [
        "AccountChangeConflictException", "AccountEndpointsExtensions", "AccountServiceCollectionExtensions",
        "AppKitAccountOptions", "AppKitAuthEmailOptions", "AppKitAuthEndpointOptions", "AppKitAuthOptions",
        "AppKitClaims", "AppKitJwtBearerOptions", "AppKitNewUser", "AppKitUserAdminOptions",
        "AuthEmailServiceCollectionExtensions", "AuthEndpointsExtensions", "AuthErrorCodes",
        "AuthServiceCollectionExtensions", "CodeCheck", "EmailBrand", "EmailLanguage", "EmailVerificationCodes",
        "IAccountDataEraser", "IAccountDataExporter", "IAccountDeletedHandler", "IAccountDeleter", "IAppKitAccount",
        "IAuthDbContext", "IAuthEmails", "IdentityAndBearerExtensions", "IEmailBrand", "IEmailVerificationUser",
        "IJwtTokenService", "IPasswordResetUser", "IUserRolesChangedHandler", "IVerificationEmails",
        "LastAdminException", "RefreshToken", "RefreshTokenModelBuilderExtensions", "UserAdminExtensions",
    ];

    private static readonly string[] Added =
    [
        "AccountConsent", "AccountConsentModelBuilderExtensions", "AppKitRegistration", "AppKitRegistrationOptions",
        "IAccountConsentDbContext", "IConsentRequirements", "IConsentVersionSource", "IEmailVerifiedHandler",
        "IRegistrationEmails", "IRegistrationHandler", "ConsentRequirement", "SelfRegistrationServiceCollectionExtensions",
        "PendingConsent", "SignUpMailLog", "AppKitEndpointFilterExtensions", "IRemovalReminderUser", "IUnverifiedAccountCleanup", "UnverifiedCleanupResult",
    ];

    private static string[] PublicNames() =>
        typeof(RefreshToken).Assembly.GetExportedTypes().Select(t => t.Name.Split('`')[0]).Distinct().Order().ToArray();

    [Fact]
    public void Every_public_type_from_before_is_still_public()
    {
        var names = PublicNames();
        Assert.Empty(Before.Except(names));
    }

    [Fact]
    public void The_only_new_public_types_are_the_sign_up_types()
    {
        var unexpected = PublicNames().Except(Before).Except(Added);
        Assert.Empty(unexpected);
    }

    [Fact]
    public void The_new_types_live_in_the_Auth_namespace() =>
        Assert.All(typeof(RefreshToken).Assembly.GetExportedTypes(), t => Assert.Equal("SjolystInnovation.AppKit.Auth", t.Namespace));

    [Fact]
    public void The_register_mapping_is_a_new_method_on_the_existing_endpoints_class()
    {
        var methods = typeof(AuthEndpointsExtensions).GetMethods().Where(m => m.IsPublic && m.IsStatic).Select(m => m.Name).Order().ToArray();

        Assert.Equal(["MapAppKitAuth", "MapAppKitRegistration"], methods);
    }

    [Fact]
    public void MapAppKitAuth_keeps_its_parameters()
    {
        var method = typeof(AuthEndpointsExtensions).GetMethod("MapAppKitAuth")!;

        Assert.Equal(["endpoints", "prefix", "configure"], method.GetParameters().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void An_app_that_never_opts_in_has_no_consent_tables()
    {
        using var db = new TestDb().NewContext();
        Assert.Null(db.Model.FindEntityType(typeof(AccountConsent)));
        Assert.Null(db.Model.FindEntityType(typeof(ConsentRequirement)));
        Assert.NotNull(db.Model.FindEntityType(typeof(RefreshToken)));
    }

    [Fact]
    public void The_existing_registrations_leave_no_sign_up_marker_and_no_sign_up_options()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAppKitAccounts<IdentityTestUser, IdentityTestDbContext>();

        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<SelfRegistrationRegistered>());
        Assert.Null(provider.GetService<IConsentRequirements>());
        Assert.DoesNotContain(services, d => d.ServiceType.Name.Contains("Registration", StringComparison.Ordinal)
            || d.ServiceType.Name.Contains("Consent", StringComparison.Ordinal));
    }
}
