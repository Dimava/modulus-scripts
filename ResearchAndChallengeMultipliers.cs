using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using AYellowpaper.SerializedCollections;
using Data.FactoryFloor.Resources;
using Data.Objectives;
using Data.SaveData.PersistentSOs;
using Data.Statistics;
using Data.TechTree.Validators;
using Events;
using Events.FactoryFloor;
using Events.UI.Overlays;
using HarmonyLib;
using Presentation.Locators;
using Presentation.UI.HUD;
using Presentation.UI.Objectives;
using Presentation.UI.Overlays.Notifications;
using ScriptEngine;
using TMPro;
using UnityEngine;

/// <summary>
/// Scales tech tree shard costs and module challenge rewards without mutating
/// the game's authored data assets.
/// </summary>
[ScriptEntry]
public sealed class ResearchAndChallengeMultipliers : ScriptMod
{
    internal const float ResearchCostMultiplier = 1f;
    internal const float ChallengeRewardMultiplier = 1f;

    protected override void OnEnable()
    {
        Log($"Research cost x{ResearchCostMultiplier:0.###}, challenge rewards x{ChallengeRewardMultiplier:0.###}.");
    }

    internal static bool HasResearchScaling => ResearchCostMultiplier != 1f;
    internal static bool HasChallengeScaling => ChallengeRewardMultiplier != 1f;

    internal static bool IsModuleChallengeCategory(ObjectiveTargetCategorySO category)
    {
        return category != null && category.Resource != null && category.Resource.HasShapeData;
    }

    internal static ResourceCost BuildScaledResearchCost(ResourceCost originalCost)
    {
        if (originalCost == null)
        {
            return new ResourceCost();
        }

        if (!HasResearchScaling)
        {
            return originalCost;
        }

        SerializedDictionary<ResourceDataSO, int> scaledCosts = new SerializedDictionary<ResourceDataSO, int>();
        foreach (KeyValuePair<ResourceDataSO, int> entry in originalCost.GetAllCosts())
        {
            scaledCosts[entry.Key] = ScalePositive(entry.Value, ResearchCostMultiplier);
        }

        return new ResourceCost(scaledCosts);
    }

    internal static void ApplyScaledTechCostUI(TechTreeNodeView view)
    {
        if (!HasResearchScaling)
        {
            return;
        }

        TechTreeNodeSO node = PrivateFields.NodeViewNode(view);
        if (node == null || node.IsUnlocked)
        {
            return;
        }

        CurrencyPersistentSO currency = PrivateFields.NodeViewCurrency(view);
        SerializedDictionary<ResourceDataSO, DataShardCostUI> costUIs = PrivateFields.NodeViewCostUIs(view);
        GameObject costIcons = PrivateFields.NodeViewCostIcons(view);
        Color notAffordableColor = PrivateFields.NodeViewNotAffordableColor(view);

        bool hasVisibleCost = false;
        foreach (KeyValuePair<ResourceDataSO, int> entry in node.Cost.GetAllCosts())
        {
            DataShardCostUI costUI;
            int scaledCost = ScalePositive(entry.Value, ResearchCostMultiplier);
            if (entry.Key == null || costUIs == null || !costUIs.TryGetValue(entry.Key, out costUI) || costUI == null)
            {
                continue;
            }

            if (scaledCost > 0)
            {
                hasVisibleCost = true;
                costUI.SetAmount(scaledCost);
                costUI.gameObject.SetActive(true);

                if (currency != null && currency.GetResourceCount(entry.Key) >= scaledCost)
                {
                    costUI.ResetColor();
                }
                else
                {
                    costUI.SetColor(notAffordableColor);
                }
            }
            else
            {
                costUI.gameObject.SetActive(false);
            }
        }

        if (costIcons != null)
        {
            costIcons.SetActive(hasVisibleCost);
        }
    }

