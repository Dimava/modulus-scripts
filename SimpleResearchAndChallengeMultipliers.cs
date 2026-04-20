using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
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
using Presentation.UI;
using Presentation.UI.Objectives;
using Presentation.UI.Overlays.Notifications;
using ScriptEngine;
using TMPro;
using UnityEngine;

/// <summary>
/// Scales <see cref="ResourceCost.GetAllCosts"/> only while tech-tree shard cost is in play
/// (validator, node cost UI, unlock save snapshot). Other <see cref="ResourceCost"/> uses are unchanged.
/// Module challenge <em>currency</em> rewards use <see cref="ChallengeRewardMultiplier"/>; XP stays at authored values.
/// <see cref="BotRequirementMultiplier"/> scales delivery-bot tier thresholds; <see cref="ShapeChallengeRequirementMultiplier"/> scales module-shape tier thresholds (authored amounts unchanged).
/// </summary>
[ScriptEntry]
public sealed class SimpleResearchAndChallengeMultipliers : ScriptMod
{
    public const int ResearchCostMultiplier = 10;
    public const int ChallengeRewardMultiplier = 10;
    public const int BotRequirementMultiplier = 10;
    public const int ShapeChallengeRequirementMultiplier = 10;

    protected override void OnEnable()
    {
        Log($"Research GetAllCosts x{ResearchCostMultiplier}; challenge currency x{ChallengeRewardMultiplier} (XP vanilla); bot reqs x{BotRequirementMultiplier}; shape reqs x{ShapeChallengeRequirementMultiplier}.");
    }

    internal static bool HasChallengeCurrencyScaling => ChallengeRewardMultiplier != 1;

    internal static bool IsModuleChallengeCategory(ObjectiveTargetCategorySO category)
    {
        return category != null && category.Resource != null && category.Resource.HasShapeData;
    }
}

/// <summary>
/// Scales tier thresholds and progress display for bots (<see cref="SimpleResearchAndChallengeMultipliers.BotRequirementMultiplier"/>)
/// and module shapes (<see cref="SimpleResearchAndChallengeMultipliers.ShapeChallengeRequirementMultiplier"/>) without changing SO data.
/// </summary>
internal static class ObjectiveRequirementScaling
{
    internal static int GetRequirementMultiplier(ObjectiveTargetCategorySO category)
    {
        if (category?.Resource == null)
        {
            return 1;
        }

        if (category.Resource.HasResourceData)
        {
            return SimpleResearchAndChallengeMultipliers.BotRequirementMultiplier;
        }

        if (category.Resource.HasShapeData)
        {
            return SimpleResearchAndChallengeMultipliers.ShapeChallengeRequirementMultiplier;
        }

        return 1;
    }

    internal static uint ScaleRequirement(uint amount, int multiplier)
    {
        if (multiplier <= 1)
        {
            return amount;
        }

        if (amount == 0u)
        {
            return 0u;
        }

        ulong product = (ulong)amount * (ulong)multiplier;
        return product > uint.MaxValue ? uint.MaxValue : (uint)product;
    }
}

