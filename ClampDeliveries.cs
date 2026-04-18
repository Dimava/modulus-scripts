using System;
using System.Collections.Generic;
using Data.FactoryFloor.Behaviours;
using Data.FactoryFloor.Resources;
using Data.Shapes;
using Data.Statistics;
using HarmonyLib;
using ScriptEngine;
using UnityEngine;

[ScriptEntry]
public sealed class ClampDeliveries : ScriptMod
{
    private static ClampDeliveries? _instance;
    private static readonly Dictionary<RotationIndependentHash, uint> ChallengeCaps = new Dictionary<RotationIndependentHash, uint>();
    private static readonly Dictionary<int, uint> DeliveryCaps = new Dictionary<int, uint>();

    protected override void OnEnable()
    {
        _instance = this;
        RebuildChallengeCaps();
    }

    protected override void OnDisable()
    {
        ChallengeCaps.Clear();
        DeliveryCaps.Clear();
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }
    }

    public static void RebuildChallengeCaps()
    {
        ChallengeCaps.Clear();
        DeliveryCaps.Clear();

        foreach (ModuleChallengeSO challengeSO in Resources.FindObjectsOfTypeAll<ModuleChallengeSO>())
        {
            if (challengeSO == null || challengeSO.Sets == null)
            {
                continue;
            }

            foreach (ModuleChallengeSet set in challengeSO.Sets)
            {
                if (set == null || set.Categories == null)
                {
                    continue;
                }

                foreach (ObjectiveTargetCategorySO category in set.Categories)
                {
                    if (category == null || !category.Resource.HasShapeData || category.Items == null || category.Items.Count == 0)
                    {
                        continue;
                    }

                    RotationIndependentHash hash = category.Resource.GetRotationIndependentHash();
                    uint cap = category.Items[category.Items.Count - 1].RequiredAmount;

                    if (!ChallengeCaps.TryGetValue(hash, out uint existingCap) || cap > existingCap)
                    {
                        ChallengeCaps[hash] = cap;
                    }
                }
            }
        }

        foreach (DeliveryTargetSO deliveryTargetSO in Resources.FindObjectsOfTypeAll<DeliveryTargetSO>())
        {
            if (deliveryTargetSO == null || deliveryTargetSO.Categories == null)
            {
                continue;
            }

            foreach (ObjectiveTargetCategorySO category in deliveryTargetSO.Categories)
            {
                if (category == null || !category.Resource.HasResourceData || category.Items == null || category.Items.Count == 0)
                {
                    continue;
                }

                int resourceId = category.Resource.GetResourceID();
                uint cap = category.Items[category.Items.Count - 1].RequiredAmount;

                if (!DeliveryCaps.TryGetValue(resourceId, out uint existingCap) || cap > existingCap)
                {
                    DeliveryCaps[resourceId] = cap;
                }
            }
        }
    }

    private static bool HashesMatch(RotationIndependentHash a, RotationIndependentHash b)
    {
        if (a == b)
        {
            return true;
        }

        if (a.Rotations == null || b.Rotations == null)
        {
            return false;
        }

        for (int i = 0; i < b.Rotations.Length; i++)
        {
            if (a.Contains(b.Rotations[i]))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryGetChallengeCap(RotationIndependentHash deliveredHash, out RotationIndependentHash challengeHash, out uint cap)
    {
        if (ChallengeCaps.Count == 0)
        {
            RebuildChallengeCaps();
        }

        foreach (KeyValuePair<RotationIndependentHash, uint> entry in ChallengeCaps)
        {
            if (HashesMatch(entry.Key, deliveredHash))
            {
                challengeHash = entry.Key;
                cap = entry.Value;
                return true;
            }
        }

        challengeHash = default(RotationIndependentHash);
        cap = 0;
        return false;
    }

    public static uint ClampToCap(uint value, uint cap)
    {
        return value > cap ? cap : value;
    }

    public static bool TryGetDeliveryCap(int resourceId, out uint cap)
    {
        if (ChallengeCaps.Count == 0 && DeliveryCaps.Count == 0)
        {
            RebuildChallengeCaps();
        }

        return DeliveryCaps.TryGetValue(resourceId, out cap);
    }

    public static void NormalizeDeliveredShapeStats(StatisticsSO statisticsSO)
    {
        if (statisticsSO == null)
        {
            return;
        }

        if (ChallengeCaps.Count == 0)
        {
            RebuildChallengeCaps();
        }

        List<RotationIndependentHash> keysToRemove = new List<RotationIndependentHash>();
        List<KeyValuePair<RotationIndependentHash, uint>> deliveredStats = new List<KeyValuePair<RotationIndependentHash, uint>>(statisticsSO.DeliveredShapesStats);

        for (int i = 0; i < deliveredStats.Count; i++)
        {
            KeyValuePair<RotationIndependentHash, uint> entry = deliveredStats[i];
            RotationIndependentHash challengeHash;
            uint cap;
            if (!TryGetChallengeCap(entry.Key, out challengeHash, out cap))
            {
                continue;
            }

            uint clamped = ClampToCap(entry.Value, cap);
            if (statisticsSO.DeliveredShapesStats.TryGetValue(challengeHash, out uint existingValue))
            {
                if (clamped > existingValue)
                {
                    statisticsSO.DeliveredShapesStats[challengeHash] = clamped;
                }
            }
            else
            {
                statisticsSO.DeliveredShapesStats[challengeHash] = clamped;
            }

            if (entry.Key != challengeHash)
            {
                keysToRemove.Add(entry.Key);
            }
        }

        for (int i = 0; i < keysToRemove.Count; i++)
        {
            statisticsSO.DeliveredShapesStats.Remove(keysToRemove[i]);
        }
    }

    public static void NormalizeDeliveredStats(StatisticsSO statisticsSO)
    {
        if (statisticsSO == null)
        {
            return;
        }

        if (ChallengeCaps.Count == 0 && DeliveryCaps.Count == 0)
        {
            RebuildChallengeCaps();
        }

        List<int> resourceIds = new List<int>(statisticsSO.DeliveredStats.Keys);
        for (int i = 0; i < resourceIds.Count; i++)
        {
            int resourceId = resourceIds[i];
            if (!DeliveryCaps.TryGetValue(resourceId, out uint cap))
            {
                continue;
            }

            statisticsSO.DeliveredStats[resourceId] = ClampToCap(statisticsSO.DeliveredStats[resourceId], cap);
        }

        NormalizeDeliveredShapeStats(statisticsSO);
    }
}

[HarmonyPatch(typeof(StatisticsSO), nameof(StatisticsSO.ApplySaveData))]
static class StatisticsSO_ApplySaveData_ModuleChallengeClampPatch
{
    static void Postfix(StatisticsSO __instance)
    {
        ClampDeliveries.NormalizeDeliveredStats(__instance);
    }
}

[HarmonyPatch(typeof(StatisticsSO), nameof(StatisticsSO.AddDeliveredStatistic))]
static class StatisticsSO_AddDeliveredStatistic_DeliveryClampPatch
{
    static bool Prefix(StatisticsSO __instance, int resourceId, uint addAmount)
    {
        uint cap;
        if (!ClampDeliveries.TryGetDeliveryCap(resourceId, out cap))
        {
            return true;
        }

        uint currentValue = __instance.GetDeliveredStatistic(resourceId);
        ulong nextValue = (ulong)currentValue + addAmount;
        uint clampedValue = (uint)Math.Min((ulong)cap, nextValue);
        __instance.DeliveredStats[resourceId] = clampedValue;
        return false;
    }
}

[HarmonyPatch(typeof(StatisticsSO), nameof(StatisticsSO.AddDeliveredShapeStatistic))]
static class StatisticsSO_AddDeliveredShapeStatistic_ModuleChallengeClampPatch
{
    static bool Prefix(StatisticsSO __instance, RotationIndependentHash shapeHash, uint addAmount)
    {
        RotationIndependentHash challengeHash;
        uint cap;
        if (!ClampDeliveries.TryGetChallengeCap(shapeHash, out challengeHash, out cap))
        {
            return true;
        }

        uint currentValue = __instance.GetDeliveredShapesStatistic(challengeHash);
        ulong nextValue = (ulong)currentValue + addAmount;
        uint clampedValue = (uint)Math.Min((ulong)cap, nextValue);
        __instance.DeliveredShapesStats[challengeHash] = clampedValue;
        return false;
    }
}

[HarmonyPatch(typeof(StatisticsSO), nameof(StatisticsSO.GetDeliveredStatistic))]
static class StatisticsSO_GetDeliveredStatistic_DeliveryClampPatch
{
    static void Postfix(int resourceId, ref uint __result)
    {
        uint cap;
        if (!ClampDeliveries.TryGetDeliveryCap(resourceId, out cap))
        {
            return;
        }

        __result = ClampDeliveries.ClampToCap(__result, cap);
    }
}

[HarmonyPatch(typeof(StatisticsSO), nameof(StatisticsSO.GetDeliveredShapesStatistic))]
static class StatisticsSO_GetDeliveredShapesStatistic_ModuleChallengeClampPatch
{
    static void Postfix(StatisticsSO __instance, RotationIndependentHash shapeHash, ref uint __result)
    {
        RotationIndependentHash challengeHash;
        uint cap;
        if (!ClampDeliveries.TryGetChallengeCap(shapeHash, out challengeHash, out cap))
        {
            return;
        }

        uint value = __result;
        if (__instance.DeliveredShapesStats.TryGetValue(challengeHash, out uint canonicalValue) && canonicalValue > value)
        {
            value = canonicalValue;
        }

        __result = ClampDeliveries.ClampToCap(value, cap);
    }
}

[HarmonyPatch(typeof(ExoportBehaviour), nameof(ExoportBehaviour.CanReceiveResource))]
static class ExoportBehaviour_CanReceiveResource_ModuleChallengeClampPatch
{
    static void Postfix(ExoportBehaviour __instance, Resource resource, ref bool __result)
    {
        if (!__result)
        {
            return;
        }

        StatisticsSO statisticsSO = Traverse.Create(__instance).Field("_statisticsSO").GetValue<StatisticsSO>();
        if (statisticsSO == null)
        {
            return;
        }

        if (resource is ShapeResource shapeResource)
        {
            RotationIndependentHash challengeHash;
            uint challengeCap;
            if (!ClampDeliveries.TryGetChallengeCap(shapeResource.ShapeData.RotationIndependantHash, out challengeHash, out challengeCap))
            {
                return;
            }

            if (statisticsSO.GetDeliveredShapesStatistic(challengeHash) >= challengeCap)
            {
                __result = false;
            }
            return;
        }

        uint deliveryCap;
        if (ClampDeliveries.TryGetDeliveryCap(resource.Data.ID, out deliveryCap)
            && statisticsSO.GetDeliveredStatistic(resource.Data.ID) >= deliveryCap)
        {
            __result = false;
        }
    }
}
