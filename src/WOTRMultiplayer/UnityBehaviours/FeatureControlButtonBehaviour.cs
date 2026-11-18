using Kingmaker.Localization;
using Owlcat.Runtime.UI.Controls.Button;
using TMPro;
using UnityEngine;
using WOTRMultiplayer.Entities;

namespace WOTRMultiplayer.UnityBehaviours
{
    public class FeatureControlButtonBehaviour : MonoBehaviour
    {
        private NetworkPlayerControlledFeature _feature;
        private OwlcatButton _button;
        private TextMeshProUGUI _label;

        public void Initialize(NetworkPlayerControlledFeature feature)
        {
            _feature = feature;
            _button = GetComponent<OwlcatButton>();
            _label = GetComponentInChildren<TextMeshProUGUI>();
            _button.OnLeftClick.RemoveAllListeners();
            _button.OnLeftClick.AddListener(() => Main.Multiplayer.RequestFeatureControl(_feature));
            Refresh();
        }

        private void Update() => Refresh();

        private void Refresh()
        {
            if (Main.Multiplayer == null || !Main.Multiplayer.IsActive || _button == null || _label == null) return;
            var isController = _feature switch
            {
                NetworkPlayerControlledFeature.Dialogs => Main.Multiplayer.CanControlDialog(),
                NetworkPlayerControlledFeature.VendorDeal => Main.Multiplayer.CanControlVendorDeals(),
                NetworkPlayerControlledFeature.CrusadeArmyCombat => Main.Multiplayer.CanControlTacticalCombat(),
                _ => false
            };
            var pending = Main.Multiplayer.IsFeatureControlTransferPending(_feature);
            var controller = Main.Multiplayer.GetFeatureControllerName(_feature);
            var label = pending
                ? Localized("wotrmultiplayer.lobbyWindow.featureControl.pending")
                : isController
                    ? Localized("wotrmultiplayer.lobbyWindow.featureControl.yours")
                    : string.Format(Localized("wotrmultiplayer.lobbyWindow.featureControl.take"), controller);
            _label.SetText(label);
            _button.Interactable = !pending && !isController;
        }

        private static string Localized(string key) => new LocalizedString { Key = key };
    }
}
