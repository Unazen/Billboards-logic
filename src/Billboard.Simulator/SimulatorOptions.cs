using System.Globalization;

namespace Billboard.Simulator;

/// <summary>Настройки симулятора билборда.</summary>
public sealed class SimulatorOptions
{
    public const string Version = "1.0.0";

    public Guid BillboardId { get; set; } = Guid.Empty;

    public string ApiBaseUrl { get; set; } = "http://localhost:5000";

    public string CacheDirectory { get; set; } = "simulator-cache";

    public long CacheCapacityBytes { get; set; } = global::Billboard.Scheduling.Billboard.DefaultCapacityBytes;

    public int HeartbeatIntervalSeconds { get; set; } = 30;

    public int ManifestRefreshSeconds { get; set; } = 60;

    public int EventBatchSize { get; set; } = 20;

    /// <summary>1.0 — реальное время; 100 — ускоренная демонстрация.</summary>
    public double PlaybackSpeed { get; set; } = 1.0;

    /// <summary>0 — работать до остановки; больше нуля — остановиться после N показов.</summary>
    public int MaxPlays { get; set; }

    public bool DemoMode { get; set; }

    public string? ManifestFile { get; set; }

    public bool ShowHelp { get; set; }

    public static SimulatorOptions Parse(
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        var options = new SimulatorOptions();
        var env = environment ?? new Dictionary<string, string?>();

        ApplyEnvironment(options, env);

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            string Next()
            {
                if (index + 1 >= args.Count)
                {
                    throw new ArgumentException($"Для параметра {argument} не указано значение.");
                }

                return args[++index];
            }

            switch (argument)
            {
                case "--billboard-id":
                case "--billboard":
                    if (!Guid.TryParse(Next(), out var billboardId))
                    {
                        throw new ArgumentException("--billboard-id должен быть Guid.");
                    }

                    options.BillboardId = billboardId;
                    break;

                case "--api":
                    options.ApiBaseUrl = Next().TrimEnd('/');
                    break;

                case "--cache":
                    options.CacheDirectory = Next();
                    break;

                case "--cache-bytes":
                    options.CacheCapacityBytes = long.Parse(Next(), CultureInfo.InvariantCulture);
                    break;

                case "--cache-mb":
                    options.CacheCapacityBytes =
                        (long)(double.Parse(Next(), CultureInfo.InvariantCulture) * 1024 * 1024);
                    break;

                case "--heartbeat":
                    options.HeartbeatIntervalSeconds = int.Parse(Next(), CultureInfo.InvariantCulture);
                    break;

                case "--refresh":
                    options.ManifestRefreshSeconds = int.Parse(Next(), CultureInfo.InvariantCulture);
                    break;

                case "--batch":
                    options.EventBatchSize = int.Parse(Next(), CultureInfo.InvariantCulture);
                    break;

                case "--speed":
                    options.PlaybackSpeed = double.Parse(Next(), CultureInfo.InvariantCulture);
                    break;

                case "--max-plays":
                    options.MaxPlays = int.Parse(Next(), CultureInfo.InvariantCulture);
                    break;

                case "--manifest":
                    options.ManifestFile = Next();
                    break;

                case "--demo":
                    options.DemoMode = true;
                    break;

                case "--help":
                case "-h":
                case "/?":
                    options.ShowHelp = true;
                    break;

                default:
                    throw new ArgumentException($"Неизвестный параметр: {argument}. Используйте --help.");
            }
        }

