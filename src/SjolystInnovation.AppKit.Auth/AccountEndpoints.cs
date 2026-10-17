using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SjolystInnovation.AppKit.Auth;

/// <summary>Adds the app's own data to an account export.</summary>
public interface IAccountDataExporter<in TUser>
{
    /// <summary>Gets the app's data for the user. The export writes it under <c>data</c>.</summary>
    Task<object?> ExportAsync(TUser user, CancellationToken cancellationToken);
}

internal sealed record AccountProfile(string Id, string UserName, string FirstName, string LastName, string Email, DateTime CreatedAt);
internal sealed record UpdateProfileRequest(string? FirstName, string? LastName);
internal sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

internal sealed record AccountExport(AccountProfile Account, IList<string> Roles, DateTime ExportedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Data);

/// <summary>Maps the account endpoints.</summary>
public static class AccountEndpointsExtensions
{
    /// <summary>
    /// Maps the account endpoints for signed-in users under <paramref name="prefix"/>: <c>GET</c> and
    /// <c>PUT {id}/profile</c>, <c>PUT {id}/password</c>, <c>GET {id}/export</c>, and <c>DELETE {id}/account</c>.
    /// The user or an admin may call them, except that only the user can change their own password.
    /// Deleting goes through <see cref="IAccountDeleter{TUser}"/>, so you must register
    /// <see cref="AccountServiceCollectionExtensions.AddAppKitAccounts{TUser, TContext}"/>. The admin role
    /// comes from its options.
    /// </summary>
    public static RouteGroupBuilder MapAppKitAccount<TUser>(this IEndpointRouteBuilder endpoints, string prefix = "/api/users")
        where TUser : IdentityUser, IAppKitAccount
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup(prefix).RequireAuthorization();
        group.MapGet("{id}/profile", (string id, HttpContext http) => AccountHandlers<TUser>.GetAsync(id, http));
        group.MapPut("{id}/profile", (string id, UpdateProfileRequest request, HttpContext http) => AccountHandlers<TUser>.UpdateAsync(id, request, http));
        group.MapPut("{id}/password", (string id, ChangePasswordRequest request, HttpContext http) => AccountHandlers<TUser>.ChangePasswordAsync(id, request, http));
        group.MapGet("{id}/export", (string id, HttpContext http) => AccountHandlers<TUser>.ExportAsync(id, http));
        group.MapDelete("{id}/account", (string id, HttpContext http) => AccountHandlers<TUser>.DeleteAsync(id, http));
        return group;
    }
}

internal static class AccountHandlers<TUser>
    where TUser : IdentityUser, IAppKitAccount
{
    private static T Get<T>(HttpContext http) where T : notnull => http.RequestServices.GetRequiredService<T>();

    private static string? CallerId(HttpContext http) =>
        http.User.GetUserId();

    private static bool CanActOn(HttpContext http, string id) =>
        CallerId(http) == id || http.User.IsInRole(Get<IOptions<AppKitAccountOptions>>(http).Value.AdminRole);

    private static AccountProfile Profile(TUser u) => new(u.Id, u.UserName ?? "", u.FirstName, u.LastName, u.Email ?? "", u.CreatedAt);

    /// <summary>Finds the user to act on. Returns a refusal when the caller may not act or the account is gone.</summary>
    private static async Task<(TUser? User, IResult? Refusal)> FindAsync(string id, HttpContext http, bool selfOnly = false)
    {
        if (selfOnly ? CallerId(http) != id : !CanActOn(http, id))
            return (null, TypedResults.Forbid());
        var user = await Get<UserManager<TUser>>(http).FindByIdAsync(id);
        return user is null || user.IsDeleted ? (null, TypedResults.NotFound()) : (user, null);
    }

    public static async Task<IResult> GetAsync(string id, HttpContext http)
    {
        var (user, refusal) = await FindAsync(id, http);
        return refusal ?? TypedResults.Ok(Profile(user!));
    }

    public static async Task<IResult> UpdateAsync(string id, UpdateProfileRequest request, HttpContext http)
    {
        var errors = new AuthFieldErrors();
        errors.Name("FirstName", request.FirstName, 50);
        errors.Name("LastName", request.LastName, 50);
        if (errors.Any)
            return errors.ToResult();

        var (user, refusal) = await FindAsync(id, http);
        if (refusal is not null)
            return refusal;
        user!.FirstName = request.FirstName!;
        user.LastName = request.LastName!;
        var result = await Get<UserManager<TUser>>(http).UpdateAsync(user);
        return result.Succeeded ? TypedResults.Ok(Profile(user)) : AuthResults.Error(AuthErrorCodes.ProfileUpdateFailed, "Failed to update profile.", StatusCodes.Status400BadRequest);
    }

    public static async Task<IResult> ChangePasswordAsync(string id, ChangePasswordRequest request, HttpContext http)
    {
        var errors = new AuthFieldErrors();
        if (string.IsNullOrWhiteSpace(request.CurrentPassword))
            errors.Required("CurrentPassword");
        if (string.IsNullOrWhiteSpace(request.NewPassword))
            errors.Required("NewPassword");
        if (errors.Any)
            return errors.ToResult();

        var (user, refusal) = await FindAsync(id, http, selfOnly: true);
        if (refusal is not null)
            return refusal;
        var result = await Get<UserManager<TUser>>(http).ChangePasswordAsync(user!, request.CurrentPassword!, request.NewPassword!);
        return result.Succeeded
            ? TypedResults.Ok(new MessageResponse("Password changed."))
            : new AuthFieldErrors().Password("NewPassword", result.Errors, Get<IOptions<IdentityOptions>>(http).Value.Password.RequiredLength, currentField: "CurrentPassword").ToResult();
    }

    public static async Task<IResult> ExportAsync(string id, HttpContext http)
    {
        var (user, refusal) = await FindAsync(id, http);
        if (refusal is not null)
            return refusal;
        var roles = await Get<UserManager<TUser>>(http).GetRolesAsync(user!);
        var extra = http.RequestServices.GetService<IAccountDataExporter<TUser>>();
        var data = extra is null ? null : await extra.ExportAsync(user!, http.RequestAborted);
        return TypedResults.Ok(new AccountExport(Profile(user!), roles, Get<TimeProvider>(http).GetUtcNow().UtcDateTime, data));
    }

    public static async Task<IResult> DeleteAsync(string id, HttpContext http)
    {
        var (user, refusal) = await FindAsync(id, http);
        if (refusal is not null)
            return refusal;
        try
        {
            await Get<IAccountDeleter<TUser>>(http).DeleteAsync(user!, http.RequestAborted);
        }
        catch (LastAdminException e)
        {
            return AuthResults.Error(AuthErrorCodes.LastAdmin, e.Message, StatusCodes.Status409Conflict);
        }
        catch (AccountChangeConflictException e)
        {
            return AuthResults.Error(AuthErrorCodes.AccountChangeConflict, e.Message, StatusCodes.Status409Conflict);
        }
        return TypedResults.NoContent();
    }
}
