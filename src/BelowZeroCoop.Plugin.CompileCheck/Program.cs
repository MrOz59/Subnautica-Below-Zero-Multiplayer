using BelowZeroCoop.Plugin;
using BelowZeroCoop.Protocol;

static void Require(bool condition, string message)
{
    if (!condition)
        throw new Exception("Fake-Unity test failed: " + message);
}

// These are compile-check stubs only. We do NOT claim they reproduce Unity.
Player.main = new Player();
var preview = new MovementPreview();
var output = new List<MovementDatagram>();
bool Publish(MovementDatagram sample)
{
    output.Add(sample);
    return true;
}

preview.Tick(0.0f, Publish);
preview.Tick(0.01f, Publish);
preview.Tick(0.06f, Publish);
Require(output.Count == 1, "stationary player does not flood network");
Player.main.transform.position = new UnityEngine.Vector3(1f, 2f, 3f);
preview.Tick(0.12f, Publish);
Require(output.Count == 2, "moving player publishes another packet");
Require(output[0].Sequence == 1 && output[1].Sequence == 2,
        "outbound movement sequence is monotonic");
Require(output.All(packet => packet.PeerId == 0), "client never chooses its own peer ID");
preview.Reset();
preview.Tick(0.21f, Publish);
Require(output.Count == 3 && output[2].Sequence == 3,
        "network reset must not reuse a recent sequence number");

// Simulate peer creation and removal without running any real game methods.
int destroyedBefore = UnityEngine.Object.DestroyedCount;
int createdBefore = UnityEngine.GameObject.CreatedCount;
preview.OnPosition(new MovementDatagram(1, 5, 0.10, 3f, 0f, 4f), 0.25f);
preview.OnPosition(new MovementDatagram(1, 5, 0.10, 3f, 0f, 4f), 0.25f);
preview.OnPosition(new MovementDatagram(2, 5, 0.15, 3.2f, 0f, 4f), 0.30f);
Require(UnityEngine.GameObject.CreatedCount == createdBefore + 1,
        "repeated movement for one peer never creates duplicate avatars");
preview.Tick(0.31f, Publish);
preview.OnPeerLeft(5);
Require(UnityEngine.Object.DestroyedCount == destroyedBefore + 1,
        "PeerLeft immediately destroys the temporary remote capsule");
preview.OnPeerLeft(5);
Require(UnityEngine.Object.DestroyedCount == destroyedBefore + 1,
        "Repeated PeerLeft is idempotent");
preview.Tick(10.5f, Publish);
preview.Reset();

// Original #64: merely loading our BepInEx plugin must not alter
// or contact anything while the opt-in network preview is disabled.
var plugin = new BelowZeroCoop.Plugin.Plugin();
const System.Reflection.BindingFlags privateInstance =
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
var awake = plugin.GetType().GetMethod("Awake", privateInstance);
var update = plugin.GetType().GetMethod("Update", privateInstance);
Require(awake != null && update != null, "plugin lifecycle callbacks exist");
int visualsBeforeSingleplayer = UnityEngine.GameObject.CreatedCount;
awake.Invoke(plugin, null);
update.Invoke(plugin, null);
Require(UnityEngine.GameObject.CreatedCount == visualsBeforeSingleplayer,
        "disabled plugin must not spawn remote objects in singleplayer");

// Hook discovery is read-only. It cannot patch or invoke game methods.
var hooks = GameHookCatalog.Probe(
    new[] { typeof(GameHookProbeFixture).Assembly },
    new[]
    {
        new GameHookCatalog.Target("Test", "GameHookProbeFixture",
            "Present", "After", "exactly one method"),
        new GameHookCatalog.Target("Test", "GameHookProbeFixture",
            "Overloaded", "Before", "must disambiguate overloads"),
        new GameHookCatalog.Target("Test", "GameHookProbeFixture",
            "NotPresent", "Before", "unknown method"),
        new GameHookCatalog.Target("Test", "MissingGameType",
            "Anything", "Before", "unknown type")
    });
Require(hooks.Count == 4 && hooks[0].Status == GameHookCatalog.ProbeStatus.Found &&
        hooks[1].Status == GameHookCatalog.ProbeStatus.Ambiguous &&
        hooks[2].Status == GameHookCatalog.ProbeStatus.MethodMissing &&
        hooks[3].Status == GameHookCatalog.ProbeStatus.TypeMissing,
        "hook diagnostics distinguish found, overloaded and missing game methods");
Require(GameHookCatalog.Targets.Count >= 30,
        "hook catalog covers core world, inventory and construction targets");
Require(GameHookProbeFixture.InvokedCount == 0,
        "reflection-only diagnostics must not invoke game methods");

// Passive Harmony observers: fake dispatch models callback bookkeeping only,
// not Unity/Harmony injection or the real game method signatures.
Require(HarmonyLib.HarmonyFakeDispatch.PatchCount == 0,
        "disabled singleplayer plugin must not install Harmony observers");
