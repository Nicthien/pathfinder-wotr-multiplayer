using System;
using System.Linq;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Stats;
using Kingmaker.UI.MVVM._PCView.CharGen.Phases;
using Kingmaker.UI.MVVM._PCView.CharGen.Phases.AbilityScores;
using Kingmaker.UI.MVVM._PCView.CharGen.Phases.Alignment;
using Kingmaker.UI.MVVM._PCView.CharGen.Phases.Appearance;
using Kingmaker.UI.MVVM._PCView.CharGen.Phases.Class;
using Kingmaker.UI.MVVM._PCView.CharGen.Phases.FeatureSelector;
using Kingmaker.UI.MVVM._PCView.CharGen.Phases.Mythic;
using Kingmaker.UI.MVVM._PCView.CharGen.Phases.Name;
using Kingmaker.UI.MVVM._PCView.CharGen.Phases.Portrait;
using Kingmaker.UI.MVVM._PCView.CharGen.Phases.Race;
using Kingmaker.UI.MVVM._PCView.CharGen.Phases.Skills;
using Kingmaker.UI.MVVM._PCView.CharGen.Phases.Spells;
using Kingmaker.UI.MVVM._PCView.CharGen.Phases.Voice;
using Kingmaker.UI.MVVM._VM.CharGen;
using Kingmaker.UI.MVVM._VM.CharGen.Phases;
using Kingmaker.UI.MVVM._VM.CharGen.Phases.Class;
using Kingmaker.UI.MVVM._VM.CharGen.Phases.Common;
using Kingmaker.UI.MVVM._VM.CharGen.Phases.FeatureSelector;
using Kingmaker.UI.MVVM._VM.CharGen.Phases.Mythic;
using Kingmaker.UI.MVVM._VM.CharGen.Phases.Portrait;
using Kingmaker.UI.MVVM._VM.CharGen.Phases.Spells;
using Kingmaker.UnitLogic.Class.LevelUp;
using Microsoft.Extensions.Logging;
using Owlcat.Runtime.UI.SelectionGroup;
using WOTRMultiplayer.Abstractions.GameInteraction;
using WOTRMultiplayer.Abstractions.Unity;
using WOTRMultiplayer.Entities.Leveling;

namespace WOTRMultiplayer.Services.GameInteraction
{
    public class LevelingInteractionService : ILevelingInteractionService
    {
        private readonly ILogger<LevelingInteractionService> _logger;
        private readonly IMainThreadAccessor _mainThreadAccessor;
        private readonly IUIAccessor _uiAccessor;
        private readonly IPlayerNotificationService _playerNotificationService;
        private readonly IGameStateLookupService _gameStateLookupService;
        private readonly IUISyncCountersService _uiSyncCountersService;
        private CharGenVM _backgroundCharGen;
        private LevelUpController _backgroundLevelUpController;
        private Func<bool> _backgroundAuthorization;
        [ThreadStatic] private static Func<bool> _replayAuthorization;
        [ThreadStatic] private static Action<Exception> _replayFailure;

        public IDisposable EnterReplayScope(Func<bool> authorized)
            => EnterReplayScope(authorized, null);

        public IDisposable EnterReplayScope(Func<bool> authorized, Action<Exception> onFailure)
        {
            var previous = _replayAuthorization;
            var previousFailure = _replayFailure;
            _replayAuthorization = authorized;
            _replayFailure = onFailure;
            return new ReplayScope(() => { _replayAuthorization = previous; _replayFailure = previousFailure; });
        }

        private sealed class ReplayScope(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }

        private static void RejectGuardedApplication(string reason)
        {
            // A missing target is an application failure too, even when legacy
            // unscoped UI callers only logged it and returned.
            if (_replayFailure != null) throw new InvalidOperationException(reason);
        }

        private void Post(Action action)
        {
            var authorized = _replayAuthorization;
            var onFailure = _replayFailure;
            _mainThreadAccessor.Post(() =>
            {
                if (authorized != null && !authorized()) return;
                try
                {
                    if (_backgroundAuthorization != null && !_backgroundAuthorization())
                        FinishBackgroundLeveling(commit: false);
                    using (EnterReplayScope(authorized, onFailure)) action();
                }
                catch (Exception error)
                {
                    // Once application started, even a callback that cleared its
                    // current progression may have failed after partial mutation.
                    onFailure?.Invoke(error);
                    throw;
                }
            });
        }

        public bool IsLevelingUIReady => _backgroundCharGen != null || _uiAccessor.CharGenView?.ViewModel != null;
        public bool IsBackgroundLevelingReady => _backgroundCharGen != null;

        public LevelingInteractionService(
            ILogger<LevelingInteractionService> logger,
            IMainThreadAccessor mainThreadAccessor,
            IUIAccessor uiAccessor,
            IPlayerNotificationService playerNotificationService,
            IGameStateLookupService gameStateLookupService,
            IUISyncCountersService uiSyncCountersService)
        {
            _logger = logger;
            _mainThreadAccessor = mainThreadAccessor;
            _uiAccessor = uiAccessor;
            _playerNotificationService = playerNotificationService;
            _gameStateLookupService = gameStateLookupService;
            _uiSyncCountersService = uiSyncCountersService;
        }

