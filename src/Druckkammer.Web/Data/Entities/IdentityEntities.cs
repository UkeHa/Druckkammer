using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace Druckkammer.Web.Data.Entities;

public class ApplicationUser : IdentityUser
{
    [MaxLength(100)]
    public string DisplayName { get; set; } = string.Empty;

    [MaxLength(20)]
    public string AvatarColor { get; set; } = "#7E57C2";

    public bool IsApproved { get; set; } = true;

    [MaxLength(100)]
    public string? DiscordUserId { get; set; }

    [MaxLength(100)]
    public string? TelegramChatId { get; set; }

    public bool EmailNotificationsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class InviteToken
{
    public int Id { get; set; }

    [Required, MaxLength(64)]
    public string Code { get; set; } = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    [MaxLength(200)]
    public string Description { get; set; } = string.Empty;

    [Required, MaxLength(64)]
    public string AssignedRole { get; set; } = AppRoles.Requester;

    public int MaxUses { get; set; } = 5;

    public int UsedCount { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public bool IsActive { get; set; } = true;

    [MaxLength(100)]
    public string? CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool IsValid => IsActive && UsedCount < MaxUses && (!ExpiresAt.HasValue || ExpiresAt.Value > DateTime.UtcNow);
}

public class SystemSetting
{
    [Key, MaxLength(100)]
    public string Key { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string Value { get; set; } = string.Empty;

    [MaxLength(300)]
    public string Description { get; set; } = string.Empty;
}

public enum WebhookProviderType
{
    Discord,
    Telegram,
    GenericJson,
    SmtpEmail
}

public class NotificationWebhook
{
    public int Id { get; set; }

    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    public WebhookProviderType ProviderType { get; set; } = WebhookProviderType.Discord;

    [MaxLength(500)]
    public string WebhookUrl { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? SecretOrBotToken { get; set; }

    [MaxLength(200)]
    public string? TargetChatOrEmail { get; set; }

    public bool IsEnabled { get; set; } = true;

    public bool NotifyOnNewRequest { get; set; } = true;

    public bool NotifyOnBatchThresholdReached { get; set; } = true;

    public bool NotifyOnStatusChange { get; set; } = true;

    public bool NotifyOnRequestReady { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
