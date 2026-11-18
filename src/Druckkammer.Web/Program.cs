using Druckkammer.Web.Components;
using Druckkammer.Web.Data;
using Druckkammer.Web.Data.Entities;
using Druckkammer.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Resolve persistent data directory
var configuredDataDir = builder.Configuration["Druckkammer:DataDirectory"];
string dataDir = !string.IsNullOrWhiteSpace(configuredDataDir)
    ? Path.GetFullPath(configuredDataDir)
    : Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "data"));
Directory.CreateDirectory(dataDir);

string dbPath = Path.Combine(dataDir, "druckkammer.db");
string connectionString = $"Data Source={dbPath}";

builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseSqlite(connectionString));
builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequireDigit = false;
    options.Password.RequiredLength = 6;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireLowercase = false;
    options.User.RequireUniqueEmail = true;
    options.Tokens.AuthenticatorIssuer = "Druckkammer";
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/login";
    options.AccessDeniedPath = "/login";
    options.ExpireTimeSpan = TimeSpan.FromDays(30);
});

builder.Services.AddAuthorizationBuilder();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddHttpClient();
builder.Services.AddMudServices();

builder.Services.AddSingleton<StlAnalysisService>();
builder.Services.AddSingleton<ManagedStorageService>();
builder.Services.AddSingleton<LibraryScannerBackgroundService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LibraryScannerBackgroundService>());
builder.Services.AddScoped<OpenGraphService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<PrintQueueService>();
builder.Services.AddScoped<UserContextService>();
builder.Services.AddScoped<AuthConfigurationService>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        options.DetailedErrors = true;
    });

var app = builder.Build();

// Initialize SQLite database, roles, and permissions
await DatabaseInitializer.InitializeAsync(app.Services);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// --- First-Run Admin Setup Endpoint ---
app.MapPost("/auth/setup", async (
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    [FromForm] string displayName,
    [FromForm] string email,
    [FromForm] string password,
    [FromForm] string? confirmPassword) =>
{
    if (await userManager.Users.AnyAsync())
        return Results.Redirect("/login");

    email = email?.Trim() ?? string.Empty;
    displayName = displayName?.Trim() ?? string.Empty;

    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(displayName))
        return Results.Redirect("/setup?error=missing");

    if (!string.IsNullOrEmpty(confirmPassword) && password != confirmPassword)
        return Results.Redirect("/setup?error=mismatch");

    if (password.Length < 6)
        return Results.Redirect("/setup?error=short_password");

    var adminUser = new ApplicationUser
    {
        UserName = email,
        Email = email,
        DisplayName = displayName,
        AvatarColor = "#7E57C2",
        EmailConfirmed = true,
        IsApproved = true
    };

    var createResult = await userManager.CreateAsync(adminUser, password);
    if (!createResult.Succeeded)
        return Results.Redirect("/setup?error=invalid");

    await userManager.AddToRolesAsync(adminUser, [AppRoles.Admin, AppRoles.Operator]);
    await signInManager.SignInAsync(adminUser, isPersistent: true);
    return Results.Redirect("/");
}).DisableAntiforgery();

// --- Standard & TOTP 2FA Login Endpoints ---
app.MapPost("/auth/login", async (
    SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager,
    [FromForm] string email,
    [FromForm] string password,
    [FromForm] string? returnUrl) =>
{
    if (!await userManager.Users.AnyAsync())
        return Results.Redirect("/setup");

    var user = await userManager.FindByEmailAsync(email?.Trim() ?? string.Empty);
    if (user is null)
        return Results.Redirect("/login?error=invalid");

    if (!user.IsApproved)
        return Results.Redirect("/login?error=unapproved");

    var result = await signInManager.PasswordSignInAsync(user, password ?? string.Empty, isPersistent: true, lockoutOnFailure: false);
    if (result.RequiresTwoFactor)
    {
        return Results.Redirect("/login-2fa");
    }
    if (!result.Succeeded)
        return Results.Redirect("/login?error=invalid");

    return Results.Redirect(string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl);
}).DisableAntiforgery();

app.MapPost("/auth/login-2fa", async (
    SignInManager<ApplicationUser> signInManager,
    [FromForm] string code,
    [FromForm] bool? rememberMachine,
    [FromForm] bool? useRecoveryCode) =>
{
    var user = await signInManager.GetTwoFactorAuthenticationUserAsync();
    if (user is null)
        return Results.Redirect("/login?error=invalid");

    string cleanCode = (code ?? string.Empty).Replace(" ", string.Empty).Replace("-", string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(cleanCode))
        return Results.Redirect("/login-2fa?error=invalid_code");

    Microsoft.AspNetCore.Identity.SignInResult result;
    if (useRecoveryCode == true || cleanCode.Length > 6)
    {
        result = await signInManager.TwoFactorRecoveryCodeSignInAsync(cleanCode);
    }
    else
    {
        result = await signInManager.TwoFactorAuthenticatorSignInAsync(
            cleanCode,
            isPersistent: true,
            rememberClient: rememberMachine == true);
    }

    if (!result.Succeeded)
        return Results.Redirect("/login-2fa?error=invalid_code");

    return Results.Redirect("/");
}).DisableAntiforgery();

