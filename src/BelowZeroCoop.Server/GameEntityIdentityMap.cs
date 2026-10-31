using System.Security.Cryptography;
using System.Text;

namespace BelowZeroCoop.Server;

/// <summary>
/// Trusted-host-only translation from a game's persistent string identifier
/// into a scoped, deterministic internal Guid. The ID never comes from a
/// volatile Unity instance hash, a temporary ghost or a client packet.
/// This does not perform construction or grant permissions to mutate a world.
/// </summary>
internal sealed class GameEntityIdentityMap
{
    internal const int SchemaVersion = 1;
    internal const int MaxEntries = 2048;

    private readonly Guid worldScope;
    private readonly Dictionary<string, GameIdentityEntry> bySource =
        new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, string> byId = new();
    private long revision;

    internal GameEntityIdentityMap(Guid worldScope)
    {
        if (worldScope == Guid.Empty)
            throw new ArgumentException("Game identity scope must not be empty.", nameof(worldScope));
        this.worldScope = worldScope;
    }

    /// <summary>Does not allocate or change the map when the source is unknown.</summary>
    internal bool TryFind(string gameId, out Guid entityId)
    {
        entityId = Guid.Empty;
        if (!ValidId(gameId) || !bySource.TryGetValue(gameId, out var entry))
            return false;
        entityId = entry.EntityId;
        return true;
    }

    /// <summary>
    /// Stably assigns an ID from the exact game string + authoritative
    /// world scope. Rebinding is idempotent. A hash collision or capacity
    /// exhaustion rejects the bind instead of silently aliasing two objects.
    /// </summary>
    internal bool TryBind(string gameId, out Guid entityId)
    {
        entityId = Guid.Empty;
        if (!ValidId(gameId))
            return false;
        if (bySource.TryGetValue(gameId, out var existing))
        {
            entityId = existing.EntityId;
            return true;
        }
        if (bySource.Count >= MaxEntries || revision == long.MaxValue)
            return false;

        Guid candidate = Derive(worldScope, gameId);
        if (candidate == Guid.Empty || byId.ContainsKey(candidate))
            return false;

        long nextRevision = revision + 1;
        bySource.Add(gameId, new GameIdentityEntry
        {
            GameId = gameId,
            EntityId = candidate,
            CreatedRevision = nextRevision
        });
        byId.Add(candidate, gameId);
        revision = nextRevision;
        entityId = candidate;
        return true;
    }

    internal GameIdentityDocument Snapshot() => new()
    {
        Version = SchemaVersion,
        ScopeId = worldScope,
        Revision = revision,
        Entries = bySource.Values.OrderBy(x => x.GameId, StringComparer.Ordinal)
            .Select(x => x.Copy()).ToList()
    };

    internal static GameEntityIdentityMap Restore(GameIdentityDocument document,
        Guid expectedScope)
    {
        if (document == null || document.Version != SchemaVersion ||
            expectedScope == Guid.Empty || document.ScopeId != expectedScope ||
            document.Revision < 0 || document.Entries == null ||
            document.Entries.Count > MaxEntries ||
            document.Revision < document.Entries.Count)
            throw new InvalidDataException("Unsupported game identity snapshot.");

        var restored = new GameEntityIdentityMap(expectedScope)
        {
            revision = document.Revision
        };
        var revisions = new HashSet<long>();

        foreach (var entry in document.Entries)
        {
            if (entry == null || !ValidId(entry.GameId) ||
                entry.CreatedRevision < 1 ||
                entry.CreatedRevision > document.Revision ||
                entry.EntityId != Derive(expectedScope, entry.GameId) ||
                entry.EntityId == Guid.Empty ||
                !revisions.Add(entry.CreatedRevision) ||
                !restored.bySource.TryAdd(entry.GameId, entry.Copy()) ||
                !restored.byId.TryAdd(entry.EntityId, entry.GameId))
                throw new InvalidDataException("Invalid or conflicting game identity entry.");
        }

        // Entries are append-only; no removal/eviction is allowed. Otherwise
        // a reused game identifier could silently change its identity.
        if (document.Revision != document.Entries.Count ||
            revisions.Count != document.Entries.Count)
            throw new InvalidDataException("Game identity snapshot has missing history.");
        return restored;
    }

    internal static bool ValidId(string? id)
    {
        // Deliberately strict until actual game GUID formats are validated.
        // Case remains significant: no implicit ToLower or Guid.Parse.
        return !string.IsNullOrWhiteSpace(id) && id.Length <= 128 &&
            id.All(c => (c >= 'a' && c <= 'z') ||
                        (c >= 'A' && c <= 'Z') ||
                        (c >= '0' && c <= '9') ||
                        c is '-' or '_' or '.' or ':' or '/');
    }

    internal static Guid Derive(Guid scope, string gameId)
    {
        if (scope == Guid.Empty || !ValidId(gameId))
            throw new ArgumentException("Cannot derive identity from an invalid world or game ID.");

        // Include a fixed domain separator and the scoped world GUID. All
        // machines encode the same ASCII scope and UTF-8 source string.
        byte[] message = Encoding.UTF8.GetBytes(
            "BelowZeroCoop:GameIdentity:v1\n" + scope.ToString("N") +
            "\n" + gameId);
        using var algorithm = SHA256.Create();
        byte[] digest = algorithm.ComputeHash(message);
        byte[] guidBytes = new byte[16];
        Array.Copy(digest, guidBytes, guidBytes.Length);
        return new Guid(guidBytes);
    }
}

internal sealed class GameIdentityDocument
{
    public int Version { get; set; } = GameEntityIdentityMap.SchemaVersion;
    public Guid ScopeId { get; set; }
    public long Revision { get; set; }
    public List<GameIdentityEntry> Entries { get; set; } = new();
}

internal sealed class GameIdentityEntry
{
    public string GameId { get; set; } = "";
    public Guid EntityId { get; set; }
    public long CreatedRevision { get; set; }

    internal GameIdentityEntry Copy() => new()
    {
        GameId = GameId,
        EntityId = EntityId,
        CreatedRevision = CreatedRevision
    };
}
