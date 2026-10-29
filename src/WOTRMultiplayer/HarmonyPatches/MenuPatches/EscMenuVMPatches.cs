using HarmonyLib;
using Kingmaker.UI;
using Kingmaker.UI.MVVM._VM.EscMenu;

namespace WOTRMultiplayer.HarmonyPatches.MenuPatches
{
    [HarmonyPatch]
    public class EscMenuVMPatches
    {
        [HarmonyPatch(typeof(EscMenuVM), MethodType.Constructor, new[] { typeof(System.Action) })]
        [HarmonyPostfix]
        public static void EscMenuVM_Constructed_Postfix(EscMenuVM __instance)
        {
            if (Main.Multiplayer?.CanSaveCombatFromMenu != true) return;
            // Replace only this menu entry's command/availability. Never globally
            // authorize native saves, quicksaves or overwriting a combat archive.
            var save = new Kingmaker.UI.MVVM._VM.ContextMenu.ContextMenuEntityVM(
                new Kingmaker.UI.MVVM._VM.ContextMenu.ContextMenuCollectionEntity(
                    Kingmaker.UI.Common.UIUtility.GetSaberBookFormat(Kingmaker.Blueprints.Root.Strings.UIStrings.Instance.EscMenuTexts.SaveButton),
                    __instance.OnSave, () => Main.Multiplayer.CanSaveCombatFromMenu
                        && !Kingmaker.Settings.SettingsRoot.Difficulty.OnlyOneSave));
            AccessTools.Field(typeof(EscMenuVM), nameof(EscMenuVM.SaveVm)).SetValue(__instance, save);
            __instance.AddDisposable(save);
        }

        [HarmonyPatch(typeof(EscMenuVM), nameof(EscMenuVM.OnSave))]
        [HarmonyPrefix]
        public static bool EscMenuVM_OnSave_Prefix(EscMenuVM __instance)
            => RouteSave(Main.Multiplayer, Kingmaker.Settings.SettingsRoot.Difficulty.OnlyOneSave, __instance.OnClose);

        internal static bool RouteSave(Abstractions.IMultiplayer multiplayer, bool onlyOneSave, System.Action close)
        {
            if (multiplayer?.IsActive != true || !multiplayer.IsInCombat) return true;
            // Close EscMode before the coordinator checks native world readiness.
            // Even a stale button must not fall through to a native combat save.
            if (multiplayer.CanSaveCombatFromMenu && !onlyOneSave)
            {
                close();
                multiplayer.SaveCombatFromMenu();
            }
            return false;
        }

        [HarmonyPatch(typeof(EscMenuVM), nameof(EscMenuVM.OnQuitToMainMenuAction))]
        [HarmonyPrefix]
        public static void EscMenuVM_OnQuitToMainMenuAction_Prefix(MessageModalBase.ButtonType buttonType)
        {
            if (buttonType == MessageModalBase.ButtonType.Yes)
            {
                Main.Multiplayer.TerminateMultiplayer();
            }
        }
    }
}
