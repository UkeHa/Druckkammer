using System.Net;
using HtmlAgilityPack;

namespace Druckkammer.Web.Services;

public record OpenGraphMetadata(
    bool IsSuccess,
    string Title,
    string Description,
    string? ImageUrl,
    string SiteName,
    string SourceUrl);

public class OpenGraphService(IHttpClientFactory httpClientFactory, ILogger<OpenGraphService> logger)
{
    public async Task<OpenGraphMetadata> FetchMetadataAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return new OpenGraphMetadata(false, string.Empty, string.Empty, null, string.Empty, url);
        }

        string fallbackSite = GuessStudioOrPlatform(uri.Host);
        string fallbackTitle = GuessTitleFromPath(uri);

        try
        {
            using var client = httpClientFactory.CreateClient("OpenGraph");
            client.Timeout = TimeSpan.FromSeconds(6);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Druckkammer/1.0; +https://github.com)");

            using var response = await client.GetAsync(uri);
            if (!response.IsSuccessStatusCode)
            {
                return new OpenGraphMetadata(true, fallbackTitle, $"Importiert von {fallbackSite}", null, fallbackSite, uri.ToString());
            }

            string html = await response.Content.ReadAsStringAsync();
            return ParseOpenGraphHtml(html, uri.ToString(), fallbackTitle, fallbackSite);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OpenGraph Abruf für {Url} fehlgeschlagen – verwende URL-Fallback.", url);
            return new OpenGraphMetadata(true, fallbackTitle, $"Externes 3D-Modell auf {fallbackSite}", null, fallbackSite, uri.ToString());
        }
    }

    public static OpenGraphMetadata ParseOpenGraphHtml(string html, string sourceUrl, string fallbackTitle = "3D-Modell", string fallbackSite = "External")
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        string? ogTitle = GetMetaContent(doc, "og:title") ?? GetMetaContent(doc, "twitter:title");
        string? ogDesc = GetMetaContent(doc, "og:description") ?? GetMetaContent(doc, "description");
        string? ogImage = GetMetaContent(doc, "og:image") ?? GetMetaContent(doc, "twitter:image");
        string? ogSite = GetMetaContent(doc, "og:site_name");

        if (string.IsNullOrWhiteSpace(ogTitle))
        {
            ogTitle = doc.DocumentNode.SelectSingleNode("//title")?.InnerText;
        }

        string cleanTitle = WebUtility.HtmlDecode(ogTitle ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleanTitle))
            cleanTitle = fallbackTitle;

        string cleanDesc = WebUtility.HtmlDecode(ogDesc ?? string.Empty).Trim();
        string cleanSite = WebUtility.HtmlDecode(ogSite ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleanSite))
            cleanSite = fallbackSite;

        return new OpenGraphMetadata(true, cleanTitle, cleanDesc, ogImage, cleanSite, sourceUrl);
    }

    private static string? GetMetaContent(HtmlDocument doc, string propertyOrName)
    {
        var node = doc.DocumentNode.SelectSingleNode($"//meta[@property='{propertyOrName}']")
                   ?? doc.DocumentNode.SelectSingleNode($"//meta[@name='{propertyOrName}']");
        return node?.GetAttributeValue("content", null);
    }

    private static string GuessStudioOrPlatform(string host)
    {
        var h = host.ToLowerInvariant();
        if (h.Contains("myminifactory")) return "MyMiniFactory";
        if (h.Contains("printables")) return "Printables";
        if (h.Contains("thingiverse")) return "Thingiverse";
        if (h.Contains("patreon")) return "Patreon";
        if (h.Contains("cults3d")) return "Cults3D";
        if (h.Contains("makerworld")) return "MakerWorld";
        return host.Replace("www.", "");
    }

    private static string GuessTitleFromPath(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return "Externes 3D-Modell";
        var last = segments[^1];
        // Entferne reine ID-Nummern am Ende falls vorhanden
        if (int.TryParse(last, out _) && segments.Length >= 2)
            last = segments[^2];
        return WebUtility.UrlDecode(last).Replace('-', ' ').Replace('_', ' ').Trim();
    }
}