// --- OpenID Connect (OIDC / SSO) Endpoints ---
app.MapGet("/auth/oidc-login", async (
    HttpContext ctx,
    AuthConfigurationService authConfigService) =>
{
    var oidc = await authConfigService.GetOidcConfigAsync();
    if (!oidc.Enabled)
        return Results.Redirect("/login?error=oidc_disabled");

    try
    {
        string callbackUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}/auth/oidc-callback";
        var (authUrl, state, verifier) = await authConfigService.BuildOidcAuthorizationUrlAsync(oidc, callbackUrl);

        ctx.Response.Cookies.Append("druckkammer_oidc_state", $"{state}:{verifier}", new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddMinutes(10)
        });

        return Results.Redirect(authUrl);
    }
    catch
    {
        return Results.Redirect("/login?error=oidc_failed");
    }
});

app.MapGet("/auth/oidc-callback", async (
    HttpContext ctx,
    AuthConfigurationService authConfigService,
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager) =>
{
    string? code = ctx.Request.Query["code"];
    string? state = ctx.Request.Query["state"];
    if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        return Results.Redirect("/login?error=oidc_failed");

    if (!ctx.Request.Cookies.TryGetValue("druckkammer_oidc_state", out var cookieVal) || string.IsNullOrWhiteSpace(cookieVal))
        return Results.Redirect("/login?error=oidc_failed");

    ctx.Response.Cookies.Delete("druckkammer_oidc_state");
    var parts = cookieVal.Split(':', 2);
    if (parts.Length != 2 || parts[0] != state)
        return Results.Redirect("/login?error=oidc_failed");

    string codeVerifier = parts[1];
    var oidc = await authConfigService.GetOidcConfigAsync();
    string callbackUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}/auth/oidc-callback";

    var identity = await authConfigService.ExchangeCodeForUserIdentityAsync(oidc, code, codeVerifier, callbackUrl);
    if (identity is null)
        return Results.Redirect("/login?error=oidc_failed");

    // 1. Prüfen ob dies der allererste Benutzer in einer leeren Instanz ist
    bool isFirstUserEver = !await userManager.Users.AnyAsync();

    var user = await userManager.FindByLoginAsync("OpenIdConnect", identity.Subject)
               ?? await userManager.FindByEmailAsync(identity.Email);

    if (user is null)
    {
        user = new ApplicationUser
        {
            UserName = identity.Email,
            Email = identity.Email,
            DisplayName = identity.DisplayName,
            AvatarColor = "#26A69A",
            EmailConfirmed = true,
            IsApproved = isFirstUserEver || oidc.AutoApproveUsers
        };
        var createRes = await userManager.CreateAsync(user);
        if (!createRes.Succeeded)
            return Results.Redirect("/login?error=oidc_failed");

        if (isFirstUserEver)
        {
            await userManager.AddToRolesAsync(user, [AppRoles.Admin, AppRoles.Operator]);
        }
        else
        {
            string role = AppRoles.All.Contains(oidc.DefaultRole) ? oidc.DefaultRole : AppRoles.Requester;
            await userManager.AddToRoleAsync(user, role);
        }
    }

    var logins = await userManager.GetLoginsAsync(user);
    if (!logins.Any(l => l.LoginProvider == "OpenIdConnect" && l.ProviderKey == identity.Subject))
    {
        await userManager.AddLoginAsync(user, new UserLoginInfo("OpenIdConnect", identity.Subject, oidc.ProviderName));
    }

    if (!user.IsApproved)
        return Results.Redirect("/login?error=unapproved");

    await signInManager.SignInAsync(user, isPersistent: true);
    return Results.Redirect("/");
});

