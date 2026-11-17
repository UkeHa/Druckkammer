using System.Text;
using Druckkammer.Web.Data;
using Druckkammer.Web.Data.Entities;
using Druckkammer.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Druckkammer.Tests;

public class DruckkammerTests
{
    [Fact]
    public void BinaryStlAnalysis_ShouldCalculateVolumeAndBoundingBox()
    {
        var service = new StlAnalysisService();
        byte[] stlBytes = StlAnalysisService.GenerateSampleBinaryStl(baseRadiusMm: 16f, totalHeightMm: 36f, segments: 16);

        using var ms = new MemoryStream(stlBytes);
        var result = service.AnalyzeStlStream(ms);

        Assert.True(result.IsValid);
        Assert.True(result.VolumeCm3 > 1.0 && result.VolumeCm3 < 20.0);
        Assert.InRange(result.DimXMm, 30.0, 33.0);
        Assert.InRange(result.DimYMm, 30.0, 33.0);
        Assert.Equal(36.0, result.DimZMm, 1);
        Assert.True(result.TriangleCount > 40);
    }

    [Fact]
    public void AsciiStlAnalysis_UnitCube10Mm_ShouldEqualOneCm3()
    {
        // A 10mm x 10mm x 10mm cube has volume 1000 mm³ = 1.00 cm³ (1 ml)
        string asciiCube = """
            solid cube
            facet normal 0 0 -1
              outer loop
                vertex 0 0 0
                vertex 0 10 0
                vertex 10 10 0
              endloop
            endfacet
            facet normal 0 0 -1
              outer loop
                vertex 0 0 0
                vertex 10 10 0
                vertex 10 0 0
              endloop
            endfacet
            facet normal 0 0 1
              outer loop
                vertex 0 0 10
                vertex 10 10 10
                vertex 0 10 10
              endloop
            endfacet
            facet normal 0 0 1
              outer loop
                vertex 0 0 10
                vertex 10 0 10
                vertex 10 10 10
              endloop
            endfacet
            facet normal 0 -1 0
              outer loop
                vertex 0 0 0
                vertex 10 0 0
                vertex 10 0 10
              endloop
            endfacet
            facet normal 0 -1 0
              outer loop
                vertex 0 0 0
                vertex 10 0 10
                vertex 0 0 10
              endloop
            endfacet
            facet normal 0 1 0
              outer loop
                vertex 0 10 0
                vertex 10 10 10
                vertex 10 10 0
              endloop
            endfacet
            facet normal 0 1 0
              outer loop
                vertex 0 10 0
                vertex 0 10 10
                vertex 10 10 10
              endloop
            endfacet
            facet normal -1 0 0
              outer loop
                vertex 0 0 0
                vertex 0 0 10
                vertex 0 10 10
              endloop
            endfacet
            facet normal -1 0 0
              outer loop
                vertex 0 0 0
                vertex 0 10 10
                vertex 0 10 0
              endloop
            endfacet
            facet normal 1 0 0
              outer loop
                vertex 10 0 0
                vertex 10 10 0
                vertex 10 10 10
              endloop
            endfacet
            facet normal 1 0 0
              outer loop
                vertex 10 0 0
                vertex 10 10 10
                vertex 10 0 10
              endloop
            endfacet
            endsolid cube
            """;

        var service = new StlAnalysisService();
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(asciiCube));
        var res = service.AnalyzeStlStream(ms);

