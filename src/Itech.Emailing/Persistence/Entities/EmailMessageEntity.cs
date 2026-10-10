using System;
using Itech.Emailing.Abstractions;

namespace Itech.Emailing.Persistence.Entities;

public sealed class EmailMessageEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? TenantId { get; set; }

    // Stable business-event key, globally unique (include tenant in the caller's namespace).
    public string? IdempotencyKey { get; set; }
    public string? PayloadSha256 { get; set; }

    // Fencing token assigned during an atomic PostgreSQL claim.
    public Guid? DispatchLeaseId { get; set; }

    public string ToEmail { get; set; } = default!;
    public string? ToName { get; set; }

    public string Subject { get; set; } = default!;

    // Template mode
    public int? TemplateId { get; set; }
    public string? TemplateParamsJson { get; set; }

    // Html mode
    public string? HtmlContent { get; set; }
    public string? TextContent { get; set; }

    // Provider correlation
    public string? ProviderMessageId { get; set; }

    // Tags
    public string? ContextTagsCsv { get; set; }   // level 1
    public string? UseCaseTagsCsv { get; set; }   // level 2 (resolved strings)

    // Status and retries
    public EmailStatus Status { get; set; } = EmailStatus.Queued;
    public int AttemptCount { get; set; } = 0;
    public DateTime? NextAttemptAtUtc { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public string? LastError { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastEventAtUtc { get; set; }

    public System.Collections.Generic.List<EmailAttachmentEntity> Attachments { get; set; } = new();
}
