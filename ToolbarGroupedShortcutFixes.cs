using System;
using System.Collections.Generic;
using HarmonyLib;
using Presentation.FactoryFloor.Toolbar;
using Presentation.UI.Toolbar;
using ScriptEngine;
using UnityEngine;
using UnityEngine.InputSystem;

[ScriptEntry]
public sealed class ToolbarGroupedShortcutFixes : ScriptMod
{
    private static ToolbarGroupedShortcutFixes _instance;

    private static readonly Dictionary<string, string> BreadcrumbToTargetAction = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        { "PainterData", "MonotonerShortCut" },
        { "DyeMixerData", "MonotonerShortCut" },
        { "CounterData", "ScrapperShortCut" },
        { "DataCenterData", "CounterShortCut" },
        { "ChemicalPlantData", "CounterShortCut" },
        { "SupplyTankData", "HarvestorPadShortCut" },
        { "StorageDepotData", "HarvestorPadShortCut" },
        { "FreightHubData", "HarvestorPadShortCut" }
    };

    private static readonly Dictionary<string, Dictionary<string, int>> DesiredOrderByAction = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal)
    {
        {
            "MonotonerShortCut",
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "MonotonerData", 0 },
                { "PainterData", 1 },
                { "DyeMixerData", 2 }
            }
        },
        {
            "ScrapperShortCut",
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "CounterData", 0 },
                { "ScrapperData", 1 }
            }
        },
        {
            "CounterShortCut",
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "DataCenterData", 0 },
                { "ChemicalPlantData", 1 }
            }
        },
        {
            "HarvestorPadShortCut",
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "HarvesterPadData", 0 },
                { "StorageDepotData", 1 },
                { "SupplyTankData", 2 },
                { "FreightHubData", 3 }
            }
        }
    };

    private static readonly Dictionary<string, InputActionReference> ActionCache = new Dictionary<string, InputActionReference>(StringComparer.Ordinal);

    protected override void OnEnable()
    {
        _instance = this;
        ApplyToExistingShortcuts();
    }

    protected override void OnDisable()
    {
        ActionCache.Clear();
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }
    }

    internal static void RemapShortcutAction(ToolBarButtonShortcut shortcut)
    {
        if (shortcut == null)
        {
            return;
        }

        string breadcrumb = GetBreadcrumb(shortcut);
        if (string.IsNullOrEmpty(breadcrumb))
        {
            return;
        }

        string targetActionName;
        if (!BreadcrumbToTargetAction.TryGetValue(breadcrumb, out targetActionName))
        {
            return;
        }

        InputActionReference targetReference = ResolveActionReference(targetActionName);
        if (targetReference == null)
        {
            _instance?.Warn($"Could not find InputActionReference for {targetActionName} while remapping {breadcrumb}.");
            return;
        }

        InputActionReference currentReference = Traverse.Create(shortcut).Field("_groupInputAction").GetValue<InputActionReference>();
        if (ReferenceEquals(currentReference, targetReference))
        {
            return;
        }

        Traverse.Create(shortcut).Field("_groupInputAction").SetValue(targetReference);
        _instance?.Log($"{breadcrumb} -> {targetActionName}");
    }

    internal static void NormalizeShortcutGroup(ToolBarButtonShortcut shortcut)
    {
        if (shortcut == null)
        {
            return;
        }

        InputActionReference inputActionReference = Traverse.Create(shortcut).Field("_groupInputAction").GetValue<InputActionReference>();
        if (inputActionReference == null || inputActionReference.action == null)
        {
            return;
        }

        string actionName = inputActionReference.action.name;
        Dictionary<string, int> desiredOrder;
        if (!DesiredOrderByAction.TryGetValue(actionName, out desiredOrder))
        {
            return;
        }

        ToolBarButtonGroupsSO groupSo = Traverse.Create(shortcut).Field("_toolBarButtonGroup").GetValue<ToolBarButtonGroupsSO>();
        if (groupSo == null)
        {
            return;
        }

        Dictionary<InputAction, ToolBarButtonGroup> groups = Traverse.Create(groupSo)
            .Field("_buttonsByInput")
            .GetValue<Dictionary<InputAction, ToolBarButtonGroup>>();
        if (groups == null)
        {
            return;
        }

        ToolBarButtonGroup group;
        if (!groups.TryGetValue(inputActionReference.action, out group) || group == null)
        {
            return;
        }

        List<ToolBarButtonShortcut> buttons = Traverse.Create(group).Field("_buttons").GetValue<List<ToolBarButtonShortcut>>();
        List<bool> hasButtons = Traverse.Create(group).Field("_hasButtons").GetValue<List<bool>>();
        if (buttons == null || hasButtons == null || buttons.Count != hasButtons.Count)
        {
            return;
        }

        List<ToolBarButtonShortcut> activeButtons = new List<ToolBarButtonShortcut>();
        for (int i = 0; i < buttons.Count; i++)
        {
            if (hasButtons[i] && buttons[i] != null)
            {
                activeButtons.Add(buttons[i]);
            }
        }

        if (activeButtons.Count <= 1)
        {
            return;
        }

        activeButtons.Sort(delegate (ToolBarButtonShortcut a, ToolBarButtonShortcut b)
        {
            int orderA = GetDesiredOrder(desiredOrder, GetBreadcrumb(a));
            int orderB = GetDesiredOrder(desiredOrder, GetBreadcrumb(b));
            if (orderA != orderB)
            {
                return orderA.CompareTo(orderB);
            }
            return string.CompareOrdinal(GetBreadcrumb(a), GetBreadcrumb(b));
        });

        bool changed = false;
        for (int i = 0; i < activeButtons.Count; i++)
        {
            if (!ReferenceEquals(buttons[i], activeButtons[i]) || !hasButtons[i])
            {
                changed = true;
                break;
            }
        }
        if (!changed)
        {
            return;
        }

        for (int i = 0; i < activeButtons.Count; i++)
        {
            buttons[i] = activeButtons[i];
            hasButtons[i] = true;
            Traverse.Create(activeButtons[i]).Field("_groupIndex").SetValue(i);
        }

        for (int i = activeButtons.Count; i < buttons.Count; i++)
        {
            buttons[i] = null;
            hasButtons[i] = false;
        }

        groupSo.SetLastPressedButton(inputActionReference.action, 0);
        _instance?.Log($"Normalized {actionName}: {JoinBreadcrumbs(activeButtons)}");
    }

    private static void ApplyToExistingShortcuts()
    {
        ApplyExistingShortcut("PainterData");
        ApplyExistingShortcut("DyeMixerData");

        ApplyExistingShortcut("DataCenterData");
        ApplyExistingShortcut("ChemicalPlantData");
        SetGroupSelection("CounterShortCut", "DataCenterData");
        ApplyExistingShortcut("CounterData");

        ApplyExistingShortcut("StorageDepotData");
        ApplyExistingShortcut("SupplyTankData");
        ApplyExistingShortcut("FreightHubData");
    }

    private static void ApplyExistingShortcut(string breadcrumb)
    {
        ToolBarButtonShortcut shortcut = FindShortcutByBreadcrumb(breadcrumb);
        if (shortcut == null)
        {
            return;
        }

        shortcut.UnInit();
        RemapShortcutAction(shortcut);
        shortcut.Init();
        NormalizeShortcutGroup(shortcut);
    }

    private static ToolBarButtonShortcut FindShortcutByBreadcrumb(string breadcrumb)
    {
        foreach (ToolBarButtonShortcut shortcut in Resources.FindObjectsOfTypeAll<ToolBarButtonShortcut>())
        {
            if (shortcut == null || !shortcut.gameObject.scene.IsValid())
            {
                continue;
            }

            if (string.Equals(GetBreadcrumb(shortcut), breadcrumb, StringComparison.Ordinal))
            {
                return shortcut;
            }
        }

        return null;
    }

    private static void SetGroupSelection(string actionName, string activeBreadcrumb)
    {
        InputActionReference actionReference = ResolveActionReference(actionName);
        if (actionReference == null || actionReference.action == null)
        {
            return;
        }

        foreach (ToolBarButtonGroupsSO groupSo in Resources.FindObjectsOfTypeAll<ToolBarButtonGroupsSO>())
        {
            if (groupSo == null)
            {
                continue;
            }

            Dictionary<InputAction, ToolBarButtonGroup> groups = Traverse.Create(groupSo)
                .Field("_buttonsByInput")
                .GetValue<Dictionary<InputAction, ToolBarButtonGroup>>();
            if (groups == null)
            {
                continue;
            }

            ToolBarButtonGroup group;
            if (!groups.TryGetValue(actionReference.action, out group) || group == null)
            {
                continue;
            }

            List<ToolBarButtonShortcut> buttons = Traverse.Create(group).Field("_buttons").GetValue<List<ToolBarButtonShortcut>>();
            List<bool> hasButtons = Traverse.Create(group).Field("_hasButtons").GetValue<List<bool>>();
            if (buttons == null || hasButtons == null)
            {
                continue;
            }

            for (int i = 0; i < buttons.Count; i++)
            {
                if (!hasButtons[i] || buttons[i] == null)
                {
                    continue;
                }

                if (string.Equals(GetBreadcrumb(buttons[i]), activeBreadcrumb, StringComparison.Ordinal))
                {
                    groupSo.SetLastPressedButton(actionReference.action, i);
                    _instance?.Log($"Selected {activeBreadcrumb} in {actionName}");
                    return;
                }
            }
        }
    }

    private static int GetDesiredOrder(Dictionary<string, int> desiredOrder, string breadcrumb)
    {
        int order;
        return desiredOrder.TryGetValue(breadcrumb, out order) ? order : int.MaxValue;
    }

    private static string JoinBreadcrumbs(List<ToolBarButtonShortcut> shortcuts)
    {
        string result = "";
        for (int i = 0; i < shortcuts.Count; i++)
        {
            if (i > 0)
            {
                result += " -> ";
            }
            result += GetBreadcrumb(shortcuts[i]);
        }
        return result;
    }

    private static InputActionReference ResolveActionReference(string actionName)
    {
        InputActionReference cached;
        if (ActionCache.TryGetValue(actionName, out cached) && cached != null)
        {
            return cached;
        }

        foreach (InputActionReference reference in Resources.FindObjectsOfTypeAll<InputActionReference>())
        {
            if (reference == null)
            {
                continue;
            }

            if (reference.action != null && string.Equals(reference.action.name, actionName, StringComparison.Ordinal))
            {
                ActionCache[actionName] = reference;
                return reference;
            }

            if (string.Equals(reference.name, actionName, StringComparison.Ordinal))
            {
                ActionCache[actionName] = reference;
                return reference;
            }
        }

        return null;
    }

    private static string GetBreadcrumb(ToolBarButtonShortcut shortcut)
    {
        ToolBarButton toolBarButton = Traverse.Create(shortcut).Field("_toolBarButton").GetValue<ToolBarButton>();
        if (toolBarButton == null)
        {
            return "";
        }

        try
        {
            return toolBarButton.BreadcrumbId ?? "";
        }
        catch
        {
            return "";
        }
    }
}

