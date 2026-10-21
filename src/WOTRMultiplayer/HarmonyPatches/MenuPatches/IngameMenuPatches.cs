using HarmonyLib;
using Kingmaker;
using System;
using Kingmaker.UI.MVVM._VM.IngameMenu;

namespace WOTRMultiplayer.HarmonyPatches.MenuPatches
{
    [HarmonyPatch]
    public class IngameMenuPatches
    {
        private static WeakReference<IngameMenuVM> _modeMenu;

        [HarmonyPatch(typeof(IngameMenuVM), MethodType.Constructor)]
        [HarmonyPostfix]
        public static void IngameMenuVM_Constructed_Postfix(IngameMenuVM __instance)
        {
            _modeMenu = new WeakReference<IngameMenuVM>(__instance);
        }

        [HarmonyPatch(typeof(IngameMenuVM), "DisposeImplementation")]
        [HarmonyPostfix]
        public static void IngameMenuVM_Disposed_Postfix(IngameMenuVM __instance)
        {
            if (_modeMenu != null && _modeMenu.TryGetTarget(out var menu) && ReferenceEquals(menu, __instance))
                _modeMenu = null;
        }

        // Unity-thread only: display the actual engine setting, not an uncommitted request.
        internal static void RefreshCombatModeButton()
        {
            if (_modeMenu != null && _modeMenu.TryGetTarget(out var menu))
                menu.InitTurnBasedModeValues();
        }

        [HarmonyPatch(typeof(IngameMenuVM), nameof(IngameMenuVM.SwitchTBM))]
        [HarmonyPrefix]
        public static bool IngameMenuVM_SwitchTBM_Prefix()
        {
            if (!Main.Multiplayer.IsActive) return true;
            // Never toggle one engine independently of the other peer.
            Main.Multiplayer.ToggleNetworkCombatMode();
            return false;
        }

        [HarmonyPatch(typeof(IngameMenuVM), nameof(IngameMenuVM.OpenRestCamp))]
        [HarmonyPrefix]
        public static void IngameMenuVM_OpenRestCamp_Prefix()
        {
            if (!Main.Multiplayer.IsActive || !Game.Instance.Player.CapitalPartyMode)
            {
                return;
            }

            Main.Multiplayer.OnCapitalModeRest();
        }
    }
}
