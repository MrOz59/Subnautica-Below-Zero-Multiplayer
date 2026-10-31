namespace BelowZeroCoop.Server;

/// <summary>
/// Server-owned fabricator lifecycle for regression prevention (#3, #76).
/// This is a standalone model: no network endpoint accepts these commands.
/// Caller identity and recipe authority MUST be verified by a future game adapter.
/// </summary>
internal sealed class FabricationState
{
    public const int SchemaVersion = 2;
    public const int MaxStations = 512;
    public const int MaxJobs = 4096;
    private static readonly TimeSpan MaxLease = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxCraftTime = TimeSpan.FromMinutes(2);
    private const int MaxPlayers = 16;
    private const int MaxMaterialKinds = 128;
    private const int MaxOwnedItems = 128;
    private const int MaxMaterialPerKind = 100000;
    private const int MaxGrants = 4096;

    private readonly object gate = new();
    private readonly Dictionary<Guid, FabricationStation> stations = new();
    private readonly Dictionary<Guid, FabricationJob> jobs = new();
    private readonly Dictionary<Guid, FabricationLease> leases = new();
    private readonly Dictionary<string, FabricationRecipe> recipes;
    // Part of the SAME aggregate/snapshot as the jobs: resource deductions
    // and output grants cannot be split across independent save files.
    private readonly Dictionary<uint, FabricationInventory> inventories = new();
    // Do not evict grants: evicting operation IDs would make stale replays pay again.
    private readonly Dictionary<Guid, FabricationGrantReceipt> grants = new();
    private long revision;

    public FabricationState(IEnumerable<FabricationRecipe> trustedRecipes)
    {
        if (trustedRecipes == null)
            throw new ArgumentNullException(nameof(trustedRecipes));

        recipes = new Dictionary<string, FabricationRecipe>(StringComparer.Ordinal);
        foreach (FabricationRecipe recipe in trustedRecipes)
        {
            if (recipe == null || !KeyValid(recipe.Id) || !KeyValid(recipe.OutputType) ||
                recipe.Duration <= TimeSpan.Zero || recipe.Duration > MaxCraftTime ||
                recipe.Ingredients == null || recipe.Ingredients.Count > 32 ||
                recipe.Ingredients.Any(entry => !KeyValid(entry.Key) ||
                    entry.Value <= 0 || entry.Value > MaxMaterialPerKind) ||
                !recipes.TryAdd(recipe.Id, recipe with
                {
                    Ingredients = new Dictionary<string, int>(
                        recipe.Ingredients, StringComparer.Ordinal)
                }))
                throw new ArgumentException("Invalid or duplicated trusted recipe.");
        }
    }

    /// <summary>Only the authoritative world adapter may register a station.</summary>
    public bool RegisterStation(Guid stationId, int generation)
    {
        if (stationId == Guid.Empty || generation < 1)
            return false;

        lock (gate)
        {
            if (!stations.TryGetValue(stationId, out FabricationStation? current))
            {
                if (generation != 1 || stations.Count >= MaxStations)
                    return false;
                stations.Add(stationId, new FabricationStation
                {
                    Id = stationId, Generation = generation
                });
                revision++;
                return true;
            }

            if (!current.Deleted)
                return current.Generation == generation;

            if (current.Generation == int.MaxValue ||
                generation != current.Generation + 1)
                return false;

            current.Generation = generation;
            current.Deleted = false;
            leases.Remove(stationId);
            revision++;
            return true;
        }
    }

    public bool DestroyStation(Guid stationId, int generation)
    {
        lock (gate)
        {
            if (!stations.TryGetValue(stationId, out FabricationStation? station) ||
                station.Deleted || station.Generation != generation)
                return false;

            station.Deleted = true;
            leases.Remove(stationId);
            foreach (FabricationJob job in jobs.Values)
            {
                if (job.StationId == stationId && job.StationGeneration == generation &&
                    job.Status == FabricationStatus.Running)
                {
                    RefundIngredients(job);
                    job.Status = FabricationStatus.Cancelled;
                    revision++;
                }
            }
            // Already completed output remains reserved for its owner.
            revision++;
            return true;
        }
    }

