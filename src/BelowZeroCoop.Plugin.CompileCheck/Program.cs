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

preview.Tick(0.0f, sample => output.Add(sample));
preview.Tick(0.01f, sample => output.Add(sample));
preview.Tick(0.06f, sample => output.Add(sample));
Require(output.Count == 2, "publisher respects a 20Hz budget");
Require(output[0].Sequence == 1 && output[1].Sequence == 2,
        "outbound movement sequence is monotonic");
Require(output.All(packet => packet.PeerId == 0), "client never chooses its own peer ID");
preview.Reset();
preview.Tick(0.11f, sample => output.Add(sample));
Require(output.Count == 3 && output[2].Sequence == 3,
        "network reset must not reuse a recent sequence number");

// Simulate peer creation and removal without running any real game methods.
int destroyedBefore = UnityEngine.Object.DestroyedCount;
preview.OnPosition(new MovementDatagram(1, 5, 0.10, 3f, 0f, 4f), 0.25f);
preview.Tick(0.26f, sample => output.Add(sample));
preview.OnPeerLeft(5);
Require(UnityEngine.Object.DestroyedCount == destroyedBefore + 1,
        "PeerLeft immediately destroys the temporary remote capsule");
preview.OnPeerLeft(5);
Require(UnityEngine.Object.DestroyedCount == destroyedBefore + 1,
        "Repeated PeerLeft is idempotent");
preview.Tick(10.5f, sample => output.Add(sample));
preview.Reset();

Console.WriteLine("Plugin syntax and fake-Unity preview lifecycle checks passed.");
