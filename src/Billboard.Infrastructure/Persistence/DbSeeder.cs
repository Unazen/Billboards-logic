using Billboard.Domain.Entities;
using Billboard.Domain.Enums;
using Billboard.Infrastructure.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Billboard.Infrastructure.Persistence;

public static class DbSeeder
{
    public const string AdminRole = "Admin";
    public const string ClientRole = "Client";

    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("DbSeeder");
        var db = sp.GetRequiredService<AppDbContext>();

        // В рабочей среде применяем миграции. В тестах (SQLite) миграции Npgsql неприменимы,
        // поэтому схема создаётся из модели — так же, как в остальных тестовых сценариях.
        if (db.Database.IsNpgsql() && db.Database.GetMigrations().Any())
            await db.Database.MigrateAsync(ct);
        else
        {
            log.LogWarning("Схема создаётся через EnsureCreated (провайдер БД: {Provider}). " +
                           "Для PostgreSQL миграции применяются автоматически.",
                db.Database.ProviderName);
            await db.Database.EnsureCreatedAsync(ct);
        }

        if (sp.GetService<MinioVideoStorage>() is { } minio)
            await minio.EnsureBucketAsync(ct);

        await SeedTariffsAsync(db, ct);
        await SeedRolesAndAdminAsync(sp, log);
    }

    private static async Task SeedTariffsAsync(AppDbContext db, CancellationToken ct)
    {
        if (await db.TariffPlans.AnyAsync(ct)) return;

        const long MB = 1024L * 1024;
        db.TariffPlans.AddRange(
            new TariffPlan
            {
                Id = Guid.NewGuid(), Name = "Старт", IsDefault = true,
                MaxVideoCount = 5, MaxVideoSizeBytes = 100 * MB, MaxVideoDurationSeconds = 15,
                CompressionLevel = CompressionLevel.Basic, AdvancedStatistics = false, PriorityProcessing = false,
                MultiBillboardDiscountPercent = 0, MonthlyPrice = 0
            },
            new TariffPlan
            {
                Id = Guid.NewGuid(), Name = "Стандарт",
                MaxVideoCount = 20, MaxVideoSizeBytes = 500 * MB, MaxVideoDurationSeconds = 30,
                CompressionLevel = CompressionLevel.Full, AdvancedStatistics = true, PriorityProcessing = true,
                MultiBillboardDiscountPercent = 5, MonthlyPrice = 20
            },
            new TariffPlan
            {
                Id = Guid.NewGuid(), Name = "Бизнес",
                MaxVideoCount = 100, MaxVideoSizeBytes = 2048 * MB, MaxVideoDurationSeconds = 60,
                CompressionLevel = CompressionLevel.Full, AdvancedStatistics = true, PriorityProcessing = true,
                MultiBillboardDiscountPercent = 10, MonthlyPrice = 80
            });
        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedRolesAndAdminAsync(IServiceProvider sp, ILogger log)
    {
        var roles = sp.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var role in new[] { AdminRole, ClientRole })
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new IdentityRole<Guid>(role));

        // Администратор создаётся из конфигурации (переменные окружения / user-secrets), пароль в коде не хранится.
        var config = sp.GetRequiredService<IConfiguration>();
        var email = config["Seed:AdminEmail"];
        var password = config["Seed:AdminPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            log.LogWarning("Seed:AdminEmail / Seed:AdminPassword не заданы — администратор не создан.");
            return;
        }

        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        if (await users.FindByEmailAsync(email) is not null) return;

        var admin = new ApplicationUser { Id = Guid.NewGuid(), UserName = email, Email = email, EmailConfirmed = true, DisplayName = "Администратор" };
        var result = await users.CreateAsync(admin, password);
        if (!result.Succeeded)
        {
            log.LogError("Не удалось создать администратора: {Errors}", string.Join("; ", result.Errors.Select(e => e.Description)));
            return;
        }
        await users.AddToRoleAsync(admin, AdminRole);
    }
}
