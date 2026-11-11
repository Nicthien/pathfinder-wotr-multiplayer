using HarmonyLib;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.View;

namespace WOTRMultiplayer.HarmonyPatches.MapObjects
{
    [HarmonyPatch]
    public static class AreaEnterPointPatches
    {
        // In capital party mode the native predicate only moves the main
        // character. Multiplayer characters still belong to the active party.
        [HarmonyPatch(typeof(AreaEnterPoint), nameof(AreaEnterPoint.ShouldMoveCharacterOnAreaEnterPoint))]
        [HarmonyPostfix]
        public static void AreaEnterPoint_ShouldMoveCharacterOnAreaEnterPoint_Postfix(UnitEntityData character, ref bool __result)
        {
            if (__result || Main.Multiplayer?.IsActive != true || character == null
                || Game.Instance?.Player?.CapitalPartyMode != true) return;
            var controlledUnit = character.Master ?? character;
            if (controlledUnit.Descriptor?.State?.IsFinallyDead == true) return;
            __result = Main.Multiplayer.IsControlledByPlayers(controlledUnit.UniqueId)
                || CapitalMercenaryPatches.IsCapitalMercenary(controlledUnit);
        }
    }
}
