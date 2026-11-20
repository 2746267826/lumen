using Lumen.Server.Data;
using Lumen.Server.Infrastructure;
using Lumen.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lumen.Server.Services;

public sealed class CleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<LumenOptions> options,
    ILogger<CleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await CleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Lumen cleanup pass failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LumenDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<StorageService>();
        var now = DateTimeOffset.UtcNow;

        var expiredSessions = await db.Sessions
            .Where(x => x.ExpiresAt < now.AddDays(-1))
            .ExecuteDeleteAsync(ct);

        var shareCutoff = now.AddDays(-30);
        var staleShares = await db.Shares
            .Where(x => x.ExpiresAt < shareCutoff || x.Revoked)
            .ToListAsync(ct);
        foreach (var share in staleShares)
        {
            storage.DeleteShare(share.Id, share.StoragePath);
        }

        if (staleShares.Count > 0)
        {
            db.Shares.RemoveRange(staleShares);
        }

        var expiredShareFiles = await db.Shares
            .Where(x => x.ExpiresAt <= now && x.StoragePath != null)
            .ToListAsync(ct);
        foreach (var share in expiredShareFiles)
        {
            storage.DeleteShare(share.Id, share.StoragePath);
            share.StoragePath = null;
            share.Size = 0;
        }

        var cutoff = now.AddDays(-Math.Max(options.Value.RetentionDays, 1));
        var expiredTransfers = await db.Transfers
            .Where(x =>
                x.UpdatedAt < cutoff &&
                (x.Status == TransferStatuses.Completed ||
                 x.Status == TransferStatuses.Declined ||
                 x.Status == TransferStatuses.Cancelled ||
                 x.Status == TransferStatuses.Failed))
            .ToListAsync(ct);
        foreach (var transfer in expiredTransfers)
        {
            storage.DeleteTransfer(transfer.Id);
        }

        if (expiredTransfers.Count > 0)
        {
            db.Transfers.RemoveRange(expiredTransfers);
        }

        await db.SaveChangesAsync(ct);
        if (expiredSessions + staleShares.Count + expiredTransfers.Count > 0)
        {
            logger.LogInformation(
                "Cleanup removed {Sessions} sessions, {Shares} shares, {Transfers} transfers",
                expiredSessions,
                staleShares.Count,
                expiredTransfers.Count);
        }
    }
}
