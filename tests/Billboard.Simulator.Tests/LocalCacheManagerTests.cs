using Billboard.Simulator.Cache;

namespace Billboard.Simulator.Tests;

public class LocalCacheManagerTests
{
    [Fact]
    public async Task EnsureCached_StoresFileAndEntry()
    {
        using var directory = new TempDirectory();
        var cache = new LocalCacheManager(directory.Path, 1024 * 1024);
        var data = TestItems.Bytes(1024);
        var item = TestItems.Create(size: data.Length, checksum: TestItems.Sha256(data));

        var result = await cache.EnsureCachedAsync(
            item,
            Array.Empty<Guid>(),
            new BytesDownloader(data));

        Assert.True(result.Success, result.Message);
        Assert.Equal(data.Length, cache.UsedBytes);
        Assert.Single(cache.Entries);
        Assert.NotNull(cache.TryGetEntry(item.VideoId));
        Assert.True(File.Exists(cache.GetFilePath(item.VideoId)));
    }

    [Fact]
    public async Task EnsureCached_RejectsInvalidItem()
    {
        using var directory = new TempDirectory();
        var cache = new LocalCacheManager(directory.Path, 1024 * 1024);
        var item = TestItems.Create(videoId: Guid.Empty, size: 10);

        var result = await cache.EnsureCachedAsync(
            item,
            Array.Empty<Guid>(),
            new BytesDownloader(TestItems.Bytes(10)));

        Assert.False(result.Success);
        Assert.Equal(CacheErrorCodes.InvalidItem, result.ErrorCode);
    }

    [Fact]
    public async Task EnsureCached_RejectsFileLargerThanCapacity()
    {
        using var directory = new TempDirectory();
        var cache = new LocalCacheManager(directory.Path, 1024);
        var item = TestItems.Create(size: 2048);

        var result = await cache.EnsureCachedAsync(
            item,
            Array.Empty<Guid>(),
            new BytesDownloader(TestItems.Bytes(2048)));

        Assert.False(result.Success);
        Assert.Equal(CacheErrorCodes.CapacityExceeded, result.ErrorCode);
    }

    [Fact]
    public async Task EnsureCached_WhenFull_EvictsLeastRecentlyUsed()
    {
        using var directory = new TempDirectory();
        var now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        var cache = new LocalCacheManager(
            directory.Path,
            2 * 1024 * 1024,
            clock: () => now);

        var first = TestItems.Create(size: 1024 * 1024);
        var second = TestItems.Create(size: 1024 * 1024);
        var third = TestItems.Create(size: 1024 * 1024);

        Assert.True((await cache.EnsureCachedAsync(
            first,
            Array.Empty<Guid>(),
            new BytesDownloader(TestItems.Bytes(1024 * 1024)))).Success);

        now = now.AddMinutes(1);
        Assert.True((await cache.EnsureCachedAsync(
            second,
            Array.Empty<Guid>(),
            new BytesDownloader(TestItems.Bytes(1024 * 1024)))).Success);

        now = now.AddMinutes(1);
        Assert.True((await cache.EnsureCachedAsync(
            third,
            Array.Empty<Guid>(),
            new BytesDownloader(TestItems.Bytes(1024 * 1024)))).Success);

        Assert.Null(cache.TryGetEntry(first.VideoId));
        Assert.NotNull(cache.TryGetEntry(second.VideoId));
        Assert.NotNull(cache.TryGetEntry(third.VideoId));
        Assert.Equal(2 * 1024 * 1024, cache.UsedBytes);
    }

    [Fact]
    public async Task EnsureCached_WhenAllFilesProtected_ReturnsCapacityError()
    {
        using var directory = new TempDirectory();
        var cache = new LocalCacheManager(directory.Path, 2 * 1024 * 1024);
        var first = TestItems.Create(size: 1024 * 1024);
        var second = TestItems.Create(size: 1024 * 1024);
        var third = TestItems.Create(size: 1024 * 1024);

        Assert.True((await cache.EnsureCachedAsync(
            first,
            Array.Empty<Guid>(),
            new BytesDownloader(TestItems.Bytes(1024 * 1024)))).Success);
        Assert.True((await cache.EnsureCachedAsync(
            second,
            Array.Empty<Guid>(),
            new BytesDownloader(TestItems.Bytes(1024 * 1024)))).Success);

        var result = await cache.EnsureCachedAsync(
            third,
            new[] { first.VideoId, second.VideoId },
            new BytesDownloader(TestItems.Bytes(1024 * 1024)));

        Assert.False(result.Success);
        Assert.Equal(CacheErrorCodes.CapacityExceeded, result.ErrorCode);
        Assert.NotNull(cache.TryGetEntry(first.VideoId));
        Assert.NotNull(cache.TryGetEntry(second.VideoId));
    }

    [Fact]
    public async Task EnsureCached_WhenChecksumMismatch_RejectsFile()
    {
        using var directory = new TempDirectory();
        var cache = new LocalCacheManager(directory.Path, 1024 * 1024);
        var data = TestItems.Bytes(1024, seed: 1);
        var wrong = TestItems.Bytes(1024, seed: 2);
        var item = TestItems.Create(size: data.Length, checksum: TestItems.Sha256(wrong));

        var result = await cache.EnsureCachedAsync(
            item,
            Array.Empty<Guid>(),
            new BytesDownloader(data));

        Assert.False(result.Success);
        Assert.Equal(CacheErrorCodes.ChecksumMismatch, result.ErrorCode);
        Assert.Equal(0, cache.UsedBytes);
        Assert.Null(cache.GetFilePath(item.VideoId));
    }

