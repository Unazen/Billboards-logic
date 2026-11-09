using System.Security.Cryptography;
using Billboard.Application.Common;

namespace Billboard.Application.Media;

public static class BoundedCopy
{
    /// <summary>
    /// Копирует поток в файл, считая SHA-256 и не позволяя превысить maxBytes.
    /// При превышении бросает VIDEO_TOO_LARGE, не дочитывая поток до конца.
    /// </summary>
    public static async Task<(long Size, string Sha256)> CopyToFileAsync(
        Stream source, string destinationPath, long maxBytes, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var file = new FileStream(destinationPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw AppException.Validation("VIDEO_TOO_LARGE",
                    $"Размер файла превышает допустимый лимит {maxBytes / 1024 / 1024} МБ.",
                    new { limitBytes = maxBytes });

            hash.AppendData(buffer, 0, read);
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return (total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }
}
