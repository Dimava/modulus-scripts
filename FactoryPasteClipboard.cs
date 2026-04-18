using System;
using System.Text;
using Data.Operator;
using HarmonyLib;
using Logic.Factory;
using Logic.Factory.Blueprint;
using Logic.FactoryTools;
using Data.FactoryFloor;
using Presentation.FactoryFloor;
using Presentation.Locators;
using SaveData.FactoryFloor;
using ScriptEngine;
using UnityEngine;
using UnityEngine.EventSystems;
using Utils.JsonConverterUtils;
using Newtonsoft.Json;

[ScriptEntry]
public sealed class FactoryPasteClipboard : ScriptMod
{
    private static FactoryPasteClipboard? _instance;
    private const string ClipboardPrefix = "modulus-blueprint:";
    private const string ClipboardBlueprintName = "Clipboard";
    private static Blueprint? _clipboard;
    private static string _clipboardLabel = "clipboard";

    protected override void OnEnable()
    {
        _instance = this;
        BindKey("keyCopy", "Ctrl+C");
        BindKey("keyPaste", "V");
        BindKey("keyPasteSystemClipboard", "Ctrl+V");
    }

    protected override void OnDisable()
    {
        _clipboard = null;
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }
    }

    protected override void OnUpdate()
    {
        if (IsUiFocused())
            return;

        if (WasPressed("keyCopy"))
            TryCopySelectedToolToClipboard();

        if (WasPressed("keyPasteSystemClipboard"))
        {
            TryPaste(preferSystemClipboard: true);
            return;
        }

        if (WasPressed("keyPaste"))
            TryPaste(preferSystemClipboard: false);
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

        _instance?.Log($"Paste ready: {_clipboardLabel}");
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

        _instance?.Log($"Auto-copied from {source}: {_clipboardLabel}");
    }

    static bool StoreBlueprint(Blueprint? blueprint)
    {
        if (blueprint == null || blueprint.Elements == null || blueprint.Elements.Count == 0)
            return false;

        _clipboard = blueprint.GetCopy();
        _clipboardLabel = DescribeBlueprint(_clipboard);

        if (TryWriteClipboardString(_clipboard))
            _instance?.Log($"Copied: {_clipboardLabel} (system clipboard updated)");
        else
            _instance?.Log($"Copied: {_clipboardLabel}");

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
            _instance?.Warn($"Failed to decode system clipboard blueprint: {ex.Message}");
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

        _instance?.Log($"Loaded from system clipboard: {_clipboardLabel}");
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
            _instance?.Warn($"Failed to write system clipboard blueprint: {ex.Message}");
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
