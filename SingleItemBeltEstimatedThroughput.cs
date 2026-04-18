using System;
using System.Collections.Generic;
using System.Linq;
using Data.Buildings;
using Data.FactoryFloor.Buildings;
using Data.Variables.Cranes;
using HarmonyLib;
using Logic.Factory;
using Presentation.UI;
using Presentation.UI.OperatorUIs.OperatorPanelUIs.Buildings;
using ScriptEngine;
using UnityEngine;

[ScriptEntry]
public sealed class SingleItemBeltEstimatedThroughput : ScriptMod
{
    public sealed class EstimateData
    {
        public int[] Requirements;
        public int[] AssignedCranes;
        public int CraneCount;
        public int OutputAmount;
        public double CraneItemsPerMinute;
        public double CraftingTime;
        public double EstimatedOutputPerMinute;
        public bool IsPossible;
    }

    private static double RoundToTwoDecimals(double value)
    {
        return (double)Mathf.Round((float)(value * 100.0)) * 0.01;
    }

    public static string FormatNumber(double value)
    {
        if (double.IsInfinity(value))
        {
            return "Infinity";
        }

        return value.ToString("0.##");
    }

    private static int[] CalculateAssignments(int[] requirements, int craneCount, out double craftingTime, out bool isPossible)
    {
        int requirementCount = requirements.Length;
        int[] assigned = new int[requirementCount];

        if (requirementCount == 0 || craneCount <= 0 || craneCount < requirementCount)
        {
            craftingTime = double.PositiveInfinity;
            isPossible = false;
            return assigned;
        }

        for (int i = 0; i < requirementCount; i++)
        {
            assigned[i] = 1;
        }

        int remaining = craneCount - requirementCount;
        while (remaining > 0)
        {
            double worstTime = 0.0;
            for (int i = 0; i < requirementCount; i++)
            {
                double time = (double)requirements[i] / assigned[i];
                if (time > worstTime)
                {
                    worstTime = time;
                }
            }

            for (int i = 0; i < requirementCount && remaining > 0; i++)
            {
                double time = (double)requirements[i] / assigned[i];
                if (Math.Abs(time - worstTime) < 1e-9)
                {
                    assigned[i]++;
                    remaining--;
                }
            }
        }

        craftingTime = 0.0;
        for (int i = 0; i < requirementCount; i++)
        {
            double time = (double)requirements[i] / assigned[i];
            if (time > craftingTime)
            {
                craftingTime = time;
            }
        }

        isPossible = true;
        return assigned;
    }

    public static bool TryBuildEstimate(BuildingBehaviour behaviour, BuildingCranesBehaviour cranesBehaviour, out EstimateData data)
    {
        data = null;
        if (behaviour == null || cranesBehaviour == null)
        {
            return false;
        }

        int[] requirements = behaviour.BuildRequirements
            .Select(requirement => requirement.Max)
            .Where(max => max > 0)
            .ToArray();

        if (requirements.Length == 0)
        {
            return false;
        }

        int outputAmount = 0;
        foreach (var output in behaviour.GetCurrentOutputs())
        {
            outputAmount = output.Item2;
            break;
        }

        double craneItemsPerMinute = (double)FactoryUpdater.Instance.GetUnscaledStepsPerSecond()
            / cranesBehaviour.UpdateFrequency
            * 60.0;

        double craftingTime;
        bool isPossible;
        int[] assignedCranes = CalculateAssignments(requirements, cranesBehaviour.Cranes.Count, out craftingTime, out isPossible);

        double estimatedOutputPerMinute = 0.0;
        if (isPossible && outputAmount > 0 && craftingTime > 0.0)
        {
            estimatedOutputPerMinute = RoundToTwoDecimals(craneItemsPerMinute / craftingTime * outputAmount);
        }

        data = new EstimateData
        {
            Requirements = requirements,
            AssignedCranes = assignedCranes,
            CraneCount = cranesBehaviour.Cranes.Count,
            OutputAmount = outputAmount,
            CraneItemsPerMinute = craneItemsPerMinute,
            CraftingTime = craftingTime,
            EstimatedOutputPerMinute = estimatedOutputPerMinute,
            IsPossible = isPossible
        };
        return true;
    }

    public static string BuildFormulaText(EstimateData data)
    {
        if (data == null)
        {
            return string.Empty;
        }

        if (!data.IsPossible)
        {
            return string.Format(
                "Need at least <color=#2AB1FF>{0}</color> dedicated input belts, only <color=#C43939>{1}</color> cranes available. Estimate = 0",
                data.Requirements.Length,
                data.CraneCount);
        }

        string[] terms = new string[data.Requirements.Length];
        for (int i = 0; i < data.Requirements.Length; i++)
        {
            terms[i] = string.Format(
                "<color=#2AB1FF>{0}</color>/<color=#C43939>{1}</color>",
                data.Requirements[i],
                data.AssignedCranes[i]);
        }

        return string.Format(
            "<color=#FFD926>{0}</color> / max({1}) * <color=#55C472>{2}</color> = {3}",
            FormatNumber(data.CraneItemsPerMinute),
            string.Join(", ", terms),
            data.OutputAmount,
            FormatNumber(data.EstimatedOutputPerMinute));
    }