    internal static bool TryUpdateChallengeRewardLabelTexts(ChallengeRewardLabels labels, ObjectiveTargetItem item, int tier)
    {
        if (!HasChallengeScaling)
        {
            return false;
        }

        IList rewardLabels = PrivateFields.RewardLabelsField.GetValue(labels) as IList;
        if (rewardLabels == null || tier < 0 || tier >= rewardLabels.Count)
        {
            return false;
        }

        object rewardLabel = rewardLabels[tier];
        TextMeshProUGUI xpText = rewardLabel == null ? null : PrivateFields.RewardLabelXpTextField.GetValue(rewardLabel) as TextMeshProUGUI;
        TextMeshProUGUI currencyText = rewardLabel == null ? null : PrivateFields.RewardLabelCurrencyTextField.GetValue(rewardLabel) as TextMeshProUGUI;
        if (xpText == null || currencyText == null)
        {
            return false;
        }

        xpText.SetText(string.Format(LocalizationUtility.GetLocalizedText("Objectives.xpLabel"), ScalePositive(item.XpReward, ChallengeRewardMultiplier).ToString()));
        currencyText.SetText(ScalePositive(item.CurrencyReward, ChallengeRewardMultiplier).ToString());
        return true;
    }

    internal static bool TryAwardScaledModuleChallenge(ObjectiveManager manager, ObjectiveTargetCategorySO category, int currentTier, XPEarnedSource xpEarnedSource)
    {
        if (!HasChallengeScaling || !IsModuleChallengeCategory(category) || category.Items == null || currentTier < 0 || currentTier >= category.Items.Count)
        {
            return false;
        }

        ObjectiveTargetItem item = category.Items[currentTier];
        uint scaledXp = ScalePositive(item.XpReward, ChallengeRewardMultiplier);
        uint scaledCurrency = ScalePositive(item.CurrencyReward, ChallengeRewardMultiplier);

        AddXPEvent addXPEvent = PrivateFields.ObjectiveManagerAddXP(manager);
        AddCurrencyEvent addCurrencyEvent = PrivateFields.ObjectiveManagerAddCurrency(manager);
        ShowIngameNotificationEvent showNotificationEvent = PrivateFields.ObjectiveManagerShowNotification(manager);
        ChallengesUILibrary challengesUILibrary = PrivateFields.ObjectiveManagerChallengesUi(manager);
        CurrencyUILibrary currencyUILibrary = PrivateFields.ObjectiveManagerCurrencyUi(manager);
        AudioManagerLocator audioManagerLocator = PrivateFields.ObjectiveManagerAudio(manager);
        BaseEvent moduleChallengeCompleted = PrivateFields.ObjectiveManagerModuleChallengeCompleted(manager);
        ModuleChallengeSO moduleChallengeSO = PrivateFields.ObjectiveManagerModuleChallenge(manager);

        addXPEvent?.Fire(ToInt(scaledXp), xpEarnedSource);
        if (item.CurrenyRewardResourceData != null)
        {
            addCurrencyEvent?.Fire(new AddCurrencyEventDto(item.CurrenyRewardResourceData, ToInt(scaledCurrency)));
        }

        Sprite currencySprite = null;
        CurrencyUILibrary.CurrencyUI currencyUI;
        if (item.CurrenyRewardResourceData != null
            && currencyUILibrary != null
            && currencyUILibrary.CurrencyUIs.TryGetValue(item.CurrenyRewardResourceData, out currencyUI))
        {
            currencySprite = currencyUI.Sprite;
        }

        InGameNotificationDto notification = new InGameNotificationDto(
            deliveriesNotificationDto: new InGameObjectivesNotificationDto(Color.white, currentTier, scaledXp, currencySprite, scaledCurrency),
            labelText: LocalizationUtility.GetLocalizedText(category.ModuleNameLocaKey) + " - " + LocalizationUtility.GetLocalizedText(challengesUILibrary.TierLocaKeys[currentTier]),
            sprite: category.Resource.Icon,
            type: InGameNotificationType.Challenge);
        showNotificationEvent?.Fire(notification);
        moduleChallengeCompleted?.Fire();

        if (moduleChallengeSO != null && moduleChallengeSO.CheckChallengeSetCompleted(item, out ModuleChallengeSet claimedItemSet))
        {
            audioManagerLocator?.AudioManager.PlayModuleChallengeComplete();
            StartRewardCosmetic(manager, claimedItemSet);
        }
        else
        {
            audioManagerLocator?.AudioManager.PlayNotificationReward();
        }

        return true;
    }

    private static void StartRewardCosmetic(ObjectiveManager manager, ModuleChallengeSet claimedItemSet)
    {
        if (PrivateFields.RewardCosmeticMethod == null)
        {
            return;
        }

        IEnumerator rewardCosmeticRoutine = PrivateFields.RewardCosmeticMethod.Invoke(manager, new object[] { claimedItemSet }) as IEnumerator;
        if (rewardCosmeticRoutine != null)
        {
            manager.StartCoroutine(rewardCosmeticRoutine);
        }
    }

