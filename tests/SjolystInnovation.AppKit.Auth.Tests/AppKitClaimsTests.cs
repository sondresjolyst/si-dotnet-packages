using System.Security.Claims;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

public sealed class AppKitClaimsTests
{
    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "Test"));

    [Fact]
    public void GetUserId_reads_the_name_identifier_before_sub()
    {
        var user = Principal(new Claim("sub", "from-sub"), new Claim(ClaimTypes.NameIdentifier, "from-nameid"));

        Assert.Equal("from-nameid", user.GetUserId());
    }

    [Fact]
    public void GetUserId_falls_back_to_sub()
    {
        Assert.Equal("ada", Principal(new Claim("sub", "ada")).GetUserId());
    }

    [Fact]
    public void GetUserId_is_null_without_either_claim()
    {
        Assert.Null(Principal(new Claim(ClaimTypes.Email, "ada@example.invalid")).GetUserId());
        Assert.Null(new ClaimsPrincipal().GetUserId());
    }

    [Fact]
    public void IsCaller_compares_the_user_id_ordinally()
    {
        var user = Principal(new Claim(ClaimTypes.NameIdentifier, "ada"));

        Assert.True(user.IsCaller("ada"));
        Assert.False(user.IsCaller("Ada"));
        Assert.False(user.IsCaller("bob"));
    }

    [Fact]
    public void IsCaller_is_false_without_a_user_id()
    {
        Assert.False(Principal().IsCaller(""));
        Assert.False(Principal().IsCaller("ada"));
    }

    [Fact]
    public void IsInAnyRole_matches_any_of_the_roles()
    {
        var user = Principal(new Claim(ClaimTypes.Role, "Editor"));

        Assert.True(user.IsInAnyRole("Admin", "Editor"));
        Assert.False(user.IsInAnyRole("Admin", "Viewer"));
        Assert.False(user.IsInAnyRole());
    }

    [Fact]
    public void IsInAnyRole_is_case_sensitive()
    {
        var user = Principal(new Claim(ClaimTypes.Role, "Admin"));

        Assert.False(user.IsInAnyRole("admin", "ADMIN"));
        Assert.True(user.IsInAnyRole("admin", "Admin"));

        // An old token with a lower-cased role does not pass for the canonical name.
        var legacy = Principal(new Claim(ClaimTypes.Role, "admin"));
        Assert.False(legacy.IsInAnyRole("Admin"));
    }
}
