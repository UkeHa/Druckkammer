using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Druckkammer.Web.Data;
using Druckkammer.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Druckkammer.Web.Services;

public record StoredFileResult(
    bool IsDuplicate,
    string Sha256Hash,
    string RelativeStoragePath,
    string AbsolutePath,
    long FileSizeBytes,
    ModelFileCategory Category,
    string VariantLabel,
    MeshAnalysisResult? MeshAnalysis);

public record FolderScanSummary(
    int ScannedFilesCount,
    int ImportedModelsCount,
    int ImportedFilesCount,
    int SkippedDuplicatesCount,
    List<string> LogMessages);

public partial class ManagedStorageService
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly StlAnalysisService _stlAnalysis;
    private readonly IServiceScopeFactory _scopeFactory;

    public ManagedStorageService(
        IConfiguration config,
        IWebHostEnvironment env,
        StlAnalysisService stlAnalysis,
        IServiceScopeFactory scopeFactory)
    {
        _config = config;
        _env = env;
        _stlAnalysis = stlAnalysis;
        _scopeFactory = scopeFactory;

        EnsureDirectoriesExist();
    }

    public string DataDirectory
    {
        get
        {
            var configured = _config["Druckkammer:DataDirectory"];
            if (!string.IsNullOrWhiteSpace(configured))
                return Path.GetFullPath(configured);
            return Path.GetFullPath(Path.Combine(_env.ContentRootPath, "..", "..", "data"));
        }
    }

    public string ImportDirectory
    {
        get
        {
            var configured = _config["Druckkammer:ImportDirectory"];
            if (!string.IsNullOrWhiteSpace(configured))
                return Path.GetFullPath(configured);
            return Path.GetFullPath(Path.Combine(_env.ContentRootPath, "..", "..", "import"));
        }
    }

    public string StorageRoot => Path.Combine(DataDirectory, "storage");
    public string ThumbnailsRoot => Path.Combine(DataDirectory, "thumbnails");
    public string MakesRoot => Path.Combine(DataDirectory, "makes");
    public string TempRoot => Path.Combine(DataDirectory, "temp");

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(StorageRoot);
        Directory.CreateDirectory(ThumbnailsRoot);
        Directory.CreateDirectory(MakesRoot);
        Directory.CreateDirectory(TempRoot);
        Directory.CreateDirectory(ImportDirectory);
    }

    public string GetAbsoluteStoragePath(string relativePath)
    {
        var clean = relativePath.TrimStart('/', '\\').Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(DataDirectory, clean);
    }

    public static async Task<string> ComputeSha256Async(Stream stream)
    {
        stream.Position = 0;
        byte[] hashBytes = await SHA256.HashDataAsync(stream);
        stream.Position = 0;
        return Convert.ToHexStringLower(hashBytes);
    }

    public static string Slugify(string? input, string fallback = "general")
    {
        if (string.IsNullOrWhiteSpace(input))
            return fallback;

        string normalized = input.Trim().ToLowerInvariant()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        normalized = NonAlphaNumRegex().Replace(normalized, "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    /// <summary>
    /// Erkennt anhand des Dateinamens und übergeordneten Ordners automatisch die Kategorie (Supported, Unsupported, Base, Slicer, Poster, etc.).
    /// </summary>
    public static (ModelFileCategory Category, string VariantLabel) ClassifyFile(string fileName, string folderContext = "")
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var combined = $"{folderContext}/{nameWithoutExt}".ToLowerInvariant();

        if (ext is ".lys" or ".lyt" or ".chitubox")
            return (ModelFileCategory.SlicerProject, $"Slicer-Projekt ({ext.TrimStart('.').ToUpperInvariant()})");

        if (ext is ".ctb" or ".goo" or ".gcode" or ".pwmx")
            return (ModelFileCategory.SlicedPlate, $"Druckplatte ({ext.TrimStart('.').ToUpperInvariant()})");

        if (ext is ".png" or ".jpg" or ".jpeg" or ".webp")
        {
            if (combined.Contains("poster") || combined.Contains("overview") || combined.Contains("release") || combined.Contains("bundle") || combined.Contains("banner"))
                return (ModelFileCategory.ReleasePoster, "Release-Poster");
            return (ModelFileCategory.PreviewImage, "Vorschaubild / Render");
        }

        if (ext is ".pdf" or ".txt" or ".md")
            return (ModelFileCategory.Documentation, "Dokumentation / Katalog");

        // 3D Mesh (.stl, .3mf, .obj)
        if (combined.Contains("base") || combined.Contains("sockel") || combined.Contains("pedestal"))
            return (ModelFileCategory.BaseMesh, CleanVariantLabel(nameWithoutExt, "Base"));

        if (combined.Contains("unsup") || combined.Contains("un-sup") || combined.Contains("no_sup") || combined.Contains("nosup") || combined.Contains("raw"))
            return (ModelFileCategory.UnsupportedMesh, CleanVariantLabel(nameWithoutExt, "Unsupported"));

        if (combined.Contains("presup") || combined.Contains("pre-sup") || combined.Contains("pre_sup") || combined.Contains("supported") || combined.Contains("_sup"))
            return (ModelFileCategory.PresupportedMesh, CleanVariantLabel(nameWithoutExt, "Presupported"));

        if (combined.Contains("part") || combined.Contains("arm") || combined.Contains("head") || combined.Contains("wing") || combined.Contains("weapon"))
            return (ModelFileCategory.PartMesh, CleanVariantLabel(nameWithoutExt, "Einzelteil"));

        return (ModelFileCategory.UnsupportedMesh, CleanVariantLabel(nameWithoutExt, "Standard Mesh"));
    }

    private static string CleanVariantLabel(string rawName, string prefix)
    {
        var cleaned = rawName.Replace('_', ' ').Replace('-', ' ').Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? prefix : cleaned;
    }

    /// <summary>
    /// Kopiert einen Datei-Stream in den verwalteten App-Speicher unter /data/storage/{Creator}/{Collection}/{ModelSlug}/
    /// und führt SHA-256 Deduplizierung sowie STL-Volumenanalyse durch.
    /// </summary>
    public async Task<StoredFileResult> StoreModelFileStreamAsync(
        Stream sourceStream,
        string originalFileName,
        string creatorStudio,
        string collectionName,
        string modelTitle,
        string folderContext = "",
        bool checkDuplicateInDb = true)
    {
        using var ms = new MemoryStream();
        await sourceStream.CopyToAsync(ms);
        string sha256 = await ComputeSha256Async(ms);

        var (category, variantLabel) = ClassifyFile(originalFileName, folderContext);
        var ext = Path.GetExtension(originalFileName).ToLowerInvariant();

        if (checkDuplicateInDb)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var existing = await db.ModelFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Sha256Hash == sha256);
            if (existing is not null)
            {
                var existingAbs = GetAbsoluteStoragePath(existing.RelativeStoragePath);
                if (File.Exists(existingAbs))
                {
                    var existingMesh = (ext is ".stl" or ".obj")
                        ? new MeshAnalysisResult(existing.VolumeCm3, existing.DimXMm, existing.DimYMm, existing.DimZMm, existing.TriangleCount, existing.VolumeCm3 > 0)
                        : null;

                    return new StoredFileResult(
                        IsDuplicate: true,
                        Sha256Hash: sha256,
                        RelativeStoragePath: existing.RelativeStoragePath,
                        AbsolutePath: existingAbs,
                        FileSizeBytes: existing.FileSizeBytes,
                        Category: category,
                        VariantLabel: variantLabel,
                        MeshAnalysis: existingMesh);
                }
            }
        }

        string creatorSlug = Slugify(creatorStudio, "community");
        string collectionSlug = Slugify(collectionName, "unsorted");
        string modelSlug = Slugify(modelTitle, "model");

        string safeFileName = SanitizeFileName(originalFileName);
        string relativeDir = Path.Combine("storage", creatorSlug, collectionSlug, modelSlug);
        string absoluteDir = Path.Combine(DataDirectory, relativeDir);
        Directory.CreateDirectory(absoluteDir);

        string targetFileName = safeFileName;
        string absoluteFilePath = Path.Combine(absoluteDir, targetFileName);

        // Falls gleichnamige Datei mit anderem Hash existiert, kurzen Hash-Suffix anhängen
        if (File.Exists(absoluteFilePath))
        {
            var baseName = Path.GetFileNameWithoutExtension(safeFileName);
            targetFileName = $"{baseName}_{sha256[..6]}{ext}";
            absoluteFilePath = Path.Combine(absoluteDir, targetFileName);
        }

        ms.Position = 0;
        await using (var fs = File.Create(absoluteFilePath))
        {
            await ms.CopyToAsync(fs);
        }

        string relativeStoragePath = Path.Combine(relativeDir, targetFileName).Replace('\\', '/');

        MeshAnalysisResult? meshResult = null;
        if (ext is ".stl" or ".obj")
        {
            ms.Position = 0;
            meshResult = ext == ".obj"
                ? _stlAnalysis.AnalyzeObjStream(ms)
                : _stlAnalysis.AnalyzeStlStream(ms);
        }

        return new StoredFileResult(
            IsDuplicate: false,
            Sha256Hash: sha256,
            RelativeStoragePath: relativeStoragePath,
            AbsolutePath: absoluteFilePath,
            FileSizeBytes: ms.Length,
            Category: category,
            VariantLabel: variantLabel,
            MeshAnalysis: meshResult);
    }

    /// <summary>
    /// Speichert ein hochgeladenes oder aus dem 3D-Canvas generiertes Thumbnail unter /data/thumbnails/
    /// </summary>
    public async Task<string> SaveThumbnailBytesAsync(byte[] imageBytes, string prefix, string extension = ".png")
    {
        EnsureDirectoriesExist();
        string fileName = $"{Slugify(prefix, "thumb")}_{Guid.NewGuid().ToString("N")[..8]}{extension}";
        string absPath = Path.Combine(ThumbnailsRoot, fileName);
        await File.WriteAllBytesAsync(absPath, imageBytes);
        return $"thumbnails/{fileName}";
    }

    /// <summary>
    /// Speichert ein Foto für die Makes- & Bemal-Galerie unter /data/makes/
    /// </summary>
    public async Task<string> SaveMakeImageStreamAsync(Stream imageStream, string originalFileName)
    {
        EnsureDirectoriesExist();
        string ext = Path.GetExtension(originalFileName).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
        string fileName = $"make_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..6]}{ext}";
        string absPath = Path.Combine(MakesRoot, fileName);
        await using var fs = File.Create(absPath);
        await imageStream.CopyToAsync(fs);
        return $"makes/{fileName}";
    }

    /// <summary>
    /// Entpackt ein hochgeladenes ZIP-Bundle und legt daraus (oder ergänzt) ein Modell inkl. Collection und Tags an.
    /// </summary>
    public async Task<ModelItem> ImportZipBundleAsModelAsync(
        Stream zipStream,
        string zipFileName,
        string? overrideTitle,
        string creatorStudio,
        string releaseMonth,
        int? collectionId,
        List<int> tagIds,
        TabletopScaleSize scaleSize,
        string? userId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        string collectionName = "Allgemein";
        Collection? targetCollection = null;
        if (collectionId.HasValue)
        {
            targetCollection = await db.Collections.FindAsync(collectionId.Value);
            if (targetCollection is not null)
                collectionName = targetCollection.Name;
        }

        string title = !string.IsNullOrWhiteSpace(overrideTitle)
            ? overrideTitle.Trim()
            : CleanVariantLabel(Path.GetFileNameWithoutExtension(zipFileName), "Neues Bundle-Modell");

        var model = new ModelItem
        {
            Title = title,
            Slug = Slugify(title),
            CreatorStudio = string.IsNullOrWhiteSpace(creatorStudio) ? "Unbekanntes Studio" : creatorStudio.Trim(),
            ReleaseMonth = releaseMonth?.Trim() ?? string.Empty,
            ScaleSize = scaleSize,
            NativeScaleMm = scaleSize == TabletopScaleSize.BustDisplay || scaleSize == TabletopScaleSize.Huge ? 75.0 : 32.0,
            CreatedByUserId = userId
        };

        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name.StartsWith('.') || entry.FullName.Contains("__MACOSX"))
                continue;

            var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (!IsSupportedExtension(ext))
                continue;

            string folderContext = Path.GetDirectoryName(entry.FullName) ?? string.Empty;
            await using var entryStream = entry.Open();
            var stored = await StoreModelFileStreamAsync(
                entryStream,
                entry.Name,
                model.CreatorStudio,
                collectionName,
                model.Title,
                folderContext,
                checkDuplicateInDb: true);

            AttachStoredFileToModel(model, entry.Name, stored, targetCollection);
        }

        FinalizeModelMetrics(model);

        if (targetCollection is not null)
        {
            model.Collections.Add(new ModelCollectionMap { CollectionId = targetCollection.Id });
        }

        foreach (var tagId in tagIds.Distinct())
        {
            model.Tags.Add(new ModelTagMap { TagId = tagId });
        }

        db.ModelItems.Add(model);
        await db.SaveChangesAsync();
        return model;
    }

    /// <summary>
    /// Scannt das gemountete /import Verzeichnis (z.B. NAS-Ordner mit Patreon-Bundles), kopiert neue Dateien
    /// zerstörungsfrei in den verwalteten Speicher (/data/storage) und legt Collections, Modelle & Tags automatisch an.
    /// </summary>
    public async Task<FolderScanSummary> ScanImportDirectoryAsync(string? triggeredByUserId = null)
    {
        EnsureDirectoriesExist();
        var logs = new List<string>();
        int scannedFiles = 0;
        int importedModels = 0;
        int importedFiles = 0;
        int skippedDuplicates = 0;

        if (!Directory.Exists(ImportDirectory))
        {
            logs.Add($"Import-Verzeichnis '{ImportDirectory}' existiert nicht.");
            return new FolderScanSummary(0, 0, 0, 0, logs);
        }

        var allFiles = Directory.EnumerateFiles(ImportDirectory, "*.*", SearchOption.AllDirectories)
            .Where(f => IsSupportedExtension(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        if (allFiles.Count == 0)
        {
            logs.Add($"Keine unterstützten 3D-/Medien-Dateien in '{ImportDirectory}' gefunden.");
            return new FolderScanSummary(0, 0, 0, 0, logs);
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Gruppiere Dateien nach logischem Modell-Ordner (ignoriere Unterordner wie "Supported", "Unsupported", "Bases", "Renders", "Lychee")
        var groupedByModelFolder = allFiles.GroupBy(f => GetLogicalModelDirectory(f, ImportDirectory));

        foreach (var group in groupedByModelFolder)
        {
            string relativeModelFolder = group.Key;
            var segments = relativeModelFolder.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

            string creatorName = segments.Length >= 2 ? FormatReadableName(segments[0]) : "Importierte Sammlung";
            string collectionName = segments.Length >= 3 ? FormatReadableName(segments[1]) : (segments.Length >= 1 ? FormatReadableName(segments[0]) : "Import-Bundle");
            string modelTitle = segments.Length >= 1 ? FormatReadableName(segments[^1]) : "Importiertes Modell";

            // Collection finden oder anlegen
            var collection = await db.Collections.FirstOrDefaultAsync(c => c.Name == collectionName && c.CreatorStudio == creatorName);
            if (collection is null)
            {
                collection = new Collection
                {
                    Name = collectionName,
                    Slug = Slugify(collectionName),
                    CreatorStudio = creatorName,
                    ReleaseIdentifier = ExtractReleaseMonth(collectionName),
                    IsCuratedBundle = true,
                    CreatedByUserId = triggeredByUserId
                };
                db.Collections.Add(collection);
                await db.SaveChangesAsync();
                logs.Add($"Collection angelegt: '{creatorName} / {collectionName}'");
            }

            // Prüfen, ob Modell in dieser Collection schon existiert
            var model = await db.ModelItems
                .Include(m => m.Files)
                .Include(m => m.Collections)
                .FirstOrDefaultAsync(m => m.Title == modelTitle && m.CreatorStudio == creatorName);

            bool isNewModel = false;
            if (model is null)
            {
                model = new ModelItem
                {
                    Title = modelTitle,
                    Slug = Slugify(modelTitle),
                    CreatorStudio = creatorName,
                    ReleaseMonth = collection.ReleaseIdentifier,
                    CreatedByUserId = triggeredByUserId
                };
                isNewModel = true;
            }

            int addedToThisModel = 0;
            foreach (var filePath in group)
            {
                scannedFiles++;
                string fileName = Path.GetFileName(filePath);
                string subFolderContext = Path.GetRelativePath(Path.Combine(ImportDirectory, relativeModelFolder), Path.GetDirectoryName(filePath) ?? string.Empty);

                await using var fs = File.OpenRead(filePath);
                var stored = await StoreModelFileStreamAsync(
                    fs,
                    fileName,
                    creatorName,
                    collectionName,
                    modelTitle,
                    subFolderContext,
                    checkDuplicateInDb: true);

                if (stored.IsDuplicate && model.Files.Any(f => f.Sha256Hash == stored.Sha256Hash))
                {
                    skippedDuplicates++;
                    continue;
                }

                AttachStoredFileToModel(model, fileName, stored, collection);
                importedFiles++;
                addedToThisModel++;
            }

            if (isNewModel && addedToThisModel > 0)
            {
                FinalizeModelMetrics(model);
                model.Collections.Add(new ModelCollectionMap { CollectionId = collection.Id });
                db.ModelItems.Add(model);
                importedModels++;
                logs.Add($"Modell importiert: '{model.Title}' ({addedToThisModel} Dateien, {model.EstimatedVolumeCm3:F1} ml)");
            }
            else if (!isNewModel && addedToThisModel > 0)
            {
                FinalizeModelMetrics(model);
                if (!model.Collections.Any(c => c.CollectionId == collection.Id))
                    model.Collections.Add(new ModelCollectionMap { CollectionId = collection.Id });
                logs.Add($"Modell aktualisiert: '{model.Title}' (+{addedToThisModel} neue Dateien)");
            }

            await db.SaveChangesAsync();
        }

        return new FolderScanSummary(scannedFiles, importedModels, importedFiles, skippedDuplicates, logs);
    }

    public static void AttachStoredFileToModel(ModelItem model, string fileName, StoredFileResult stored, Collection? targetCollection = null)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var modelFile = new ModelFile
        {
            FileName = fileName,
            RelativeStoragePath = stored.RelativeStoragePath,
            FileExtension = ext,
            FileSizeBytes = stored.FileSizeBytes,
            Sha256Hash = stored.Sha256Hash,
            Category = stored.Category,
            VariantLabel = stored.VariantLabel,
            VolumeCm3 = stored.MeshAnalysis?.VolumeCm3 ?? 0,
            DimXMm = stored.MeshAnalysis?.DimXMm ?? 0,
            DimYMm = stored.MeshAnalysis?.DimYMm ?? 0,
            DimZMm = stored.MeshAnalysis?.DimZMm ?? 0,
            TriangleCount = stored.MeshAnalysis?.TriangleCount ?? 0
        };

        model.Files.Add(modelFile);

        if (stored.Category == ModelFileCategory.PreviewImage && string.IsNullOrWhiteSpace(model.ThumbnailPath))
        {
            model.ThumbnailPath = stored.RelativeStoragePath;
        }
        else if (stored.Category == ModelFileCategory.ReleasePoster)
        {
            if (string.IsNullOrWhiteSpace(model.ThumbnailPath))
                model.ThumbnailPath = stored.RelativeStoragePath;
            if (targetCollection is not null && string.IsNullOrWhiteSpace(targetCollection.PosterImagePath))
                targetCollection.PosterImagePath = stored.RelativeStoragePath;
        }
        else if (stored.Category == ModelFileCategory.Documentation && ext == ".pdf" && targetCollection is not null && string.IsNullOrWhiteSpace(targetCollection.PosterPdfPath))
        {
            targetCollection.PosterPdfPath = stored.RelativeStoragePath;
        }
    }

    public static void FinalizeModelMetrics(ModelItem model)
    {
        model.HasPresupports = model.Files.Any(f => f.Category is ModelFileCategory.PresupportedMesh or ModelFileCategory.SlicerProject);
        model.HasSlicerProject = model.Files.Any(f => f.Category is ModelFileCategory.SlicerProject or ModelFileCategory.SlicedPlate);

        // Wähle primäres 3D-Mesh für Volumen und Bounding-Box (bevorzuge Unsupported oder Presupported Haupt-Mesh vor Base)
        var primaryMesh = model.Files
            .Where(f => f.VolumeCm3 > 0)
            .OrderByDescending(f => f.Category is ModelFileCategory.UnsupportedMesh or ModelFileCategory.PresupportedMesh)
            .ThenByDescending(f => f.VolumeCm3)
            .FirstOrDefault();

        if (primaryMesh is not null)
        {
            model.EstimatedVolumeCm3 = primaryMesh.VolumeCm3;
            model.BoundingBoxXMm = primaryMesh.DimXMm;
            model.BoundingBoxYMm = primaryMesh.DimYMm;
            model.BoundingBoxZMm = primaryMesh.DimZMm;

            if (model.ScaleSize == TabletopScaleSize.Unknown)
            {
                model.ScaleSize = StlAnalysisService.SuggestTabletopSize(primaryMesh.DimXMm, primaryMesh.DimYMm, primaryMesh.DimZMm);
            }
        }
    }

    /// <summary>
    /// Erstellt ein Sammel-ZIP-Archiv im Speicher mit allen benötigten Druckdateien einer Buildplate (PrintBatch) für den Slicer.
    /// </summary>
    public byte[] CreateBatchSlicerZipArchive(PrintBatch batch)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            int index = 1;
            foreach (var req in batch.Requests)
            {
                string? relPath = req.SelectedModelFile?.RelativeStoragePath
                    ?? req.ModelItem?.Files
                        .OrderByDescending(f => f.Category == ModelFileCategory.PresupportedMesh)
                        .ThenByDescending(f => f.Category == ModelFileCategory.SlicerProject)
                        .FirstOrDefault(f => f.Category is ModelFileCategory.PresupportedMesh or ModelFileCategory.UnsupportedMesh or ModelFileCategory.SlicerProject)
                        ?.RelativeStoragePath
                    ?? req.DirectUploadFilePath;

                if (string.IsNullOrWhiteSpace(relPath))
                    continue;

                string absPath = GetAbsoluteStoragePath(relPath);
                if (!File.Exists(absPath))
                    continue;

                string origName = Path.GetFileName(absPath);
                string entryName = $"{index:D2}_x{req.QuantityRequested}_Scale{req.ScalePercentage:F0}pct_{Slugify(req.RequesterDisplayName, "user")}_{origName}";
                archive.CreateEntryFromFile(absPath, entryName, CompressionLevel.Fastest);
                index++;
            }

            // Readme mit Slicer-Checkliste beilegen
            var readmeEntry = archive.CreateEntry("00_BUILDPLATE_CHECKLISTE.txt");
            using var writer = new StreamWriter(readmeEntry.Open(), Encoding.UTF8);
            writer.WriteLine($"=== DRUCKKAMMER BUILDPLATE: {batch.BatchName} ===");
            writer.WriteLine($"Drucker: {batch.Printer?.Name ?? "Nicht zugewiesen"}");
            writer.WriteLine($"Material: {batch.Material?.Name ?? "Standard"} ({batch.Material?.ColorName})");
            writer.WriteLine($"Geschätztes Gesamtvolumen: {batch.EstimatedVolumeMl:F1} ml");
            writer.WriteLine(new string('-', 60));
            foreach (var r in batch.Requests)
            {
                writer.WriteLine($"[ ] {r.QuantityRequested}x {r.Title} | Maßstab: {r.ScalePercentage:F1}% | Für: {r.RequesterDisplayName} | Notiz: {r.RequesterNotes}");
            }
        }
        ms.Position = 0;
        return ms.ToArray();
    }

    public static bool IsSupportedExtension(string ext) =>
        ext is ".stl" or ".3mf" or ".obj" or ".lys" or ".lyt" or ".chitubox" or ".ctb" or ".goo" or ".gcode"
            or ".png" or ".jpg" or ".jpeg" or ".webp" or ".pdf" or ".txt" or ".md";

    private static string GetLogicalModelDirectory(string filePath, string rootDir)
    {
        string dir = Path.GetDirectoryName(filePath) ?? rootDir;
        string rel = Path.GetRelativePath(rootDir, dir);
        if (rel == ".") return "Importiertes-Bundle";

        var parts = rel.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).ToList();
        var subFolderKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "supported", "presupported", "pre-supported", "unsupported", "un-supported",
            "bases", "base", "renders", "images", "photos", "lychee", "chitubox", "stl", "32mm", "75mm", "parts"
        };

        while (parts.Count > 1 && subFolderKeywords.Contains(parts[^1]))
        {
            parts.RemoveAt(parts.Count - 1);
        }

        return string.Join('/', parts);
    }

    private static string FormatReadableName(string folderSegment)
    {
        return folderSegment.Replace('_', ' ').Replace('-', ' ').Trim();
    }

    private static string ExtractReleaseMonth(string text)
    {
        var match = ReleaseMonthRegex().Match(text);
        return match.Success ? match.Value : string.Empty;
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(fileName.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "model.stl" : clean;
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphaNumRegex();

    [GeneratedRegex(@"20\d{2}[-_](0[1-9]|1[0-2])")]
    private static partial Regex ReleaseMonthRegex();
}
