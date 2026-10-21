using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Kingmaker;
using Kingmaker.Assets.Controllers.GlobalMap;
using Kingmaker.Blueprints.Root;
using Kingmaker.Blueprints.Root.Strings;
using Kingmaker.Globalmap;
using Kingmaker.Globalmap.View;
using Kingmaker.RandomEncounters;
using Kingmaker.UI;
using Kingmaker.UI.Common;
using Kingmaker.Utility;
using Kingmaker.View;
using Microsoft.Extensions.Logging;
using UnityEngine;
using WOTRMultiplayer.Entities.GlobalMap;

namespace WOTRMultiplayer.Services.GameInteraction
{
    public partial class GlobalMapInteractionService
    {
        private sealed class PendingPopup
        {
            public NetworkGlobalMapCommonPopup Popup;
            public TaskCompletionSource<bool> Completion;
            public float Deadline;
            public bool Created;
        }
        private sealed class PendingEncounter
        {
            public NetworkGlobalMapEncounter Encounter;
            public string Id;
            public float Deadline;
        }
        private readonly List<PendingPopup> _pendingPopups = new();
        private readonly List<PendingEncounter> _pendingEncounters = new();
        private readonly HashSet<string> _seenEncounterIds = new();
        private readonly Queue<string> _encounterIdOrder = new();
        private string _presentationScope;
        private string _shownPopupKey;
        private string _acceptingPopupKey;
        private CombatRandomEncounterData _markerEncounter;
        private float _markerCheckAt;
        private int _markerRepairs;
        private bool _markerFocused;
        private CameraRig _mapCameraRig;
        private NetworkGlobalMapLocation _pendingArrival;
        private float _arrivalDeadline;

        public void ShowReceivedLocation(NetworkGlobalMapLocation location)
        {
            var scope = GlobalMapPresentationScope.Current;
            _mainThreadAccessor.Post(() =>
            {
                if (!GlobalMapPresentationScope.Matches(scope)) return;
                EnsurePresentationScope();
                _pendingArrival = location;
                _arrivalDeadline = Time.unscaledTime + 30;
                _logger.LogInformation("[MapEvent] Arrival view queued for confirmed location. Location={Location}", location?.Id);
            });
        }

        private static string PopupKey(NetworkGlobalMapCommonPopup popup) => popup == null
            ? null : popup.Type + ":" + popup.Location?.Id;

        private void EnsurePresentationScope()
        {
            var scope = Main.Multiplayer.IsActive ? GlobalMapPresentationScope.Current : null;
            if (_presentationScope == scope) return;
            foreach (var pending in _pendingPopups) pending.Completion.TrySetResult(false);
            _pendingPopups.Clear();
            _pendingEncounters.Clear();
            _seenEncounterIds.Clear();
            _encounterIdOrder.Clear();
            _shownPopupKey = null;
            _markerEncounter = null;
            _pendingArrival = null;
            _presentationScope = scope;
        }

        public void ObserveCommonPopup(NetworkGlobalMapCommonPopup popup)
        {
            // Called from gameplay/UI callbacks on the Unity thread, including
            // natural arrivals. Identifies an existing popup before its replica
            // is requested; do not confuse any other modal with this one.
            EnsurePresentationScope();
            _shownPopupKey = PopupKey(popup);
        }

        private void QueuePopup(NetworkGlobalMapCommonPopup popup, TaskCompletionSource<bool> completion)
        {
            EnsurePresentationScope();
            if (popup == null || popup.Type is not (NetworkGlobalMapCommonPopupType.Ingredients
                or NetworkGlobalMapCommonPopupType.Fatigue or NetworkGlobalMapCommonPopupType.Exhaust))
            {
                // Crusade-specific windows keep their existing local callbacks.
                completion.TrySetResult(false);
                return;
            }
            var key = PopupKey(popup);
            var modal = _uiAccessor.CommonPCView?.m_MessageModalPCView;
            if (_shownPopupKey == key && modal?.ViewModel != null)
            {
                completion.TrySetResult(false); // Already witnessed locally.
                return;
            }
            var duplicate = _pendingPopups.FirstOrDefault(p => PopupKey(p.Popup) == key);
            if (duplicate != null)
            {
                completion.TrySetResult(false);
                return;
            }
            if (_pendingPopups.Count >= 16)
            {
                PresentationFailed("popup-queue-full", key);
                completion.TrySetResult(false);
                return;
            }
            _pendingPopups.Add(new PendingPopup { Popup = popup, Completion = completion, Deadline = Time.unscaledTime + 30 });
            _logger.LogInformation("[MapEvent] Popup queued until its view is available. Event={Event}, Scope={Scope}", key, _presentationScope);
        }

        private void CancelPendingPopup(string key)
        {
            foreach (var pending in _pendingPopups.Where(p => PopupKey(p.Popup) == key).ToArray())
            {
                _pendingPopups.Remove(pending);
                pending.Completion.TrySetResult(false);
            }
        }

