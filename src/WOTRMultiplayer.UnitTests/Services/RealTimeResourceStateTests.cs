using NUnit.Framework;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.Services;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeResourceStateTests
    {
        private static NetworkAbilityResource Resource(string id, int amount = 3, int retained = 1) =>
            new() { BlueprintId = id, Amount = amount, RetainCount = retained };

        [Test]
        public void ChangedAmounts_AreRepairableWithoutAddingOrSpendingResources()
        {
            Assert.That(RealTimeResourceState.Validate([Resource("rage", 2)], [Resource("rage", 7)]), Is.Null);
        }

        [TestCase(-1, 1)]
        [TestCase(3, 0)]
        public void InvalidResource_IsRejected(int amount, int retained)
        {
            Assert.That(RealTimeResourceState.Validate([Resource("rage", amount, retained)], [Resource("rage")]),
                Is.EqualTo("invalid-resources"));
        }

        [Test]
        public void MissingExtraDuplicateAndRetainedResources_RequireReconstruction()
        {
            Assert.That(RealTimeResourceState.Validate([Resource("rage")], []), Is.EqualTo("missing-resource:rage"));
            Assert.That(RealTimeResourceState.Validate([], [Resource("rage")]), Is.EqualTo("extra-resource:rage"));
            Assert.That(RealTimeResourceState.Validate([Resource("rage"), Resource("RAGE")], []), Is.EqualTo("invalid-resources"));
            Assert.That(RealTimeResourceState.Validate([Resource("rage", retained: 2)], [Resource("rage")]),
                Is.EqualTo("resource-retain-count:rage"));
        }

        [Test]
        public void ResourceDivergence_ChangesHashAndHasReadableDiagnosis()
        {
            var host = State(2);
            var client = State(3);
            Assert.That(Hash(host), Is.Not.EqualTo(Hash(client)));
            Assert.That(RealTimeCombatDiff.Summarize(host, client), Is.EqualTo("unit:hero:resources"));
            client.Units[0].Descriptor.Resources[0].Amount = 2;
            Assert.That(Hash(host), Is.EqualTo(Hash(client)));
        }

        [Test]
        public void ResourceOrder_DoesNotAffectHash()
        {
            var host = State(2);
            host.Units[0].Descriptor.Resources.Add(Resource("channel"));
            var client = State(2);
            client.Units[0].Descriptor.Resources.Insert(0, Resource("channel"));
            Assert.That(Hash(host), Is.EqualTo(Hash(client)));
        }

        [Test]
        public void UncapturedResources_MissingDescriptorDoesNotCreateFalseDiagnosis()
        {
            var host = State(2);
            host.Units[0].Descriptor.ResourcesCaptured = false;
            var client = State(2);
            client.Units[0].Descriptor = null;
            Assert.That(Hash(host), Is.EqualTo(Hash(client)));
            Assert.That(RealTimeCombatDiff.Summarize(host, client), Is.EqualTo("unclassified-state-difference"));
        }

        [Test]
        public void CapturedEmptyResources_AreDifferentFromUncapturedResources()
        {
            var host = State(2);
            host.Units[0].Descriptor.Resources.Clear();
            var client = State(2);
            client.Units[0].Descriptor = null;
            Assert.That(Hash(host), Is.Not.EqualTo(Hash(client)));
            Assert.That(RealTimeCombatDiff.Summarize(host, client), Is.EqualTo("unit:hero:resources"));
        }

        private static ulong Hash(NetworkCombatState state) =>
            RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = state });

        private static NetworkCombatState State(int amount) => new()
        {
            Units = [new NetworkUnit
            {
                Id = "hero", Descriptor = new NetworkUnitDescriptor
                {
                    ResourcesCaptured = true, Resources = [Resource("rage", amount)]
                }
            }]
        };
    }
}
