using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using AutoMapper;
using NUnit.Framework;
using WOTRMultiplayer.Config.Mapping;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Entities.Units;
using WOTRMultiplayer.HarmonyPatches.Combat;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeSpellbookStateTests
    {
        private static NetworkSpellbookState Book(string id = "wizard") => new()
        {
            BlueprintId = id, SpontaneousSlots = [0, 3, 2],
            PreparedSlots = [new NetworkPreparedSpellState
            {
                Level = 1, Index = 0, Type = 0, SpellBlueprintId = "magic-missile", Available = true
            }]
        };

        [Test]
        public void SpentPreparedAndSpontaneousSlots_AreRepairableWithoutCasting()
        {
            var host = Book();
            var client = Book();
            client.SpontaneousSlots[1] = 1;
            client.PreparedSlots[0].Available = false;
            Assert.That(RealTimeSpellbookState.Validate([host], [client]), Is.Null);
        }

        [TestCase("spell")]
        [TestCase("metamagic")]
        [TestCase("opposition")]
        [TestCase("level")]
        [TestCase("index")]
        [TestCase("type")]
        public void ChangedSlotIdentity_IsRejectedBeforeApplyingCounts(string changed)
        {
            var client = Book();
            var slot = client.PreparedSlots[0];
            switch (changed)
            {
                case "spell": slot.SpellBlueprintId = "fireball"; break;
                case "metamagic": slot.Metamagic = 1; break;
                case "opposition": slot.IsOpposition = true; break;
                case "level": slot.Level++; break;
                case "index": slot.Index++; break;
                case "type": slot.Type++; break;
            }
            Assert.That(RealTimeSpellbookState.Validate([Book()], [client]),
                Is.EqualTo("spellbook-slot-identity:wizard"));
        }

        [Test]
        public void MissingExtraBooksAndLevelShapes_AreRejected()
        {
            Assert.That(RealTimeSpellbookState.Validate([Book()], []), Is.EqualTo("missing-spellbook:wizard"));
            Assert.That(RealTimeSpellbookState.Validate([], [Book()]), Is.EqualTo("extra-spellbook:wizard"));
            var client = Book();
            client.SpontaneousSlots.RemoveAt(0);
            Assert.That(RealTimeSpellbookState.Validate([Book()], [client]), Is.EqualTo("spellbook-levels:wizard"));
            client = Book();
            client.PreparedSlots.Clear();
            Assert.That(RealTimeSpellbookState.Validate([Book()], [client]), Is.EqualTo("spellbook-slots:wizard"));
        }

        [Test]
        public void MalformedBooksAndDuplicateSlots_AreRejected()
        {
            Assert.That(RealTimeSpellbookState.Validate(null, []), Is.EqualTo("invalid-spellbooks"));
            Assert.That(RealTimeSpellbookState.Validate([Book(), Book("WIZARD")], []), Is.EqualTo("invalid-spellbooks"));
            var invalid = Book();
            invalid.SpontaneousSlots[1] = -1;
            Assert.That(RealTimeSpellbookState.Validate([invalid], []), Is.EqualTo("invalid-spellbooks"));
            invalid = Book();
            invalid.PreparedSlots.Add(invalid.PreparedSlots[0]);
            Assert.That(RealTimeSpellbookState.Validate([invalid], []), Is.EqualTo("invalid-spellbooks"));
            Assert.That(RealTimeSpellbookState.Validate([null], []), Is.EqualTo("invalid-spellbooks"));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void CapturedBookState_RoundTripsMapperAndWire(bool populated)
        {
            var mapper = new MapperConfiguration(configuration =>
                configuration.AddProfile<NetworkMessagesProfile>()).CreateMapper();
            var source = new NetworkUnitDescriptor { SpellbooksCaptured = true };
            if (populated) source.Spellbooks.Add(Book());
            var restored = mapper.Map<NetworkUnitDescriptor>(ProtoBuf.Serializer.DeepClone(
                mapper.Map<Networking.Messages.Contracts.NetworkUnitDescriptor>(source)));
            Assert.That(restored.SpellbooksCaptured, Is.True);
            Assert.That(restored.Spellbooks.Count, Is.EqualTo(populated ? 1 : 0));
            if (!populated) return;
            Assert.That(restored.Spellbooks[0].SpontaneousSlots, Is.EqualTo(new[] { 0, 3, 2 }));
            Assert.That(restored.Spellbooks[0].PreparedSlots[0].SpellBlueprintId, Is.EqualTo("magic-missile"));
            Assert.That(restored.Spellbooks[0].PreparedSlots[0].Available, Is.True);
        }

        [Test]
        public void AvailabilityDivergence_ChangesHashAndNamesSpellbooks()
        {
            var host = State(Book());
            var client = State(Book());
            client.Units[0].Descriptor.Spellbooks[0].PreparedSlots[0].Available = false;
            Assert.That(Hash(host), Is.Not.EqualTo(Hash(client)));
            Assert.That(RealTimeCombatDiff.Summarize(host, client), Is.EqualTo("unit:hero:spellbooks"));
            client.Units[0].Descriptor.Spellbooks[0].PreparedSlots[0].Available = true;
            client.Units[0].Descriptor.Spellbooks[0].SpontaneousSlots[1]--;
            Assert.That(Hash(host), Is.Not.EqualTo(Hash(client)));
        }

        [Test]
        public void BookAndSlotOrder_DoesNotChangeHash()
        {
            var first = Book();
            first.PreparedSlots.Add(new NetworkPreparedSpellState { Level = 2, Index = 0, Type = 0 });
            var second = Book();
            second.PreparedSlots.Insert(0, new NetworkPreparedSpellState { Level = 2, Index = 0, Type = 0 });
            Assert.That(Hash(State(first, Book("cleric"))), Is.EqualTo(Hash(State(Book("cleric"), second))));
        }

        [Test]
        public void ClientMutationHooks_ExistInInstalledGameAssembly()
        {
            var targets = RealTimeSpellbookPatches.Targets().ToList();
            Assert.That(targets.Count, Is.EqualTo(3));
            Assert.That(targets.All(target => target != null), Is.True);
            var internalSpend = HarmonyLib.AccessTools.DeclaredMethod(typeof(Kingmaker.UnitLogic.Spellbook), "SpendInternal");
            Assert.That(internalSpend, Is.Not.Null);
            Assert.That(internalSpend.GetParameters().Any(parameter => parameter.Name == "doSpend"
                && parameter.ParameterType == typeof(bool)), Is.True);
            Assert.That(RealTimeSpellbookInternalSpendPatch.BeforeSpend(false), Is.True);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ApplyingAvailability_IsIdempotentAndNeedsNoOwnerOrGameplayCallbacks(bool available)
        {
            // Plain managed objects, no Game.Instance or Unity startup. Spend/Rest
            // cannot run on this ownerless fixture: only direct state writes work.
            var bookType = typeof(Kingmaker.UnitLogic.Spellbook);
            var slotType = typeof(Kingmaker.UnitLogic.SpellSlot);
            var book = (Kingmaker.UnitLogic.Spellbook)FormatterServices.GetUninitializedObject(bookType);
            var slot = (Kingmaker.UnitLogic.SpellSlot)FormatterServices.GetUninitializedObject(slotType);
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            slotType.GetField("Index", flags).SetValue(slot, 0);
            var counts = new[] { 0, 8, 9 };
            var slots = new List<Kingmaker.UnitLogic.SpellSlot>[] { [], [slot] };
            bookType.GetField("m_SpontaneousSlots", flags).SetValue(book, counts);
            bookType.GetField("m_MemorizedSpells", flags).SetValue(book, slots);
            var state = Book();
            state.PreparedSlots[0].Available = available;
            var apply = typeof(RealTimeSpellbookReplication).GetMethod("ApplyBookAvailability",
                BindingFlags.NonPublic | BindingFlags.Static);
            apply.Invoke(null, new object[] { book, state });
            apply.Invoke(null, new object[] { book, state });
            Assert.That(counts, Is.EqualTo(new[] { 0, 3, 2 }));
            Assert.That(slotType.GetField("Available", flags).GetValue(slot), Is.EqualTo(available));
            Assert.That(bookType.GetField("m_MemorizedSpells", flags).GetValue(book), Is.SameAs(slots));
            Assert.That(state.SpontaneousSlots, Is.EqualTo(new[] { 0, 3, 2 }));
        }

        private static NetworkCombatState State(params NetworkSpellbookState[] books) => new()
        {
            Units = [new NetworkUnit { Id = "hero", Descriptor = new NetworkUnitDescriptor
                { SpellbooksCaptured = true, Spellbooks = books.ToList() } }]
        };
        private static ulong Hash(NetworkCombatState state) =>
            RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = state });
    }
}
