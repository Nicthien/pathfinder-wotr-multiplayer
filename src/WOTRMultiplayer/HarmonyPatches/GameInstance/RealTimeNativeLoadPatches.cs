using System;
using HarmonyLib;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Persistence;
using WOTRMultiplayer.Services;
using WOTRMultiplayer.Services.GameInteraction;

namespace WOTRMultiplayer.HarmonyPatches.GameInstance
{
    [HarmonyPatch(typeof(LoadingProcess), nameof(LoadingProcess.TickLoading))]
    public static class RealTimeNativeLoadFailurePatches
    {
        [HarmonyPrepare]
        public static bool Prepare() => RealTimePresentationRuntime.Enabled;
        [HarmonyFinalizer]
        public static void AfterTick(Exception __exception)
        {
            if (__exception != null) RealTimeNativeLoadLease.RecordFault("native-loading-process-failed:" + __exception.Message);
        }
    }

    [HarmonyPatch(typeof(ThreadedGameLoader), "DoLoad")]
    public static class RealTimeNativeThreadedLoadPatches
    {
        [HarmonyPrepare]
        public static bool Prepare() => RealTimePresentationRuntime.Enabled;
        [HarmonyPrefix]
        public static void BeforeLoad(SaveInfo ___m_SaveInfo, out IDisposable __state)
            => __state = RealTimeNativeLoadLease.EnterHydration(___m_SaveInfo);
        [HarmonyFinalizer]
        public static void AfterLoad(IDisposable __state, Exception __exception)
        {
            if (__state == null) return;
            try { if (__exception != null) RealTimeNativeLoadLease.RecordFault("native-threaded-load-failed:" + __exception.Message); }
            finally { __state.Dispose(); }
        }
    }

    [HarmonyPatch(typeof(AreaDataStash), nameof(AreaDataStash.UnstashAreaState))]
    public static class RealTimeNativeAreaLoadPatches
    {
        [HarmonyPrepare]
        public static bool Prepare() => RealTimePresentationRuntime.Enabled;
        [HarmonyPrefix]
        public static void BeforeLoad(AreaPersistentState __0, out IDisposable __state)
            => __state = RealTimeNativeLoadLease.EnterAreaHydration(__0?.AreaGuid.ToString());
        [HarmonyFinalizer]
        public static void AfterLoad(IDisposable __state, Exception __exception)
        {
            if (__state == null) return;
            try { if (__exception != null) RealTimeNativeLoadLease.RecordFault("native-area-load-failed:" + __exception.Message); }
            finally { __state.Dispose(); }
        }
    }
}
