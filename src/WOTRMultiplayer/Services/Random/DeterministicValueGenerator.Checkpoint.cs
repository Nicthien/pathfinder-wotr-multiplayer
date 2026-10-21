using System;
using System.Linq;

namespace WOTRMultiplayer.Services.Random
{
    public partial class DeterministicValueGenerator
    {
        // Call only under the combat-save barrier, after all command/rule work has stopped.
        // These APIs do not authorize saving, pause the engine or reset turn lifetimes.
        public DeterministicGeneratorCheckpoint CaptureCheckpoint()
            => new()
            {
                Streams = _seedGenerators.ToDictionary(x => x.Key, x => FrameworkRandomCheckpoint.Capture(x.Value)),
                EntityCounters = _entityCounters.ToDictionary(x => x.Key,
                    x => x.Value.NameIdentifiers.ToDictionary(y => y.Key, y => y.Value))
            };

        public void RestoreCheckpoint(DeterministicGeneratorCheckpoint checkpoint)
        {
            if (checkpoint?.Version != 1 || checkpoint.Streams == null || checkpoint.EntityCounters == null
                || checkpoint.Streams.Count > 100000 || checkpoint.EntityCounters.Count > 1000)
                throw new ArgumentException("combat-save-generator-checkpoint-invalid");
            // Validate/rebuild everything before replacing the live dictionaries.
            var streams = checkpoint.Streams.ToDictionary(x => x.Key, x => FrameworkRandomCheckpoint.Restore(x.Value));
            var counters = checkpoint.EntityCounters.ToDictionary(x => x.Key, x =>
            {
                if (string.IsNullOrWhiteSpace(x.Key) || x.Value == null || x.Value.Count > 100000
                    || x.Value.Any(y => string.IsNullOrWhiteSpace(y.Key)))
                    throw new ArgumentException("combat-save-counter-checkpoint-invalid");
                var counter = new IdCounters();
                foreach (var entry in x.Value) counter.NameIdentifiers[entry.Key] = entry.Value;
                return counter;
            });
            _seedGenerators.Clear();
            _entityCounters.Clear();
            foreach (var pair in streams) _seedGenerators[pair.Key] = pair.Value;
            foreach (var pair in counters) _entityCounters[pair.Key] = pair.Value;
        }
    }
}