    public bool TryOpen(Guid stationId, int generation, uint verifiedActor,
                        DateTime utcNow, TimeSpan leaseTime)
    {
        if (!Utc(utcNow) || verifiedActor == 0 || leaseTime <= TimeSpan.Zero ||
            leaseTime > MaxLease)
            return false;

        lock (gate)
        {
            AdvanceInternal(utcNow);
            if (!StationActive(stationId, generation) ||
                HasRunningJob(stationId, generation))
                return false;

            ExpireLeasesInternal(utcNow);
            if (leases.TryGetValue(stationId, out FabricationLease? existing) &&
                existing.ActorId != verifiedActor)
                return false;

            leases[stationId] = new FabricationLease(verifiedActor, utcNow + leaseTime);
            return true;
        }
    }

    public bool TryClose(Guid stationId, uint verifiedActor)
    {
        lock (gate)
        {
            if (verifiedActor == 0 || !leases.TryGetValue(stationId, out FabricationLease? lease) ||
                lease.ActorId != verifiedActor)
                return false;

            leases.Remove(stationId);
            return true;
        }
    }

    /// <summary>
    /// JobId is the idempotency key. Output ID is generated exactly once
    /// and never re-created when duplicate Begin requests are received.
    /// </summary>
    public FabricationBeginResult Begin(Guid jobId, Guid stationId, int generation,
                                       uint verifiedActor, string recipeId, DateTime utcNow)
    {
        lock (gate)
        {
            if (jobId == Guid.Empty || verifiedActor == 0 || !Utc(utcNow) ||
                !recipes.TryGetValue(recipeId ?? "", out FabricationRecipe? recipe))
                return FabricationBeginResult.Reject("invalid_request");

            if (jobs.TryGetValue(jobId, out FabricationJob? previous))
            {
                return previous.StationId == stationId &&
                       previous.StationGeneration == generation &&
                       previous.OwnerId == verifiedActor &&
                       previous.RecipeId == recipeId
                    ? new FabricationBeginResult(true, true, "duplicate", jobId,
                        previous.OutputItemId)
                    : FabricationBeginResult.Reject("job_id_collision");
            }

            AdvanceInternal(utcNow);
            ExpireLeasesInternal(utcNow);
            if (!StationActive(stationId, generation) ||
                !leases.TryGetValue(stationId, out FabricationLease? lease) ||
                lease.ActorId != verifiedActor || HasRunningJob(stationId, generation))
                return FabricationBeginResult.Reject("station_not_available");
            if (jobs.Count >= MaxJobs)
                return FabricationBeginResult.Reject("job_capacity");

            if (recipe.Ingredients.Count != 0)
            {
                if (!inventories.TryGetValue(verifiedActor, out FabricationInventory? stock) ||
                    recipe.Ingredients.Any(entry =>
                        !stock.Materials.TryGetValue(entry.Key, out int available) ||
                        available < entry.Value))
                    return FabricationBeginResult.Reject("missing_materials");

                foreach (var entry in recipe.Ingredients)
                {
                    int remaining = stock.Materials[entry.Key] - entry.Value;
                    if (remaining == 0)
                        stock.Materials.Remove(entry.Key);
                    else
                        stock.Materials[entry.Key] = remaining;
                }
            }

            FabricationJob job = new()
            {
                Id = jobId,
                StationId = stationId,
                StationGeneration = generation,
                OwnerId = verifiedActor,
                RecipeId = recipe.Id,
                OutputType = recipe.OutputType,
                OutputItemId = Guid.NewGuid(),
                ReservedIngredients = new Dictionary<string, int>(
                    recipe.Ingredients, StringComparer.Ordinal),
                StartedUtc = utcNow,
                ReadyUtc = utcNow + recipe.Duration,
                Status = FabricationStatus.Running
            };
            jobs.Add(jobId, job);
            // The interaction lease is no longer needed while the machine
            // works. The active job itself prevents another Begin.
            leases.Remove(stationId);
            revision++;
            return new FabricationBeginResult(true, false, "started",
                jobId, job.OutputItemId);
        }
    }

