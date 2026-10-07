using System;
using System.IO;
using System.Text;
using ProtoBuf;
using WOTRMultiplayer.Networking.Messages.Contracts;

namespace WOTRMultiplayer.Networking
{
    // Bounded reference data only; no native game types or executable payload.
    public static class RealTimeNativeReferenceCodec
    {
        public const int MaximumBytes = 8 * 1024 * 1024;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("RTWREF01");

        public static byte[] Encode(NetworkCombatState state)
        {
            RequireState(state);
            using var stream = new BoundedStream();
            stream.Write(Magic, 0, Magic.Length);
            Serializer.Serialize(stream, state);
            return stream.ToArray();
        }

        public static NetworkCombatState Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length < Magic.Length || bytes.Length > MaximumBytes)
                throw new InvalidDataException("native-reference-size-invalid");
            for (var i = 0; i < Magic.Length; i++)
                if (bytes[i] != Magic[i]) throw new InvalidDataException("native-reference-format-invalid");
            using var input = new MemoryStream(bytes, Magic.Length, bytes.Length - Magic.Length, false);
            var state = bytes.Length == Magic.Length ? new NetworkCombatState() : Serializer.Deserialize<NetworkCombatState>(input);
            RequireState(state);
            return state;
        }

        private static void RequireState(NetworkCombatState state)
        {
            if (state?.Units == null || state.AreaEffects == null || state.Projectiles == null
                || state.Units.Count > 4096 || state.AreaEffects.Count > 16384 || state.Projectiles.Count > 4096)
                throw new InvalidDataException("native-reference-entity-count-invalid");
        }

        private sealed class BoundedStream : MemoryStream
        {
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (count < 0 || Position > MaximumBytes - (long)count)
                    throw new InvalidDataException("native-reference-size-limit");
                base.Write(buffer, offset, count);
            }
            public override void WriteByte(byte value)
            {
                if (Position >= MaximumBytes) throw new InvalidDataException("native-reference-size-limit");
                base.WriteByte(value);
            }
            public override void SetLength(long value)
            {
                if (value < 0 || value > MaximumBytes) throw new InvalidDataException("native-reference-size-limit");
                base.SetLength(value);
            }
        }
    }
}
