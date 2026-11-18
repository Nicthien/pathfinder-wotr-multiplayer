using TMPro;
using UnityEngine;
using UnityEngine.UI;
using WOTRMultiplayer.Entities;
using WOTRMultiplayer.UnityBehaviours;

namespace WOTRMultiplayer.UI
{
    public static class FeatureControlButtonFactory
    {
        public static void Add(Transform parent, NetworkPlayerControlledFeature feature)
        {
            if (parent == null || Main.Multiplayer?.UIFactory == null) return;
            var name = "WOTRFeatureControl_" + feature;
            var existing = parent.Find(name);
            var buttonObject = existing != null ? existing.gameObject : Main.Multiplayer.UIFactory.CreateButton(parent);
            buttonObject.name = name;
            var layout = buttonObject.GetComponent<LayoutElement>() ?? buttonObject.AddComponent<LayoutElement>();
            layout.minWidth = layout.preferredWidth = 190f;
            layout.minHeight = layout.preferredHeight = 40f;
            var label = buttonObject.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null)
            {
                label.enableAutoSizing = true;
                label.fontSizeMin = 12;
                label.fontSizeMax = 18;
            }
            var behaviour = buttonObject.GetComponent<FeatureControlButtonBehaviour>() ?? buttonObject.AddComponent<FeatureControlButtonBehaviour>();
            behaviour.Initialize(feature);
        }
    }
}
