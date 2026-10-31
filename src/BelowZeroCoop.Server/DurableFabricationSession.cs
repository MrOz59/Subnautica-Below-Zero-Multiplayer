namespace BelowZeroCoop.Server;

/// <summary>
/// Single-process durable transaction boundary around fabrication jobs,
/// ingredient stock and delivered output instance IDs. The file is our own
/// local snapshot, never a Subnautica native save or network endpoint.
///
/// All public mutating operations persist before reporting their result.
/// On persistence failure, the in-memory state is restored from the last
/// known snapshot and any volatile UI leases are released intentionally.
/// </summary>
internal sealed class DurableFabricationSession : IDisposable
{
    private readonly object gate = new();
    private readonly string file;
    private readonly FabricationRecipe[] recipes;
    private readonly FileStream exclusiveLock;
    private FabricationState state;
    private bool disposed;

    public DurableFabricationSession(
        string snapshotPath, IEnumerable<FabricationRecipe> trustedRecipes)
    {
        if (string.IsNullOrWhiteSpace(snapshotPath))
            throw new ArgumentException("Snapshot file path is required.", nameof(snapshotPath));
        if (trustedRecipes == null)
            throw new ArgumentNullException(nameof(trustedRecipes));

        recipes = trustedRecipes.ToArray();
        file = Path.GetFullPath(snapshotPath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        // FileShare.None prevents two local processes from independently
        // mutating the same authoritative crafting inventory at the same time.
        exclusiveLock = new FileStream(file + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            state = File.Exists(file)
                ? GameplayStore.LoadFabrication(file, recipes)
                : new FabricationState(recipes);
        }
        catch
        {
            exclusiveLock.Dispose();
            throw;
        }
    }

    public FabricationDocument Snapshot()
    {
        lock (gate)
        {
            EnsureOpen();
            return state.Snapshot();
        }
    }

    public FabricationInventory? SnapshotInventory(uint verifiedActor)
    {
        lock (gate)
        {
            EnsureOpen();
            return state.SnapshotInventory(verifiedActor);
        }
    }

    public bool RegisterStation(Guid stationId, int generation) =>
        Commit(x => x.RegisterStation(stationId, generation));

    public bool DestroyStation(Guid stationId, int generation) =>
        Commit(x => x.DestroyStation(stationId, generation));

    public bool GrantMaterials(
        Guid creditId, uint verifiedActor, string material, int quantity) =>
        Commit(x => x.GrantMaterials(creditId, verifiedActor, material, quantity));

    public FabricationBeginResult Begin(
        Guid jobId, Guid stationId, int generation, uint verifiedActor,
        string recipeId, DateTime utcNow) =>
        Commit(x => x.Begin(jobId, stationId, generation,
                            verifiedActor, recipeId, utcNow));

    public FabricationClaimResult Claim(
        Guid jobId, uint verifiedActor, DateTime utcNow) =>
        Commit(x => x.Claim(jobId, verifiedActor, utcNow));

    /// <summary>
    /// Atomically adds one output to our embedded item ledger and marks
    /// the job complete in the SAME saved document.
    /// </summary>
    public FabricationDeliveryResult DeliverToInventory(
        Guid jobId, uint verifiedActor, DateTime utcNow) =>
        Commit(x => x.DeliverToInventory(jobId, verifiedActor, utcNow));

    public bool Cancel(Guid jobId, uint verifiedActor, DateTime utcNow) =>
        Commit(x => x.Cancel(jobId, verifiedActor, utcNow));

    public int Advance(DateTime utcNow) =>
        Commit(x => x.Advance(utcNow));

    // UI leases are intentionally not persisted and never reused on restart.
    public bool TryOpen(Guid stationId, int generation, uint verifiedActor,
                        DateTime utcNow, TimeSpan leaseTime)
    {
        lock (gate)
        {
            EnsureOpen();
            return state.TryOpen(stationId, generation, verifiedActor, utcNow, leaseTime);
        }
    }

    public bool TryClose(Guid stationId, uint verifiedActor)
    {
        lock (gate)
        {
            EnsureOpen();
            return state.TryClose(stationId, verifiedActor);
        }
    }

    public int ReleaseActor(uint verifiedActor)
    {
        lock (gate)
        {
            EnsureOpen();
            return state.ReleaseActor(verifiedActor);
        }
    }

    private T Commit<T>(Func<FabricationState, T> apply)
    {
        lock (gate)
        {
            EnsureOpen();
            FabricationDocument before = state.Snapshot();
            try
            {
                T result = apply(state);
                // Persist ALL state transitions, including cleanup during a
                // rejected operation. Don't reply before the file is visible.
                GameplayStore.SaveFabrication(file, state);
                return result;
            }
            catch
            {
                // Reset volatile UI leases; callers must reopen the station
                // after any storage error before attempting a new craft.
                state = FabricationState.Restore(before, recipes);
                throw;
            }
        }
    }

    private void EnsureOpen()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(DurableFabricationSession));
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
