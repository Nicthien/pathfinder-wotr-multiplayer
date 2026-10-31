using System;
using FakeItEasy;
using NUnit.Framework;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Dialogs;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Messages.Game;

namespace WOTRMultiplayer.UnitTests.Services
{
    public partial class MultiplayerHostTests
    {
        private NetworkDialogState PrepareControlledDialog(bool systemAnswer = false)
        {
            var host = new NetworkPlayer(-1) { IsHost = true, LobbySyncStatus = NetworkLobbySyncStatus.Succeed };
            var client = new NetworkPlayer(42) { LobbySyncStatus = NetworkLobbySyncStatus.Succeed };
            var state = new NetworkDialogState(new NetworkDialog { Id = "dialog", Name = "dialog" })
                { CurrentCueName = "cue", VoteRoundId = "round", HasSystemAnswer = systemAnswer, VoteParticipants = [-1, 42] };
            state.CueViews["cue"] = [-1, 42];
            _multiplayerHost.Game = new NetworkGame(new NetworkGameStartUp())
                { LocalPlayerId = -1, Players = [host, client], DialogState = state };
            _multiplayerHost.Game.FeatureControllers[NetworkPlayerControlledFeature.Dialogs] = 42;
            A.CallTo(() => _mainThreadAccessor.Post(A<Action>._)).Invokes((Action action) => action());
            return state;
        }

        [Test]
        public void ContinueWaitsForEveryCueWitnessBeforeEngineExecution()
        {
            var state = PrepareControlledDialog(systemAnswer: true);
            _multiplayerHost.Game.FeatureControllers[NetworkPlayerControlledFeature.Dialogs] = -1;
            state.CueViews["cue"].Remove(42);
            Assert.That(_multiplayerHost.OnBeforeSelectDialogAnswer(state.Dialog, "cue", "continue", false, null), Is.False);
            Assert.That(state.Answer, Is.Null);
            A.CallTo(() => _networkServer.Broadcast(A<NotifyDialogCueAnswerSelected>._)).MustNotHaveHappened();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ClientControlledChoiceExecutesOnHostOnlyAfterBothVotes(bool clientFirst)
        {
            var state = PrepareControlledDialog();
            if (!clientFirst) state.AnswerSuggestions[-1] = "choice";
            var applied = false;
            A.CallTo(() => _dialogInteractionService.SelectDialogAnswer("choice", null, A<Func<bool>>._))
                .Invokes((string answer, string unit, Func<bool> authorized) =>
                {
                    Assert.That(authorized(), Is.True);
                    applied = _multiplayerHost.OnBeforeSelectDialogAnswer(state.Dialog, "cue", answer, false, unit);
                });
            FakeUtils.GetNetworkReceiverHandler<ClientDialogCueAnswerSelected>(_networkServer)(42,
                new ClientDialogCueAnswerSelected
                {
                    Dialog = _mapper.Map<Networking.Messages.Contracts.NetworkDialog>(state.Dialog),
                    CueName = "cue", AnswerName = "choice", VoteRoundId = "round"
                });
            if (clientFirst)
            {
                Assert.That(applied, Is.False);
                applied = _multiplayerHost.OnBeforeSelectDialogAnswer(state.Dialog, "cue", "choice", false, null);
            }
            Assert.That(applied, Is.True);
            Assert.That(state.VoteCommitted, Is.True);
            A.CallTo(() => _networkServer.Broadcast(A<NotifyDialogCueAnswerSelected>._)).MustNotHaveHappened();
            _multiplayerHost.OnAfterPlayDialogCue();
            A.CallTo(() => _networkServer.Broadcast(A<NotifyDialogCueAnswerSelected>.That.Matches(message =>
                message.AnswerName == "choice" && message.VoteRoundId == "round"))).MustHaveHappenedOnceExactly();
        }

        [TestCase(99, "round")]
        [TestCase(42, "old-round")]
        public void RemoteSelectionRejectsWrongControllerAndStaleRound(long sender, string round)
        {
            var state = PrepareControlledDialog();
            state.AnswerSuggestions[-1] = "choice";
            FakeUtils.GetNetworkReceiverHandler<ClientDialogCueAnswerSelected>(_networkServer)(sender,
                new ClientDialogCueAnswerSelected
                {
                    Dialog = _mapper.Map<Networking.Messages.Contracts.NetworkDialog>(state.Dialog),
                    CueName = "cue", AnswerName = "choice", VoteRoundId = round
                });
            Assert.That(state.Answer, Is.Null);
            Assert.That(state.IsSelectingAnswer, Is.False);
            A.CallTo(() => _dialogInteractionService.SelectDialogAnswer(A<string>._, A<string>._, A<Func<bool>>._))
                .MustNotHaveHappened();
            A.CallTo(() => _networkServer.Broadcast(A<NotifyDialogCueAnswerSelected>._)).MustNotHaveHappened();
        }
    }
}
