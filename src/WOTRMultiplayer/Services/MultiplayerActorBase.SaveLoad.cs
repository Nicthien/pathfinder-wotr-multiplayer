using Microsoft.Extensions.Logging;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.Entities.Combat.RealTime;

namespace WOTRMultiplayer.Services
{
    public abstract partial class MultiplayerActorBase
    {
        private NetworkGame _saveLoadSession;
        private int _saveLoadSeed;
        private bool _nativeSaveLoadStarted;
        private bool _nativeSaveLoadAdmitted;

        internal bool IsSaveLoadPending => Game != null && ReferenceEquals(Game, _saveLoadSession)
            && Game.LoadedSaveSeed == _saveLoadSeed;
        internal bool HoldsWorldBeforeSaveLoad => IsSaveLoadPending && !_nativeSaveLoadStarted;

        // Revoke the old encounter before transfer/unload, not in SoftReset:
        // Owlcat hydrates native stats before OnAreaLoadingComplete calls it.
        internal void BeginSaveLoad()
        {
            if (IsSaveLoadPending) return;
            var previous = Game.RealTimeCombat;
            _saveLoadSession = Game;
            _saveLoadSeed = Game.LoadedSaveSeed;
            _nativeSaveLoadStarted = false;
            _nativeSaveLoadAdmitted = false;
            Game.Combat = null;
            Game.LastCombatTurn = null;
            Game.RealTimeCombat = new NetworkRealTimeCombatState
            {
                PreferredMode = previous.PreferredMode,
                PausePolicy = previous.PausePolicy,
                PreferenceSequence = previous.PreferenceSequence,
                Epoch = checked(previous.Epoch + 1)
            };
            ResetRealTimeRuleRolls();
            SafeFireAndForget(() => GameInteraction.SetPauseAsync(true));
            Logger.LogInformation("[SaveLoad] Previous combat context revoked before world replacement. SaveSeed={SaveSeed}", _saveLoadSeed);
        }

        private void CompleteSaveLoad()
        {
            if (!IsSaveLoadPending || !_nativeSaveLoadStarted) return;
            _saveLoadSession = null;
            Logger.LogInformation("[SaveLoad] Native load lifecycle completed for the replacement world. SaveSeed={SaveSeed}", Game.LoadedSaveSeed);
        }
    }
}
