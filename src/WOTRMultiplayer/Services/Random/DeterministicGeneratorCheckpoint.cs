using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace WOTRMultiplayer.Services.Random
{
    // Explicit data only: never deserialize runtime objects/BinaryFormatter from a save.
    public sealed class DeterministicGeneratorCheckpoint
    {
        public int Version { get; set; } = 1;
        public Dictionary<int, RandomStreamCheckpoint> Streams { get; set; } = new();
        public Dictionary<string, Dictionary<string, uint>> EntityCounters { get; set; } = new();
    }

    public sealed class RandomStreamCheckpoint
    {
        public IdentifierLifetime Lifetime { get; set; }
        public int Index { get; set; }
        public int PreviousIndex { get; set; }
        public int[] State { get; set; }
    }

    internal static class FrameworkRandomCheckpoint
    {
        private static FieldInfo Field(string framework, string mono)
            => typeof(System.Random).GetField(framework, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? typeof(System.Random).GetField(mono, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new NotSupportedException("combat-save-random-runtime-unsupported");

        internal static RandomStreamCheckpoint Capture(Seed seed)
        {
            if (seed.Random?.GetType() != typeof(System.Random))
                throw new NotSupportedException("combat-save-random-subclass-unsupported");
            var result = new RandomStreamCheckpoint
            {
                Lifetime = seed.Lifetime,
                Index = (int)Field("inext", "_inext").GetValue(seed.Random),
                PreviousIndex = (int)Field("inextp", "_inextp").GetValue(seed.Random),
                State = ((int[])Field("SeedArray", "_seedArray").GetValue(seed.Random)).ToArray()
            };
            Validate(result);
            return result;
        }

        private static void Validate(RandomStreamCheckpoint value)
        {
            if (value == null || !Enum.IsDefined(typeof(IdentifierLifetime), value.Lifetime)
                || value.Index < 0 || value.Index > 55 || value.PreviousIndex < 0 || value.PreviousIndex > 55
                || value.State?.Length != 56 || value.State.Any(x => x < 0 || x >= int.MaxValue))
                throw new ArgumentException("combat-save-random-state-invalid");
        }

        internal static Seed Restore(RandomStreamCheckpoint value)
        {
            Validate(value);
            var random = new System.Random(0);
            Field("inext", "_inext").SetValue(random, value.Index);
            Field("inextp", "_inextp").SetValue(random, value.PreviousIndex);
            Field("SeedArray", "_seedArray").SetValue(random, value.State.ToArray());
            return new Seed { Lifetime = value.Lifetime, Random = random };
        }
    }
}