        private void ApplyConfirmedIngredients(NetworkGlobalMapCommonPopup popup)
        {
            var point = _gameStateLookupService.GetGlobalMapPoint(popup.Location);
            if (point == null) { PresentationFailed("ingredients-location-missing", popup.Location?.Id); return; }
            var state = GlobalMapView.Instance.State.GetPointState(point.Blueprint);
            if (state.IngredientWasCollected) return; // Durable idempotence.
            var collected = BlueprintRoot.Instance.CraftRoot.CollectRoot.CollectIngredient(point.Blueprint);
            state.IngredientWasCollected = true;
            state.SetVisited();
            _logger.LogInformation("[MapEvent] Host-confirmed collection applied once. Location={Location}, Count={Count}", popup.Location.Id, collected.Count);
        }

        private void QueueEncounter(NetworkGlobalMapEncounter encounter, string id)
        {
            EnsurePresentationScope();
            if (_pendingEncounters.Any(p => p.Id == id)) return;
            if (_pendingEncounters.Count >= 16) { PresentationFailed("encounter-queue-full", id); return; }
            _pendingEncounters.Add(new PendingEncounter { Encounter = encounter, Id = id, Deadline = Time.unscaledTime + 30 });
            _logger.LogInformation("[MapEvent] Encounter waiting for map initialization. EventId={EventId}", id);
        }

        private bool RememberEncounter(string id)
        {
            EnsurePresentationScope();
            if (string.IsNullOrEmpty(id)) return true;
            if (!_seenEncounterIds.Add(id)) { _logger.LogInformation("[MapEvent] Duplicate encounter ignored. EventId={EventId}", id); return false; }
            _encounterIdOrder.Enqueue(id);
            if (_encounterIdOrder.Count > 256) _seenEncounterIds.Remove(_encounterIdOrder.Dequeue());
            return true;
        }

        private void ObserveEncounterMarker(CombatRandomEncounterData encounter, string id)
        {
            _markerEncounter = encounter;
            _markerCheckAt = Time.unscaledTime + 1.25f; // Let the appearance tween finish.
            _markerRepairs = 0;
            _markerFocused = false;
            _logger.LogInformation("[MapEvent] Encounter started; marker verification scheduled. EventId={EventId}, Position={Position}", id, encounter.Position);
        }

        public void TickMapPresentation()
        {
            EnsurePresentationScope();
            if (!Main.Multiplayer.IsActive) return;
            var mapReady = GlobalMapView.Instance != null && _uiAccessor.GlobalMapPCView?.ViewModel != null;
            foreach (var pending in _pendingEncounters.ToArray())
            {
                if (Time.unscaledTime >= pending.Deadline) { _pendingEncounters.Remove(pending); PresentationFailed("encounter-map-timeout", pending.Id); }
                else if (mapReady) { _pendingEncounters.Remove(pending); RollEncounter(pending.Encounter, pending.Id); }
            }
            if (_shownPopupKey != null && _uiAccessor.CommonPCView?.m_MessageModalPCView?.ViewModel == null)
                _shownPopupKey = null;

            foreach (var pending in _pendingPopups.ToArray())
            {
                var key = PopupKey(pending.Popup);
                if (Time.unscaledTime >= pending.Deadline)
                {
                    _pendingPopups.Remove(pending);
                    pending.Completion.TrySetResult(false);
                    PresentationFailed("popup-display-timeout", key);
                    continue;
                }
                if (!mapReady) continue;
                var modal = _uiAccessor.CommonPCView?.m_MessageModalPCView;
                if (modal == null) continue;
                if (modal.ViewModel != null)
                {
                    if (_shownPopupKey != key) continue; // Another window: wait.
                    _pendingPopups.Remove(pending);
                    pending.Completion.TrySetResult(pending.Created);
                    _logger.LogInformation("[MapEvent] Popup view confirmed. Event={Event}, Replica={Replica}", key, pending.Created);
                    continue;
                }
                if (pending.Created) continue; // Opening callback not bound yet.
                if (pending.Popup.Type == NetworkGlobalMapCommonPopupType.Ingredients)
                {
                    var point = _gameStateLookupService.GetGlobalMapPoint(pending.Popup.Location);
                    if (point == null) continue;
                    var state = GlobalMapView.Instance.State.GetPointState(point.Blueprint);
                    if (state.IngredientWasCollected)
                    {
                        _pendingPopups.Remove(pending);
                        pending.Completion.TrySetResult(false);
                        continue;
                    }
                    var root = BlueprintRoot.Instance.CraftRoot.CollectRoot;
                    UIUtility.ShowMessageBox(root.PointResources, MessageModalBase.ModalType.Dialog,
                        result => { if (result == MessageModalBase.ButtonType.Yes && _acceptingPopupKey == key) ApplyConfirmedIngredients(pending.Popup); },
                        null, 0, UIStrings.Instance.Tooltips.Collect, null, [.. point.Blueprint.Ingredients.Select(i => i.Ingredient.Get())]);
                }
                else
                {
                    var text = pending.Popup.Type == NetworkGlobalMapCommonPopupType.Exhaust
                        ? UIStrings.Instance.GlobalMap.PartyIsExhausted : UIStrings.Instance.GlobalMap.PartyIsFatigue;
                    UIUtility.ShowMessageBox(text, MessageModalBase.ModalType.Dialog,
                        result => { if (_acceptingPopupKey == key) _uiAccessor.GlobalMapPCView.ViewModel.OnFatigueClose(result); });
                }
                _shownPopupKey = key;
                modal.m_AcceptButton.Interactable = false;
                modal.m_DeclineButton.Interactable = false;
                pending.Created = true;
                break; // One modal, never overwrite a second one this frame.
            }
            if (mapReady) VerifyEncounterMarker();
            if (mapReady && _pendingArrival != null)
            {
                var arrival = _pendingArrival;
                var view = _uiAccessor.GlobalMapEnterMessagePCView;
                if (view?.ViewModel?.Location?.Blueprint.AssetGuid.ToString() == arrival.Id)
                    _pendingArrival = null; // Natural arrival already presented.
                else if (Time.unscaledTime >= _arrivalDeadline)
                {
                    _pendingArrival = null;
                    PresentationFailed("arrival-view-timeout", arrival.Id);
                }
                else if (view?.ViewModel == null && _uiAccessor.CommonPCView?.m_MessageModalPCView?.ViewModel == null)
                {
                    var point = _gameStateLookupService.GetGlobalMapPoint(arrival);
                    var player = GlobalMapView.Instance.State.Player;
                    if (point != null && player.Location == point.Blueprint)
                    {
                        // Only present arrival after the local pawn really arrived;
                        // do not teleport it or restart the journey.
                        _pendingArrival = null;
                        GlobalMapMovementUtility.InteractWithLocation(GlobalMapView.Instance.State, point.Blueprint, player, true);
                        _logger.LogInformation("[MapEvent] Missing arrival view recreated. Location={Location}", arrival.Id);
                    }
                }
            }
        }

