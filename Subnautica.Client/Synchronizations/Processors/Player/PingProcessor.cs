namespace Subnautica.Client.Synchronizations.Processors.Player
{
    using Subnautica.API.Extensions;
    using Subnautica.API.Features;
    using Subnautica.Client.Abstracts;
    using Subnautica.Client.Core;
    using Subnautica.Client.Modules;
    using Subnautica.Client.Synchronizations.InitialSync;
    using Subnautica.Events.EventArgs;
    using Subnautica.Network.Models.Core;
    using System.Diagnostics;
    using static VFXParticlesPool;
    using ServerModel = Subnautica.Network.Models.Server;

    public class PingProcessor : NormalProcessor
    {
        /**
         *
         * Timing nesnesini barındırır.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public StopwatchItem Timing { get; set; } = new StopwatchItem(1000f);

        /**
         *
         * Zamanlama sınıfını barındırır.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public Stopwatch Ping = new Stopwatch();

        private uint nextPingSequence;
        private uint pendingPingSequence;

        /**
        *
        * Gelen veriyi işler
        *
        * @author Ismail <ismaiil_0234@hotmail.com>
        *
        */
        public override bool OnDataReceived(NetworkPacket networkPacket)
        {
            var packet = networkPacket.GetPacket<ServerModel.PingArgs>();
            if (packet.ServerTime <= 0)
            {
                return false;
            }

            // Ignore late answers to an earlier request, instead of treating them
            // as a huge RTT measurement for the current request.
            if (packet.PingSequence != 0 && packet.PingSequence != this.pendingPingSequence)
            {
                return true;
            }

            if (this.Ping.IsRunning)
            {
                this.Ping.Stop();
                long roundTripMs = this.Ping.ElapsedMilliseconds;

                // The original 250ms threshold prevented world clock sync at
                // normal 300ms RTT. Reject only pathological stale measurements.
                if (roundTripMs > 0 && roundTripMs < 2000)
                {
                    WorldProcessor.SetDayNightCycle(this.GetServerTime(packet.ServerTime, roundTripMs));
                }

                PingLatency.SetPingText(roundTripMs);
            }

            return true;
        }

        /**
         *
         * Sınıf başlatılırken tetiklenir.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public override void OnStart()
        {
            this.Ping.Reset();
            this.pendingPingSequence = 0;
            this.nextPingSequence = 0;
        }

        /**
         *
         * Her Sabit karede tetiklenir.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public override void OnFixedUpdate()
        {
            if (World.IsLoaded && this.Timing.IsFinished())
            {
                this.Timing.Restart();

                this.SendPacketToServer();
            }
            else
            {
                if (this.Ping.IsRunning)
                {
                    PingLatency.SetPingText(this.Ping.ElapsedMilliseconds);
                }
            }
        }

        /**
         *
         * Oyuncu istatistikleri alındığında tetiklenir.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        public void SendPacketToServer()
        {
            if (!NetworkClient.IsConnected())
            {
                return;
            }

            // Keep just one RTT sample outstanding. Retry after a timeout,
            // with a different sequence number so stale answers are ignored.
            if (this.Ping.IsRunning && this.Ping.ElapsedMilliseconds < 2500)
            {
                return;
            }

            this.Ping.Restart();
            this.pendingPingSequence = ++this.nextPingSequence;
            if (this.pendingPingSequence == 0)
            {
                this.pendingPingSequence = ++this.nextPingSequence;
            }

            if (!NetworkClient.SendPacket(new ServerModel.PingArgs()
            {
                PingSequence = this.pendingPingSequence
            }))
            {
                this.Ping.Stop();
            }
        }

        /**
         *
         * Ortalama sunucu zamanını hesaplar.
         *
         * @author Ismail <ismaiil_0234@hotmail.com>
         *
         */
        private double GetServerTime(double serverTime, long roundTripMs)
        {
            // The authoritative sample is stamped at the server. Estimate the
            // one-way network delay as RTT / 2, not an entire round trip.
            return serverTime + 0.04 + roundTripMs / 2000.0;
        }
    }
}
