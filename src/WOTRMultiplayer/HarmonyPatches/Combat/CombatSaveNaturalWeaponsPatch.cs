using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Kingmaker.Blueprints.Items;
using Kingmaker.Designers.Mechanics.Buffs;
using Kingmaker.Items;
using Kingmaker.UnitLogic.FactLogic;
using WOTRMultiplayer.Services.CombatSaving;

namespace WOTRMultiplayer.HarmonyPatches.Combat
{
    // Keep native TurnOff/serialization semantics. On rollback, re-equip the
    // exact removed objects instead of allocating new IDs and losing item state.
    [HarmonyPatch(typeof(EmptyHandWeaponOverride), "OnTurnOff")]
    internal static class CombatSaveRememberEmptyHandPatch
    {
        [HarmonyPrefix]
        internal static void Prefix(EmptyHandWeaponOverride __instance)
        {
            if (CombatSaveCaptureScope.Active)
                CombatSaveCaptureScope.RememberWeapons(__instance.Data, new[] { __instance.Data.WeaponEntity });
        }
    }
    [HarmonyPatch(typeof(AddSecondaryAttacks), "OnTurnOff")]
    internal static class CombatSaveRememberSecondaryPatch
    {
        [HarmonyPrefix]
        internal static void Prefix(AddSecondaryAttacks __instance)
        {
            if (CombatSaveCaptureScope.Active)
                CombatSaveCaptureScope.RememberWeapons(__instance.Data,
                    __instance.Data.Added.Select(index => __instance.Owner.Body.AdditionalLimbs[index].Weapon));
        }
    }
    [HarmonyPatch(typeof(AddAdditionalLimb), "OnTurnOff")]
    internal static class CombatSaveRememberAdditionalLimbPatch
    {
        [HarmonyPrefix]
        internal static void Prefix(AddAdditionalLimb __instance)
        {
            if (!CombatSaveCaptureScope.Active) return;
            var index = __instance.Data.LimbIndex;
            var body = __instance.Owner.Body;
            if (index < 0 || index >= body.AdditionalLimbs.Count)
                throw new InvalidOperationException("combat-save-additional-limb-index-invalid");
            var weapons = new List<ItemEntityWeapon> { body.AdditionalLimbs[index].Weapon };
            // A weapon marked KeepInPolymorph is represented by a second
            // temporary item. AddLimbToIndex recreates the human item first,
            // then this mapped polymorph item; retain both in that exact order.
            if (body.m_PolymorphAdditionalLimbs != null
                && body.m_HumanToPolymorphAdditionalLimbsMapping.TryGetValue(index, out var polymorphIndex)
                && polymorphIndex >= 0 && polymorphIndex < body.m_PolymorphAdditionalLimbs.Count)
            {
                var polymorph = body.m_PolymorphAdditionalLimbs[polymorphIndex];
                if (polymorph.HasItem) weapons.Add(polymorph.Weapon);
            }
            CombatSaveCaptureScope.RememberWeapons(__instance.Data, weapons);
        }
    }
    [HarmonyPatch(typeof(EmptyHandWeaponOverride), "OnTurnOn")]
    internal static class CombatSaveRestoreEmptyHandPatch
    {
        [HarmonyPrefix]
        internal static void Prefix(EmptyHandWeaponOverride __instance, out IDisposable __state)
            => __state = CombatSaveCaptureScope.RestoreWeapons(__instance.Data);
        [HarmonyFinalizer]
        internal static void Finalizer(IDisposable __state) => __state?.Dispose();
    }
    [HarmonyPatch(typeof(AddSecondaryAttacks), "OnTurnOn")]
    internal static class CombatSaveRestoreSecondaryPatch
    {
        [HarmonyPrefix]
        internal static void Prefix(AddSecondaryAttacks __instance, out IDisposable __state)
            => __state = CombatSaveCaptureScope.RestoreWeapons(__instance.Data);
        [HarmonyFinalizer]
        internal static void Finalizer(IDisposable __state) => __state?.Dispose();
    }
    [HarmonyPatch(typeof(AddAdditionalLimb), "OnTurnOn")]
    internal static class CombatSaveRestoreAdditionalLimbPatch
    {
        [HarmonyPrefix]
        internal static void Prefix(AddAdditionalLimb __instance, out IDisposable __state)
            => __state = CombatSaveCaptureScope.RestoreWeapons(__instance.Data);
        [HarmonyFinalizer]
        internal static void Finalizer(IDisposable __state) => __state?.Dispose();
    }
    [HarmonyPatch(typeof(ItemsEntityFactory), "TryCreateWeapon")]
    internal static class CombatSaveReuseNaturalWeaponPatch
    {
        [HarmonyPrefix]
        internal static bool Prefix(BlueprintItem bpItem, ref ItemEntity __result)
        {
            var weapon = CombatSaveCaptureScope.ReuseWeapon(bpItem);
            if (weapon == null) return true;
            __result = weapon;
            return false;
        }
    }
}
