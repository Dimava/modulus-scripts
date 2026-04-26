using System;
using System.IO;
using System.IO.Compression;
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
public sealed class BlueprintClipboardCopyPaste : ScriptMod
{
    private static BlueprintClipboardCopyPaste? _instance;
    private const string ClipboardPrefix = "modulus-blueprint:";
    private const string ClipboardFormatDefault = "j1zu";
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
            TryLoadBlueprintFromSystemClipboard();
            TryPasteFromModClipboard();
            return;
        }

        if (WasPressed("keyPaste"))
            TryPasteFromModClipboard();
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

    /// <summary>
    /// Places from the mod's in-memory clipboard. Ctrl+V loads the OS clipboard into this buffer
    /// without re-encoding it to the OS buffer, so the blueprint stays here for repeated V presses.
    /// </summary>
    internal static void TryPasteFromModClipboard()
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
        if (!TryReadBlueprintJsonFromSystemClipboard(out var json))
            return false;

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
        if (!StoreBlueprintModOnly(blueprint))
            return false;

        _instance?.Log($"Loaded from system clipboard: {_clipboardLabel} (press V to place again)");
        return true;
    }

    /// <summary>
    /// Keeps a blueprint in the mod clipboard only. Does not write <see cref="GUIUtility.systemCopyBuffer"/>,
    /// so importing with Ctrl+V does not replace the user's OS clipboard and repeats reliably with V.
    /// </summary>
    static bool StoreBlueprintModOnly(Blueprint? blueprint)
    {
        if (blueprint == null || blueprint.Elements == null || blueprint.Elements.Count == 0)
            return false;

        _clipboard = blueprint.GetCopy();
        _clipboardLabel = DescribeBlueprint(_clipboard);
        return true;
    }

    static bool TryWriteClipboardString(Blueprint blueprint)
    {
        try
        {
            var dto = new BlueprintDto(blueprint, ClipboardBlueprintName, Color.white, -1);
            var json = SerializeBlueprintDto(dto);
            // _instance?.Log($"Clipboard JSON: {json}");
            var compressed = DeflateUtf8(json);
            GUIUtility.systemCopyBuffer = ClipboardPrefix + ClipboardFormatDefault + ":" + EncodeBase64Url(compressed);
            return true;
        }
        catch (Exception ex)
        {
            _instance?.Warn($"Failed to write system clipboard blueprint: {ex.Message}");
            return false;
        }
    }

    static bool TryReadBlueprintJsonFromSystemClipboard(out string json)
    {
        json = string.Empty;

        var clipboardText = GUIUtility.systemCopyBuffer?.Trim();
        if (string.IsNullOrWhiteSpace(clipboardText) || !TryExtractUniqueBlueprintToken(clipboardText, out var blueprintToken))
            return false;

        try
        {
            return TryDecodeClipboardJson(blueprintToken, out json);
        }
        catch (Exception ex)
        {
            _instance?.Warn($"Failed to decode system clipboard blueprint: {ex.Message}");
            return false;
        }
    }

    static bool TryDecodeClipboardJson(string clipboardText, out string json)
    {
        json = string.Empty;

        var encoded = clipboardText.Substring(ClipboardPrefix.Length);
        var parts = encoded.Split(':');
        if (parts.Length < 2)
            return false;

        var format = parts[0].Trim();
        var payload = parts[^1].Trim();
        if (string.IsNullOrWhiteSpace(format) || string.IsNullOrWhiteSpace(payload))
            return false;

        var data = Encoding.UTF8.GetBytes(payload);

        if (format.IndexOf('u') >= 0)
            data = DecodeBase64Flexible(payload);

        if (format.IndexOf('z') >= 0)
            data = Inflate(data);

        if (format.Contains("j1", StringComparison.Ordinal))
        {
            json = Encoding.UTF8.GetString(data);
            return true;
        }

        return false;
    }

    static bool TryExtractUniqueBlueprintToken(string clipboardText, out string blueprintToken)
    {
        blueprintToken = string.Empty;
        var tokens = clipboardText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var matchCount = 0;

        foreach (var token in tokens)
        {
            if (!token.StartsWith(ClipboardPrefix, StringComparison.Ordinal))
                continue;

            blueprintToken = token;
            matchCount++;
            if (matchCount > 1)
                return false;
        }

        return matchCount == 1;
    }

    static byte[] Inflate(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    static byte[] DeflateUtf8(string text)
    {
        var input = Encoding.UTF8.GetBytes(text);
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(input, 0, input.Length);
        return output.ToArray();
    }

    static string EncodeBase64Url(byte[] data)
    {
        return Convert.ToBase64String(data)
            .Replace('+', '-')
            .Replace('/', '_');
    }

    static byte[] DecodeBase64Flexible(string text)
    {
        var normalized = text
            .Replace('-', '+')
            .Replace('_', '/');

        return Convert.FromBase64String(normalized);
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
        BlueprintClipboardCopyPaste.AutoCopySelection(__instance, "duplicate");
    }
}