    /// <summary>
    /// A claimed result has one immutable item identity. Repeated calls return
    /// NewlyClaimed=false. The future inventory adapter must use the returned
    /// item ID and a durable joint commit/receipt to guarantee crash safety.
    /// </summary>
    public FabricationClaimResult Claim(Guid jobId, uint verifiedActor, DateTime utcNow)
    {
        lock (gate)
        {
            if (verifiedActor == 0 || !Utc(utcNow) ||
                !jobs.TryGetValue(jobId, out FabricationJob? job) ||
                job.OwnerId != verifiedActor)
                return FabricationClaimResult.Reject("job_not_found");

            AdvanceInternal(utcNow);
            if (job.Status == FabricationStatus.Claimed)
                return new FabricationClaimResult(true, false, "already_claimed",
                    job.OutputItemId, job.OutputType);
            if (job.Status == FabricationStatus.Delivering)
                return new FabricationClaimResult(true, false, "pending_delivery",
                    job.OutputItemId, job.OutputType);
            if (job.Status != FabricationStatus.Ready)
                return FabricationClaimResult.Reject("not_ready");

            // Prepare a durable outbox entry. This is NOT an inventory add.
            // The real adapter must persist this before applying a unique
            // item to inventory, and then acknowledge it after inventory save.
            job.Status = FabricationStatus.Delivering;
            revision++;
            return new FabricationClaimResult(true, true, "delivery_prepared",
                job.OutputItemId, job.OutputType);
        }
    }

    /// <summary>
    /// Called only after the resulting inventory has been durably applied.
    /// Multiple acknowledgements for the same output are idempotent.
    /// </summary>
    public bool ConfirmDelivery(Guid jobId, uint verifiedActor, Guid outputItemId)
    {
        lock (gate)
        {
            if (verifiedActor == 0 ||
                !jobs.TryGetValue(jobId, out FabricationJob? job) ||
                job.OwnerId != verifiedActor || job.OutputItemId != outputItemId)
                return false;

            if (job.Status == FabricationStatus.Claimed)
                return true;
            if (job.Status != FabricationStatus.Delivering)
                return false;

            job.Status = FabricationStatus.Claimed;
            revision++;
            return true;
        }
    }

    public bool Cancel(Guid jobId, uint verifiedActor, DateTime utcNow)
    {
        lock (gate)
        {
            if (verifiedActor == 0 || !Utc(utcNow) ||
                !jobs.TryGetValue(jobId, out FabricationJob? job) ||
                job.OwnerId != verifiedActor)
                return false;

            AdvanceInternal(utcNow);
            if (job.Status == FabricationStatus.Cancelled)
                return true;
            if (job.Status != FabricationStatus.Running)
                return false;

            RefundIngredients(job);
            job.Status = FabricationStatus.Cancelled;
            revision++;
            return true;
        }
    }

