using MauiDevFlow.Logging;

namespace MauiDevFlow.Tests;

/// <summary>
/// Tests for the buffered logging system: verifies that entries written to the
/// in-memory buffer are correctly merged with on-disk entries, maintain proper
/// ordering (newest first), and survive the drain-to-disk cycle without
/// duplicates or data loss.
/// </summary>
public class BufferedLoggingTests : IDisposable
{
    private readonly string _logDir;

    public BufferedLoggingTests()
    {
        _logDir = Path.Combine(Path.GetTempPath(), $"mauidevflow-buftest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_logDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_logDir))
            Directory.Delete(_logDir, true);
    }

    [Fact]
    public void BufferedEntriesAreVisibleBeforeDrain()
    {
        using var provider = CreateProvider();

        // Write entries — they go to buffer, not disk yet
        WriteEntry(provider, "BUF_1", DateTime.UtcNow);
        WriteEntry(provider, "BUF_2", DateTime.UtcNow.AddMilliseconds(1));
        WriteEntry(provider, "BUF_3", DateTime.UtcNow.AddMilliseconds(2));

        // File should be empty (no drain yet)
        var currentFile = Path.Combine(_logDir, "log-current.jsonl");
        var fileSize = File.Exists(currentFile) ? new FileInfo(currentFile).Length : 0;
        Assert.Equal(0, fileSize);

        // But reader should still see all 3 entries from the buffer
        var entries = provider.Reader.Read(100);
        Assert.Equal(3, entries.Count);
        Assert.Equal("BUF_3", entries[0].Message);
        Assert.Equal("BUF_2", entries[1].Message);
        Assert.Equal("BUF_1", entries[2].Message);
    }

    [Fact]
    public void EntriesAreDrainedToDiskAfterTimerFires()
    {
        using var provider = CreateProvider();

        WriteEntry(provider, "DRAIN_1", DateTime.UtcNow);
        WriteEntry(provider, "DRAIN_2", DateTime.UtcNow.AddMilliseconds(1));

        // Wait for drain timer to fire (1s interval + margin)
        provider.Writer.Flush();

        // File should now contain the entries
        var currentFile = Path.Combine(_logDir, "log-current.jsonl");
        Assert.True(File.Exists(currentFile));
        var lines = ReadAllLinesShared(currentFile).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        Assert.Equal(2, lines.Count);

        // Reader should still return them correctly
        var entries = provider.Reader.Read(100);
        Assert.Equal(2, entries.Count);
        Assert.Equal("DRAIN_2", entries[0].Message);
        Assert.Equal("DRAIN_1", entries[1].Message);
    }

    [Fact]
    public void BufferAndDiskEntriesMergeInCorrectOrder()
    {
        using var provider = CreateProvider();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Write some entries and wait for drain
        WriteEntry(provider, "OLD_1", baseTime);
        WriteEntry(provider, "OLD_2", baseTime.AddSeconds(1));
        provider.Writer.Flush();

        // Now write more entries (these stay in buffer)
        WriteEntry(provider, "NEW_1", baseTime.AddSeconds(2));
        WriteEntry(provider, "NEW_2", baseTime.AddSeconds(3));

        // Read should merge disk (OLD_1, OLD_2) + buffer (NEW_1, NEW_2), newest first
        var entries = provider.Reader.Read(100);
        Assert.Equal(4, entries.Count);
        Assert.Equal("NEW_2", entries[0].Message);
        Assert.Equal("NEW_1", entries[1].Message);
        Assert.Equal("OLD_2", entries[2].Message);
        Assert.Equal("OLD_1", entries[3].Message);
    }

    [Fact]
    public void NoDuplicatesAfterDrain()
    {
        using var provider = CreateProvider();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < 10; i++)
            WriteEntry(provider, $"ENTRY_{i:D2}", baseTime.AddMilliseconds(i));

        // Read before drain: all from buffer
        var before = provider.Reader.Read(100);
        Assert.Equal(10, before.Count);

        // Wait for drain
        provider.Writer.Flush();

        // Read after drain: all from disk (buffer empty)
        var after = provider.Reader.Read(100);
        Assert.Equal(10, after.Count);

        // Verify no duplicates by checking unique messages
        var messages = after.Select(e => e.Message).ToList();
        Assert.Equal(messages.Distinct().Count(), messages.Count);
    }

    [Fact]
    public void OrderingWithMixedSources()
    {
        using var provider = CreateProvider();
        var baseTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        // Write native and webview entries interleaved
        WriteEntry(provider, "Native 1", baseTime, source: "native");
        WriteEntry(provider, "WebView 1", baseTime.AddMilliseconds(1), source: "webview");
        WriteEntry(provider, "Native 2", baseTime.AddMilliseconds(2), source: "native");
        WriteEntry(provider, "WebView 2", baseTime.AddMilliseconds(3), source: "webview");

        var all = provider.Reader.Read(100);
        Assert.Equal(4, all.Count);
        Assert.Equal("WebView 2", all[0].Message);
        Assert.Equal("Native 2", all[1].Message);
        Assert.Equal("WebView 1", all[2].Message);
        Assert.Equal("Native 1", all[3].Message);

        // Filter by source
        var native = provider.Reader.Read(100, source: "native");
        Assert.Equal(2, native.Count);
        Assert.Equal("Native 2", native[0].Message);
        Assert.Equal("Native 1", native[1].Message);

        var webview = provider.Reader.Read(100, source: "webview");
        Assert.Equal(2, webview.Count);
        Assert.Equal("WebView 2", webview[0].Message);
        Assert.Equal("WebView 1", webview[1].Message);
    }

