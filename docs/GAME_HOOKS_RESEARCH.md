# Below Zero game hook research (independent clean-room implementation)

**Research scope:** public class/method names declared in the original
[Subnautica.Events patch directory](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/tree/main/Subnautica.Events/Patches/Events)
and a few related fixes. These are **game API identifiers and interoperability
facts**. We do not import, compile, embed or reproduce BOT Benson's source
files, event layer, transpilers, assets or protocols. The original repository
has an all-rights-reserved [LICENCE.md](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/LICENCE.md).

**Status:** Verified against declarations in the public original mod source,
not yet against a legitimate locally installed game's
`Assembly-CSharp.dll`. Names and overloads may have changed. The
off-by-default passive observers exist, but have **not been verified in the
real game**.

## First integration surface: construction and removal

| Game class | Game method | Original patch observation | Proposed independent purpose |
| --- | --- | --- | --- |
| `Constructable` | `Construct` | Postfix | Capture furniture construction completion and stable entity identity |
| `BaseGhost` | `Finish` | Postfix | Capture base piece completion; resolve parent and prefab transform |
| `Constructable` | `ProgressDeconstruction` | Coroutine wrapper | Confirm finished furniture removal, not just an initial interaction |
| `ConstructableBase` | `ProgressDeconstruction` | Coroutine wrapper | Confirm completed base-piece removal |
| `Constructable` | `DeconstructAsync` | Postfix | Track partial completion rather than assuming removal |
| `BaseDeconstructable` | `Deconstruct` | Prefix | Validate removal request before accepting any world update |
| `BuilderTool` | `HandleInput` | Prefix | Detect initiated furniture deconstruction |
| `Builder` | `TryPlace` | Prefix | Placement precondition, position and collision validation |
| `Builder` | `Update` | Postfix | Moving ghost; high-frequency, do not network each frame |

Primary patch references:
[ConstructionCompleted.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/Building/ConstructionCompleted.cs),
[ConstructionRemoved.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/Building/ConstructionRemoved.cs),
[ConstructionAmountChanged.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/Building/ConstructionAmountChanged.cs),
[DeconstructionBegin.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/Building/DeconstructionBegin.cs),
[FurnitureDeconstructionBegin.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/Building/FurnitureDeconstructionBegin.cs),
[ConstructionGhostTryPlacing.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/Building/ConstructionGhostTryPlacing.cs).

**Crucial implementation distinction:** a `Prefix` on
`BaseDeconstructable.Deconstruct` is an *attempt*, not proof the object has
been successfully removed. The upstream removal patches wrap coroutine
progress; for our independent implementation, capture a stable ID at the
request, confirm actual state after completion, then submit an idempotent
world transaction. Never generate a delete event unconditionally on the
first `Deconstruct` call. Do not tie world replication to every ghost move.

## Fabricator lifecycle (#3, #76)

| Game class | Game method | Original patch observation | Proposed purpose |
| --- | --- | --- | --- |
| `GhostCrafter` | `Craft` | Prefix | Validate start, source, player, ingredients and recipe |
| `GhostCrafter` | `OnCraftingEnd` | Prefix | Completion transition; must distinguish ready from delivered |
| `GhostCrafter` | `OnHandClick` | Prefix | Opening the machine versus retrieving an output item |
| `uGUI_CraftingMenu` | `OnDeselect` | Prefix | Dismiss UI lease and clear current interaction |

Original declarations:
[CrafterBegin.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/Furnitures/CrafterBegin.cs),
[CrafterOpening.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/Furnitures/CrafterOpening.cs),
[CrafterClosed.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/Furnitures/CrafterClosed.cs).

**Safety:** The upstream module sometimes returns `false` from Harmony
prefixes to suppress the base game's method. This is dangerous if copied
without full state semantics. Our first game hooks should be passive observers
and **must not suppress vanilla crafting or UI**. A future actual crafting
adapter needs a proven dedicated session, authority and save transaction.

## Inventory, quickslots and power (#78, #81, #82)