var gameAssembly = typeof(Constructable).Assembly;
using (var observer = new BuildingHookObserver())
{
    int installed = observer.Install(gameAssembly,
        BuildingHookObserver.DefaultTargets(), null);
    Require(installed == 2 && HarmonyLib.HarmonyFakeDispatch.PatchCount == 2,
        "two independent postfixes installed only when explicitly requested");

    int before = Constructable.Executions;
    Require(new Constructable().Construct() && Constructable.Executions == before + 1,
        "observing does not suppress original game construction");
    before = BaseGhost.Executions;
    new BaseGhost().Finish();
    Require(BaseGhost.Executions == before + 1,
        "observing base finish leaves original method active");

    var observations = observer.Drain(16);
    Require(observations.Count == 2 &&
            observations[0].EventName == "construct_method_returned" &&
            observations[1].EventName == "base_ghost_finish_returned",
            "both observations captured without invoking world or network writes");
    Require(observer.Drain(16).Count == 0, "drained records cannot replay");

    var building = new Constructable { techType = "BaseWall", constructedAmount = 0.3f };
    building.gameObject.SetComponent(new UniqueIdentifier { Id = "original-game-id-100" });
    building.gameObject.transform.position = new UnityEngine.Vector3(12, 3, -8);
    building.gameObject.transform.localPosition = new UnityEngine.Vector3(1, 2, 3);
    building.gameObject.transform.rotation = new UnityEngine.Quaternion(0, 0, 0, 1);
    building.gameObject.transform.localRotation = new UnityEngine.Quaternion(0, 0, 0, 1);

    Require(building.Construct(), "partial construction still runs normally");
    var partial = observer.Drain(16).Single();
    Require(partial.Game.ObservedObjectId == "original-game-id-100" &&
            partial.Game.TechType == "BaseWall" &&
            partial.Game.WorldTransform != null &&
            partial.Game.WorldTransform.X == 12 &&
            partial.Game.LocalTransform.Z == 3 &&
            partial.Game.CompletionEvidence == "partial_progress" &&
            partial.Game.Origin == "unknown",
            "partial progress has identity/transform but is never labeled as player completion");

    building.constructedAmount = 1f;
    Require(building.Construct(), "complete construction still runs normally");
    var complete = observer.Drain(16).Single();
    Require(complete.Game.ObservedObjectId == partial.Game.ObservedObjectId &&
            complete.Game.CompletionEvidence == "complete_candidate" &&
            complete.Game.ConstructedAmount == 1d,
            "same game ID and transformed metadata on candidate completion");

    Require(building.Construct(), "repeated vanilla construction still executes");
    Require(observer.Drain(16).Count == 0 &&
            observer.SuppressedDuplicateCount == 1,
            "identical rapid method returns coalesce only diagnostic records");
    building.constructedAmount = 0.98f;
    Require(building.Construct() && observer.Drain(16).Count == 1,
            "real progress change is never swallowed by telemetry coalescing");
    building.constructedAmount = 1f;
    building.gameObject.transform.position = new UnityEngine.Vector3(13, 3, -8);
    Require(building.Construct(), "changed position still executes");
    Require(observer.Drain(16).Single().Game.WorldTransform.X == 13,
            "changed transforms are not mistaken for duplicated telemetry");

    using (ConstructionObservationContext.Suppress(
        ConstructionObservationContext.SuppressionReason.SaveRestore))
    {
        Require(ConstructionObservationContext.IsSuppressed,
            "save loading boundary suppresses diagnostic observation");
        building.Construct();
        using (ConstructionObservationContext.Suppress(
            ConstructionObservationContext.SuppressionReason.RemoteReplay))
        {
            ghost.Finish();
            Require(ConstructionObservationContext.IsSuppressed,
                "nested replay boundary remains suppressed");
        }
        Require(ConstructionObservationContext.IsSuppressed,
            "disposing nested replay leaves enclosing save scope active");
        Require(observer.Drain(16).Count == 0,
            "no callbacks emitted during scoped save load or remote replay");
    }
    Require(!ConstructionObservationContext.IsSuppressed,
        "scope disposal restores callback visibility");
    Require(building.Construct() && observer.Drain(16).Count == 1,
        "normal observations resume once all suppression scopes exit");

    var parent = new FakeBaseOwner();
    parent.gameObject.SetComponent(new UniqueIdentifier { Id = "base-owner-500" });
    var ghost = new BaseGhost { TargetBase = parent };
    ghost.Finish();
    var ghostObservation = observer.Drain(16).Single();
    Require(ghostObservation.Game.TargetBaseId == "base-owner-500" &&
            ghostObservation.Game.ObservedObjectId == null &&
            ghostObservation.Game.CompletionEvidence == "finish_returned_candidate",
            "parent base ID is distinct from nonexistent final-piece ID");

    var piece = new ConstructableBase();
    piece.gameObject.SetComponent(new UniqueIdentifier { Id = "piece-uid-900" });
    piece.gameObject.transform.position = new UnityEngine.Vector3(88, 17, -44);
    ghost.ParentPiece = piece;
    ghost.gameObject.SetComponent(new UniqueIdentifier { Id = "temporary-ghost-id" });
    ghost.Finish();
    var placed = observer.Drain(16).Single();
    Require(placed.Game.ObservedObjectId == "piece-uid-900" &&
            placed.Game.ObjectIdSource == "constructable_base_parent_uid" &&
            placed.Game.TargetBaseId == "base-owner-500" &&
            placed.Game.WorldTransform.X == 88 &&
            placed.Game.Origin == "unknown" &&
            placed.Game.CompletionEvidence == "finish_returned_candidate",
            "placed-piece candidate uses enclosing construction ID, never temporary ghost ID");
    ghost.ParentPiece = null;
    ghost.Finish();
    Require(observer.Drain(16).Single().Game.ObservedObjectId == null,
            "missing parent-piece identity does not fall back to misleading ghost UID");

    var invalidIdentity = new Constructable { techType = "BaseWall", constructedAmount = 1f };
    invalidIdentity.gameObject.SetComponent(new UniqueIdentifier { Id = "bad\\nidentity" });
    invalidIdentity.gameObject.transform.position =
        new UnityEngine.Vector3(float.NaN, 0, 0);
    invalidIdentity.Construct();
    var unsafeObservation = observer.Drain(16).Single();
    Require(unsafeObservation.Game.ObservedObjectId == null &&
            unsafeObservation.Game.WorldTransform == null,
            "malformed ID or nonfinite game geometry is rejected for persistence");

    for (int i = 0; i < BuildingHookObserver.MaxPending + 10; ++i)
        new Constructable().Construct();
    Require(observer.DroppedCount == 10,
        "bounded observer drops oldest records rather than allocating forever");
    Require(observer.Drain(512).Count == 32,
        "drain is limited to a strict main-thread frame budget");
}
Require(HarmonyLib.HarmonyFakeDispatch.PatchCount == 0,
        "plugin shutdown unregisters only its Harmony callbacks");
