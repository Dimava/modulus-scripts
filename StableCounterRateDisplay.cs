using System.Collections.Generic;
using Data.FactoryFloor.FactoryObjectBehaviours;
using HarmonyLib;
using Logic.Factory;
using ScriptEngine;
using UnityEngine;

// Dual-window counter: long window (120 s) for precision, short window (20 s) for fast
// response to real rate changes. Display uses the long window at steady state (stable,
// zero fluctuation for periodic patterns whose period divides 120 and 20), and blends
// toward the short window when they diverge significantly.
//
// 1,1,0,0 pattern (period 4): 4 divides both 120 and 20 -> both windows are always equal
// -> delta=0 -> display = longCount, perfectly constant.
//
// Rate changes (e.g. 30->45): short window reflects the new rate within ~SHORT_SECS seconds
// while long window lags; delta grows -> display tracks the short window quickly.
[ScriptEntry]
public sealed class StableCounterRateDisplay : ScriptMod
{
    public const int SHORT_SECS = 20;
    public const float BLEND_THRESHOLD = 8f;

    internal sealed class ShortState
    {
        public Queue<bool> Hist = new Queue<bool>();
        public int Count;
    }

    internal static readonly Dictionary<CounterBehaviour, ShortState> States = new Dictionary<CounterBehaviour, ShortState>();

    protected override void OnDisable()
    {
        States.Clear();
    }
}

[HarmonyPatch(typeof(CounterBehaviour), nameof(CounterBehaviour.Update))]
static class CounterBehaviour_Update_DualWindow_Patch
{
    static bool Prefix(CounterBehaviour __instance)
    {
        var trav = Traverse.Create(__instance);

        bool hasOutput = trav.Method("HasOutputResourceHolder", new object[] { 0 }).GetValue<bool>();
        if (!hasOutput)
        {
            trav.Field("_counter").SetValue(0);
            trav.Field("_histogram").GetValue<Queue<bool>>().Clear();
            trav.Field("_outputResourceSuccessfully").SetValue(true);
            StableCounterRateDisplay.ShortState state;
            if (StableCounterRateDisplay.States.TryGetValue(__instance, out state))
            {
                state.Hist.Clear();
                state.Count = 0;
            }
            __instance.OnCounterUpdated.Fire(0f);
            __instance.OnCalibrating.Fire(new CounterBehaviour.CalibratingValues(true, true, 0f));
            return false;
        }

        int stepsPerSec = FactoryUpdater.Instance.GetStepsPerSecond();
        int updateFreq = __instance.UpdateFrequency;
        int longLen = stepsPerSec * 120 / updateFreq;
        int shortLen = stepsPerSec * StableCounterRateDisplay.SHORT_SECS / updateFreq;
        shortLen = Mathf.Max(shortLen, 1);

        bool inputFull = trav.Method("IsInputBufferFull", new object[] { 0 }).GetValue<bool>();
        bool outputOk = trav.Field("_outputResourceSuccessfully").GetValue<bool>();
        bool passed = inputFull && outputOk;

        var longHist = trav.Field("_histogram").GetValue<Queue<bool>>();
        int longCounter = trav.Field("_counter").GetValue<int>();
        longHist.Enqueue(passed);
        if (passed) longCounter++;
        while (longHist.Count > longLen)
            if (longHist.Dequeue()) longCounter--;
        trav.Field("_counter").SetValue(longCounter);

        if (!StableCounterRateDisplay.States.ContainsKey(__instance))
            StableCounterRateDisplay.States[__instance] = new StableCounterRateDisplay.ShortState();
        StableCounterRateDisplay.ShortState shortState = StableCounterRateDisplay.States[__instance];
        shortState.Hist.Enqueue(passed);
        if (passed) shortState.Count++;
        while (shortState.Hist.Count > shortLen)
            if (shortState.Hist.Dequeue()) shortState.Count--;

        float longDisplay = longCounter;
        float shortDisplay = shortState.Count * 120f / StableCounterRateDisplay.SHORT_SECS;
        float delta = Mathf.Abs(shortDisplay - longDisplay);
        float blend = Mathf.Clamp01(delta / StableCounterRateDisplay.BLEND_THRESHOLD);
        float display = Mathf.Lerp(longDisplay, shortDisplay, blend);

        __instance.OnCounterUpdated.Fire(display);
        __instance.OnCalibrating.Fire(new CounterBehaviour.CalibratingValues(false, false, 1f));

        if (inputFull)
            trav.Field("_outputResourceSuccessfully").SetValue(false);

        trav.Method("TryOutput").GetValue();
        return false;
    }
}

[HarmonyPatch(typeof(CounterBehaviour), "ApplySaveState")]
static class CounterBehaviour_ApplySaveState_DualWindow_Patch
{
    static void Postfix(CounterBehaviour __instance)
    {
        var trav = Traverse.Create(__instance);
        var longHist = trav.Field("_histogram").GetValue<Queue<bool>>();
        if (longHist == null || longHist.Count == 0) return;

        int stepsPerSec = FactoryUpdater.Instance.GetStepsPerSecond();
        int updateFreq = __instance.UpdateFrequency;
        if (updateFreq <= 0 || stepsPerSec <= 0) return;

        int shortLen = Mathf.Max(stepsPerSec * StableCounterRateDisplay.SHORT_SECS / updateFreq, 1);
        var arr = longHist.ToArray();
        int start = Mathf.Max(0, arr.Length - shortLen);

        var state = new StableCounterRateDisplay.ShortState();
        for (int i = start; i < arr.Length; i++)
        {
            state.Hist.Enqueue(arr[i]);
            if (arr[i]) state.Count++;
        }
        StableCounterRateDisplay.States[__instance] = state;
    }
}

[HarmonyPatch(typeof(CounterBehaviour), nameof(CounterBehaviour.UnInit))]
static class CounterBehaviour_UnInit_DualWindow_Patch
{
    static void Postfix(CounterBehaviour __instance)
    {
        StableCounterRateDisplay.States.Remove(__instance);
    }
}