    public static int GetActualCraneLimit(BuildingCranesBehaviour cranesBehaviour)
    {
        if (cranesBehaviour == null)
        {
            return 0;
        }

        CraneMaxAmountPerBuilding globalLimitVariable = Traverse.Create(cranesBehaviour)
            .Field("_maxAmountOfCranes")
            .GetValue<CraneMaxAmountPerBuilding>();

        int globalLimit = globalLimitVariable != null ? globalLimitVariable.Value : 0;
        int possiblePositions = cranesBehaviour.PossibleCranePositions != null ? cranesBehaviour.PossibleCranePositions.Count : 0;
        int buildingLimit = cranesBehaviour.Cranes.Count + possiblePositions;

        if (globalLimit <= 0)
        {
            return buildingLimit;
        }

        return Math.Min(globalLimit, buildingLimit);
    }
}

[HarmonyPatch(typeof(BuildingCranesBehaviour), "get_MaxAmountOfCranes")]
static class BuildingCranesBehaviour_MaxAmountOfCranes_Patch
{
    static bool Prefix(BuildingCranesBehaviour __instance, ref int __result)
    {
        __result = SingleItemBeltEstimatedThroughput.GetActualCraneLimit(__instance);
        return false;
    }
}

[HarmonyPatch(typeof(BuildingBehaviour), nameof(BuildingBehaviour.CalculateEstimatedOutputSpeed))]
static class BuildingBehaviour_CalculateEstimatedOutputSpeed_Patch
{
    static bool Prefix(BuildingBehaviour __instance, ref double __result)
    {
        BuildingCranesBehaviour cranesBehaviour;
        if (!__instance.FactoryObject.TryGetFactoryObjectBehaviour<BuildingCranesBehaviour>(out cranesBehaviour))
        {
            __result = 0.0;
            return false;
        }

        SingleItemBeltEstimatedThroughput.EstimateData data;
        if (!SingleItemBeltEstimatedThroughput.TryBuildEstimate(__instance, cranesBehaviour, out data) || data == null)
        {
            __result = 0.0;
            return false;
        }

        __result = data.EstimatedOutputPerMinute;
        return false;
    }
}

[HarmonyPatch(typeof(BuildingPanelUI), "UpdateOutputEstimates")]
static class BuildingPanelUI_UpdateOutputEstimates_Patch
{
    static bool Prefix(BuildingPanelUI __instance)
    {
        Traverse traverse = Traverse.Create(__instance);
        BuildingBehaviour behaviour = traverse.Field("_behaviour").GetValue<BuildingBehaviour>();
        GameObject estimatedOutputPanel = traverse.Field("_estimatedOutputPanel").GetValue<GameObject>();
        LocalizedTMPText estimatedOutputText = traverse.Field("_estimatedOutputText").GetValue<LocalizedTMPText>();
        TextInfoPanelContent estimatedOutputHoverTextPanel = traverse.Field("_estimatedOutputHoverTextPanel").GetValue<TextInfoPanelContent>();
        string estimatedOutputHoverLocaKey = traverse.Field("_estimatedOutputHoverLocaKey").GetValue<string>();

        bool shouldShow = behaviour != null && !behaviour.IsUpgrading && behaviour.GetCurrentOutputs().Any();
        if (estimatedOutputPanel != null)
        {
            estimatedOutputPanel.SetActive(shouldShow);
        }

        BuildingCranesBehaviour cranesBehaviour = null;
        if (behaviour != null && behaviour.FactoryObject != null)
        {
            cranesBehaviour = behaviour.FactoryObject.GetFactoryObjectBehaviour<BuildingCranesBehaviour>();
        }
        traverse.Field("_buildingCranesBehaviour").SetValue(cranesBehaviour);

        if (!shouldShow || cranesBehaviour == null || estimatedOutputText == null || estimatedOutputHoverTextPanel == null)
        {
            return false;
        }

        SingleItemBeltEstimatedThroughput.EstimateData data;
        if (!SingleItemBeltEstimatedThroughput.TryBuildEstimate(behaviour, cranesBehaviour, out data) || data == null)
        {
            estimatedOutputText.SetArguments("0");
            estimatedOutputHoverTextPanel.UpdateContent(estimatedOutputHoverLocaKey, "Estimate = 0");
            return false;
        }

        string estimateText = SingleItemBeltEstimatedThroughput.FormatNumber(data.EstimatedOutputPerMinute);
        string formulaText = SingleItemBeltEstimatedThroughput.BuildFormulaText(data);

        estimatedOutputText.SetArguments(estimateText);
        estimatedOutputHoverTextPanel.UpdateContent(estimatedOutputHoverLocaKey, formulaText);
        return false;
    }
}