    /// <summary>
    /// Trusted server/bootstrap-only material provision. Operation IDs are
    /// retained in the snapshot, not evicted, to prevent replay-based credits.
    /// Not exposed on the unauthenticated development UDP listener.
    /// </summary>
    public bool GrantMaterials(Guid operationId, uint verifiedActor,
                               string material, int quantity)
    {
        lock (gate)
        {
            if (operationId == Guid.Empty || verifiedActor == 0 ||
                !KeyValid(material) || quantity <= 0 ||
                quantity > MaxMaterialPerKind)
                return false;

            if (grants.TryGetValue(operationId, out FabricationGrantReceipt? previous))
                return previous.ActorId == verifiedActor &&
                       previous.Material == material && previous.Quantity == quantity;
            if (grants.Count >= MaxGrants)
                return false;

            if (!inventories.TryGetValue(verifiedActor, out FabricationInventory? stock))
            {
                if (inventories.Count >= MaxPlayers)
                    return false;
                stock = new FabricationInventory { ActorId = verifiedActor };
            }

            int old = stock.Materials.TryGetValue(material, out int count) ? count : 0;
            if (old > MaxMaterialPerKind - quantity ||
                (old == 0 && stock.Materials.Count >= MaxMaterialKinds))
                return false;

            // Outstanding reservations are still owned by this player.
            // Bound future refunds so cancellation cannot overflow inventory.
            int reserved = jobs.Values.Where(job =>
                job.OwnerId == verifiedActor && job.Status == FabricationStatus.Running)
                .Sum(job => job.ReservedIngredients.TryGetValue(material,
                    out int amount) ? (long)amount : 0L) is long sum
                    ? (int)Math.Min(sum, (long)MaxMaterialPerKind + 1) : 0;
            if ((long)old + quantity + reserved > MaxMaterialPerKind)
                return false;

            stock.Materials[material] = old + quantity;
            inventories[verifiedActor] = stock;
            grants.Add(operationId, new FabricationGrantReceipt(
                operationId, verifiedActor, material, quantity));
            revision++;
            return true;
        }
    }

    /// <summary>
    /// Atomically finalizes a prepared job and adds the stable output UUID
    /// into the same authoritative aggregate. Never split into two saves.
    /// </summary>
    public FabricationDeliveryResult DeliverToInventory(
        Guid jobId, uint verifiedActor, DateTime utcNow)
    {
        lock (gate)
        {
            if (!Utc(utcNow) || verifiedActor == 0 ||
                !jobs.TryGetValue(jobId, out FabricationJob? job) ||
                job.OwnerId != verifiedActor)
                return FabricationDeliveryResult.Reject("job_not_found");

            AdvanceInternal(utcNow);
            if (job.Status is not (FabricationStatus.Ready or
                                   FabricationStatus.Delivering or
                                   FabricationStatus.Claimed))
                return FabricationDeliveryResult.Reject("not_ready");

            if (!inventories.TryGetValue(verifiedActor, out FabricationInventory? stock))
            {
                if (inventories.Count >= MaxPlayers)
                    return FabricationDeliveryResult.Reject("player_capacity");
                stock = new FabricationInventory { ActorId = verifiedActor };
            }

            if (stock.OwnedItems.TryGetValue(job.OutputItemId, out string? existing))
            {
                if (existing != job.OutputType)
                    return FabricationDeliveryResult.Reject("item_conflict");
                if (job.Status != FabricationStatus.Claimed)
                {
                    job.Status = FabricationStatus.Claimed;
                    revision++;
                }
                return new FabricationDeliveryResult(true, true, "duplicate",
                    job.OutputItemId, job.OutputType);
            }

            if (job.Status == FabricationStatus.Claimed)
                return FabricationDeliveryResult.Reject("external_delivery");
            if (stock.OwnedItems.Count >= MaxOwnedItems)
                return FabricationDeliveryResult.Reject("inventory_full");

            // One output UUID may exist at most once across all players.
            if (inventories.Values.Any(x => x.OwnedItems.ContainsKey(job.OutputItemId)))
                return FabricationDeliveryResult.Reject("item_conflict");

            stock.OwnedItems.Add(job.OutputItemId, job.OutputType);
            inventories[verifiedActor] = stock;
            job.Status = FabricationStatus.Claimed;
            revision++;
            return new FabricationDeliveryResult(true, false, "delivered",
                job.OutputItemId, job.OutputType);
        }
    }

    public FabricationInventory? SnapshotInventory(uint actorId)
    {
        lock (gate)
            return inventories.TryGetValue(actorId, out FabricationInventory? stock)
                ? stock.Copy() : null;
    }

    private void RefundIngredients(FabricationJob job)
    {
        if (job.ReservedIngredients.Count == 0)
            return;

        FabricationInventory stock = inventories[job.OwnerId];
        foreach (var entry in job.ReservedIngredients)
        {
            int current = stock.Materials.TryGetValue(entry.Key,
                out int count) ? count : 0;
            // GrantMaterials enforces outstanding reservations when adding
            // externally provided stock, so refund cannot exceed its bounds.
            stock.Materials[entry.Key] = checked(current + entry.Value);
        }
    }