/// <summary>
/// Awards module challenge rewards with scaled currency only; mirrors vanilla <see cref="ObjectiveManager"/> flow.
/// </summary>
internal static class SimpleModuleChallengeCurrencyAward
{
    internal static bool TryAwardObjectiveRewards(
        ObjectiveManager manager,
        ObjectiveTargetCategorySO category,
        XPEarnedSource xpEarnedSource,
        int currentTier)
    {
        if (!SimpleResearchAndChallengeMultipliers.HasChallengeCurrencyScaling
            || !SimpleResearchAndChallengeMultipliers.IsModuleChallengeCategory(category)
            || category.Items == null
            || currentTier < 0
            || currentTier >= category.Items.Count)
        {
            return false;
        }

        ObjectiveTargetItem item = category.Items[currentTier];
        int mult = SimpleResearchAndChallengeMultipliers.ChallengeRewardMultiplier;
        uint scaledCurrency = ScaleCurrency(item.CurrencyReward, mult);

        AddXPEvent addXPEvent = Fields.ObjectiveManagerAddXP(manager);
        AddCurrencyEvent addCurrencyEvent = Fields.ObjectiveManagerAddCurrency(manager);
        ShowIngameNotificationEvent showNotificationEvent = Fields.ObjectiveManagerShowNotification(manager);
        ChallengesUILibrary challengesUILibrary = Fields.ObjectiveManagerChallengesUi(manager);
        CurrencyUILibrary currencyUILibrary = Fields.ObjectiveManagerCurrencyUi(manager);
        AudioManagerLocator audioManagerLocator = Fields.ObjectiveManagerAudio(manager);
        BaseEvent moduleChallengeCompleted = Fields.ObjectiveManagerModuleChallengeCompleted(manager);
        ModuleChallengeSO moduleChallengeSO = Fields.ObjectiveManagerModuleChallenge(manager);

        addXPEvent?.Fire(ToInt(item.XpReward), xpEarnedSource);
        if (item.CurrenyRewardResourceData != null)
        {
            addCurrencyEvent?.Fire(new AddCurrencyEventDto(item.CurrenyRewardResourceData, ToInt(scaledCurrency)));
        }

        Sprite currencySprite = null;
        if (item.CurrenyRewardResourceData != null
            && currencyUILibrary != null
            && currencyUILibrary.CurrencyUIs.TryGetValue(item.CurrenyRewardResourceData, out CurrencyUILibrary.CurrencyUI currencyUI))
        {
            currencySprite = currencyUI.Sprite;
        }

        InGameNotificationDto notification = new InGameNotificationDto(
            deliveriesNotificationDto: new InGameObjectivesNotificationDto(
                Color.white,
                currentTier,
                item.XpReward,
                currencySprite,
                scaledCurrency),
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

    internal static void TryUpdateCurrencyLabelOnly(ChallengeRewardLabels labels, ObjectiveTargetItem item, int tier)
    {
        if (!SimpleResearchAndChallengeMultipliers.HasChallengeCurrencyScaling)
        {
            return;
        }

        IList rewardLabels = Fields.RewardLabelsField.GetValue(labels) as IList;
        if (rewardLabels == null || tier < 0 || tier >= rewardLabels.Count)
        {
            return;
        }

        object rewardLabel = rewardLabels[tier];
        TextMeshProUGUI currencyText = rewardLabel == null ? null : Fields.RewardLabelCurrencyTextField.GetValue(rewardLabel) as TextMeshProUGUI;
        if (currencyText == null)
        {
            return;
        }

        uint scaled = ScaleCurrency(item.CurrencyReward, SimpleResearchAndChallengeMultipliers.ChallengeRewardMultiplier);
        currencyText.SetText(scaled.ToString());
    }

    private static void StartRewardCosmetic(ObjectiveManager manager, ModuleChallengeSet claimedItemSet)
    {
        if (Fields.RewardCosmeticMethod == null)
        {
            return;
        }

        IEnumerator rewardCosmeticRoutine = Fields.RewardCosmeticMethod.Invoke(manager, new object[] { claimedItemSet }) as IEnumerator;
        if (rewardCosmeticRoutine != null)
        {
            manager.StartCoroutine(rewardCosmeticRoutine);
        }
    }

    private static int ToInt(uint value)
    {
        return value > int.MaxValue ? int.MaxValue : (int)value;
    }

    private static uint ScaleCurrency(uint amount, int multiplier)
    {
        if (amount == 0u || multiplier <= 0)
        {
            return 0u;
        }

        ulong product = (ulong)amount * (ulong)multiplier;
        return product > uint.MaxValue ? uint.MaxValue : (uint)product;
    }

    private static class Fields
    {
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

        internal static readonly FieldInfo RewardLabelCurrencyTextField =
            AccessTools.Field(RewardLabelType, "CurrencyText");

        internal static readonly MethodInfo RewardCosmeticMethod =
            AccessTools.Method(typeof(ObjectiveManager), "RewardCosmetic");
    }
}

/// <summary>
/// Nested depth: <see cref="DataShardsValidator.CanBuy"/> can run under <see cref="TechTreeNodeView"/> refresh.
/// </summary>
internal static class TechTreeShardCostScope
{
    private static int _depth;

    internal static bool IsActive => _depth > 0;

    internal static void Enter()
    {
        _depth++;
    }

    internal static void Exit()
    {
        if (_depth > 0)
        {
            _depth--;
        }
    }
}

[HarmonyPatch(typeof(ResourceCost), nameof(ResourceCost.GetAllCosts))]
static class SimpleResearchAndChallengeMultipliers_ResourceCost_GetAllCosts_Patch
{
    static void Postfix(Dictionary<ResourceDataSO, int> __result)
    {
        if (!TechTreeShardCostScope.IsActive)
        {
            return;
        }

        int mult = SimpleResearchAndChallengeMultipliers.ResearchCostMultiplier;
        List<ResourceDataSO> keys = new List<ResourceDataSO>(__result.Keys);
        foreach (ResourceDataSO key in keys)
        {
            __result[key] *= mult;
        }
    }
}

[HarmonyPatch(typeof(DataShardsValidator), nameof(DataShardsValidator.CanBuy))]
static class SimpleResearchAndChallengeMultipliers_DataShardsValidator_CanBuy_Scope
{
    static void Prefix()
    {
        TechTreeShardCostScope.Enter();
    }

    static void Finalizer()
    {
        TechTreeShardCostScope.Exit();
    }
}

[HarmonyPatch(typeof(DataShardsValidator), nameof(DataShardsValidator.Buy))]
static class SimpleResearchAndChallengeMultipliers_DataShardsValidator_Buy_Scope
{
    static void Prefix()
    {
        TechTreeShardCostScope.Enter();
    }

    static void Finalizer()
    {
        TechTreeShardCostScope.Exit();
    }
}

[HarmonyPatch(typeof(TechTreeNodeView), "SetCost")]
static class SimpleResearchAndChallengeMultipliers_TechTreeNodeView_SetCost_Scope
{
    static void Prefix()
    {
        TechTreeShardCostScope.Enter();
    }

    static void Finalizer()
    {
        TechTreeShardCostScope.Exit();
    }
}

[HarmonyPatch(typeof(TechTreeSaveDataNode), MethodType.Constructor, typeof(int), typeof(int), typeof(ResourceCost))]
static class SimpleResearchAndChallengeMultipliers_TechTreeSaveDataNode_Ctor_Scope
{
    static void Prefix()
    {
        TechTreeShardCostScope.Enter();
    }

    static void Finalizer()
    {
        TechTreeShardCostScope.Exit();
    }
}

[HarmonyPatch(typeof(ObjectiveManager), "AwardObjectiveRewards")]
static class SimpleResearchAndChallengeMultipliers_ObjectiveManager_AwardObjectiveRewards_Patch
{
    static bool Prefix(ObjectiveManager __instance, ObjectiveTargetCategorySO objectiveTargetCategory, XPEarnedSource xpEarnedSource, int currentTier)
    {
        return !SimpleModuleChallengeCurrencyAward.TryAwardObjectiveRewards(__instance, objectiveTargetCategory, xpEarnedSource, currentTier);
    }
}

[HarmonyPatch(typeof(ChallengeRewardLabels), nameof(ChallengeRewardLabels.Build))]
static class SimpleResearchAndChallengeMultipliers_ChallengeRewardLabels_Build_Patch
{
    static void Postfix(ChallengeRewardLabels __instance, ObjectiveTargetItem item, int tier)
    {
        SimpleModuleChallengeCurrencyAward.TryUpdateCurrencyLabelOnly(__instance, item, tier);
    }
}

[HarmonyPatch(typeof(ObjectiveTargetCategorySO), "get_CurrentTier")]
static class SimpleResearchAndChallengeMultipliers_ObjectiveTargetCategorySO_CurrentTier_Patch
{
    static bool Prefix(ObjectiveTargetCategorySO __instance, ref int __result)
    {
        int mult = ObjectiveRequirementScaling.GetRequirementMultiplier(__instance);
        if (mult <= 1 || __instance.Items == null || __instance.Items.Count == 0)
        {
            return true;
        }

        uint delivered = __instance.DeliveredAmount;
        int i;
        for (i = 0; i < __instance.Items.Count; i++)
        {
            uint threshold = ObjectiveRequirementScaling.ScaleRequirement(__instance.Items[i].RequiredAmount, mult);
            if (delivered < threshold)
            {
                break;
            }
        }

        __result = i;
        return false;
    }
}

[HarmonyPatch(typeof(ObjectiveTargetCategorySO), "get_DisplayDeliveredInTier")]
static class SimpleResearchAndChallengeMultipliers_ObjectiveTargetCategorySO_DisplayDeliveredInTier_Patch
{
    static bool Prefix(ObjectiveTargetCategorySO __instance, ref uint __result)
    {
        int mult = ObjectiveRequirementScaling.GetRequirementMultiplier(__instance);
        if (mult <= 1 || __instance.Items == null || __instance.Items.Count == 0)
        {
            return true;
        }

        uint delivered = __instance.DeliveredAmount;
        int clamped = __instance.ClampedCurrentTier;
        uint scaledOffset = ObjectiveRequirementScaling.ScaleRequirement(__instance.Items[clamped].AmountStartOffset, mult);
        __result = delivered > scaledOffset ? delivered - scaledOffset : 0u;
        return false;
    }
}

[HarmonyPatch(typeof(ObjectiveTargetCategorySO), "get_DisplayRequiredInTier")]
static class SimpleResearchAndChallengeMultipliers_ObjectiveTargetCategorySO_DisplayRequiredInTier_Patch
{
    static bool Prefix(ObjectiveTargetCategorySO __instance, ref uint __result)
    {
        int mult = ObjectiveRequirementScaling.GetRequirementMultiplier(__instance);
        if (mult <= 1 || __instance.Items == null || __instance.Items.Count == 0)
        {
            return true;
        }

        int clamped = __instance.ClampedCurrentTier;
        __result = ObjectiveRequirementScaling.ScaleRequirement(__instance.Items[clamped].Amount, mult);
        return false;
    }
}

[HarmonyPatch(typeof(TargetItemView), "SetViewCurrent")]
static class SimpleResearchAndChallengeMultipliers_TargetItemView_SetViewCurrent_Patch
{
    static void Postfix(TargetItemView __instance, ObjectiveTargetItem item, uint deliveredAmount)
    {
        if (item == null)
        {
            return;
        }

        ObjectiveTargetCategorySO category = TargetItemViewFields.CategorySo(__instance);
        int m = category != null ? ObjectiveRequirementScaling.GetRequirementMultiplier(category) : 1;
        if (m <= 1)
        {
            return;
        }

        uint denom = ObjectiveRequirementScaling.ScaleRequirement(item.Amount, m);
        if (denom == 0u)
        {
            return;
        }

        Image bg = TargetItemViewFields.Background(__instance);
        if (bg != null)
        {
            bg.fillAmount = (float)deliveredAmount / (float)denom;
        }
    }
}

[HarmonyPatch(typeof(TargetItemView), "UpdateText")]
static class SimpleResearchAndChallengeMultipliers_TargetItemView_UpdateText_Patch
{
    static void Postfix(TargetItemView __instance)
    {
        ObjectiveTargetCategorySO category = TargetItemViewFields.CategorySo(__instance);
        ObjectiveTargetItem item = TargetItemViewFields.ItemSo(__instance);
        int tier = TargetItemViewFields.Tier(__instance);
        if (category == null || item == null)
        {
            return;
        }

        int m = ObjectiveRequirementScaling.GetRequirementMultiplier(category);
        if (m <= 1 || category.CurrentTier == tier)
        {
            return;
        }

        uint scaled = ObjectiveRequirementScaling.ScaleRequirement(item.Amount, m);
        AdvancedTextInfoPanelContent info = TargetItemViewFields.InfoPanel(__instance);
        if (info == null)
        {
            return;
        }

        string categoryName = TargetItemViewFields.CategoryName(__instance);
        string title = string.Format(LocalizationUtility.GetLocalizedText("DeliverTargets.LevelTitle"), categoryName, (tier + 1).ToString());
        string desc = string.Format(LocalizationUtility.GetLocalizedText("DeliverTargets.LevelDescription"), scaled.ToString(), categoryName);
        info.UpdateContent(title, desc);
    }
}

[HarmonyPatch(typeof(ChallengeItemView), "SetViewCurrent")]
static class SimpleResearchAndChallengeMultipliers_ChallengeItemView_SetViewCurrent_Patch
{
    static void Postfix(ChallengeItemView __instance, ObjectiveTargetItem item, uint deliveredAmount)
    {
        if (item == null)
        {
            return;
        }

        int m = SimpleResearchAndChallengeMultipliers.ShapeChallengeRequirementMultiplier;
        if (m <= 1)
        {
            return;
        }

        uint denom = ObjectiveRequirementScaling.ScaleRequirement(item.Amount, m);
        if (denom == 0u)
        {
            return;
        }

        Image bg = ChallengeItemViewFields.Background(__instance);
        if (bg != null)
        {
            bg.fillAmount = (float)deliveredAmount / (float)denom;
        }

        AdvancedTextInfoPanelContent info = ChallengeItemViewFields.InfoPanel(__instance);
        if (info != null && info.enabled)
        {
            info.UpdateText2($"{deliveredAmount}/{denom}");
            info.ForceUpdate();
        }
    }
}

[HarmonyPatch(typeof(ChallengeItemView), "SetViewClaimed")]
static class SimpleResearchAndChallengeMultipliers_ChallengeItemView_SetViewClaimed_Patch
{
    static void Postfix(ChallengeItemView __instance, ObjectiveTargetItem item)
    {
        if (item == null)
        {
            return;
        }

        int m = SimpleResearchAndChallengeMultipliers.ShapeChallengeRequirementMultiplier;
        if (m <= 1)
        {
            return;
        }

        uint denom = ObjectiveRequirementScaling.ScaleRequirement(item.Amount, m);
        AdvancedTextInfoPanelContent info = ChallengeItemViewFields.InfoPanel(__instance);
        if (info != null && info.enabled)
        {
            info.UpdateText2($"{denom}/{denom}");
            info.ForceUpdate();
        }
    }
}

[HarmonyPatch(typeof(ModuleChallengesUI), "InstantiateModuleChallengesViews")]
static class SimpleResearchAndChallengeMultipliers_ModuleChallengesUI_InstantiateModuleChallengesViews_Patch
{
    static void Postfix(ModuleChallengesUI __instance)
    {
        int m = SimpleResearchAndChallengeMultipliers.ShapeChallengeRequirementMultiplier;
        if (m <= 1)
        {
            return;
        }

        ModuleChallengeSO challengeSo = ModuleChallengesUiFields.ModuleChallengeSo(__instance);
        List<TextMeshProUGUI> texts = ModuleChallengesUiFields.DeliverAmountsTexts(__instance);
        if (challengeSo?.Sets == null || challengeSo.Sets.Count == 0 || texts == null)
        {
            return;
        }

        ModuleChallengeSet set0 = challengeSo.Sets[0];
        if (set0.Categories == null || set0.Categories.Count == 0)
        {
            return;
        }

        ObjectiveTargetCategorySO cat = set0.Categories[0];
        if (cat?.Items == null)
        {
            return;
        }

        for (int i = 0; i < texts.Count && i < cat.Items.Count; i++)
        {
            uint s = ObjectiveRequirementScaling.ScaleRequirement(cat.Items[i].Amount, m);
            texts[i].SetText(s.ToString());
        }
    }
}

internal static class TargetItemViewFields
{
    internal static readonly AccessTools.FieldRef<TargetItemView, ObjectiveTargetCategorySO> CategorySo =
        AccessTools.FieldRefAccess<TargetItemView, ObjectiveTargetCategorySO>("_categorySO");

    internal static readonly AccessTools.FieldRef<TargetItemView, ObjectiveTargetItem> ItemSo =
        AccessTools.FieldRefAccess<TargetItemView, ObjectiveTargetItem>("_itemSO");

    internal static readonly AccessTools.FieldRef<TargetItemView, int> Tier =
        AccessTools.FieldRefAccess<TargetItemView, int>("_tier");

    internal static readonly AccessTools.FieldRef<TargetItemView, string> CategoryName =
        AccessTools.FieldRefAccess<TargetItemView, string>("_categoryName");

    internal static readonly AccessTools.FieldRef<TargetItemView, AdvancedTextInfoPanelContent> InfoPanel =
        AccessTools.FieldRefAccess<TargetItemView, AdvancedTextInfoPanelContent>("_infoPanel");

    internal static readonly AccessTools.FieldRef<TargetItemView, Image> Background =
        AccessTools.FieldRefAccess<TargetItemView, Image>("_background");
}

internal static class ChallengeItemViewFields
{
    internal static readonly AccessTools.FieldRef<ChallengeItemView, AdvancedTextInfoPanelContent> InfoPanel =
        AccessTools.FieldRefAccess<ChallengeItemView, AdvancedTextInfoPanelContent>("_infoPanel");

    internal static readonly AccessTools.FieldRef<ChallengeItemView, Image> Background =
        AccessTools.FieldRefAccess<ChallengeItemView, Image>("_background");
}

internal static class ModuleChallengesUiFields
{
    internal static readonly AccessTools.FieldRef<ModuleChallengesUI, ModuleChallengeSO> ModuleChallengeSo =
        AccessTools.FieldRefAccess<ModuleChallengesUI, ModuleChallengeSO>("_moduleChallengeSO");

    internal static readonly AccessTools.FieldRef<ModuleChallengesUI, System.Collections.Generic.List<TextMeshProUGUI>> DeliverAmountsTexts =
        AccessTools.FieldRefAccess<ModuleChallengesUI, System.Collections.Generic.List<TextMeshProUGUI>>("_deliverAmountsTexts");
}
