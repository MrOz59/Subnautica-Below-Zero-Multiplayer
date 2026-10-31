using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace BelowZeroCoop.Plugin
{
    /// <summary>
    /// Independent read-only diagnostics of game API entry points.
    /// Names describe methods in the game's Assembly-CSharp and are facts
    /// learned from research, not code copied from any third-party mod.
    /// This class installs NO Harmony patches and changes NO gameplay.
    /// </summary>
    internal static class GameHookCatalog
    {
        internal enum ProbeStatus
        {
            Found,
            TypeMissing,
            MethodMissing,
            Ambiguous,
            InspectionFailed
        }

        internal sealed class Target
        {
            internal readonly string Area;
            internal readonly string TypeName;
            internal readonly string MethodName;
            internal readonly string IntendedPhase;
            internal readonly string Note;

            internal Target(string area, string typeName, string methodName,
                string intendedPhase, string note)
            {
                Area = area;
                TypeName = typeName;
                MethodName = methodName;
                IntendedPhase = intendedPhase;
                Note = note;
            }
        }

        internal sealed class Result
        {
            internal readonly Target Target;
            internal readonly ProbeStatus Status;
            internal readonly string Detail;

            internal Result(Target target, ProbeStatus status, string detail)
            {
                Target = target;
                Status = status;
                Detail = detail;
            }
        }

        // Publicly identifiable game class/method names found in the original
        // source patch declarations. Target signatures and callback timing
        // still require validation with a legitimately installed game build.
        internal static readonly IReadOnlyList<Target> Targets = new[]
        {
            new Target("Construction", "Constructable", "Construct",
                "After", "Furniture build completion; verify final state"),
            new Target("Construction", "BaseGhost", "Finish",
                "After", "Base piece placement completion"),
            new Target("Construction", "Constructable", "ProgressDeconstruction",
                "Coroutine", "Deconstruction progress; coroutine wrapper needed"),
            new Target("Construction", "ConstructableBase", "ProgressDeconstruction",
                "Coroutine", "Base deconstruction; coroutine wrapper needed"),
            new Target("Construction", "Constructable", "DeconstructAsync",
                "After", "Partial deconstruction amount"),
            new Target("Construction", "BaseDeconstructable", "Deconstruct",
                "Before", "Removal request and validation"),
            new Target("Construction", "BuilderTool", "HandleInput",
                "Before", "Furniture deconstruction interaction"),
            new Target("Construction", "Builder", "TryPlace",
                "Before", "Attempt to place building ghost"),
            new Target("Construction", "Builder", "Update",
                "After", "Moving building ghost; potentially per frame"),

            new Target("Fabricator", "GhostCrafter", "Craft",
                "Before", "Craft requested; never suppress game original by default"),
            new Target("Fabricator", "GhostCrafter", "OnCraftingEnd",
                "After", "Craft ending; verify output lifecycle"),
            new Target("Fabricator", "GhostCrafter", "OnHandClick",
                "Before", "Opening machine or claiming output"),
            new Target("Fabricator", "uGUI_CraftingMenu", "OnDeselect",
                "After", "Fabricator menu closing"),

            new Target("Power", "PowerRelay", "AddInboundPower",
                "Before", "Source connection becoming active"),
            new Target("Power", "PowerRelay", "RemoveInboundPower",
                "Before", "Source connection being disconnected"),

            new Target("Inventory", "Inventory", "OnAddItem",
                "After", "Inventory item inserted"),
            new Target("Inventory", "Inventory", "OnRemoveItem",
                "After", "Inventory item removed"),
            new Target("Inventory", "QuickSlots", "Bind",
                "After", "Quickslot binds to item instance"),
            new Target("Inventory", "QuickSlots", "Unbind",
                "After", "Quickslot unbound"),
            new Target("Inventory", "Equipment", "NotifyEquip",
                "After", "Equipped item changed"),
            new Target("Inventory", "Equipment", "NotifyUnequip",
                "After", "Equipment item removed"),

            new Target("World", "EntityCell", "AwakeAsync",
                "After", "Cell becoming loaded"),
            new Target("World", "EntityCell", "SleepAsync",
                "Before", "Cell becoming unloaded"),
            new Target("World", "LargeWorldStreamer", "LoadGlobalRootAsync",
                "Before", "Initial world load; may return coroutine"),
            new Target("World", "ProtobufSerializer", "DeserializeObjectsAsync",
                "Coroutine", "Entity loading/deserializing; overload resolution critical"),
            new Target("World", "PrefabPlaceholder", "Spawn",
                "Before", "Placeholder entity spawning"),
            new Target("World", "EntitySlotsPlaceholder", "Spawn",
                "Before", "Slot-based entity spawning"),
            new Target("World", "EntitySlot", "SpawnVirtualEntities",
                "Before", "Virtual entities inside streaming cells"),

            new Target("Lifecycle", "IngameMenu", "QuitGame",
                "Before", "Exit to desktop or main menu"),
            new Target("Lifecycle", "UWE.GameApplication", "OnApplicationQuitting",
                "Before", "Application shutdown"),
            new Target("Lifecycle", "Player", "ResetPlayerOnDeath",
                "After", "Respawn event, not necessarily first spawn"),
            new Target("Lifecycle", "ArmsController", "Update",
                "After", "Per-frame player update"),

            new Target("Vehicles", "SeaTruckConnection", "SetConnectedTo",
                "Before", "Seatruck segment connection"),
            new Target("Vehicles", "SeaTruckSegment", "OnClickDetachLever",
                "Before", "Seatruck segment detach request"),
            new Target("Vehicles", "SeaTruckSegment", "Start",
                "Before", "Seatruck segment initialization"),
            new Target("Vehicles", "Player", "EnterInterior",
                "Before", "Player enters interior/vehicle")
        };

        /// <summary>
        /// Probe classes/methods by reflection WITHOUT binding to game
        /// assemblies, invoking any method, or installing patches.
        /// Multiple overloads are reported rather than guessed.
        /// </summary>
        internal static IReadOnlyList<Result> Probe(IEnumerable<Assembly> assemblies,
            IEnumerable<Target> targets)
        {
            if (assemblies == null)
                throw new ArgumentNullException("assemblies");
            if (targets == null)
                throw new ArgumentNullException("targets");

            Assembly[] loaded = assemblies.Where(a => a != null).Distinct().ToArray();
            var output = new List<Result>();
            foreach (Target target in targets)
            {
                if (target == null)
                    continue;

                try
                {
                    Type gameType = null;
                    foreach (Assembly assembly in loaded)
                    {
                        gameType = assembly.GetType(target.TypeName, false);
                        if (gameType != null)
                            break;
                    }

                    if (gameType == null)
                    {
                        output.Add(new Result(target, ProbeStatus.TypeMissing,
                            "Type not loaded"));
                        continue;
                    }

                    MethodInfo[] matches = gameType.GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Instance | BindingFlags.Static)
                        .Where(m => m.Name == target.MethodName).ToArray();

                    if (matches.Length == 0)
                    {
                        output.Add(new Result(target, ProbeStatus.MethodMissing,
                            "Class available, method absent"));
                    }
                    else if (matches.Length > 1)
                    {
                        output.Add(new Result(target, ProbeStatus.Ambiguous,
                            matches.Length + " overloads; do not auto-patch"));
                    }
                    else
                    {
                        MethodInfo method = matches[0];
                        string parameters = string.Join(", ",
                            method.GetParameters().Select(p => p.ParameterType.Name));
                        output.Add(new Result(target, ProbeStatus.Found,
                            method.ReturnType.Name + " (" + parameters + ")"));
                    }
                }
                catch (Exception ex)
                {
                    // Diagnostics must not break startup or singleplayer,
                    // even if a game assembly fails to load reflection types.
                    output.Add(new Result(target, ProbeStatus.InspectionFailed,
                        ex.GetType().Name));
                }
            }
            return output;
        }
    }
}
