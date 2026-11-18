using System.Security.Claims;
using Druckkammer.Web.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Druckkammer.Web.Data;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        await db.Database.EnsureCreatedAsync();

        // 1. Standardrollen & granulare Permissions anlegen
        foreach (var roleName in AppRoles.All)
        {
            var role = await roleManager.FindByNameAsync(roleName);
            if (role is null)
            {
                role = new IdentityRole(roleName);
                await roleManager.CreateAsync(role);

                foreach (var perm in AppPermissions.GetDefaultPermissionsForRole(roleName))
                {
                    await roleManager.AddClaimAsync(role, new Claim(AppPermissions.ClaimType, perm));
                }
            }
        }

        // 2. Standard-Systemeinstellungen anlegen (keine Demo-Benutzer; der erste Benutzer richtet beim ersten Start den Admin-Account ein)
        if (!await db.SystemSettings.AnyAsync())
        {
            db.SystemSettings.AddRange(
                new SystemSetting
                {
                    Key = "AllowSelfRegistrationWithApproval",
                    Value = "true",
                    Description = "Erlaubt Registrierung ohne Einladungscode (erfordert Admin-Freischaltung)."
                },
                new SystemSetting
                {
                    Key = "DefaultSupportMultiplier",
                    Value = "1.25",
                    Description = "Aufschlagsfaktor für Supports und Waschverlust bei der Volumenschätzung (z.B. 1.25 = +25%)."
                }
            );
            await db.SaveChangesAsync();
        }

        // 3. Basis-Tags für den Start anlegen (falls noch keine existieren)
        if (!await db.Tags.AnyAsync())
        {
            db.Tags.AddRange(
                new Tag { Name = "32mm Heroic", ColorHex = "#42A5F5" },
                new Tag { Name = "75mm Display", ColorHex = "#AB47BC" },
                new Tag { Name = "Presupported", ColorHex = "#26A69A" },
                new Tag { Name = "Boss", ColorHex = "#EF5350" },
                new Tag { Name = "Terrain / Scenery", ColorHex = "#8D6E63" }
            );
            await db.SaveChangesAsync();
        }
    }
}
