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

Console.WriteLine("Plugin syntax, disabled-singleplayer and fake-Unity lifecycle tests passed.");
