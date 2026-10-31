using System.Text.Json;

namespace BelowZeroCoop.Server;

/// <summary>
/// Isolated host-side construction topology (#6, #10, #12, #79, #80).
/// No real-game hooks; no access from the unauthenticated UDP listener.
/// </summary>
internal sealed class ConstructionState
{
    public const int SchemaVersion = 1;
    public const int MaxNodes = 2048;
    public const int MaxLinks = 8192;
    public const int MaxReceipts = 4096;
    public const int MaxCascade = 128;

    private readonly object gate = new();
    private readonly Dictionary<Guid, ConstructionNode> nodes = new();
    private readonly List<ConstructionLink> links = new();
    private readonly Dictionary<Guid, ConstructionReceipt> receipts = new();
    private readonly Queue<Guid> receiptOrder = new();
    private long revision;

    public ConstructionResult Place(Guid commandId, long expectedRevision,
        Guid entityId, int generation, string kind, ConstructionBounds bounds,
        IReadOnlyCollection<ConstructionBounds> trustedPlayerVolumes)
    {
        lock (gate)
        {
            string signature = JsonSerializer.Serialize(new
            {
                Action = "Place", expectedRevision, entityId, generation, kind, bounds
            });
            if (Replay(commandId, signature, out ConstructionResult replay))
                return replay;

            if (!Ready(commandId, expectedRevision) || !ValidKey(kind) ||
                !ValidBounds(bounds) || trustedPlayerVolumes == null ||
                !ValidVolumes(trustedPlayerVolumes) ||
                trustedPlayerVolumes.Any(bounds.Intersects))
                return Reject("invalid_placement");

            if (nodes.TryGetValue(entityId, out ConstructionNode? previous))
            {
                if (!previous.Deleted || previous.Generation == int.MaxValue ||
                    generation != previous.Generation + 1 ||
                    links.Any(link => link.Has(entityId)))
                    return Reject("generation_conflict");

                previous.Generation = generation;
                previous.Kind = kind;
                previous.Volume = bounds;
                previous.Deleted = false;
                previous.Revision = ++revision;
            }
            else
            {
                if (entityId == Guid.Empty || generation != 1 || nodes.Count >= MaxNodes)
                    return Reject("invalid_new_entity");

                nodes.Add(entityId, new ConstructionNode
                {
                    Id = entityId, Generation = generation, Kind = kind,
                    Volume = bounds, Revision = ++revision
                });
            }

            return Record(commandId, signature, 1, 0);
        }
    }

    public ConstructionResult Connect(Guid commandId, long expectedRevision,
        Guid a, int generationA, Guid b, int generationB, ConstructionLinkKind kind)
    {
        lock (gate)
        {
            if (kind == ConstructionLinkKind.Power && a.CompareTo(b) > 0)
            {
                (a, b) = (b, a);
                (generationA, generationB) = (generationB, generationA);
            }

            string signature = JsonSerializer.Serialize(new
            {
                Action = "Connect", expectedRevision, a, generationA, b, generationB, kind
            });
            if (Replay(commandId, signature, out ConstructionResult replay))
                return replay;

            if (!Ready(commandId, expectedRevision) || a == b ||
                !Enum.IsDefined(typeof(ConstructionLinkKind), kind) ||
                !IsLive(a, generationA) || !IsLive(b, generationB) ||
                links.Count >= MaxLinks)
                return Reject("invalid_connection");

            ConstructionLink proposed = new()
            {
                A = a, GenerationA = generationA,
                B = b, GenerationB = generationB, Kind = kind
            };
            if (links.Any(x => x.Same(proposed)))
                return Reject("connection_exists");

            // Structural edges are directed: A (supported child) -> B
            // (supporting parent). Reject parent chains that become cyclic.
            if (kind == ConstructionLinkKind.Structural &&
                (HasPath(b, a) || links.Any(x =>
                    x.Kind == ConstructionLinkKind.Structural && x.A == a)))
                return Reject("invalid_structural_dependency");

            links.Add(proposed);
            revision++;
            return Record(commandId, signature, 0, 0);
        }
    }

