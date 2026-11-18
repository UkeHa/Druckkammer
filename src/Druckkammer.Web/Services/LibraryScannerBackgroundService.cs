using Druckkammer.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Druckkammer.Web.Services;

public class LibraryScannerBackgroundService(
    IServiceScopeFactory scopeFactory,
    ManagedStorageService storageService,
    ILogger<LibraryScannerBackgroundService> logger) : BackgroundService
{
    public DateTime? LastAutoScanAtUtc { get; private set; }
    public FolderScanSummary? LastAutoScanSummary { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Kurze Startverzögerung nach Anwendungsstart
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            int intervalMinutes = 60;
            bool isEnabled = false;

            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var settings = await db.SystemSettings
                    .AsNoTracking()
                    .Where(s => s.Key == "AutoScanEnabled" || s.Key == "AutoScanIntervalMinutes")
                    .ToDictionaryAsync(s => s.Key, s => s.Value, stoppingToken);

                if (settings.TryGetValue("AutoScanEnabled", out var enabledStr))
                {
                    isEnabled = enabledStr.Equals("true", StringComparison.OrdinalIgnoreCase);
                }

                if (settings.TryGetValue("AutoScanIntervalMinutes", out var intervalStr) &&
                    int.TryParse(intervalStr, out int parsedInterval) && parsedInterval >= 1)
                {
                    intervalMinutes = parsedInterval;
                }

                if (isEnabled)
                {
                    bool shouldRun = !LastAutoScanAtUtc.HasValue ||
                                     (DateTime.UtcNow - LastAutoScanAtUtc.Value).TotalMinutes >= intervalMinutes;
                    if (shouldRun)
                    {
                        logger.LogInformation("Starte automatischen Hintergrund-Scan von {ImportDir}...", storageService.ImportDirectory);
                        LastAutoScanSummary = await storageService.ScanImportDirectoryAsync("system-auto-scanner");
                        LastAutoScanAtUtc = DateTime.UtcNow;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Fehler im automatischen Hintergrund-Ordnerscanner.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
