using System.Text.Json;

namespace BelowZeroCoop.Server;

internal sealed class IntegratedWorldSession : IDisposable
{
    public const int SchemaVersion = 2;
    private const int MaxBytes = 12 * 1024 * 1024;
    // Service-only actor for standalone world mirroring. Never accepted from
    // a public packet or treated as a real authenticated player ID.
    private const uint HostAuthorityActorId = 1;
    private readonly object gate = new();
    private readonly string file;
    private readonly FileStream exclusiveLock;
    private readonly FabricationRecipe[] recipes;
    private IntegratedWorldDocument document;
    private bool disposed;

    public IntegratedWorldSession(string snapshotPath, Guid gridId,
        long powerCapacity, IEnumerable<FabricationRecipe> trustedRecipes)
    {
        if (string.IsNullOrWhiteSpace(snapshotPath))
            throw new ArgumentException("Snapshot path is required", nameof(snapshotPath));
        if (trustedRecipes == null)
            throw new ArgumentNullException(nameof(trustedRecipes));

        recipes = trustedRecipes.ToArray();
        file = Path.GetFullPath(snapshotPath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        exclusiveLock = new FileStream(file + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (File.Exists(file))
            {
                using var input = File.OpenRead(file);
                if (input.Length > MaxBytes)
                    throw new InvalidDataException("Integrated snapshot exceeds size budget");
                using var root = JsonDocument.Parse(input);
                if (root.RootElement.ValueKind != JsonValueKind.Object ||
                    !root.RootElement.TryGetProperty("Version", out _) ||
                    !root.RootElement.TryGetProperty("Revision", out _) ||
                    !root.RootElement.TryGetProperty("Construction", out _) ||
                    !root.RootElement.TryGetProperty("Power", out _) ||
                    !root.RootElement.TryGetProperty("Fabrication", out _) ||
                    !root.RootElement.TryGetProperty("World", out _))
                    throw new InvalidDataException("Incomplete integrated snapshot");
                input.Position = 0;
                document = JsonSerializer.Deserialize<IntegratedWorldDocument>(input)
                    ?? throw new InvalidDataException("Empty integrated snapshot");
            }
            else
            {
                document = new IntegratedWorldDocument
                {
                    Construction = new ConstructionState().Snapshot(),
                    Power = new PowerGridState(gridId, powerCapacity).Snapshot(),
                    Fabrication = new FabricationState(recipes).Snapshot(),
                    World = new WorldState().Snapshot()
                };
            }

            if (document.Power == null || document.Power.GridId != gridId ||
                document.Power.CapacityMilliUnits != powerCapacity)
                throw new InvalidDataException("Grid identity or capacity mismatch");
            Validate(document, recipes);
        }
        catch
        {
            exclusiveLock.Dispose();
            throw;
        }
    }

    public IntegratedWorldDocument Snapshot()
    {
        lock (gate)
        {
            EnsureOpen();
            return Clone(document);
        }
    }

    public ConstructionResult Place(Guid operationId, long expectedRevision,
        Guid entityId, int generation, string kind, ConstructionBounds bounds,
        IReadOnlyCollection<ConstructionBounds> trustedPlayers)
    {
        lock (gate)
        {
            EnsureOpen();
            return Apply((construction, power, crafting, world) =>
            {
                ConstructionResult result = construction.Place(operationId,
                    expectedRevision, entityId, generation, kind, bounds, trustedPlayers);
                if (!result.Applied || result.Duplicate)
                    return (result, false);
                if (kind == "Fabricator" &&
                    !crafting.RegisterStation(entityId, generation))
                    return (new ConstructionResult(false, false,
                        "fabricator_registration_failed", result.Revision, 0, 0), false);

                WorldEntity? prior = world.Snapshot().Entities
                    .SingleOrDefault(e => e.Id == entityId);
                long expectedEntityRevision = prior?.Revision ?? 0;
                WorldResult built = world.Apply(new WorldCommand(
                    Guid.NewGuid(), HostAuthorityActorId, entityId, generation,
                    expectedEntityRevision, WorldAction.Build, Kind: kind),
                    DateTime.UtcNow);
                if (!built.Applied)
                    return (new ConstructionResult(false, false,
                        "world_registration_failed", result.Revision, 0, 0), false);
                return (result, true);
            });
        }
    }

    public ConstructionResult Connect(Guid operationId, long revision,
        Guid a, int generationA, Guid b, int generationB, ConstructionLinkKind kind)
    {
        lock (gate)
        {
            EnsureOpen();
            return Apply((construction, power, crafting, world) =>
            {
                var result = construction.Connect(operationId, revision,
                    a, generationA, b, generationB, kind);
                return (result, result.Applied && !result.Duplicate);
            });
        }
    }

    public PowerResult InstallPowerDevice(Guid operationId, long expectedPowerRevision,
        Guid constructionId, int generation, PowerRole role, long rate)
    {
        lock (gate)
        {
            EnsureOpen();
            return Apply((construction, power, crafting, world) =>
            {
                ConstructionNode? node = construction.Snapshot().Nodes
                    .SingleOrDefault(n => n.Id == constructionId);
                if (node == null || node.Deleted || node.Generation != generation ||
                    !PowerCompatible(node.Kind, role))
                    return (PowerResult.Reject("construction_mismatch",
                        power.Snapshot().ConfigRevision), false);

                PowerResult result = power.Apply(new PowerCommand(operationId,
                    expectedPowerRevision, PowerAction.Upsert, constructionId,
                    generation, role, rate));
                return (result, result.Applied && !result.Duplicate);
            });
        }
    }

    /// <summary>Only trusted host logic may supply the monotonic clock.</summary>
    public bool AdvancePower(long hostMonotonicMs)
    {
        lock (gate)
        {
            EnsureOpen();
            return Apply((construction, power, crafting, world) =>
            {
                long before = power.Snapshot().LastTickMs;
                if (!power.TryAdvance(hostMonotonicMs, out _))
                    return (false, false);
                return (true, power.Snapshot().LastTickMs != before);
            });
        }
    }

    /// <summary>
    /// Mutating all three domains is a single local snapshot commit.
    /// A failed power teardown or fabrication cleanup rolls back all
    /// tentative tombstones, item refunds and detached power edges.
    /// </summary>
    public IntegratedRemovalResult Remove(Guid operationId, long expectedRevision,
        Guid entityId, int generation, bool cascade,
        IReadOnlyCollection<ConstructionBounds> trustedPlayers, long hostMonotonicMs)
    {
        lock (gate)
        {
            EnsureOpen();
            return Apply((construction, power, crafting, world) =>
            {
                var plan = construction.PreviewRemove(expectedRevision,
                    entityId, generation, cascade, trustedPlayers);
                var result = construction.Remove(operationId, expectedRevision,
                    entityId, generation, cascade, trustedPlayers);
                if (!result.Applied || result.Duplicate)
                    return (new IntegratedRemovalResult(result.Applied,
                        result.Duplicate, result.Reason,
                        result.Revision, result.RemovedCount), false);

                HashSet<Guid> removedIds = new(plan.EntityIds);
                List<PowerDevice> devices = power.Snapshot().Devices
                    .Where(x => !x.Deleted && removedIds.Contains(x.Id)).ToList();
                if (devices.Count > 0 && !power.TryAdvance(hostMonotonicMs, out _))
                    return (IntegratedRemovalResult.Fail("invalid_power_clock"), false);

                foreach (PowerDevice device in devices)
                {
                    PowerResult powerResult = power.Apply(new PowerCommand(Guid.NewGuid(),
                        power.Snapshot().ConfigRevision, PowerAction.Remove,
                        device.Id, device.Generation, PowerRole.None, 0));
                    if (!powerResult.Applied)
                        return (IntegratedRemovalResult.Fail("power_cleanup_failed"), false);
                }

                foreach (ConstructionNode node in construction.Snapshot().Nodes)
                {
                    if (!removedIds.Contains(node.Id) || node.Kind != "Fabricator")
                        continue;
                    if (!crafting.DestroyStation(node.Id, node.Generation))
                        return (IntegratedRemovalResult.Fail("craft_cleanup_failed"), false);
                }

                // The general world index is part of this exact transaction.
                // No construction may be tombstoned while its world entry
                // remains alive, including on a failed persisted write.
                var worldBefore = world.Snapshot().Entities.ToDictionary(x => x.Id);
                DateTime nowUtc = DateTime.UtcNow;
                foreach (Guid removedId in plan.EntityIds)
                {
                    if (!worldBefore.TryGetValue(removedId, out WorldEntity? old) ||
                        !world.TryAcquireLease(removedId, HostAuthorityActorId,
                            nowUtc, TimeSpan.FromSeconds(30)))
                        return (IntegratedRemovalResult.Fail("world_lease_failed"), false);
                    WorldResult destroyed = world.Apply(new WorldCommand(
                        Guid.NewGuid(), HostAuthorityActorId,
                        removedId, old.Generation, old.Revision,
                        WorldAction.Destroy), nowUtc);
                    if (!destroyed.Applied)
                        return (IntegratedRemovalResult.Fail("world_cleanup_failed"), false);
                }

                return (new IntegratedRemovalResult(true, false, "ok",
                    result.Revision, result.RemovedCount), true);
            });
        }
    }

    // These inventory and recipe methods are a *trusted-host test adapter*.
    // No client-supplied actor ID is trusted or accepted over UDP.
    public bool GrantMaterials(Guid operationId, uint actorId,
        string material, int amount)
    {
        lock (gate)
        {
            EnsureOpen();
            return Apply((construction, power, crafting, world) =>
            {
                long revision = crafting.Snapshot().Revision;
                bool credited = crafting.GrantMaterials(operationId, actorId,
                    material, amount);
                return (credited, credited && crafting.Snapshot().Revision != revision);
            });
        }
    }

    public FabricationBeginResult BeginCraft(Guid jobId, Guid stationId,
        int generation, uint verifiedActor, string recipeId, DateTime nowUtc)
    {
        lock (gate)
        {
            EnsureOpen();
            return Apply((construction, power, crafting, world) =>
            {
                if (!crafting.Snapshot().Jobs.Any(j => j.Id == jobId) &&
                    !crafting.TryOpen(stationId, generation, verifiedActor,
                        nowUtc, TimeSpan.FromSeconds(10)))
                    return (FabricationBeginResult.Reject("station_unavailable"), false);

                FabricationBeginResult result = crafting.Begin(jobId, stationId,
                    generation, verifiedActor, recipeId, nowUtc);
                return (result, result.Started && !result.Duplicate);
            });
        }
    }

    public FabricationDeliveryResult DeliverCraft(Guid jobId,
        uint verifiedActor, DateTime nowUtc)
    {
        lock (gate)
        {
            EnsureOpen();
            return Apply((construction, power, crafting, world) =>
            {
                FabricationDeliveryResult result = crafting.DeliverToInventory(
                    jobId, verifiedActor, nowUtc);
                return (result, result.Delivered && !result.Duplicate);
            });
        }
    }

    public bool CancelCraft(Guid jobId, uint verifiedActor, DateTime nowUtc)
    {
        lock (gate)
        {
            EnsureOpen();
            return Apply((construction, power, crafting, world) =>
            {
                long revision = crafting.Snapshot().Revision;
                bool cancelled = crafting.Cancel(jobId, verifiedActor, nowUtc);
                return (cancelled, cancelled &&
                    crafting.Snapshot().Revision != revision);
            });
        }
    }

    public IntegratedWorldDocument SnapshotForJoin() => Snapshot();

    private T Apply<T>(Func<ConstructionState, PowerGridState, FabricationState,
        WorldState, (T Result, bool Changed)> operation)
    {
        var construction = ConstructionState.Restore(document.Construction);
        var power = PowerGridState.Restore(document.Power);
        var crafting = FabricationState.Restore(document.Fabrication, recipes);
        var world = WorldState.Restore(document.World);
        (T result, bool changed) = operation(construction, power, crafting, world);
        if (!changed)
            return result;

        IntegratedWorldDocument next = new()
        {
            Version = SchemaVersion,
            Revision = checked(document.Revision + 1),
            Construction = construction.Snapshot(),
            Power = power.Snapshot(),
            Fabrication = crafting.Snapshot(),
            World = world.Snapshot()
        };
        Validate(next, recipes);
        Save(next);
        document = next; // publish only after the file commit succeeds
        return result;
    }

    private void Save(IntegratedWorldDocument next)
    {
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(next);
        if (content.Length > MaxBytes)
            throw new InvalidDataException("Integrated snapshot exceeds size budget");

        string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
            {
                output.Write(content);
                output.Flush(flushToDisk: true);
            }
            if (File.Exists(file))
                File.Copy(file, file + ".bak", overwrite: true);
            File.Move(temp, file, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static void Validate(IntegratedWorldDocument snapshot,
        FabricationRecipe[] recipes)
    {
        if (snapshot == null || snapshot.Version != SchemaVersion ||
            snapshot.Revision < 0 || snapshot.Construction == null ||
            snapshot.Power == null || snapshot.Fabrication == null ||
            snapshot.World == null)
            throw new InvalidDataException("Unsupported integrated world schema");

        var construction = ConstructionState.Restore(snapshot.Construction);
        PowerGridState.Restore(snapshot.Power);
        FabricationState.Restore(snapshot.Fabrication, recipes);
        WorldState.Restore(snapshot.World);
        Dictionary<Guid, ConstructionNode> nodes =
            construction.Snapshot().Nodes.ToDictionary(x => x.Id);

        // Validate both directions: no orphan world index entries and no
        // topology nodes absent from the general registry.
        if (snapshot.World.Entities.Count != nodes.Count)
            throw new InvalidDataException("Construction/world entity count mismatch");
        foreach (WorldEntity worldEntity in snapshot.World.Entities)
        {
            if (!nodes.TryGetValue(worldEntity.Id, out ConstructionNode? node) ||
                worldEntity.Generation != node.Generation ||
                worldEntity.Deleted != node.Deleted ||
                worldEntity.Kind != node.Kind)
                throw new InvalidDataException("Construction and world index diverged");
        }

        foreach (PowerDevice device in snapshot.Power.Devices)
        {
            if (!nodes.TryGetValue(device.Id, out ConstructionNode? node) ||
                node.Generation < device.Generation ||
                (!device.Deleted && (node.Deleted ||
                    node.Generation != device.Generation ||
                    !PowerCompatible(node.Kind, device.Role))))
                throw new InvalidDataException(
                    "Power device lacks matching live construction");
        }

        foreach (FabricationStation station in snapshot.Fabrication.Stations)
        {
            if (!nodes.TryGetValue(station.Id, out ConstructionNode? node) ||
                node.Kind != "Fabricator" || node.Generation < station.Generation ||
                (!station.Deleted &&
                 (node.Deleted || node.Generation != station.Generation)))
                throw new InvalidDataException(
                    "Fabricator lacks matching construction");
        }

        foreach (ConstructionNode node in nodes.Values)
        {
            if (!node.Deleted && node.Kind == "Fabricator" &&
                !snapshot.Fabrication.Stations.Any(x =>
                    x.Id == node.Id && x.Generation == node.Generation && !x.Deleted))
                throw new InvalidDataException("Live Fabricator missing station");
        }
    }

    private static bool PowerCompatible(string kind, PowerRole role) =>
        role switch
        {
            PowerRole.Generator => kind is "SolarPanel" or "NuclearReactor" or "Bioreactor",
            PowerRole.Consumer => kind is "Fabricator" or "WaterFiltration",
            _ => false
        };

    private static IntegratedWorldDocument Clone(IntegratedWorldDocument source) =>
        JsonSerializer.Deserialize<IntegratedWorldDocument>(
            JsonSerializer.Serialize(source))!;

    private void EnsureOpen()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(IntegratedWorldSession));
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            exclusiveLock.Dispose();
            disposed = true;
        }
    }
}

internal sealed class IntegratedWorldDocument
{
    public int Version { get; set; } = IntegratedWorldSession.SchemaVersion;
    public long Revision { get; set; }
    public ConstructionDocument Construction { get; set; } = null!;
    public PowerDocument Power { get; set; } = null!;
    public FabricationDocument Fabrication { get; set; } = null!;
    public WorldDocument World { get; set; } = null!;
}

internal sealed record IntegratedRemovalResult(
    bool Applied, bool Duplicate, string Reason, long ConstructionRevision,
    int RemovedCount)
{
    internal static IntegratedRemovalResult Fail(string reason) =>
        new(false, false, reason, 0, 0);
}
