using Microsoft.EntityFrameworkCore;
using VendorFunding.Infrastructure.Persistence;

namespace VendorFunding.Api.Services;

/// <summary>
/// Seeds demo data in deployed environments, where the pipeline (not the app) applies migrations.
/// Runs in the background and retries, so startup never waits on a database that is still
/// resuming from auto-pause or hasn't been migrated yet.
/// </summary>
public sealed class DemoDataBackgroundSeeder(IServiceScopeFactory scopes, ILogger<DemoDataBackgroundSeeder> logger)
    : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private const int MaxAttempts = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<VendorFundingDbContext>();

                if ((await db.Database.GetPendingMigrationsAsync(stoppingToken)).Any())
                {
                    logger.LogInformation("Database has pending migrations; demo data will be seeded after they're applied.");
                }
                else
                {
                    await DemoDataSeeder.SeedAsync(db, stoppingToken);
                    logger.LogInformation("Demo data is in place.");
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Demo data seeding attempt {Attempt} of {MaxAttempts} failed.", attempt, MaxAttempts);
            }

            await Task.Delay(RetryDelay, stoppingToken);
        }
    }
}