    public ConstructionResult Disconnect(Guid commandId, long expectedRevision,
        Guid a, Guid b, ConstructionLinkKind kind)
    {
        lock (gate)
        {
            if (kind == ConstructionLinkKind.Power && a.CompareTo(b) > 0)
                (a, b) = (b, a);

            string signature = JsonSerializer.Serialize(new
            {
                Action = "Disconnect", expectedRevision, a, b, kind
            });
            if (Replay(commandId, signature, out ConstructionResult replay))
                return replay;
            if (!Ready(commandId, expectedRevision))
                return Reject("revision_conflict");

            int index = links.FindIndex(x => x.A == a && x.B == b && x.Kind == kind);
            if (index < 0)
                return Reject("missing_connection");

            links.RemoveAt(index);
            revision++;
            return Record(commandId, signature, 0, 1);
        }
    }

    /// <summary>
    /// Power edges are detachable: they do not hold a dome/room hostage when a
    /// transmitter is installed. Structural dependencies do block deletion
    /// unless explicit child-first cascading was requested. Occupancy is a
    /// trusted host input, not a client packet.
    /// </summary>
    public ConstructionRemovalPlan PreviewRemove(
        long expectedRevision, Guid id, int generation, bool cascade,
        IReadOnlyCollection<ConstructionBounds> trustedOccupantVolumes)
    {
        lock (gate)
            return EvaluateRemoval(expectedRevision, id, generation, cascade,
                                   trustedOccupantVolumes);
    }

    public ConstructionResult Remove(Guid commandId, long expectedRevision,
        Guid id, int generation, bool cascade,
        IReadOnlyCollection<ConstructionBounds> trustedOccupantVolumes)
    {
        lock (gate)
        {
            // Occupancy is intentionally excluded from the idempotency
            // signature because it is ephemeral world-authority context.
            string signature = JsonSerializer.Serialize(new
            {
                Action = "Remove", expectedRevision, id, generation, cascade
            });
            if (Replay(commandId, signature, out ConstructionResult replay))
                return replay;

            ConstructionRemovalPlan plan = EvaluateRemoval(expectedRevision,
                id, generation, cascade, trustedOccupantVolumes);
            if (!plan.Allowed)
                return Reject(plan.Reason);

            HashSet<Guid> removed = new(plan.EntityIds);
            int detached = links.RemoveAll(edge =>
                removed.Contains(edge.A) || removed.Contains(edge.B));
            long newRevision = ++revision;
            foreach (Guid childId in plan.EntityIds)
            {
                ConstructionNode node = nodes[childId];
                node.Deleted = true;
                node.Revision = newRevision;
            }
            return Record(commandId, signature, removed.Count, detached);
        }
    }

    public ConstructionDocument Snapshot()
    {
        lock (gate)
        {
            return new ConstructionDocument
            {
                Version = SchemaVersion,
                Revision = revision,
                Nodes = nodes.Values.OrderBy(x => x.Id).Select(x => x.Copy()).ToList(),
                Links = links.Select(x => x.Copy()).ToList(),
                Receipts = receiptOrder.Select(id => receipts[id]).ToList()
            };
        }
    }

    public static ConstructionState Restore(ConstructionDocument document)
    {
        if (document == null || document.Version != SchemaVersion ||
            document.Revision < 0 || document.Nodes == null ||
            document.Links == null || document.Receipts == null ||
            document.Nodes.Count > MaxNodes || document.Links.Count > MaxLinks ||
            document.Receipts.Count > MaxReceipts)
            throw new InvalidDataException("Invalid construction snapshot schema");

        ConstructionState result = new() { revision = document.Revision };
        foreach (ConstructionNode node in document.Nodes)
        {
            if (node == null || node.Id == Guid.Empty || node.Generation < 1 ||
                !ValidKey(node.Kind) || !ValidBounds(node.Volume) ||
                node.Revision <= 0 || node.Revision > document.Revision ||
                !result.nodes.TryAdd(node.Id, node.Copy()))
                throw new InvalidDataException("Invalid or duplicate construction node");
        }

        foreach (ConstructionLink link in document.Links)
        {
            if (link == null || link.A == link.B ||
                !Enum.IsDefined(typeof(ConstructionLinkKind), link.Kind) ||
                !result.IsLive(link.A, link.GenerationA) ||
                !result.IsLive(link.B, link.GenerationB) ||
                (link.Kind == ConstructionLinkKind.Power &&
                 link.A.CompareTo(link.B) > 0) ||
                result.links.Any(existing => existing.Same(link)) ||
                (link.Kind == ConstructionLinkKind.Structural &&
                 (result.HasPath(link.B, link.A) ||
                  result.links.Any(x => x.Kind == ConstructionLinkKind.Structural &&
                                        x.A == link.A))))
                throw new InvalidDataException("Invalid, duplicated or cyclic structure link");

            result.links.Add(link.Copy());
        }

        foreach (ConstructionReceipt receipt in document.Receipts)
        {
            if (receipt == null || receipt.OperationId == Guid.Empty ||
                string.IsNullOrWhiteSpace(receipt.Signature) ||
                receipt.Signature.Length > 2048 ||
                receipt.Revision < 1 || receipt.Revision > document.Revision ||
                receipt.RemovedCount < 0 || receipt.RemovedCount > MaxCascade ||
                receipt.DetachedCount < 0 || receipt.DetachedCount > MaxLinks ||
                !result.receipts.TryAdd(receipt.OperationId, receipt))
                throw new InvalidDataException("Invalid construction operation receipt");
            result.receiptOrder.Enqueue(receipt.OperationId);
        }

        return result;
    }

