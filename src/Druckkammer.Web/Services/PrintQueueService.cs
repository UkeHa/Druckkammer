using Druckkammer.Web.Data;
using Druckkammer.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Druckkammer.Web.Services;

public record MisprintItemResult(int RequestId, int SucceededQuantity, int FailedQuantity);

public class PrintQueueService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    NotificationService notificationService)
{
    /// <summary>
    /// Holt oder erstellt den aktuellen offenen Session-Warenkorb (PrintOrder mit IsSubmitted == false) eines Benutzers.
    /// </summary>
    public async Task<PrintOrder> GetOrCreateActiveCartAsync(string userId, string userDisplayName)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var cart = await db.PrintOrders
            .Include(o => o.Requests)
                .ThenInclude(r => r.ModelItem)
            .Include(o => o.Requests)
                .ThenInclude(r => r.SelectedModelFile)
            .Include(o => o.Requests)
                .ThenInclude(r => r.PreferredMaterial)
            .FirstOrDefaultAsync(o => o.RequesterUserId == userId && !o.IsSubmitted);

        if (cart is not null)
            return cart;

        cart = new PrintOrder
        {
            Title = $"Session-Paket ({DateTime.Now:dd.MM.})",
            RequesterUserId = userId,
            RequesterDisplayName = userDisplayName,
            TargetSessionDate = DateTime.UtcNow.AddDays(7),
            IsSubmitted = false
        };
        db.PrintOrders.Add(cart);
        await db.SaveChangesAsync();
        return cart;
    }

    public async Task<int> GetActiveCartItemCountAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.PrintRequests
            .Where(r => r.RequesterUserId == userId && r.Status == PrintRequestStatus.CartDraft)
            .SumAsync(r => r.QuantityRequested);
    }

    /// <summary>
    /// Fügt einen neuen Druckwunsch entweder direkt ins Kanban-Board (RequestedOpen) oder in den Session-Warenkorb (CartDraft) ein.
    /// </summary>
    public async Task<PrintRequest> CreatePrintRequestAsync(PrintRequest request, bool addToSessionCart)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        decimal pricePerLiterOrKg = 24.90m;
        double density = 1.15;
        if (request.PreferredMaterialId.HasValue)
        {
            var mat = await db.Materials.FindAsync(request.PreferredMaterialId.Value);
            if (mat is not null)
            {
                pricePerLiterOrKg = mat.PricePerLiterOrKg;
                density = mat.DensityGPerCm3;
                request.PreferredTech = mat.Technology;
            }
        }

        if (request.BaseUnitVolumeCm3 <= 0 && request.ModelItemId.HasValue)
        {
            var model = await db.ModelItems.FindAsync(request.ModelItemId.Value);
            if (model is not null)
            {
                request.BaseUnitVolumeCm3 = model.EstimatedVolumeCm3 > 0 ? model.EstimatedVolumeCm3 : 6.5;
                if (string.IsNullOrWhiteSpace(request.ThumbnailUrlOrPath))
                    request.ThumbnailUrlOrPath = model.ThumbnailPath;
            }
        }
        if (request.BaseUnitVolumeCm3 <= 0)
            request.BaseUnitVolumeCm3 = 6.0; // Fallback für externe Links ohne bekannte STL

        request.EstimatedTotalVolumeMl = StlAnalysisService.CalculateEstimatedTotalVolumeMl(
            request.BaseUnitVolumeCm3,
            request.QuantityRequested,
            request.ScalePercentage);

        request.EstimatedCostEur = StlAnalysisService.CalculateEstimatedMaterialCostEur(
            request.EstimatedTotalVolumeMl,
            pricePerLiterOrKg,
            density,
            request.PreferredTech);

        if (addToSessionCart)
        {
            var cart = await db.PrintOrders.FirstOrDefaultAsync(o => o.RequesterUserId == request.RequesterUserId && !o.IsSubmitted);
            if (cart is null)
            {
                cart = new PrintOrder
                {
                    Title = $"Session-Paket ({DateTime.Now:dd.MM.})",
                    RequesterUserId = request.RequesterUserId,
                    RequesterDisplayName = request.RequesterDisplayName,
                    TargetSessionDate = DateTime.UtcNow.AddDays(7),
                    IsSubmitted = false
                };
                db.PrintOrders.Add(cart);
                await db.SaveChangesAsync();
            }

            request.PrintOrderId = cart.Id;
            request.Status = PrintRequestStatus.CartDraft;
        }
        else
        {
            request.Status = PrintRequestStatus.RequestedOpen;
        }

        db.PrintRequests.Add(request);
        await db.SaveChangesAsync();

        if (!addToSessionCart)
        {
            await notificationService.NotifyNewRequestAsync(
                request.Title,
                request.RequesterDisplayName,
                request.QuantityRequested,
                request.EstimatedTotalVolumeMl);
        }

        return request;
    }

    /// <summary>
    /// Schickt den gesamten Session-Warenkorb als zusammengehöriges Wunsch-Paket ans Kanban-Board.
    /// </summary>
    public async Task<PrintOrder?> SubmitSessionCartAsync(
        int printOrderId,
        string packageTitle,
        string description,
        DateTime? targetSessionDate,
        RequestPriority priority)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var order = await db.PrintOrders
            .Include(o => o.Requests)
            .FirstOrDefaultAsync(o => o.Id == printOrderId);

        if (order is null || order.Requests.Count == 0)
            return null;

        order.Title = string.IsNullOrWhiteSpace(packageTitle) ? order.Title : packageTitle.Trim();
        order.Description = description?.Trim() ?? string.Empty;
        order.TargetSessionDate = targetSessionDate;
        order.IsSubmitted = true;
        order.SubmittedAt = DateTime.UtcNow;

        foreach (var req in order.Requests)
        {
            req.Status = PrintRequestStatus.RequestedOpen;
            req.DueDate = targetSessionDate;
            req.Priority = priority;
            req.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();

        double totalVol = order.Requests.Sum(r => r.EstimatedTotalVolumeMl);
        int totalQty = order.Requests.Sum(r => r.QuantityRequested);
        await notificationService.NotifyNewRequestAsync(order.Title, order.RequesterDisplayName, totalQty, totalVol);

        return order;
    }

    /// <summary>
    /// Bündelt mehrere offene Druckwünsche auf eine gemeinsame Buildplate (PrintBatch).
    /// </summary>
    public async Task<PrintBatch> CreateBatchFromRequestsAsync(
        string batchName,
        int? printerId,
        int? materialId,
        List<int> requestIds,
        decimal wearFlatFee = 1.50m,
        string operatorNotes = "")
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var requests = await db.PrintRequests
            .Where(r => requestIds.Contains(r.Id))
            .ToListAsync();

        var batch = new PrintBatch
        {
            BatchName = batchName,
            PrinterId = printerId,
            MaterialId = materialId,
            Status = PrintBatchStatus.SlicedReady,
            EstimatedVolumeMl = Math.Round(requests.Sum(r => r.EstimatedTotalVolumeMl), 2),
            WearAndConsumablesFlatFee = wearFlatFee,
            OperatorNotes = operatorNotes
        };

        db.PrintBatches.Add(batch);
        await db.SaveChangesAsync();

        foreach (var req in requests)
        {
            req.PrintBatchId = batch.Id;
            req.Status = PrintRequestStatus.ScheduledOnPlate;
            if (materialId.HasValue && !req.PreferredMaterialId.HasValue)
                req.PreferredMaterialId = materialId;
            req.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
        return batch;
    }

    /// <summary>
    /// Aktualisiert den Status einer gesamten Buildplate und schiebt alle enthaltenen Druckwünsche im Kanban mit.
    /// </summary>
    public async Task UpdateBatchStatusAsync(int batchId, PrintBatchStatus newBatchStatus)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var batch = await db.PrintBatches
            .Include(b => b.Requests)
            .Include(b => b.Printer)
            .FirstOrDefaultAsync(b => b.Id == batchId);

        if (batch is null) return;

        batch.Status = newBatchStatus;
        if (newBatchStatus == PrintBatchStatus.Printing)
        {
            batch.StartedAt ??= DateTime.UtcNow;
            if (batch.Printer is not null)
                batch.Printer.Status = PrinterStatus.Printing;
        }
        else if (newBatchStatus is PrintBatchStatus.PostProcessing or PrintBatchStatus.Completed)
        {
            if (batch.Printer is not null && batch.Printer.Status == PrinterStatus.Printing)
                batch.Printer.Status = PrinterStatus.IdleReady;
        }

        PrintRequestStatus targetReqStatus = newBatchStatus switch
        {
            PrintBatchStatus.Planning or PrintBatchStatus.SlicedReady => PrintRequestStatus.ScheduledOnPlate,
            PrintBatchStatus.Printing => PrintRequestStatus.Printing,
            PrintBatchStatus.PostProcessing => PrintRequestStatus.PostProcessing,
            PrintBatchStatus.Completed => PrintRequestStatus.ReadyForPickup,
            _ => PrintRequestStatus.ScheduledOnPlate
        };

        if (newBatchStatus == PrintBatchStatus.Completed)
            batch.CompletedAt = DateTime.UtcNow;

        foreach (var req in batch.Requests)
        {
            req.Status = targetReqStatus;
            req.UpdatedAt = DateTime.UtcNow;
            if (targetReqStatus == PrintRequestStatus.ReadyForPickup)
            {
                req.QuantityCompleted = req.QuantityRequested;
                req.CompletedAt = DateTime.UtcNow;
            }
        }

        await db.SaveChangesAsync();

        foreach (var req in batch.Requests)
        {
            await notificationService.NotifyStatusChangedAsync(req.Title, req.RequesterDisplayName, targetReqStatus);
        }
    }

    /// <summary>
    /// Teil-Fehldruck-Workflow (Partial Misprint Recovery):
    /// Schließt eine Buildplate ab, verbucht die erfolgreichen Exemplare und reiht fehlgeschlagene Figuren automatisch
    /// als Nachdruck-Wünsche wieder in die offene Warteschlange (RequestedOpen) für die nächste Buildplate ein.
    /// </summary>
    public async Task<List<PrintRequest>> CompleteBatchWithMisprintRecoveryAsync(
        int batchId,
        List<MisprintItemResult> itemResults,
        double? actualSlicerVolumeMl = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var batch = await db.PrintBatches
            .Include(b => b.Requests)
                .ThenInclude(r => r.ModelItem)
            .Include(b => b.Printer)
            .Include(b => b.Material)
            .FirstOrDefaultAsync(b => b.Id == batchId);

        if (batch is null)
            return [];

        batch.Status = PrintBatchStatus.Completed;
        batch.CompletedAt = DateTime.UtcNow;
        if (actualSlicerVolumeMl.HasValue && actualSlicerVolumeMl.Value > 0)
            batch.ActualSlicerVolumeMl = actualSlicerVolumeMl.Value;

        if (batch.Printer is not null)
            batch.Printer.Status = PrinterStatus.IdleReady;

        // Materialbestand reduzieren
        double consumedVolume = batch.ActualSlicerVolumeMl ?? batch.EstimatedVolumeMl;
        if (batch.Material is not null && consumedVolume > 0)
        {
            batch.Material.StockAmountMlOrG = Math.Max(0, Math.Round(batch.Material.StockAmountMlOrG - consumedVolume, 1));
        }

        var reQueuedRequests = new List<PrintRequest>();

        foreach (var req in batch.Requests)
        {
            var result = itemResults.FirstOrDefault(x => x.RequestId == req.Id);
            int succeeded = result is not null
                ? Math.Clamp(result.SucceededQuantity, 0, req.QuantityRequested)
                : req.QuantityRequested;
            int failed = result is not null
                ? Math.Max(0, req.QuantityRequested - succeeded)
                : 0;

            req.QuantityCompleted = succeeded;
            req.QuantityFailed += failed;
            req.UpdatedAt = DateTime.UtcNow;

            if (succeeded > 0 && req.ModelItem is not null)
            {
                req.ModelItem.PrintCount += succeeded;
            }

            if (succeeded > 0)
            {
                req.Status = PrintRequestStatus.ReadyForPickup;
                req.CompletedAt = DateTime.UtcNow;
            }
            else
            {
                // Komplett fehlgeschlagen auf dieser Platte -> dieser Eintrag wird als Teil-Fehldruck archiviert und neu eingereiht
                req.Status = PrintRequestStatus.Rejected;
                req.OperatorNotes = $"{req.OperatorNotes} [Fehldruck auf Platte '{batch.BatchName}' -> automatisch neu eingereiht]".Trim();
            }

            if (failed > 0)
            {
                double unitVol = req.BaseUnitVolumeCm3 > 0 ? req.BaseUnitVolumeCm3 : 6.0;
                double retryVolume = StlAnalysisService.CalculateEstimatedTotalVolumeMl(unitVol, failed, req.ScalePercentage);
                decimal pricePerLiter = batch.Material?.PricePerLiterOrKg ?? 24.90m;
                decimal retryCost = StlAnalysisService.CalculateEstimatedMaterialCostEur(retryVolume, pricePerLiter, batch.Material?.DensityGPerCm3 ?? 1.15, req.PreferredTech);

                var retryRequest = new PrintRequest
                {
                    Title = $"{req.Title} (Nachdruck {failed}x)",
                    RequesterUserId = req.RequesterUserId,
                    RequesterDisplayName = req.RequesterDisplayName,
                    PrintOrderId = req.PrintOrderId,
                    ModelItemId = req.ModelItemId,
                    SelectedModelFileId = req.SelectedModelFileId,
                    DirectUploadFilePath = req.DirectUploadFilePath,
                    DirectUploadFileName = req.DirectUploadFileName,
                    ExternalModelUrl = req.ExternalModelUrl,
                    ThumbnailUrlOrPath = req.ThumbnailUrlOrPath,
                    QuantityRequested = failed,
                    ScalePercentage = req.ScalePercentage,
                    TargetHeightMm = req.TargetHeightMm,
                    PreferredTech = req.PreferredTech,
                    PreferredMaterialId = req.PreferredMaterialId ?? batch.MaterialId,
                    Priority = RequestPriority.HighSessionUrgent,
                    DueDate = req.DueDate,
                    Status = PrintRequestStatus.RequestedOpen,
                    ParentMisprintRequestId = req.Id,
                    BaseUnitVolumeCm3 = req.BaseUnitVolumeCm3,
                    EstimatedTotalVolumeMl = retryVolume,
                    EstimatedCostEur = retryCost,
                    RequesterNotes = req.RequesterNotes,
                    OperatorNotes = $"Automatischer Nachdruck aus Teil-Fehldruck von Platte '{batch.BatchName}' ({failed} von {req.QuantityRequested} fehlgeschlagen)."
                };

                db.PrintRequests.Add(retryRequest);
                reQueuedRequests.Add(retryRequest);
            }
        }

        await db.SaveChangesAsync();

        foreach (var req in batch.Requests.Where(r => r.Status == PrintRequestStatus.ReadyForPickup))
        {
            await notificationService.NotifyStatusChangedAsync(req.Title, req.RequesterDisplayName, req.Status);
        }

        return reQueuedRequests;
    }
}
