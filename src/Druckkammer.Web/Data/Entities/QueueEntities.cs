using System.ComponentModel.DataAnnotations;

namespace Druckkammer.Web.Data.Entities;

public enum PrinterStatus
{
    IdleReady = 0,
    Printing = 1,
    Maintenance = 2,
    Offline = 3
}

public enum PrintRequestStatus
{
    CartDraft = 0,          // Im Session-Warenkorb (noch nicht abgeschickt)
    RequestedOpen = 1,      // Offen / Gewünscht
    ScheduledOnPlate = 2,   // Eingeplant (Auf Buildplate / Gesliced)
    Printing = 3,           // Druckt gerade
    PostProcessing = 4,     // Nachbearbeitung (Waschen / Supports / UV-Härten)
    ReadyForPickup = 5,     // Fertig / Abholbereit
    CompletedHandedOver = 6,// Übergeben / Archiviert
    OnHoldQuestion = 7,     // Rückfrage / Pausiert
    Rejected = 8            // Abgelehnt
}

public enum RequestPriority
{
    Low = 0,
    Normal = 1,
    HighSessionUrgent = 2   // Dringend für nächste Spielrunde!
}

public enum PrintBatchStatus
{
    Planning = 0,
    SlicedReady = 1,
    Printing = 2,
    PostProcessing = 3,
    Completed = 4
}

public class Printer
{
    public int Id { get; set; }

    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(120)]
    public string ManufacturerModel { get; set; } = string.Empty;

    public PrintingTechnology Technology { get; set; } = PrintingTechnology.ResinSLA;

    public double BuildVolumeXMm { get; set; } = 218;
    public double BuildVolumeYMm { get; set; } = 122;
    public double BuildVolumeZMm { get; set; } = 220;

    public PrinterStatus Status { get; set; } = PrinterStatus.IdleReady;

    [MaxLength(500)]
    public string Notes { get; set; } = string.Empty;
}

public class Material
{
    public int Id { get; set; }

    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(100)]
    public string Brand { get; set; } = string.Empty;

    public PrintingTechnology Technology { get; set; } = PrintingTechnology.ResinSLA;

    [MaxLength(80)]
    public string MaterialType { get; set; } = "ABS-Like Resin";

    [MaxLength(60)]
    public string ColorName { get; set; } = "Standard Grau";

    [MaxLength(20)]
    public string ColorHex { get; set; } = "#78909C";

    public double DensityGPerCm3 { get; set; } = 1.15;

    /// <summary>
    /// Preis pro Liter (Resin) bzw. pro kg (Filament) in EUR
    /// </summary>
    public decimal PricePerLiterOrKg { get; set; } = 24.90m;

    /// <summary>
    /// Aktueller Bestand in ml bzw. g
    /// </summary>
    public double StockAmountMlOrG { get; set; } = 1000;

    public bool IsAvailable { get; set; } = true;
}

/// <summary>
/// Wunsch-Paket / Session-Warenkorb (z.B. "D&D Session 14: Goblin-Höhle")
/// </summary>
public class PrintOrder
{
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(1500)]
    public string Description { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string RequesterUserId { get; set; } = string.Empty;

    [MaxLength(100)]
    public string RequesterDisplayName { get; set; } = string.Empty;

    public DateTime? TargetSessionDate { get; set; }

    public bool IsSubmitted { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SubmittedAt { get; set; }

    public List<PrintRequest> Requests { get; set; } = [];
}

/// <summary>
/// Buildplate / Drucklauf (Bündelung mehrerer Druckwünsche auf eine Druckplatte)
/// </summary>
public class PrintBatch
{
    public int Id { get; set; }

    [Required, MaxLength(180)]
    public string BatchName { get; set; } = string.Empty;

    public int? PrinterId { get; set; }
    public Printer? Printer { get; set; }

    public int? MaterialId { get; set; }
    public Material? Material { get; set; }

    public PrintBatchStatus Status { get; set; } = PrintBatchStatus.Planning;

    public double EstimatedVolumeMl { get; set; }
    public double? ActualSlicerVolumeMl { get; set; }
    public int? EstimatedPrintTimeMinutes { get; set; }

    /// <summary>
    /// Pauschale für IPA, Handschuhe, FEP-Verschleiß und Strom pro Drucklauf
    /// </summary>
    public decimal WearAndConsumablesFlatFee { get; set; } = 1.50m;

    /// <summary>
    /// Im Slicer-Tresor abgelegte Plattendatei (.lys, .ctb, .goo, .gcode)
    /// </summary>
    [MaxLength(500)]
    public string? SlicedPlateFilePath { get; set; }

    [MaxLength(500)]
    public string? SlicedPlateFileName { get; set; }

    [MaxLength(1500)]
    public string OperatorNotes { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public List<PrintRequest> Requests { get; set; } = [];
}

public class PrintRequest
{
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string RequesterUserId { get; set; } = string.Empty;

    [MaxLength(100)]
    public string RequesterDisplayName { get; set; } = string.Empty;

    public int? PrintOrderId { get; set; }
    public PrintOrder? PrintOrder { get; set; }

    public int? ModelItemId { get; set; }
    public ModelItem? ModelItem { get; set; }

    public int? SelectedModelFileId { get; set; }
    public ModelFile? SelectedModelFile { get; set; }

    [MaxLength(500)]
    public string? DirectUploadFilePath { get; set; }

    [MaxLength(260)]
    public string? DirectUploadFileName { get; set; }

    [MaxLength(600)]
    public string? ExternalModelUrl { get; set; }

    [MaxLength(500)]
    public string? ThumbnailUrlOrPath { get; set; }

    public int QuantityRequested { get; set; } = 1;
    public int QuantityCompleted { get; set; }
    public int QuantityFailed { get; set; }

    /// <summary>
    /// Verweist auf den ursprünglichen Wunsch, falls dieser Eintrag durch Teil-Fehldruck-Recovery erzeugt wurde
    /// </summary>
    public int? ParentMisprintRequestId { get; set; }

    public double ScalePercentage { get; set; } = 100.0;
    public double? TargetHeightMm { get; set; }

    public PrintingTechnology PreferredTech { get; set; } = PrintingTechnology.ResinSLA;

    public int? PreferredMaterialId { get; set; }
    public Material? PreferredMaterial { get; set; }

    public RequestPriority Priority { get; set; } = RequestPriority.Normal;
    public DateTime? DueDate { get; set; }

    public PrintRequestStatus Status { get; set; } = PrintRequestStatus.RequestedOpen;

    public int? PrintBatchId { get; set; }
    public PrintBatch? PrintBatch { get; set; }

    public double BaseUnitVolumeCm3 { get; set; }
    public double EstimatedTotalVolumeMl { get; set; }

    public decimal EstimatedCostEur { get; set; }
    public decimal? ActualCostEur { get; set; }
    public bool IsCostSettled { get; set; }

    [MaxLength(2000)]
    public string RequesterNotes { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string OperatorNotes { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public List<PrintRequestComment> Comments { get; set; } = [];
}

public class PrintRequestComment
{
    public int Id { get; set; }

    public int PrintRequestId { get; set; }
    public PrintRequest? PrintRequest { get; set; }

    [MaxLength(100)]
    public string AuthorUserId { get; set; } = string.Empty;

    [MaxLength(100)]
    public string AuthorDisplayName { get; set; } = string.Empty;

    [Required, MaxLength(2000)]
    public string Message { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? AttachmentImagePath { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
