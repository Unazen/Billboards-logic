using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Billboard.Application.Common;
using Billboard.Application.Contracts;
using Billboard.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Billboard.Infrastructure.Media;

public class MediaOptions
{
    public const string Section = "Media";

    public string FfprobePath { get; set; } = "ffprobe";
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>true: если ffprobe недоступен, загрузка отклоняется (длительность проверить нельзя).</summary>
    public bool RequireProbe { get; set; } = true;

    /// <summary>Только для разработки: разрешает импорт по URL с адресов внутренних сетей.</summary>
    public bool AllowPrivateUrlImport { get; set; }

    public int ImportTimeoutSeconds { get; set; } = 300;
    public int DownloadUrlTtlMinutes { get; set; } = 15;
    public string? TempPath { get; set; }
}

internal static class ProcessRunner
{
    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // ArgumentList исключает инъекцию через имена файлов.
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Не удалось запустить {fileName}");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* процесс уже завершился */ }
            throw;
        }
        return (process.ExitCode, await stdout, await stderr);
    }
}

/// <summary>Определяет длительность и формат файла через ffprobe.</summary>
public sealed class FfprobeVideoProbe(Microsoft.Extensions.Options.IOptions<MediaOptions> options, ILogger<FfprobeVideoProbe> log) : IVideoProbe
{
    public async Task<VideoProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default)
    {
        (int ExitCode, string StdOut, string StdErr) run;
        try
        {
            run = await ProcessRunner.RunAsync(options.Value.FfprobePath,
                new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", filePath }, ct);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            log.LogWarning("ffprobe не найден ({Path}); метаданные видео не будут определены.", options.Value.FfprobePath);
            return null;
        }

        if (run.ExitCode != 0)
            throw AppException.Validation("INVALID_VIDEO", "Файл повреждён или не является видео.");

        using var doc = JsonDocument.Parse(run.StdOut);
        var root = doc.RootElement;

        string? videoCodec = null;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                if (s.TryGetProperty("codec_type", out var t) && t.GetString() == "video")
                {
                    videoCodec = s.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
                    break;
                }
            }
        }

        if (videoCodec is null)
            throw AppException.Validation("INVALID_VIDEO", "В файле нет видеопотока.");

        decimal? duration = null;
        string? format = null;
        if (root.TryGetProperty("format", out var f))
        {
            format = f.TryGetProperty("format_name", out var fn) ? fn.GetString() : null;
            if (f.TryGetProperty("duration", out var d) &&
                decimal.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
                duration = Math.Round(secs, 2);
        }

        return new VideoProbeResult(duration, format, videoCodec);
    }
}

/// <summary>Сжатие видео для устройств: H.264 + AAC, ограничение высоты кадра, faststart.</summary>
public sealed class FfmpegTranscoder(Microsoft.Extensions.Options.IOptions<MediaOptions> options, ILogger<FfmpegTranscoder> log) : IVideoTranscoder
{
    public async Task<bool> TranscodeAsync(string inputPath, string outputPath, CompressionLevel level, CancellationToken ct = default)
    {
        if (level == CompressionLevel.None) return false;

        // Basic: до 720p и более сильное сжатие; Full: до 1080p и лучшее качество.
        var (maxHeight, crf) = level == CompressionLevel.Full ? (1080, "24") : (720, "28");

        try
        {
            var run = await ProcessRunner.RunAsync(options.Value.FfmpegPath, new[]
            {
                "-y", "-i", inputPath,
                "-vf", $"scale=-2:'min({maxHeight},ih)'",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", crf,
                "-c:a", "aac", "-b:a", "128k",
                "-movflags", "+faststart",
                outputPath
            }, ct);

            if (run.ExitCode != 0)
                log.LogWarning("ffmpeg завершился с кодом {Code}: {Err}", run.ExitCode, run.StdErr);
            return run.ExitCode == 0 && File.Exists(outputPath);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            log.LogWarning("ffmpeg не найден ({Path}); сжатие пропущено.", options.Value.FfmpegPath);
            return false;
        }
    }
}
