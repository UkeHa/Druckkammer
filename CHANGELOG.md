# Changelog

Alle relevanten Änderungen und neuen Funktionen von **Druckkammer** werden in dieser Datei dokumentiert.  
Das Format orientiert sich an [Keep a Changelog](https://keepachangelog.com/de/1.1.0/).

---

## [0.2.0] – 2026-11-18

### Hinzugefügt (Added)
- **OpenID Connect (OIDC / Single Sign-On)**:
  - Unterstützung für externe Identity-Provider (z.B. *Authentik*, *Keycloak*, *Pocket ID*, *Authelia*, *Google*, *Microsoft Entra*) über Authorization-Code-Flow mit **PKCE (`S256`)** und automatischer `.well-known/openid-configuration`-Discovery (`/auth/oidc-login` & `/auth/oidc-callback`).
  - Live-Konfiguration im Admin-Menü (`/admin` → Tab **„🔐 OpenID Connect (SSO)“**) ohne Container-Neustart sowie alternativ per Umgebungsvariablen (`Druckkammer__OpenIdConnect__*`).
  - Automatische Kontoverknüpfung über E-Mail oder Subject-ID sowie konfigurierbare Standardrolle und Auto-Freischaltung für neue SSO-Benutzer.
- **Zwei-Faktor-Authentifizierung (TOTP 2FA)**:
  - Neue Sicherheitsseite unter `/account/security` ([`AccountSecurity.razor`](src/Druckkammer.Web/Components/Pages/Auth/AccountSecurity.razor)) zum Aktivieren/Deaktivieren von TOTP-2FA mit scannbarem **SVG-QR-Code** und Base32-Schlüssel für *Google Authenticator*, *Bitwarden*, *1Password*, *Ente Auth*, *Authy* u.a.
  - Generierung von **8 Notfall-Recovery-Codes** bei der 2FA-Aktivierung.
  - Zweistufiger Login-Dialog unter `/login-2fa` ([`Login2fa.razor`](src/Druckkammer.Web/Components/Pages/Auth/Login2fa.razor)) inkl. *„Diesem Browser 30 Tage vertrauen“* und Recovery-Code-Unterstützung.
  - 2FA-Statusanzeige und Notfall-Reset-Funktion für Administratoren in der Benutzerverwaltung (`/admin`).
- **Ersteinrichtungs-Workflow (`/setup`)**:
  - Neue Einrichtungsseite ([`Setup.razor`](src/Druckkammer.Web/Components/Pages/Auth/Setup.razor)), die beim allerersten Start einer leeren Instanz automatisch aufgerufen wird, damit der erste Benutzer das **Administrator- & Operator-Konto** anlegt.
- **Projekt-Logo, Favicon & Dokumentation**:
  - Neues isometrisches 3D-Druckkammer-Projektlogo (`docs/logo.png`, `wwwroot/logo.png`) und Vektor-Favicon (`wwwroot/favicon.svg`) in AppBar, Login, Setup und README.
  - Screenshots aller Hauptansichten unter `docs/screenshots/` in die `README.md` aufgenommen.

### Geändert (Changed)
- Vorinstallierte Demo-Benutzerkonten und Demo-Schnell-Logins aus [`DatabaseInitializer.cs`](src/Druckkammer.Web/Data/DatabaseInitializer.cs) und [`Login.razor`](src/Druckkammer.Web/Components/Pages/Auth/Login.razor) entfernt – neue Instanzen starten sauber mit dem `/setup`-Assistenten.

---

## [0.1.0] – 2026-11-17

### Hinzugefügt (Added)
- **Grundarchitektur & Deployment**:
  - **.NET 11** (`net11.0`) Blazor Interactive Server Anwendung mit **MudBlazor** (Dark/Light Mode) und **SQLite** via Entity Framework Core.
  - Multi-Stage [`Dockerfile`](Dockerfile), [`docker-compose.yml`](docker-compose.yml) und GitHub Actions Workflow ([`.github/workflows/docker-publish.yml`](.github/workflows/docker-publish.yml)) für Multi-Arch Container-Builds (`linux/amd64`, `linux/arm64`) nach `ghcr.io`.
- **Verwaltete 3D-Modell-Bibliothek**:
  - Kanonischer Copy-on-Import Dateispeicher (`/data/storage/{Studio}/{Collection}/{Modell}/`) mit **SHA-256 Datei-Hashing** zur Duplikat-Vermeidung ([`ManagedStorageService`](src/Druckkammer.Web/Services/ManagedStorageService.cs)).
  - Einzeldatei- und `.zip`-Bundle-Upload sowie Hintergrund-Verzeichnis-Scanner für gemountete NAS-/Import-Ordner (`/import`) mit automatischer Dateiklassifizierung (*Presupported*, *Unsupported*, *Base*, *Slicer-Projekt*, *Release-Poster*).
  - Serverseitiger Binär-/ASCII-STL- und OBJ-Parser ([`StlAnalysisService`](src/Druckkammer.Web/Services/StlAnalysisService.cs)) zur automatischen Berechnung von **Volumen ($\text{cm}^3$/$\text{ml}$)** und **Bounding-Box ($X \times Y \times Z$ in mm)**.
  - Interaktiver **3D-Viewer** ([`Model3DViewer.razor`](src/Druckkammer.Web/Components/Shared/Model3DViewer.razor)) mit Varianten-Umschaltung, Wireframe-Modus und automatischer Canvas-Thumbnail-Generierung.
  - Hierarchische **Collections** inkl. großem Bundle-Poster im Kopfbereich, Tagging, Favoriten, D&D-Kreaturengrößen-Filtern, Smart-Filtern, **Printability-Ampel** und **Makes-/Bemal-Galerie**.
  - **OpenGraph URL-Importer** ([`OpenGraphService`](src/Druckkammer.Web/Services/OpenGraphService.cs)) für Links von *MyMiniFactory*, *Printables*, *Thingiverse* u.a.
- **Session-Warenkorb, Maßstabs-Rechner & Kanban-Druckjobverwaltung**:
  - **Session-Warenkorb (`PrintOrder`)** zum Bündeln mehrerer Figuren für einen Tabletop-/D&D-Spielabend mit gemeinsamem Wunschtermin.
  - Integrierter **Tabletop-Maßstabs-Umrechner** (`28mm ↔ 32mm ↔ 54mm ↔ 75mm` sowie freie Zielhöhe in mm) mit kubischer Volumen- und Kostenschätzung ($s^3$).
  - **Kanban-Board** (`Offen` → `Auf Buildplate` → `Druckt gerade` → `Waschen & Härten` → `Abholbereit`) mit Kommentar-Threads.
  - **Buildplate-Batching (`PrintBatch`)** inkl. Resin-Warteschlangen-Füllstandsanzeige, Sammel-ZIP-Download für den Slicer, Slicer-Tresor (`.lys`/`.ctb`) und **Teil-Fehldruck-Recovery** (automatisches Wiedereinreihen fehlgeschlagener Figuren).
- **Werkstatt, Kaffeekasse, RBAC & Webhooks**:
  - Drucker- und Material-/Resin-Verwaltung sowie freundschaftliche **Kaffeekassen-Übersicht** und **Hall of Fame** Dashboard.
  - Rollenbasiertes Rechtesystem (`Admin`, `Drucker-Operator`, `Bibliotheks-Kurator`, `Anforderer`) mit live anpassbarer Berechtigungs-Matrix und Einladungs-Tokens (`InviteToken`).
  - Webhook-Benachrichtigungen für Discord, Telegram, generisches JSON und SMTP ([`NotificationService`](src/Druckkammer.Web/Services/NotificationService.cs)).
