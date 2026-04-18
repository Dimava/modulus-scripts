using Data.Buildings;
using HarmonyLib;
using Presentation.UI.OperatorUIs.OperatorPanelUIs.Buildings;
using ScriptEngine;
using UnityEngine;

/// <summary>
/// Always show the output section and estimated output panel, even while
/// a building is under initial construction (stage 0) or upgrading.
/// </summary>
[ScriptEntry]
public sealed class BuildingAlwaysShowOutput : ScriptMod { }

// During stage 0 the whole _fullOutputContainer is hidden. Force it visible
// whenever the building has an output resource defined.
[HarmonyPatch(typeof(BuildingPanelUI), "UpdateOutput")]
static class BuildingAlwaysShowOutput_UpdateOutput_Patch
{
    static void Postfix(BuildingPanelUI __instance)
    {
        var t = Traverse.Create(__instance);
        GameObject output = t.Field("_output").GetValue<GameObject>();
        if (output == null || !output.activeSelf) return;

        t.Field("_fullOutputContainer").GetValue<GameObject>()?.SetActive(true);
    }
}

// During upgrading, _estimatedOutputPanel is hidden and the text update is
// skipped. Briefly clear _isUpgrading so the method runs normally.
[HarmonyPatch(typeof(BuildingPanelUI), "UpdateOutputEstimates")]
static class BuildingAlwaysShowOutput_UpdateOutputEstimates_Patch
{
    static void Prefix(BuildingPanelUI __instance, out bool __state)
    {
        __state = false;
        BuildingBehaviour behaviour = Traverse.Create(__instance).Field("_behaviour").GetValue<BuildingBehaviour>();
        if (behaviour == null) return;

        __state = Traverse.Create(behaviour).Field("_isUpgrading").GetValue<bool>();
        if (__state)
            Traverse.Create(behaviour).Field("_isUpgrading").SetValue(false);
    }

    static void Postfix(BuildingPanelUI __instance, bool __state)
    {
        if (!__state) return;
        BuildingBehaviour behaviour = Traverse.Create(__instance).Field("_behaviour").GetValue<BuildingBehaviour>();
        if (behaviour != null)
            Traverse.Create(behaviour).Field("_isUpgrading").SetValue(true);
    }
}