    /// <summary>Completes ready work even if all clients disconnected.</summary>
    public int Advance(DateTime utcNow)
    {
        if (!Utc(utcNow))
            return 0;
        lock (gate)
        {
            ExpireLeasesInternal(utcNow);
            return AdvanceInternal(utcNow);
        }
    }

    public int ReleaseActor(uint actorId)
    {
        lock (gate)
        {
            Guid[] keys = leases.Where(x => x.Value.ActorId == actorId)
                                .Select(x => x.Key).ToArray();
            foreach (Guid key in keys)
                leases.Remove(key);
            // Running jobs continue; the result remains claimable after
            // reconnection. Never transfer a reserved item to another user.
            return keys.Length;
        }
    }

    public FabricationDocument Snapshot()
    {
        lock (gate)
        {
            return new FabricationDocument
            {
                Version = SchemaVersion,
                Revision = revision,
                Stations = stations.Values.OrderBy(x => x.Id).Select(x => x.Copy()).ToList(),
                Jobs = jobs.Values.OrderBy(x => x.Id).Select(x => x.Copy()).ToList(),
                Inventories = inventories.Values.OrderBy(x => x.ActorId)
                    .Select(x => x.Copy()).ToList(),
                Grants = grants.Values.OrderBy(x => x.OperationId).ToList()
            };
        }
    }

