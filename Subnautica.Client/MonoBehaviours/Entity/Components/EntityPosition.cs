namespace Subnautica.Client.MonoBehaviours.Entity.Components
{
    using System.Collections.Generic;
    using System.Linq;

    using UnityEngine;

    using Subnautica.API.Extensions;
    using Subnautica.API.Features;
    using Subnautica.Client.Core;
    using Subnautica.Network.Models.Storage.World.Childrens;

    using ServerModel = Subnautica.Network.Models.Server;

    public class EntityPosition
    {
        // Only send new data when movement exceeds the visual dead-band.
        // The heartbeat ensures that stationary objects still converge.
        private const float MinPositionDeltaSquared = 0.0225f;
        private const float MinRotationDelta = 3f;
        private const float HeartbeatSeconds = 1f;

        private struct SentState
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public float Time;
        }

        private readonly Dictionary<ushort, SentState> lastSent = new Dictionary<ushort, SentState>();
        /**
         *
         * Timing nesnesini barındırır.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public StopwatchItem Timing { get; set; } = new StopwatchItem(200f);

        /**
         *
         * Konumları barındırır.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public List<WorldDynamicEntityPosition> Positions { get; set; } = new List<WorldDynamicEntityPosition>();

        /**
         *
         * Her karede tetiklenir.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public void Update()
        {
            if (this.Timing.IsFinished())
            {
                this.Timing.Restart();

                foreach (var entityId in Network.DynamicEntity.GetActivatedEntityIds())
                {
                    var entity = Network.DynamicEntity.GetEntity(entityId);
                    if (entity == null || entity.IsUsingByPlayer || !entity.IsMine(ZeroPlayer.CurrentPlayer.UniqueId) || entity.ParentId.IsNotNull())
                    {
                        continue;
                    }

                    this.EntityPositionToQueue(entity);
                }

                this.SendPositionPacketToServer();
            }
        }

        /**
         *
         * Güncellenmiş nesne konumunu kuyruğa alır.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        private void EntityPositionToQueue(WorldDynamicEntity entity)
        {
            entity.UpdateGameObject();

            if (entity.GameObject)
            {
                Vector3 position = entity.GameObject.transform.position;
                Quaternion rotation = entity.GameObject.transform.rotation;
                float now = Time.realtimeSinceStartup;

                // Maintain local authoritative state even if this snapshot is
                // below the network send threshold.
                entity.Position = position.ToZeroVector3();
                entity.Rotation = rotation.ToZeroQuaternion();

                SentState last;
                if (this.lastSent.TryGetValue(entity.Id, out last) &&
                    now - last.Time < HeartbeatSeconds &&
                    (position - last.Position).sqrMagnitude < MinPositionDeltaSquared &&
                    Quaternion.Angle(rotation, last.Rotation) < MinRotationDelta)
                {
                    return;
                }

                // Prevent indefinitely retaining state for entities that were removed.
                if (this.lastSent.Count >= 8192)
                {
                    this.lastSent.Clear();
                }

                this.lastSent[entity.Id] = new SentState()
                {
                    Position = position,
                    Rotation = rotation,
                    Time = now
                };

                this.Positions.Add(new WorldDynamicEntityPosition() {
                    Id       = entity.Id,
                    Position = entity.Position.Compress(),
                    Rotation = entity.Rotation.Compress(),
                });
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
                    ServerModel.WorldDynamicEntityPositionArgs request = new ServerModel.WorldDynamicEntityPositionArgs()
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
