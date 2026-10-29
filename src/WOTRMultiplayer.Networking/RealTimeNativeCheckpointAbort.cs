using System;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.Networking
{
    // Bounded, immutable abort retransmission. A receipt never verifies a world
    // and an exhausted wait must not be interpreted as permission to resume.
    public sealed class RealTimeNativeCheckpointAbort
    {
        public const int MaximumAttempts = 30;
        public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(150);
        private readonly long _peer;
        private readonly NotifyRealTimeNativeCheckpoint _header;
        private readonly DateTime _deadline;
        private DateTime _next;
        private int _attempts;
        public bool IsAcknowledged { get; private set; }

        public RealTimeNativeCheckpointAbort(long peer, NotifyRealTimeNativeCheckpoint source, DateTime started)
        {
            if (!RealTimeNativeCheckpointLimits.IsPrepare(source) && !RealTimeNativeCheckpointLimits.IsManifest(source))
                throw new ArgumentException("native-abort-source-invalid", nameof(source));
            _peer = peer;
            _header = RealTimeNativeCheckpointLimits.Copy(source);
            _header.Stage = (int)RealTimeNativeCheckpointStage.Abort;
            // Native work may still be draining for much longer than a packet
            // retry. Keep a finite load-sized window, never renew it on replay.
            _deadline = started + MaximumWait;
            _next = started;
        }

        public NotifyRealTimeNativeCheckpoint GetDue(DateTime now)
        {
            if (IsAcknowledged) return null;
            if (now >= _deadline || _attempts >= MaximumAttempts && now >= _next)
                throw new InvalidOperationException("native-abort-receipt-timeout");
            if (now < _next) return null;
            _next = now + RetryDelay;
            _attempts++;
            return RealTimeNativeCheckpointLimits.Copy(_header);
        }

        public bool Acknowledge(long peer, ClientRealTimeNativeCheckpointAck ack)
        {
            if (_attempts == 0 || peer != _peer || !RealTimeNativeCheckpointLimits.Matches(_header, ack)
                || ack.Stage != (int)RealTimeNativeCheckpointAckStage.Aborted || ack.Number != 0
                || ack.CanonicalStateHash != 0 || ack.ReferenceStateHash != null || ack.Reason != null) return false;
            IsAcknowledged = true;
            return true;
        }
    }
}
