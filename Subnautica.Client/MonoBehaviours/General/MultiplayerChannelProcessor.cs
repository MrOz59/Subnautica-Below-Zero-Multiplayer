namespace Subnautica.Client.MonoBehaviours.General
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Collections.Concurrent;

    using Subnautica.API.Enums;
    using Subnautica.API.Extensions;
    using Subnautica.API.Features;
    using Subnautica.Client.Abstracts;
    using Subnautica.Client.MonoBehaviours.Creature;
    using Subnautica.Client.MonoBehaviours.Entity;
    using Subnautica.Network.Models.Core;

    using UnityEngine;

    using UWE;

    public class MultiplayerChannelProcessor : MonoBehaviour
    {
        /**
         *
         * Player.Update Kilidi
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public SynchronizedPacketQueue Packets { get; set; } = new SynchronizedPacketQueue();

        /**
         *
         * Verilerin alınacağı network kanalı.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public NetworkChannel ChannelId { get; set; }

        /**
         *
         * Veri işleme aktiflik durumu
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public bool IsActive { get; set; } = false;

        /**
         *
         * Asenkron bir işlem mi?
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        private bool IsAsyncProcessor { get; set; } = false;

        /**
         *
         * Asenkron durumu
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public bool IsAsyncConsuming { get; set; } = false;

        /**
         *
         * Sahneler arası silinmemesi için
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public static ConcurrentDictionary<NetworkChannel, MultiplayerChannelProcessor> Processors { get; set; } = new ConcurrentDictionary<NetworkChannel, MultiplayerChannelProcessor>();

        /**
         *
         * Komponent eklendiğinde çalışır.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public void Start()
        {
            if (!Processors.TryGetValue(this.ChannelId, out var instance))
            {
                Processors.TryAdd(this.ChannelId, this);
                DontDestroyOnLoad(gameObject);
            }
            else
            {
                Destroy(gameObject);
            }
        }

        /**
         *
         * Update tetiklendikten sonra çalışır.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public void Update()
        {
            if (!this.IsActive)
            {
                return;
            }

            if (this.IsAsyncProcessor)
            {
                if (!this.IsAsyncConsuming && this.Packets.Count > 0)
                {
                    CoroutineHost.StartCoroutine(this.AsyncConsumeQueue());
                }
            }
            else
            {
                int budget = this.ChannelId == NetworkChannel.PlayerMovement ||
                             this.ChannelId == NetworkChannel.VehicleMovement ||
                             this.ChannelId == NetworkChannel.EntityMovement ||
                             this.ChannelId == NetworkChannel.FishMovement ? 64 : 128;

                NetworkPacket packet;
                while (budget-- > 0 && this.Packets.TryDequeue(out packet))
                {
                    try
                    {
                        if (packet != null)
                        {
                            if (ProcessorShared.Processors.TryGetValue(packet.Type, out NormalProcessor processor))
                            {
                                processor.SetFinished(true);
                                processor.OnDataReceived(packet);
                            }
                            else
                            {
                                Log.Error($"Processor Not Found: " + packet.Type);
                            }
                        }
                        else
                        {
                            Log.Error($"Packet Not Found: " + this.ChannelId);
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error($"MultiplayerDataReceive Exception, ChannelId: {this.ChannelId}, Error Message: {e}");
                    }
                }
            }
        }

        /**
         *
         * Asenkron tüketimini başlatır.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public IEnumerator AsyncConsumeQueue()
        {
            this.IsAsyncConsuming = true;

            NetworkPacket packet;
            while (this.Packets.TryDequeue(out packet))
            {
                if (packet != null)
                {
                    if (ProcessorShared.Processors.TryGetValue(packet.Type, out NormalProcessor processor))
                    {
                        try
                        {
                            processor.SetFinished(true);
                            processor.OnDataReceived(packet);
                        }
                        catch (Exception e)
                        {
                            processor.SetFinished(true);
                            Log.Error($"AsyncConsumeQueue Exception, ChannelId: {this.ChannelId}, Error Message: {e}");
                        }

                        while (!processor.IsFinished())
                        {
                            yield return CoroutineUtils.waitForNextFrame;
                        }

                        if (processor.IsWaitingForNextFrame())
                        {
                            yield return CoroutineUtils.waitForNextFrame;
                        }
                    }
                    else
                    {
                        Log.Error($"AsyncConsumeQueue -> Processor Not Found: {packet.Type}");
                    }
                }
                else
                {
                    Log.Error($"AsyncConsumeQueue -> Packet Not Found: {this.ChannelId}");
                }
            }

            this.IsAsyncConsuming = false;
        }

        /**
         *
         * Veri tüketmeyi aktif/pasif yapar.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public void SetEnabled(bool isActive)
        {
            this.IsActive = isActive;
        }

        /**
         *
         * Veri kanalını değiştirir.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public void SetChannel(NetworkChannel channelId)
        {
            this.ChannelId = channelId;
        }

        /**
         *
         * Asenkron durumunu değiştirir.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public void SetAsyncEnabled(bool isAsyncProcessor)
        {
            this.IsAsyncProcessor = isAsyncProcessor;
        }

        /**
         *
         * Packet ekler.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public void AddPacket(NetworkPacket packet)
        {
            this.Packets.Enqueue(packet);
        }

        /**
         *
         * Tüm paketleri temizler.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public void ClearPackets()
        {
            this.Packets.Clear();
        }

        /**
         *
         * Sınıf yok edilirken tetiklenir.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public void OnDestroy()
        {
            if (Processors.TryGetValue(this.ChannelId, out var current) && ReferenceEquals(current, this))
            {
                Processors.TryRemove(this.ChannelId, out var removed);
            }
        }

        /**
         *
         * Packet'i işlemciye ekler.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public static void AddPacketToProcessor(NetworkChannel channelId, NetworkPacket packet)
        {
            if (Processors.TryGetValue(channelId, out var processor))
            {
                processor.AddPacket(packet);
            }
            else
            {
                Log.Error(string.Format("[MultiplayerChannelProcessor.AddPacketToProcessor] Channel Not Found: {0}", channelId));
            }
        }

        /**
         *
         * Oyuncuya işlemcileri ekler.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public static bool AddToPlayerMultiplayerProcessors()
        {
            MultiplayerChannelProcessor.Processors.Clear();

            ZeroPlayer.DisposeAll();

            var player = new ZeroPlayer("Server Host && Client", true);
            player.PlayerObject.AddComponent<ConnectionSignals>();
            player.PlayerObject.AddComponent<MultiplayerEntityTracker>();
            player.PlayerObject.AddComponent<CreatureWatcher>();

            ProcessorBehaviour processorBehaviour = player.PlayerObject.AddComponent<ProcessorBehaviour>();

            AddProcessorToBehaviour(processorBehaviour, ProcessorShared.Processors.Values);
            AddProcessorToBehaviour(processorBehaviour, ProcessorShared.MetadataProcessors.Values);
            AddProcessorToBehaviour(processorBehaviour, ProcessorShared.PlayerItemProcessors.Values);
            AddProcessorToBehaviour(processorBehaviour, ProcessorShared.WorldEntityProcessors.Values);
            AddProcessorToBehaviour(processorBehaviour, ProcessorShared.WorldCreatureProcessors.Values);
            AddProcessorToBehaviour(processorBehaviour, ProcessorShared.WorldDynamicEntityProcessors.Values);

            foreach (NetworkChannel channelId in Enum.GetValues(typeof(NetworkChannel)))
            {
                var channelProcessor = ZeroPlayer.CurrentPlayer.PlayerObject.AddComponent<MultiplayerChannelProcessor>();
                channelProcessor.SetChannel(channelId);

                if (channelId == NetworkChannel.Startup)
                {
                    channelProcessor.SetEnabled(true);
                }
            }

            return true;
        }

        /**
         *
         * Oyuncuya işlemcileri ekler.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public static void AddProcessorToBehaviour(ProcessorBehaviour behaviour, IEnumerable<BaseProcessor> processors)
        {
            foreach (var processor in processors)
            {
                processor.OnStart();

                var processorType = processor.GetType();
                if (processorType.GetMethod("OnUpdate").IsOverride())
                {
                    behaviour.AddUpdateProcessor(processor);
                }

                if (processorType.GetMethod("OnLateUpdate").IsOverride())
                {
                    behaviour.AddLateUpdateProcessor(processor);
                }

                if (processorType.GetMethod("OnFixedUpdate").IsOverride())
                {
                    behaviour.AddFixedUpdateProcessor(processor);
                }

                if (processorType.GetMethod("OnDispose").IsOverride())
                {
                    behaviour.AddDisposeProcessor(processor);
                }
            }
        }
    }
}