    private ConstructionRemovalPlan EvaluateRemoval(long expectedRevision,
        Guid id, int generation, bool cascade,
        IReadOnlyCollection<ConstructionBounds> occupied)
    {
        if (expectedRevision != revision || !IsLive(id, generation) ||
            occupied == null || !ValidVolumes(occupied))
            return new ConstructionRemovalPlan(false, "invalid_or_stale_request",
                Array.Empty<Guid>(), 0);

        // Traverse child->parent relations backwards to find dependents.
        // Power links never enter this traversal, regardless of their
        // endpoints. A power transmitter therefore cannot obstruct removal.
        HashSet<Guid> group = new() { id };
        Queue<Guid> pending = new();
        pending.Enqueue(id);
        while (pending.Count > 0)
        {
            Guid parent = pending.Dequeue();
            foreach (ConstructionLink edge in links)
            {
                if (edge.Kind != ConstructionLinkKind.Structural ||
                    edge.B != parent || !group.Add(edge.A))
                    continue;

                if (group.Count > MaxCascade)
                    return new ConstructionRemovalPlan(false, "cascade_too_large",
                        Array.Empty<Guid>(), 0);
                pending.Enqueue(edge.A);
            }
        }

        if (!cascade && group.Count > 1)
            return new ConstructionRemovalPlan(false, "structural_dependents",
                Array.Empty<Guid>(), 0);

        if (group.Any(entityId => occupied.Any(volume =>
                nodes[entityId].Volume.Intersects(volume))))
            return new ConstructionRemovalPlan(false, "occupied_volume",
                Array.Empty<Guid>(), 0);

        int linkCount = links.Count(link =>
            group.Contains(link.A) || group.Contains(link.B));

        // Deterministic ordering: structural children are listed first.
        // The delete itself is atomic, so this is for future scene teardown.
        List<Guid> order = new();
        HashSet<Guid> visited = new();
        void Visit(Guid parent)
        {
            foreach (ConstructionLink edge in links.Where(edge =>
                edge.Kind == ConstructionLinkKind.Structural && edge.B == parent)
                .OrderBy(edge => edge.A))
            {
                if (group.Contains(edge.A) && visited.Add(edge.A))
                    Visit(edge.A);
            }
            order.Add(parent);
        }
        visited.Add(id);
        Visit(id);

        return new ConstructionRemovalPlan(true, "ok", order, linkCount);
    }

    private bool HasPath(Guid start, Guid target)
    {
        HashSet<Guid> visited = new();
        Stack<Guid> stack = new();
        stack.Push(start);
        while (stack.Count > 0)
        {
            Guid current = stack.Pop();
            if (current == target)
                return true;
            if (!visited.Add(current))
                continue;
            foreach (ConstructionLink edge in links)
            {
                if (edge.Kind == ConstructionLinkKind.Structural && edge.A == current)
                    stack.Push(edge.B);
            }
        }
        return false;
    }

    private bool IsLive(Guid id, int generation)
        => nodes.TryGetValue(id, out ConstructionNode? node) &&
           !node.Deleted && node.Generation == generation;

