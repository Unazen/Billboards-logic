using System.Security.Cryptography;
using Billboard.Scheduling;
using Billboard.Simulator.Cache;

namespace Billboard.Simulator.Tests;

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "billboard-p2-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class BytesDownloader : IVideoDownloader
{
    private readonly byte[] _bytes;

    public BytesDownloader(byte[] bytes)
    {
        _bytes = bytes;
    }

    public int Calls { get; private set; }

    public async Task DownloadAsync(
        ManifestItem item,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        await destination.WriteAsync(_bytes, cancellationToken);
    }
}

internal sealed class FailingDownloader : IVideoDownloader
{
    public Task DownloadAsync(
        ManifestItem item,
        Stream destination,
        CancellationToken cancellationToken = default) =>
        throw new IOException("Тестовая ошибка загрузки.");
}

internal static class TestItems
{
    public static ManifestItem Create(
        Guid? videoId = null,
        Guid? placementId = null,
        long size = 1024,
        string? checksum = null,
        string fileName = "video.mp4",
        int durationSeconds = 10,
        int playsPerHour = 1) => new()
    {
        PlacementId = placementId ?? Guid.NewGuid(),
        VideoId = videoId ?? Guid.NewGuid(),
        FileName = fileName,
        DownloadUrl = "test://video",
        SizeBytes = size,
        DurationSeconds = durationSeconds,
        PlaysPerHour = playsPerHour,
        ChecksumSha256 = checksum,
    };

    public static string Sha256(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static byte[] Bytes(int size, byte seed = 7)
    {
        var data = new byte[size];
        for (var index = 0; index < size; index++)
        {
            data[index] = (byte)((seed + index) % 251);
        }

        return data;
    }
}
