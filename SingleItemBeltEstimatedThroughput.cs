using System;
using System.Globalization;
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
        public int[] CraneIconCounts;
        public int CraneCount;
        public int OutputAmount;
        public double CraneItemsPerMinute;
        public double CraftingTime;
        public double MinCoverage;
        public double EstimatedOutputPerMinute;
        public bool IsCoverageMaxed;
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

        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static int GetOutputAmount(BuildingBehaviour behaviour)
    {
        foreach (var output in behaviour.GetCurrentOutputs())
        {
            return output.Item2;
        }

        return 0;
    }

    private static double GetCraneItemsPerMinute(BuildingCranesBehaviour cranesBehaviour)
    {
        return (double)FactoryUpdater.Instance.GetUnscaledStepsPerSecond()
            / cranesBehaviour.UpdateFrequency
            * 60.0;
    }

    private static int[] BuildCraneIconCounts(BuildingBehaviour behaviour, int[] requirements)
    {
        int smallestAmount;
        int smallestMultiplier = behaviour.GetSmallestMultiplier(out smallestAmount);
        if (smallestAmount <= 0)
        {
            return new int[requirements.Length];
        }

        return requirements.Select(requirement => requirement * smallestMultiplier / smallestAmount).ToArray();
    }

    private static double CalculateMinCoverage(int[] craneIconCounts, int[] assignedCranes)
    {
        double minCoverage = double.MaxValue;
        for (int i = 0; i < craneIconCounts.Length; i++)
        {
            if (craneIconCounts[i] <= 0)
            {
                continue;
            }

            minCoverage = Math.Min(minCoverage, (double)assignedCranes[i] / craneIconCounts[i]);
        }

        return minCoverage == double.MaxValue ? 0.0 : minCoverage;
    }

    private static string BuildDistributionText(EstimateData data)
    {
        int[] displayedAssignedCranes = data.CraneIconCounts
            .Select((count, index) =>
            {
                if (count <= 0 || index >= data.AssignedCranes.Length)
                {
                    return 0;
                }

                int requiredForCurrentCoverage = (int)Math.Ceiling(count * data.MinCoverage - 1e-9);
                return Math.Min(data.AssignedCranes[index], requiredForCurrentCoverage);
            })
            .ToArray();
        int unusedCranes = Math.Max(0, data.CraneCount - displayedAssignedCranes.Sum());
        string assignedTerms = string.Join(",",
            displayedAssignedCranes.Select((count, index) =>
            {
                bool isBottleneck = !data.IsCoverageMaxed
                    && index < data.CraneIconCounts.Length
                    && data.CraneIconCounts[index] > 0
                    && Math.Abs((double)count / data.CraneIconCounts[index] - data.MinCoverage) < 1e-9;
                string suffix = isBottleneck ? "<color=#FFB020>^</color>" : string.Empty;
                return $"<color=#EAEAEA>{count}</color>{suffix}";
            }));
        string unusedSuffix = unusedCranes > 0 ? $" + <color=#EAEAEA>{unusedCranes}</color>" : string.Empty;
        string maxSuffix = data.IsCoverageMaxed ? "  <color=#C8C8C8>(max)</color>" : string.Empty;
        return $"<color=#2AB1FF>{FormatNumber(data.MinCoverage)}x</color> ({assignedTerms}){unusedSuffix}{maxSuffix}";
    }

    private static string BuildCraftingTimeExpression(EstimateData data)
    {
        return string.Join(",",
            data.Requirements.Zip(data.AssignedCranes, (requirement, assigned) =>
                $"<color=#2AB1FF>{requirement}</color>/<color=#EAEAEA>{assigned}</color>"));
    }

    private static bool TryGetItemsPerShownCrane(EstimateData data, out double itemsPerShownCrane)
    {
        itemsPerShownCrane = 0.0;
        bool foundValue = false;

        for (int i = 0; i < data.Requirements.Length; i++)
        {
            int shownCranes = data.CraneIconCounts[i];
            if (shownCranes <= 0)
            {
                continue;
            }

            double value = (double)data.Requirements[i] / shownCranes;
            if (!foundValue)
            {
                itemsPerShownCrane = value;
                foundValue = true;
                continue;
            }

            if (Math.Abs(itemsPerShownCrane - value) > 1e-9)
            {
                itemsPerShownCrane = 0.0;
                return false;
            }
        }

        return foundValue;
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

        int outputAmount = GetOutputAmount(behaviour);
        double craneItemsPerMinute = GetCraneItemsPerMinute(cranesBehaviour);

        double craftingTime;
        bool isPossible;
        int craneCount = cranesBehaviour.Cranes.Count;
        int[] assignedCranes = CalculateAssignments(requirements, craneCount, out craftingTime, out isPossible);
        int[] craneIconCounts = BuildCraneIconCounts(behaviour, requirements);
        double minCoverage = CalculateMinCoverage(craneIconCounts, assignedCranes);
        int maxCraneCount = GetActualCraneLimit(cranesBehaviour);
        bool isCoverageMaxed = false;

        if (maxCraneCount > 0)
        {
            double maxCraftingTime;
            bool isMaxPossible;
            int[] maxAssignedCranes = CalculateAssignments(requirements, maxCraneCount, out maxCraftingTime, out isMaxPossible);
            double maxCoverage = isMaxPossible ? CalculateMinCoverage(craneIconCounts, maxAssignedCranes) : 0.0;
            isCoverageMaxed = FormatNumber(minCoverage) == FormatNumber(maxCoverage);
        }

        double estimatedOutputPerMinute = 0.0;
        if (isPossible && outputAmount > 0 && craftingTime > 0.0)
        {
            estimatedOutputPerMinute = RoundToTwoDecimals(craneItemsPerMinute / craftingTime * outputAmount);
        }

        data = new EstimateData
        {
            Requirements = requirements,
            AssignedCranes = assignedCranes,
            CraneIconCounts = craneIconCounts,
            CraneCount = craneCount,
            OutputAmount = outputAmount,
            CraneItemsPerMinute = craneItemsPerMinute,
            CraftingTime = craftingTime,
            MinCoverage = minCoverage,
            EstimatedOutputPerMinute = estimatedOutputPerMinute,
            IsCoverageMaxed = isCoverageMaxed,
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
                "Need at least <color=#2AB1FF>{0}</color> dedicated input belts, only <color=#EAEAEA>{1}</color> cranes available. Estimate = 0",
                data.Requirements.Length,
                data.CraneCount);
        }

        string iconTerms = string.Join(",", data.CraneIconCounts.Select(c => $"<color=#2AB1FF>{c}</color>"));
        string distributionText = BuildDistributionText(data);

        double itemsPerShownCrane;
        if (TryGetItemsPerShownCrane(data, out itemsPerShownCrane))
        {
            return string.Format(
                "<color=#EAEAEA>{0}</color> / ({1}) = {2}\n<color=#FFD926>{3}</color> * <color=#2AB1FF>{4}x</color> / <color=#EAEAEA>{5}</color> * <color=#55C472>{6}</color> = {7}",
                data.CraneCount,
                iconTerms,
                distributionText,
                FormatNumber(data.CraneItemsPerMinute),
                FormatNumber(data.MinCoverage),
                FormatNumber(itemsPerShownCrane),
                data.OutputAmount,
                FormatNumber(data.EstimatedOutputPerMinute));
        }

        string craftingTimeTerms = BuildCraftingTimeExpression(data);

        return string.Format(
            "<color=#EAEAEA>{0}</color> / ({1}) = {2}\n<color=#FFD926>{3}</color> / max({4}) * <color=#55C472>{5}</color> = {6}",
            data.CraneCount,
            iconTerms,
            distributionText,
            FormatNumber(data.CraneItemsPerMinute),
            craftingTimeTerms,
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
