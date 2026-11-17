using System.Net.Http.Json;
using Druckkammer.Web.Data;
using Druckkammer.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Druckkammer.Web.Services;

public class NotificationService(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    ILogger<NotificationService> logger)
{
    public async Task NotifyNewRequestAsync(string title, string requesterName, int itemCount, double totalVolumeMl)
    {
        string message = $"🆕 **Neuer Druckwunsch in der Druckkammer!**\n" +
                         $"👤 **Von:** {requesterName}\n" +
                         $"📦 **Auftrag:** {title} ({itemCount} Position(en), ca. {totalVolumeMl:F1} ml)";
        await BroadcastAsync(message, w => w.NotifyOnNewRequest);
    }

    public async Task NotifyStatusChangedAsync(string title, string requesterName, PrintRequestStatus newStatus)
    {
        string statusText = GetGermanStatusName(newStatus);
        string emoji = newStatus == PrintRequestStatus.ReadyForPickup ? "✅🎉" : "🖨️";
        string message = $"{emoji} **Status-Update für {requesterName}:**\n" +
                         $"Modell/Auftrag **{title}** ist jetzt im Status: **{statusText}**";

        bool isReady = newStatus == PrintRequestStatus.ReadyForPickup;
        await BroadcastAsync(message, w => isReady ? w.NotifyOnRequestReady : w.NotifyOnStatusChange);
    }

    public async Task SendTestNotificationAsync(NotificationWebhook webhook)
    {
        string msg = $"🔔 **Druckkammer Test-Benachrichtigung** für Kanal *{webhook.Name}* ({webhook.ProviderType}) erfolgreich!";
        await SendToSingleWebhookAsync(webhook, msg);
    }

    private async Task BroadcastAsync(string message, Func<NotificationWebhook, bool> filter)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var hooks = await db.NotificationWebhooks.AsNoTracking().Where(w => w.IsEnabled).ToListAsync();

            foreach (var hook in hooks.Where(filter))
            {
                await SendToSingleWebhookAsync(hook, message);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fehler beim Senden von Webhook-Benachrichtigungen.");
        }
    }

    private async Task SendToSingleWebhookAsync(NotificationWebhook hook, string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hook.WebhookUrl) && hook.ProviderType != WebhookProviderType.SmtpEmail)
                return;

            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);

            switch (hook.ProviderType)
            {
                case WebhookProviderType.Discord:
                    await client.PostAsJsonAsync(hook.WebhookUrl, new
                    {
                        username = "Druckkammer Bot",
                        content = message
                    });
                    break;

                case WebhookProviderType.Telegram:
                    string tgUrl = hook.WebhookUrl;
                    if (!string.IsNullOrWhiteSpace(hook.SecretOrBotToken) && !tgUrl.Contains("api.telegram.org"))
                    {
                        tgUrl = $"https://api.telegram.org/bot{hook.SecretOrBotToken}/sendMessage";
                    }
                    await client.PostAsJsonAsync(tgUrl, new
                    {
                        chat_id = hook.TargetChatOrEmail,
                        text = message,
                        parse_mode = "Markdown"
                    });
                    break;

                case WebhookProviderType.GenericJson:
                    await client.PostAsJsonAsync(hook.WebhookUrl, new
                    {
                        source = "Druckkammer",
                        timestamp = DateTime.UtcNow,
                        message
                    });
                    break;

                case WebhookProviderType.SmtpEmail:
                    logger.LogInformation("[SMTP Benachrichtigung -> {Target}]: {Message}", hook.TargetChatOrEmail, message);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Webhook {Name} ({Type}) konnte nicht erreicht werden.", hook.Name, hook.ProviderType);
        }
    }

    public static string GetGermanStatusName(PrintRequestStatus status) => status switch
    {
        PrintRequestStatus.CartDraft => "Im Session-Warenkorb",
        PrintRequestStatus.RequestedOpen => "Offen / Gewünscht",
        PrintRequestStatus.ScheduledOnPlate => "Eingeplant (Auf Buildplate)",
        PrintRequestStatus.Printing => "Druckt gerade",
        PrintRequestStatus.PostProcessing => "Nachbearbeitung (Waschen/Härten)",
        PrintRequestStatus.ReadyForPickup => "Fertig / Abholbereit",
        PrintRequestStatus.CompletedHandedOver => "Übergeben / Archiviert",
        PrintRequestStatus.OnHoldQuestion => "Rückfrage / Pausiert",
        PrintRequestStatus.Rejected => "Abgelehnt",
        _ => status.ToString()
    };
}
