using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using Kingmaker;
using Kingmaker.Armies.TacticalCombat;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Parts;

namespace WOTRMultiplayer.HarmonyPatches
{
    // Extend only the capital's party exception, never IsMainCharacter globally.
    [HarmonyPatch]
    internal static class CapitalMercenaryPatches
    {
        private static readonly object TransitionLock = new();
        private static readonly HashSet<string> TransitionMercenaries = new(StringComparer.OrdinalIgnoreCase);

        internal static bool IsEligible(bool multiplayer, bool capital, bool tactical,
            bool custom, bool activeParty, bool scriptedOrHidden)
            => multiplayer && capital && !tactical && custom && activeParty && !scriptedOrHidden;

        internal static bool IsCapitalMercenary(UnitEntityData unit)
        {
            if (unit == null || Main.Multiplayer?.IsActive != true || TacticalCombatHelper.IsActive)
                return false;
            var capital = Game.Instance?.Player?.CapitalPartyMode == true;
            bool leased;
            lock (TransitionLock) leased = TransitionMercenaries.Contains(unit.UniqueId);
            if (!capital && !leased) return false;
            var companion = unit.Get<UnitPartCompanion>();
            return IsEligible(true, capital || leased, false, unit.IsCustomCompanion(),
                companion != null && (int)companion.State == 2,
                companion == null || companion.ShouldBeHidden || companion.IsIgnoreFixInGame
                || companion.IsEtudeBracketSetCompanionPosition || companion.GetCurrentSpawner() != null);
        }

        internal static void CaptureTransitionMercenaries()
        {
            lock (TransitionLock)
            {
                TransitionMercenaries.Clear();
                if (Main.Multiplayer?.IsActive != true || Game.Instance?.Player?.CapitalPartyMode != true
                    || TacticalCombatHelper.IsActive) return;
                foreach (var unit in Game.Instance.Player.PartyAndPets)
                    if (IsCapitalMercenary(unit)) TransitionMercenaries.Add(unit.UniqueId);
            }
        }

        internal static void ClearTransitionMercenaries()
        {
            lock (TransitionLock) TransitionMercenaries.Clear();
        }

        private static bool IsMainOrCapitalMercenary(UnitEntityData unit)
            => unit.IsMainCharacter || IsCapitalMercenary(unit);

        private static bool IsMainOrControllableMercenary(UnitEntityData unit)
            => unit.IsMainCharacter || (IsCapitalMercenary(unit)
                && unit.Faction.IsDirectlyControllable && !unit.Descriptor.State.IsFinallyDead
                && !unit.Descriptor.State.IsPanicked && !unit.IsDetached);

        // Preserve the native in-game, pet, spawner and companion-state checks.
        [HarmonyPatch(typeof(Player), "AddCharacterToLists")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> PartyList(IEnumerable<CodeInstruction> instructions)
            => ReplaceMainCheck(instructions, nameof(IsMainOrCapitalMercenary), 1);

        [HarmonyPatch(typeof(UnitEntityData), nameof(UnitEntityData.IsDirectlyControllable), MethodType.Getter)]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> DirectControl(IEnumerable<CodeInstruction> instructions)
            => ReplaceMainCheck(instructions, nameof(IsMainOrControllableMercenary), 2);

        internal static IEnumerable<CodeInstruction> ReplaceMainCheck(IEnumerable<CodeInstruction> instructions,
            string replacement, int expected)
        {
            var code = instructions.Select(x => new CodeInstruction(x)).ToList();
            var getter = AccessTools.PropertyGetter(typeof(UnitEntityData), nameof(UnitEntityData.IsMainCharacter));
            var matches = code.Where(x => x.Calls(getter)).ToList();
            if (matches.Count != expected)
                throw new InvalidOperationException("Capital mercenary patch: unexpected engine layout; refusing partial patch.");
            foreach (var instruction in matches)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(CapitalMercenaryPatches), replacement);
            }
            return code;
        }

        [HarmonyPatch(typeof(UnitPartCompanion), nameof(UnitPartCompanion.IsControllableInParty))]
        [HarmonyPostfix]
        private static void CompanionControl(UnitPartCompanion __instance, ref bool __result)
        {
            if (!__result && IsCapitalMercenary(__instance.Owner)) __result = true;
        }

        // Use the native party placement only when the mercenary needs restoring.
        // No new entity, companion-state change or per-frame teleport is introduced.
        [HarmonyPatch(typeof(Player), "FixPartyMember")]
        [HarmonyPrefix]
        private static void FixPartyMember(UnitEntityData __0, ref bool __2)
        {
            if (IsCapitalMercenary(__0)) __2 = true;
        }
    }
}
