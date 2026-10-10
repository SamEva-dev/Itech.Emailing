using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Itech.Emailing.Abstractions;
using Itech.Emailing.Internal;
using Itech.Emailing.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Itech.Emailing.Services;

internal sealed partial class EmailingService
{
    public Task<Guid> QueueHtmlOnceAsync(string toEmail, string subject, string htmlContent,
        string? textContent, IReadOnlyCollection<EmailAttachment>? attachments, EmailUseCaseTags tags,
        string idempotencyKey, Guid? tenantId, CancellationToken cancellationToken) =>
        QueueOnceCoreAsync(toEmail, subject, htmlContent, textContent, null, null,
            attachments, tags, idempotencyKey, tenantId, cancellationToken);

    public Task<Guid> QueueTemplateOnceAsync(string toEmail, int templateId, object templateParams,
        IReadOnlyCollection<EmailAttachment>? attachments, EmailUseCaseTags tags,
        string idempotencyKey, Guid? tenantId, CancellationToken cancellationToken)
    {
        if (templateId <= 0) throw new ArgumentOutOfRangeException(nameof(templateId));
        return QueueOnceCoreAsync(toEmail, $"Template:{templateId}", null, null, templateId,
            JsonSerializer.Serialize(templateParams), attachments, tags, idempotencyKey, tenantId, cancellationToken);
    }

    private async Task<Guid> QueueOnceCoreAsync(string toEmail, string subject, string? html, string? text,
        int? templateId, string? templateJson, IReadOnlyCollection<EmailAttachment>? attachments,
        EmailUseCaseTags tags, string key, Guid? tenantId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length is < 12 or > 200 ||
            key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is ':' or '.' or '_' or '-')))
            throw new ArgumentException("EMAIL_IDEMPOTENCY_KEY_INVALID", nameof(key));
        if (string.IsNullOrWhiteSpace(toEmail) || string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException("EMAIL_RECIPIENT_OR_SUBJECT_INVALID");
        if (attachments is { Count: > 20 }) throw new ArgumentException("EMAIL_TOO_MANY_ATTACHMENTS");

        var useCaseTags = TagCatalog.ResolveUseCaseTags(tags);
        var contextTags = TagCatalog.ParseCsv(_brevo.ContextTagsCsv);
        var contextCsv = string.Join(',', contextTags);
        var useCaseCsv = string.Join(',', useCaseTags);
        var digest = PayloadHash(toEmail, subject, html, text, templateId, templateJson,
            attachments, contextCsv, useCaseCsv, tenantId);

        var email = new EmailMessageEntity
        {
            TenantId = tenantId, IdempotencyKey = key, PayloadSha256 = digest,
            ToEmail = toEmail, Subject = subject, HtmlContent = html, TextContent = text,
            TemplateId = templateId, TemplateParamsJson = templateJson,
            ContextTagsCsv = contextCsv, UseCaseTagsCsv = useCaseCsv,
            Status = EmailStatus.Queued, NextAttemptAtUtc = DateTime.UtcNow
        };
        if (attachments is not null)
            foreach (var a in attachments)
                email.Attachments.Add(new EmailAttachmentEntity
                {
                    FileName = a.FileName, ContentType = a.ContentType, Content = a.Content
                });
        _db.EmailMessages.Add(email);
        try
        {
            await _db.SaveChangesAsync(ct);
            return email.Id;
        }
        catch (DbUpdateException ex) when (IsUniqueConflict(ex))
        {
            // PostgreSQL uniqueness serializes concurrent inserts (one commits, the other
            // receives 23505). Do not retry insertion and never swallow unrelated DB errors.
            _db.ChangeTracker.Clear();
            var existing = await _db.EmailMessages.AsNoTracking()
                .Where(x => x.IdempotencyKey == key)
                .Select(x => new { x.Id, x.PayloadSha256, x.TenantId })
                .SingleOrDefaultAsync(ct);
            if (existing is null) throw;
            if (existing.TenantId != tenantId ||
                !string.Equals(existing.PayloadSha256, digest, StringComparison.Ordinal))
                throw new InvalidOperationException("EMAIL_IDEMPOTENCY_KEY_CONFLICT");
            _logger.LogInformation("Email enqueue deduplicated for message {EmailId}", existing.Id);
            return existing.Id;
        }
    }

    private static bool IsUniqueConflict(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: "23505" } or
            SqliteException { SqliteErrorCode: 19 };

    private static string PayloadHash(string to, string subject, string? html, string? text,
        int? templateId, string? templateJson, IReadOnlyCollection<EmailAttachment>? attachments,
        string context, string useCase, Guid? tenantId)
    {
        var attachmentDescriptors = attachments?.Select(a => new
        {
            a.FileName, a.ContentType,
            Digest = Convert.ToHexString(SHA256.HashData(a.Content))
        }).ToArray();
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            TenantId = tenantId, To = to.Trim().ToLowerInvariant(), Subject = subject,
            Html = html, Text = text, TemplateId = templateId, Template = templateJson,
            Context = context, UseCase = useCase, Attachments = attachmentDescriptors
        });
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }
}
