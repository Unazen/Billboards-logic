using Billboard.Application.Contracts;
using Billboard.Domain.Interfaces;
using Billboard.Infrastructure.Media;
using Billboard.Infrastructure.Payments;
using Billboard.Infrastructure.Persistence;
using Billboard.Infrastructure.Scheduling;
using Billboard.Infrastructure.Services;
using Billboard.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Billboard.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddBillboardInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<AppDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("Postgres")
                        ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:Postgres.")));

        services.Configure<StorageOptions>(config.GetSection(StorageOptions.Section));
        services.Configure<MediaOptions>(config.GetSection(MediaOptions.Section));

        var provider = config.GetSection(StorageOptions.Section).GetValue<string>("Provider") ?? "Minio";
        if (provider.Equals("Local", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IVideoStorage, LocalVideoStorage>();
        else
        {
            services.AddSingleton<MinioVideoStorage>();
            services.AddSingleton<IVideoStorage>(sp => sp.GetRequiredService<MinioVideoStorage>());
        }

        services.AddSingleton<IUrlFetcher, SafeUrlFetcher>();
        services.AddSingleton<IVideoProbe, FfprobeVideoProbe>();
        services.AddSingleton<IVideoTranscoder, FfmpegTranscoder>();
        services.AddSingleton<IPaymentGateway, MockPaymentService>();

        services.AddScoped<WalletLedger>();
        services.AddScoped<ITariffService, TariffService>();
        services.AddScoped<IVideoService, VideoService>();
        services.AddScoped<IWalletService, WalletService>();
        services.AddScoped<IBillingService, BillingService>();
        services.AddScoped<IPlaybackService, PlaybackService>();
        services.AddScoped<IBillboardService, BillboardService>();
        services.AddScoped<IBillboardGroupService, BillboardGroupService>();
        services.AddScoped<IAdminService, AdminService>();

        // Планирование: базовая проверка ёмкости; участник 2 заменяет реализацию своим планировщиком.
        services.AddScoped<IScheduleValidator, CapacityScheduleValidator>();
        services.AddScoped<IPlacementService, PlacementService>();
        services.AddScoped<IDeviceService, DeviceService>();

        return services;
    }
}
