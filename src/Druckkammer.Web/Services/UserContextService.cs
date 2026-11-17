using System.Security.Claims;
using Druckkammer.Web.Data;
using Druckkammer.Web.Data.Entities;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;

namespace Druckkammer.Web.Services;

public record CurrentUserInfo(
    bool IsAuthenticated,
    string UserId,
    string Email,
    string DisplayName,
    string AvatarColor,
    bool IsApproved,
    IReadOnlyList<string> Roles,
    HashSet<string> Permissions)
{
    public bool HasPermission(string permission) =>
        Roles.Contains(AppRoles.Admin) || Permissions.Contains(permission);
}

public class UserContextService(
    AuthenticationStateProvider authStateProvider,
    IServiceScopeFactory scopeFactory)
{
    public async Task<CurrentUserInfo> GetCurrentUserAsync()
    {
        var state = await authStateProvider.GetAuthenticationStateAsync();
        var principal = state.User;

        if (principal.Identity?.IsAuthenticated != true)
        {
            return new CurrentUserInfo(false, string.Empty, string.Empty, "Gast", "#78909C", false, [], []);
        }

        string userId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return new CurrentUserInfo(false, string.Empty, string.Empty, "Gast", "#78909C", false, [], []);
        }

        using var scope = scopeFactory.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return new CurrentUserInfo(false, string.Empty, string.Empty, "Gast", "#78909C", false, [], []);
        }

        var roles = await userManager.GetRolesAsync(user);
        var permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // User-spezifische Permission-Claims
        var userClaims = await userManager.GetClaimsAsync(user);
        foreach (var c in userClaims.Where(c => c.Type == AppPermissions.ClaimType))
        {
            permissions.Add(c.Value);
        }

        // Rollen-basierte Permission-Claims
        foreach (var roleName in roles)
        {
            if (roleName == AppRoles.Admin)
            {
                foreach (var p in AppPermissions.All)
                    permissions.Add(p.Key);
                continue;
            }

            var role = await roleManager.FindByNameAsync(roleName);
            if (role is not null)
            {
                var roleClaims = await roleManager.GetClaimsAsync(role);
                foreach (var rc in roleClaims.Where(c => c.Type == AppPermissions.ClaimType))
                {
                    permissions.Add(rc.Value);
                }
            }
        }

        return new CurrentUserInfo(
            IsAuthenticated: true,
            UserId: user.Id,
            Email: user.Email ?? string.Empty,
            DisplayName: string.IsNullOrWhiteSpace(user.DisplayName) ? (user.Email ?? "User") : user.DisplayName,
            AvatarColor: string.IsNullOrWhiteSpace(user.AvatarColor) ? "#7E57C2" : user.AvatarColor,
            IsApproved: user.IsApproved,
            Roles: roles.ToList(),
            Permissions: permissions);
    }
}
