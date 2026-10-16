using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SjolystInnovation.AppKit.Auth;

/// <summary>The user fields the account and admin endpoints read and the account deleter scrubs.</summary>
public interface IAppKitAccount
{
    /// <summary>The user's first name.</summary>
    string FirstName { get; set; }
    /// <summary>The user's last name.</summary>
    string LastName { get; set; }
    /// <summary>When the account was created.</summary>
    DateTime CreatedAt { get; }
    /// <summary>Whether the account is deleted.</summary>
    bool IsDeleted { get; set; }
    /// <summary>When the account was deleted, or null if it is not deleted.</summary>
    DateTime? DeletedAt { get; set; }
}

/// <summary>
/// Runs after an admin endpoint gives a user a role or removes one, for example to update what the role
/// grants. The role change is saved first, so an exception here fails the request but keeps the change.
/// </summary>
public interface IUserRolesChangedHandler<in TUser>
{
    /// <summary>Handles a change to the user's roles.</summary>
    Task OnRolesChangedAsync(TUser user, CancellationToken cancellationToken);
}

/// <summary>The options for <see cref="UserAdminExtensions.MapAppKitUserAdmin{TUser, TContext}"/>.</summary>
public sealed class AppKitUserAdminOptions<TUser>
{
    /// <summary>The roles an admin may give. The endpoints refuse any other role.</summary>
    public IReadOnlyCollection<string> Roles { get; set; } = [];

    /// <summary>The role that must keep at least one active holder. Defaults to <c>Admin</c>.</summary>
    public string AdminRole { get; set; } = "Admin";

    /// <summary>The authorization policy every endpoint requires. Defaults to <c>Admin</c>.</summary>
    public string Policy { get; set; } = "Admin";

    /// <summary>A function that creates a user from an email address, a first name, and a last name, for an invite. Required.</summary>
    public Func<string, string, string, TUser>? NewUser { get; set; }

    /// <summary>How long an invite code lasts. Defaults to 7 days.</summary>
    public TimeSpan InviteLifetime { get; set; } = TimeSpan.FromDays(7);
}

internal sealed record AdminUser(string Id, string UserName, string Email, string FirstName, string LastName, DateTime CreatedAt, bool IsDeleted, IList<string> Roles);
internal sealed record AssignRoleRequest(string? Role);
internal sealed record InviteRequest(string? Email, string? FirstName, string? LastName, string? Role);

/// <summary>Maps the user admin endpoints and seeds the first admin.</summary>
public static partial class UserAdminExtensions
{
    /// <summary>
    /// Maps the user admin endpoints under <paramref name="prefix"/>, behind
    /// <see cref="AppKitUserAdminOptions{TUser}.Policy"/>. <c>GET</c> lists users with their roles, and
    /// <c>?includeDeleted=true</c> adds deleted users. <c>POST {id}/roles</c> and
    /// <c>DELETE {id}/roles/{role}</c> give and remove a role. <c>POST invite</c> creates an account
    /// without a password and emails its owner a code through <see cref="IAuthEmails{TUser}"/>. Nobody
    /// can remove the admin role from themselves or from the last active admin.
    /// </summary>
    public static RouteGroupBuilder MapAppKitUserAdmin<TUser, TContext>(this IEndpointRouteBuilder endpoints,
        string prefix = "/api/users", Action<AppKitUserAdminOptions<TUser>>? configure = null)
        where TUser : IdentityUser, IAppKitAccount, IPasswordResetUser
        where TContext : IdentityDbContext<TUser, IdentityRole, string>
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = new AppKitUserAdminOptions<TUser>();
        configure?.Invoke(options);
        if (options.NewUser is null)
            throw new InvalidOperationException("AppKitUserAdminOptions.NewUser is required to create invited users.");
        var logger = endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SjolystInnovation.AppKit.Auth.UserAdmin");
        var handlers = new UserAdminHandlers<TUser, TContext>(options, logger);

