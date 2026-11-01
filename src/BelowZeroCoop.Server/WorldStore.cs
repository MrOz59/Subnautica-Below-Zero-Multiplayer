using System.Text;
using System.Text.Json;

namespace BelowZeroCoop.Server;

/// <summary>
/// Local, explicit, opt-in persistence for the independent world-state model.
/// Saves only our own JSON data. It NEVER modifies Subnautica save files.
/// </summary>
internal static class WorldStore
{
    private const int MaxSnapshotBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false
    };

    public static void Save(string path, WorldState state)
    {
        if (state == null)
            throw new ArgumentNullException(nameof(state));
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A destination path is required.", nameof(path));

        string destination = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] content = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state.Snapshot(), Json));
        // A snapshot too large to load must never be committed to disk.
        // Check before copying backups or replacing the last valid primary.
        if (content.Length > MaxSnapshotBytes)
            throw new InvalidDataException("World snapshot exceeds the safety limit.");

        try
        {
            // Write in the destination directory so final rename stays on
            // the same filesystem. Flush buffers before exposing a new file.
            using (var stream = new FileStream(temporary, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            // Keep the previous complete snapshot as a manual recovery option.
            // Never silently load a backup or discard a corrupted primary.
            if (File.Exists(destination))
                File.Copy(destination, destination + ".bak", overwrite: true);

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static WorldState Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A snapshot path is required.", nameof(path));

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            return new WorldState();

        try
        {
            using FileStream stream = File.OpenRead(fullPath);
            if (stream.Length > MaxSnapshotBytes)
                throw new InvalidDataException("World snapshot exceeds the safety limit.");

            // A syntactically valid '{}' or truncated-but-valid JSON object
            // must NEVER be treated as an empty world. That would erase builds.
            using (JsonDocument parsed = JsonDocument.Parse(stream))
            {
                if (parsed.RootElement.ValueKind != JsonValueKind.Object ||
                    !parsed.RootElement.TryGetProperty("Version", out _) ||
                    !parsed.RootElement.TryGetProperty("Revision", out _) ||
                    !parsed.RootElement.TryGetProperty("Entities", out _) ||
                    !parsed.RootElement.TryGetProperty("Receipts", out _) ||
                    !parsed.RootElement.TryGetProperty("Changes", out _))
                    throw new InvalidDataException(
                        "Incomplete world snapshot; use the explicit .bak recovery file.");
            }

            stream.Position = 0;
            WorldDocument? document = JsonSerializer.Deserialize<WorldDocument>(stream, Json);
            if (document == null)
                throw new InvalidDataException("World snapshot is empty.");
            return WorldState.Restore(document);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "World snapshot is damaged or incompatible. The previous snapshot may " +
                "be available at " + fullPath + ".bak", ex);
        }
    }
}