[HarmonyPatch(typeof(ToolBarButtonShortcut), nameof(ToolBarButtonShortcut.Init))]
static class ToolbarGroupedShortcutFixes_ToolBarButtonShortcut_Init_Patch
{
    static void Prefix(ToolBarButtonShortcut __instance)
    {
        ToolbarGroupedShortcutFixes.RemapShortcutAction(__instance);
    }

    static void Postfix(ToolBarButtonShortcut __instance)
    {
        ToolbarGroupedShortcutFixes.NormalizeShortcutGroup(__instance);
    }
}

// When the user presses group 1's shortcut after cycling group 3 to item 3-2,
// group 3's GUI keeps showing 3-2 as active. The fix: whenever any group's active
// button is set (via SetLastPressedButton), immediately reset all OTHER groups back
// to index 0 so their GUI reflects the first item.
[HarmonyPatch(typeof(ToolBarButtonGroupsSO), nameof(ToolBarButtonGroupsSO.SetLastPressedButton))]
static class ToolbarGroupedShortcutFixes_ToolBarButtonGroupsSO_SetLastPressedButton_Patch
{
    [ThreadStatic]
    private static bool _resetting;

    static void Postfix(ToolBarButtonGroupsSO __instance, InputAction inputAction)
    {
        if (_resetting)
        {
            return;
        }

        Dictionary<InputAction, ToolBarButtonGroup> groups = Traverse.Create(__instance)
            .Field("_buttonsByInput")
            .GetValue<Dictionary<InputAction, ToolBarButtonGroup>>();
        if (groups == null)
        {
            return;
        }

        _resetting = true;
        try
        {
            foreach (InputAction action in groups.Keys)
            {
                if (ReferenceEquals(action, inputAction))
                {
                    continue;
                }

                __instance.SetLastPressedButton(action, 0);
            }
        }
        finally
        {
            _resetting = false;
        }
    }
}
