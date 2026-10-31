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
