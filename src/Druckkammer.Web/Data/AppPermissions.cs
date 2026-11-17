namespace Druckkammer.Web.Data;

public static class AppRoles
{
    public const string Admin = "Admin";
    public const string Operator = "Drucker-Operator";
    public const string Curator = "Bibliotheks-Kurator";
    public const string Requester = "Anforderer";

    public static readonly string[] All = [Admin, Operator, Curator, Requester];
}

public record PermissionDefinition(string Key, string Name, string Category, string Description);

public static class AppPermissions
{
    public const string ClaimType = "permission";

    // Bibliothek
    public const string LibraryView = "Library.View";
    public const string LibraryDownload = "Library.Download";
    public const string LibraryManageCollectionsOwn = "Library.ManageCollectionsOwn";
    public const string LibraryUploadMakes = "Library.UploadMakes";
    public const string LibraryRatePrintability = "Library.RatePrintability";
    public const string LibraryUploadModels = "Library.UploadModels";
    public const string LibraryManageModels = "Library.ManageModels";
    public const string LibraryScanFolders = "Library.ScanFolders";

    // Druckjob-Queue & Warenkorb
    public const string QueueCreateRequest = "Queue.CreateRequest";
    public const string QueueViewAll = "Queue.ViewAll";
    public const string QueueManageBatches = "Queue.ManageBatches";

    // Werkstatt & Inventar
    public const string InventoryManage = "Inventory.Manage";

    // Administration
    public const string SystemAdmin = "System.Admin";

    public static readonly IReadOnlyList<PermissionDefinition> All =
    [
        new(LibraryView, "Bibliothek ansehen", "Bibliothek", "Modelle, Collections, 3D-Vorschau und Katalog durchsuchen."),
        new(LibraryDownload, "Dateien herunterladen", "Bibliothek", "STL-, 3MF- und Slicer-Dateien aus der Bibliothek herunterladen."),
        new(LibraryManageCollectionsOwn, "Sammlungen & Favoriten pflegen", "Bibliothek", "Eigene Collections/Kampagnenlisten und Favoriten verwalten."),
        new(LibraryUploadMakes, "Makes & Bemal-Fotos teilen", "Bibliothek", "Fotos von gedruckten oder bemalten Figuren am Modell hochladen."),
        new(LibraryRatePrintability, "Printability bewerten", "Bibliothek", "Druckbarkeits-Ampel und technische Erfahrungsnotizen eintragen."),
        new(LibraryUploadModels, "Modelle & Bundles hochladen", "Bibliothek", "Neue Modelle und ZIP-Bundles in die Bibliothek einspeisen."),
        new(LibraryManageModels, "Bibliothek kuratieren", "Bibliothek", "Alle Modelle, Bundle-Poster, Collections und Tags bearbeiten."),
        new(LibraryScanFolders, "Ordner-Scanner steuern", "Bibliothek", "Import-Verzeichnis scannen und Dateien in den App-Speicher übernehmen."),
        new(QueueCreateRequest, "Druckwünsche & Session-Pakete anlegen", "Druck-Queue", "Einzelwünsche, Direkt-Uploads und Session-Warenkörbe einreichen."),
        new(QueueViewAll, "Kanban-Board einsehen", "Druck-Queue", "Alle Druckwünsche und aktiven Buildplates auf dem Board sehen."),
        new(QueueManageBatches, "Kanban & Buildplates steuern", "Druck-Queue", "Status ändern, Buildplates schnüren und Teil-Fehldrucke verwalten."),
        new(InventoryManage, "Drucker & Materialien verwalten", "Werkstatt", "Drucker, Resins, Filamente, Preise und Kaffeekasse pflegen."),
        new(SystemAdmin, "System-Administration", "System", "Benutzer, Rollen, Berechtigungen, Einladungs-Links und Webhooks verwalten.")
    ];

    public static IReadOnlyList<string> GetDefaultPermissionsForRole(string role) => role switch
    {
        AppRoles.Admin => All.Select(p => p.Key).ToList(),
        AppRoles.Operator =>
        [
            LibraryView, LibraryDownload, LibraryManageCollectionsOwn, LibraryUploadMakes,
            LibraryRatePrintability, LibraryUploadModels, LibraryManageModels, LibraryScanFolders,
            QueueCreateRequest, QueueViewAll, QueueManageBatches, InventoryManage
        ],
        AppRoles.Curator =>
        [
            LibraryView, LibraryDownload, LibraryManageCollectionsOwn, LibraryUploadMakes,
            LibraryRatePrintability, LibraryUploadModels, LibraryManageModels, LibraryScanFolders,
            QueueCreateRequest, QueueViewAll
        ],
        AppRoles.Requester =>
        [
            LibraryView, LibraryDownload, LibraryManageCollectionsOwn, LibraryUploadMakes,
            LibraryRatePrintability, QueueCreateRequest, QueueViewAll
        ],
        _ => [LibraryView, QueueCreateRequest, QueueViewAll]
    };
}