    private static int ToInt(uint value)
    {
        return value > int.MaxValue ? int.MaxValue : (int)value;
    }

    private static int ScalePositive(int amount, float multiplier)
    {
        if (amount <= 0 || multiplier <= 0f)
        {
            return 0;
        }

        int scaled = (int)Math.Round(amount * (double)multiplier, MidpointRounding.AwayFromZero);
        return scaled == 0 ? 1 : Mathf.Max(0, scaled);
    }

    private static uint ScalePositive(uint amount, float multiplier)
    {
        if (amount == 0u || multiplier <= 0f)
        {
            return 0u;
        }

        ulong scaled = (ulong)Math.Round(amount * (double)multiplier, MidpointRounding.AwayFromZero);
        if (scaled == 0ul)
        {
            return 1u;
        }

        return scaled > uint.MaxValue ? uint.MaxValue : (uint)scaled;
    }

    internal static class PrivateFields
    {
        internal static readonly AccessTools.FieldRef<DataShardsValidator, CurrencyPersistentSO> ValidatorCurrency =
            AccessTools.FieldRefAccess<DataShardsValidator, CurrencyPersistentSO>("_currencySO");

        internal static readonly AccessTools.FieldRef<TechTreeNodeView, TechTreeNodeSO> NodeViewNode =
            AccessTools.FieldRefAccess<TechTreeNodeView, TechTreeNodeSO>("_techTreeNodeSO");

        internal static readonly AccessTools.FieldRef<TechTreeNodeView, CurrencyPersistentSO> NodeViewCurrency =
            AccessTools.FieldRefAccess<TechTreeNodeView, CurrencyPersistentSO>("_currentCurrency");

        internal static readonly AccessTools.FieldRef<TechTreeNodeView, SerializedDictionary<ResourceDataSO, DataShardCostUI>> NodeViewCostUIs =
            AccessTools.FieldRefAccess<TechTreeNodeView, SerializedDictionary<ResourceDataSO, DataShardCostUI>>("_costUIs");

        internal static readonly AccessTools.FieldRef<TechTreeNodeView, GameObject> NodeViewCostIcons =
            AccessTools.FieldRefAccess<TechTreeNodeView, GameObject>("_costIcons");

        internal static readonly AccessTools.FieldRef<TechTreeNodeView, Color> NodeViewNotAffordableColor =
            AccessTools.FieldRefAccess<TechTreeNodeView, Color>("_notAffordableColor");

        internal static readonly AccessTools.FieldRef<ObjectiveManager, AddXPEvent> ObjectiveManagerAddXP =
            AccessTools.FieldRefAccess<ObjectiveManager, AddXPEvent>("_addXPEvent");

        internal static readonly AccessTools.FieldRef<ObjectiveManager, AddCurrencyEvent> ObjectiveManagerAddCurrency =
            AccessTools.FieldRefAccess<ObjectiveManager, AddCurrencyEvent>("_addCurrencyEvent");

        internal static readonly AccessTools.FieldRef<ObjectiveManager, ShowIngameNotificationEvent> ObjectiveManagerShowNotification =
            AccessTools.FieldRefAccess<ObjectiveManager, ShowIngameNotificationEvent>("_showIngameNotificationEvent");

        internal static readonly AccessTools.FieldRef<ObjectiveManager, ChallengesUILibrary> ObjectiveManagerChallengesUi =
            AccessTools.FieldRefAccess<ObjectiveManager, ChallengesUILibrary>("_challengesUILibrary");

        internal static readonly AccessTools.FieldRef<ObjectiveManager, CurrencyUILibrary> ObjectiveManagerCurrencyUi =
            AccessTools.FieldRefAccess<ObjectiveManager, CurrencyUILibrary>("_currencyUILibrary");

        internal static readonly AccessTools.FieldRef<ObjectiveManager, AudioManagerLocator> ObjectiveManagerAudio =
            AccessTools.FieldRefAccess<ObjectiveManager, AudioManagerLocator>("_audioManagerLocator");

        internal static readonly AccessTools.FieldRef<ObjectiveManager, BaseEvent> ObjectiveManagerModuleChallengeCompleted =
            AccessTools.FieldRefAccess<ObjectiveManager, BaseEvent>("_moduleChallengeCompleted");

