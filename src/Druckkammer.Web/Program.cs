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
builder.Services.AddScoped<OpenGraphService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<PrintQueueService>();
builder.Services.AddScoped<UserContextService>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        options.DetailedErrors = true;
    });

var app = builder.Build();

// Initialize SQLite database, roles, permissions, and sample data
await DatabaseInitializer.InitializeAsync(app.Services);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// --- Auth Endpoints ---
app.MapPost("/auth/login", async (
    HttpContext ctx,
    SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager,
    [FromForm] string email,
    [FromForm] string password,
    [FromForm] string? returnUrl) =>
{
    var user = await userManager.FindByEmailAsync(email?.Trim() ?? string.Empty);
    if (user is null)
        return Results.Redirect("/login?error=invalid");

    if (!user.IsApproved)
        return Results.Redirect("/login?error=unapproved");

    var result = await signInManager.PasswordSignInAsync(user, password ?? string.Empty, isPersistent: true, lockoutOnFailure: false);
    if (!result.Succeeded)
        return Results.Redirect("/login?error=invalid");

    return Results.Redirect(string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl);
}).DisableAntiforgery();

app.MapPost("/auth/quick-login", async (
    SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager,
    [FromForm] string email) =>
{
    var user = await userManager.FindByEmailAsync(email);
    if (user is not null && user.IsApproved)
    {
        await signInManager.SignInAsync(user, isPersistent: true);
        return Results.Redirect("/");
    }
    return Results.Redirect("/login?error=invalid");
}).DisableAntiforgery();

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
        .AsNoTracking()
        .FirstOrDefaultAsync(b => b.Id == batchId);

    if (batch is null)
        return Results.NotFound();

    byte[] zipBytes = storage.CreateBatchSlicerZipArchive(batch);
    string zipFileName = $"Buildplate_{ManagedStorageService.Slugify(batch.BatchName, "batch")}.zip";
    return Results.File(zipBytes, "application/zip", zipFileName);
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program { }
