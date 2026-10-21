using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kingmaker.Controllers.Clicks;
using Kingmaker.Controllers.Clicks.Handlers;
using UnityEngine;
using WOTRMultiplayer.Extensions;

namespace WOTRMultiplayer.HarmonyPatches.Clicks
{
    [HarmonyPatch]
    public static class MapPingClickPatches
    {
        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> Targets()
        {
            var signature = new[] { typeof(GameObject), typeof(Vector3), typeof(int),
                typeof(bool), typeof(bool), typeof(bool) };
            foreach (var type in new[] { typeof(ClickGroundHandler), typeof(ClickUnitHandler),
                typeof(ClickMapObjectHandler), typeof(ClickWithSelectedAbilityHandler), typeof(PlaceRestMarkerHandler) })
                yield return AccessTools.DeclaredMethod(type, "OnClick", signature)
                    ?? throw new MissingMethodException(type.FullName, "OnClick");
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static bool BeforeClick(Vector3 __1, int __2, bool __3, ref bool __result)
        {
            if (!Main.Multiplayer.IsActive || __3 || __2 != 0 || PointerController.InGui
                || !(Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))) return true;
            // Actual local clicks only; consume before movement, interaction,
            // casting and replicated commands. Never inspect keys in a replay.
            Main.Multiplayer.PingWorldPosition(__1.ToNetworkVector3());
            // False suppresses the normal click event and map-object postfix.
            // PointerController does not try another handler after this result.
            __result = false;
            return false;
        }
    }
}
