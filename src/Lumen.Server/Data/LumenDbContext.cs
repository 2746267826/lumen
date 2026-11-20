using Lumen.Server.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Lumen.Server.Data;

public sealed class LumenDbContext(DbContextOptions<LumenDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<UserSession> Sessions => Set<UserSession>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Transfer> Transfers => Set<Transfer>();
    public DbSet<TransferChunk> TransferChunks => Set<TransferChunk>();
    public DbSet<TransferEvent> TransferEvents => Set<TransferEvent>();
    public DbSet<Share> Shares => Set<Share>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var dateTimeConverter = new ValueConverter<DateTimeOffset, long>(
            value => value.ToUnixTimeMilliseconds(),
            value => DateTimeOffset.FromUnixTimeMilliseconds(value));
        var nullableDateTimeConverter = new ValueConverter<DateTimeOffset?, long?>(
            value => value.HasValue ? value.Value.ToUnixTimeMilliseconds() : null,
            value => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null);

        foreach (var entityType in model.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(dateTimeConverter);
                }
                else if (property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(nullableDateTimeConverter);
                }
            }
        }

        model.Entity<User>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.Username).HasMaxLength(64);
            e.Property(x => x.DisplayName).HasMaxLength(80);
            e.Property(x => x.PasswordHash).HasMaxLength(512);
        });

        model.Entity<UserSession>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Device>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.LastSeenAt });
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Platform).HasMaxLength(40);
            e.Property(x => x.Model).HasMaxLength(120);
            e.Property(x => x.AppVersion).HasMaxLength(40);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Transfer>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.SenderDeviceId, x.CreatedAt });
            e.HasIndex(x => new { x.RecipientDeviceId, x.CreatedAt });
            e.Property(x => x.Name).HasMaxLength(512);
            e.Property(x => x.MimeType).HasMaxLength(200);
            e.Property(x => x.Status).HasMaxLength(24);
            e.Property(x => x.Route).HasMaxLength(16);
            e.HasOne(x => x.SenderDevice).WithMany().HasForeignKey(x => x.SenderDeviceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.RecipientDevice).WithMany().HasForeignKey(x => x.RecipientDeviceId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<TransferChunk>(e =>
        {
            e.HasKey(x => new { x.TransferId, x.Index });
            e.HasOne(x => x.Transfer).WithMany(x => x.Chunks).HasForeignKey(x => x.TransferId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<TransferEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TransferId, x.At });
            e.HasOne(x => x.Transfer).WithMany(x => x.Events).HasForeignKey(x => x.TransferId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Share>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.Property(x => x.Token).HasMaxLength(80);
            e.Property(x => x.Name).HasMaxLength(512);
            e.Property(x => x.MimeType).HasMaxLength(200);
            e.Property(x => x.PasswordHash).HasMaxLength(512);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