        public void StartLeveling(string unitId, NetworkLevelingType levelingType)
        {
            Post(() =>
            {
                try
                {
                    var unit = _gameStateLookupService.GetUnitEntity(unitId);
                    if (unit == null)
                    {
                        _logger.LogError("Unable to start leveling due to missing unit. UnitId={UnitId}, Type={Type}", unitId, levelingType);
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    var mode = levelingType switch
                    {
                        NetworkLevelingType.Leveling when unit.Progression.CharacterLevel <= 0 => LevelUpState.CharBuildMode.CharGen,
                        NetworkLevelingType.Leveling => LevelUpState.CharBuildMode.LevelUp,
                        _ or NetworkLevelingType.MythicLeveling => LevelUpState.CharBuildMode.Mythic,
                    };

                    LevelUpConfig.Create(unit, mode).OpenUI();
                    _logger.LogInformation("Started leveling. UnitId={UnitId}, Mode={Mode}, LevelingType={LevelingType}", unitId, mode, levelingType);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while starting leveling process. UnitId={UnitId}", unitId);
                    throw;
                }
            });
        }

        public void StartBackgroundLeveling(string unitId, NetworkLevelingType levelingType)
        {
            Post(() =>
            {
                try
                {
                    if (_backgroundCharGen != null)
                        throw new InvalidOperationException("background-leveling-already-active");
                    var unit = _gameStateLookupService.GetUnitEntity(unitId);
                    if (unit == null)
                        throw new InvalidOperationException($"background-leveling-unit-missing:{unitId}");
                    var mode = levelingType switch
                    {
                        NetworkLevelingType.Leveling when unit.Progression.CharacterLevel <= 0 => LevelUpState.CharBuildMode.CharGen,
                        NetworkLevelingType.Leveling => LevelUpState.CharBuildMode.LevelUp,
                        _ or NetworkLevelingType.MythicLeveling => LevelUpState.CharBuildMode.Mythic,
                    };
                    var config = LevelUpConfig.Create(unit, mode);
                    _backgroundAuthorization = _replayAuthorization;
                    _backgroundLevelUpController = config.StartController();
                    using (_backgroundLevelUpController.EnterApplyingPlanScope())
                    {
                        _backgroundCharGen = new CharGenVM(
                            _backgroundLevelUpController,
                            () => FinishBackgroundLeveling(commit: false),
                            () => FinishBackgroundLeveling(commit: true),
                            config);
                    }
                    _logger.LogInformation("Started headless remote leveling. UnitId={UnitId}, Mode={Mode}", unitId, mode);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while starting headless remote leveling. UnitId={UnitId}", unitId);
                    FinishBackgroundLeveling(commit: false);
                    throw;
                }
            });
        }

        private void FinishBackgroundLeveling(bool commit)
        {
            var viewModel = _backgroundCharGen;
            var controller = _backgroundLevelUpController;
            _backgroundCharGen = null;
            _backgroundLevelUpController = null;
            _backgroundAuthorization = null;
            try
            {
                if (commit) controller?.Commit();
                controller?.Stop();
            }
            finally
            {
                viewModel?.Dispose();
            }
        }

        public void SelectLevelingClassArchetype(NetworkLevelingArchetype levelingArchetype)
        {
            Post(() =>
            {
                try
                {
                    var viewModel = GetLevelingPhaseViewModel();
                    if (viewModel == null)
                    {
                        _logger.LogWarning("Unable to select leveling class archetype due to missing viewmodel");
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    if (levelingArchetype == null)
                    {
                        viewModel.SelectedClassVM.Value.TryUnselectArchetypes();
                        viewModel.OnSelectorArchetypeChanged(null);
                        _logger.LogInformation("Leveling archetype has been deselected");
                        return;
                    }

                    if (viewModel.SelectedClassVM.Value == null)
                    {
                        _logger.LogWarning("Class must be selected to select archetype");
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    var archetypes = viewModel.SelectedClassVM.Value.GetArchetypesList(viewModel.SelectedClassVM.Value.Class).Cast<CharGenClassSelectorItemVM>().ToList();
                    var archetype = archetypes.FirstOrDefault(c => string.Equals(c.Archetype.AssetGuid.ToString(), levelingArchetype.Id, StringComparison.OrdinalIgnoreCase));
                    if (archetype == null)
                    {
                        _playerNotificationService.ShowWarningNotification(WellKnownKeys.GameNotifications.Leveling.ArchetypeMismatch.Key, args: levelingArchetype.Name);
                        RejectGuardedApplication("leveling-archetype-missing");
                        return;
                    }

                    archetype.IsSelected.Value = true;
                    viewModel.SelectedClassVM.Value.SelectedArchetype.Value = archetype;
                    viewModel.SelectedArchetypeVM.Value = archetype;
                    viewModel.LastSelectedArchetypeVM = archetype;
                    viewModel.OnSelectorArchetypeChanged(archetype.Archetype);
                    _logger.LogInformation("Leveling archetype has been set. ClassName={ClassName}, ArchetypeId={ArchetypeId}", viewModel.SelectedClassVM.Value.Class.NameForAcronym, viewModel.SelectedClassVM.Value.SelectedArchetype.Value?.Archetype?.NameForAcronym);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while selecting leveling class archetype. ArchetypeId={ArchetypeId}", levelingArchetype?.Id);
                    throw;
                }
            });
        }

        public void SelectMythicLevelingClass(string mythicClassId)
        {
            Post(() =>
            {
                try
                {
                    var viewModel = _backgroundCharGen?.CurrentPhaseVM.Value as CharGenMythicPhaseVM
                        ?? (_uiAccessor.CharGenView?.SelectedDetailView as CharGenMythicPhaseDetailedPCView)?.ViewModel;
                    if (viewModel == null)
                    {
                        _logger.LogError("Can't select mythic class due to missing mythic leveling phase viewmodel");
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    var selectedMythicClass = viewModel.m_MythicVMs.FirstOrDefault(c => string.Equals(c.Class.AssetGuid.ToString(), mythicClassId, StringComparison.OrdinalIgnoreCase));
                    viewModel.SelectedMythicVM.Value = selectedMythicClass;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while selecting mythic leveling class. ClassId={ClassId}", mythicClassId);
                    throw;
                }
            });
        }
        public void SelectLevelingPortrait(NetworkLevelingPortrait levelingPortrait)
        {
            Post(() =>
            {
                try
                {
                    if (_uiAccessor.CharGenView?.ViewModel == null)
                    {
                        _logger.LogWarning("Can't select leveling portrait due to missing CharGenView");
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    var viewModel = (_uiAccessor.CharGenView.SelectedDetailView as CharGenPortraitPhaseDetailedPCView)?.ViewModel;
                    if (viewModel == null)
                    {
                        _logger.LogError("Can't select leveling portrait due to missing portrait phase viewmodel");
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    Enum.TryParse<Kingmaker.Enums.PortraitCategory>(levelingPortrait.Category, true, out var category);
                    if (string.IsNullOrEmpty(levelingPortrait.CustomId))
                    {
                        if (!viewModel.PortraitGroupVms.TryGetValue(category, out var group))
                        {
                            _logger.LogError("Unable to find requested portrait category. Name={Name}, Category={Category}", levelingPortrait.Name, category);
                            RejectGuardedApplication("leveling-application-incomplete");
                            return;
                        }

                        var portrait = group.PortraitCollection.FirstOrDefault(p => string.Equals(p.PortraitData.SmallPortrait.name, levelingPortrait.Name, StringComparison.OrdinalIgnoreCase));
                        if (portrait == null)
                        {
                            _logger.LogError("Unable to find requested portrait. Name={Name}, Category={Category}", levelingPortrait.Name, levelingPortrait.Category);
                            RejectGuardedApplication("leveling-application-incomplete");
                            return;
                        }

                        viewModel.SelectedPortrait.Value = portrait;
                        var defaultTab = viewModel.TabSelector.EntitiesCollection.FirstOrDefault(e => e.Tab == CharGenPortraitTab.Default);
                        viewModel.CurrentTab.Value = defaultTab;
                        _logger.LogInformation("Leveling portrait has been selected. Name={Name}, CustomId={CustomId}, Category={Category}", viewModel.SelectedPortrait.Value.PortraitData.SmallPortrait.name, viewModel.SelectedPortrait.Value.PortraitData.CustomId, viewModel.SelectedPortrait.Value.PortraitData.PortraitCategory);
                        return;
                    }

                    var customPortraitVM = viewModel.CustomPortraitGroup.PortraitCollection.FirstOrDefault(x => string.Equals(x.PortraitData?.CustomId, levelingPortrait.CustomId, StringComparison.OrdinalIgnoreCase));
                    if (customPortraitVM == null)
                    {
                        // CustomPortraitsManager.Instance.CreateNew as a reference
                        var portraitData = new PortraitData(levelingPortrait.CustomId);
                        CustomPortraitsManager.Instance.EnsureDirectory(portraitData.CustomId, true);
                        CustomPortraitsManager.Instance.EnsureCustomPortraits(portraitData.CustomId);
                        portraitData.EnsureImages();
                        customPortraitVM = new CharGenPortraitSelectorItemVM(portraitData);
                        viewModel.AllPortraitsCollection.Add(customPortraitVM);
                        viewModel.CustomPortraitGroup.Add(customPortraitVM);
                        _logger.LogInformation("Custom leveling portrait has been created. Name={Name}, CustomId={CustomId}, Category={Category}", portraitData.SmallPortrait.name, portraitData.CustomId, portraitData.PortraitCategory);
                    }

                    viewModel.SelectedPortrait.Value = customPortraitVM;
                    var customTab = viewModel.TabSelector.EntitiesCollection.FirstOrDefault(e => e.Tab == CharGenPortraitTab.Custom);
                    viewModel.CurrentTab.Value = customTab;
                    _logger.LogInformation("Custom leveling portrait has been selected. Name={Name}, CustomId={CustomId}, Category={Category}", viewModel.SelectedPortrait.Value.PortraitData.SmallPortrait.name, viewModel.SelectedPortrait.Value.PortraitData.CustomId, viewModel.SelectedPortrait.Value.PortraitData.PortraitCategory);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while selecting leveling portrait. Name={Name}, CustomId={CustomId}, Category={Category}", levelingPortrait.Name, levelingPortrait.CustomId, levelingPortrait.Category);
                    throw;
                }
            });
        }

        public void SelectLevelingVoice(NetworkLevelingVoice levelingVoice)
        {
            Post(() =>
            {
                try
                {
                    if (_uiAccessor.CharGenView?.ViewModel == null)
                    {
                        _logger.LogWarning("Can't select leveling voice due to missing CharGenView");
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    var view = _uiAccessor.CharGenView.SelectedDetailView as CharGenVoicePhaseDetailedPCView;
                    if (view == null)
                    {
                        _logger.LogError("Can't select leveling voice due to missing voice phase view");
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    var voice = view.ViewModel.VoiceSelector.EntitiesCollection
                        .FirstOrDefault(e => string.Equals(e.Gender.ToString(), levelingVoice.GenderId, StringComparison.OrdinalIgnoreCase) && string.Equals(e.Voice.AssetGuid.ToString(), levelingVoice.Id, StringComparison.OrdinalIgnoreCase));

                    if (voice == null)
                    {
                        _logger.LogError("Unable to find leveling voice. Id={Id}, GenderId={GenderId}", levelingVoice.Id, levelingVoice.GenderId);
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    view.ViewModel.SelectedVoiceVM.Value = voice;
                    view.VoiceSelectorPc.Gender.Value = view.ViewModel.SelectedVoiceVM.Value.Gender;
                    view.PlayVoicePreview(view.ViewModel.Barks.Value);
                    _logger.LogInformation("Leveling voice has been selected. Id={Id}, Gender={Gender}", view.ViewModel.SelectedVoiceVM.Value.Voice.AssetGuid.ToString(), view.ViewModel.SelectedVoiceVM.Value.Gender);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while selecting leveling voice. Id={Id}, GenderId={GenderId}", levelingVoice.Id, levelingVoice.GenderId);
                    throw;
                }
            });
        }

        public void SelectLevelingGender(string genderId)
        {
            Post(() =>
            {
                try
                {
                    if (_uiAccessor.CharGenView?.ViewModel == null)
                    {
                        _logger.LogWarning("Can't select leveling gender due to missing CharGenView");
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    var viewModel = (_uiAccessor.CharGenView.SelectedDetailView as CharGenRacePhaseDetailedPCView)?.ViewModel;
                    if (viewModel == null)
                    {
                        _logger.LogError("Can't select leveling gender due to missing race phase viewmodel");
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    var gender = viewModel.m_Genders.FirstOrDefault(g => string.Equals(g.Gender.ToString(), genderId, StringComparison.OrdinalIgnoreCase));
                    if (gender == null)
                    {
                        _logger.LogError("Unable to find leveling gender. GenderId={GenderId}", genderId);
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    viewModel.SelectedGenderVM.Value = gender;
                    _logger.LogInformation("Leveling gender has been selected. Gender={Gender}", viewModel.SelectedGenderVM.Value.Gender);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while selecting leveling gender. GenderId={GenderId}", genderId);
                    throw;
                }
            });
        }

        public void SelectLevelingRace(string raceId)
        {
            Post(() =>
            {
                try
                {
                    if (_uiAccessor.CharGenView?.ViewModel == null)
                    {
                        _logger.LogWarning("Can't select leveling race due to missing CharGenView");
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    var viewModel = (_uiAccessor.CharGenView.SelectedDetailView as CharGenRacePhaseDetailedPCView)?.ViewModel;
                    if (viewModel == null)
                    {
                        _logger.LogError("Can't select leveling race due to missing race phase viewmodel");
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    var race = viewModel.m_RacesVMs.FirstOrDefault(r => string.Equals(r.Race.AssetGuid.ToString(), raceId, StringComparison.OrdinalIgnoreCase));
                    if (race == null)
                    {
                        _logger.LogError("Unable to find leveling race. RaceId={RaceId}", raceId);
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    viewModel.SelectedRaceVM.Value = race;
                    _logger.LogInformation("Leveling race has been selected. Race={Race}, RaceId={RaceId}", viewModel.SelectedRaceVM.Value.Race.name, viewModel.SelectedRaceVM.Value.Race.AssetGuid.ToString());
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while selecting leveling race. RaceId={RaceId}", raceId);
                    throw;
                }
            });
        }

        public void SelectLevelingWarpaintColorAppearance(NetworkLevelingWarpaint levelingWarpaint)
        {
            Post(() =>
            {
                var view = GetCharGenAppearancePhaseView();
                if (view == null)
                {
                    return;
                }

                var warpaint = view.ViewModel.WarpaintsColorSelectorVMList[levelingWarpaint.PageNumber];
                SelectAppearanceTexture(warpaint, "warpaint-color", levelingWarpaint.TextureName);
                view.m_WarpaintPaginator.OnClickPage(levelingWarpaint.PageNumber);
                view.ViewModel.RefreshView.Execute();
            });
        }

        public void SelectLevelingWarpaintAppearance(NetworkLevelingWarpaint levelingWarpaint)
        {
            Post(() =>
            {
                var view = GetCharGenAppearancePhaseView();
                if (view == null)
                {
                    return;
                }

                var warpaint = view.ViewModel.WarpaintsSelectorVMList[levelingWarpaint.PageNumber];
                SelectAppearanceSlider(warpaint, levelingWarpaint.Index);
                view.m_WarpaintPaginator.OnClickPage(levelingWarpaint.PageNumber);
                view.ViewModel.RefreshView.Execute();
            });
        }

        public void SelectLevelingTattooColorAppearance(NetworkLevelingTattoo levelingTattoo)
        {
            Post(() =>
            {
                var view = GetCharGenAppearancePhaseView();
                if (view == null)
                {
                    return;
                }

                var tattoo = view.ViewModel.TattoosColorSelectorVMList[levelingTattoo.PageNumber];
                SelectAppearanceTexture(tattoo, "tattoo-color", levelingTattoo.TextureName);
                view.m_TatooPaginator.OnClickPage(levelingTattoo.PageNumber);
                view.ViewModel.RefreshView.Execute();
            });
        }

        public void SelectLevelingTattooAppearance(NetworkLevelingTattoo levelingTattoo)
        {
            Post(() =>
            {
                var view = GetCharGenAppearancePhaseView();
                if (view == null)
                {
                    return;
                }

                var tattoo = view.ViewModel.TattoosSelectorVMList[levelingTattoo.PageNumber];
                SelectAppearanceSlider(tattoo, levelingTattoo.Index);
                view.m_TatooPaginator.OnClickPage(levelingTattoo.PageNumber);
                view.ViewModel.RefreshView.Execute();
            });
        }

        public void SelectLevelingScarAppearance(int index)
        {
            Post(() =>
            {
                var view = GetCharGenAppearancePhaseView();
                if (view == null)
                {
                    return;
                }

                SelectAppearanceSlider(view.m_ScarSelectorPcView.ViewModel, index);
            });
        }

        public void SelectLevelingSecondaryOutfitColorAppearance(string textureName)
        {
            Post(() =>
            {
                var view = GetCharGenAppearancePhaseView();
                if (view == null)
                {
                    return;
                }

                SelectAppearanceTexture(view.m_SecondaryOutfitColorSelectorView.ViewModel, "secondary-outfit-color", textureName);
                view.ViewModel.RefreshView.Execute();
            });
        }

        public void SelectLevelingPrimaryOutfitColorAppearance(string textureName)
        {
            Post(() =>
            {
                var view = GetCharGenAppearancePhaseView();
                if (view == null)
                {
                    return;
                }

                SelectAppearanceTexture(view.m_PrimaryOutfitColorSelectorView.ViewModel, "primary-outfit-color", textureName);
                view.ViewModel.RefreshView.Execute();
            });
        }

        public void SelectLevelingHornsColorAppearance(string textureName)
        {
            Post(() =>
            {
                var view = GetCharGenAppearancePhaseView();
                if (view == null)
                {
                    return;
                }

                SelectAppearanceTexture(view.m_HornColorSelectorView.ViewModel, "horn-color", textureName);
                view.ViewModel.RefreshView.Execute();
            });
        }

        public void SelectLevelingHornsAppearance(int index)
        {
            Post(() =>
            {
                var view = GetCharGenAppearancePhaseView();
                if (view == null)
                {
                    return;
                }

                SelectAppearanceSlider(view.m_HornSelectorPcView.ViewModel, index);
            });
        }

        public void SelectLevelingHairStyleAppearance(int index)
        {
            Post(() =>
            {
                var view = GetCharGenAppearancePhaseView();
                if (view == null)
                {
                    return;
                }

                SelectAppearanceSlider(view.m_HairSelectorPcView.ViewModel, index);
            });
        }

        public void SelectLevelingHairColorAppearance(string textureName)
        {
            Post(() =>
            {
                var view = GetCharGenAppearancePhaseView();
                if (view == null)
                {
                    return;
                }

                SelectAppearanceTexture(view.m_HairColorSelectorView.ViewModel, "hair-color", textureName);
                view.ViewModel.RefreshView.Execute();
            });
        }

        public void SelectLevelingFaceAppearance(int index)
        {
            Post(() =>
            {
                var view = GetCharGenAppearancePhaseView();
                if (view == null)
                {
                    return;
                }

                SelectAppearanceSlider(view.m_FaceSelectorPcView.ViewModel, index);
            });
        }

        public void SelectLevelingBodyTypeAppearance(int index)
        {
            Post(() =>
            {
                var view = GetCharGenAppearancePhaseView();
                if (view == null)
                {
                    return;
                }

                SelectAppearanceSlider(view.m_BodySelectorPcView.ViewModel, index);
            });
        }

        public void SelectLevelingEyesColorAppearance(string textureName)
        {
            Post(() =>
            {
                var view = GetCharGenAppearancePhaseView();
                if (view == null)
                {
                    return;
                }

                SelectAppearanceTexture(view.m_EyesColorSelectorView.ViewModel, "eyes-color", textureName);
                view.ViewModel.RefreshView.Execute();
            });
        }

        public void SelectLevelingBodyColorAppearance(string textureName)
        {
            Post(() =>
            {
                var view = GetCharGenAppearancePhaseView();
                if (view == null)
                {
                    return;
                }

                SelectAppearanceTexture(view.m_BodyColorSelectorView.ViewModel, "body-color", textureName);
                view.ViewModel.RefreshView.Execute();
            });
        }

        public void SelectLevelingAlignment(string alignmentId)
        {
            Post(() =>
            {
                try
                {
                    if (_uiAccessor.CharGenView?.ViewModel == null)
                    {
                        _logger.LogWarning("Can't select leveling alignment due to missing CharGenView");
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    var viewModel = (_uiAccessor.CharGenView.SelectedDetailView as CharGenAlignmentPhaseDetailedPCView)?.ViewModel;
                    if (viewModel == null)
                    {
                        _logger.LogError("Can't select leveling alignment due to missing alignment phase viewmodel");
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    var alignment = viewModel.AlignmentSectorViewModels.FirstOrDefault(a => string.Equals(a.Alignment.ToString(), alignmentId, StringComparison.OrdinalIgnoreCase));
                    if (alignment == null)
                    {
                        _logger.LogError("Unable to find leveling alignment. AlignmentId={AlignmentId}", alignmentId);
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    viewModel.SelectedAlignmentVM.Value = alignment;
                    _logger.LogInformation("Leveling alignment has been selected. AlignmentId={AlignmentId}", viewModel.SelectedAlignmentVM.Value.Alignment);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while selecting leveling alignment. AlignmentId={AlignmentId}", alignmentId);
                    throw;
                }
            });
        }

        public void SelectLevelingClass(NetworkLevelingClass levelingClass)
        {
            Post(() =>
            {
                try
                {
                    var viewModel = GetLevelingPhaseViewModel();
                    if (viewModel == null)
                    {
                        _logger.LogWarning("Unable to select leveling class due to missing viewmodel");
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    var selectedClass = viewModel.m_ClassesVMs.FirstOrDefault(c => string.Equals(c.Class.AssetGuid.ToString(), levelingClass.Id, StringComparison.OrdinalIgnoreCase));
                    if (selectedClass == null)
                    {
                        _playerNotificationService.ShowWarningNotification(WellKnownKeys.GameNotifications.Leveling.ClassMismatch.Key, args: levelingClass.Name);
                        RejectGuardedApplication("leveling-class-missing");
                        return;
                    }

                    viewModel.SelectedClassVM.Value = selectedClass;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while selecting leveling class. ClassId={ClassId}", levelingClass.Id);
                    throw;
                }
            });
        }

        public void SelectLevelingFeature(NetworkLevelingFeature networkLevelingFeature)
        {
            Post(() =>
            {
                try
                {
                    CharGenFeatureSelectorItemVM requestedFeatureVM;
                    if (_backgroundCharGen?.CurrentPhaseVM.Value is CharGenFeatureSelectorPhaseVM backgroundPhase)
                    {
                        requestedFeatureVM = backgroundPhase.ExtractNestedEntities()
                            .OfType<CharGenFeatureSelectorItemVM>().FirstOrDefault(featureItem =>
                            string.Equals(featureItem.Feature.NameForAcronym, networkLevelingFeature.Name, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(featureItem.Feature.Feature.AssetGuid.ToString(), networkLevelingFeature.Id, StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        var view = _uiAccessor.CharGenView?.SelectedDetailView as CharGenFeatureSelectorPhaseDetailedPCView;
                        requestedFeatureVM = view?.m_Selector.VirtualList.Elements
                            .Select(element => element.Data as CharGenFeatureSelectorItemVM)
                            .FirstOrDefault(featureItem => featureItem != null
                                && string.Equals(featureItem.Feature.NameForAcronym, networkLevelingFeature.Name, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(featureItem.Feature.Feature.AssetGuid.ToString(), networkLevelingFeature.Id, StringComparison.OrdinalIgnoreCase));
                    }
                    if (requestedFeatureVM == null)
                    {
                        _playerNotificationService.ShowWarningNotification(WellKnownKeys.GameNotifications.Leveling.FeatureMismatch.Key, args: networkLevelingFeature.Name);
                        _logger.LogError("Unable to find requested feature in the list. FeatureName={FeatureName}, FeatureId={FeatureId}", networkLevelingFeature.Name, networkLevelingFeature.Id);
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }
                    requestedFeatureVM.SetSelected(true);
                    _logger.LogInformation("Selected leveling feature. FeatureName={FeatureName}, FeatureId={FeatureId}", requestedFeatureVM.Feature.NameForAcronym, requestedFeatureVM.Feature.Feature.AssetGuid.ToString());
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while selecting leveling feature. FeatureName={FeatureName}, FeatureId={FeatureId}", networkLevelingFeature.Name, networkLevelingFeature.Id);
                    throw;
                }
            });
        }
        public void SelectLevelingSpell(NetworkLevelingSpell networkLevelingSpell)
        {
            Post(() =>
            {
                try
                {
                    var spellsPhaseVM = GetCharGenSpellsPhaseVM();
                    if (spellsPhaseVM == null)
                    {
                        return;
                    }

                    var spellToAdd = spellsPhaseVM.SpellsSelector.Value.EntitiesCollection.FirstOrDefault(x => string.Equals(x.Spell.AssetGuid.ToString(), networkLevelingSpell.Id, StringComparison.OrdinalIgnoreCase));
                    if (spellToAdd == null)
                    {
                        _logger.LogError("Unable to add missing leveling spell. SpellName={SpellName}, SpellId={SpellId}", networkLevelingSpell.Name, networkLevelingSpell.Id);
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    spellsPhaseVM.SpellsSelector.Value.SelectedEntitiesCollection.Add(spellToAdd);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while selecting leveling spell. SpellName={SpellName}, SpellId={SpellId}", networkLevelingSpell.Name, networkLevelingSpell.Id);
                    throw;
                }
            });
        }

        public void RemoveLevelingSpell(NetworkLevelingSpell networkLevelingSpell)
        {
            Post(() =>
            {
                try
                {
                    var spellsPhaseVM = GetCharGenSpellsPhaseVM();
                    if (spellsPhaseVM == null)
                    {
                        return;
                    }

                    var spellToRemove = spellsPhaseVM.SpellsSelector.Value.SelectedEntitiesCollection.FirstOrDefault(x => string.Equals(x.Spell.AssetGuid.ToString(), networkLevelingSpell.Id, StringComparison.OrdinalIgnoreCase));
                    if (spellToRemove == null)
                    {
                        _logger.LogError("Unable to remove missing leveling spell. SpellName={SpellName}, SpellId={SpellId}", networkLevelingSpell.Name, networkLevelingSpell.Id);
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    spellsPhaseVM.SpellsSelector.Value.SelectedEntitiesCollection.Remove(spellToRemove);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while removing selected leveling spell. SpellName={SpellName}, SpellId={SpellId}", networkLevelingSpell.Name, networkLevelingSpell.Id);
                    throw;
                }
            });
        }

        public void IncreaseLevelingSkillPoint(NetworkLevelingSkillPoint networkLevelingSkillPoint)
        {
            Post(() =>
            {
                try
                {
                    if (_backgroundLevelUpController != null)
                    {
                        _backgroundLevelUpController.SpendSkillPoint(networkLevelingSkillPoint.StatType);
                        return;
                    }
                    var skillView = GetLevelingSkillAllocatorView(networkLevelingSkillPoint.StatType);
                    if (skillView == null)
                    {
                        return;
                    }

                    skillView.ViewModel.m_LevelUpController.SpendSkillPoint(skillView.ViewModel.StatType);
                    skillView.OnChangedValue();
                    _logger.LogInformation("Leveling skillpoint has been increased. StatType={StatType}", networkLevelingSkillPoint.StatType);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while increasing leveling skill point. StatType={StatType}", networkLevelingSkillPoint.StatType);
                    throw;
                }
            });
        }

        public void DecreaseLevelingSkillPoint(NetworkLevelingSkillPoint networkLevelingSkillPoint)
        {
            Post(() =>
            {
                try
                {
                    if (_backgroundLevelUpController != null)
                    {
                        _backgroundLevelUpController.UnspendSkillPoint(networkLevelingSkillPoint.StatType);
                        return;
                    }
                    var skillView = GetLevelingSkillAllocatorView(networkLevelingSkillPoint.StatType);
                    if (skillView == null)
                    {
                        return;
                    }
                    skillView.ViewModel.m_LevelUpController.UnspendSkillPoint(skillView.ViewModel.StatType);
                    skillView.OnChangedValue();
                    _logger.LogInformation("Leveling skillpoint has been decreased. StatType={StatType}", networkLevelingSkillPoint.StatType);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while decreasing leveling skill point. StatType={StatType}", networkLevelingSkillPoint.StatType);
                    throw;
                }
            });
        }

        public void ChangeLevelingRacialAbilityScoreBonus(NetworkLevelingSequenceDirection direction)
        {
            Post(() =>
            {
                try
                {
                    if (_uiAccessor.CharGenView?.ViewModel == null)
                    {
                        _logger.LogError("Unable to get leveling ability score vm due too missing CharGenView");
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    var view = _uiAccessor.CharGenView.SelectedDetailView as CharGenAbilityScoresDetailedPCView;
                    if (view == null)
                    {
                        _logger.LogError("Can't change leveling racial bonus due to missing ability score phase view");
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    switch (direction)
                    {
                        case NetworkLevelingSequenceDirection.Left:
                            view.RaceBonusSelectorPc.ViewModel.OnLeft();
                            break;
                        case NetworkLevelingSequenceDirection.Right:
                            view.RaceBonusSelectorPc.ViewModel.OnRight();
                            break;
                    }

                    _logger.LogInformation("Leveling racial ability score bonus has been changed. StatType={StatType}, Direction={Direction}", view.ViewModel.SelectedRaceBonus.Value, direction);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while changing leveling racial ability score bonus. Direction={Direction}", direction);
                    throw;
                }
            });
        }

        public void SetLevelingName(string name)
        {
            Post(() =>
            {
                try
                {
                    if (_uiAccessor.CharGenView?.ViewModel == null)
                    {
                        _logger.LogError("Unable to get leveling name vm due too missing CharGenView");
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    var view = _uiAccessor.CharGenView.SelectedDetailView as CharGenNamePhaseDetailedPCView;
                    if (view == null)
                    {
                        _logger.LogError("Can't change leveling name due to missing ability score phase view");
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    view.m_NameInputField.text = name;
                    view.ViewModel.OnEndEdit(name);
                    _logger.LogInformation("Leveling name has been changed. Name={Name}", view.ViewModel.ChosenName.Value);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while changing leveling name. Name={Name}", name);
                    throw;
                }
            });
        }

        public void ChangeLevelingBirthDay(NetworkLevelingSequenceDirection direction)
        {
            Post(() =>
            {
                try
                {
                    if (_uiAccessor.CharGenView?.ViewModel == null)
                    {
                        _logger.LogError("Unable to get leveling birth day vm due too missing CharGenView");
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    var view = _uiAccessor.CharGenView.SelectedDetailView as CharGenNamePhaseDetailedPCView;
                    if (view == null)
                    {
                        _logger.LogError("Can't change leveling birth day due to missing ability score phase view");
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    switch (direction)
                    {
                        case NetworkLevelingSequenceDirection.Left:
                            view.ViewModel.DaySelectorVM.OnLeft();
                            break;
                        case NetworkLevelingSequenceDirection.Right:
                            view.ViewModel.DaySelectorVM.OnRight();
                            break;
                    }

                    _logger.LogInformation("Leveling birth day has been changed. Day={Day}, Direction={Direction}", view.ViewModel.DaySelectorVM.Value, direction);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while changing leveling birth day. Direction={Direction}", direction);
                    throw;
                }
            });
        }

        public void ChangeLevelingBirthMonth(NetworkLevelingSequenceDirection direction)
        {
            Post(() =>
            {
                try
                {
                    if (_uiAccessor.CharGenView?.ViewModel == null)
                    {
                        _logger.LogError("Unable to get leveling birth month vm due too missing CharGenView");
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    var view = _uiAccessor.CharGenView.SelectedDetailView as CharGenNamePhaseDetailedPCView;
                    if (view == null)
                    {
                        _logger.LogError("Can't change leveling birth month due to missing ability score phase view");
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    switch (direction)
                    {
                        case NetworkLevelingSequenceDirection.Left:
                            view.ViewModel.MonthSelectorVM.OnLeft();
                            break;
                        case NetworkLevelingSequenceDirection.Right:
                            view.ViewModel.MonthSelectorVM.OnRight();
                            break;
                    }

                    _logger.LogInformation("Leveling birth month has been changed. Month={Month}, Direction={Direction}", view.ViewModel.MonthSelectorVM.Value, direction);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while changing leveling birth month. Direction={Direction}", direction);
                    throw;
                }
            });
        }

        public void IncreaseLevelingAbilityScore(NetworkLevelingAbilityScore networkLevelingAbilityScore)
        {
            Post(() =>
            {
                try
                {
                    if (_backgroundLevelUpController != null)
                    {
                        _backgroundLevelUpController.AddStatPoint(networkLevelingAbilityScore.StatType);
                        return;
                    }
                    var abilityScoreView = GetLevelingAbilityScoreAllocatorView(networkLevelingAbilityScore.StatType);
                    if (abilityScoreView == null)
                    {
                        _logger.LogError("Unable to find ability score allocator view. StatType={StatType}", networkLevelingAbilityScore.StatType);
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    abilityScoreView.ViewModel.TryIncreaseValue();
                    _logger.LogInformation("Leveling ability score has been increased. StatType={StatType}", networkLevelingAbilityScore.StatType);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while increasing leveling ability score. StatType={StatType}", networkLevelingAbilityScore.StatType);
                    throw;
                }
            });
        }

        public void DecreaseLevelingAbilityScore(NetworkLevelingAbilityScore networkLevelingAbilityScore)
        {
            Post(() =>
            {
                try
                {
                    if (_backgroundLevelUpController != null)
                    {
                        _backgroundLevelUpController.RemoveStatPoint(networkLevelingAbilityScore.StatType);
                        return;
                    }
                    var abilityScoreView = GetLevelingAbilityScoreAllocatorView(networkLevelingAbilityScore.StatType);
                    if (abilityScoreView == null)
                    {
                        _logger.LogError("Unable to find ability score allocator view. StatType={StatType}", networkLevelingAbilityScore.StatType);
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }

                    abilityScoreView.ViewModel.TryDecreaseValue();
                    _logger.LogInformation("Leveling ability score has been decreased. StatType={StatType}", networkLevelingAbilityScore.StatType);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while decreasing leveling ability score. StatType={StatType}", networkLevelingAbilityScore.StatType);
                    throw;
                }
            });
        }

        public void CompleteLeveling()
        {
            Post(() =>
            {
                try
                {
                    if (_backgroundCharGen != null) _backgroundCharGen.Complete();
                    else _uiAccessor.CharGenView.ViewModel.Complete();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while completing char gen");
                    throw;
                }
            });
        }

        public void TerminateLeveling()
        {
            Post(() =>
            {
                try
                {
                    if (_backgroundCharGen != null) _backgroundCharGen.Close();
                    else _uiAccessor.CharGenView.ViewModel.Close();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while closing char gen");
                    throw;
                }
            });
        }

        public void UpdateLevelingRespecUI(bool isInteractable, int readyPlayersCount, int totalPlayersCount)
        {
            Post(() =>
            {
                if (_uiAccessor.RespecView?.ViewModel == null)
                {
                    _logger.LogWarning("Unable to update closed respec window");
                    return;
                }

                _uiAccessor.RespecView.m_MaxLevelupButton.Interactable = false;
                _uiAccessor.RespecView.m_MaxMythicUpButton.Interactable = false;

                _uiAccessor.RespecView.m_LevelupButton.Interactable = _uiAccessor.RespecView.ViewModel.CanUpCharacterLevel.Value && isInteractable;
                _uiAccessor.RespecView.m_MythicUpButton.Interactable = _uiAccessor.RespecView.ViewModel.CanUpMythicLevel.Value && isInteractable;
                _uiAccessor.RespecView.m_CompleteButton.Interactable = _uiAccessor.RespecView.ViewModel.IsFinished.Value && isInteractable;

                _uiSyncCountersService.UpdateButtonTextCounter(_uiAccessor.RespecView.m_CompleteButtonTitle, readyPlayersCount, totalPlayersCount);

                _logger.LogInformation("Respec window UI has been updated. IsInteractable={IsInteractable}, ReadyPlayers={ReadyPlayers}, TotalPlayers={TotalPlayers}", isInteractable, readyPlayersCount, totalPlayersCount);
            });
        }

        public string GetCurrentRespecWindowUnitId()
        {
            return _uiAccessor.RespecView?.ViewModel?.CurrentUnit.Value.UniqueId;
        }

        public void CompleteLevelingRespec()
        {
            Post(() =>
            {
                if (_uiAccessor.RespecView?.ViewModel == null)
                {
                    _logger.LogWarning("Unable to complete closed respec window");
                    RejectGuardedApplication("leveling-application-incomplete");
                    return;
                }

                _uiAccessor.RespecView.ViewModel.Complete();
                _logger.LogInformation("Respec window UI has been completed");
            });
        }

        public void InitiateLevelingRespecLevelUp()
        {
            Post(() =>
            {
                if (_uiAccessor.RespecView?.ViewModel == null)
                {
                    _logger.LogWarning("Unable to levelup due to missing respec window");
                    RejectGuardedApplication("leveling-application-incomplete");
                    return;
                }

                _uiAccessor.RespecView.ViewModel.InitiateNextLevelup();
                _logger.LogInformation("Respec window levelup has been initiated");
            });
        }

        public void InitiateLevelingRespecMythicLevelUp()
        {
            Post(() =>
            {
                if (_uiAccessor.RespecView?.ViewModel == null)
                {
                    _logger.LogWarning("Unable to mythic levelup due to missing respec window");
                    RejectGuardedApplication("leveling-application-incomplete");
                    return;
                }

                _uiAccessor.RespecView.ViewModel.InitiateNextMythic();
                _logger.LogInformation("Respec window mythic levelup has been initiated");
            });
        }

        public void UpdateLevelingPhaseControls(bool isEnabled)
        {
            Post(() =>
            {
                try
                {
                    if (_uiAccessor.CharGenView?.ViewModel == null)
                    {
                        _logger.LogError("Unable to update leveling controls due too missing CharGenView");
                        return;
                    }

                    _logger.LogInformation("Updating generic part of leveling screen. IsEnabled={IsEnabled}", isEnabled);
                    _uiAccessor.CharGenView.m_CloseButton.Interactable = isEnabled;
                    _uiAccessor.CharGenView.SetActiveNextPhaseButton(isEnabled);

                    var nextEnabled = _uiAccessor.CharGenView.CanGoNext.Value && isEnabled;
                    _uiAccessor.CharGenView.m_NextButton.Interactable = nextEnabled;
                    _uiAccessor.CharGenView.m_NextValidPageButton.Interactable = nextEnabled;
                    var backEnabled = _uiAccessor.CharGenView.CanGoBack.Value && isEnabled;
                    _uiAccessor.CharGenView.m_BackButton.Interactable = backEnabled;
                    _uiAccessor.CharGenView.m_FirstPageButton.Interactable = backEnabled;

                    foreach (var roadmapPhase in _uiAccessor.CharGenView.RoadmapMenuView.m_VisiblePhases)
                    {
                        var baseView = roadmapPhase as CharGenPhaseRoadmapBaseView<CharGenPhaseBaseVM>;
                        if (baseView != null)
                        {
                            baseView.m_Button.Interactable = baseView.m_Button.Interactable && isEnabled;
                            baseView.m_ButtonBackground.Interactable = baseView.m_ButtonBackground.Interactable && isEnabled;
                            baseView.m_ButtonLabel.Interactable = baseView.m_ButtonLabel.Interactable && isEnabled;
                        }
                    }

                    switch (_uiAccessor.CharGenView.SelectedDetailView)
                    {
                        case CharGenNamePhaseDetailedPCView namePhase:
                            namePhase.m_NameInputField.readOnly = !isEnabled;
                            break;
                        case CharGenAppearancePhaseDetailedPCView appearancePhase:
                            appearancePhase.m_BodySelectorPcView.m_Slider.enabled = isEnabled;
                            appearancePhase.m_FaceSelectorPcView.m_Slider.enabled = isEnabled;
                            appearancePhase.m_ScarSelectorPcView.m_Slider.enabled = isEnabled;
                            appearancePhase.m_HairSelectorPcView.m_Slider.enabled = isEnabled;
                            appearancePhase.m_HornSelectorPcView.m_Slider.enabled = isEnabled;
                            appearancePhase.m_WarpaintSelectorPcView.m_Slider.enabled = isEnabled;
                            appearancePhase.m_TatooSelectorPcView.m_Slider.enabled = isEnabled;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while updating leveling phase controls");
                    throw;
                }
            });
        }

        public void SwitchLevelingPhase(NetworkLevelingPhase networkLevelingPhase)
        {
            Post(() =>
            {
                if (_backgroundCharGen != null)
                {
                    if (networkLevelingPhase.Index < 0
                        || networkLevelingPhase.Index >= _backgroundCharGen.PhasesCollection.Count)
                    {
                        _logger.LogError("Background leveling phase is out of range. Index={Index}, TotalCount={TotalCount}",
                            networkLevelingPhase.Index, _backgroundCharGen.PhasesCollection.Count);
                        RejectGuardedApplication("leveling-application-incomplete");
                        return;
                    }
                    _backgroundCharGen.CurrentPhaseVM.Value = _backgroundCharGen.PhasesCollection[networkLevelingPhase.Index];
                    return;
                }
                if (_uiAccessor.CharGenView == null)
                {
                    _logger.LogError("Unable to update switch leveling phase due too missing CharGenView");
                    RejectGuardedApplication("leveling-application-incomplete");
                    return;
                }

                var roadmapVM = _uiAccessor.CharGenView.RoadmapMenuView.ViewModel;
                if (networkLevelingPhase.Index >= roadmapVM.EntitiesCollection.Count)
                {
                    _logger.LogError("Leveling phase is out of range. Index={Index}, TotalCount={TotalCount}", networkLevelingPhase.Index, roadmapVM.EntitiesCollection.Count);
                    RejectGuardedApplication("leveling-application-incomplete");
                    return;
                }

                var phaseVM = roadmapVM.EntitiesCollection[networkLevelingPhase.Index];
                roadmapVM.SelectedEntity.Value = phaseVM;
            });
        }

        private CharGenSkillAllocatorPCView GetLevelingSkillAllocatorView(StatType statType)
        {
            if (_uiAccessor.CharGenView == null)
            {
                _logger.LogError("Unable to get leveling skillpoint vm due too missing CharGenView");
                RejectGuardedApplication("leveling-application-incomplete");
                return null;
            }

            if (_uiAccessor.CharGenView.SelectedDetailView is not CharGenSkillsPhaseDetailedPCView skillAllocator)
            {
                _logger.LogWarning("Unable to get leveling skillpoint vm because current phase is not skill phase");
                RejectGuardedApplication("leveling-application-incomplete");
                return null;
            }

            var skillView = skillAllocator.m_StatAllocators.FirstOrDefault(x => x.ViewModel.StatType == statType);
            if (skillView == null)
            {
                _logger.LogWarning("Unable to find leveling view for stat. StatType={StatType}", statType);
                RejectGuardedApplication("leveling-application-incomplete");
                return null;
            }

            return skillView;
        }

        private CharGenAbilityScoreAllocatorPCView GetLevelingAbilityScoreAllocatorView(StatType statType)
        {
            if (_uiAccessor.CharGenView?.ViewModel == null)
            {
                _logger.LogError("Unable to get leveling ability score vm due too missing CharGenView");
                RejectGuardedApplication("leveling-application-incomplete");
                return null;
            }

            if (_uiAccessor.CharGenView.SelectedDetailView is not CharGenAbilityScoresDetailedPCView abilityScoresDetailedPCView)
            {
                _logger.LogWarning("Unable to get leveling ability score vm because current phase is not skill phase");
                RejectGuardedApplication("leveling-application-incomplete");
                return null;
            }

            var abilityScoreView = abilityScoresDetailedPCView.m_StatAllocators.FirstOrDefault(x => x.ViewModel.StatType == statType);
            if (abilityScoreView == null)
            {
                _logger.LogWarning("Unable to find ability score leveling view for stat. StatType={StatType}", statType);
                RejectGuardedApplication("leveling-application-incomplete");
                return null;
            }

            return abilityScoreView;
        }

        private CharGenSpellsPhaseVM GetCharGenSpellsPhaseVM()
        {
            if (_backgroundCharGen?.CurrentPhaseVM.Value is CharGenSpellsPhaseVM backgroundPhase)
                return backgroundPhase;
            if (_uiAccessor.CharGenView == null)
            {
                _logger.LogError("Unable to get leveling spellphase vm due too missing CharGenView");
                RejectGuardedApplication("leveling-application-incomplete");
                return null;
            }

            if (_uiAccessor.CharGenView.SelectedDetailView is not CharGenSpellsPhaseDetailedPCView spellsPhaseDetailedPCView)
            {
                _logger.LogWarning("Unable to get leveling spellphase vm because current phase is not spell phase");
                RejectGuardedApplication("leveling-application-incomplete");
                return null;
            }

            return spellsPhaseDetailedPCView.ViewModel;
        }

        private CharGenClassPhaseVM GetLevelingPhaseViewModel()
        {
            return _backgroundCharGen?.CurrentPhaseVM.Value as CharGenClassPhaseVM
                ?? (_uiAccessor.CharGenView?.SelectedDetailView as CharGenClassPhaseDetailedPCView)?.ViewModel;
        }

        private void SelectAppearanceTexture(SelectionGroupRadioVM<TextureSelectorItemVM> selector, string selectorName, string textureName)
        {
            var texture = selector.EntitiesCollection.FirstOrDefault(x => string.Equals(x.Texture.Value.name, textureName, StringComparison.OrdinalIgnoreCase));
            if (texture == null)
            {
                _logger.LogError("Unable to find texture. Selector={Selector}, TextureName={TextureName}", selectorName, textureName);
                RejectGuardedApplication("leveling-application-incomplete");
                return;
            }

            selector.SelectedEntity.Value = texture;
            _logger.LogInformation("Leveling appearance texture has been selected. Selector={Selector}, TextureName={TextureName}", selectorName, textureName);
        }

        private void SelectAppearanceSlider(StringSequentialSelectorVM selector, int index)
        {
            selector.m_CurrentIndex.Value = index;
        }

        private CharGenAppearancePhaseDetailedPCView GetCharGenAppearancePhaseView()
        {
            var view = _uiAccessor.CharGenView?.SelectedDetailView as CharGenAppearancePhaseDetailedPCView;
            if (view == null) RejectGuardedApplication("leveling-appearance-view-missing");
            return view;
        }
    }
}
