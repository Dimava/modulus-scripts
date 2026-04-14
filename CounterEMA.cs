using System.Collections.Generic;
using Data.FactoryFloor.FactoryObjectBehaviours;
using HarmonyLib;
using Logic.Factory;
using MelonLoader;
using UnityEngine;

// Dual-window counter: long window (120 s) for precision, short window (20 s) for fast
// response to real rate changes. Display uses the long window at steady state (stable,
// zero fluctuation for periodic patterns whose period divides 120 and 20), and blends
// toward the short window when they diverge significantly.
//
// 1,1,0,0 pattern (period 4): 4 divides both 120 and 20 → both windows are always equal
// → delta=0 → display = longCount, perfectly constant.
//
// Rate changes (e.g. 30→45): short window reflects the new rate within ~SHORT_SECS seconds
// while long window lags; delta grows → display tracks the short window quickly.

public static class CounterDualWindow
{
    static readonly HarmonyLib.Harmony _h = new HarmonyLib.Harmony("counter-dual-window");

    // Short window length in seconds. Must divide 120 and common machine periods.
    // 20 handles periods 1,2,4,5,10,20. Change to 12 for period-3/6 machines.
    public const int SHORT_SECS = 20;

    // How many items/120s of deviation triggers blending toward the short window.
    // Lower = more responsive, higher = more stable. ~5-10% of typical rate is good.
    public const float BLEND_THRESHOLD = 8f;

    // Per-instance short-window state (queue + rolling sum).
    internal sealed class ShortState
    {
        public Queue<bool> Hist  = new Queue<bool>();
        public int         Count;
    }
    internal static readonly Dictionary<CounterBehaviour, ShortState> States
        = new Dictionary<CounterBehaviour, ShortState>();

    public static void OnLoad()
    {
        _h.UnpatchSelf();
        _h.PatchAll(typeof(CounterDualWindow).Assembly);
        MelonLogger.Msg("[CounterDualWindow] Loaded.");
    }

    public static void OnUnload()
    {
        _h.UnpatchSelf();
        MelonLogger.Msg("[CounterDualWindow] Unloaded.");
    }
}

[HarmonyPatch(typeof(CounterBehaviour), nameof(CounterBehaviour.Update))]
static class CounterBehaviour_Update_DualWindow_Patch
{
    static bool Prefix(CounterBehaviour __instance)
    {
        var trav = Traverse.Create(__instance);

        // --- no output downstream ---
        bool hasOutput = trav.Method("HasOutputResourceHolder", new object[] { 0 }).GetValue<bool>();
        if (!hasOutput)
        {
            trav.Field("_counter").SetValue(0);
            trav.Field("_histogram").GetValue<Queue<bool>>().Clear();
            trav.Field("_outputResourceSuccessfully").SetValue(true);
            ShortState st;
            if (CounterDualWindow.States.TryGetValue(__instance, out st))
            {
                st.Hist.Clear();
                st.Count = 0;
            }
            __instance.OnCounterUpdated.Fire(0f);
            __instance.OnCalibrating.Fire(
                new CounterBehaviour.CalibratingValues(true, true, 0f));
            return false;
        }

        // --- tick rate ---
        int stepsPerSec = FactoryUpdater.Instance.GetStepsPerSecond();
        int updateFreq  = __instance.UpdateFrequency;
        int longLen     = stepsPerSec * 120               / updateFreq;
        int shortLen    = stepsPerSec * CounterDualWindow.SHORT_SECS / updateFreq;
        shortLen        = Mathf.Max(shortLen, 1);

        // --- sample ---
        bool inputFull = trav.Method("IsInputBufferFull", new object[] { 0 }).GetValue<bool>();
        bool outputOk  = trav.Field("_outputResourceSuccessfully").GetValue<bool>();
        bool passed    = inputFull && outputOk;

        // --- long window (stored in base-game fields) ---
        var longHist    = trav.Field("_histogram").GetValue<Queue<bool>>();
        int longCounter = trav.Field("_counter").GetValue<int>();
        longHist.Enqueue(passed);
        if (passed) longCounter++;
        while (longHist.Count > longLen)
            if (longHist.Dequeue()) longCounter--;
        trav.Field("_counter").SetValue(longCounter);

        // --- short window ---
        if (!CounterDualWindow.States.ContainsKey(__instance))
            CounterDualWindow.States[__instance] = new ShortState();
        ShortState shortState = CounterDualWindow.States[__instance];
        shortState.Hist.Enqueue(passed);
        if (passed) shortState.Count++;
        while (shortState.Hist.Count > shortLen)
            if (shortState.Hist.Dequeue()) shortState.Count--;

        // --- blend: short normalized to 120 s scale ---
        float longDisplay  = (float)longCounter;
        float shortDisplay = (float)shortState.Count * 120f / CounterDualWindow.SHORT_SECS;
        float delta        = Mathf.Abs(shortDisplay - longDisplay);
        float blend        = Mathf.Clamp01(delta / CounterDualWindow.BLEND_THRESHOLD);
        float display      = Mathf.Lerp(longDisplay, shortDisplay, blend);

        __instance.OnCounterUpdated.Fire(display);
        __instance.OnCalibrating.Fire(
            new CounterBehaviour.CalibratingValues(false, false, 1f));

        if (inputFull)
            trav.Field("_outputResourceSuccessfully").SetValue(false);

        trav.Method("TryOutput").GetValue();

        return false;
    }
}

// Seed short window from the tail of the saved long histogram on load.
[HarmonyPatch(typeof(CounterBehaviour), "ApplySaveState")]
static class CounterBehaviour_ApplySaveState_DualWindow_Patch
{
    static void Postfix(CounterBehaviour __instance)
    {
        var trav     = Traverse.Create(__instance);
        var longHist = trav.Field("_histogram").GetValue<Queue<bool>>();
        if (longHist == null || longHist.Count == 0) return;

        int stepsPerSec = FactoryUpdater.Instance.GetStepsPerSecond();
        int updateFreq  = __instance.UpdateFrequency;
        if (updateFreq <= 0 || stepsPerSec <= 0) return;

        int shortLen = Mathf.Max(stepsPerSec * CounterDualWindow.SHORT_SECS / updateFreq, 1);

        // Take the most recent shortLen samples from the tail of the long histogram.
        // Queue iterates oldest-first so we skip the front and keep the tail.
        var arr   = longHist.ToArray(); // oldest at [0]
        int start = Mathf.Max(0, arr.Length - shortLen);

        var state = new ShortState();
        for (int i = start; i < arr.Length; i++)
        {
            state.Hist.Enqueue(arr[i]);
            if (arr[i]) state.Count++;
        }
        CounterDualWindow.States[__instance] = state;
    }
}

[HarmonyPatch(typeof(CounterBehaviour), nameof(CounterBehaviour.UnInit))]
static class CounterBehaviour_UnInit_DualWindow_Patch
{
    static void Postfix(CounterBehaviour __instance)
    {
        CounterDualWindow.States.Remove(__instance);
    }
}
