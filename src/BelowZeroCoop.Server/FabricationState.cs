namespace BelowZeroCoop.Server;

/// <summary>
/// Server-owned fabricator lifecycle for regression prevention (#3, #76).
/// This is a standalone model: no network endpoint accepts these commands.
/// Caller identity and recipe authority MUST be verified by a future game adapter.
/// </summary>
internal sealed class FabricationState
{
    public const int SchemaVersion = 1;
    public const int MaxStations = 512;
    public const int MaxJobs = 4096;
    private static readonly TimeSpan MaxLease = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxCraftTime = TimeSpan.FromMinutes(2);

    private readonly object gate = new();
    private readonly Dictionary<Guid, FabricationStation> stations = new();
    private readonly Dictionary<Guid, FabricationJob> jobs = new();
    private readonly Dictionary<Guid, FabricationLease> leases = new();
    private readonly Dictionary<string, FabricationRecipe> recipes;
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
                !recipes.TryAdd(recipe.Id, recipe))
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

            FabricationJob job = new()
            {
                Id = jobId,
                StationId = stationId,
                StationGeneration = generation,
                OwnerId = verifiedActor,
                RecipeId = recipe.Id,
                OutputType = recipe.OutputType,
                OutputItemId = Guid.NewGuid(),
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
            if (job.Status != FabricationStatus.Ready)
                return FabricationClaimResult.Reject("not_ready");

            job.Status = FabricationStatus.Claimed;
            revision++;
            return new FabricationClaimResult(true, true, "claimed",
                job.OutputItemId, job.OutputType);
        }
    }

    public bool Cancel(Guid jobId, uint verifiedActor)
    {
        lock (gate)
        {
            if (verifiedActor == 0 || !jobs.TryGetValue(jobId, out FabricationJob? job) ||
                job.OwnerId != verifiedActor)
                return false;

            if (job.Status == FabricationStatus.Cancelled)
                return true;
            if (job.Status != FabricationStatus.Running)
                return false;

            job.Status = FabricationStatus.Cancelled;
            revision++;
            return true;
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
                Jobs = jobs.Values.OrderBy(x => x.Id).Select(x => x.Copy()).ToList()
            };
        }
    }

    public static FabricationState Restore(FabricationDocument document,
                                           IEnumerable<FabricationRecipe> trustedRecipes)
    {
        if (document == null || document.Version != SchemaVersion ||
            document.Revision < 0 || document.Stations == null ||
            document.Jobs == null || document.Stations.Count > MaxStations ||
            document.Jobs.Count > MaxJobs)
            throw new InvalidDataException("Invalid fabrication snapshot schema.");

        FabricationState restored = new(trustedRecipes) { revision = document.Revision };
        foreach (FabricationStation station in document.Stations)
        {
            if (station == null || station.Id == Guid.Empty ||
                station.Generation < 1 || !restored.stations.TryAdd(station.Id, station.Copy()))
                throw new InvalidDataException("Invalid or repeated fabrication station.");
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

        // All ephemeral UI interaction leases are intentionally discarded.
        return restored;
    }

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
                                         TimeSpan Duration);

internal enum FabricationStatus { Running = 1, Ready = 2, Claimed = 3, Cancelled = 4 }

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

    public FabricationJob Copy() => new()
    {
        Id = Id, StationId = StationId, StationGeneration = StationGeneration,
        OwnerId = OwnerId, RecipeId = RecipeId, OutputType = OutputType,
        OutputItemId = OutputItemId, StartedUtc = StartedUtc,
        ReadyUtc = ReadyUtc, Status = Status
    };
}

internal sealed class FabricationDocument
{
    public int Version { get; set; } = FabricationState.SchemaVersion;
    public long Revision { get; set; }
    public List<FabricationStation> Stations { get; set; } = new();
    public List<FabricationJob> Jobs { get; set; } = new();
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