    public static FabricationState Restore(FabricationDocument document,
                                           IEnumerable<FabricationRecipe> trustedRecipes)
    {
        if (document == null || document.Version != SchemaVersion ||
            document.Revision < 0 || document.Stations == null ||
            document.Jobs == null || document.Stations.Count > MaxStations ||
            document.Jobs.Count > MaxJobs ||
            document.Inventories == null || document.Inventories.Count > MaxPlayers ||
            document.Grants == null || document.Grants.Count > MaxGrants)
            throw new InvalidDataException("Invalid fabrication snapshot schema.");

        FabricationState restored = new(trustedRecipes) { revision = document.Revision };
        foreach (FabricationStation station in document.Stations)
        {
            if (station == null || station.Id == Guid.Empty ||
                station.Generation < 1 || !restored.stations.TryAdd(station.Id, station.Copy()))
                throw new InvalidDataException("Invalid or repeated fabrication station.");
        }

        foreach (FabricationInventory inventory in document.Inventories)
        {
            if (inventory == null || inventory.ActorId == 0 ||
                inventory.Materials == null || inventory.OwnedItems == null ||
                inventory.Materials.Count > MaxMaterialKinds ||
                inventory.OwnedItems.Count > MaxOwnedItems ||
                inventory.Materials.Any(x => !KeyValid(x.Key) ||
                    x.Value < 1 || x.Value > MaxMaterialPerKind) ||
                inventory.OwnedItems.Any(x => x.Key == Guid.Empty ||
                    !KeyValid(x.Value)) ||
                !restored.inventories.TryAdd(inventory.ActorId, inventory.Copy()))
                throw new InvalidDataException("Invalid fabrication inventory.");
        }

        foreach (FabricationGrantReceipt receipt in document.Grants)
        {
            if (receipt == null || receipt.OperationId == Guid.Empty ||
                receipt.ActorId == 0 || !KeyValid(receipt.Material) ||
                receipt.Quantity <= 0 || receipt.Quantity > MaxMaterialPerKind ||
                !restored.grants.TryAdd(receipt.OperationId, receipt))
                throw new InvalidDataException("Invalid or duplicated material credit receipt.");
        }

        HashSet<Guid> outputs = new();
        HashSet<Guid> runningStations = new();
        foreach (FabricationJob job in document.Jobs)
        {
            if (job == null || job.Id == Guid.Empty || job.OutputItemId == Guid.Empty ||
                job.OwnerId == 0 || job.StationGeneration < 1 ||
                !restored.stations.TryGetValue(job.StationId, out FabricationStation? station) ||
                station.Generation < job.StationGeneration ||
                (station.Generation == job.StationGeneration &&
                 station.Deleted && job.Status == FabricationStatus.Running) ||
                !restored.recipes.TryGetValue(job.RecipeId ?? "",
                                              out FabricationRecipe? recipe) ||
                recipe.OutputType != job.OutputType ||
                job.ReservedIngredients == null ||
                !SameIngredients(recipe.Ingredients, job.ReservedIngredients) ||
                !Enum.IsDefined(typeof(FabricationStatus), job.Status) ||
                !Utc(job.StartedUtc) || !Utc(job.ReadyUtc) ||
                job.ReadyUtc - job.StartedUtc != recipe.Duration ||
                (job.Status == FabricationStatus.Running &&
                 (!runningStations.Add(job.StationId) ||
                  station.Generation != job.StationGeneration)) ||
                !outputs.Add(job.OutputItemId) ||
                !restored.jobs.TryAdd(job.Id, job.Copy()))
                throw new InvalidDataException("Invalid, duplicate or conflicting craft job.");
        }

        // Inventory item IDs must match actual claimed jobs exactly, and a
        // single item UUID cannot be credited to two players after recovery.
        HashSet<Guid> itemIds = new();
        foreach (FabricationInventory stock in restored.inventories.Values)
        {
            foreach (var item in stock.OwnedItems)
            {
                if (!itemIds.Add(item.Key) ||
                    !restored.jobs.Values.Any(job =>
                        job.OwnerId == stock.ActorId &&
                        job.OutputItemId == item.Key &&
                        job.OutputType == item.Value &&
                        job.Status == FabricationStatus.Claimed))
                    throw new InvalidDataException("Fabricated item has no matching claimed job.");
            }
        }

        foreach (FabricationJob job in restored.jobs.Values)
        {
            if (job.Status != FabricationStatus.Running)
                continue;
            if (!restored.inventories.TryGetValue(job.OwnerId,
                    out FabricationInventory? stock))
            {
                if (job.ReservedIngredients.Count != 0)
                    throw new InvalidDataException("Missing inventory for reserved materials.");
                continue;
            }
            foreach (var material in job.ReservedIngredients)
            {
                int owned = stock.Materials.TryGetValue(material.Key,
                    out int count) ? count : 0;
                long total = (long)owned + restored.jobs.Values
                    .Where(x => x.OwnerId == job.OwnerId &&
                        x.Status == FabricationStatus.Running)
                    .Sum(x => x.ReservedIngredients.TryGetValue(material.Key,
                        out int n) ? (long)n : 0L);
                if (total > MaxMaterialPerKind)
                    throw new InvalidDataException("Reserved material exceeds inventory capacity.");
            }
        }

        // All ephemeral UI interaction leases are intentionally discarded.
        return restored;
    }

    private static bool SameIngredients(
        IReadOnlyDictionary<string, int> expected, Dictionary<string, int> actual)
        => actual.Count == expected.Count &&
           actual.All(x => expected.TryGetValue(x.Key, out int qty) &&
                           qty == x.Value);

    private bool StationActive(Guid stationId, int generation)
        => stations.TryGetValue(stationId, out FabricationStation? station) &&
           !station.Deleted && station.Generation == generation;

    private bool HasRunningJob(Guid stationId, int generation)
        => jobs.Values.Any(job => job.StationId == stationId &&
                                  job.StationGeneration == generation &&
                                  job.Status == FabricationStatus.Running);

    private int AdvanceInternal(DateTime utcNow)
    {
        int completed = 0;
        foreach (FabricationJob job in jobs.Values)
        {
            if (job.Status == FabricationStatus.Running && utcNow >= job.ReadyUtc)
            {
                job.Status = FabricationStatus.Ready;
                leases.Remove(job.StationId);
                revision++;
                completed++;
            }
        }
        return completed;
    }

