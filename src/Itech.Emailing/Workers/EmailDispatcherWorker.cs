using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Itech.Emailing.Abstractions;
using Itech.Emailing.Internal;
using Itech.Emailing.Options;
using Itech.Emailing.Persistence;
using Itech.Emailing.Providers;
using Itech.Emailing.Providers.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itech.Emailing.Workers;

/// <summary>
/// PostgreSQL multi-instance dispatcher with atomic SKIP LOCKED claims and fencing tokens.
/// Ambiguous sends are quarantined, not retried automatically. This does not provide
/// exactly-once delivery at the external email provider.
/// </summary>
public sealed class EmailDispatcherWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EmailDispatcherWorker> _logger;
    private readonly IOptions<EmailDispatcherOptions> _options;

    public EmailDispatcherWorker(IServiceScopeFactory scopeFactory,
        ILogger<EmailDispatcherWorker> logger, IOptions<EmailDispatcherOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DispatchBatch(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Email dispatcher batch failed"); }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.Value.PollSeconds)), stoppingToken);
        }
    }

    private async Task DispatchBatch(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailingDbContext>();
        if (db.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL")
            throw new InvalidOperationException("S20 requires PostgreSQL for atomic email dispatch claims.");

        var brevo = scope.ServiceProvider.GetRequiredService<IOptions<BrevoOptions>>().Value;
        var now = DateTime.UtcNow;
        var batchSize = Math.Clamp(_options.Value.BatchSize, 1, 100);

        // A previous worker may have crashed AFTER the provider accepted a message.
        // Never automatically resend a timed-out Sending row: operator/provider
        // reconciliation is required to avoid a duplicate signature email.
        var quarantined = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE emailing."EmailMessages"
            SET "Status" = {(int)EmailStatus.Deferred},
                "NextAttemptAtUtc" = NULL,
                "LockedUntilUtc" = NULL,
                "DispatchLeaseId" = NULL,
                "LastError" = 'EMAIL_DELIVERY_OUTCOME_UNKNOWN'
            WHERE "Status" = {(int)EmailStatus.Sending}
              AND "LockedUntilUtc" < {now}
            """, ct);
        if (quarantined > 0)
            _logger.LogWarning("Quarantined {Count} emails whose sending lease expired; reconcile with provider", quarantined);

        // Messages scheduled by older worker versions have unknown retry semantics.
        // Preserve only 429 (explicit provider rejection); quarantine ambiguous retries.
        var legacy = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE emailing."EmailMessages"
            SET "Status" = {(int)EmailStatus.Deferred},
                "NextAttemptAtUtc" = NULL,
                "LastError" = 'EMAIL_LEGACY_RETRY_REQUIRES_RECONCILIATION'
            WHERE "Status" = {(int)EmailStatus.Failed}
              AND "NextAttemptAtUtc" IS NOT NULL
              AND ("LastError" IS NULL OR "LastError" NOT LIKE 'Brevo API error 429:%')
            """, ct);
        if (legacy > 0)
            _logger.LogWarning("Quarantined {Count} legacy retrying emails for reconciliation", legacy);

        // Claim only ONE email at a time, immediately before sending it. Claiming
        // a whole batch would let the lease expire before later emails are sent.
        var provider = CreateProvider(scope.ServiceProvider, brevo);
        for (var position = 0; position < batchSize; position++)
        {
            var lease = Guid.NewGuid();
            var claimedAt = DateTime.UtcNow;
            var lockUntil = claimedAt.AddMinutes(Math.Max(1, _options.Value.LockMinutes));
            // PostgreSQL locks the selected row and skips concurrent claims.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE emailing."EmailMessages" AS m
            SET "Status" = {(int)EmailStatus.Sending},
                "LockedUntilUtc" = {lockUntil},
                "DispatchLeaseId" = {lease},
                "AttemptCount" = m."AttemptCount" + 1
            WHERE m."Id" IN (
                SELECT e."Id" FROM emailing."EmailMessages" AS e
                WHERE (e."Status" = {(int)EmailStatus.Queued}
                       OR (e."Status" = {(int)EmailStatus.Failed}
                           AND e."NextAttemptAtUtc" IS NOT NULL))
                  AND (e."NextAttemptAtUtc" IS NULL OR e."NextAttemptAtUtc" <= {claimedAt})
                  AND (e."LockedUntilUtc" IS NULL OR e."LockedUntilUtc" < {claimedAt})
                ORDER BY e."CreatedAtUtc", e."Id"
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            )
            """, ct);

            var msg = await db.EmailMessages.AsNoTracking()
                .Include(x => x.Attachments)
                .SingleOrDefaultAsync(x => x.DispatchLeaseId == lease && x.Status == EmailStatus.Sending, ct);
            if (msg is null) break;

            ct.ThrowIfCancellationRequested();
            EmailStatus status;
            string? providerId = null;
            string? error = null;
            DateTime? retryAt = null;
            try
            {
                var contextTags = TagCatalog.ParseCsv(msg.ContextTagsCsv);
                var useCaseTags = TagCatalog.ParseCsv(msg.UseCaseTagsCsv);
                object? templateParams = null;
                if (!string.IsNullOrWhiteSpace(msg.TemplateParamsJson))
                    templateParams = JsonSerializer.Deserialize<object>(msg.TemplateParamsJson);
                var request = new SendRequest
                {
                    ToEmail = msg.ToEmail, ToName = msg.ToName,
                    Subject = msg.Subject, HtmlContent = msg.HtmlContent,
                    TextContent = msg.TextContent, TemplateId = msg.TemplateId,
                    TemplateParams = templateParams,
                    Attachments = msg.Attachments.Select(a => new SendAttachment
                    {
                        FileName = a.FileName, ContentType = a.ContentType, Content = a.Content
                    }).ToList(),
                    Tags = contextTags.Concat(useCaseTags)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                };
                var result = await provider.SendAsync(request, ct);
                providerId = result.ProviderMessageId;
                error = result.Error;
                if (result.Success) status = EmailStatus.Sent;
                else if (result.Retryable && result.Error?.StartsWith("Brevo API error 429:", StringComparison.Ordinal) == true
                         && msg.AttemptCount < brevo.MaxRetries)
                {
                    // 429 is an explicit rejection; it is safe to schedule another attempt.
                    status = EmailStatus.Failed;
                    retryAt = DateTime.UtcNow.Add(RetryPolicy.ComputeDelay(msg.AttemptCount));
                }
                else if (result.Retryable)
                    status = EmailStatus.Deferred; // 5xx, timeout or SMTP ambiguity
                else
                    status = EmailStatus.Failed;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Keep lease; next batch will quarantine it if the worker terminates.
                throw;
            }
            catch (Exception ex)
            {
                status = EmailStatus.Deferred;
                error = "EMAIL_DELIVERY_OUTCOME_UNKNOWN";
                _logger.LogError(ex, "Uncertain email provider outcome for {EmailId}", msg.Id);
            }

            // Fencing: a late provider response may not overwrite a row after the
            // lease was revoked/quarantined by another worker.
            var changed = await db.EmailMessages
                .Where(x => x.Id == msg.Id && x.DispatchLeaseId == lease && x.Status == EmailStatus.Sending)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(x => x.Status, status)
                    .SetProperty(x => x.ProviderMessageId, providerId)
                    .SetProperty(x => x.LastError, error)
                    .SetProperty(x => x.NextAttemptAtUtc, retryAt)
                    .SetProperty(x => x.LockedUntilUtc, (DateTime?)null)
                    .SetProperty(x => x.DispatchLeaseId, (Guid?)null)
                    .SetProperty(x => x.LastEventAtUtc,
                        status == EmailStatus.Sent ? (DateTime?)DateTime.UtcNow : null), ct);
            if (changed != 1)
                _logger.LogError("Email dispatch lease lost for {EmailId}; reconcile provider result manually", msg.Id);
            else if (status == EmailStatus.Deferred)
                _logger.LogWarning("Email {EmailId} outcome uncertain; manual reconciliation required", msg.Id);
        }
    }

    private static IEmailProvider CreateProvider(IServiceProvider sp, BrevoOptions brevo)
    {
        if (string.Equals(brevo.Mode, "BREVO_SMTP", StringComparison.OrdinalIgnoreCase))
            return new SmtpEmailProvider(brevo, sp.GetRequiredService<ILogger<SmtpEmailProvider>>());
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("BrevoApi");
        return new BrevoApiEmailProvider(http, brevo,
            sp.GetRequiredService<ILogger<BrevoApiEmailProvider>>());
    }
}