        Validate(options);
        return options;
    }

    public static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Симулятор билборда BillboardSystem");
        writer.WriteLine();
        writer.WriteLine("Использование:");
        writer.WriteLine("  dotnet run --project src/Billboard.Simulator -- [параметры]");
        writer.WriteLine();
        writer.WriteLine("Параметры:");
        writer.WriteLine("  --billboard-id <guid>  идентификатор билборда (или BILLBOARD_ID)");
        writer.WriteLine("  --api <url>            адрес API (по умолчанию http://localhost:5000)");
        writer.WriteLine("  --cache <dir>          каталог локального кэша (по умолчанию simulator-cache)");
        writer.WriteLine("  --cache-mb <mb>        лимит кэша в мегабайтах (по умолчанию 32768 = 32 ГБ)");
        writer.WriteLine("  --heartbeat <sec>      период heartbeat (по умолчанию 30)");
        writer.WriteLine("  --refresh <sec>        период обновления manifest (по умолчанию 60)");
        writer.WriteLine("  --batch <count>        размер пакета событий (по умолчанию 20)");
        writer.WriteLine("  --speed <x>            скорость демонстрации, 1 = реальное время");
        writer.WriteLine("  --max-plays <n>        остановиться после N показов (0 = без ограничения)");
        writer.WriteLine("  --manifest <file>      работать по локальному manifest.json без API");
        writer.WriteLine("  --demo                 автономная демонстрация без сервера");
        writer.WriteLine("  --help                 эта справка");
    }

    private static void ApplyEnvironment(SimulatorOptions options, IReadOnlyDictionary<string, string?> env)
    {
        if (env.TryGetValue("BILLBOARD_ID", out var billboardId) &&
            Guid.TryParse(billboardId, out var parsedBillboardId))
        {
            options.BillboardId = parsedBillboardId;
        }

        if (env.TryGetValue("BILLBOARD_API_URL", out var api) && !string.IsNullOrWhiteSpace(api))
        {
            options.ApiBaseUrl = api.TrimEnd('/');
        }

        if (env.TryGetValue("BILLBOARD_CACHE_DIR", out var cache) && !string.IsNullOrWhiteSpace(cache))
        {
            options.CacheDirectory = cache;
        }

        if (env.TryGetValue("BILLBOARD_CACHE_MB", out var cacheMb) &&
            double.TryParse(cacheMb, NumberStyles.Float, CultureInfo.InvariantCulture, out var megabytes) &&
            megabytes > 0)
        {
            options.CacheCapacityBytes = (long)(megabytes * 1024 * 1024);
        }

        if (env.TryGetValue("BILLBOARD_DEMO", out var demo) &&
            bool.TryParse(demo, out var isDemo))
        {
            options.DemoMode = isDemo;
        }

        if (env.TryGetValue("BILLBOARD_SPEED", out var speed) &&
            double.TryParse(speed, NumberStyles.Float, CultureInfo.InvariantCulture, out var playbackSpeed) &&
            playbackSpeed > 0)
        {
            options.PlaybackSpeed = playbackSpeed;
        }
    }

    private static void Validate(SimulatorOptions options)
    {
        if (options.ShowHelp)
        {
            return;
        }

        if (options.CacheCapacityBytes <= 0)
        {
            throw new ArgumentException("Лимит кэша должен быть больше нуля.");
        }

        if (options.HeartbeatIntervalSeconds <= 0)
        {
            throw new ArgumentException("Период heartbeat должен быть больше нуля.");
        }

        if (options.ManifestRefreshSeconds <= 0)
        {
            throw new ArgumentException("Период обновления manifest должен быть больше нуля.");
        }

        if (options.EventBatchSize <= 0)
        {
            throw new ArgumentException("Размер пакета событий должен быть больше нуля.");
        }

        if (options.MaxPlays < 0)
        {
            throw new ArgumentException("--max-plays не может быть отрицательным.");
        }

        if (options.PlaybackSpeed <= 0 || !double.IsFinite(options.PlaybackSpeed))
        {
            throw new ArgumentException("Скорость воспроизведения должна быть конечным числом больше нуля.");
        }

        if (options.CacheDirectory.Trim().Length == 0)
        {
            throw new ArgumentException("Каталог кэша не может быть пустым.");
        }

        if (!Uri.TryCreate(options.ApiBaseUrl, UriKind.Absolute, out var apiUri) ||
            (apiUri.Scheme != Uri.UriSchemeHttp && apiUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("--api должен быть абсолютным http(s)-адресом.");
        }

        if (options.DemoMode && options.BillboardId == Guid.Empty)
        {
            options.BillboardId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        }

        if (!options.DemoMode && options.BillboardId == Guid.Empty)
        {
            throw new ArgumentException(
                "Не указан --billboard-id. Для автономной проверки используйте --demo.");
        }
    }
}
