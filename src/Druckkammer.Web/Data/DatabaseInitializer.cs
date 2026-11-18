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

        // Sicherstellen, dass neue Tabellen (z.B. ExposureProfiles bei Upgrade von v0.1/v0.2) existieren
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ExposureProfiles" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_ExposureProfiles" PRIMARY KEY AUTOINCREMENT,
                "PrinterId" INTEGER NOT NULL,
                "MaterialId" INTEGER NOT NULL,
                "ProfileName" TEXT NOT NULL,
                "LayerHeightMm" REAL NOT NULL,
                "NormalExposureSeconds" REAL NOT NULL,
                "BottomExposureSeconds" REAL NOT NULL,
                "BottomLayersCount" INTEGER NOT NULL,
                "LiftDistanceMm" REAL NOT NULL,
                "LiftSpeedMmPerMin" REAL NOT NULL,
                "RetractSpeedMmPerMin" REAL NOT NULL,
                "LightOffDelaySeconds" REAL NOT NULL,
                "Notes" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                CONSTRAINT "FK_ExposureProfiles_Printers_PrinterId" FOREIGN KEY ("PrinterId") REFERENCES "Printers" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_ExposureProfiles_Materials_MaterialId" FOREIGN KEY ("MaterialId") REFERENCES "Materials" ("Id") ON DELETE CASCADE
            );
            """);

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
                },
                new SystemSetting
                {
                    Key = "AutoScanEnabled",
                    Value = "false",
                    Description = "Aktiviert den automatischen Hintergrund-Ordnerscanner für /import."
                },
                new SystemSetting
                {
                    Key = "AutoScanIntervalMinutes",
                    Value = "60",
                    Description = "Intervall in Minuten für den automatischen Hintergrund-Scan von /import."
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
                new Tag { Name = "Terrain / Scenery", ColorHex = "#8D6E63" },
                new Tag { Name = "Goblin", ColorHex = "#66BB6A" },
                new Tag { Name = "Untote", ColorHex = "#7E57C2" }
            );
            await db.SaveChangesAsync();
        }
    }
}