app.MapPost("/auth/register", async (
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    [FromForm] string displayName,
    [FromForm] string email,
    [FromForm] string password,
    [FromForm] string? inviteCode) =>
{
    email = email?.Trim() ?? string.Empty;
    displayName = displayName?.Trim() ?? string.Empty;
    inviteCode = inviteCode?.Trim().ToUpperInvariant();

    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(displayName))
        return Results.Redirect("/register?error=missing");

    bool isFirstUserEver = !await userManager.Users.AnyAsync();
    if (isFirstUserEver)
    {
        var firstAdmin = new ApplicationUser
        {
            UserName = email,
            Email = email,
            DisplayName = displayName,
            AvatarColor = "#7E57C2",
            EmailConfirmed = true,
            IsApproved = true
        };
        var firstResult = await userManager.CreateAsync(firstAdmin, password);
        if (!firstResult.Succeeded)
            return Results.Redirect("/setup?error=short_password");

        await userManager.AddToRolesAsync(firstAdmin, [AppRoles.Admin, AppRoles.Operator]);
        await signInManager.SignInAsync(firstAdmin, isPersistent: true);
        return Results.Redirect("/");
    }

    if (await userManager.FindByEmailAsync(email) is not null)
        return Results.Redirect("/register?error=exists");

    InviteToken? validToken = null;
    if (!string.IsNullOrWhiteSpace(inviteCode))
    {
        validToken = await db.InviteTokens.FirstOrDefaultAsync(t => t.Code == inviteCode);
        if (validToken is null || !validToken.IsValid)
            return Results.Redirect("/register?error=invalid_invite");
    }
    else
    {
        var allowSelfReg = await db.SystemSettings.FindAsync("AllowSelfRegistrationWithApproval");
        if (allowSelfReg?.Value?.Equals("true", StringComparison.OrdinalIgnoreCase) != true)
            return Results.Redirect("/register?error=invite_required");
    }

    bool isApprovedImmediately = validToken is not null;
    string assignedRole = validToken?.AssignedRole ?? AppRoles.Requester;

    var newUser = new ApplicationUser
    {
        UserName = email,
        Email = email,
        DisplayName = displayName,
        AvatarColor = "#42A5F5",
        EmailConfirmed = true,
        IsApproved = isApprovedImmediately
    };

    var createResult = await userManager.CreateAsync(newUser, password);
    if (!createResult.Succeeded)
        return Results.Redirect("/register?error=password");

    await userManager.AddToRoleAsync(newUser, assignedRole);

    if (validToken is not null)
    {
        validToken.UsedCount++;
        await db.SaveChangesAsync();
        await signInManager.SignInAsync(newUser, isPersistent: true);
        return Results.Redirect("/");
    }

    return Results.Redirect("/login?info=pending_approval");
}).DisableAntiforgery();

app.MapGet("/auth/logout", async (SignInManager<ApplicationUser> signInManager) =>
{
    await signInManager.SignOutAsync();
    return Results.Redirect("/login");
});

// --- Media & 3D File Endpoints ---
var contentTypeProvider = new FileExtensionContentTypeProvider();
contentTypeProvider.Mappings[".stl"] = "model/stl";
contentTypeProvider.Mappings[".obj"] = "model/obj";
contentTypeProvider.Mappings[".3mf"] = "model/3mf";
contentTypeProvider.Mappings[".lys"] = "application/octet-stream";
contentTypeProvider.Mappings[".ctb"] = "application/octet-stream";

app.MapGet("/media/{**relPath}", (string relPath, ManagedStorageService storage) =>
{
    if (string.IsNullOrWhiteSpace(relPath) || relPath.Contains(".."))
        return Results.BadRequest();

    string absPath = storage.GetAbsoluteStoragePath(relPath);
    if (!File.Exists(absPath))
        return Results.NotFound();

    if (!contentTypeProvider.TryGetContentType(absPath, out var contentType))
        contentType = "application/octet-stream";

    return Results.File(absPath, contentType, enableRangeProcessing: true);
});

app.MapGet("/api/files/{fileId:int}/download", async (
    int fileId,
    ApplicationDbContext db,
    ManagedStorageService storage) =>
{
    var modelFile = await db.ModelFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId);
    if (modelFile is null)
        return Results.NotFound();

    string absPath = storage.GetAbsoluteStoragePath(modelFile.RelativeStoragePath);
    if (!File.Exists(absPath))
        return Results.NotFound();

    return Results.File(absPath, "application/octet-stream", modelFile.FileName);
});

app.MapGet("/api/batches/{batchId:int}/zip", async (
    int batchId,
    ApplicationDbContext db,
    ManagedStorageService storage) =>
{
    var batch = await db.PrintBatches
        .Include(b => b.Printer)
        .Include(b => b.Material)
        .Include(b => b.Requests)
            .ThenInclude(r => r.SelectedModelFile)
        .Include(b => b.Requests)
            .ThenInclude(r => r.ModelItem)
                .ThenInclude(m => m!.Files)
        .AsSplitQuery()
        .AsNoTracking()
        .FirstOrDefaultAsync(b => b.Id == batchId);

    if (batch is null)
        return Results.NotFound();

    byte[] zipBytes = storage.CreateBatchSlicerZipArchive(batch);
    string zipFileName = $"Buildplate_{ManagedStorageService.Slugify(batch.BatchName, "batch")}.zip";
    return Results.File(zipBytes, "application/zip", zipFileName);
});

app.MapGet("/api/admin/backup-db", async (
    HttpContext ctx,
    ManagedStorageService storage) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    byte[] dbBytes = await storage.CreateSqliteBackupBytesAsync();
    string fileName = $"druckkammer_backup_{DateTime.UtcNow:yyyy-MM-dd_HHmm}.db";
    return Results.File(dbBytes, "application/x-sqlite3", fileName);
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program { }
