using System.Collections.Generic;
using System.Linq;
using AYellowpaper.SerializedCollections;
using Data.FactoryFloor.Resources;
using HarmonyLib;
using Logic.Factory;
using ScriptEngine;
using UnityEngine;

/// <summary>
/// Flat integer multipliers for research costs, challenge currency rewards,
/// bot delivery tier requirements, and shape/module challenge tier requirements.
///
/// Strategy: mutate ScriptableObject assets in memory on script enable and on
/// every save-load, revert on disable. No save-file touching — all mutated
/// state lives purely on SO assets which Unity re-hydrates from bundles each
/// launch. XP rewards are deliberately NOT scaled since ObjectiveTargetItem.XpReward
/// is a precomputed running sum, not a per-tier delta.
///
/// Edge case: if a node is bought under factor N and the mod is later disabled,
/// TechTreeSaveDataNode.PaidCosts keeps the N× amount. This only matters on
/// RefunableReUnlock, which refunds the historical paid value — player keeps
/// whatever they paid. Acceptable.
/// </summary>
[ScriptEntry]
public sealed class SimpleResearchAndChallengeMultipliers : ScriptMod
{
    public const int ResearchCostMultiplier = 10;
    public const int ChallengeRewardMultiplier = 10;
    public const int BotRequirementMultiplier = 10;
    public const int ShapeChallengeRequirementMultiplier = 10;

    private sealed class CategoryMarker { public int Requirement = 1; public int Currency = 1; }
    private sealed class NodeMarker { public int Research = 1; }

    // Keyed by GetInstanceID(). Static so state survives individual OnEnable
    // cycles within the same assembly; cleared on RevertAll and naturally
    // discarded when the script assembly unloads.
    private static readonly Dictionary<int, CategoryMarker> _categoryMarkers = new Dictionary<int, CategoryMarker>();
    private static readonly Dictionary<int, NodeMarker> _nodeMarkers = new Dictionary<int, NodeMarker>();

    private static readonly AccessTools.FieldRef<ResourceCost, SerializedDictionary<ResourceDataSO, int>> ResourceCostField =
        AccessTools.FieldRefAccess<ResourceCost, SerializedDictionary<ResourceDataSO, int>>("_cost");

    private FactoryLoader _factoryLoader;
    private bool _lastHadSave;

    protected override void OnEnable()
    {
        Log($"research x{ResearchCostMultiplier}, reward x{ChallengeRewardMultiplier}, bot req x{BotRequirementMultiplier}, shape req x{ShapeChallengeRequirementMultiplier}.");
        _factoryLoader = null;
        _lastHadSave = false;
        ApplyAll("enable");
    }

    protected override void OnDisable()
    {
        RevertAll();
    }

    protected override void OnUpdate()
    {
        if (_factoryLoader == null)
        {
            _factoryLoader = Resources.FindObjectsOfTypeAll<FactoryLoader>().FirstOrDefault();
            if (_factoryLoader == null) return;
        }

        bool hasSave = _factoryLoader.HasFinishedLoadingSave;
        if (hasSave && !_lastHadSave)
        {
            ApplyAll("save-loaded");
        }
        _lastHadSave = hasSave;
    }

    private void ApplyAll(string trigger)
    {
        int cats = 0, nodes = 0;

        foreach (var cat in Resources.FindObjectsOfTypeAll<ObjectiveTargetCategorySO>())
        {
            if (cat == null || cat.Items == null || cat.Resource == null) continue;
            int reqTarget = cat.Resource.HasResourceData
                ? BotRequirementMultiplier
                : ShapeChallengeRequirementMultiplier;
            if (RetargetCategory(cat, reqTarget, ChallengeRewardMultiplier)) cats++;
        }

        foreach (var node in Resources.FindObjectsOfTypeAll<TechTreeNodeSO>())
        {
            if (node == null || node.Cost == null) continue;
            if (RetargetNode(node, ResearchCostMultiplier)) nodes++;
        }

        if (cats > 0 || nodes > 0)
        {
            Log($"[{trigger}] scaled {cats} categories, {nodes} tech nodes.");
        }
    }

    private void RevertAll()
    {
        int cats = 0, nodes = 0;

        foreach (var cat in Resources.FindObjectsOfTypeAll<ObjectiveTargetCategorySO>())
        {
            if (cat == null || cat.Items == null) continue;
            if (RetargetCategory(cat, 1, 1)) cats++;
        }

        foreach (var node in Resources.FindObjectsOfTypeAll<TechTreeNodeSO>())
        {
            if (node == null || node.Cost == null) continue;
            if (RetargetNode(node, 1)) nodes++;
        }

        _categoryMarkers.Clear();
        _nodeMarkers.Clear();

        Log($"disabled; reverted {cats} categories, {nodes} tech nodes.");
    }

    private static bool RetargetCategory(ObjectiveTargetCategorySO cat, int reqTarget, int currencyTarget)
    {
        int id = cat.GetInstanceID();
        if (!_categoryMarkers.TryGetValue(id, out var m))
        {
            m = new CategoryMarker();
            _categoryMarkers[id] = m;
        }

        bool changed = false;

        if (m.Requirement != reqTarget)
        {
            foreach (var it in cat.Items)
            {
                if (it == null) continue;
                it.Amount = Rescale(it.Amount, m.Requirement, reqTarget);
                it.AmountStartOffset = Rescale(it.AmountStartOffset, m.Requirement, reqTarget);
            }
            m.Requirement = reqTarget;
            changed = true;
        }

        if (m.Currency != currencyTarget)
        {
            foreach (var it in cat.Items)
            {
                if (it == null) continue;
                it.CurrencyReward = Rescale(it.CurrencyReward, m.Currency, currencyTarget);
            }
            m.Currency = currencyTarget;
            changed = true;
        }

        return changed;
    }

    private static bool RetargetNode(TechTreeNodeSO node, int researchTarget)
    {
        int id = node.GetInstanceID();
        if (!_nodeMarkers.TryGetValue(id, out var m))
        {
            m = new NodeMarker();
            _nodeMarkers[id] = m;
        }

        if (m.Research == researchTarget) return false;

        var costs = ResourceCostField(node.Cost);
        if (costs == null) return false;

        var keys = costs.Keys.ToList();
        foreach (var k in keys)
        {
            costs[k] = Rescale(costs[k], m.Research, researchTarget);
        }
        m.Research = researchTarget;
        return true;
    }

    private static uint Rescale(uint value, int from, int to)
    {
        if (value == 0u || from == to) return value;
        return (uint)((ulong)value * (uint)to / (uint)from);
    }

    private static int Rescale(int value, int from, int to)
    {
        if (value == 0 || from == to) return value;
        return (int)((long)value * to / from);
    }
}