        Assert.True(res.IsValid);
        Assert.Equal(1.0, res.VolumeCm3, 2);
        Assert.Equal(10.0, res.DimXMm, 1);
        Assert.Equal(10.0, res.DimYMm, 1);
        Assert.Equal(10.0, res.DimZMm, 1);
        Assert.Equal(12, res.TriangleCount);
    }

    [Fact]
    public void ScaleAndCostCalculator_ShouldScaleCubically()
    {
        // 28mm -> 32mm should be 114.3%
        double scalePct = StlAnalysisService.ConvertTabletopScalePercentage(28, 32);
        Assert.Equal(114.3, scalePct);

        // Doubling scale (200%) should multiply volume by 8 (2³ = 8)
        double vol100 = StlAnalysisService.CalculateEstimatedTotalVolumeMl(10.0, quantity: 1, scalePercentage: 100.0, supportMultiplier: 1.0);
        double vol200 = StlAnalysisService.CalculateEstimatedTotalVolumeMl(10.0, quantity: 1, scalePercentage: 200.0, supportMultiplier: 1.0);
        Assert.Equal(10.0, vol100);
        Assert.Equal(80.0, vol200);

        // 100 ml of Resin at 25.00 EUR/Liter = 2.50 EUR
        decimal cost = StlAnalysisService.CalculateEstimatedMaterialCostEur(100.0, 25.00m);
        Assert.Equal(2.50m, cost);
    }

    [Fact]
    public void FileClassification_ShouldRecognizeSupportedBasesAndSlicerFiles()
    {
        var (cat1, _) = ManagedStorageService.ClassifyFile("goblin_shaman_presupported.stl", "Presupported");
        var (cat2, _) = ManagedStorageService.ClassifyFile("goblin_shaman_raw.stl", "Unsupported");
        var (cat3, _) = ManagedStorageService.ClassifyFile("scenic_base_32mm.stl", "Bases");
        var (cat4, _) = ManagedStorageService.ClassifyFile("goblin_plate.lys", "Lychee");
        var (cat5, _) = ManagedStorageService.ClassifyFile("october_release_poster.jpg", "Renders");

        Assert.Equal(ModelFileCategory.PresupportedMesh, cat1);
        Assert.Equal(ModelFileCategory.UnsupportedMesh, cat2);
        Assert.Equal(ModelFileCategory.BaseMesh, cat3);
        Assert.Equal(ModelFileCategory.SlicerProject, cat4);
        Assert.Equal(ModelFileCategory.ReleasePoster, cat5);
    }

    [Fact]
    public void OpenGraphParser_ShouldExtractMetadata()
    {
        string html = """
            <html>
            <head>
                <meta property="og:title" content="Goblin Warboss 32mm &amp; 75mm" />
                <meta property="og:description" content="Pre-supported fantasy miniature for tabletop RPGs." />
                <meta property="og:image" content="https://cdn.example.com/goblin.jpg" />
                <meta property="og:site_name" content="MyMiniFactory" />
            </head>
            <body></body>
            </html>
            """;

        var meta = OpenGraphService.ParseOpenGraphHtml(html, "https://www.myminifactory.com/object/123");
        Assert.True(meta.IsSuccess);
        Assert.Equal("Goblin Warboss 32mm & 75mm", meta.Title);
        Assert.Equal("Pre-supported fantasy miniature for tabletop RPGs.", meta.Description);
        Assert.Equal("https://cdn.example.com/goblin.jpg", meta.ImageUrl);
        Assert.Equal("MyMiniFactory", meta.SiteName);
    }

    [Fact]
    public async Task PartialMisprintRecovery_ShouldRequeueFailedItemsAutomatically()
    {
        var services = new ServiceCollection();
        string dbName = $"DruckkammerTest_{Guid.NewGuid():N}.db";
        string tempDbPath = Path.Combine(Path.GetTempPath(), dbName);

        try
        {
            services.AddDbContextFactory<ApplicationDbContext>(opt => opt.UseSqlite($"Data Source={tempDbPath}"));
            services.AddHttpClient();
            services.AddLogging();
            services.AddScoped<NotificationService>();
            services.AddScoped<PrintQueueService>();

            var provider = services.BuildServiceProvider();
            var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
            }

            var queueService = provider.GetRequiredService<PrintQueueService>();

            // 1. Create a request for 8 Skeletons
            var req = await queueService.CreatePrintRequestAsync(new PrintRequest
            {
                Title = "Skelett-Krieger",
                RequesterUserId = "andre",
                RequesterDisplayName = "Andre (GM)",
                QuantityRequested = 8,
                BaseUnitVolumeCm3 = 5.0
            }, addToSessionCart: false);

            // 2. Bundle onto a Buildplate
            var batch = await queueService.CreateBatchFromRequestsAsync("Platte #1", null, null, [req.Id]);
            Assert.Equal(PrintBatchStatus.SlicedReady, batch.Status);

            // 3. Complete batch with Partial Misprint: 6 succeeded, 2 failed
            var reQueued = await queueService.CompleteBatchWithMisprintRecoveryAsync(
                batch.Id,
                [new MisprintItemResult(req.Id, SucceededQuantity: 6, FailedQuantity: 2)],
                actualSlicerVolumeMl: 48.5);

            Assert.Single(reQueued);
            Assert.Equal(2, reQueued[0].QuantityRequested);
            Assert.Equal(PrintRequestStatus.RequestedOpen, reQueued[0].Status);
            Assert.Equal(req.Id, reQueued[0].ParentMisprintRequestId);
            Assert.Equal(RequestPriority.HighSessionUrgent, reQueued[0].Priority);
        }
        finally
        {
            if (File.Exists(tempDbPath))
                File.Delete(tempDbPath);
        }
    }
}
