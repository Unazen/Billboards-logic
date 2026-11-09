using Billboard.Scheduling;
using Billboard.Simulator.Runtime;

namespace Billboard.Simulator.Tests;

public class PlaybackEventOutboxTests
{
    [Fact]
    public void Enqueue_PersistsEventsAcrossRestart()
    {
        using var directory = new TempDirectory();
        var path = System.IO.Path.Combine(directory.Path, "outbox.json");
        var outbox = new PlaybackEventOutbox(path);
        var first = CreateEvent();
        var second = CreateEvent();

        Assert.True(outbox.Enqueue(first));
        Assert.True(outbox.Enqueue(second));
        Assert.Equal(2, outbox.Count);

        var restarted = new PlaybackEventOutbox(path);

        Assert.Equal(2, restarted.Count);
        Assert.Contains(restarted.Snapshot(), item => item.Event.EventId == first.EventId);
        Assert.Contains(restarted.Snapshot(), item => item.Event.EventId == second.EventId);
    }

    [Fact]
    public void Enqueue_DuplicateEventId_IsIgnored()
    {
        using var directory = new TempDirectory();
        var outbox = new PlaybackEventOutbox(System.IO.Path.Combine(directory.Path, "outbox.json"));
        var playbackEvent = CreateEvent();

        Assert.True(outbox.Enqueue(playbackEvent));
        Assert.False(outbox.Enqueue(playbackEvent));
        Assert.Equal(1, outbox.Count);
    }

    [Fact]
    public void Enqueue_NormalizesEmptyIdempotencyKey()
    {
        using var directory = new TempDirectory();
        var outbox = new PlaybackEventOutbox(System.IO.Path.Combine(directory.Path, "outbox.json"));
        var playbackEvent = CreateEvent() with { IdempotencyKey = string.Empty };

        outbox.Enqueue(playbackEvent);

        var stored = Assert.Single(outbox.Snapshot()).Event;
        Assert.Equal(playbackEvent.EventId.ToString("N"), stored.IdempotencyKey);
    }

    [Fact]
    public void Acknowledge_RemovesEventsAndPersists()
    {
        using var directory = new TempDirectory();
        var path = System.IO.Path.Combine(directory.Path, "outbox.json");
        var outbox = new PlaybackEventOutbox(path);
        var first = CreateEvent();
        var second = CreateEvent();
        outbox.Enqueue(first);
        outbox.Enqueue(second);

        outbox.Acknowledge(new[] { first.EventId });

        Assert.Equal(1, outbox.Count);
        Assert.Equal(second.EventId, Assert.Single(outbox.Snapshot()).Event.EventId);

        var restarted = new PlaybackEventOutbox(path);
        Assert.Equal(1, restarted.Count);
    }

    [Fact]
    public void Take_ReturnsOldestFirst()
    {
        using var directory = new TempDirectory();
        var outbox = new PlaybackEventOutbox(System.IO.Path.Combine(directory.Path, "outbox.json"));
        var ids = new List<Guid>();
        for (var index = 0; index < 5; index++)
        {
            var playbackEvent = CreateEvent();
            ids.Add(playbackEvent.EventId);
            outbox.Enqueue(playbackEvent);
            Thread.Sleep(2);
        }

        var batch = outbox.Take(3);

        Assert.Equal(3, batch.Count);
        Assert.Equal(ids.Take(3), batch.Select(item => item.EventId));
    }

    [Fact]
    public void RecordAttempt_IncrementsAttempts()
    {
        using var directory = new TempDirectory();
        var outbox = new PlaybackEventOutbox(System.IO.Path.Combine(directory.Path, "outbox.json"));
        var playbackEvent = CreateEvent();
        outbox.Enqueue(playbackEvent);

        outbox.RecordAttempt(new[] { playbackEvent.EventId });

        Assert.Equal(1, Assert.Single(outbox.Snapshot()).Attempts);
    }

    [Fact]
    public void CorruptedFile_StartsEmptyAndArchivesFile()
    {
        using var directory = new TempDirectory();
        var path = System.IO.Path.Combine(directory.Path, "outbox.json");
        File.WriteAllText(path, "это не json");

        var outbox = new PlaybackEventOutbox(path);

        Assert.Equal(0, outbox.Count);
        Assert.NotEmpty(Directory.GetFiles(directory.Path, "outbox.json.corrupt.*"));
    }

    private static PlaybackEvent CreateEvent() =>
        PlaybackEvent.Create(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            TestItems.Create(),
            DateTimeOffset.UtcNow);
}
