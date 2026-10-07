using NUnit.Framework;
using ProtoBuf;
using WOTRMultiplayer.Networking.Messages.Contracts;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.Networking.UnitTests.Messages
{
    [TestFixture]
    public class RealTimeMessagesTests
    {
        [Test]
        public void ProjectilePresentation_RoundTripsConfirmedImpactAndTransform()
        {
            var frame = Serializer.DeepClone(new NotifyRealTimeAuthoritativeEvent
            {
                Epoch = 9, Sequence = 42, EventId = "9:42", ActionId = "arrow",
                Kind = (int)RealTimeAuthoritativeEventKind.ProjectilePresentation,
                Projectile = new RealTimeProjectilePresentationPayload
                {
                    Stage = 3, State = new NetworkProjectileState
                    {
                        Id = "arrow", BlueprintId = "projectile-blueprint", IsHit = true,
                        Position = new NetworkVector3 { X = 1, Y = 2, Z = 3 }, RotationY = 0.6f, RotationW = 0.8f
                    }
                }
            });
            Assert.That(frame.Ability, Is.Null);
            Assert.That(frame.Attack, Is.Null);
            Assert.That(frame.Projectile.Stage, Is.EqualTo(3));
            Assert.That(frame.Projectile.State.IsHit, Is.True);
            Assert.That(frame.Projectile.State.Position.Z, Is.EqualTo(3));
            Assert.That(frame.Projectile.State.RotationY, Is.EqualTo(0.6f));
            Assert.That(frame.Projectile.State.RotationW, Is.EqualTo(0.8f));
        }

        [Test]
        public void AbilityPresentation_RoundTripsIdentityAndCastParameters()
        {
            var frame = Serializer.DeepClone(new NotifyRealTimeAuthoritativeEvent
            {
                SessionId = "session", LoadedSaveSeed = 18, Epoch = 9, Sequence = 42,
                EventId = "9:42", ActionId = "hero:ability:5", UnitId = "hero", TargetUnitId = "enemy",
                Kind = (int)RealTimeAuthoritativeEventKind.AbilityPresentation,
                Ability = new RealTimeAbilityPresentationPayload
                {
                    Stage = 1, BlueprintId = "blueprint", AnimationType = 8, CastStyle = 3,
                    SpecialBehaviour = 2, CastingTime = 2.5f, SpeedScale = 1.1f,
                    WeaponStyle = 4, TargetDistance = 12, IsMounted = true, MinimalTransitionOut = 0.3f
                }
            });
            Assert.That(frame.SessionId, Is.EqualTo("session"));
            Assert.That(frame.LoadedSaveSeed, Is.EqualTo(18));
            Assert.That(frame.EventId, Is.EqualTo("9:42"));
            Assert.That(frame.ActionId, Is.EqualTo("hero:ability:5"));
            Assert.That(frame.Attack, Is.Null);
            Assert.That(frame.Ability.BlueprintId, Is.EqualTo("blueprint"));
            Assert.That(frame.Ability.CastingTime, Is.EqualTo(2.5f));
            Assert.That(frame.Ability.SpecialBehaviour, Is.EqualTo(2));
            Assert.That(frame.Ability.IsMounted, Is.True);
            Assert.That(frame.Ability.MinimalTransitionOut, Is.EqualTo(0.3f));
        }

        [Test]
        public void Resources_RoundTripAmountsAndExplicitEmptyCapture()
        {
            var captured = Serializer.DeepClone(new NetworkUnitDescriptor
            {
                ResourcesCaptured = true,
                Resources = [new NetworkAbilityResource { BlueprintId = "rage", Amount = 2, RetainCount = 1 }]
            });
            Assert.That(captured.ResourcesCaptured, Is.True);
            Assert.That(captured.Resources[0].BlueprintId, Is.EqualTo("rage"));
            Assert.That(captured.Resources[0].Amount, Is.EqualTo(2));
            Assert.That(captured.Resources[0].RetainCount, Is.EqualTo(1));
            var empty = Serializer.DeepClone(new NetworkUnitDescriptor { ResourcesCaptured = true });
            Assert.That(empty.ResourcesCaptured, Is.True);
            Assert.That(empty.Resources, Is.Empty);
        }
        [Test]
        public void TurnEndSynchronization_RoundTripsFailureAndSequence()
        {
            var checkpoint = Serializer.DeepClone(new NotifyCombatTurnEndSynchronizationRequired
            {
                UnitId = "hero", SynchronizationSequence = 9,
                Units = [new NetworkUnit { Id = "hero" }]
            });
            var reply = Serializer.DeepClone(new ClientCombatTurnEndSynchronized
            {
                UnitId = "hero", PlayerId = 2, SynchronizationSequence = 9,
                Succeeded = false, FailureReason = "missing-unit:hero"
            });
            var complete = Serializer.DeepClone(new NotifyCombatTurnEnded
            {
                UnitId = "hero", SynchronizationSequence = 9
            });

            Assert.That(checkpoint.UnitId, Is.EqualTo("hero"));
            Assert.That(checkpoint.SynchronizationSequence, Is.EqualTo(9));
            Assert.That(checkpoint.Units[0].Id, Is.EqualTo("hero"));
            Assert.That(reply.SynchronizationSequence, Is.EqualTo(9));
            Assert.That(reply.Succeeded, Is.False);
            Assert.That(reply.FailureReason, Is.EqualTo("missing-unit:hero"));
            Assert.That(complete.UnitId, Is.EqualTo("hero"));
            Assert.That(complete.SynchronizationSequence, Is.EqualTo(9));
        }

        [Test]
        public void ContentState_RoundTripsExactBuildFingerprint()
        {
            var source = new NetworkContentState
            {
                GameVersion = "2.7.0",
                RealTimeProtocolVersion = 30,
                RealTimeBuildFingerprint = "ABCD1234"
            };

            var copy = Serializer.DeepClone(source);

            Assert.That(copy.RealTimeProtocolVersion, Is.EqualTo(30));
            Assert.That(copy.RealTimeBuildFingerprint, Is.EqualTo("ABCD1234"));
        }

        [Test]
        public void Snapshot_RoundTripsEpochSequenceHashAndCombatState()
        {
            var source = new NotifyRealTimeCombatSnapshot
            {
                Epoch = 7,
                Sequence = 12,
                CanonicalStateHash = 987654321UL,
                ResponseTimeMs = 42,
                MissingReplies = 3,
                SessionId = "session-a",
                LoadedSaveSeed = 44,
                IncludedEventSequence = 9,
                State = new NetworkCombatState
                {
                    Units = [new NetworkUnit
                    {
                        Id = "unit-a", BlueprintId = "unit-blueprint", OriginalBlueprintId = "original-unit",
                        CasterUnitId = "caster", SourceAbilityBlueprintId = "summon-spell",
                        MasterUnitId = "master", GroupId = "summoned-group", SceneName = "combat-scene"
                    }],
                    AreaEffects = [new NetworkAreaEffect
                    {
                        Id = "area-a", BlueprintId = "area-blueprint", CasterUnitId = "caster",
                        OwnerUnitId = "owner", SourceAbilityBlueprintId = "source-ability",
                        CreationGameTimeTicks = 123456, DurationTicks = 60000000,
                        SceneName = "combat-scene", TargetUnitId = "unit-a",
                        TargetOrientation = 1.5f, IsAttachedToUnit = true
                    }]
                }
            };

            var copy = Serializer.DeepClone(source);

            Assert.That(copy.Epoch, Is.EqualTo(7));
            Assert.That(copy.Sequence, Is.EqualTo(12));
            Assert.That(copy.CanonicalStateHash, Is.EqualTo(987654321UL));
            Assert.That(copy.ResponseTimeMs, Is.EqualTo(42));
            Assert.That(copy.MissingReplies, Is.EqualTo(3));
            Assert.That(copy.SessionId, Is.EqualTo("session-a"));
            Assert.That(copy.LoadedSaveSeed, Is.EqualTo(44));
            Assert.That(copy.IncludedEventSequence, Is.EqualTo(9));
            Assert.That(copy.State.Units[0].Id, Is.EqualTo("unit-a"));
            Assert.That(copy.State.Units[0].BlueprintId, Is.EqualTo("unit-blueprint"));
            Assert.That(copy.State.Units[0].OriginalBlueprintId, Is.EqualTo("original-unit"));
            Assert.That(copy.State.Units[0].CasterUnitId, Is.EqualTo("caster"));
            Assert.That(copy.State.Units[0].SourceAbilityBlueprintId, Is.EqualTo("summon-spell"));
            Assert.That(copy.State.Units[0].MasterUnitId, Is.EqualTo("master"));
            Assert.That(copy.State.Units[0].GroupId, Is.EqualTo("summoned-group"));
            Assert.That(copy.State.Units[0].SceneName, Is.EqualTo("combat-scene"));
            Assert.That(copy.State.AreaEffects[0].BlueprintId, Is.EqualTo("area-blueprint"));
            Assert.That(copy.State.AreaEffects[0].CasterUnitId, Is.EqualTo("caster"));
            Assert.That(copy.State.AreaEffects[0].OwnerUnitId, Is.EqualTo("owner"));
            Assert.That(copy.State.AreaEffects[0].SourceAbilityBlueprintId, Is.EqualTo("source-ability"));
            Assert.That(copy.State.AreaEffects[0].CreationGameTimeTicks, Is.EqualTo(123456));
            Assert.That(copy.State.AreaEffects[0].DurationTicks, Is.EqualTo(60000000));
            Assert.That(copy.State.AreaEffects[0].SceneName, Is.EqualTo("combat-scene"));
            Assert.That(copy.State.AreaEffects[0].TargetUnitId, Is.EqualTo("unit-a"));
            Assert.That(copy.State.AreaEffects[0].TargetOrientation, Is.EqualTo(1.5f));
            Assert.That(copy.State.AreaEffects[0].IsAttachedToUnit, Is.True);
        }

        [Test]
        public void IntentAcceptanceRejectionAndPresentation_RoundTripIdentity()
        {
            var intent = Serializer.DeepClone(new ClientRealTimeCombatCommand
            {
                SessionId = "session-a", LoadedSaveSeed = 44, Epoch = 7,
                Sequence = 3, PlayerId = 42, CommandId = "42:7:3", ObservedStateHash = 99
            });
            var accepted = Serializer.DeepClone(new NotifyRealTimeCombatCommandAccepted
            {
                SessionId = "session-a", LoadedSaveSeed = 44, Epoch = 7,
                ClientSequence = 3, CommandId = "42:7:3", AuthoritativeSequence = 8
            });
            var rejected = Serializer.DeepClone(new NotifyRealTimeCombatCommandRejected
            {
                SessionId = "session-a", LoadedSaveSeed = 44, Epoch = 7,
                PlayerId = 42, ClientSequence = 3, CommandId = "42:7:3", Reason = 4
            });
            var presentation = Serializer.DeepClone(new NotifyRealTimeAuthoritativeEvent
            {
                SessionId = "session-a", LoadedSaveSeed = 44, Epoch = 7, Sequence = 5,
                EventId = "7:5", ActionId = "hero:1", UnitId = "hero",
                Kind = (int)RealTimeAuthoritativeEventKind.AttackPresentation,
                Attack = new RealTimeAttackPresentationPayload { Stage = 2 }
            });
            Assert.That(intent.CommandId, Is.EqualTo("42:7:3"));
            Assert.That(intent.ObservedStateHash, Is.EqualTo(99));
            Assert.That(accepted.CommandId, Is.EqualTo(intent.CommandId));
            Assert.That(rejected.CommandId, Is.EqualTo(intent.CommandId));
            Assert.That(rejected.Reason, Is.EqualTo(4));
            Assert.That(presentation.ActionId, Is.EqualTo("hero:1"));
            Assert.That(presentation.EventId, Is.EqualTo("7:5"));
            Assert.That(presentation.Kind, Is.EqualTo((int)RealTimeAuthoritativeEventKind.AttackPresentation));
            Assert.That(presentation.Attack.Stage, Is.EqualTo(2));
        }

        [Test]
        public void StateHashReply_RoundTrips()
        {
            var copy = Serializer.DeepClone(new ClientRealTimeStateHash { Epoch = 7, Sequence = 12, CanonicalStateHash = 123UL, Applied = true });

            Assert.That(copy.Epoch, Is.EqualTo(7));
            Assert.That(copy.Sequence, Is.EqualTo(12));
            Assert.That(copy.CanonicalStateHash, Is.EqualTo(123UL));
            Assert.That(copy.Applied, Is.True);
        }

        [Test]
        public void HostRuleRollAndClientFault_RoundTrip()
        {
            var roll = Serializer.DeepClone(new NotifyRealTimeRuleRoll
            {
                Epoch = 7, Key = "attack:d20", Sequence = 3, Result = 19
            });
            var fault = Serializer.DeepClone(new ClientRealTimeRuleRollFault
            {
                Epoch = 7, Key = "attack:d20", Sequence = 4, Reason = "host-roll-timeout"
            });

            Assert.That((roll.Epoch, roll.Key, roll.Sequence, roll.Result),
                Is.EqualTo((7L, "attack:d20", 3L, 19)));
            Assert.That((fault.Epoch, fault.Key, fault.Sequence, fault.Reason),
                Is.EqualTo((7L, "attack:d20", 4L, "host-roll-timeout")));
        }

        [Test]
        public void AIActionFault_RoundTrips()
        {
            var copy = Serializer.DeepClone(new ClientRealTimeAIActionFault
            {
                Epoch = 7, UnitId = "enemy", ActionId = "spell", Reason = "target-not-found"
            });

            Assert.That((copy.Epoch, copy.UnitId, copy.ActionId, copy.Reason),
                Is.EqualTo((7L, "enemy", "spell", "target-not-found")));
        }

        [Test]
        public void CombatCommandFault_RoundTrips()
        {
            var copy = Serializer.DeepClone(new ClientRealTimeCommandFault
            {
                Epoch = 7, AuthoritativeSequence = 12, Reason = "authoritative-command-rejected"
            });

            Assert.That((copy.Epoch, copy.AuthoritativeSequence, copy.Reason),
                Is.EqualTo((7L, 12L, "authoritative-command-rejected")));
        }

        [Test]
        public void HostEntityIdAndClientFault_RoundTrip()
        {
            var published = Serializer.DeepClone(new NotifyRealTimeEntityId
            {
                Epoch = 4, Key = "area:cloud", Sequence = 2, EntityId = "host-area-id"
            });
            var fault = Serializer.DeepClone(new ClientRealTimeEntityIdFault
            {
                Epoch = 4, Key = "area:cloud", Sequence = 2, Reason = "host-id-timeout"
            });

            Assert.That((published.Epoch, published.Key, published.Sequence, published.EntityId),
                Is.EqualTo((4L, "area:cloud", 2L, "host-area-id")));
            Assert.That((fault.Epoch, fault.Key, fault.Sequence, fault.Reason),
                Is.EqualTo((4L, "area:cloud", 2L, "host-id-timeout")));
        }

        [Test]
        public void StateHashReply_ReportsApplicationFailure()
        {
            var copy = Serializer.DeepClone(new ClientRealTimeStateHash
            {
                Epoch = 7, Sequence = 12, Applied = false, FailureReason = "InvalidOperationException"
            });

            Assert.That(copy.Applied, Is.False);
            Assert.That(copy.FailureReason, Is.EqualTo("InvalidOperationException"));
        }

        [Test]
        public void CombatPreference_RoundTrips()
        {
            var copy = Serializer.DeepClone(new NotifyRealTimeCombatPreference
            {
                Sequence = 3, PreferredMode = 1, PausePolicy = 2
            });

            Assert.That(copy.Sequence, Is.EqualTo(3));
            Assert.That(copy.PreferredMode, Is.EqualTo(1));
            Assert.That(copy.PausePolicy, Is.EqualTo(2));
        }

        [Test]
        public void ClientCommand_RoundTripsMovePayload()
        {
            var source = new ClientRealTimeCombatCommand
            {
                Epoch = 4,
                Sequence = 8,
                PlayerId = 42,
                Kind = 0,
                Movement = new NetworkUnitMoveTo
                {
                    InitiatorUnitId = "unit-a",
                    Destination = new NetworkVector3(1, 2, 3)
                }
            };

            var copy = Serializer.DeepClone(source);

            Assert.That(copy.Epoch, Is.EqualTo(4));
            Assert.That(copy.Sequence, Is.EqualTo(8));
            Assert.That(copy.PlayerId, Is.EqualTo(42));
            Assert.That(copy.Movement.InitiatorUnitId, Is.EqualTo("unit-a"));
            Assert.That(copy.Movement.Destination.Z, Is.EqualTo(3));
        }

        [Test]
        public void AcceptedCommand_RoundTripsAuthoritativeSequence()
        {
            var source = new NotifyRealTimeCombatCommandAccepted
            {
                Epoch = 4,
                AuthoritativeSequence = 12,
                PlayerId = 42,
                ClientSequence = 8,
                Kind = 2,
                Attack = new NetworkUnitAttack { InitiatorUnitId = "unit-a", TargetUnitId = "unit-b" }
            };

            var copy = Serializer.DeepClone(source);

            Assert.That(copy.AuthoritativeSequence, Is.EqualTo(12));
            Assert.That(copy.ClientSequence, Is.EqualTo(8));
            Assert.That(copy.Attack.TargetUnitId, Is.EqualTo("unit-b"));
        }

        [Test]
        public void PauseState_RoundTripsVotes()
        {
            var source = new NotifyRealTimePauseChanged
            {
                Epoch = 2,
                Sequence = 4,
                IsPaused = true,
                Policy = 1,
                PausedPlayers = [42],
                ResumeVotes = [-1]
            };

            var copy = Serializer.DeepClone(source);

            Assert.That(copy.Epoch, Is.EqualTo(2));
            Assert.That(copy.Sequence, Is.EqualTo(4));
            Assert.That(copy.PausedPlayers, Is.EquivalentTo(new long[] { 42 }));
            Assert.That(copy.ResumeVotes, Is.EquivalentTo(new long[] { -1 }));
        }

        [Test]
        public void ModeTransition_RoundTripsCheckpointAndStage()
        {
            var source = new NotifyRealTimeModeTransition
            {
                Epoch = 8,
                Stage = (int)RealTimeModeTransitionStage.Commit,
                Mode = 1,
                CanonicalStateHash = 123456UL,
                State = new NetworkCombatState { Units = [new NetworkUnit { Id = "unit-a" }] }
            };

            var copy = Serializer.DeepClone(source);

            Assert.That(copy.Epoch, Is.EqualTo(8));
            Assert.That(copy.Stage, Is.EqualTo((int)RealTimeModeTransitionStage.Commit));
            Assert.That(copy.CanonicalStateHash, Is.EqualTo(123456UL));
            Assert.That(copy.State.Units[0].Id, Is.EqualTo("unit-a"));
        }

        [Test]
        public void ModeTransitionAck_RoundTripsFailureAndHash()
        {
            var copy = Serializer.DeepClone(new ClientRealTimeModeTransitionAck
            {
                Epoch = 8,
                Stage = (int)RealTimeModeTransitionStage.Abort,
                PlayerId = 42,
                Applied = false,
                CanonicalStateHash = 999UL
            });

            Assert.That(copy.PlayerId, Is.EqualTo(42));
            Assert.That(copy.Applied, Is.False);
            Assert.That(copy.CanonicalStateHash, Is.EqualTo(999UL));
        }

        [Test]
        public void RepairMessages_RoundTripStageStateAndAcknowledgement()
        {
            var instruction = Serializer.DeepClone(new NotifyRealTimeRepair
            {
                Epoch = 9,
                Stage = (int)RealTimeRepairStage.Apply,
                Mode = 1,
                CanonicalStateHash = 77UL,
                State = new NetworkCombatState { Units = [new NetworkUnit { Id = "unit-a" }] }
            });
            var ack = Serializer.DeepClone(new ClientRealTimeRepairAck
            {
                Epoch = 9,
                Stage = (int)RealTimeRepairStage.Apply,
                PlayerId = 42,
                CanonicalStateHash = 77UL,
                Applied = true,
                TemporalMismatch = true
            });

            Assert.That(instruction.State.Units[0].Id, Is.EqualTo("unit-a"));
            Assert.That(instruction.CanonicalStateHash, Is.EqualTo(77UL));
            Assert.That(ack.PlayerId, Is.EqualTo(42));
            Assert.That(ack.Applied, Is.True);
            Assert.That(ack.TemporalMismatch, Is.True);
        }
    }
}
