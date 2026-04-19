using Data.Variables;
using HarmonyLib;
using Presentation.UI;
using ScriptEngine;
using UnityEngine.UI;

/// <summary>
/// Raises the settings slider cap for the existing max zoom-out modifier.
/// The base camera already supports larger modifier values; this exposes more range.
/// </summary>
[ScriptEntry]
public sealed class CameraZoomLimitCap : ScriptMod
{
    internal const float SliderCap = 300f;

    private static CameraZoomLimitCap _instance;
    private static bool _loggedCapRaise;

    protected override void OnEnable()
    {
        _instance = this;
        Log($"Camera zoom modifier slider cap set to {SliderCap:0}.");
    }

    protected override void OnDisable()
    {
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }
    }

    internal static void MaybeLogCapRaise(float oldMax, float newMax)
    {
        if (_loggedCapRaise)
        {
            return;
        }

        _loggedCapRaise = true;
        _instance?.Log($"Raised camera zoom modifier slider cap from {oldMax:0} to {newMax:0}.");
    }
}

[HarmonyPatch(typeof(SettingsDisplay), "InitMaxZoomLevelModifier")]
static class CameraZoomLimitCap_SettingsDisplay_InitMaxZoomLevelModifier_Patch
{
    static void Postfix(SettingsDisplay __instance)
    {
        Slider slider = Traverse.Create(__instance).Field("_maxZoomLevelModifierSlider").GetValue<Slider>();
        if (slider == null)
        {
            return;
        }

        float oldMax = slider.maxValue;
        if (oldMax >= CameraZoomLimitCap.SliderCap)
        {
            return;
        }

        slider.maxValue = CameraZoomLimitCap.SliderCap;
        slider.wholeNumbers = true;

        MaxZoomLevelModifierSO zoomModifier = Traverse.Create(__instance).Field("_maxZoomLevelModifier").GetValue<MaxZoomLevelModifierSO>();
        if (zoomModifier != null)
        {
            slider.SetValueWithoutNotify(zoomModifier.Value);
        }

        CameraZoomLimitCap.MaybeLogCapRaise(oldMax, slider.maxValue);
    }
}
