using System;
using System.Collections.Generic;
using FakeItEasy;
using NUnit.Framework;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Leveling;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.UnitTests.Services
{
    public partial class MultiplayerClientTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async System.Threading.Tasks.Task OwnedConfirmationWaitsForAnAlreadyRunningBackgroundReplay(bool duplicate)
        {
            var host = new NetworkPlayer(-1) { IsHost = true };
            var owner = new NetworkPlayer(42);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "session", LocalPlayerId = 42, Players = [host, owner],
                Characters = [new NetworkCharacter { UnitId = "host-unit", Owner = host },
                    new NetworkCharacter { UnitId = "client-unit", Owner = owner }]
            };
            // The request leaves before the remote replay starts; its reply is still in flight.
            _multiplayerClient.OnRequestLevelingUI("client-unit", NetworkLevelingType.Leveling);
            var queued = new System.Collections.Concurrent.ConcurrentQueue<Action>();
            A.CallTo(() => _mainThreadAccessor.Post(A<Action>._)).Invokes((Action action) => queued.Enqueue(action));
            var calls = new List<string>();
            A.CallTo(() => _levelingInteractionService.IsBackgroundLevelingReady).Returns(false);
            A.CallTo(() => _levelingInteractionService.StartBackgroundLeveling("host-unit", NetworkLevelingType.Leveling))
                .Invokes(() => calls.Add("background-start"));
            A.CallTo(() => _levelingInteractionService.StartLeveling("client-unit", NetworkLevelingType.Leveling))
                .Invokes(() => calls.Add("own-start"));
            A.CallTo(() => _levelingInteractionService.CompleteLeveling()).Invokes(() =>
            {
                calls.Add("background-complete");
                _multiplayerClient.OnLevelingCompleted();
            });
            var remote = LevelingStart("host-unit", NetworkLevelingType.Leveling);
            FakeUtils.GetNetworkReceiverHandler<NotifyLevelingStarted>(_networkClient)(-1, remote);
            FakeUtils.GetNetworkReceiverHandler<NotifyLevelingCompleted>(_networkClient)(-1,
                new NotifyLevelingCompleted { Leveling = remote.Leveling });
            for (var i = 0; i < 200 && !calls.Contains("background-start"); i++)
            {
                if (queued.TryDequeue(out var action)) action();
                await System.Threading.Tasks.Task.Delay(1);
            }
            Assert.That(calls, Does.Contain("background-start"));
            var own = LevelingStart("client-unit", NetworkLevelingType.Leveling);
            FakeUtils.GetNetworkReceiverHandler<NotifyLevelingStarted>(_networkClient)(-1, own);
            while (queued.TryDequeue(out var pending)) pending();
            A.CallTo(() => _levelingInteractionService.IsBackgroundLevelingReady).Returns(true);
            if (duplicate)
            {
                for (var i = 0; i < 200 && !calls.Contains("background-complete"); i++)
                {
                    if (queued.TryDequeue(out var action)) action();
                    await System.Threading.Tasks.Task.Delay(10);
                }
                FakeUtils.GetNetworkReceiverHandler<NotifyLevelingStarted>(_networkClient)(-1, own);
                // Hold the main thread while replay cleanup schedules the owned start
                // behind the already received duplicate confirmation.
                await System.Threading.Tasks.Task.Delay(150);
            }
            for (var i = 0; i < 200; i++)
            {
                while (queued.TryDequeue(out var action)) action();
                if (calls.Count >= 3) break;
                await System.Threading.Tasks.Task.Delay(10);
            }
            Assert.That(calls, Is.EqualTo(new[] { "background-start", "background-complete", "own-start" }));
            Assert.That(_multiplayerClient.Game.Leveling?.Identity.Id, Is.EqualTo(own.Leveling.Id));
            _multiplayerClient.OnLevelingTerminated();
            while (queued.TryDequeue(out var after)) after();
            Assert.That(_multiplayerClient.Game.Leveling, Is.Null, "A duplicate pending start must not reopen the finished UI");
        }

        [Test]
        public void RealDispatcherPreservesBurstOrderAcrossItsFrameBudget()
        {
            var owner = new NetworkPlayer(42);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "session", LocalPlayerId = 42, Players = [new NetworkPlayer(-1) { IsHost = true }, owner],
                Characters = [new NetworkCharacter { UnitId = "client-unit", Owner = owner }]
            };
            var dispatcher = new WOTRMultiplayer.UI.MainThreadAccessor();
            A.CallTo(() => _mainThreadAccessor.Post(A<Action>._)).Invokes((Action action) => dispatcher.Post(action));
            var calls = new List<string>();
            var expected = new List<string> { "start" };
            A.CallTo(() => _levelingInteractionService.StartLeveling("client-unit", NetworkLevelingType.Leveling))
                .Invokes(() => calls.Add("start"));
            A.CallTo(() => _levelingInteractionService.SetLevelingName(A<string>._))
                .Invokes((string name) => calls.Add(name));
            A.CallTo(() => _levelingInteractionService.CompleteLeveling()).Invokes(() =>
            {
                calls.Add("complete");
                _multiplayerClient.OnLevelingCompleted();
            });
            var start = LevelingStart("client-unit", NetworkLevelingType.Leveling);
            FakeUtils.GetNetworkReceiverHandler<NotifyLevelingStarted>(_networkClient)(-1, start);
            for (var i = 0; i < 300; i++)
            {
                var name = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                expected.Add(name);
                FakeUtils.GetNetworkReceiverHandler<NotifyLevelingNameChanged>(_networkClient)(-1,
                    new NotifyLevelingNameChanged { Leveling = start.Leveling, Name = name });
            }
            FakeUtils.GetNetworkReceiverHandler<NotifyLevelingCompleted>(_networkClient)(-1,
                new NotifyLevelingCompleted { Leveling = start.Leveling });
            FakeUtils.GetNetworkReceiverHandler<NotifyLevelingNameChanged>(_networkClient)(-1,
                new NotifyLevelingNameChanged { Leveling = start.Leveling, Name = "after-complete" });
            dispatcher.Drain();
            Assert.That(calls, Is.Empty, "The first frame only validates part of the received burst");
            for (var i = 0; i < 8; i++) dispatcher.Drain();
            expected.Add("complete");
            Assert.That(calls, Is.EqualTo(expected));
            Assert.That(_multiplayerClient.IsSavingBlockedByCombatSync, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OwnedBurstPreservesStartChoicesAndTerminalOrder(bool cancel)
        {
            var owner = new NetworkPlayer(42);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "session", LocalPlayerId = 42, Players = [new NetworkPlayer(-1) { IsHost = true }, owner],
                Characters = [new NetworkCharacter { UnitId = "client-unit", Owner = owner }]
            };
            var queued = new Queue<Action>();
            A.CallTo(() => _mainThreadAccessor.Post(A<Action>._)).Invokes((Action action) => queued.Enqueue(action));
            var calls = new List<string>();
            A.CallTo(() => _levelingInteractionService.StartLeveling("client-unit", NetworkLevelingType.Leveling))
                .Invokes(() => calls.Add("start"));
            A.CallTo(() => _levelingInteractionService.SetLevelingName(A<string>._))
                .Invokes((string name) => calls.Add(name));
            A.CallTo(() => _levelingInteractionService.CompleteLeveling()).Invokes(() =>
            {
                calls.Add("complete");
                _multiplayerClient.OnLevelingCompleted();
            });
            A.CallTo(() => _levelingInteractionService.TerminateLeveling()).Invokes(() =>
            {
                calls.Add("cancel");
                _multiplayerClient.OnLevelingTerminated();
            });
            var start = LevelingStart("client-unit", NetworkLevelingType.Leveling);
            FakeUtils.GetNetworkReceiverHandler<NotifyLevelingStarted>(_networkClient)(-1, start);
            foreach (var name in new[] { "first", "second" })
                FakeUtils.GetNetworkReceiverHandler<NotifyLevelingNameChanged>(_networkClient)(-1,
                    new NotifyLevelingNameChanged { Leveling = start.Leveling, Name = name });
            if (cancel)
                FakeUtils.GetNetworkReceiverHandler<NotifyLevelingTerminated>(_networkClient)(-1,
                    new NotifyLevelingTerminated { Leveling = start.Leveling });
            else
                FakeUtils.GetNetworkReceiverHandler<NotifyLevelingCompleted>(_networkClient)(-1,
                    new NotifyLevelingCompleted { Leveling = start.Leveling });
            while (queued.Count > 0) queued.Dequeue()();
            Assert.That(calls, Is.EqualTo(new[] { "start", "first", "second", cancel ? "cancel" : "complete" }));
            Assert.That(_multiplayerClient.IsSavingBlockedByCombatSync, Is.False);
        }

        [TestCase("session")]
        [TestCase("save")]
        [TestCase("area")]
        public void QueuedProgressionCannotSurviveWorldInvalidation(string invalidate)
        {
            var owner = new NetworkPlayer(42);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "session", LoadedSaveSeed = 17, LocalPlayerId = 42,
                Players = [new NetworkPlayer(-1) { IsHost = true }, owner],
                Characters = [new NetworkCharacter { UnitId = "client-unit", Owner = owner }]
            };
            var queued = new Queue<Action>();
            A.CallTo(() => _mainThreadAccessor.Post(A<Action>._)).Invokes((Action action) => queued.Enqueue(action));
            var start = LevelingStart("client-unit", NetworkLevelingType.Leveling);
            FakeUtils.GetNetworkReceiverHandler<NotifyLevelingStarted>(_networkClient)(-1, start);
            FakeUtils.GetNetworkReceiverHandler<NotifyLevelingNameChanged>(_networkClient)(-1,
                new NotifyLevelingNameChanged { Leveling = start.Leveling, Name = "obsolete" });
            if (invalidate == "session")
                _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp()) { Id = "session", LoadedSaveSeed = 17 };
            else if (invalidate == "save") _multiplayerClient.Game.LoadedSaveSeed = 18;
            else typeof(WOTRMultiplayer.Services.MultiplayerActorBase)
                .GetMethod("SoftReset", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(_multiplayerClient, null);
            while (queued.Count > 0) queued.Dequeue()();
            Assert.That(_multiplayerClient.Game.Leveling, Is.Null);
            A.CallTo(() => _levelingInteractionService.StartLeveling(A<string>._, A<NetworkLevelingType>._)).MustNotHaveHappened();
            A.CallTo(() => _levelingInteractionService.SetLevelingName(A<string>._)).MustNotHaveHappened();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnknownChoiceBeforeStartIsStillRejected(bool choiceBeforeStart)
        {
            var owner = new NetworkPlayer(42);
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "session", LocalPlayerId = 42, Players = [new NetworkPlayer(-1) { IsHost = true }, owner],
                Characters = [new NetworkCharacter { UnitId = "client-unit", Owner = owner }]
            };
            var queued = new Queue<Action>();
            A.CallTo(() => _mainThreadAccessor.Post(A<Action>._)).Invokes((Action action) => queued.Enqueue(action));
            var start = LevelingStart("client-unit", NetworkLevelingType.Leveling);
            var choice = new NotifyLevelingNameChanged { Leveling = start.Leveling, Name = "choice" };
            if (choiceBeforeStart) FakeUtils.GetNetworkReceiverHandler<NotifyLevelingNameChanged>(_networkClient)(-1, choice);
            FakeUtils.GetNetworkReceiverHandler<NotifyLevelingStarted>(_networkClient)(-1, start);
            if (!choiceBeforeStart) FakeUtils.GetNetworkReceiverHandler<NotifyLevelingNameChanged>(_networkClient)(-1, choice);
            while (queued.Count > 0) queued.Dequeue()();
            if (choiceBeforeStart)
                A.CallTo(() => _levelingInteractionService.SetLevelingName("choice")).MustNotHaveHappened();
            else A.CallTo(() => _levelingInteractionService.SetLevelingName("choice")).MustHaveHappenedOnceExactly();
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async System.Threading.Tasks.Task CompletionMustSurviveQueuedStart(bool delayStart, bool cancel)
        {
            var host = new NetworkPlayer(-1) { IsHost = true };
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "session", LocalPlayerId = 42, Players = [host, new NetworkPlayer(42)],
                Characters = [new NetworkCharacter { UnitId = "host-unit", Owner = host }]
            };
            A.CallTo(() => _levelingInteractionService.IsBackgroundLevelingReady).Returns(true);
            A.CallTo(() => _levelingInteractionService.CompleteLeveling()).Invokes(() => _multiplayerClient.OnLevelingCompleted());
            A.CallTo(() => _levelingInteractionService.TerminateLeveling()).Invokes(() => _multiplayerClient.OnLevelingTerminated());
            var queued = new System.Collections.Concurrent.ConcurrentQueue<Action>();
            A.CallTo(() => _mainThreadAccessor.Post(A<Action>._)).Invokes((Action action) => queued.Enqueue(action));
            var start = LevelingStart("host-unit", NetworkLevelingType.Leveling);
            FakeUtils.GetNetworkReceiverHandler<NotifyLevelingStarted>(_networkClient)(-1, start);
            if (!delayStart) while (queued.TryDequeue(out var action)) action();
            if (cancel)
                FakeUtils.GetNetworkReceiverHandler<NotifyLevelingTerminated>(_networkClient)(-1,
                    new NotifyLevelingTerminated { Leveling = start.Leveling });
            else
                FakeUtils.GetNetworkReceiverHandler<NotifyLevelingCompleted>(_networkClient)(-1,
                    new NotifyLevelingCompleted { Leveling = start.Leveling });
            for (var i = 0; i < 100; i++)
            {
                while (queued.TryDequeue(out var action)) action();
                await System.Threading.Tasks.Task.Delay(10);
            }
            Assert.That(_multiplayerClient.IsSavingBlockedByCombatSync, Is.False,
                "A valid completion delivered after Start must not disappear while Start awaits the game thread");
            if (cancel) A.CallTo(() => _levelingInteractionService.TerminateLeveling()).MustHaveHappenedOnceExactly();
            else A.CallTo(() => _levelingInteractionService.CompleteLeveling()).MustHaveHappenedOnceExactly();
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void ChoicesMustSurviveQueuedStart(bool nativeFirst, bool delayStart)
        {
            var host = new NetworkPlayer(-1) { IsHost = true };
            _multiplayerClient.Game = new NetworkGame(new NetworkGameStartUp())
            {
                Id = "session", LocalPlayerId = 42, Players = [host, new NetworkPlayer(42)],
                Characters = [new NetworkCharacter { UnitId = "host-unit", Owner = host }]
            };
            var start = LevelingStart("host-unit", NetworkLevelingType.MythicLeveling);
            start.IsForced = true;
            if (nativeFirst) _multiplayerClient.OnForceLevelingUI("host-unit", NetworkLevelingType.MythicLeveling);
            var queued = new Queue<Action>();
            A.CallTo(() => _mainThreadAccessor.Post(A<Action>._)).Invokes((Action action) => queued.Enqueue(action));
            FakeUtils.GetNetworkReceiverHandler<NotifyLevelingStarted>(_networkClient)(-1, start);
            if (!delayStart) while (queued.Count > 0) queued.Dequeue()();
            FakeUtils.GetNetworkReceiverHandler<NotifyLevelingNameChanged>(_networkClient)(-1,
                new NotifyLevelingNameChanged { Leveling = start.Leveling, Name = "host-choice" });
            while (queued.Count > 0) queued.Dequeue()();
            if (!nativeFirst) _multiplayerClient.OnForceLevelingUI("host-unit", NetworkLevelingType.MythicLeveling);
            while (queued.Count > 0) queued.Dequeue()();
            A.CallTo(() => _levelingInteractionService.SetLevelingName("host-choice")).MustHaveHappenedOnceExactly();
        }
    }
}
