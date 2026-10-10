using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Itech.Emailing.Abstractions;

/// <summary>
/// Idempotent enqueue into the durable email database. A repeated key returns the
/// original queue id; reusing the key for a different recipient/payload is rejected.
/// This guarantees one queue row, NOT exactly-once external SMTP/API delivery.
/// </summary>
public interface IIdempotentEmailingService
{
    Task<Guid> QueueHtmlOnceAsync(string toEmail, string subject, string htmlContent,
        string? textContent, IReadOnlyCollection<EmailAttachment>? attachments,
        EmailUseCaseTags tags, string idempotencyKey, Guid? tenantId, CancellationToken cancellationToken);

    Task<Guid> QueueTemplateOnceAsync(string toEmail, int templateId, object templateParams,
        IReadOnlyCollection<EmailAttachment>? attachments, EmailUseCaseTags tags,
        string idempotencyKey, Guid? tenantId, CancellationToken cancellationToken);
}
