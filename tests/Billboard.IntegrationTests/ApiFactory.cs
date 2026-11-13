using Billboard.Application.Contracts;
using Billboard.Domain.Interfaces;
using Billboard.Infrastructure.Persistence;
using Billboard.Infrastructure.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Billboard.IntegrationTests;

/// <summary>
/// Поднимает настоящий ASP.NET Core хост API, но на SQLite в памяти и локальном хранилище файлов.
/// Так проверяется весь HTTP-сценарий без Docker и PostgreSQL.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly string _storagePath = Path.Combine(AppContext.BaseDirectory, "test-storage");

    public ApiFactory()
    {
        _connection.Open();
        Directory.CreateDirectory(_storagePath);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // EventLog в песочнице недоступен; для диагностики оставляем консоль.
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddSimpleConsole(o => o.SingleLine = true);
            logging.SetMinimumLevel(LogLevel.Warning);
        });

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=unused",
            ["Storage:Provider"] = "Local",
            ["Storage:LocalPath"] = _storagePath,
            ["Media:RequireProbe"] = "false",
            ["Media:DownloadUrlTtlMinutes"] = "15",
            ["Media:PublicBaseUrl"] = "http://localhost",
            ["Seed:AdminEmail"] = SeedAdminEmail,
            ["Seed:AdminPassword"] = SeedAdminPassword
        }));

        builder.ConfigureServices(services =>
        {
            // Убираем регистрацию Npgsql из AddBillboardInfrastructure и подключаем SQLite в памяти.
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection));

            // Программная регистрация хранилища выполняется до тестовых настроек,
            // поэтому MinIO заменяем локальным хранилищем явно.
            services.RemoveAll<MinioVideoStorage>();
            services.RemoveAll<IVideoStorage>();
            services.AddSingleton<IVideoStorage>(_ => new LocalVideoStorage(
                Options.Create(new StorageOptions { Provider = "Local", LocalPath = _storagePath })));

            // ffprobe в тестовом окружении нет: подменяем анализатор, чтобы длительность была известна.
            services.RemoveAll<IVideoProbe>();
            services.AddSingleton<IVideoProbe, StubVideoProbe>();
        });
    }

    public const string SeedAdminEmail = "admin@billboard.test";
    public const string SeedAdminPassword = "Admin_123";

    public new HttpClient CreateClient() => base.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false
    });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }

    private sealed class StubVideoProbe : IVideoProbe
    {
        public Task<VideoProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult<VideoProbeResult?>(new VideoProbeResult(15m, "mp4", "h264"));
    }
}
