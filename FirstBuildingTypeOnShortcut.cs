using System.Collections.Generic;
using HarmonyLib;
using MelonLoader;
using Presentation.FactoryFloor.Toolbar;
using Presentation.UI.Toolbar;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// When a number shortcut key is pressed and NO button in the group is
/// currently selected (i.e. you're coming from a different tool),
/// always start cycling from index 0 — the primary building type.
///
/// Pressing the key again while index 0 is already selected will cycle
/// to index 1, and so on — same as vanilla.
/// </summary>
public static class FirstBuildingTypeOnShortcut
{
    static readonly HarmonyLib.Harmony _harmony = new HarmonyLib.Harmony("first-building-type-on-shortcut");

    public static void OnLoad()
    {
        _harmony.UnpatchSelf();
        _harmony.PatchAll(typeof(FirstBuildingTypeOnShortcut).Assembly);
        MelonLogger.Msg("[FirstBuildingTypeOnShortcut] Loaded.");
    }

    public static void OnUnload()
    {
        _harmony.UnpatchSelf();
    }
}

// On keypress: if nothing selected, reset to index 0 and update visuals before cycling
[HarmonyPatch(typeof(ToolBarButtonGroup), "ActionPerformed")]
static class ToolBarButtonGroup_ActionPerformed_Patch
{
    static void Prefix(
        ref int ____currentIndex,
        List<ToolBarButtonShortcut> ____buttons,
        List<bool> ____hasButtons)
    {
        bool anySelected = false;
        for (int i = 0; i < ____buttons.Count; i++)
        {
            if (____hasButtons[i] && ____buttons[i] != null && ____buttons[i].IsSelected)
            {
                anySelected = true;
                break;
            }
        }

        if (!anySelected && ____currentIndex != 0)
        {
            ____currentIndex = 0;
            for (int i = 0; i < ____buttons.Count; i++)
                if (____hasButtons[i] && ____buttons[i] != null)
                    ____buttons[i].SetButtonActiveInGroup(i == 0);
        }
    }
}

// On deselect/escape: reset all toolbar groups back to index 0 so the GUI
// always shows the primary building type as the active shortcut target
[HarmonyPatch(typeof(SelectShortcutHelper), "OnActionCanceled")]
static class SelectShortcutHelper_OnActionCanceled_Patch
{
    static void Postfix()
    {
        foreach (var groupSO in Resources.FindObjectsOfTypeAll<ToolBarButtonGroupsSO>())
        {
            var dict = Traverse.Create(groupSO)
                .Field("_buttonsByInput")
                .GetValue<Dictionary<InputAction, ToolBarButtonGroup>>();
            if (dict == null) continue;
            foreach (var kvp in dict)
                groupSO.SetLastPressedButton(kvp.Key, 0);
        }
    }
}