| Game class | Game method | Upstream patch phase | Potential purpose |
| --- | --- | --- | --- |
| `Inventory` | `OnAddItem` | Prefix | Discover item instance metadata and owner |
| `Inventory` | `OnRemoveItem` | Prefix | Detect item withdrawal; wait for completed removal before commit |
| `QuickSlots` | `Bind` | Postfix | Observe actual slot mapping after game mutation |
| `QuickSlots` | `Unbind` | Postfix | Observe cleared mapping |
| `Equipment` | `NotifyEquip` | Postfix | Equipped instances, reactor fuel and charger attachments |
| `Equipment` | `NotifyUnequip` | Postfix | Equipment detachment and power source removal |
| `PowerRelay` | `AddInboundPower` | Prefix | New connected supply interface |
| `PowerRelay` | `RemoveInboundPower` | Prefix | Inbound energy connection removed |

Original declarations:
[ItemAdded.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/Inventory/ItemAdded.cs),
[ItemRemoved.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/Inventory/ItemRemoved.cs),
[QuickSlotBinded.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/Inventory/QuickSlotBinded.cs),
[QuickSlotUnbinded.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/Inventory/QuickSlotUnbinded.cs),
[EquipmentEquiped.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/Inventory/EquipmentEquiped.cs),
[EquipmentUnequiped.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/Inventory/EquipmentUnequiped.cs),
[PowerSourceAdding.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/Game/PowerSourceAdding.cs),
[PowerSourceRemoving.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/Game/PowerSourceRemoving.cs).

`PowerRelay` connection events are not equivalent to the amount of energy
generated or consumed. Treat them as *topology notifications*, then query the
host's actual power data to synchronize state. Use item UUIDs, not item
labels, for quickslot replication.

## Streaming, entity loading and world save lifecycle (#5, #17, #85)

| Game class | Method | Original observation | Proposed purpose |
| --- | --- | --- | --- |
| `LargeWorldStreamer` | `LoadGlobalRootAsync` | Coroutine wrapper | World load/reconciliation boundary |
| `EntityCell` | `AwakeAsync` | Postfix | Cell loading (may precede completion of async contents) |
| `EntityCell` | `SleepAsync` | Prefix | Cell unload request; release local game objects without deleting persistent state |
| `ProtobufSerializer` | `DeserializeObjectsAsync` | Explicit overload coroutine | Saved object creation; stable identity during load |
| `PrefabPlaceholder` | `Spawn` | Prefix | Placeholder spawning |
| `EntitySlotsPlaceholder` | `Spawn` | Prefix | Slot-based spawn |
| `EntitySlot` | `SpawnVirtualEntities` | Prefix | Virtual world entity creation |

Original declarations:
[WorldLoading.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/Game/WorldLoading.cs),
[CellLoading.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/World/CellLoading.cs),
[CellUnLoading.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/World/CellUnLoading.cs),
[EntitySpawning.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/World/EntitySpawning.cs),
[EntitySlotSpawning.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/World/EntitySlotSpawning.cs).

The `ProtobufSerializer.DeserializeObjectsAsync` patch explicitly names
a many-parameter overload in the original source. A naïve patch by method
name alone can select the wrong overload. Its actual method signature must be
validated from the installed game assemblies.

**Cell unload is not object demolition.** Do not send entity tombstones on
`EntityCell.SleepAsync`; only unload ephemeral scene objects. Permanent
world state remains authoritative and should be restored on cell re-entry.

## Session lifecycle, player state and vehicles

| Game class | Method | Observation |
| --- | --- | --- |
| `IngameMenu` | `QuitGame` | Session teardown before menu/desktop exit |
| `UWE.GameApplication` | `OnApplicationQuitting` | Application shutdown |
| `Player` | `ResetPlayerOnDeath` | Respawn hook, **not** a general initial player spawn |
| `ArmsController` | `Update` | Frame-level player action; throttle and separate gameplay events |
| `Player` | `EnterInterior` | Transition to base/vehicle coordinate frame |
| `SeaTruckConnection` | `SetConnectedTo` | Module attachment |
| `SeaTruckSegment` | `OnClickDetachLever` | Module detach initiation |
| `SeaTruckSegment` | `Start` | Segment initialization |