    private static bool ValidBounds(ConstructionBounds bounds)
        => bounds != null &&
           double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
           double.IsFinite(bounds.Z) &&
           double.IsFinite(bounds.RadiusX) && double.IsFinite(bounds.RadiusY) &&
           double.IsFinite(bounds.RadiusZ) &&
           Math.Abs(bounds.X) <= 100000 && Math.Abs(bounds.Y) <= 100000 &&
           Math.Abs(bounds.Z) <= 100000 &&
           bounds.RadiusX > 0 && bounds.RadiusX <= 200 &&
           bounds.RadiusY > 0 && bounds.RadiusY <= 200 &&
           bounds.RadiusZ > 0 && bounds.RadiusZ <= 200;

    private static bool ValidVolumes(IReadOnlyCollection<ConstructionBounds> volumes)
        => volumes.Count <= 64 && volumes.All(ValidBounds);

    private static bool ValidKey(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
           value.All(ch => char.IsAsciiLetterOrDigit(ch) ||
                           ch == '_' || ch == '-' || ch == '.');

    private bool Ready(Guid operationId, long expectedRevision)
        => operationId != Guid.Empty && expectedRevision == revision;

    private bool Replay(Guid operationId, string signature,
                        out ConstructionResult result)
    {
        result = Reject("not_found");
        if (!receipts.TryGetValue(operationId, out ConstructionReceipt? prior))
            return false;

        result = prior.Signature == signature
            ? new ConstructionResult(true, true, "duplicate", prior.Revision,
                prior.RemovedCount, prior.DetachedCount)
            : Reject("operation_id_collision");
        return true;
    }

    private ConstructionResult Record(Guid operationId, string signature,
                                      int removed, int detached)
    {
        receipts.Add(operationId, new ConstructionReceipt(
            operationId, signature, revision, removed, detached));
        receiptOrder.Enqueue(operationId);
        while (receipts.Count > MaxReceipts)
            receipts.Remove(receiptOrder.Dequeue());
        return new ConstructionResult(true, false, "ok", revision, removed, detached);
    }

    private ConstructionResult Reject(string reason)
        => new(false, false, reason, revision, 0, 0);
}

internal enum ConstructionLinkKind { Structural = 1, Power = 2 }

internal sealed record ConstructionBounds(
    double X, double Y, double Z,
    double RadiusX, double RadiusY, double RadiusZ)
{
    public bool Intersects(ConstructionBounds other)
        => Math.Abs(X - other.X) < RadiusX + other.RadiusX &&
           Math.Abs(Y - other.Y) < RadiusY + other.RadiusY &&
           Math.Abs(Z - other.Z) < RadiusZ + other.RadiusZ;
}

internal sealed class ConstructionNode
{
    public Guid Id { get; set; }
    public int Generation { get; set; }
    public long Revision { get; set; }
    public string Kind { get; set; } = "";
    public ConstructionBounds Volume { get; set; } = null!;
    public bool Deleted { get; set; }
    public ConstructionNode Copy() => new()
    {
        Id = Id, Generation = Generation, Revision = Revision, Kind = Kind,
        Volume = Volume with { }, Deleted = Deleted
    };
}

internal sealed class ConstructionLink
{
    public Guid A { get; set; }
    public int GenerationA { get; set; }
    public Guid B { get; set; }
    public int GenerationB { get; set; }
    public ConstructionLinkKind Kind { get; set; }
    public bool Has(Guid nodeId) => A == nodeId || B == nodeId;
    public bool Same(ConstructionLink other)
        => A == other.A && B == other.B && Kind == other.Kind &&
           GenerationA == other.GenerationA && GenerationB == other.GenerationB;
    public ConstructionLink Copy() => new()
    {
        A = A, B = B, GenerationA = GenerationA,
        GenerationB = GenerationB, Kind = Kind
    };
}

internal sealed class ConstructionDocument
{
    public int Version { get; set; } = ConstructionState.SchemaVersion;
    public long Revision { get; set; }
    public List<ConstructionNode> Nodes { get; set; } = new();
    public List<ConstructionLink> Links { get; set; } = new();
    public List<ConstructionReceipt> Receipts { get; set; } = new();
}

internal sealed record ConstructionReceipt(
    Guid OperationId, string Signature, long Revision,
    int RemovedCount, int DetachedCount);

internal sealed record ConstructionResult(
    bool Applied, bool Duplicate, string Reason, long Revision,
    int RemovedCount, int DetachedCount);

internal sealed record ConstructionRemovalPlan(
    bool Allowed, string Reason, IReadOnlyList<Guid> EntityIds, int DetachedLinks);