    private void ExpireLeasesInternal(DateTime utcNow)
    {
        foreach (Guid stationId in leases
            .Where(x => x.Value.ExpiresUtc <= utcNow)
            .Select(x => x.Key).ToArray())
            leases.Remove(stationId);
    }

    private static bool Utc(DateTime value) => value.Kind == DateTimeKind.Utc;

    private static bool KeyValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
        value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.');

    private sealed record FabricationLease(uint ActorId, DateTime ExpiresUtc);
}

internal sealed record FabricationRecipe(string Id, string OutputType,
                                         TimeSpan Duration,
    IReadOnlyDictionary<string, int>? Ingredients = null)
{
    public IReadOnlyDictionary<string, int> Ingredients { get; init; } =
        Ingredients ?? new Dictionary<string, int>(StringComparer.Ordinal);
}

internal enum FabricationStatus
{
    Running = 1,
    Ready = 2,
    Claimed = 3,
    Cancelled = 4,
    Delivering = 5
}

internal sealed class FabricationStation
{
    public Guid Id { get; set; }
    public int Generation { get; set; }
    public bool Deleted { get; set; }

    public FabricationStation Copy() => new()
    {
        Id = Id, Generation = Generation, Deleted = Deleted
    };
}

internal sealed class FabricationJob
{
    public Guid Id { get; set; }
    public Guid StationId { get; set; }
    public int StationGeneration { get; set; }
    public uint OwnerId { get; set; }
    public string RecipeId { get; set; } = "";
    public string OutputType { get; set; } = "";
    public Guid OutputItemId { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime ReadyUtc { get; set; }
    public FabricationStatus Status { get; set; }
    public Dictionary<string, int> ReservedIngredients { get; set; } =
        new(StringComparer.Ordinal);

    public FabricationJob Copy() => new()
    {
        Id = Id, StationId = StationId, StationGeneration = StationGeneration,
        OwnerId = OwnerId, RecipeId = RecipeId, OutputType = OutputType,
        OutputItemId = OutputItemId, StartedUtc = StartedUtc,
        ReadyUtc = ReadyUtc, Status = Status,
        ReservedIngredients = new Dictionary<string, int>(
            ReservedIngredients, StringComparer.Ordinal)
    };
}

internal sealed class FabricationDocument
{
    public int Version { get; set; } = FabricationState.SchemaVersion;
    public long Revision { get; set; }
    public List<FabricationStation> Stations { get; set; } = new();
    public List<FabricationJob> Jobs { get; set; } = new();
    public List<FabricationInventory> Inventories { get; set; } = new();
    public List<FabricationGrantReceipt> Grants { get; set; } = new();
}

internal sealed record FabricationBeginResult(bool Started, bool Duplicate,
    string Reason, Guid JobId, Guid OutputItemId)
{
    public static FabricationBeginResult Reject(string reason)
        => new(false, false, reason, Guid.Empty, Guid.Empty);
}

internal sealed record FabricationClaimResult(bool Allowed, bool NewlyClaimed,
    string Reason, Guid OutputItemId, string OutputType)
{
    public static FabricationClaimResult Reject(string reason)
        => new(false, false, reason, Guid.Empty, "");
}

internal sealed class FabricationInventory
{
    public uint ActorId { get; set; }
    public Dictionary<string, int> Materials { get; set; } =
        new(StringComparer.Ordinal);
    public Dictionary<Guid, string> OwnedItems { get; set; } = new();

    public FabricationInventory Copy() => new()
    {
        ActorId = ActorId,
        Materials = new Dictionary<string, int>(Materials, StringComparer.Ordinal),
        OwnedItems = new Dictionary<Guid, string>(OwnedItems)
    };
}

internal sealed record FabricationGrantReceipt(
    Guid OperationId, uint ActorId, string Material, int Quantity);

internal sealed record FabricationDeliveryResult(
    bool Delivered, bool Duplicate, string Reason, Guid OutputItemId, string OutputType)
{
    internal static FabricationDeliveryResult Reject(string reason) =>
        new(false, false, reason, Guid.Empty, "");
}