        var group = endpoints.MapGroup(prefix).RequireAuthorization(options.Policy);
        group.MapGet("", (HttpContext http, bool includeDeleted = false) => UserAdminHandlers<TUser, TContext>.ListAsync(http, includeDeleted));
        group.MapPost("{id}/roles", (string id, AssignRoleRequest request, HttpContext http) => handlers.AddRoleAsync(id, request, http));
        group.MapDelete("{id}/roles/{role}", (string id, string role, HttpContext http) => handlers.RemoveRoleAsync(id, role, http));
        group.MapPost("invite", (InviteRequest request, HttpContext http) => handlers.InviteAsync(request, http));
        return group;
    }

    /// <summary>
    /// Creates the first admin from <c>Seed:AdminEmail</c> and <c>Seed:AdminPassword</c> while no user holds
    /// <paramref name="adminRole"/>, or gives the role to an existing user with that email. The names come
    /// from <c>Seed:AdminFirstName</c> and <c>Seed:AdminLastName</c>. The first name defaults to
    /// <c>Admin</c> and the last name to empty. Does nothing when the email or the password is not set.
    /// </summary>
    public static async Task SeedAppKitFirstAdminAsync<TUser>(this IServiceProvider services, IConfiguration configuration,
        Func<string, string, string, TUser> newUser, string adminRole = "Admin")
        where TUser : IdentityUser
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(newUser);
        var email = configuration["Seed:AdminEmail"];
        var password = configuration["Seed:AdminPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return;

        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<TUser>>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SjolystInnovation.AppKit.Auth.Seed");
        if ((await users.GetUsersInRoleAsync(adminRole)).Count > 0)
            return;

        var existing = await users.FindByEmailAsync(email);
        if (existing is not null)
        {
            await users.AddToRoleAsync(existing, adminRole);
            Log.SeedPromoted(logger, existing.Id);
            return;
        }

        var user = newUser(email, configuration["Seed:AdminFirstName"] ?? "Admin", configuration["Seed:AdminLastName"] ?? "");
        user.UserName ??= email;
        user.Email = email;
        user.EmailConfirmed = true;
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            Log.SeedFailed(logger, string.Join(" ", created.Errors.Select(e => e.Description)));
            return;
        }
        await users.AddToRoleAsync(user, adminRole);
        Log.SeedCreated(logger, user.Id);
    }

    /// <summary>
    /// Creates each role in <paramref name="roles"/> that does not exist yet. Existing roles stay as they
    /// are, so the call is safe on every startup. Logs each role it creates and each role it fails to create.
    /// </summary>
    /// <param name="services">The app's service provider.</param>
    /// <param name="roles">The role names the app uses.</param>
    public static async Task SeedAppKitRolesAsync(this IServiceProvider services, IEnumerable<string> roles)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(roles);

        using var scope = services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SjolystInnovation.AppKit.Auth.Seed");
        foreach (var role in roles)
        {
            if (await roleManager.RoleExistsAsync(role))
                continue;
            var created = await roleManager.CreateAsync(new IdentityRole(role));
            if (created.Succeeded)
                Log.RoleCreated(logger, role);
            else
                Log.RoleFailed(logger, role, string.Join(" ", created.Errors.Select(e => e.Description)));
        }
    }

    internal static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Gave the admin role to the existing seed user {UserId}.")]
        public static partial void SeedPromoted(ILogger logger, string userId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Created the first admin {UserId} from configuration.")]
        public static partial void SeedCreated(ILogger logger, string userId);

        [LoggerMessage(Level = LogLevel.Error, Message = "Could not create the seed admin: {Errors}")]
        public static partial void SeedFailed(ILogger logger, string errors);

        [LoggerMessage(Level = LogLevel.Information, Message = "Created the role {Role}.")]
        public static partial void RoleCreated(ILogger logger, string role);

        [LoggerMessage(Level = LogLevel.Error, Message = "Could not create the role {Role}: {Errors}")]
        public static partial void RoleFailed(ILogger logger, string role, string errors);

        [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} invited with the role {Role}.")]
        public static partial void Invited(ILogger logger, string userId, string role);
    }
}