References:
[QuittingToMainMenu.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/Game/QuittingToMainMenu.cs),
[Quitting.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/Game/Quitting.cs),
[Spawned.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/Player/Spawned.cs),
[Updated.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/Player/Updated.cs),
[EnteredInterior.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/Player/EnteredInterior.cs),
[SeaTruckConnecting.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/Vehicle/SeaTruckConnecting.cs),
[SeaTruckDetaching.cs](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/blob/main/Subnautica.Events/Patches/Events/Vehicle/SeaTruckDetaching.cs).

## BelowZeroCoop: game-hook discovery added

`src/BelowZeroCoop.Plugin/GameHookCatalog.cs` is our own **read-only**
reflection diagnostic. Enable explicitly in BepInEx config:

```ini
[Development]
ScanGameHooksOnStartup = true
```

Restart the game, then inspect its BepInEx log for `[HookScan]`.

- `Found`: the exact type and a single named method were located;
  the log includes its return and parameter types.
- `Ambiguous`: multiple method overloads exist; do not auto-patch.
- `MethodMissing`: type exists but not that method.
- `TypeMissing`: no matching loaded type.
- `InspectionFailed`: reflection failed; no game action attempted.

**No Harmony patches, method invocations or network packets are installed
or sent by this diagnostic.** It is independently disabled by default,
does not enable the UDP probe and does not affect singleplayer. The
CompileCheck project covers exact, ambiguous and missing method cases.

## Next implementation order

1. Verify `Assembly-CSharp.dll` method signatures from the installed game,
   including async/coroutine return types and overloaded methods.
2. **Implemented (candidate-only):** opt-in, non-mutating Harmony Postfix
   observers for `Constructable.Construct` and `BaseGhost.Finish`.
   The installed game signatures still need validation; no teardown-complete
   observers or save/replication writes are enabled yet.
3. Emit local, main-thread, immutable capture records; verify player
   versus world/remote origin and deduplicate by stable entity ID.
4. Integrate a trusted, authenticated event dispatcher to
   `IntegratedWorldSession`; game state must be authoritative and
   save/restored without feedback loops.
5. Add Fabricator/UI and energy observers after builder lifecycle is
   validated, then test actual game/Proton integration.

Do not copy the original Harmony patch bodies: the game-facing target names
are enough to write a separate implementation with clear source boundaries.

## Passive construction hooks implemented (experimental)

`src/BelowZeroCoop.Plugin/BuildingHookObserver.cs` now has an independent
Harmony implementation. It is completely disabled by default.

To inspect a legitimately installed copy of Below Zero, the plugin config can
enable **both** the separate read-only signature scan and the independent
passive observer:

```ini
[Development]
ScanGameHooksOnStartup = true
ObserveBuildingHooks = true
```

Both options default to `false`; neither enables the development UDP client.
The observer looks for only the loaded `Assembly-CSharp`, and installs
Harmony **Postfix-only** methods on uniquely resolved, nonabstract instance
methods `Constructable.Construct` and `BaseGhost.Finish`. If a method is
missing, ambiguous or fails patch installation, that method is skipped and
reported. There is no `Prefix`, no return-value modification and no method
suppression.

Captured records include the method name, observed UTC time, instance type
and a **process-local volatile token**. This token is *not* a stable world
object identity; the observer cannot yet create a `WorldState` entity or
persist a building. They are queued in a fixed-size buffer (128 entries)
with old-event drop counting and a maximum of 8 log entries per Unity frame.
Logging uses `[BuildObserve]` in the BepInEx output.

**Construction nuance:** `Constructable.Construct` may execute for partial
building progress, so the observation is called
`construct_method_returned`, not “construction completed.”
Similarly, `BaseGhost.Finish` is treated as a method-return observation,
not unconditional proof that world persistence succeeded.

