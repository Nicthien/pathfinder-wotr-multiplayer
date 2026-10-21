using System.Collections.Generic;
using Kingmaker.UI.MVVM._PCView.CharGen;
using Microsoft.Extensions.Logging;
using UnityEngine;

namespace WOTRMultiplayer.UnityBehaviours.Leveling
{
    // Keep the bound views alive: replay uses SelectedDetailView and their phase
    // witness callbacks. SetActive(false) during leveling would stop that sync.
    public sealed class LevelingOwnerVisibilityBehaviour : MonoBehaviour
    {
        private CharGenPCView _view;
        private CanvasGroup _mask;
        private float _alpha;
        private bool _interactable, _blocksRaycasts, _ignoreParent;
        private readonly Dictionary<Canvas, bool> _canvases = new();
        private readonly Dictionary<CanvasGroup, bool> _groups = new();
        private float _nextScan;
        public bool IsHidden { get; private set; }

        public void Apply(CharGenPCView view)
        {
            _view = view;
            if (!Main.Multiplayer.IsActive || Main.Multiplayer.ShouldShowLevelingUI())
            {
                Restore();
                return;
            }
            if (!IsHidden)
            {
                _mask = GetComponent<CanvasGroup>();
                if (_mask == null) _mask = gameObject.AddComponent<CanvasGroup>();
                _alpha = _mask.alpha;
                _interactable = _mask.interactable;
                _blocksRaycasts = _mask.blocksRaycasts;
                _ignoreParent = _mask.ignoreParentGroups;
                IsHidden = true;
                _nextScan = 0;
                Main.GetLogger<LevelingOwnerVisibilityBehaviour>().LogInformation(
                    "[LevelingOwnerUI] Background replication; window hidden. UnitId={UnitId}", view.ViewModel?.LevelUpConfig?.Unit?.UniqueId);
            }
            Mask();
        }

        private void Mask()
        {
            if (!_mask) return;
            _mask.alpha = 0;
            _mask.interactable = false;
            _mask.blocksRaycasts = false;
            _mask.ignoreParentGroups = false;
            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 0.25f;
            foreach (var canvas in GetComponentsInChildren<Canvas>(true))
            {
                if (!_canvases.ContainsKey(canvas)) _canvases.Add(canvas, canvas.enabled);
                canvas.enabled = false;
            }
            foreach (var group in GetComponentsInChildren<CanvasGroup>(true))
            {
                if (group == _mask) continue;
                if (!_groups.ContainsKey(group)) _groups.Add(group, group.ignoreParentGroups);
                group.ignoreParentGroups = false;
            }
        }

        private void LateUpdate()
        {
            if (!_view) return;
            // The VM can disappear during Close before Hide. Keep it masked until
            // Hide instead of briefly revealing the remote window.
            if (_view.ViewModel != null)
            {
                if (IsHidden && (!Main.Multiplayer.IsActive || Main.Multiplayer.ShouldShowLevelingUI()))
                {
                    Restore();
                    // Re-run the normal appear animation when a disconnected
                    // owner's window is handed to the host; its saved alpha may
                    // have been zero before the background Bind.
                    _view.Show();
                }
                else Apply(_view);
            }
            else if (IsHidden) Mask();
        }

        public void Restore()
        {
            if (!IsHidden) return;
            foreach (var pair in _canvases) if (pair.Key) pair.Key.enabled = pair.Value;
            foreach (var pair in _groups) if (pair.Key) pair.Key.ignoreParentGroups = pair.Value;
            _canvases.Clear();
            _groups.Clear();
            if (_mask)
            {
                _mask.alpha = _alpha;
                _mask.interactable = _interactable;
                _mask.blocksRaycasts = _blocksRaycasts;
                _mask.ignoreParentGroups = _ignoreParent;
                // Retain an added neutral group, avoiding deferred Destroy racing
                // the next Bind in the same frame.
            }
            IsHidden = false;
            Main.GetLogger<LevelingOwnerVisibilityBehaviour>().LogInformation("[LevelingOwnerUI] Normal visibility restored.");
        }

        private void OnDisable() => Restore();
        private void OnEnable() { if (_view) Apply(_view); }
    }
}
