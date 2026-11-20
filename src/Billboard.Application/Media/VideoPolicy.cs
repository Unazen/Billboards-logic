using Billboard.Application.Common;
using Billboard.Domain.Entities;

namespace Billboard.Application.Media;

/// <summary>Проверки видео по тарифу. Чистая логика без ввода-вывода.</summary>
public static class VideoPolicy
{
    public static readonly IReadOnlySet<string> AllowedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".webm", ".mov", ".mkv" };

    private static readonly Dictionary<string, string> ContentTypeToExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        ["video/mp4"] = ".mp4",
        ["video/webm"] = ".webm",
        ["video/quicktime"] = ".mov",
        ["video/x-matroska"] = ".mkv"
    };

    public static string? ExtensionForContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return null;
        var main = contentType.Split(';')[0].Trim();
        return ContentTypeToExtension.TryGetValue(main, out var ext) ? ext : null;
    }

    public static string ContentTypeForExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        _ => "application/octet-stream"
    };

    public static void ValidateExtension(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext))
            throw AppException.Validation("UNSUPPORTED_FORMAT",
                $"Формат файла не поддерживается. Допустимо: {string.Join(", ", AllowedExtensions)}.");
    }

    /// <summary>Проверка до начала загрузки: формат, заявленный размер и лимит числа видео.</summary>
    public static void ValidateUploadRequest(TariffPlan plan, string fileName, long? declaredSize, int currentVideoCount)
    {
        ValidateExtension(fileName);

        if (currentVideoCount >= plan.MaxVideoCount)
            throw AppException.Conflict("VIDEO_LIMIT_REACHED",
                $"Достигнут лимит медиатеки по тарифу «{plan.Name}»: {plan.MaxVideoCount} видео.",
                new { limit = plan.MaxVideoCount });

        if (declaredSize is { } size)
        {
            if (size <= 0)
                throw AppException.Validation("EMPTY_FILE", "Файл пустой.");
            if (size > plan.MaxVideoSizeBytes)
                throw TooLarge(plan, size);
        }
    }

    /// <summary>Проверка после получения файла и анализа метаданных.</summary>
    public static void ValidateProbed(TariffPlan plan, long sizeBytes, decimal? durationSeconds)
    {
        if (sizeBytes <= 0)
            throw AppException.Validation("EMPTY_FILE", "Файл пустой.");
        if (sizeBytes > plan.MaxVideoSizeBytes)
            throw TooLarge(plan, sizeBytes);

        if (durationSeconds is { } d && d > plan.MaxVideoDurationSeconds)
            throw AppException.Validation("VIDEO_TOO_LONG",
                $"Длительность {d:0.#} с превышает лимит тарифа «{plan.Name}»: {plan.MaxVideoDurationSeconds} с.",
                new { limitSeconds = plan.MaxVideoDurationSeconds, actualSeconds = d });
    }

    private static AppException TooLarge(TariffPlan plan, long size) =>
        AppException.Validation("VIDEO_TOO_LARGE",
            $"Размер {size / 1024 / 1024} МБ превышает лимит тарифа «{plan.Name}»: {plan.MaxVideoSizeBytes / 1024 / 1024} МБ.",
            new { limitBytes = plan.MaxVideoSizeBytes, actualBytes = size });
}
