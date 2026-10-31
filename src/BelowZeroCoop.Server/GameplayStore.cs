using System.Text.Json;

namespace BelowZeroCoop.Server;

/// <summary>
/// File-backed, versioned metadata snapshots. Does NOT touch the game's native
/// save directory. Only intended for local, explicit development storage.
/// </summary>
internal static class GameplayStore
{
    private const int MaxSnapshotBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false
    };

    public static void SaveEquipment(string path, EquipmentState state)
    {
        if (state == null)
            throw new ArgumentNullException(nameof(state));
        Write(path, state.Snapshot());
    }

    public static EquipmentState LoadEquipment(string path)
    {
        EquipmentDocument document = Read<EquipmentDocument>(
            path, "Version", "Players", "Receipts");
        return EquipmentState.Restore(document);
    }

    public static void SavePower(string path, PowerGridState state)
    {
        if (state == null)
            throw new ArgumentNullException(nameof(state));
        Write(path, state.Snapshot());
    }

    public static PowerGridState LoadPower(string path)
    {
        PowerDocument document = Read<PowerDocument>(
            path, "Version", "GridId", "ConfigRevision", "CapacityMilliUnits",
            "EnergyMilliUnits", "LastTickMs", "Devices", "Receipts");
        return PowerGridState.Restore(document);
    }

    public static void SaveFabrication(string path, FabricationState state)
    {
        if (state == null)
            throw new ArgumentNullException(nameof(state));
        Write(path, state.Snapshot());
    }

    public static FabricationState LoadFabrication(
        string path, IEnumerable<FabricationRecipe> trustedRecipes)
    {
        FabricationDocument document = Read<FabricationDocument>(
            path, "Version", "Revision", "Stations", "Jobs",
            "Inventories", "Grants");
        return FabricationState.Restore(document, trustedRecipes);
    }

    public static void SaveConstruction(string path, ConstructionState state)
    {
        if (state == null)
            throw new ArgumentNullException(nameof(state));
        Write(path, state.Snapshot());
    }

    public static ConstructionState LoadConstruction(string path)
    {
        ConstructionDocument document = Read<ConstructionDocument>(
            path, "Version", "Revision", "Nodes", "Links", "Receipts");
        return ConstructionState.Restore(document);
    }

    private static void Write<T>(string path, T document)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A snapshot path is required.", nameof(path));

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(document, Json);
        if (payload.Length > MaxSnapshotBytes)
            throw new InvalidDataException("Metadata snapshot exceeds the size limit.");

        string file = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(file))
                File.Copy(file, file + ".bak", overwrite: true);
            File.Move(temporary, file, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void ValidateEntries(
        JsonElement document, string collection, params string[] required)
    {
        if (!document.TryGetProperty(collection, out JsonElement items) ||
            items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Metadata collection is missing or invalid.");

        foreach (JsonElement item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                required.Any(key => !item.TryGetProperty(key, out _)))
                throw new InvalidDataException(
                    "A metadata object is missing required fields.");
        }
    }

    private static T Read<T>(string path, params string[] requiredProperties)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A snapshot path is required.", nameof(path));

        string file = Path.GetFullPath(path);
        using FileStream stream = File.OpenRead(file);
        if (stream.Length > MaxSnapshotBytes)
            throw new InvalidDataException("Metadata snapshot exceeds the size limit.");

        try
        {
            using (JsonDocument json = JsonDocument.Parse(stream))
            {
                if (json.RootElement.ValueKind != JsonValueKind.Object ||
                    requiredProperties.Any(property =>
                        !json.RootElement.TryGetProperty(property, out _)))
                    throw new InvalidDataException(
                        "Metadata snapshot is incomplete; restore an explicit backup.");

                // Reject partial *nested* records too. A missing Slots array
                // could otherwise become five empty quickslots after loading.
                if (typeof(T) == typeof(EquipmentDocument))
                    ValidateEntries(json.RootElement, "Players",
                        "PlayerId", "Revision", "OwnedItems", "Slots");

                // A source missing its stored Rate would otherwise silently
                // become a zero-production generator after restart.
                if (typeof(T) == typeof(PowerDocument))
                    ValidateEntries(json.RootElement, "Devices",
                        "Id", "Generation", "Role",
                        "RateMilliUnitsPerSecond", "Deleted");

                if (typeof(T) == typeof(FabricationDocument))
                {
                    ValidateEntries(json.RootElement, "Stations",
                        "Id", "Generation", "Deleted");
                    ValidateEntries(json.RootElement, "Jobs",
                        "Id", "StationId", "StationGeneration", "OwnerId",
                        "RecipeId", "OutputType", "OutputItemId",
                        "StartedUtc", "ReadyUtc", "Status",
                        "ReservedIngredients");
                    ValidateEntries(json.RootElement, "Inventories",
                        "ActorId", "Materials", "OwnedItems");
                    ValidateEntries(json.RootElement, "Grants",
                        "OperationId", "ActorId", "Material", "Quantity");
                }

                if (typeof(T) == typeof(ConstructionDocument))
                {
                    ValidateEntries(json.RootElement, "Nodes",
                        "Id", "Generation", "Revision", "Kind", "Volume", "Deleted");
                    ValidateEntries(json.RootElement, "Links",
                        "A", "B", "GenerationA", "GenerationB", "Kind");
                    ValidateEntries(json.RootElement, "Receipts",
                        "OperationId", "Signature", "Revision",
                        "RemovedCount", "DetachedCount");
                }
            }

            stream.Position = 0;
            return JsonSerializer.Deserialize<T>(stream, Json)
                   ?? throw new InvalidDataException("Metadata snapshot was empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Metadata snapshot is malformed.", ex);
        }
    }
}