internal sealed class UserAdminHandlers<TUser, TContext>(AppKitUserAdminOptions<TUser> options, ILogger logger)
    where TUser : IdentityUser, IAppKitAccount, IPasswordResetUser
    where TContext : IdentityDbContext<TUser, IdentityRole, string>
{
    private static T Get<T>(HttpContext http) where T : notnull => http.RequestServices.GetRequiredService<T>();

    private static JsonHttpResult<MessageResponse> Message(string message, int status) => TypedResults.Json(new MessageResponse(message), statusCode: status);

    private bool KnownRole(string? role) => role is not null && options.Roles.Contains(role);

    public static async Task<IResult> ListAsync(HttpContext http, bool includeDeleted)
    {
        var db = Get<TContext>(http);
        var query = db.Users.AsNoTracking();
        if (!includeDeleted)
            query = query.Where(u => !u.IsDeleted);
        var users = await query.OrderBy(u => u.Email).ToListAsync(http.RequestAborted);

        // One query for every user's roles, instead of one per user.
        var roles = (await (from ur in db.UserRoles
                            join r in db.Roles on ur.RoleId equals r.Id
                            select new { ur.UserId, r.Name }).ToListAsync(http.RequestAborted))
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => (IList<string>)g.Select(x => x.Name!).Order(StringComparer.Ordinal).ToList());

        return TypedResults.Ok(users.Select(u => new AdminUser(u.Id, u.UserName ?? "", u.Email ?? "", u.FirstName, u.LastName,
            u.CreatedAt, u.IsDeleted, roles.GetValueOrDefault(u.Id) ?? [])).ToList());
    }

    public async Task<IResult> AddRoleAsync(string id, AssignRoleRequest request, HttpContext http)
    {
        if (string.IsNullOrWhiteSpace(request.Role))
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Role"] = ["Required."] });
        if (!KnownRole(request.Role))
            return Message("Unknown role.", StatusCodes.Status400BadRequest);

        var users = Get<UserManager<TUser>>(http);
        var user = await users.FindByIdAsync(id);
        if (user is null || user.IsDeleted)
            return TypedResults.NotFound();
        if (await users.IsInRoleAsync(user, request.Role))
            return TypedResults.Ok(new MessageResponse("User already has this role."));

        var result = await users.AddToRoleAsync(user, request.Role);
        if (!result.Succeeded)
            return Message("Failed to assign role.", StatusCodes.Status400BadRequest);
        await RolesChangedAsync(user, http);
        return TypedResults.Ok(new MessageResponse("Role assigned."));
    }

    public async Task<IResult> RemoveRoleAsync(string id, string role, HttpContext http)
    {
        if (!KnownRole(role))
            return Message("Unknown role.", StatusCodes.Status400BadRequest);
        var callerId = http.User.GetUserId();
        var isAdminRole = string.Equals(role, options.AdminRole, StringComparison.Ordinal);
        if (isAdminRole && id == callerId)
            return Message("You cannot remove your own admin role.", StatusCodes.Status400BadRequest);

        var users = Get<UserManager<TUser>>(http);
        var user = await users.FindByIdAsync(id);
        if (user is null)
            return TypedResults.NotFound();

        if (isAdminRole && !user.IsDeleted && await users.IsInRoleAsync(user, role)
            && (await users.GetUsersInRoleAsync(role)).Count(a => !a.IsDeleted) <= 1)
            return Message("The last admin keeps the admin role. Give someone else the role first.", StatusCodes.Status409Conflict);

        var result = await users.RemoveFromRoleAsync(user, role);
        if (!result.Succeeded)
            return Message("Failed to remove role.", StatusCodes.Status400BadRequest);
        await RolesChangedAsync(user, http);
        return TypedResults.NoContent();
    }

    public async Task<IResult> InviteAsync(InviteRequest request, HttpContext http)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Email) || !LooksLikeEmail(request.Email) || request.Email.Length > 200)
            errors["Email"] = ["Not a valid email address."];
        if (string.IsNullOrWhiteSpace(request.FirstName) || request.FirstName.Length > 50)
            errors["FirstName"] = ["Required, at most 50 characters."];
        if (string.IsNullOrWhiteSpace(request.LastName) || request.LastName.Length > 50)
            errors["LastName"] = ["Required, at most 50 characters."];
        if (!KnownRole(request.Role))
            errors["Role"] = ["Unknown role."];
        if (errors.Count > 0)
            return TypedResults.ValidationProblem(errors);

        var users = Get<UserManager<TUser>>(http);
        if (await users.FindByEmailAsync(request.Email!) is not null)
            return Message("That email already has an account.", StatusCodes.Status409Conflict);

        var user = options.NewUser!(request.Email!, request.FirstName!, request.LastName!);
        user.UserName ??= request.Email;
        user.Email = request.Email;
        user.EmailConfirmed = true;
        var created = await users.CreateAsync(user);
        if (!created.Succeeded)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Email"] = created.Errors.Select(e => e.Description).ToArray() });

        if (!await TryAddRoleAsync(users, user, request.Role!))
        {
            // Delete the half-made account, so the invite can be sent again.
            await users.DeleteAsync(user);
            return Message("Failed to assign role.", StatusCodes.Status400BadRequest);
        }
        var code = PasswordResetCodes.Issue(user, lifetime: options.InviteLifetime);
        await users.UpdateAsync(user);
        await RolesChangedAsync(user, http);
        await Get<IAuthEmails<TUser>>(http).SendInviteAsync(user, code, http.RequestAborted);
        UserAdminExtensions.Log.Invited(logger, user.Id, request.Role!);
        return TypedResults.Ok(new MessageResponse("Invitation sent."));
    }

    // Drop the user's cached roles from the bearer check, so the change applies at once on this instance.
    private static async Task RolesChangedAsync(TUser user, HttpContext http)
    {
        http.RequestServices.GetService<IMemoryCache>()?.Remove(IdentityAndBearerExtensions.UserCheckKey(user.Id));
        foreach (var handler in http.RequestServices.GetServices<IUserRolesChangedHandler<TUser>>())
            await handler.OnRolesChangedAsync(user, http.RequestAborted);
    }

    // Identity throws instead of returning a failure when the role is missing from the database.
    private static async Task<bool> TryAddRoleAsync(UserManager<TUser> users, TUser user, string role)
    {
        try
        {
            return (await users.AddToRoleAsync(user, role)).Succeeded;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool LooksLikeEmail(string value)
    {
        var at = value.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && at < value.Length - 1 && at == value.LastIndexOf('@');
    }
}
