namespace Subnautica.Client.MonoBehaviours.Creature
{
    using System.Collections.Generic;
    using System.Linq;

    using Subnautica.API.Extensions;
    using Subnautica.API.Features;
    using Subnautica.API.Features.Creatures;
    using Subnautica.Client.Core;

    using Subnautica.Network.Models.Server;

    using UnityEngine;

    using ServerModel = Subnautica.Network.Models.Server;

    public class CreatureWatcher : MonoBehaviour
    {
        private const float MinCreatureDeltaSquared = 0.01f;
        private const float MinCreatureRotationDelta = 4f;

        private struct SentState
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public float Time;
        }

        private readonly Dictionary<ushort, SentState> lastCreatureSent = new Dictionary<ushort, SentState>();
        /**
         *
         * Timing nesnesini barındırır.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public StopwatchItem Timing { get; set; } = new StopwatchItem(BroadcastInterval.CreaturePosition);

        /**
         *
         * IsNormalTrigger nesnesini barındırır.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public bool IsNormalTrigger { get; set; } = false;

        /**
         *
         * Mesafeyi barındırır.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public List<WorldCreaturePosition> Positions { get; set; } = new List<WorldCreaturePosition>();

        /**
         *
         * Her karede tetiklenir.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public void Update()
        {
            if (World.IsLoaded)
            {
                if (this.Timing.IsFinished())
                {
                    this.Timing.Restart();
                    this.IsNormalTrigger = !this.IsNormalTrigger;

                    foreach (var creature in Network.Creatures.GetActiveCreatures())
                    {
                        if (creature.IsMine())
                        {
                            if (creature.Data.IsFastSyncActivated)
                            {
                                this.AddPositionToQueue(creature.Id, creature.GetCreatureObject());
                                continue;
                            }
                            
                            if (this.IsNormalTrigger)
                            {
                                this.AddPositionToQueue(creature.Id, creature.GetCreatureObject());
                                continue;
                            }
                        }
                    }

                    this.SendPositionPacketToServer();
                }

                foreach (var creature in Network.Creatures.GetActiveCreatures())
                {
                    if (creature.IsMine() == false)
                    {
                        creature.GetCreatureObject()?.Movement.SimpleMoveV2();
                    }
                }
            }
        }

        /**
         *
         * Güncellenmiş nesne konumunu kuyruğa alır.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public void AddPositionToQueue(ushort creatureId, MultiplayerCreature creature)
        {
            if (creature != null && creature.IsActive)
            {
                if (creature.GameObject)
                {
                    Vector3 position = creature.GameObject.transform.position;
                    Quaternion rotation = creature.GameObject.transform.rotation;
                    float now = Time.realtimeSinceStartup;
                    float heartbeat = creature.CreatureItem.Data.IsFastSyncActivated ? 0.5f : 1f;

                    SentState last;
                    if (this.lastCreatureSent.TryGetValue(creatureId, out last) &&
                        now - last.Time < heartbeat &&
                        (position - last.Position).sqrMagnitude < MinCreatureDeltaSquared &&
                        Quaternion.Angle(rotation, last.Rotation) < MinCreatureRotationDelta)
                    {
                        return;
                    }

                    if (this.lastCreatureSent.Count >= 8192)
                    {
                        this.lastCreatureSent.Clear();
                    }

                    this.lastCreatureSent[creatureId] = new SentState()
                    {
                        Position = position,
                        Rotation = rotation,
                        Time = now
                    };

                    this.Positions.Add(new WorldCreaturePosition()
                    {
                        CreatureId = creatureId,
                        Position   = position.Compress(),
                        Rotation   = rotation.Compress(),
                    });
                }
                else
                {
                    Log.Info("NULL => " + creatureId + ", TYPE => " + creature.CreatureItem.TechType);
                }
            }
        }

        /**
         *
         * Konum verilerini sunucuya gönderir.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public void SendPositionPacketToServer()
        {
            if (this.Positions.Count > 0)
            {
                foreach (var positions in this.Positions.Split(21))
                {
                    ServerModel.WorldCreaturePositionArgs request = new ServerModel.WorldCreaturePositionArgs()
                    {
                        Positions = positions.ToList(),
                    };

                    NetworkClient.SendPacket(request);
                }

                this.Positions.Clear();
            }
        }
    }
}

