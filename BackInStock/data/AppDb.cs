using Microsoft.EntityFrameworkCore;

namespace BackInStock.Data;

public static class RequestStatus
{
    public const string Pending = "pending";
    public const string Sending = "sending";
    public const string Sent = "sent";
}

public sealed class NotifyRequest
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string Sku { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string Status { get; set; } = RequestStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClaimedAt { get; set; }
    public DateTime? NotifiedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

public sealed class StockEvent
{
    public string EventId { get; set; } = "";
    public string Sku { get; set; } = "";
    public int Quantity { get; set; }
    public DateTime ReceivedAt { get; set; }
    public int Deliveries { get; set; } = 1;
}

public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<NotifyRequest> Requests => Set<NotifyRequest>();
    public DbSet<StockEvent> Events => Set<StockEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<NotifyRequest>(e =>
        {
            e.ToTable("Requests");
            e.HasKey(r => r.Id);
            e.Property(r => r.Email).IsRequired().HasMaxLength(254);
            e.Property(r => r.Sku).IsRequired().HasMaxLength(64);
            e.Property(r => r.Status).IsRequired().HasMaxLength(16);
            // One open (not yet sent) request per (email, sku).
            e.HasIndex(r => new { r.Email, r.Sku }).IsUnique().HasFilter("\"Status\" <> 'sent'");
            e.HasIndex(r => new { r.Sku, r.Status });
        });
        b.Entity<StockEvent>(e =>
        {
            e.ToTable("Events");
            e.HasKey(x => x.EventId);
        });
    }
}