        private void VerifyEncounterMarker()
        {
            if (_markerEncounter == null || Time.unscaledTime < _markerCheckAt) return;
            var map = GlobalMapView.Instance;
            if (!ReferenceEquals(map.State.Player.CurrentEncounterData, _markerEncounter)) { _markerEncounter = null; return; }
            if (!_markerEncounter.Position.HasValue || map.PawnsHolder == null) return;
            var pawn = map.PawnsHolder.GetComponentsInChildren<GlobalMapRandomEncounterPawn>(true)
                .FirstOrDefault(p => ReferenceEquals(p.Data, _markerEncounter));
            if (pawn == null)
            {
                if (_markerRepairs++ >= 2) { PresentationFailed("encounter-marker-missing", _markerEncounter.Blueprint.AssetGuid.ToString()); _markerEncounter = null; return; }
                // Visual representation only: NEVER call StartEncounter again.
                map.SpawnEncounterPawn(_markerEncounter, false);
                _logger.LogWarning("[MapEvent] Missing encounter marker recreated; no encounter replay.");
                _markerCheckAt = Time.unscaledTime + 0.5f;
                return;
            }
            pawn.gameObject.SetActive(true);
            var expected = _markerEncounter.Position.Value;
            var position = pawn.transform.position;
            if (Math.Abs(position.x - expected.x) > 0.25f || Math.Abs(position.z - expected.z) > 0.25f)
            {
                pawn.transform.position = expected + new Vector3(0, 0.065f, 0);
                _logger.LogWarning("[MapEvent] Encounter marker position corrected. Previous={Previous}, Confirmed={Confirmed}", position, expected);
            }
            if (!_mapCameraRig) _mapCameraRig = UnityEngine.Object.FindObjectOfType<CameraRig>();
            var camera = _mapCameraRig?.Camera;
            if (!_markerFocused && camera != null)
            {
                var viewport = camera.WorldToViewportPoint(pawn.transform.position);
                if (viewport.z <= 0 || viewport.x < 0.05f || viewport.x > 0.95f || viewport.y < 0.05f || viewport.y > 0.95f)
                {
                    _mapCameraRig.ScrollTo(expected, true);
                    _logger.LogInformation("[MapEvent] Camera centered on off-screen encounter marker.");
                }
                _markerFocused = true;
            }
            _markerCheckAt = Time.unscaledTime + 1;
        }

        private void PresentationFailed(string reason, string id)
        {
            _logger.LogError("[MapEvent] Presentation failed. Reason={Reason}, Event={Event}, Scope={Scope}", reason, id, _presentationScope);
            _playerNotificationService.ShowWarningNotification(WellKnownKeys.GameNotifications.GlobalMap.EventDisplayFailed.Key);
        }
    }
}
