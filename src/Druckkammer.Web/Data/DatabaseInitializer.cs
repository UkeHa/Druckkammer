using System.Security.Claims;
using Druckkammer.Web.Data.Entities;
using Druckkammer.Web.Services;
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
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var storage = scope.ServiceProvider.GetRequiredService<ManagedStorageService>();

        await db.Database.EnsureCreatedAsync();

        // 1. Rollen & granulare Permissions anlegen
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

        // 2. Standard-Benutzer anlegen (Admin/Operator, Andre als GM, Marcel als Kurator)
        string adminEmail = config["Druckkammer:AdminEmail"] ?? "admin@druckkammer.local";
        string adminPassword = config["Druckkammer:AdminInitialPassword"] ?? "Druckkammer!2026";

        var adminUser = await EnsureUserAsync(userManager, adminEmail, "Uke (Maker & Admin)", "#7E57C2", adminPassword, [AppRoles.Admin, AppRoles.Operator]);
        var andreUser = await EnsureUserAsync(userManager, "andre@druckkammer.local", "Andre (Dungeon Master)", "#26A69A", "Druckkammer!2026", [AppRoles.Requester]);
        var marcelUser = await EnsureUserAsync(userManager, "marcel@druckkammer.local", "Marcel (Abo-Kurator)", "#FFA726", "Druckkammer!2026", [AppRoles.Curator]);

        // 3. System Settings & Invite Token
        if (!await db.SystemSettings.AnyAsync())
        {
            db.SystemSettings.AddRange(
                new SystemSetting { Key = "AllowSelfRegistrationWithApproval", Value = "true", Description = "Erlaubt Registrierung ohne Einladungscode (erfordert Admin-Freischaltung)." },
                new SystemSetting { Key = "DefaultSupportMultiplier", Value = "1.25", Description = "Aufschlagsfaktor für Supports und Waschverlust bei der Volumenschätzung (z.B. 1.25 = +25%)." }
            );
        }

        if (!await db.InviteTokens.AnyAsync())
        {
            db.InviteTokens.Add(new InviteToken
            {
                Code = "DND-GRUPPE-2026",
                Description = "Einladung für unsere Tabletop- & D&D-Runde",
                AssignedRole = AppRoles.Requester,
                MaxUses = 10,
                UsedCount = 2,
                ExpiresAt = DateTime.UtcNow.AddMonths(6),
                CreatedByUserId = adminUser.Id
            });
        }

        // 4. Drucker & Materialien
        if (!await db.Printers.AnyAsync())
        {
            db.Printers.AddRange(
                new Printer
                {
                    Name = "Resin-Hauptdrucker (Saturn 4 Ultra)",
                    ManufacturerModel = "Elegoo Saturn 4 Ultra 16K",
                    Technology = PrintingTechnology.ResinSLA,
                    BuildVolumeXMm = 218.8,
                    BuildVolumeYMm = 122.8,
                    BuildVolumeZMm = 220.0,
                    Status = PrinterStatus.IdleReady,
                    Notes = "Tilting Release Vat, ACF-Folie neu im Oktober. Ideal für 32mm & 75mm Minis."
                },
                new Printer
                {
                    Name = "Terrain-Arbeitstier (Bambu P1S)",
                    ManufacturerModel = "Bambu Lab P1S + AMS",
                    Technology = PrintingTechnology.FilamentFDM,
                    BuildVolumeXMm = 256,
                    BuildVolumeYMm = 256,
                    BuildVolumeZMm = 256,
                    Status = PrinterStatus.IdleReady,
                    Notes = "0.4mm Düse für Dungeon-Tiles, Gelände, Würfeltürme und große Bosse."
                }
            );
            await db.SaveChangesAsync();
        }

        if (!await db.Materials.AnyAsync())
        {
            db.Materials.AddRange(
                new Material
                {
                    Name = "ABS-Like Resin Pro 2",
                    Brand = "Sunlu",
                    Technology = PrintingTechnology.ResinSLA,
                    MaterialType = "ABS-Like Resin (Bruchfest)",
                    ColorName = "Tabletop Grau",
                    ColorHex = "#78909C",
                    DensityGPerCm3 = 1.14,
                    PricePerLiterOrKg = 22.90m,
                    StockAmountMlOrG = 2450,
                    IsAvailable = true
                },
                new Material
                {
                    Name = "8K Standard Photopolymer",
                    Brand = "Elegoo",
                    Technology = PrintingTechnology.ResinSLA,
                    MaterialType = "8K High-Detail Resin",
                    ColorName = "Space Grey",
                    ColorHex = "#546E7A",
                    DensityGPerCm3 = 1.18,
                    PricePerLiterOrKg = 27.50m,
                    StockAmountMlOrG = 1200,
                    IsAvailable = true
                },
                new Material
                {
                    Name = "Water-Washable Clear",
                    Brand = "Anycubic",
                    Technology = PrintingTechnology.ResinSLA,
                    MaterialType = "Transparent Resin (Zaubereffekte)",
                    ColorName = "Kristallklar / Eisblau",
                    ColorHex = "#4FC3F7",
                    DensityGPerCm3 = 1.12,
                    PricePerLiterOrKg = 26.00m,
                    StockAmountMlOrG = 750,
                    IsAvailable = true
                },
                new Material
                {
                    Name = "PLA Matte Terrain",
                    Brand = "Bambu Lab",
                    Technology = PrintingTechnology.FilamentFDM,
                    MaterialType = "PLA Matte",
                    ColorName = "Ash Grey",
                    ColorHex = "#9E9E9E",
                    DensityGPerCm3 = 1.24,
                    PricePerLiterOrKg = 19.99m,
                    StockAmountMlOrG = 1800,
                    IsAvailable = true
                }
            );
            await db.SaveChangesAsync();
        }

        // 5. Tags, Collections & Demo-3D-Modelle (mit echten generierten binären STL-Meshes!)
        if (!await db.ModelItems.AnyAsync())
        {
            var tagGoblin = new Tag { Name = "Goblin", ColorHex = "#66BB6A" };
            var tagBoss = new Tag { Name = "Boss", ColorHex = "#EF5350" };
            var tag32mm = new Tag { Name = "32mm Heroic", ColorHex = "#42A5F5" };
            var tag75mm = new Tag { Name = "75mm Display", ColorHex = "#AB47BC" };
            var tagPresup = new Tag { Name = "Presupported", ColorHex = "#26A69A" };
            var tagTerrain = new Tag { Name = "Terrain / Scenery", ColorHex = "#8D6E63" };
            var tagUndead = new Tag { Name = "Untote", ColorHex = "#7E57C2" };
            db.Tags.AddRange(tagGoblin, tagBoss, tag32mm, tag75mm, tagPresup, tagTerrain, tagUndead);
            await db.SaveChangesAsync();

            var colLootRoot = new Collection
            {
                Name = "Loot Studios",
                Slug = "loot-studios",
                CreatorStudio = "Loot Studios",
                Description = "Unsere gesammelten Monats-Bundles von Loot Studios (Fantasy).",
                IsCuratedBundle = true,
                CreatedByUserId = marcelUser.Id
            };
            db.Collections.Add(colLootRoot);
            await db.SaveChangesAsync();

            var colGoblinCave = new Collection
            {
                Name = "Goblin Cave & Blood Moon",
                Slug = "goblin-cave-blood-moon",
                CreatorStudio = "Loot Studios",
                ReleaseIdentifier = "2026-10",
                ParentCollectionId = colLootRoot.Id,
                Description = "Komplettes Goblin-Höhlen-Bundle inkl. Schamanen, Bogenschützen, Spinnenreiter und Höhlen-Altar.",
                IsCuratedBundle = true,
                CreatedByUserId = marcelUser.Id
            };
            var colArchvillain = new Collection
            {
                Name = "Archvillain Games – Nekropole",
                Slug = "archvillain-nekropole",
                CreatorStudio = "Archvillain Games",
                ReleaseIdentifier = "2026-09",
                Description = "Untote Fürsten, Skelettkrieger und nekromantische Bosse.",
                IsCuratedBundle = true,
                CreatedByUserId = marcelUser.Id
            };
            var colAndreCampaign = new Collection
            {
                Name = "Andres D&D Kampagne: Schatten über Phandalin",
                Slug = "andres-dnd-kampagne",
                CreatorStudio = "Kampagnen-Liste",
                Description = "Ausgewählte Minis und Gelände-Teile für unsere nächsten Spielabende.",
                IsCuratedBundle = false,
                CreatedByUserId = andreUser.Id
            };
            db.Collections.AddRange(colGoblinCave, colArchvillain, colAndreCampaign);
            await db.SaveChangesAsync();

            // Modell 1: Goblin Schamane 32mm
            var mGoblinShaman = await CreateSampleModelWithStlAsync(
                storage,
                title: "Goblin Blutmond-Schamane",
                creator: "Loot Studios",
                release: "2026-10",
                collection: colGoblinCave,
                scaleSize: TabletopScaleSize.SmallMedium,
                nativeScaleMm: 32,
                tech: PrintingTechnology.ResinSLA,
                printability: PrintabilityStatus.GreenGood,
                printabilityNotes: "Presupported-Version druckt auf dem Saturn 4 ohne Probleme. Stabspitze beim Abnehmen der Supports vorsichtig in warmem Wasser lösen!",
                description: "Detaillierter Goblin-Schamane mit Totenschädel-Stab und Ritual-Base (32mm).",
                baseRadiusMm: 16f,
                heightMm: 36f,
                segments: 16,
                printCount: 3,
                createdByUserId: marcelUser.Id);
            mGoblinShaman.Tags.AddRange([
                new ModelTagMap { TagId = tagGoblin.Id },
                new ModelTagMap { TagId = tag32mm.Id },
                new ModelTagMap { TagId = tagPresup.Id }
            ]);
            mGoblinShaman.Collections.Add(new ModelCollectionMap { CollectionId = colAndreCampaign.Id });

            // Modell 2: Goblin Kriegshäuptling auf Höhlenspinne (Large 50mm)
            var mSpiderBoss = await CreateSampleModelWithStlAsync(
                storage,
                title: "Snargakh – Goblin-Häuptling auf Riesenspinne",
                creator: "Loot Studios",
                release: "2026-10",
                collection: colGoblinCave,
                scaleSize: TabletopScaleSize.Large,
                nativeScaleMm: 32,
                tech: PrintingTechnology.ResinSLA,
                printability: PrintabilityStatus.YellowCaution,
                printabilityNotes: "Die vorderen Spinnenbeine brauchen zwei zusätzliche Heavy-Supports an der Unterseite, sonst Top!",
                description: "Großes Boss-Modell auf 50mm Fels-Base. Inklusive Supported, Unsupported und separater Base.",
                baseRadiusMm: 25f,
                heightMm: 62f,
                segments: 18,
                printCount: 1,
                createdByUserId: marcelUser.Id);
            mSpiderBoss.Tags.AddRange([
                new ModelTagMap { TagId = tagGoblin.Id },
                new ModelTagMap { TagId = tagBoss.Id },
                new ModelTagMap { TagId = tag32mm.Id },
                new ModelTagMap { TagId = tagPresup.Id }
            ]);
            mSpiderBoss.Collections.Add(new ModelCollectionMap { CollectionId = colAndreCampaign.Id });

            // Modell 3: Knochenfürst Malakor (Huge 75mm Boss)
            var mBoneLord = await CreateSampleModelWithStlAsync(
                storage,
                title: "Malakor der Knochenfürst (Drachen-Lich)",
                creator: "Archvillain Games",
                release: "2026-09",
                collection: colArchvillain,
                scaleSize: TabletopScaleSize.Huge,
                nativeScaleMm: 32,
                tech: PrintingTechnology.ResinSLA,
                printability: PrintabilityStatus.GreenGood,
                printabilityNotes: "Ausgehöhlt (2.0mm Wandstärke) mit Drain-Holes an der Unterseite. Perfekte Passgenauigkeit der Flügel.",
                description: "Epischer Endboss auf 75mm Base für hochstufige D&D-Abenteuer.",
                baseRadiusMm: 37.5f,
                heightMm: 98f,
                segments: 20,
                printCount: 2,
                createdByUserId: marcelUser.Id);
            mBoneLord.Tags.AddRange([
                new ModelTagMap { TagId = tagBoss.Id },
                new ModelTagMap { TagId = tagUndead.Id },
                new ModelTagMap { TagId = tagPresup.Id }
            ]);

            // Modell 4: Runen-Obelisk & Dungeon-Altar (Terrain)
            var mAltar = await CreateSampleModelWithStlAsync(
                storage,
                title: "Runen-Obelisk & Opferaltar (Set)",
                creator: "Loot Studios",
                release: "2026-10",
                collection: colGoblinCave,
                scaleSize: TabletopScaleSize.TerrainScenery,
                nativeScaleMm: 32,
                tech: PrintingTechnology.Both,
                printability: PrintabilityStatus.GreenGood,
                printabilityNotes: "Kann sowohl supportfrei auf dem Bambu P1S (FDM) als auch in Resin gedruckt werden.",
                description: "Geländestück für Ritualräume und Höhlen-Begegnungen.",
                baseRadiusMm: 28f,
                heightMm: 74f,
                segments: 8,
                printCount: 4,
                createdByUserId: marcelUser.Id);
            mAltar.Tags.AddRange([
                new ModelTagMap { TagId = tagTerrain.Id },
                new ModelTagMap { TagId = tag32mm.Id }
            ]);
            mAltar.Collections.Add(new ModelCollectionMap { CollectionId = colAndreCampaign.Id });

            db.ModelItems.AddRange(mGoblinShaman, mSpiderBoss, mBoneLord, mAltar);
            await db.SaveChangesAsync();

            // 6. Beispiel-Wunschpaket & Kanban-Einträge anlegen
            var resinGrey = await db.Materials.FirstAsync(m => m.Technology == PrintingTechnology.ResinSLA);
            var saturnPrinter = await db.Printers.FirstAsync(p => p.Technology == PrintingTechnology.ResinSLA);

            var sessionOrder = new PrintOrder
            {
                Title = "D&D Session #14: Sturm auf die Goblin-Höhle",
                Description = "Bitte bis nächsten Freitag für unseren Spielabend drucken! Grau reicht völlig, ich grundiere am Donnerstag.",
                RequesterUserId = andreUser.Id,
                RequesterDisplayName = andreUser.DisplayName,
                TargetSessionDate = DateTime.UtcNow.AddDays(5),
                IsSubmitted = true,
                SubmittedAt = DateTime.UtcNow.AddHours(-18)
            };
            db.PrintOrders.Add(sessionOrder);
            await db.SaveChangesAsync();

            var batchActive = new PrintBatch
            {
                BatchName = "Saturn 4 – Platte #1 (Goblin-Trupp)",
                PrinterId = saturnPrinter.Id,
                MaterialId = resinGrey.Id,
                Status = PrintBatchStatus.Printing,
                EstimatedVolumeMl = 38.4,
                ActualSlicerVolumeMl = 41.2,
                EstimatedPrintTimeMinutes = 145,
                WearAndConsumablesFlatFee = 1.50m,
                OperatorNotes = "In Lychee 7.1 auf einer Platte arrangiert (0.03mm Layer, 1.9s Exposure).",
                StartedAt = DateTime.UtcNow.AddHours(-1)
            };
            db.PrintBatches.Add(batchActive);
            await db.SaveChangesAsync();

            db.PrintRequests.AddRange(
                new PrintRequest
                {
                    Title = "Goblin Blutmond-Schamane",
                    RequesterUserId = andreUser.Id,
                    RequesterDisplayName = andreUser.DisplayName,
                    PrintOrderId = sessionOrder.Id,
                    ModelItemId = mGoblinShaman.Id,
                    SelectedModelFileId = mGoblinShaman.Files.FirstOrDefault()?.Id,
                    QuantityRequested = 2,
                    ScalePercentage = 100.0,
                    PreferredTech = PrintingTechnology.ResinSLA,
                    PreferredMaterialId = resinGrey.Id,
                    Priority = RequestPriority.HighSessionUrgent,
                    DueDate = sessionOrder.TargetSessionDate,
                    Status = PrintRequestStatus.Printing,
                    PrintBatchId = batchActive.Id,
                    BaseUnitVolumeCm3 = mGoblinShaman.EstimatedVolumeCm3,
                    EstimatedTotalVolumeMl = StlAnalysisService.CalculateEstimatedTotalVolumeMl(mGoblinShaman.EstimatedVolumeCm3, 2, 100),
                    EstimatedCostEur = 0.85m,
                    RequesterNotes = "1x für den Schamanen-Anführer, 1x als Lehrling."
                },
                new PrintRequest
                {
                    Title = "Snargakh – Goblin-Häuptling auf Riesenspinne",
                    RequesterUserId = andreUser.Id,
                    RequesterDisplayName = andreUser.DisplayName,
                    PrintOrderId = sessionOrder.Id,
                    ModelItemId = mSpiderBoss.Id,
                    SelectedModelFileId = mSpiderBoss.Files.FirstOrDefault()?.Id,
                    QuantityRequested = 1,
                    ScalePercentage = 100.0,
                    PreferredTech = PrintingTechnology.ResinSLA,
                    PreferredMaterialId = resinGrey.Id,
                    Priority = RequestPriority.HighSessionUrgent,
                    DueDate = sessionOrder.TargetSessionDate,
                    Status = PrintRequestStatus.RequestedOpen,
                    BaseUnitVolumeCm3 = mSpiderBoss.EstimatedVolumeCm3,
                    EstimatedTotalVolumeMl = StlAnalysisService.CalculateEstimatedTotalVolumeMl(mSpiderBoss.EstimatedVolumeCm3, 1, 100),
                    EstimatedCostEur = 1.20m,
                    RequesterNotes = "Endboss der Höhle! Gerne inkl. der 50mm Fels-Base."
                },
                new PrintRequest
                {
                    Title = "Runen-Obelisk & Opferaltar (Set)",
                    RequesterUserId = andreUser.Id,
                    RequesterDisplayName = andreUser.DisplayName,
                    PrintOrderId = sessionOrder.Id,
                    ModelItemId = mAltar.Id,
                    SelectedModelFileId = mAltar.Files.FirstOrDefault()?.Id,
                    QuantityRequested = 2,
                    ScalePercentage = 100.0,
                    PreferredTech = PrintingTechnology.ResinSLA,
                    PreferredMaterialId = resinGrey.Id,
                    Priority = RequestPriority.Normal,
                    DueDate = sessionOrder.TargetSessionDate,
                    Status = PrintRequestStatus.ReadyForPickup,
                    QuantityCompleted = 2,
                    BaseUnitVolumeCm3 = mAltar.EstimatedVolumeCm3,
                    EstimatedTotalVolumeMl = StlAnalysisService.CalculateEstimatedTotalVolumeMl(mAltar.EstimatedVolumeCm3, 2, 100),
                    EstimatedCostEur = 1.60m,
                    CompletedAt = DateTime.UtcNow.AddHours(-4),
                    RequesterNotes = "Zwei Säulen links und rechts vom Eingang.",
                    OperatorNotes = "Fertig gewaschen und gehärtet, liegen in deiner Box!"
                },
                new PrintRequest
                {
                    Title = "Malakor der Knochenfürst (75mm Vitrinen-Scale)",
                    RequesterUserId = marcelUser.Id,
                    RequesterDisplayName = marcelUser.DisplayName,
                    ModelItemId = mBoneLord.Id,
                    SelectedModelFileId = mBoneLord.Files.FirstOrDefault()?.Id,
                    QuantityRequested = 1,
                    ScalePercentage = 150.0,
                    PreferredTech = PrintingTechnology.ResinSLA,
                    PreferredMaterialId = resinGrey.Id,
                    Priority = RequestPriority.Normal,
                    Status = PrintRequestStatus.RequestedOpen,
                    BaseUnitVolumeCm3 = mBoneLord.EstimatedVolumeCm3,
                    EstimatedTotalVolumeMl = StlAnalysisService.CalculateEstimatedTotalVolumeMl(mBoneLord.EstimatedVolumeCm3, 1, 150.0),
                    EstimatedCostEur = 3.90m,
                    RequesterNotes = "Einmal auf 150% vergrößert zum Bemalen für die Vitrine, eilt überhaupt nicht!"
                }
            );
            await db.SaveChangesAsync();
        }
    }

    private static async Task<ApplicationUser> EnsureUserAsync(
        UserManager<ApplicationUser> userManager,
        string email,
        string displayName,
        string avatarColor,
        string password,
        string[] roles)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                DisplayName = displayName,
                AvatarColor = avatarColor,
                EmailConfirmed = true,
                IsApproved = true
            };
            await userManager.CreateAsync(user, password);
            await userManager.AddToRolesAsync(user, roles);
        }
        return user;
    }

    private static async Task<ModelItem> CreateSampleModelWithStlAsync(
        ManagedStorageService storage,
        string title,
        string creator,
        string release,
        Collection collection,
        TabletopScaleSize scaleSize,
        double nativeScaleMm,
        PrintingTechnology tech,
        PrintabilityStatus printability,
        string printabilityNotes,
        string description,
        float baseRadiusMm,
        float heightMm,
        int segments,
        int printCount,
        string createdByUserId)
    {
        var model = new ModelItem
        {
            Title = title,
            Slug = ManagedStorageService.Slugify(title),
            CreatorStudio = creator,
            ReleaseMonth = release,
            ScaleSize = scaleSize,
            NativeScaleMm = nativeScaleMm,
            RecommendedTech = tech,
            Printability = printability,
            PrintabilityNotes = printabilityNotes,
            Description = description,
            PrintCount = printCount,
            CreatedByUserId = createdByUserId
        };

        // 1. Presupported STL generieren & speichern
        byte[] presupBytes = StlAnalysisService.GenerateSampleBinaryStl(baseRadiusMm, heightMm, segments);
        using (var ms1 = new MemoryStream(presupBytes))
        {
            var storedPresup = await storage.StoreModelFileStreamAsync(
                ms1,
                $"{model.Slug}_presupported_32mm.stl",
                creator,
                collection.Name,
                title,
                "Presupported",
                checkDuplicateInDb: false);
            ManagedStorageService.AttachStoredFileToModel(model, $"{model.Slug}_presupported_32mm.stl", storedPresup, collection);
        }

        // 2. Unsupported STL generieren & speichern
        byte[] unsupBytes = StlAnalysisService.GenerateSampleBinaryStl(baseRadiusMm * 0.95f, heightMm, segments + 2);
        using (var ms2 = new MemoryStream(unsupBytes))
        {
            var storedUnsup = await storage.StoreModelFileStreamAsync(
                ms2,
                $"{model.Slug}_unsupported.stl",
                creator,
                collection.Name,
                title,
                "Unsupported",
                checkDuplicateInDb: false);
            ManagedStorageService.AttachStoredFileToModel(model, $"{model.Slug}_unsupported.stl", storedUnsup, collection);
        }

        // 3. Separate Base STL generieren & speichern
        byte[] baseBytes = StlAnalysisService.GenerateSampleBinaryStl(baseRadiusMm, 4.2f, 24);
        using (var ms3 = new MemoryStream(baseBytes))
        {
            var storedBase = await storage.StoreModelFileStreamAsync(
                ms3,
                $"{model.Slug}_scenic_base.stl",
                creator,
                collection.Name,
                title,
                "Bases",
                checkDuplicateInDb: false);
            ManagedStorageService.AttachStoredFileToModel(model, $"{model.Slug}_scenic_base.stl", storedBase, collection);
        }

        ManagedStorageService.FinalizeModelMetrics(model);
        model.Collections.Add(new ModelCollectionMap { CollectionId = collection.Id });
        return model;
    }
}
