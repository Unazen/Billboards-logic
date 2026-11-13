using Billboard.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Billboard.Infrastructure.Persistence;

public class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsActive { get; set; } = true;
}

public class AppDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<BillboardDevice> Billboards => Set<BillboardDevice>();
    public DbSet<BillboardGroup> BillboardGroups => Set<BillboardGroup>();
    public DbSet<BillboardGroupMember> BillboardGroupMembers => Set<BillboardGroupMember>();
    public DbSet<DeviceHeartbeat> DeviceHeartbeats => Set<DeviceHeartbeat>();
    public DbSet<DeviceCachedVideo> DeviceCachedVideos => Set<DeviceCachedVideo>();

    public DbSet<VideoAsset> VideoAssets => Set<VideoAsset>();
    public DbSet<VideoVariant> VideoVariants => Set<VideoVariant>();
    public DbSet<UploadJob> UploadJobs => Set<UploadJob>();

    public DbSet<AdPlacement> AdPlacements => Set<AdPlacement>();
    public DbSet<PlaybackEvent> PlaybackEvents => Set<PlaybackEvent>();

    public DbSet<TariffPlan> TariffPlans => Set<TariffPlan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<WalletTransaction> WalletTransactions => Set<WalletTransaction>();
    public DbSet<BudgetReservation> BudgetReservations => Set<BudgetReservation>();
    public DbSet<OwnerEarning> OwnerEarnings => Set<OwnerEarning>();
    public DbSet<SystemLog> SystemLogs => Set<SystemLog>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Все деньги — decimal с фиксированной точностью (не double).
        configurationBuilder.Properties<decimal>().HavePrecision(18, 4);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<BillboardDevice>(e =>
        {
            e.ToTable("Billboards");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Address).HasMaxLength(500);
            e.Property(x => x.Latitude).HasPrecision(9, 6);
            e.Property(x => x.Longitude).HasPrecision(9, 6);
            e.Property(x => x.TimeZoneId).HasMaxLength(100);
            e.Property(x => x.DeviceTokenHash).HasMaxLength(100);
            e.HasIndex(x => x.OwnerId);
        });

        b.Entity<BillboardGroup>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.OwnerId);
        });

        b.Entity<BillboardGroupMember>(e =>
        {
            e.HasKey(x => new { x.BillboardGroupId, x.BillboardId });
            e.HasOne(x => x.Group).WithMany(g => g.Members).HasForeignKey(x => x.BillboardGroupId);
            e.HasOne(x => x.Billboard).WithMany().HasForeignKey(x => x.BillboardId);
        });

        b.Entity<DeviceHeartbeat>().HasIndex(x => new { x.BillboardId, x.ReceivedAt });
        b.Entity<DeviceCachedVideo>().HasIndex(x => new { x.BillboardId, x.VideoAssetId }).IsUnique();

        b.Entity<VideoAsset>(e =>
        {
            e.Property(x => x.OriginalFileName).HasMaxLength(255);
            e.Property(x => x.StorageKey).HasMaxLength(500);
            e.Property(x => x.ContentType).HasMaxLength(100);
            e.Property(x => x.Checksum).HasMaxLength(100);
            e.HasIndex(x => x.OwnerId);
            // Удалённые видео скрыты из всех запросов; история и ссылки сохраняются.
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<VideoVariant>(e =>
        {
            e.Property(x => x.StorageKey).HasMaxLength(500);
            e.HasOne(x => x.VideoAsset).WithMany(v => v.Variants).HasForeignKey(x => x.VideoAssetId);
            // Фильтр совпадает с фильтром родителя, чтобы не было предупреждений EF.
            e.HasQueryFilter(x => !x.VideoAsset!.IsDeleted);
        });

        b.Entity<UploadJob>().HasIndex(x => x.VideoAssetId);

        b.Entity<AdPlacement>(e =>
        {
            e.HasIndex(x => new { x.BillboardId, x.Status });
            e.HasIndex(x => x.OwnerId);
            // Выборка действующих размещений билборда по времени: манифест и проверка ёмкости.
            e.HasIndex(x => new { x.BillboardId, x.StartAt, x.EndAt });
            e.HasOne(x => x.VideoAsset).WithMany().HasForeignKey(x => x.VideoAssetId);
            e.HasOne(x => x.Billboard).WithMany().HasForeignKey(x => x.BillboardId);
            e.Property(x => x.PauseReason).HasMaxLength(100);
        });

        b.Entity<PlaybackEvent>(e =>
        {
            // Ключ в БД = "{billboardId}:{ключ устройства}", поэтому колонка длиннее DTO-лимита.
            e.Property(x => x.IdempotencyKey).HasMaxLength(300);
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => x.PlacementId);
            e.HasIndex(x => new { x.BillboardId, x.StartedAt });
        });

        b.Entity<TariffPlan>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<Subscription>(e =>
        {
            e.HasOne(x => x.TariffPlan).WithMany().HasForeignKey(x => x.TariffPlanId);
            e.HasIndex(x => new { x.UserId, x.Status });
        });

        b.Entity<Wallet>(e =>
        {
            e.HasIndex(x => x.UserId).IsUnique();
            e.Property(x => x.Currency).HasMaxLength(8);
            e.Property(x => x.Version).IsConcurrencyToken();
        });

        b.Entity<WalletTransaction>(e =>
        {
            e.Property(x => x.IdempotencyKey).HasMaxLength(300);
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => x.WalletId);
        });

        b.Entity<BudgetReservation>().HasIndex(x => x.PlacementId).IsUnique();
        b.Entity<OwnerEarning>().HasIndex(x => x.PlaybackEventId).IsUnique();
        b.Entity<SystemLog>().HasIndex(x => x.CreatedAt);

        // Пользователь, видео, билборд и история не должны удаляться каскадом.
        foreach (var fk in b.Model.GetEntityTypes()
                     .Where(t => t.ClrType.Namespace == typeof(BillboardDevice).Namespace)
                     .SelectMany(t => t.GetForeignKeys()))
        {
            fk.DeleteBehavior = DeleteBehavior.Restrict;
        }

        // Ссылки на пользователя Identity (без навигации, чтобы Domain не зависел от Identity).
        b.Entity<Wallet>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<VideoAsset>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<AdPlacement>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Subscription>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<BillboardDevice>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<BillboardGroup>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);

        // SQLite (используется в тестах) не умеет сортировать и сравнивать DateTimeOffset.
        if (Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
        {
            foreach (var property in b.Model.GetEntityTypes()
                         .SelectMany(t => t.GetProperties())
                         .Where(p => p.ClrType == typeof(DateTimeOffset) || p.ClrType == typeof(DateTimeOffset?)))
            {
                property.SetValueConverter(new DateTimeOffsetToBinaryConverter());
            }
        }
    }
}
