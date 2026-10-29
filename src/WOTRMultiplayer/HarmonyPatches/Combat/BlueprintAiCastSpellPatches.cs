using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Kingmaker.AI.Blueprints;
using Kingmaker.UnitLogic;
using Microsoft.Extensions.Logging;

namespace WOTRMultiplayer.HarmonyPatches.Combat
{
    [HarmonyPatch]
    public class BlueprintAiCastSpellPatches
    {
        [HarmonyPatch(typeof(BlueprintAiCastSpell), nameof(BlueprintAiCastSpell.CreateAbilityData))]
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> BlueprintAiCastSpell_CreateAbilityData_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var target = PatchesUtils.GetTranspilerTarget(MethodBase.GetCurrentMethod());
            var lookFor = AccessTools.Method(typeof(UnityEngine.Random), nameof(UnityEngine.Random.Range), [typeof(int), typeof(int)]);
            var replaceWithRandomSetVariant = AccessTools.Method(typeof(BlueprintAiCastSpellPatches), nameof(BlueprintAiCastSpellPatches.RollSetVariant));
            var matcher = new CodeMatcher(instructions);
            var match = matcher.SearchForward(x => x.Calls(lookFor));
            if (match.IsInvalid)
            {
                Main.GetLogger<BlueprintAiCastSpellPatches>().LogError("Transpiler has not been applied (SetVariant). Target={Target}", target);
                return instructions;
            }

            var variantSetInstructions = new List<CodeInstruction>()
            {
                new(OpCodes.Ldarg_0),
                new(OpCodes.Ldarg_1),
                new(OpCodes.Call, replaceWithRandomSetVariant),
            };
            match = match.RemoveInstruction().Insert(variantSetInstructions);

            var replaceWithRandomAbility = AccessTools.Method(typeof(BlueprintAiCastSpellPatches), nameof(BlueprintAiCastSpellPatches.RollAbility));
            match = matcher.SearchForward(x => x.Calls(lookFor));
            if (match.IsInvalid)
            {
                Main.GetLogger<BlueprintAiCastSpellPatches>().LogError("Transpiler has not been applied (RandomAbility). Target={Target}", target);
                return instructions;
            }

            var randomAbilityInstructions = new List<CodeInstruction>()
            {
                new(OpCodes.Ldarg_0),
                new(OpCodes.Ldarg_1),
                new(OpCodes.Call, replaceWithRandomAbility),
            };
            match = match.RemoveInstruction().Insert(randomAbilityInstructions);

            Main.GetLogger<BlueprintAiCastSpellPatches>().LogDebug("Transpiler has been applied (SetVariant + RandomAbility). Target={Target}", target);
            return matcher.Instructions();
        }

        private static int RollAbility(int minInclusive, int maxExclusive, BlueprintAiCastSpell aiCastSpell, UnitDescriptor caster)
        {
            if (!Main.Multiplayer.IsActive)
            {
                return UnityEngine.Random.Range(minInclusive, maxExclusive);
            }

            try
            {
                var seededContext = Main.Multiplayer.GetSeededContext();
                var identifier = $"{nameof(BlueprintAiCastSpell)}:{nameof(RollAbility)}:{caster.Unit?.UniqueId}:{aiCastSpell.Ability.name}_{seededContext.Id}";
                var index = Main.Multiplayer.ValueGenerator.Range(seededContext.Lifetime, identifier, minInclusive, maxExclusive);
                Main.GetLogger<BlueprintAiCastSpellPatches>().LogInformation("AI Random Ability has been rolled. Index={Index}, UnitId={UnitId}, Identifier={Identifier}", index, caster.Unit?.UniqueId, identifier);
                return index;
            }
            catch (Exception ex)
            {
                Main.GetLogger<BlueprintAiCastSpellPatches>().LogError(ex, "Error while rolling random AI ability variation");
                throw;
            }
        }

        private static int RollSetVariant(int minInclusive, int maxExclusive, BlueprintAiCastSpell aiCastSpell, UnitDescriptor caster)
        {
            if (!Main.Multiplayer.IsActive)
            {
                return UnityEngine.Random.Range(minInclusive, maxExclusive);
            }

            try
            {
                var seededContext = Main.Multiplayer.GetSeededContext();
                var identifier = $"{nameof(BlueprintAiCastSpell)}:{nameof(RollSetVariant)}:{caster.Unit?.UniqueId}:{aiCastSpell.Ability.name}_{seededContext.Id}";
                var index = Main.Multiplayer.ValueGenerator.Range(seededContext.Lifetime, identifier, minInclusive, maxExclusive);
                Main.GetLogger<BlueprintAiCastSpellPatches>().LogInformation("AI Ability Set Variant has been rolled. Index={Index}, UnitId={UnitId}, Identifier={Identifier}", index, caster.Unit?.UniqueId, identifier);
                return index;
            }
            catch (Exception ex)
            {
                Main.GetLogger<BlueprintAiCastSpellPatches>().LogError(ex, "Error while rolling random AI ability set variation");
                throw;
            }
        }
    }
}