    [Fact]
    public async Task EnsureCached_WhenSizeMismatch_RejectsFile()
    {
        using var directory = new TempDirectory();
        var cache = new LocalCacheManager(directory.Path, 1024 * 1024);
        var item = TestItems.Create(size: 1024);

        var result = await cache.EnsureCachedAsync(
            item,
            Array.Empty<Guid>(),
            new BytesDownloader(TestItems.Bytes(512)));

        Assert.False(result.Success);
        Assert.Equal(CacheErrorCodes.SizeMismatch, result.ErrorCode);
        Assert.Equal(0, cache.UsedBytes);
    }

    [Fact]
    public async Task EnsureCached_WhenFileCorruptedOnDisk_Redownloads()
    {
        using var directory = new TempDirectory();
        var data = TestItems.Bytes(1024, seed: 3);
        var item = TestItems.Create(size: data.Length, checksum: TestItems.Sha256(data));

        var firstCache = new LocalCacheManager(directory.Path, 1024 * 1024);
        Assert.True((await firstCache.EnsureCachedAsync(
            item,
            Array.Empty<Guid>(),
            new BytesDownloader(data))).Success);
        var path = firstCache.GetFilePath(item.VideoId)!;

        await File.WriteAllBytesAsync(path, TestItems.Bytes(1024, seed: 99));

        var secondCache = new LocalCacheManager(directory.Path, 1024 * 1024);
        var downloader = new BytesDownloader(data);
        var result = await secondCache.EnsureCachedAsync(
            item,
            Array.Empty<Guid>(),
            downloader);

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, downloader.Calls);
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Index_PersistsAcrossInstances()
    {
        using var directory = new TempDirectory();
        var data = TestItems.Bytes(2048);
        var item = TestItems.Create(size: data.Length, checksum: TestItems.Sha256(data));

        var firstCache = new LocalCacheManager(directory.Path, 1024 * 1024);
        Assert.True((await firstCache.EnsureCachedAsync(
            item,
            Array.Empty<Guid>(),
            new BytesDownloader(data))).Success);

        var secondCache = new LocalCacheManager(directory.Path, 1024 * 1024);

        Assert.Single(secondCache.Entries);
        Assert.Equal(data.Length, secondCache.UsedBytes);
        Assert.NotNull(secondCache.TryGetEntry(item.VideoId));
    }

    [Fact]
    public async Task RemoveAsync_DeletesFileAndEntry()
    {
        using var directory = new TempDirectory();
        var data = TestItems.Bytes(1024);
        var item = TestItems.Create(size: data.Length);
        var cache = new LocalCacheManager(directory.Path, 1024 * 1024);
        Assert.True((await cache.EnsureCachedAsync(
            item,
            Array.Empty<Guid>(),
            new BytesDownloader(data))).Success);

        var removed = await cache.RemoveAsync(item.VideoId);

        Assert.True(removed);
        Assert.Empty(cache.Entries);
        Assert.Equal(0, cache.UsedBytes);
        Assert.Null(cache.GetFilePath(item.VideoId));
    }

    [Fact]
    public async Task TryGetEntry_WhenFileWasDeleted_ReturnsNull()
    {
        using var directory = new TempDirectory();
        var data = TestItems.Bytes(1024);
        var item = TestItems.Create(size: data.Length);
        var cache = new LocalCacheManager(directory.Path, 1024 * 1024);
        Assert.True((await cache.EnsureCachedAsync(
            item,
            Array.Empty<Guid>(),
            new BytesDownloader(data))).Success);

        File.Delete(cache.GetFilePath(item.VideoId)!);

        Assert.Null(cache.TryGetEntry(item.VideoId));
    }

    [Fact]
    public async Task EnsureCached_WhenFileExistsWithoutIndex_AdoptsIt()
    {
        using var directory = new TempDirectory();
        var data = TestItems.Bytes(1024, seed: 5);
        var videoId = Guid.NewGuid();
        var path = System.IO.Path.Combine(directory.Path, videoId.ToString("N") + ".mp4");
        await File.WriteAllBytesAsync(path, data);

        var item = TestItems.Create(
            videoId: videoId,
            size: data.Length,
            checksum: TestItems.Sha256(data));
        var cache = new LocalCacheManager(directory.Path, 1024 * 1024);
        var downloader = new BytesDownloader(data);

        var result = await cache.EnsureCachedAsync(
            item,
            Array.Empty<Guid>(),
            downloader);

        Assert.True(result.Success, result.Message);
        Assert.Equal(0, downloader.Calls);
        Assert.NotNull(cache.TryGetEntry(videoId));
    }

    [Fact]
    public async Task EvictUnusedAsync_RemovesOnlyUnprotectedFiles()
    {
        using var directory = new TempDirectory();
        var cache = new LocalCacheManager(directory.Path, 2 * 1024 * 1024);
        var first = TestItems.Create(size: 1024 * 1024);
        var second = TestItems.Create(size: 1024 * 1024);
        Assert.True((await cache.EnsureCachedAsync(
            first, Array.Empty<Guid>(), new BytesDownloader(TestItems.Bytes(1024 * 1024)))).Success);
        Assert.True((await cache.EnsureCachedAsync(
            second, Array.Empty<Guid>(), new BytesDownloader(TestItems.Bytes(1024 * 1024)))).Success);

        var freed = await cache.EvictUnusedAsync(
            new[] { first.VideoId },
            1024 * 1024);

        Assert.Equal(1024 * 1024, freed);
        Assert.NotNull(cache.TryGetEntry(first.VideoId));
        Assert.Null(cache.TryGetEntry(second.VideoId));
    }
}
