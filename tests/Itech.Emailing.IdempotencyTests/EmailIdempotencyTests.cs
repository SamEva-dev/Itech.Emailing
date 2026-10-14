using Itech.Emailing.Abstractions;
using Itech.Emailing.Persistence;
using Itech.Emailing.Registration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Itech.Emailing.IdempotencyTests;

public sealed class EmailIdempotencyTests
{
    private static readonly Guid Tenant = Guid.Parse("60000000-0000-0000-0000-000000000001");
    private const string Key = "pedagora:event:60000000000000000000000000000002";

    private static async Task<(ServiceProvider Services, string File)> CreateAsync()
    {
        var file = Path.Combine(Path.GetTempPath(), $"itech-emailing-{Guid.NewGuid():N}.db");
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddItechEmailing(configuration, db => db.UseSqlite($"Data Source={file}"));
        var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<EmailingDbContext>().Database.EnsureCreatedAsync();
        return (provider, file);
    }

    private static async Task<Guid> QueueAsync(ServiceProvider services, string key, string html = "<p>Hello</p>",
        Guid? tenant = null)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IIdempotentEmailingService>()
            .QueueHtmlOnceAsync("learner@example.test", "Observation", html, "Hello", null,
                EmailUseCaseTags.NotificationSystem, key, tenant ?? Tenant, CancellationToken.None);
    }

    [Fact]
    public async Task Replaying_same_business_event_returns_existing_id()
    {
        var (services, file) = await CreateAsync();
        try
        {
            var first = await QueueAsync(services, Key);
            var second = await QueueAsync(services, Key);
            Assert.Equal(first, second);
            await using var scope = services.CreateAsyncScope();
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<EmailingDbContext>()
                .EmailMessages.CountAsync());
        }
        finally { await services.DisposeAsync(); File.Delete(file); }
    }

    [Fact]
    public async Task Reusing_key_with_changed_content_is_rejected()
    {
        var (services, file) = await CreateAsync();
        try
        {
            await QueueAsync(services, Key);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => QueueAsync(services, Key, "<p>Changed</p>"));
            Assert.Equal("EMAIL_IDEMPOTENCY_KEY_CONFLICT", ex.Message);
        }
        finally { await services.DisposeAsync(); File.Delete(file); }
    }

    [Fact]
    public async Task Two_distinct_reminders_can_be_queued_separately()
    {
        var (services, file) = await CreateAsync();
        try
        {
            var first = await QueueAsync(services, "pedagora:reminder:60000000000000000000000000000001");
            var second = await QueueAsync(services, "pedagora:reminder:60000000000000000000000000000002");
            Assert.NotEqual(first, second);
        }
        finally { await services.DisposeAsync(); File.Delete(file); }
    }

    [Fact]
    public async Task Reusing_key_across_tenants_is_rejected()
    {
        var (services, file) = await CreateAsync();
        try
        {
            await QueueAsync(services, Key);
            await using var scope = services.CreateAsyncScope();
            var emailing = scope.ServiceProvider.GetRequiredService<IIdempotentEmailingService>();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => emailing.QueueHtmlOnceAsync(
                "learner@example.test", "Observation", "<p>Hello</p>", "Hello", null,
                EmailUseCaseTags.NotificationSystem, Key,
                Guid.Parse("70000000-0000-0000-0000-000000000001"), CancellationToken.None));
            Assert.Equal("EMAIL_IDEMPOTENCY_KEY_CONFLICT", ex.Message);
        }
        finally { await services.DisposeAsync(); File.Delete(file); }
    }
}
