using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace SjolystInnovation.AppKit.Auth;

/// <summary>Endpoint filters that limit users who have not verified the email or who must accept a document again.</summary>
public static class AppKitEndpointFilterExtensions
{
    /// <summary>
    /// Refuses a signed-in user whose email is not confirmed with 403 and the code <c>emailNotVerified</c>. Works on an
    /// endpoint or a group. An anonymous request goes on to the normal authorization answer, so combine it with
    /// <c>RequireAuthorization</c>. It reads the fact the bearer check sets, so it takes effect as soon as the user
    /// check is refreshed, which verifying the email does at once. A principal without the fact counts as not verified.
    /// </summary>
    /// <exception cref="InvalidOperationException">When the first request builds the endpoint and the app did not call
    /// <see cref="SelfRegistrationServiceCollectionExtensions.AddAppKitSelfRegistration{TUser, TContext}"/>.</exception>
    public static TBuilder RequireVerifiedEmail<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddEndpointFilterFactory((factory, next) =>
        {
            RequireOptIn(factory.ApplicationServices, nameof(RequireVerifiedEmail));
            return invocation => invocation.HttpContext.User.Identity?.IsAuthenticated != true || invocation.HttpContext.User.IsEmailVerified()
                ? next(invocation)
                : ValueTask.FromResult<object?>(AuthResults.Error(AuthErrorCodes.EmailNotVerified, "Verify your email address to use this.", StatusCodes.Status403Forbidden));
        });
    }

    /// <summary>
    /// Refuses a signed-in user who must accept a document again with 403 and the code <c>termsAcceptanceRequired</c>. Works on
    /// an endpoint or a group. An anonymous request goes on to the normal authorization answer. It reads the fact the
    /// bearer check sets, so accepting through <c>accept-terms</c> lifts it at once. A principal without the fact, for example
    /// one from another authentication scheme, counts as not up to date and is refused too.
    /// </summary>
    /// <exception cref="InvalidOperationException">When the first request builds the endpoint and the app did not call
    /// <see cref="SelfRegistrationServiceCollectionExtensions.AddAppKitSelfRegistration{TUser, TContext}"/>.</exception>
    public static TBuilder RequireCurrentTerms<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddEndpointFilterFactory((factory, next) =>
        {
            RequireOptIn(factory.ApplicationServices, nameof(RequireCurrentTerms));
            return invocation => invocation.HttpContext.User.Identity?.IsAuthenticated != true
                || (invocation.HttpContext.User.HasPendingFact() && invocation.HttpContext.User.GetPendingConsents().Count == 0)
                ? next(invocation)
                : ValueTask.FromResult<object?>(AuthResults.Error(AuthErrorCodes.TermsAcceptanceRequired, "Accept the current terms to use this.", StatusCodes.Status403Forbidden));
        });
    }

    // The facts come from the bearer check of an app that opted in. Without the opt-in every user would pass, so the endpoint refuses to build.
    private static void RequireOptIn(IServiceProvider services, string filter)
    {
        if (services.GetService<SelfRegistrationRegistered>() is null)
            throw new InvalidOperationException(
                $"{filter} needs the sign-up options. Call AddAppKitSelfRegistration<TUser, TContext>() first.");
    }
}
