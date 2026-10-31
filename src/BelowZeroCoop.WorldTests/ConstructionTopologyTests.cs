using BelowZeroCoop.Server;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ConstructionTopologyTests
{
    private static readonly ConstructionBounds EmptyPlayerVolume =
        new(1000, 1000, 1000, 0.5, 1, 0.5);
    private static readonly IReadOnlyCollection<ConstructionBounds> NoPlayers =
        Array.Empty<ConstructionBounds>();

    private static ConstructionBounds At(double x, double y = 0, double z = 0,
        double radius = 1) => new(x, y, z, radius, radius, radius);

    private static void Check(bool yes, string name)
    {
        if (!yes)
            throw new Exception("Construction regression failed: " + name);
    }

    private static ConstructionResult Place(ConstructionState state, Guid id,
        string kind, double x, int gen = 1)
        => state.Place(Guid.NewGuid(), state.Snapshot().Revision,
            id, gen, kind, At(x), NoPlayers);

    private static ConstructionResult Link(ConstructionState state,
        Guid a, Guid b, ConstructionLinkKind kind)
        => state.Connect(Guid.NewGuid(), state.Snapshot().Revision,
            a, 1, b, 1, kind);

    public static void Run()
    {
        TransmitterNeverBlocksUnrelatedBuildings();
        DomeHasExplicitStructuralDependency();
        NoCollisionsWithPlayersOrOccupiedBase();
        CascadeIsPreviewableAndAtomic();
        DeterministicReplayAndGenerationRecovery();
        ConcurrentRemovalAndLinkCannotRace();
        DamagedTopologyIsRejected();
        Console.WriteLine("Construction removal regressions (#6/#10/#12/#79/#80) passed.");
    }

    private static void TransmitterNeverBlocksUnrelatedBuildings()
    {
        var world = new ConstructionState();
        Guid room = Guid.NewGuid(), transmitter = Guid.NewGuid(), solar = Guid.NewGuid();
        Check(Place(world, room, "Room", 0).Applied, "place room");
        Check(Place(world, transmitter, "PowerTransmitter", 10).Applied,
              "place transmitter");
        Check(Place(world, solar, "SolarPanel", 20).Applied, "place solar panel");

        Check(Link(world, room, transmitter, ConstructionLinkKind.Power).Applied,
              "attach transmitter to room");
        Check(Link(world, transmitter, solar, ConstructionLinkKind.Power).Applied,
              "attach transmitter to solar");

        long revision = world.Snapshot().Revision;
        ConstructionRemovalPlan preview = world.PreviewRemove(
            revision, room, 1, false, NoPlayers);
        Check(preview.Allowed && preview.EntityIds.Count == 1 &&
              preview.DetachedLinks == 1,
              "power transmitter alone cannot block removal of unrelated room");

        Guid operationId = Guid.NewGuid();
        var removed = world.Remove(operationId, revision, room, 1, false, NoPlayers);
        Check(removed.Applied && removed.RemovedCount == 1 &&
              removed.DetachedCount == 1, "remove room and detach only its power edge");
        Check(world.Snapshot().Links.Count == 1,
              "transmitter-to-solar wiring remains intact");
        Check(world.Snapshot().Nodes.Single(x => x.Id == transmitter).Deleted == false,
              "transmitter survives removing neighboring room");
        Check(world.Snapshot().Nodes.Single(x => x.Id == solar).Deleted == false,
              "solar panel survives removing neighboring room");

        Check(world.Remove(operationId, revision, room, 1, false, NoPlayers).Duplicate,
              "duplicate Remove returns original receipt");
        Check(!world.Remove(operationId, revision, solar, 1, false, NoPlayers).Applied,
              "reused operation ID with different target rejected");

        Check(world.Remove(Guid.NewGuid(), world.Snapshot().Revision,
            transmitter, 1, false, NoPlayers).Applied,
              "transmitter itself is removable with power link attached");
        Check(world.Snapshot().Links.Count == 0,
              "all removed transmitter edges cleaned up, without dangling references");
    }

    private static void DomeHasExplicitStructuralDependency()
    {
        var world = new ConstructionState();
        Guid dome = Guid.NewGuid(), reactor = Guid.NewGuid(), cable = Guid.NewGuid();
        Place(world, dome, "GlassDome", 0);
        Place(world, reactor, "NuclearReactor", 5);
        Place(world, cable, "PowerTransmitter", 10);
        Check(Link(world, reactor, dome, ConstructionLinkKind.Structural).Applied,
              "reactor explicitly depends on dome");
        Check(Link(world, cable, reactor, ConstructionLinkKind.Power).Applied,
              "cable connected to reactor");

        var notYet = world.PreviewRemove(world.Snapshot().Revision,
            dome, 1, false, NoPlayers);
        Check(!notYet.Allowed && notYet.Reason == "structural_dependents",
              "dome blocked by real reactor dependency, not by a power edge");

        Check(world.Remove(Guid.NewGuid(), world.Snapshot().Revision,
            reactor, 1, false, NoPlayers).Applied, "remove reactor");
        Check(world.Remove(Guid.NewGuid(), world.Snapshot().Revision,
            dome, 1, false, NoPlayers).Applied,
              "after removing the reactor, dome is removable again");
        Check(world.Snapshot().Nodes.Single(x => x.Id == cable).Deleted == false,
              "soft power link does not destroy transmitter");
        Check(world.Snapshot().Links.Count == 0, "no dangling edge after teardown");
    }

    private static void NoCollisionsWithPlayersOrOccupiedBase()
    {
        var world = new ConstructionState();
        Guid id = Guid.NewGuid();
        var player = new[] { At(0, 0, 0, 0.5) };
        Check(!world.Place(Guid.NewGuid(), 0, id, 1, "Room", At(0), player).Applied,
              "construction cannot be placed on an existing player (#12)");
        Check(world.Place(Guid.NewGuid(), 0, id, 1, "Room", At(0), NoPlayers).Applied,
              "valid placement is allowed");
        long revision = world.Snapshot().Revision;
        var occupancy = world.PreviewRemove(revision, id, 1, false, player);
        Check(!occupancy.Allowed && occupancy.Reason == "occupied_volume",
              "occupied completed base cannot be removed (#10)");
        Check(!world.Remove(Guid.NewGuid(), revision, id, 1, false, player).Applied,
              "occupied base deletion rejected, no side effects");
        Check(world.Snapshot().Revision == revision,
              "invalid deletion does not change global revision");
        Check(world.Snapshot().Nodes.Single(x => x.Id == id).Deleted == false,
              "player-safe build remains in place");
        Check(world.Remove(Guid.NewGuid(), revision, id, 1, false, NoPlayers).Applied,
              "empty base becomes removable");
    }

    private static void CascadeIsPreviewableAndAtomic()
    {
        var world = new ConstructionState();
        Guid room = Guid.NewGuid(), dome = Guid.NewGuid(), reactor = Guid.NewGuid();
        Place(world, room, "Room", 0);
        Place(world, dome, "GlassDome", 10);
        Place(world, reactor, "NuclearReactor", 20);
        Check(Link(world, dome, room, ConstructionLinkKind.Structural).Applied,
              "dome depends on room");
        Check(Link(world, reactor, dome, ConstructionLinkKind.Structural).Applied,
              "reactor depends on dome");

        long revision = world.Snapshot().Revision;
        var plan = world.PreviewRemove(revision, room, 1, true, NoPlayers);
        Check(plan.Allowed && plan.EntityIds.SequenceEqual(
              new[] { reactor, dome, room }) && plan.DetachedLinks == 2,
              "cascade plan removes structural children first");

        Check(!world.Remove(Guid.NewGuid(), revision,
            room, 1, true, new[] { At(20) }).Applied,
            "a player in any descendant cancels entire cascade");
        Check(world.Snapshot().Revision == revision &&
              world.Snapshot().Nodes.All(x => !x.Deleted),
              "failed cascade is atomic and keeps every node");

        var committed = world.Remove(Guid.NewGuid(), revision,
            room, 1, true, NoPlayers);
        Check(committed.Applied && committed.RemovedCount == 3 &&
              committed.DetachedCount == 2, "explicit cascade atomically removes subtree");
        Check(world.Snapshot().Nodes.All(x => x.Deleted) &&
              world.Snapshot().Links.Count == 0,
              "no half-removed structure or orphan links");
    }

    private static void DeterministicReplayAndGenerationRecovery()
    {
        var world = new ConstructionState();
        Guid id = Guid.NewGuid();
        Guid op = Guid.NewGuid();
        var position = At(10);
        var build = world.Place(op, 0, id, 1, "PowerTransmitter", position, NoPlayers);
        Check(build.Applied, "first transmitter built");
        Check(world.Place(op, 0, id, 1, "PowerTransmitter", position, NoPlayers).Duplicate,
              "duplicate build never creates a second instance");
        Check(!world.Place(op, 0, id, 1, "GlassDome", position, NoPlayers).Applied,
              "duplicate command ID with different metadata rejected");
        Check(world.Remove(Guid.NewGuid(), world.Snapshot().Revision,
            id, 1, false, NoPlayers).Applied, "remove first generation");

        string dir = Path.Combine(Path.GetTempPath(), "bzc-construction-" +
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "construction.json");
        try
        {
            GameplayStore.SaveConstruction(path, world);
            var recovered = GameplayStore.LoadConstruction(path);
            Check(!recovered.Place(Guid.NewGuid(), recovered.Snapshot().Revision,
                id, 1, "PowerTransmitter", position, NoPlayers).Applied,
                "old generation cannot revive after restart");
            Check(recovered.Place(Guid.NewGuid(), recovered.Snapshot().Revision,
                id, 2, "PowerTransmitter", position, NoPlayers).Applied,
                "rebuild with correct new generation succeeds");
            Check(recovered.Snapshot().Nodes.Single().Generation == 2,
                "generation advanced exactly once");
            GameplayStore.SaveConstruction(path, recovered);
            Check(File.Exists(path + ".bak"), "previous complete construction snapshot backed up");
            var snapshot = GameplayStore.LoadConstruction(path);
            Check(snapshot.Snapshot().Nodes.Single().Generation == 2 &&
                  !snapshot.Snapshot().Nodes.Single().Deleted,
                  "construction survives two disk restarts");

            File.WriteAllText(path, "{}");
            bool invalid = false;
            try { GameplayStore.LoadConstruction(path); }
            catch (InvalidDataException) { invalid = true; }
            Check(invalid, "incomplete save cannot reset builds silently");
            Check(GameplayStore.LoadConstruction(path + ".bak").Snapshot()
                .Nodes.Single().Deleted, "explicit old backup has first-generation tombstone");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private static void ConcurrentRemovalAndLinkCannotRace()
    {
        var world = new ConstructionState();
        Guid dome = Guid.NewGuid(), reactor = Guid.NewGuid();
        Place(world, dome, "GlassDome", 0);
        Place(world, reactor, "NuclearReactor", 10);

        long before = world.Snapshot().Revision;
        Check(world.PreviewRemove(before, dome, 1, false, NoPlayers).Allowed,
              "initial dome removal plan allowed");
        Check(Link(world, reactor, dome, ConstructionLinkKind.Structural).Applied,
              "concurrent structural dependency created");
        Check(!world.Remove(Guid.NewGuid(), before,
            dome, 1, false, NoPlayers).Applied,
            "stale removal preview cannot win against newly connected reactor");
        Check(world.Snapshot().Nodes.Single(x => x.Id == dome).Deleted == false,
              "dome remains live");

        // A deterministic simultaneous race: exactly one conflicting global
        // revision can be committed; no dangling link or partial deletion.
        var second = new ConstructionState();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        Place(second, a, "Room", 0);
        Place(second, b, "PowerTransmitter", 10);
        long clock = second.Snapshot().Revision;
        var tasks = new[]
        {
            Task.Run(() => second.Remove(Guid.NewGuid(), clock,
                a, 1, false, NoPlayers).Applied),
            Task.Run(() => second.Connect(Guid.NewGuid(), clock,
                a, 1, b, 1, ConstructionLinkKind.Power).Applied)
        };
        Task.WaitAll(tasks);
        Check(tasks.Count(task => task.Result) == 1,
              "only one concurrent topology mutation with same revision can commit");
        ConstructionState.Restore(second.Snapshot());
    }

    private static void DamagedTopologyIsRejected()
    {
        var world = new ConstructionState();
        Guid room = Guid.NewGuid(), dome = Guid.NewGuid();
        Place(world, room, "Room", 0);
        Place(world, dome, "GlassDome", 10);
        Check(Link(world, dome, room, ConstructionLinkKind.Structural).Applied,
              "normal structural connection");
        Check(!Link(world, room, dome, ConstructionLinkKind.Structural).Applied,
              "structural support cycles rejected");

        var invalid = world.Snapshot();
        invalid.Links.Add(invalid.Links[0].Copy());
        bool refused = false;
        try { ConstructionState.Restore(invalid); }
        catch (InvalidDataException) { refused = true; }
        Check(refused, "duplicate links rejected during restore");

        invalid = world.Snapshot();
        invalid.Links[0].B = Guid.NewGuid();
        refused = false;
        try { ConstructionState.Restore(invalid); }
        catch (InvalidDataException) { refused = true; }
        Check(refused, "dangling link never silently loaded");

        var badBounds = new ConstructionBounds(0, 0, 0,
            double.NaN, 1, 1);
        Check(!world.Place(Guid.NewGuid(), world.Snapshot().Revision,
            Guid.NewGuid(), 1, "Room", badBounds, NoPlayers).Applied,
            "nonfinite geometry rejected before world mutation");

        long r = world.Snapshot().Revision;
        Check(!world.Remove(Guid.NewGuid(), r, room, 1, false,
            new[] { new ConstructionBounds(0, 0, 0, double.PositiveInfinity, 1, 1) }).Applied,
            "malformed authoritative occupancy cannot bypass checks");
    }
}
