using System;
using FakeItEasy;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using WOTRMultiplayer.Abstractions.GameInteraction;
using WOTRMultiplayer.Abstractions.Hashing;
using WOTRMultiplayer.Services.Random;

namespace WOTRMultiplayer.UnitTests.Services.Random
{
    public class GeneratorCheckpointTests
    {
        private static DeterministicValueGenerator Create() => new(
            A.Fake<ILogger<DeterministicValueGenerator>>(), A.Fake<IGameInteractionService>(), A.Fake<IHashService>());

        [Test]
        public void RestoresExactSequenceAfterMixedRandomCalls()
        {
            var original = Create();
            var stream = original.GetRandom(IdentifierLifetime.CombatTurn, "turn");
            var bytes = new byte[23];
            stream.NextBytes(bytes); stream.NextDouble(); stream.Next(int.MinValue, int.MaxValue);
            var checkpoint = original.CaptureCheckpoint();
            var restored = Create();
            restored.RestoreCheckpoint(checkpoint);
            var next = restored.GetRandom(IdentifierLifetime.CombatTurn, "turn");
            for (int i = 0; i < 200; i++)
            {
                Assert.That(next.Next(), Is.EqualTo(stream.Next()));
                Assert.That(next.NextDouble(), Is.EqualTo(stream.NextDouble()));
            }
            Assert.That(checkpoint.Streams[0].State, Is.Not.SameAs(original.CaptureCheckpoint().Streams[0].State));
        }

        [Test]
        public void InvalidRestoreLeavesLiveGeneratorUnchanged()
        {
            var generator = Create();
            generator.GetRandom(IdentifierLifetime.Combat, "combat").Next();
            var checkpoint = generator.CaptureCheckpoint();
            var control = Create(); control.RestoreCheckpoint(checkpoint);
            checkpoint.Streams[0].State = new int[2];
            Assert.Throws<ArgumentException>(() => generator.RestoreCheckpoint(checkpoint));
            Assert.That(generator.GetRandom(IdentifierLifetime.Combat, "combat").Next(),
                Is.EqualTo(control.GetRandom(IdentifierLifetime.Combat, "combat").Next()));
        }
    }
}
