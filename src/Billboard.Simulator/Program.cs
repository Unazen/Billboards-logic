using System.Text;
using Billboard.Simulator;
using Billboard.Simulator.Api;
using Billboard.Simulator.Cache;
using Billboard.Simulator.Runtime;

try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch (IOException)
{
    // Перенаправленный вывод может не поддерживать смену кодировки.
}

SimulatorOptions options;
try
{
    options = SimulatorOptions.Parse(args);
}
catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
{
    Console.Error.WriteLine(exception.Message);
    SimulatorOptions.PrintUsage(Console.Error);
    return 2;
}

if (options.ShowHelp)
{
    SimulatorOptions.PrintUsage(Console.Out);
    return 0;
}

Console.WriteLine("BillboardSystem · симулятор билборда");
Console.WriteLine($"Билборд: {options.BillboardId}");
Console.WriteLine($"Кэш: {options.CacheDirectory} ({FormatBytes(options.CacheCapacityBytes)})");
Console.WriteLine();

if (options.DemoMode)
{
    await DemoScenario.RunAsync(options, Console.Out);
    return 0;
}

try
{
    Directory.CreateDirectory(options.CacheDirectory);
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Не удалось создать каталог кэша: {exception.Message}");
    return 1;
}

using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
using var mediaHttpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var api = new HttpSimulatorApi(httpClient, options.ApiBaseUrl, options.DeviceToken, Console.Out);

IManifestSource manifestSource = options.ManifestFile is not null
    ? new FileManifestSource(options.ManifestFile)
    : new ApiManifestSource(api, options.BillboardId, options.CacheDirectory, Console.Out);

var cache = new LocalCacheManager(
    options.CacheDirectory,
    options.CacheCapacityBytes,
    log: Console.Out);

var downloader = new HttpVideoDownloader(mediaHttpClient);

var runner = new SimulatorRunner(
    options,
    api,
    manifestSource,
    cache,
    downloader,
    Console.Out);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    await runner.RunAsync(cancellation.Token);
}
catch (OperationCanceledException)
{
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Симулятор остановлен с ошибкой: {exception}");
    return 1;
}

return 0;

static string FormatBytes(long bytes)
{
    string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
    double value = bytes;
    var unit = 0;
    while (value >= 1024 && unit < units.Length - 1)
    {
        value /= 1024;
        unit++;
    }

    return $"{value:0.##} {units[unit]}";
}