        internal static readonly AccessTools.FieldRef<ObjectiveManager, ModuleChallengeSO> ObjectiveManagerModuleChallenge =
            AccessTools.FieldRefAccess<ObjectiveManager, ModuleChallengeSO>("_moduleChallengeSO");

        internal static readonly FieldInfo RewardLabelsField =
            AccessTools.Field(typeof(ChallengeRewardLabels), "_rewardLabels");

        internal static readonly Type RewardLabelType =
            AccessTools.Inner(typeof(ChallengeRewardLabels), "RewardLabel");

        internal static readonly FieldInfo RewardLabelXpTextField =
            AccessTools.Field(RewardLabelType, "XpText");

        internal static readonly FieldInfo RewardLabelCurrencyTextField =
            AccessTools.Field(RewardLabelType, "CurrencyText");

        internal static readonly MethodInfo RewardCosmeticMethod =
            AccessTools.Method(typeof(ObjectiveManager), "RewardCosmetic");
    }
}

[HarmonyPatch(typeof(DataShardsValidator), nameof(DataShardsValidator.CanBuy))]
static class ResearchAndChallengeMultipliers_DataShardsValidator_CanBuy_Patch
{
    static bool Prefix(DataShardsValidator __instance, TechTreeNodeSO node, ref bool __result)
    {
        if (!ResearchAndChallengeMultipliers.HasResearchScaling)
        {
            return true;
        }

        CurrencyPersistentSO currency = ResearchAndChallengeMultipliers.PrivateFields.ValidatorCurrency(__instance);
        if (currency == null || node == null)
        {
            return true;
        }

        __result = currency.HasEnoughResources(ResearchAndChallengeMultipliers.BuildScaledResearchCost(node.Cost));
        return false;
    }
}

[HarmonyPatch(typeof(DataShardsValidator), nameof(DataShardsValidator.Buy))]
static class ResearchAndChallengeMultipliers_DataShardsValidator_Buy_Patch
{
    static bool Prefix(DataShardsValidator __instance, TechTreeNodeSO node)
    {
        if (!ResearchAndChallengeMultipliers.HasResearchScaling)
        {
            return true;
        }

        CurrencyPersistentSO currency = ResearchAndChallengeMultipliers.PrivateFields.ValidatorCurrency(__instance);
        if (currency == null || node == null)
        {
            return true;
        }

        currency.TryBuy(ResearchAndChallengeMultipliers.BuildScaledResearchCost(node.Cost));
        return false;
    }
}

[HarmonyPatch(typeof(TechTreeNodeView), "SetCost")]
static class ResearchAndChallengeMultipliers_TechTreeNodeView_SetCost_Patch
{
    static void Postfix(TechTreeNodeView __instance)
    {
        ResearchAndChallengeMultipliers.ApplyScaledTechCostUI(__instance);
    }
}

[HarmonyPatch(typeof(TechTreeSaveDataNode), MethodType.Constructor, typeof(int), typeof(int), typeof(ResourceCost))]
static class ResearchAndChallengeMultipliers_TechTreeSaveDataNode_Ctor_Patch
{
    static void Prefix(ref ResourceCost resourceCost)
    {
        if (!ResearchAndChallengeMultipliers.HasResearchScaling)
        {
            return;
        }

        resourceCost = ResearchAndChallengeMultipliers.BuildScaledResearchCost(resourceCost);
    }
}

[HarmonyPatch(typeof(ObjectiveManager), "AwardObjectiveRewards")]
static class ResearchAndChallengeMultipliers_ObjectiveManager_AwardObjectiveRewards_Patch
{
    static bool Prefix(ObjectiveManager __instance, ObjectiveTargetCategorySO objectiveTargetCategory, XPEarnedSource xpEarnedSource, int currentTier)
    {
        return !ResearchAndChallengeMultipliers.TryAwardScaledModuleChallenge(__instance, objectiveTargetCategory, currentTier, xpEarnedSource);
    }
}

[HarmonyPatch(typeof(ChallengeRewardLabels), nameof(ChallengeRewardLabels.Build))]
static class ResearchAndChallengeMultipliers_ChallengeRewardLabels_Build_Patch
{
    static void Postfix(ChallengeRewardLabels __instance, ObjectiveTargetItem item, int tier)
    {
        ResearchAndChallengeMultipliers.TryUpdateChallengeRewardLabelTexts(__instance, item, tier);
    }
}
