using System.ComponentModel.DataAnnotations;

namespace Druckkammer.Web.Data.Entities;

public enum TabletopScaleSize
{
    Unknown = 0,
    Tiny = 1,          // < 20mm Base / Winzig
    SmallMedium = 2,   // 25-32mm Base (Standard Held / Goblin / Mensch)
    Large = 3,         // 50mm Base (Oger / Kavallerie / Troll)
    Huge = 4,          // 75mm Base (Riese / Junger Drache)
    Gargantuan = 5,    // 100mm+ Base (Alter Drache / Koloss / Boss)
    TerrainScenery = 6,// Gelände, Gebäude, Props
    BustDisplay = 7,   // Büste / 75mm Vitrinenfigur
    FunctionalOther = 8// Funktionaler Druck / Zubehör
}

public enum ModelFileCategory
{
    UnsupportedMesh = 0,
    PresupportedMesh = 1,
    BaseMesh = 2,
    PartMesh = 3,
    SlicerProject = 4, // .lys, .lyt, .chitubox
    SlicedPlate = 5,   // .ctb, .goo, .gcode (Slicer-Tresor)
    PreviewImage = 6,  // .png, .jpg, .webp
    ReleasePoster = 7, // Bundle-Poster / Übersicht
    Documentation = 8  // .pdf, .txt, .md
}

public enum PrintabilityStatus
{
    Untested = 0,
    GreenGood = 1,     // Presupports top / druckt problemlos
    YellowCaution = 2, // Vorsicht bei dünnen Details / leichte Nacharbeit
    RedNeedsWork = 3   // Supports fehlerhaft / muss manuell gestützt werden
}

public enum PrintingTechnology
{
    ResinSLA = 0,
    FilamentFDM = 1,
    Both = 2
}

public class ModelItem
{
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(220)]
    public string Slug { get; set; } = string.Empty;

    [MaxLength(4000)]
    public string Description { get; set; } = string.Empty;

    [MaxLength(150)]
    public string CreatorStudio { get; set; } = string.Empty;

    [MaxLength(50)]
    public string ReleaseMonth { get; set; } = string.Empty;

    [MaxLength(600)]
    public string? ExternalSourceUrl { get; set; }

    [MaxLength(500)]
    public string? ThumbnailPath { get; set; }

    public TabletopScaleSize ScaleSize { get; set; } = TabletopScaleSize.SmallMedium;

    public double NativeScaleMm { get; set; } = 32.0;

    public PrintingTechnology RecommendedTech { get; set; } = PrintingTechnology.ResinSLA;

    public PrintabilityStatus Printability { get; set; } = PrintabilityStatus.Untested;

    [MaxLength(2000)]
    public string PrintabilityNotes { get; set; } = string.Empty;

    /// <summary>
    /// Berechnetes Volumen des primären 3D-Modells in cm³ (= ml)
    /// </summary>
    public double EstimatedVolumeCm3 { get; set; }

    public double BoundingBoxXMm { get; set; }
    public double BoundingBoxYMm { get; set; }
    public double BoundingBoxZMm { get; set; }

    public bool HasPresupports { get; set; }
    public bool HasSlicerProject { get; set; }

    public int PrintCount { get; set; }

    [MaxLength(100)]
    public string? CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<ModelFile> Files { get; set; } = [];
    public List<ModelCollectionMap> Collections { get; set; } = [];
    public List<ModelTagMap> Tags { get; set; } = [];
    public List<ModelMake> Makes { get; set; } = [];
    public List<UserFavoriteModel> Favorites { get; set; } = [];
}

public class ModelFile
{
    public int Id { get; set; }

    public int ModelItemId { get; set; }
    public ModelItem? ModelItem { get; set; }

    [Required, MaxLength(260)]
    public string FileName { get; set; } = string.Empty;

    [Required, MaxLength(600)]
    public string RelativeStoragePath { get; set; } = string.Empty;

    [MaxLength(20)]
    public string FileExtension { get; set; } = string.Empty;

    public long FileSizeBytes { get; set; }

    [Required, MaxLength(64)]
    public string Sha256Hash { get; set; } = string.Empty;

    public ModelFileCategory Category { get; set; } = ModelFileCategory.UnsupportedMesh;

    [MaxLength(150)]
    public string VariantLabel { get; set; } = string.Empty;

    public double VolumeCm3 { get; set; }
    public double DimXMm { get; set; }
    public double DimYMm { get; set; }
    public double DimZMm { get; set; }
    public int TriangleCount { get; set; }

    public PrintabilityStatus Printability { get; set; } = PrintabilityStatus.Untested;

    [MaxLength(500)]
    public string TechnicalNote { get; set; } = string.Empty;

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}

public class Collection
{
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(220)]
    public string Slug { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string Description { get; set; } = string.Empty;

    [MaxLength(150)]
    public string CreatorStudio { get; set; } = string.Empty;

    [MaxLength(50)]
    public string ReleaseIdentifier { get; set; } = string.Empty;

    public int? ParentCollectionId { get; set; }
    public Collection? ParentCollection { get; set; }
    public List<Collection> ChildCollections { get; set; } = [];

    /// <summary>
    /// Großes Release-Poster / Übersichtsbild des Monats-Bundles
    /// </summary>
    [MaxLength(500)]
    public string? PosterImagePath { get; set; }

    /// <summary>
    /// Optionaler PDF-Katalog zum Bundle
    /// </summary>
    [MaxLength(500)]
    public string? PosterPdfPath { get; set; }

    public bool IsCuratedBundle { get; set; } = true;

    [MaxLength(100)]
    public string? CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<ModelCollectionMap> Models { get; set; } = [];
}

public class Tag
{
    public int Id { get; set; }

    [Required, MaxLength(80)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(20)]
    public string ColorHex { get; set; } = "#26A69A";

    public List<ModelTagMap> Models { get; set; } = [];
}

public class ModelCollectionMap
{
    public int ModelItemId { get; set; }
    public ModelItem? ModelItem { get; set; }

    public int CollectionId { get; set; }
    public Collection? Collection { get; set; }
}

public class ModelTagMap
{
    public int ModelItemId { get; set; }
    public ModelItem? ModelItem { get; set; }

    public int TagId { get; set; }
    public Tag? Tag { get; set; }
}

public class UserFavoriteModel
{
    [MaxLength(100)]
    public string UserId { get; set; } = string.Empty;

    public int ModelItemId { get; set; }
    public ModelItem? ModelItem { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ModelMake
{
    public int Id { get; set; }

    public int ModelItemId { get; set; }
    public ModelItem? ModelItem { get; set; }

    [MaxLength(100)]
    public string UserId { get; set; } = string.Empty;

    [MaxLength(100)]
    public string UserDisplayName { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string ImagePath { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string Caption { get; set; } = string.Empty;

    public bool IsPainted { get; set; }

    [MaxLength(120)]
    public string MaterialUsed { get; set; } = string.Empty;

    [MaxLength(120)]
    public string PrinterUsed { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
