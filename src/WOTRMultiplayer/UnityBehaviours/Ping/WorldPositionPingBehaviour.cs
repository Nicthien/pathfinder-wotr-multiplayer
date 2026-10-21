using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.UI;
using Owlcat.Runtime.Core.Utils;
using UnityEngine;

namespace WOTRMultiplayer.UnityBehaviours.Ping
{
    public class WorldPositionPingBehaviour : DecayingBehaviourBase
    {
        private List<MeshRenderer> _renderers;

        public void Begin(TimeSpan expiration, Vector3 position, Vector3? scale = null)
        {
            transform.SetParent(ClickPointerManager.Instance.transform);
            transform.position = position;
            if (scale.HasValue)
            {
                transform.localScale += scale.Value;
            }

            Begin(expiration);
        }

        protected override void OnExpired()
        {
            Destroy(gameObject);
        }

        protected override void OnPartialDecay(float decayState)
        {
            var transparency = Mathf.Clamp01(1f - decayState + 0.25f);

            foreach (var renderer in _renderers)
            {
                var color = renderer.material.color;
                color.a = transparency;
                renderer.material.color = color;
            }
        }

        private void Awake()
        {
            InitRenderers();
        }

        private void InitRenderers()
        {
            _renderers = [.. GetComponentsInChildren<MeshRenderer>(true)];
            SetPlayerColor(true);
        }

        public void SetPlayerColor(bool host)
        {
            var even = host ? new Color(0.15f, 0.55f, 1f, 1f) : new Color(1f, 0.45f, 0.1f, 1f);
            var odd = host ? new Color(0.65f, 0.85f, 1f, 1f) : new Color(1f, 0.8f, 0.4f, 1f);
            for (int i = 0; i < _renderers.Count; i++)
            {
                var color = i % 2 == 0 ? even : odd;
                var renderer = _renderers[i];
                renderer.material.color = color;
            }
        }

        private void OnDestroy()
        {
            if (_renderers == null) return;
            foreach (var renderer in _renderers)
                if (renderer != null) Destroy(renderer.material);
        }
    }
}