On plugin destruction the observer releases only Postfix patches belonging
to its own Harmony owner ID. Existing patches from other mods are not
globally removed. A fake-Harmony CI suite confirms that methods still run
unmodified, observer buffers are bounded, missing/overloaded targets are
skipped, and other owners remain patched after cleanup.

This implementation **has not been run against real game binaries** yet.
The fake Unity tests do not guarantee Mono/BepInEx/Harmony compatibility
with a particular Below Zero version. A production plugin build requires
the genuine `0Harmony.dll` from the user's BepInEx 5 installation; do not
bundle game or third-party binaries in the repository.

Future steps: inspect version-specific class/method signatures, extract
stable game entity IDs and transforms via verified game APIs, distinguish
local actions from replayed remote events and completed construction from
partial progress, then feed *authenticated* server events into the
`IntegratedWorldSession`. Until then this is **local instrumentation only**.

## Candidate persistent identity and transform probe

The optional Harmony observers now take an immutable diagnostic snapshot at
the exact time the observed method returns, rather than holding onto a Unity
`GameObject` reference that may disappear on a later frame.

`src/BelowZeroCoop.Plugin/ConstructionGameSnapshotProbe.cs` independently
inspects public game API facts:

- `UniqueIdentifier.Id` obtained through `GameObject.GetComponent(Type)`
  is a **game-owned string**. This is not automatically a `.NET Guid` and
  we do **not** generate a new random GUID when the original is missing.
- `Constructable.techType` supplies a candidate object type.
- `Constructable.constructedAmount` is only evidence of progress.
  Values below 0.999 are `partial_progress`; higher values are merely
  `complete_candidate`, not proof of durable world commitment.
- `transform.position`, `transform.rotation`, `localPosition` and
  `localRotation` are copied as finite numeric values and validated
  against bounded ranges. These are observed transforms, **not verified
  base-grid coordinates**.
- `BaseGhost.Finish`: the ghost's own ID is intentionally never used
  as a permanent piece ID. We instead look for an enclosing
  `ConstructableBase` component and use its `UniqueIdentifier.Id`
  as a **candidate**. The owner base's ID, when present through
  `TargetBase`, is a **separate** field.
- All observations retain `origin=unknown`, because the same game
  callbacks can run during save loading, scripted building, or eventual
  replication. Neither loading events nor local player actions have been
  reliably distinguished yet.

The source of an ID is logged as `direct_component_uid`,
`constructable_base_parent_uid` or `unavailable`.
Empty, oversized or invalid control-character IDs and invalid/nonfinite
transforms are withheld. A missing identifier is never replaced with an
ephemeral process hash: `volatileInstance` is **diagnostic only**.

No captured observation is yet sent to the multiplayer server, inserted
into `WorldState`, or written into a native or custom save. Before
enabling any world writes, we must correlate the genuine game runtime
with the stable instance ID and distinguish actual player construction
from ghost placement, cell load and replayed remote objects.

CI currently proves these behaviors using fake Unity/game metadata,
including missing identities, partial progress, bad coordinates,
temporary ghost IDs, parent base IDs and valid candidate piece IDs.
It **cannot** prove that all real game builds expose exactly these
members. The scanner and observers simply skip unavailable data.

## Host-side game UID mapping (standalone)

The server now contains `GameEntityIdentityMap`, an append-only mapping of
verified raw game IDs to stable, scoped internal GUIDs. It is stored
transactionally inside `IntegratedWorldSession` (schema v3). The host's
`PlaceObserved` route rejects unknown origin, save loading and remote
replay. See [INTEGRATED_WORLD.md](INTEGRATED_WORLD.md).

**No Unity event is yet marked as `VerifiedLocalBuild`**, and the
read-only diagnostic hooks do not send or execute world mutations. The
server's explicit provenance enum is a future integration boundary, not
an automatic detection mechanism. Verifying real game event origin still
requires gameplay testing and an authenticated server event pipeline.
