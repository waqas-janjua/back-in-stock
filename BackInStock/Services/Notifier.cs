using BackInStock.Data;
using Microsoft.EntityFrameworkCore;

namespace BackInStock.Services;

public sealed record NotifyResult(bool InStock, int Sent, int Failed);

/// <summary>
/// Sends restock emails. Exactly-once is built on an atomic claim:
/// pending -> sending (only one caller wins the UPDATE), then sending -> sent after the email API accepts it.
/// </summary>
public sealed class Notifier(IServiceScopeFactory scopes, StockClient stock, Mailer mailer, ILogger<Notifier> log)
{
    private static readonly TimeSpan StaleClaim = TimeSpan.FromMinutes(2);

    public async Task<NotifyResult> ProcessSkuAsync(string sku, CancellationToken ct)
    {
        // Always verify against the stock API; webhook payloads are only a hint.
        var product = await stock.GetAsync(sku, ct);
        if (!product.InStock) return new NotifyResult(false, 0, 0);

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();

        await ReleaseStaleAsync(db, ct);

        var ids = await db.Requests.AsNoTracking()
            .Where(r => r.Sku == sku && r.Status == RequestStatus.Pending)
            .OrderBy(r => r.CreatedAt).Select(r => r.Id).ToListAsync(ct);

        int sent = 0, failed = 0;
        foreach (var id in ids)
        {
            var now = DateTime.UtcNow;
            var claimed = await db.Requests
                .Where(r => r.Id == id && r.Status == RequestStatus.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, RequestStatus.Sending)
                    .SetProperty(r => r.ClaimedAt, now)
                    .SetProperty(r => r.Attempts, r => r.Attempts + 1), ct);
            if (claimed == 0) continue; // someone else owns it

            var req = await db.Requests.AsNoTracking().FirstAsync(r => r.Id == id, ct);
            try
            {
                await mailer.SendAsync(req, product, ct);
                var at = DateTime.UtcNow;
                await db.Requests.Where(r => r.Id == id).ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, RequestStatus.Sent)
                    .SetProperty(r => r.NotifiedAt, at)
                    .SetProperty(r => r.LastError, (string?)null), CancellationToken.None);
                sent++;
            }
            catch (Exception ex)
            {
                // Release the claim so a retry (webhook redelivery or the sweeper) picks it up.
                log.LogWarning("Email for {Id} failed: {Msg}", id, ex.Message);
                var msg = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
                await db.Requests.Where(r => r.Id == id).ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, RequestStatus.Pending)
                    .SetProperty(r => r.LastError, msg), CancellationToken.None);
                failed++;
            }
        }
        return new NotifyResult(true, sent, failed);
    }

    /// <summary>A claim older than StaleClaim means the process died mid-send; make it eligible again.</summary>
    private static Task<int> ReleaseStaleAsync(AppDb db, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - StaleClaim;
        return db.Requests
            .Where(r => r.Status == RequestStatus.Sending && r.ClaimedAt < cutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RequestStatus.Pending), ct);
    }

    public async Task SweepAsync(CancellationToken ct)
    {
        List<string> skus;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            await ReleaseStaleAsync(db, ct);
            skus = await db.Requests.AsNoTracking()
                .Where(r => r.Status == RequestStatus.Pending).Select(r => r.Sku).Distinct().ToListAsync(ct);
        }
        foreach (var sku in skus)
        {
            try { await ProcessSkuAsync(sku, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Sweep for {Sku} failed: {Msg}", sku, ex.Message);
            }
        }
    }
}

/// <summary>Safety net: catches missed webhooks, failed emails and crashes. Runs at startup and every 15s.</summary>
public sealed class SweeperService(Notifier notifier, ILogger<SweeperService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try { await notifier.SweepAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("Sweep error: {Msg}", ex.Message); }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
