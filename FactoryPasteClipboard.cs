using System.Collections.Generic;
using HarmonyLib;
using Logic.Factory.Blueprint;
using Logic.Factory;
using Logic.FactoryTools;
using Data.FactoryFloor;
using MelonLoader;
using Presentation.FactoryFloor;
using Presentation.Locators;
using SaveData.FactoryFloor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

public static class FactoryPasteClipboard
{
    static readonly HarmonyLib.Harmony _harmony = new("factory-paste-clipboard");
    static GameObject? _gameObject;
    static Blueprint? _clipboard;
    static string _clipboardLabel = "clipboard";

    public static void OnLoad()
    {
        if (_gameObject != null)
            GameObject.Destroy(_gameObject);

        _gameObject = new GameObject("__FactoryPasteClipboard__");
        GameObject.DontDestroyOnLoad(_gameObject);
        _gameObject.AddComponent<FactoryPasteClipboardBehaviour>();

        _harmony.UnpatchSelf();
        _harmony.PatchAll(typeof(FactoryPasteClipboard).Assembly);

        MelonLogger.Msg("[FactoryPasteClipboard] Loaded. Duplicate selections update the clipboard automatically, V pastes.");
    }

    public static void OnUnload()
    {
        _harmony.UnpatchSelf();
        _clipboard = null;

        if (_gameObject != null)
        {
            GameObject.Destroy(_gameObject);
            _gameObject = null;
        }
    }

    internal static void TryPaste()
    {
        if (_clipboard == null)
            return;

        var toolSystem = FindToolSystem();
        if (toolSystem == null)
            return;

        var placementTool = Traverse.Create(toolSystem)
            .Field("_placementTool")
            .GetValue<PlacementTool>();
        var mouseToGridInput = Traverse.Create(toolSystem)
            .Field("_mouseToGridInput")
            .GetValue<MouseToGridInput>();
        var gridLocator = Traverse.Create(toolSystem)
            .Field("_gridLocator")
            .GetValue<GridLocator>();

        if (placementTool == null || mouseToGridInput == null || gridLocator == null)
            return;

        var blueprint = _clipboard.GetCopy();
        var worldPosition = mouseToGridInput.GetSelectedMapPosition();
        var cellPosition = gridLocator.GetCellPosition(worldPosition);

        blueprint.SetPosition(cellPosition);
        placementTool.SetRotation(0, resetLastRotation: true);
        toolSystem.SelectTool(placementTool, blueprint);

        MelonLogger.Msg($"[FactoryPasteClipboard] Paste ready: {_clipboardLabel}");
    }

    internal static void AutoCopySelection(SelectionFactoryTool selectionTool, string source)
    {
        if (selectionTool == null)
            return;

        var blueprint = Traverse.Create(selectionTool)
            .Field("_selection")
            .GetValue<Blueprint>();
        if (!StoreBlueprint(blueprint))
            return;

        MelonLogger.Msg($"[FactoryPasteClipboard] Auto-copied from {source}: {_clipboardLabel}");
    }

    static bool StoreBlueprint(Blueprint? blueprint)
    {
        if (blueprint == null || blueprint.Elements == null || blueprint.Elements.Count == 0)
            return false;

        _clipboard = blueprint.GetCopy();
        _clipboardLabel = DescribeBlueprint(_clipboard);
        MelonLogger.Msg($"[FactoryPasteClipboard] Copied: {_clipboardLabel}");
        return true;
    }

    static string DescribeBlueprint(Blueprint blueprint)
    {
        if (blueprint.Elements.Count == 1)
            return $"1x {blueprint.Elements[0].ObjectData.name}";

        return $"{blueprint.Elements.Count} objects";
    }

    static ToolSystem? FindToolSystem()
    {
        foreach (var toolSystem in Resources.FindObjectsOfTypeAll<ToolSystem>())
        {
            if (toolSystem == null || !toolSystem.gameObject.scene.IsValid())
                continue;

            return toolSystem;
        }

        return null;
    }
}

public sealed class FactoryPasteClipboardBehaviour : MonoBehaviour
{
    void Update()
    {
        if (IsUiFocused())
            return;

        var keyboard = Keyboard.current;
        if (keyboard == null)
            return;

        if (keyboard.vKey.wasPressedThisFrame)
            FactoryPasteClipboard.TryPaste();
    }

    static bool IsUiFocused()
    {
        return EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null;
    }
}

[HarmonyPatch(typeof(DuplicateTool), "ImplementedSelectTool")]
static class DuplicateTool_ImplementedSelectTool_Patch
{
    static void Postfix(DuplicateTool __instance)
    {
        FactoryPasteClipboard.AutoCopySelection(__instance, "duplicate");
    }
}
