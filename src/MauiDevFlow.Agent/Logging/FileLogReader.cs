using System.Text.Json;

namespace MauiDevFlow.Agent.Logging;

/// <summary>
/// Reads log entries from JSONL files, newest first.
/// Reads current file first, then rotated files in order (001, 002, ...).
/// </summary>
public class FileLogReader
{
    private readonly string _logDir;

    public FileLogReader(string logDir)
    {
        _logDir = logDir;
    }

    /// <summary>
    /// Returns log entries in reverse chronological order (newest first).
    /// </summary>
    public List<FileLogEntry> Read(int limit = 100, int skip = 0, string? source = null)
    {
        limit = Math.Clamp(limit, 1, 1000);
        skip = Math.Max(skip, 0);

        var allEntries = new List<FileLogEntry>();
        var needed = skip + limit;

        // Read current file first (has newest entries), then rotated files
        foreach (var file in GetLogFilesInOrder())
        {
            var entries = ReadFile(file);
            // Entries in each file are chronological; we'll reverse at the end
            allEntries.AddRange(entries);

            if (allEntries.Count >= needed && source == null)
                break;
        }

        // Sort newest first, then apply source filter and skip/limit
        allEntries.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));

        IEnumerable<FileLogEntry> result = allEntries;
        if (!string.IsNullOrEmpty(source))
            result = result.Where(e => string.Equals(e.Source, source, StringComparison.OrdinalIgnoreCase));

        return result
            .Skip(skip)
            .Take(limit)
            .ToList();
    }

    private IEnumerable<string> GetLogFilesInOrder()
    {
        // Current file has the newest entries
        var current = Path.Combine(_logDir, "log-current.jsonl");
        if (File.Exists(current))
            yield return current;

        // Rotated files: 001 is most recent rotation, 002 is older, etc.
        for (int i = 1; i <= 99; i++)
        {
            var path = Path.Combine(_logDir, $"log-{i:D3}.jsonl");
            if (!File.Exists(path)) break;
            yield return path;
        }
    }

    private static List<FileLogEntry> ReadFile(string path)
    {
        var entries = new List<FileLogEntry>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<FileLogEntry>(line);
                    if (entry != null)
                        entries.Add(entry);
                }
                catch
                {
                    // Skip malformed lines
                }
            }
        }
        catch
        {
            // File may be locked or deleted during rotation
        }
        return entries;
    }
}
