using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Logic.Factory.Blueprint;
using Logic.Factory;
using Logic.FactoryTools;
using Data.FactoryFloor;
using Data.Operator;
using MelonLoader;
using Newtonsoft.Json;
using Presentation.FactoryFloor;
using Presentation.Locators;
using SaveData.FactoryFloor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using Utils.JsonConverterUtils;

public static class FactoryPasteClipboard
{
    static readonly HarmonyLib.Harmony _harmony = new("factory-paste-clipboard");
    const string ClipboardPrefix = "modulus-blueprint:";
    const string ClipboardBlueprintName = "Clipboard";
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

        MelonLogger.Msg("[FactoryPasteClipboard] Loaded. Duplicate selections update the clipboard automatically, V pastes, Ctrl+C/Ctrl+V use the system clipboard.");
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

    internal static bool TryCopySelectedToolToClipboard()
    {
        var toolSystem = FindToolSystem();
        if (toolSystem == null)
            return false;

        var selectionTool = toolSystem.SelectedTool as SelectionFactoryTool;
        if (selectionTool == null)
            return _clipboard != null && TryWriteClipboardString(_clipboard);

        var blueprint = Traverse.Create(selectionTool)
            .Field("_selection")
            .GetValue<Blueprint>();
        return StoreBlueprint(blueprint);
    }

    internal static void TryPaste(bool preferSystemClipboard = false)
    {
        if (preferSystemClipboard)
            TryLoadBlueprintFromSystemClipboard();

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

        if (TryWriteClipboardString(_clipboard))
            MelonLogger.Msg($"[FactoryPasteClipboard] Copied: {_clipboardLabel} (system clipboard updated)");
        else
            MelonLogger.Msg($"[FactoryPasteClipboard] Copied: {_clipboardLabel}");

        return true;
    }

    static bool TryLoadBlueprintFromSystemClipboard()
    {
        var clipboardText = GUIUtility.systemCopyBuffer;
        if (string.IsNullOrWhiteSpace(clipboardText) || !clipboardText.StartsWith(ClipboardPrefix, StringComparison.Ordinal))
            return false;

        var payload = clipboardText.Substring(ClipboardPrefix.Length);
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        string json;
        try
        {
            json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[FactoryPasteClipboard] Failed to decode system clipboard blueprint: {ex.Message}");
            return false;
        }

        if (!SaveSystem.TryReadJson<BlueprintDto>(json, out var blueprintDto) || blueprintDto == null)
            return false;

        var toolSystem = FindToolSystem();
        if (toolSystem == null)
            return false;

        var factoryObjectDatabase = Traverse.Create(toolSystem)
            .Field("_factoryObjectDatabase")
            .GetValue<FactoryObjectDatabase>();
        if (factoryObjectDatabase == null)
            return false;

        var blueprint = blueprintDto.CopyToBlueprint(factoryObjectDatabase);
        if (!StoreBlueprint(blueprint))
            return false;

        MelonLogger.Msg($"[FactoryPasteClipboard] Loaded from system clipboard: {_clipboardLabel}");
        return true;
    }

    static bool TryWriteClipboardString(Blueprint blueprint)
    {
        try
        {
            var dto = new BlueprintDto(blueprint, ClipboardBlueprintName, Color.white, -1);
            GUIUtility.systemCopyBuffer = ClipboardPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(SerializeBlueprintDto(dto)));
            return true;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[FactoryPasteClipboard] Failed to write system clipboard blueprint: {ex.Message}");
            return false;
        }
    }

    static string SerializeBlueprintDto(BlueprintDto blueprintDto)
    {
        var settings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.Auto,
            DefaultValueHandling = DefaultValueHandling.Ignore
        };
        settings.Converters.Add(new ColorConverter());
        settings.Converters.Add(new Vector2Converter());
        settings.Converters.Add(new Vector3Converter());
        settings.Converters.Add(new Vector4Converter());
        settings.Converters.Add(new Vector2IntConverter());
        settings.Converters.Add(new Vector3IntConverter());
        return JsonConvert.SerializeObject(blueprintDto, settings);
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

        var ctrlHeld = keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed;
        if (ctrlHeld && keyboard.cKey.wasPressedThisFrame)
            FactoryPasteClipboard.TryCopySelectedToolToClipboard();

        if (keyboard.vKey.wasPressedThisFrame)
            FactoryPasteClipboard.TryPaste(preferSystemClipboard: ctrlHeld);
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