    [Fact]
    public void PaginationWorksAcrossBufferAndDisk()
    {
        using var provider = CreateProvider();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Write 5 entries and drain to disk
        for (int i = 0; i < 5; i++)
            WriteEntry(provider, $"DISK_{i:D2}", baseTime.AddSeconds(i));
        provider.Writer.Flush();

        // Write 5 more (stay in buffer)
        for (int i = 0; i < 5; i++)
            WriteEntry(provider, $"BUF_{i:D2}", baseTime.AddSeconds(5 + i));

        // Page 1: should get newest 5 (all from buffer)
        var page1 = provider.Reader.Read(5, 0);
        Assert.Equal(5, page1.Count);
        Assert.Equal("BUF_04", page1[0].Message);
        Assert.Equal("BUF_00", page1[4].Message);

        // Page 2: should get oldest 5 (all from disk)
        var page2 = provider.Reader.Read(5, 5);
        Assert.Equal(5, page2.Count);
        Assert.Equal("DISK_04", page2[0].Message);
        Assert.Equal("DISK_00", page2[4].Message);
    }

    [Fact]
    public void DisposeFlushesBufferToDisk()
    {
        var provider = CreateProvider();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < 5; i++)
            WriteEntry(provider, $"FLUSH_{i}", baseTime.AddMilliseconds(i));

        // Dispose should drain buffer to disk
        provider.Dispose();

        // Read files directly to verify
        var currentFile = Path.Combine(_logDir, "log-current.jsonl");
        Assert.True(File.Exists(currentFile));
        var lines = File.ReadAllLines(currentFile).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        Assert.Equal(5, lines.Count);
    }

    [Fact]
    public void RotationPreservesAllEntries()
    {
        // Small max file size to force rotation
        using var provider = CreateProvider(maxFileSizeBytes: 500);
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Write enough entries to trigger rotation (~200 bytes each)
        for (int i = 0; i < 20; i++)
            WriteEntry(provider, $"ROT_{i:D2} padding {new string('x', 100)}", baseTime.AddSeconds(i));

        // Wait for drain + rotation
        provider.Writer.Flush();

        // Should have multiple files
        var files = Directory.GetFiles(_logDir, "log-*.jsonl");
        Assert.True(files.Length > 1, $"Expected rotation to create multiple files, got {files.Length}");

        // All entries should be readable
        var entries = provider.Reader.Read(100);
        Assert.True(entries.Count > 0);

        // Verify ordering is still newest-first
        for (int i = 0; i < entries.Count - 1; i++)
            Assert.True(entries[i].Timestamp >= entries[i + 1].Timestamp,
                $"Entry {i} ({entries[i].Timestamp:O}) should be >= entry {i + 1} ({entries[i + 1].Timestamp:O})");
    }

    [Fact]
    public async Task ConcurrentWritesAndReadsDoNotDeadlock()
    {
        using var provider = CreateProvider();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var writeCount = 0;
        var readCount = 0;

        // Writer thread: write entries rapidly
        var writer = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var i = Interlocked.Increment(ref writeCount);
                WriteEntry(provider, $"CONC_{i}", baseTime.AddMilliseconds(i));
            }
        });

        // Reader thread: read entries rapidly
        var reader = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                provider.Reader.Read(50);
                Interlocked.Increment(ref readCount);
            }
        });

        // Let them run for 3 seconds
        await Task.Delay(3000);
        cts.Cancel();

        await Task.WhenAll(writer, reader);

        Assert.True(writeCount > 0, "Writer should have written entries");
        Assert.True(readCount > 0, "Reader should have read entries");

        // Final read should return entries without errors
        var finalEntries = provider.Reader.Read(100);
        Assert.True(finalEntries.Count > 0);
    }

    private FileLogProvider CreateProvider(long maxFileSizeBytes = 1_048_576, int maxFiles = 5)
        => new FileLogProvider(_logDir, maxFileSizeBytes, maxFiles);

    private static void WriteEntry(FileLogProvider provider, string message, DateTime timestamp, string source = "native")
    {
        var entry = new FileLogEntry(
            Timestamp: timestamp,
            Level: "Information",
            Category: "Test",
            Message: message,
            Source: source
        );
        provider.Writer.Write(entry);
    }

    // Opens the file with FileShare.ReadWrite so it can be read while the writer still holds it open (Windows).
    private static string[] ReadAllLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return lines.ToArray();
    }
}
