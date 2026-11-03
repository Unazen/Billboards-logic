using System.Security.Cryptography;
using System.Text.Json;
using Billboard.Scheduling;

namespace Billboard.Simulator.Cache;

/// <summary>
/// Локальный кэш видео с жёстким лимитом (по умолчанию 32 ГБ).
/// При нехватке места удаляются только неиспользуемые файлы; активные
/// размещения защищены. Целостность проверяется по размеру и SHA-256.
/// </summary>
public sealed class LocalCacheManager
{
    private readonly string _root;
    private readonly long _capacityBytes;
    private readonly string _indexPath;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TextWriter? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, CacheEntry> _entries = new();

    public LocalCacheManager(
        string rootDirectory,
        long capacityBytes = global::Billboard.Scheduling.Billboard.DefaultCapacityBytes,
        Func<DateTimeOffset>? clock = null,
        TextWriter? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        if (capacityBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityBytes), "Лимит кэша должен быть больше нуля.");
        }

        _root = Path.GetFullPath(rootDirectory);
        _capacityBytes = capacityBytes;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _log = log;
        _indexPath = Path.Combine(_root, "cache-index.json");

        Directory.CreateDirectory(_root);
        LoadIndex();
    }

    public string RootDirectory => _root;

    public long CapacityBytes => _capacityBytes;

    public int Count
    {
        get
        {
            _gate.Wait();
            try
            {
                return _entries.Count;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public long UsedBytes
    {
        get
        {
            _gate.Wait();
            try
            {
                return ComputeUsedBytes();
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public IReadOnlyList<CacheEntry> Entries
    {
        get
        {
            _gate.Wait();
            try
            {
                return _entries.Values.OrderBy(entry => entry.VideoId).ToArray();
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>Возвращает запись, только если файл реально существует и совпадает по размеру.</summary>
    public CacheEntry? TryGetEntry(Guid videoId)
    {
        _gate.Wait();
        try
        {
            if (!_entries.TryGetValue(videoId, out var entry))
            {
                return null;
            }

            var path = ResolvePath(entry);
            return File.Exists(path) && new FileInfo(path).Length == entry.SizeBytes
                ? entry
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public string? GetFilePath(Guid videoId)
    {
        var entry = TryGetEntry(videoId);
        return entry is null ? null : ResolvePath(entry);
    }

    public async Task<CacheResult> EnsureCachedAsync(
        ManifestItem item,
        IReadOnlyCollection<Guid> protectedVideoIds,
        IVideoDownloader downloader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(downloader);

        if (item.VideoId == Guid.Empty)
        {
            return CacheResult.Fail(CacheErrorCodes.InvalidItem, "В манифесте отсутствует VideoId.");
        }

        if (item.SizeBytes <= 0)
        {
            return CacheResult.Fail(CacheErrorCodes.InvalidItem, "Размер видео должен быть больше нуля.");
        }

        if (item.SizeBytes > _capacityBytes)
        {
            return CacheResult.Fail(
                CacheErrorCodes.CapacityExceeded,
                $"Файл {item.FileName} ({Bytes(item.SizeBytes)}) больше всего лимита кэша ({Bytes(_capacityBytes)}).");
        }

        protectedVideoIds ??= Array.Empty<Guid>();

        await _gate.WaitAsync(cancellationToken);
        string? temporaryPath = null;

        try
        {
            var existing = _entries.GetValueOrDefault(item.VideoId);
            if (existing is not null)
            {
                var existingPath = ResolvePath(existing);
                var sameSize = File.Exists(existingPath) &&
                               new FileInfo(existingPath).Length == item.SizeBytes;
                var sameChecksum = string.IsNullOrEmpty(item.ChecksumSha256) ||
                                   string.Equals(
                                       NormalizeChecksum(existing.ChecksumSha256),
                                       NormalizeChecksum(item.ChecksumSha256),
                                       StringComparison.OrdinalIgnoreCase);

                if (sameSize && sameChecksum)
                {
                    var valid = true;
                    if (!existing.Verified && !string.IsNullOrEmpty(item.ChecksumSha256))
                    {
                        valid = await VerifyChecksumAsync(existingPath, item.ChecksumSha256, cancellationToken);
                    }

                    if (valid)
                    {
                        var touched = existing with
                        {
                            LastUsedUtc = _clock(),
                            Verified = true,
                            DisplayName = item.FileName,
                            PlacementId = item.PlacementId,
                        };
                        _entries[item.VideoId] = touched;
                        await SaveIndexAsync(cancellationToken);
                        return CacheResult.Ok(touched);
                    }
                }

                DeleteEntry(existing);
                existing = null;
            }

            var finalPath = ResolveFinalPath(item.VideoId, item.FileName);

            // Файл мог остаться от предыдущего запуска без записи в индексе.
            if (File.Exists(finalPath) && new FileInfo(finalPath).Length == item.SizeBytes)
            {
                var checksumOk = string.IsNullOrEmpty(item.ChecksumSha256) ||
                                 await VerifyChecksumAsync(finalPath, item.ChecksumSha256, cancellationToken);

                if (checksumOk)
                {
                    var adopted = CreateEntry(item, finalPath);
                    _entries[item.VideoId] = adopted;
                    await SaveIndexAsync(cancellationToken);
                    return CacheResult.Ok(adopted);
                }

                TryDeleteFile(finalPath);
            }

            var overflow = ComputeUsedBytes() + item.SizeBytes - _capacityBytes;
            if (overflow > 0)
            {
                EvictUnusedCore(protectedVideoIds, overflow);
                if (ComputeUsedBytes() + item.SizeBytes > _capacityBytes)
                {
                    return CacheResult.Fail(
                        CacheErrorCodes.CapacityExceeded,
                        $"Недостаточно места: нужно {Bytes(item.SizeBytes)}, свободно " +
                        $"{Bytes(Math.Max(0, _capacityBytes - ComputeUsedBytes()))}. " +
                        "Все оставшиеся файлы используются активными размещениями.");
                }

                await SaveIndexAsync(cancellationToken);
            }

            temporaryPath = Path.Combine(
                _root,
                $"{item.VideoId:N}.{Guid.NewGuid():N}.part");

            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             useAsync: true))
            {
                await downloader.DownloadAsync(item, stream, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            var downloadedLength = new FileInfo(temporaryPath).Length;
            if (downloadedLength != item.SizeBytes)
            {
                return CacheResult.Fail(
                    CacheErrorCodes.SizeMismatch,
                    $"Размер загруженного файла {downloadedLength} не совпадает с манифестом {item.SizeBytes}.");
            }

            if (!string.IsNullOrEmpty(item.ChecksumSha256) &&
                !await VerifyChecksumAsync(temporaryPath, item.ChecksumSha256, cancellationToken))
            {
                return CacheResult.Fail(
                    CacheErrorCodes.ChecksumMismatch,
                    "Контрольная сумма SHA-256 не совпала. Файл не будет использован.");
            }

            File.Move(temporaryPath, finalPath, overwrite: true);
            temporaryPath = null;

            var entry = CreateEntry(item, finalPath);
            _entries[item.VideoId] = entry;
            await SaveIndexAsync(cancellationToken);
            return CacheResult.Ok(entry);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log($"Не удалось закэшировать {item.FileName}: {exception.Message}");
            return CacheResult.Fail(CacheErrorCodes.DownloadFailed, exception.Message);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                TryDeleteFile(temporaryPath);
            }

            _gate.Release();
        }
    }

    /// <summary>Удаляет неиспользуемые файлы, пока не освободит не меньше bytesToFree.</summary>
    public async Task<long> EvictUnusedAsync(
        IReadOnlyCollection<Guid> protectedVideoIds,
        long bytesToFree,
        CancellationToken cancellationToken = default)
    {
        protectedVideoIds ??= Array.Empty<Guid>();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var freed = EvictUnusedCore(protectedVideoIds, bytesToFree);
            if (freed > 0)
            {
                await SaveIndexAsync(cancellationToken);
            }

            return freed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(Guid videoId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_entries.TryGetValue(videoId, out var entry))
            {
                return true;
            }

            var path = ResolvePath(entry);
            var deleted = !File.Exists(path) || TryDeleteFile(path);
            if (!deleted)
            {
                return false;
            }

            _entries.Remove(videoId);
            await SaveIndexAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private CacheEntry CreateEntry(ManifestItem item, string path) => new()
    {
        VideoId = item.VideoId,
        RelativePath = Path.GetFileName(path),
        SizeBytes = item.SizeBytes,
        ChecksumSha256 = NormalizeChecksum(item.ChecksumSha256),
        LastUsedUtc = _clock(),
        Verified = true,
        PlacementId = item.PlacementId,
        DisplayName = item.FileName,
    };

    private void DeleteEntry(CacheEntry entry)
    {
        var path = ResolvePath(entry);
        if (File.Exists(path))
        {
            TryDeleteFile(path);
        }

        _entries.Remove(entry.VideoId);
    }

    private long EvictUnusedCore(IReadOnlyCollection<Guid> protectedVideoIds, long bytesToFree)
    {
        if (bytesToFree <= 0)
        {
            return 0;
        }

        var candidates = _entries.Values
            .Where(entry => !protectedVideoIds.Contains(entry.VideoId))
            .OrderBy(entry => entry.LastUsedUtc)
            .ThenBy(entry => entry.VideoId)
            .ToArray();

        long freed = 0;
        foreach (var entry in candidates)
        {
            var path = ResolvePath(entry);
            if (!File.Exists(path))
            {
                _entries.Remove(entry.VideoId);
                continue;
            }

            var length = new FileInfo(path).Length;
            if (!TryDeleteFile(path))
            {
                continue;
            }

            _entries.Remove(entry.VideoId);
            freed += length;

            if (freed >= bytesToFree)
            {
                break;
            }
        }

        return freed;
    }

    private long ComputeUsedBytes()
    {
        long total = 0;
        foreach (var entry in _entries.Values)
        {
            var path = ResolvePath(entry);
            if (File.Exists(path))
            {
                total += new FileInfo(path).Length;
            }
        }

        return total;
    }

    private string ResolvePath(CacheEntry entry) =>
        Path.Combine(_root, Path.GetFileName(entry.RelativePath));

    private string ResolveFinalPath(Guid videoId, string originalFileName) =>
        Path.Combine(_root, videoId.ToString("N") + SafeExtension(originalFileName));

    private static string SafeExtension(string fileName)
    {
        var extension = Path.GetExtension(Path.GetFileName(fileName));
        if (string.IsNullOrEmpty(extension) || extension.Length > 10)
        {
            return ".mp4";
        }

        return extension.All(character => char.IsLetterOrDigit(character) || character == '.')
            ? extension.ToLowerInvariant()
            : ".mp4";
    }

    private static string? NormalizeChecksum(string? checksum)
    {
        if (string.IsNullOrWhiteSpace(checksum))
        {
            return null;
        }

        var value = checksum.Trim();
        if (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["sha256:".Length..];
        }

        return value.ToLowerInvariant();
    }

    private static async Task<bool> VerifyChecksumAsync(
        string path,
        string expected,
        CancellationToken cancellationToken)
    {
        var expectedValue = NormalizeChecksum(expected);
        if (expectedValue is null)
        {
            return true;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();
        return string.Equals(actual, expectedValue, StringComparison.OrdinalIgnoreCase);
    }

    private void LoadIndex()
    {
        if (!File.Exists(_indexPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_indexPath);
            var entries = JsonSerializer.Deserialize<List<CacheEntry>>(json, SimulatorJson.Options) ??
                          new List<CacheEntry>();

            foreach (var entry in entries)
            {
                if (entry.VideoId == Guid.Empty || string.IsNullOrWhiteSpace(entry.RelativePath))
                {
                    continue;
                }

                var path = ResolvePath(entry);
                if (File.Exists(path) && new FileInfo(path).Length == entry.SizeBytes)
                {
                    // Файл мог измениться вне кэша: при следующем обращении
                    // контрольная сумма будет проверена заново.
                    _entries[entry.VideoId] = string.IsNullOrEmpty(entry.ChecksumSha256)
                        ? entry
                        : entry with { Verified = false };
                }
            }

            Log($"Кэш загружен: {_entries.Count} файл(ов), {Bytes(ComputeUsedBytes())}.");
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            Log($"Индекс кэша повреждён, начинаем с пустого: {exception.Message}");
            _entries.Clear();
            TryDeleteFile(_indexPath);
        }
    }

    private Task SaveIndexAsync(CancellationToken cancellationToken)
    {
        var entries = _entries.Values
            .OrderBy(entry => entry.VideoId)
            .ToArray();

        return AtomicFile.WriteAllTextAsync(
            _indexPath,
            JsonSerializer.Serialize(entries, SimulatorJson.Options),
            cancellationToken);
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Bytes(long bytes)
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

    private void Log(string message) => _log?.WriteLine($"[CACHE] {message}");
}
