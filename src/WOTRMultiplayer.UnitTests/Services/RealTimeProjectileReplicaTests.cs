using System;
using System.Collections.Generic;
using System.Linq;
using AutoMapper;
using NUnit.Framework;
using WOTRMultiplayer.Config.Mapping;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat;
using WOTRMultiplayer.Entities.Combat.RealTime;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.GameInteraction;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.UnitTests.Services
{
    public class RealTimeProjectileReplicaTests
    {
        private sealed class Visual : IRealTimeProjectileVisual
        {
            public bool IsAlive { get; set; } = true;
            public bool Disposed;
            public bool Fail;
            public int FailuresRemaining;
            public bool FailDispose;
            public int Confirmations;
            public NetworkProjectileState State;
            public void Confirm(NetworkProjectileState state)
            {
                if (Fail) { Fail = false; throw new InvalidOperationException("injected-confirm-fault"); }
                if (FailuresRemaining-- > 0) throw new InvalidOperationException("injected-persistent-confirm-fault");
                Confirmations++;
                State = state;
            }
            public void Dispose()
            {
                Disposed = true; IsAlive = false;
                if (FailDispose) throw new InvalidOperationException("injected-dispose-fault");
            }
        }

        private static NetworkProjectileState State(string id = "arrow", float x = 1, bool hit = false) => new()
        {
            Id = id, BlueprintId = "0123456789abcdef0123456789abcdef",
            Position = new NetworkVector3(x, 2, 3), IsHit = hit
        };

        [Test]
        public void FailedUpdateAndRollback_PreserveBothCausesAndConfirmedState()
        {
            var view = new Visual();
            var replica = new RealTimeProjectileReplica(_ => view);
            replica.Apply(State());
            view.FailuresRemaining = 2;
            var error = Assert.Throws<AggregateException>(() => replica.Apply(State(x: 9)));
            Assert.That(error.InnerExceptions.Count, Is.EqualTo(2));
            Assert.That(replica.Snapshot()[0].Position.X, Is.EqualTo(1));
        }

        [Test]
        public void FailedNewViewConfirmationAndCleanup_KeepBothCausesAndNoLogicalEntry()
        {
            var view = new Visual { Fail = true, FailDispose = true };
            var replica = new RealTimeProjectileReplica(_ => view);
            var error = Assert.Throws<AggregateException>(() => replica.Apply(State()));
            Assert.That(error.InnerExceptions.Count, Is.EqualTo(2));
            Assert.That(view.Disposed, Is.True);
            Assert.That(replica.Snapshot(), Is.Empty);
        }

        [Test]
        public void RetirementFailure_DoesNotPreventCleaningOtherRemovedViews()
        {
            var first = new Visual { FailDispose = true };
            var second = new Visual();
            var replica = new RealTimeProjectileReplica(state => state.Id == "a" ? first : second);
            replica.Restore([State("a"), State("b")]);
            Assert.Throws<AggregateException>(() => replica.Clear());
            Assert.That(first.Disposed, Is.True);
            Assert.That(second.Disposed, Is.True);
            Assert.That(replica.Snapshot(), Is.Empty);
        }

        [Test]
        public void MissingProjectile_IsReconstructedAndUpdatesReuseTheVisual()
        {
            var visuals = new List<Visual>();
            var replica = new RealTimeProjectileReplica(_ => { var view = new Visual(); visuals.Add(view); return view; });
            replica.Apply(State());
            replica.Apply(State(x: 8));
            Assert.That(visuals.Count, Is.EqualTo(1));
            Assert.That(replica.Snapshot()[0].Position.X, Is.EqualTo(8));
            replica.Restore([]);
            Assert.That(visuals[0].Disposed, Is.True);
            Assert.That(replica.Snapshot(), Is.Empty);
        }

        [Test]
        public void StateOwnership_IsolatedFromSenderRendererAndSnapshotConsumer()
        {
            var view = new Visual();
            var replica = new RealTimeProjectileReplica(_ => view);
            var sent = State();
            replica.Apply(sent);
            sent.Position.X = 700;
            view.State.Position.X = 800;
            replica.Snapshot()[0].Position.X = 900;
            Assert.That(replica.Snapshot()[0].Position.X, Is.EqualTo(1));
        }

        [Test]
        public void PreparationFailure_DestroysCreatedViewsAndPreservesOldState()
        {
            var views = new List<Visual>();
            var replica = new RealTimeProjectileReplica(state =>
            {
                if (state.Id == "bad") throw new InvalidOperationException("unsupported-prefab");
                var view = new Visual(); views.Add(view); return view;
            });
            replica.Apply(State("old"));
            Assert.Throws<InvalidOperationException>(() => replica.Restore([State("old", 5), State("new"), State("bad")]));
            Assert.That(views[0].Disposed, Is.False);
            Assert.That(views[0].State.Position.X, Is.EqualTo(1));
            Assert.That(views[1].Disposed, Is.True);
            Assert.That(replica.Snapshot().Select(state => state.Id), Is.EqualTo(new[] { "old" }));
        }

        [Test]
        public void ConfirmationFailure_RollsBackExistingVisualAndLogicalState()
        {
            var view = new Visual();
            var replica = new RealTimeProjectileReplica(_ => view);
            replica.Apply(State());
            view.Fail = true;
            Assert.Throws<InvalidOperationException>(() => replica.Restore([State(x: 9)]));
            Assert.That(replica.Snapshot()[0].Position.X, Is.EqualTo(1));
            Assert.That(view.State.Position.X, Is.EqualTo(1));
            Assert.That(view.Disposed, Is.False);
        }

        [Test]
        public void ImpactCannotBeUndoneByAnEventButReferenceRestorationMayRebuild()
        {
            var views = new List<Visual>();
            var replica = new RealTimeProjectileReplica(_ => { var view = new Visual(); views.Add(view); return view; });
            replica.Apply(State(hit: true));
            Assert.Throws<InvalidOperationException>(() => replica.Apply(State()));
            replica.Restore([State()]);
            Assert.That(views.Count, Is.EqualTo(2));
            Assert.That(views[0].Disposed, Is.True);
            Assert.That(replica.Snapshot()[0].IsHit, Is.False);
        }

        [Test]
        public void LostVisual_IsRecreatedWithoutDuplicatingLogicalEntity()
        {
            var views = new List<Visual>();
            var replica = new RealTimeProjectileReplica(_ => { var view = new Visual(); views.Add(view); return view; });
            replica.Apply(State());
            views[0].IsAlive = false;
            replica.Restore([State(x: 4)]);
            Assert.That(views.Count, Is.EqualTo(2));
            Assert.That(replica.Snapshot().Count, Is.EqualTo(1));
            Assert.That(views[0].Disposed, Is.True);
        }

        [TestCase("nan")]
        [TestCase("rotation")]
        [TestCase("blueprint")]
        [TestCase("identity")]
        public void MalformedReference_IsRejectedBeforeAnyVisualCreation(string invalid)
        {
            var count = 0;
            var replica = new RealTimeProjectileReplica(_ => { count++; return new Visual(); });
            var state = State();
            switch (invalid)
            {
                case "nan": state.Position.X = float.NaN; break;
                case "rotation": state.RotationW = 0; break;
                case "blueprint": state.BlueprintId = "missing"; break;
                case "identity": state.Id = " "; break;
            }
            Assert.Throws<InvalidOperationException>(() => replica.Restore([State("good"), state]));
            Assert.That(count, Is.Zero);
        }

        [Test]
        public void DuplicateReferenceIds_AreRejectedWithoutCreation()
        {
            var replica = new RealTimeProjectileReplica(_ => throw new AssertionException("must not create"));
            Assert.Throws<InvalidOperationException>(() => replica.Restore([State(), State()]));
        }

        [Test]
        public void SingleProjectileUpdate_DoesNotTouchOtherVisuals()
        {
            var views = new List<Visual>();
            var replica = new RealTimeProjectileReplica(_ => { var view = new Visual(); views.Add(view); return view; });
            replica.Restore([State("first"), State("second")]);
            var unchanged = views[1].Confirmations;
            replica.Apply(State("first", 8));
            Assert.That(views[1].Confirmations, Is.EqualTo(unchanged));
        }

        [Test]
        public void WireMappingAndCanonicalHash_PreserveFullConfirmedState()
        {
            var mapper = new MapperConfiguration(config => config.AddProfile<NetworkMessagesProfile>()).CreateMapper();
            var host = new NetworkCombatState { ProjectilesCaptured = true, Projectiles = [State("one"), State("two", 8)] };
            var restored = mapper.Map<NetworkCombatState>(ProtoBuf.Serializer.DeepClone(
                mapper.Map<Networking.Messages.Contracts.NetworkCombatState>(host)));
            restored.Projectiles.Reverse();
            Assert.That(Hash(restored), Is.EqualTo(Hash(host)));
            restored.Projectiles[0].IsHit = true;
            Assert.That(Hash(restored), Is.Not.EqualTo(Hash(host)));
            Assert.That(RealTimeCombatDiff.Summarize(host, restored), Is.EqualTo("projectiles"));
            var empty = mapper.Map<NetworkCombatState>(ProtoBuf.Serializer.DeepClone(
                mapper.Map<Networking.Messages.Contracts.NetworkCombatState>(new NetworkCombatState { ProjectilesCaptured = true })));
            Assert.That(empty.ProjectilesCaptured, Is.True);
            Assert.That(empty.Projectiles, Is.Empty);
        }

        [Test]
        public void UnverifiedPrefabScripts_AreDeniedByDefault()
        {
            Assert.That(RealTimeProjectileVisual.IsAllowedScript(typeof(RealTimeProjectileReplicaTests)), Is.False);
            Assert.That(RealTimeProjectileVisual.IsAllowedScript(typeof(Kingmaker.View.ProjectileView)), Is.True);
        }

        [Test]
        public void UnsupportedPresentation_CanBeOmittedWithoutLosingAuthoritativeProjectileState()
        {
            var replica = new RealTimeProjectileReplica(state => new RealTimeOmittedProjectileVisual(state));
            replica.Apply(State());
            replica.Apply(State(x: 8, hit: true));

            Assert.That(replica.Snapshot().Single().Position.X, Is.EqualTo(8));
            Assert.That(replica.Snapshot().Single().IsHit, Is.True);
            Assert.Throws<InvalidOperationException>(() => replica.Apply(State(x: 9)));
            Assert.That(replica.Snapshot().Single().IsHit, Is.True);
            Assert.DoesNotThrow(() => replica.Clear());
        }

        [Test]
        public void OnlyKnownPresentationCapabilityFailures_AreSafeToOmit()
        {
            Assert.That(RealTimeProjectileVisual.CanSafelyOmit(
                new InvalidOperationException("projectile-blueprint-missing:id")), Is.True);
            Assert.That(RealTimeProjectileVisual.CanSafelyOmit(
                new InvalidOperationException("projectile-prefab-missing:id")), Is.True);
            Assert.That(RealTimeProjectileVisual.CanSafelyOmit(
                new InvalidOperationException("projectile-prefab-unverified-scripts:id")), Is.True);
            Assert.That(RealTimeProjectileVisual.CanSafelyOmit(
                new InvalidOperationException("invalid-projectile-state")), Is.False);
            Assert.That(RealTimeProjectileVisual.CanSafelyOmit(new Exception("unexpected")), Is.False);
        }

        [Test]
        public void ReferenceBaselineAndDelayedImpact_ResumeAtFirstUnincludedEvent()
        {
            var mapper = new MapperConfiguration(config => config.AddProfile<NetworkMessagesProfile>()).CreateMapper();
            var replica = new RealTimeProjectileReplica(_ => new Visual());
            var inbox = new RealTimePresentationInbox();
            NotifyRealTimeAuthoritativeEvent Frame(long sequence, NetworkProjectileState state) => new()
            {
                Sequence = sequence, Epoch = 3, EventId = "3:" + sequence, ActionId = state.Id,
                Kind = (int)RealTimeAuthoritativeEventKind.ProjectilePresentation,
                Projectile = new RealTimeProjectilePresentationPayload
                {
                    Stage = state.IsHit ? 3 : 2,
                    State = mapper.Map<Networking.Messages.Contracts.NetworkProjectileState>(state)
                }
            };
            inbox.Enqueue(Frame(3, State(x: 9, hit: true)));
            inbox.Enqueue(Frame(2, State(x: 5)));
            replica.Restore([State()]); // Host reference includes event 1.
            inbox.RestoreBaseline(1);
            while (inbox.TryPeek(out var frame))
            {
                Assert.That(RealTimeAuthoritativePresentation.IsSupported(frame), Is.True);
                replica.Apply(mapper.Map<NetworkProjectileState>(frame.Projectile.State));
                inbox.Commit(frame.Sequence);
            }
            Assert.That(inbox.Enqueue(Frame(1, State())), Is.True); // Old event cannot resurrect it.
            Assert.That(inbox.TryPeek(out _), Is.False);
            var host = new NetworkCombatState { ProjectilesCaptured = true, Projectiles = [State(x: 9, hit: true)] };
            var client = new NetworkCombatState { ProjectilesCaptured = true, Projectiles = replica.Snapshot() };
            Assert.That(Hash(client), Is.EqualTo(Hash(host)));
        }

        [Test]
        public void ProjectileEnvelope_RequiresMatchingIdentityAndNoOtherPayload()
        {
            var frame = new NotifyRealTimeAuthoritativeEvent
            {
                ActionId = "arrow", Kind = (int)RealTimeAuthoritativeEventKind.ProjectilePresentation,
                Projectile = new RealTimeProjectilePresentationPayload
                    { Stage = 1, State = new Networking.Messages.Contracts.NetworkProjectileState { Id = "arrow" } }
            };
            Assert.That(RealTimeAuthoritativePresentation.IsSupported(frame), Is.True); // Point launcher has no unit.
            frame.ActionId = "other";
            Assert.That(RealTimeAuthoritativePresentation.IsSupported(frame), Is.False);
            frame.ActionId = "arrow";
            frame.Ability = new RealTimeAbilityPresentationPayload();
            Assert.That(RealTimeAuthoritativePresentation.IsSupported(frame), Is.False);
        }

        private static ulong Hash(NetworkCombatState state) =>
            RealTimeCombatHasher.Compute(new RealTimeCombatSnapshot { Combat = state });
    }
}