new Constructable().Construct();
Require(HarmonyLib.HarmonyFakeDispatch.PatchCount == 0,
        "vanilla construction remains callable after observer cleanup");

// Other mods' Harmony owner IDs must survive our own cleanup.
var foreignMethod = typeof(Constructable).GetMethod("Construct");
var foreignOwner = new HarmonyLib.Harmony("test.foreign.harmony.owner");
foreignOwner.Patch(foreignMethod, postfix: new HarmonyLib.HarmonyMethod(
    typeof(GameHookProbeFixture).GetMethod("ForeignPostfix")));
Require(HarmonyLib.HarmonyFakeDispatch.PatchCount == 1,
        "foreign owner installed its own independent postfix");
using (var local = new BuildingHookObserver())
{
    Require(local.Install(gameAssembly, BuildingHookObserver.DefaultTargets(), null) == 2,
            "our passive observers install alongside unrelated mod patches");
    Require(HarmonyLib.HarmonyFakeDispatch.PatchCount == 3,
            "foreign patch remains visible while our two hooks are enabled");
}
Require(HarmonyLib.HarmonyFakeDispatch.PatchCount == 1,
        "unpatch removes only BelowZeroCoop Harmony owner, not another plugin");
int previousForeignCalls = GameHookProbeFixture.ForeignPostfixCount;
new Constructable().Construct();
Require(GameHookProbeFixture.ForeignPostfixCount == previousForeignCalls + 1,
        "foreign owner's postfix still executes after our observer is stopped");
foreignOwner.Unpatch(foreignMethod, HarmonyLib.HarmonyPatchType.Postfix,
    "test.foreign.harmony.owner");
Require(HarmonyLib.HarmonyFakeDispatch.PatchCount == 0,
        "test-only foreign owner cleans up separately");

using (var ambiguous = new BuildingHookObserver())
{
    int installed = ambiguous.Install(gameAssembly, new[]
    {
        new BuildingHookObserver.Candidate("AmbiguousConstructable",
            "Construct", "unsupported", typeof(BuildingHookObserver).GetMethod(
                "AfterConstruct",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static)),
        new BuildingHookObserver.Candidate("MissingConstructionClass",
            "Finish", "unavailable", typeof(BuildingHookObserver).GetMethod(
                "AfterBaseFinish",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static))
    }, null);
    Require(installed == 0 && HarmonyLib.HarmonyFakeDispatch.PatchCount == 0,
        "missing/ambiguous hooks skipped instead of guessing overloads");
}

Console.WriteLine("Plugin syntax, disabled-singleplayer and fake-Unity lifecycle tests passed.");
