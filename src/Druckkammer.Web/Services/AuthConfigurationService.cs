using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using Druckkammer.Web.Data;
using Druckkammer.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using QRCoder;

namespace Druckkammer.Web.Services;

public record OidcConfigModel(
    bool Enabled,
    string ProviderName,
    string Authority,
    string ClientId,
    string ClientSecret,
    string Scopes,
    bool AutoApproveUsers,
    string DefaultRole);

public record OidcUserIdentity(
    string Subject,
    string Email,
    string DisplayName);

public class AuthConfigurationService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IConfiguration config,
    IHttpClientFactory httpClientFactory,
    ILogger<AuthConfigurationService> logger)
{
    public const string KeyOidcEnabled = "Auth.Oidc.Enabled";
    public const string KeyOidcProviderName = "Auth.Oidc.ProviderName";
    public const string KeyOidcAuthority = "Auth.Oidc.Authority";
    public const string KeyOidcClientId = "Auth.Oidc.ClientId";
    public const string KeyOidcClientSecret = "Auth.Oidc.ClientSecret";
    public const string KeyOidcScopes = "Auth.Oidc.Scopes";
    public const string KeyOidcAutoApprove = "Auth.Oidc.AutoApproveUsers";
    public const string KeyOidcDefaultRole = "Auth.Oidc.DefaultRole";

    public async Task<OidcConfigModel> GetOidcConfigAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var settings = await db.SystemSettings
            .AsNoTracking()
            .Where(s => s.Key.StartsWith("Auth.Oidc."))
            .ToDictionaryAsync(s => s.Key, s => s.Value);

        string GetVal(string dbKey, string configPath, string fallback) =>
            settings.TryGetValue(dbKey, out var v) && !string.IsNullOrWhiteSpace(v)
                ? v
                : (config[configPath] ?? fallback);

        string authority = GetVal(KeyOidcAuthority, "Druckkammer:OpenIdConnect:Authority", "");
        string clientId = GetVal(KeyOidcClientId, "Druckkammer:OpenIdConnect:ClientId", "");
        string enabledRaw = GetVal(KeyOidcEnabled, "Druckkammer:OpenIdConnect:Enabled", "");

        bool isEnabled = !string.IsNullOrWhiteSpace(enabledRaw)
            ? enabledRaw.Equals("true", StringComparison.OrdinalIgnoreCase)
            : (!string.IsNullOrWhiteSpace(authority) && !string.IsNullOrWhiteSpace(clientId));

        return new OidcConfigModel(
            Enabled: isEnabled && !string.IsNullOrWhiteSpace(authority) && !string.IsNullOrWhiteSpace(clientId),
            ProviderName: GetVal(KeyOidcProviderName, "Druckkammer:OpenIdConnect:ProviderName", "OpenID Connect (SSO)"),
            Authority: authority.TrimEnd('/'),
            ClientId: clientId.Trim(),
            ClientSecret: GetVal(KeyOidcClientSecret, "Druckkammer:OpenIdConnect:ClientSecret", "").Trim(),
            Scopes: GetVal(KeyOidcScopes, "Druckkammer:OpenIdConnect:Scopes", "openid profile email"),
            AutoApproveUsers: GetVal(KeyOidcAutoApprove, "Druckkammer:OpenIdConnect:AutoApproveUsers", "true")
                .Equals("true", StringComparison.OrdinalIgnoreCase),
            DefaultRole: GetVal(KeyOidcDefaultRole, "Druckkammer:OpenIdConnect:DefaultRole", AppRoles.Requester)
        );
    }

    public async Task SaveOidcConfigAsync(OidcConfigModel model)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await UpsertSettingAsync(db, KeyOidcEnabled, model.Enabled ? "true" : "false", "OpenID Connect Login aktivieren");
        await UpsertSettingAsync(db, KeyOidcProviderName, model.ProviderName, "Anzeigename des OIDC-Providers auf der Login-Seite");
        await UpsertSettingAsync(db, KeyOidcAuthority, model.Authority, "OpenID Connect Authority / Issuer URL");
        await UpsertSettingAsync(db, KeyOidcClientId, model.ClientId, "OpenID Connect Client ID");
        await UpsertSettingAsync(db, KeyOidcClientSecret, model.ClientSecret, "OpenID Connect Client Secret");
        await UpsertSettingAsync(db, KeyOidcScopes, model.Scopes, "Angeforderte OIDC Scopes");
        await UpsertSettingAsync(db, KeyOidcAutoApprove, model.AutoApproveUsers ? "true" : "false", "Neue OIDC-Benutzer automatisch freischalten");
        await UpsertSettingAsync(db, KeyOidcDefaultRole, model.DefaultRole, "Standardrolle für neue OIDC-Benutzer");
        await db.SaveChangesAsync();
    }

    private static async Task UpsertSettingAsync(ApplicationDbContext db, string key, string value, string desc)
    {
        var existing = await db.SystemSettings.FindAsync(key);
        if (existing is null)
        {
            db.SystemSettings.Add(new SystemSetting { Key = key, Value = value ?? string.Empty, Description = desc });
        }
        else
        {
            existing.Value = value ?? string.Empty;
        }
    }

    /// <summary>
    /// Ermittelt den Authorization-Endpoint aus .well-known/openid-configuration und erstellt die Redirect-URL inkl. PKCE & State.
    /// </summary>
    public async Task<(string AuthorizationUrl, string State, string CodeVerifier)> BuildOidcAuthorizationUrlAsync(
        OidcConfigModel oidc,
        string callbackUrl)
    {
        using var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);

        string discoveryUrl = $"{oidc.Authority.TrimEnd('/')}/.well-known/openid-configuration";
        var doc = await client.GetFromJsonAsync<JsonElement>(discoveryUrl);
        string authEndpoint = doc.GetProperty("authorization_endpoint").GetString()
                              ?? throw new InvalidOperationException("Kein authorization_endpoint im OIDC-Discovery-Dokument gefunden.");

        string state = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        string codeVerifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        byte[] challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        string codeChallenge = Convert.ToBase64String(challengeBytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var query = new Dictionary<string, string>
        {
            ["client_id"] = oidc.ClientId,
            ["redirect_uri"] = callbackUrl,
            ["response_type"] = "code",
            ["scope"] = string.IsNullOrWhiteSpace(oidc.Scopes) ? "openid profile email" : oidc.Scopes,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };

        string queryString = string.Join("&", query.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
        return ($"{authEndpoint}?{queryString}", state, codeVerifier);
    }

    /// <summary>
    /// Tauscht den Authorization-Code beim OIDC Token-Endpoint gegen ID-Token / Access-Token ein und liest Subject, E-Mail & Anzeigename aus.
    /// </summary>
    public async Task<OidcUserIdentity?> ExchangeCodeForUserIdentityAsync(
        OidcConfigModel oidc,
        string code,
        string codeVerifier,
        string callbackUrl)
    {
        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            string discoveryUrl = $"{oidc.Authority.TrimEnd('/')}/.well-known/openid-configuration";
            var discovery = await client.GetFromJsonAsync<JsonElement>(discoveryUrl);
            string tokenEndpoint = discovery.GetProperty("token_endpoint").GetString()
                                   ?? throw new InvalidOperationException("Kein token_endpoint gefunden.");

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = oidc.ClientId,
                ["code"] = code,
                ["redirect_uri"] = callbackUrl,
                ["code_verifier"] = codeVerifier
            };
            if (!string.IsNullOrWhiteSpace(oidc.ClientSecret))
            {
                form["client_secret"] = oidc.ClientSecret;
            }

            using var tokenRes = await client.PostAsync(tokenEndpoint, new FormUrlEncodedContent(form));
            if (!tokenRes.IsSuccessStatusCode)
            {
                logger.LogWarning("OIDC Token-Austausch fehlgeschlagen mit Status {Status}", tokenRes.StatusCode);
                return null;
            }

            var tokenJson = await tokenRes.Content.ReadFromJsonAsync<JsonElement>();
            string? idToken = tokenJson.TryGetProperty("id_token", out var idTokProp) ? idTokProp.GetString() : null;
            string? accessToken = tokenJson.TryGetProperty("access_token", out var accTokProp) ? accTokProp.GetString() : null;

            string? sub = null;
            string? email = null;
            string? name = null;

            if (!string.IsNullOrWhiteSpace(idToken))
            {
                var handler = new JwtSecurityTokenHandler();
                var jwt = handler.ReadJwtToken(idToken);
                sub = jwt.Claims.FirstOrDefault(c => c.Type is "sub" or "nameid")?.Value;
                email = jwt.Claims.FirstOrDefault(c => c.Type is "email" or "upn")?.Value;
                name = jwt.Claims.FirstOrDefault(c => c.Type is "name" or "preferred_username" or "given_name")?.Value;
            }

            // Falls E-Mail oder Name nur über den UserInfo-Endpoint geliefert werden
            if ((string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(sub)) &&
                !string.IsNullOrWhiteSpace(accessToken) &&
                discovery.TryGetProperty("userinfo_endpoint", out var userInfoProp))
            {
                string? userInfoEndpoint = userInfoProp.GetString();
                if (!string.IsNullOrWhiteSpace(userInfoEndpoint))
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, userInfoEndpoint);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    using var uiRes = await client.SendAsync(req);
                    if (uiRes.IsSuccessStatusCode)
                    {
                        var uiJson = await uiRes.Content.ReadFromJsonAsync<JsonElement>();
                        sub ??= uiJson.TryGetProperty("sub", out var s) ? s.GetString() : null;
                        email ??= uiJson.TryGetProperty("email", out var e) ? e.GetString() : null;
                        name ??= uiJson.TryGetProperty("name", out var n) ? n.GetString()
                            : (uiJson.TryGetProperty("preferred_username", out var pu) ? pu.GetString() : null);
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(sub))
                return null;

            if (string.IsNullOrWhiteSpace(email))
                email = $"{sub}@oidc.local";

            if (string.IsNullOrWhiteSpace(name))
                name = email.Split('@')[0];

            return new OidcUserIdentity(sub, email, name);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fehler beim Verarbeiten des OpenID Connect Callbacks.");
            return null;
        }
    }

    // --- TOTP 2FA Helpers ---

    public static string FormatAuthenticatorKey(string unformattedKey)
    {
        var result = new StringBuilder();
        int currentPosition = 0;
        while (currentPosition + 4 < unformattedKey.Length)
        {
            result.Append(unformattedKey.AsSpan(currentPosition, 4)).Append(' ');
            currentPosition += 4;
        }
        if (currentPosition < unformattedKey.Length)
        {
            result.Append(unformattedKey.AsSpan(currentPosition));
        }
        return result.ToString().ToUpperInvariant();
    }

    public static string BuildOtpAuthUri(string email, string unformattedKey, string issuer = "Druckkammer")
    {
        string encodedIssuer = UrlEncoder.Default.Encode(issuer);
        string encodedEmail = UrlEncoder.Default.Encode(email);
        return $"otpauth://totp/{encodedIssuer}:{encodedEmail}?secret={unformattedKey}&issuer={encodedIssuer}&digits=6";
    }

    public static string GenerateTotpQrCodeSvg(string otpAuthUri)
    {
        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(otpAuthUri, QRCodeGenerator.ECCLevel.Q);
        var svgQrCode = new SvgQRCode(qrCodeData);
        return svgQrCode.GetGraphic(4, "#ECEFF1", "#181B24", true);
    }
}
