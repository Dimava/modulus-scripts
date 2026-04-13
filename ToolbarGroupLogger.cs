using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using Data.Operator;
using Presentation.FactoryFloor.Toolbar;
using Presentation.UI.Toolbar;
using UnityEngine;
using UnityEngine.InputSystem;

public static class ToolbarGroupLogger
{
    private sealed class ButtonRecord
    {
        public string Label = "";
        public string ButtonType = "";
        public string BreadcrumbId = "";
        public string HierarchyPath = "";
        public bool PartOfInputActionGroup;
        public bool IsGroupStart;
        public bool HasShortcut;
        public bool HasBoundInput;
        public string ShortcutAction = "";
        public int ShortcutIndex = -1;

        public string Summary()
        {
            string shortcut = HasShortcut
                ? $"shortcut={ShortcutAction}@{ShortcutIndex} bound={HasBoundInput}"
                : "shortcut=<none>";
            return $"{Label} [{ButtonType}] {shortcut} partOfGroup={PartOfInputActionGroup} groupStart={IsGroupStart}";
        }
    }

    private sealed class CategoryContext
    {
        public BuildMode BuildMode;
        public int CategoryIndex;
        public Color CategoryColor;
        public readonly List<ButtonRecord> Buttons = new List<ButtonRecord>();
    }

    private static readonly HarmonyLib.Harmony HarmonyInstance = new HarmonyLib.Harmony("toolbar-group-logger");
    private static readonly Dictionary<int, int> CategoryCounters = new Dictionary<int, int>();
    private static readonly Dictionary<int, ButtonRecord> ButtonRecords = new Dictionary<int, ButtonRecord>();
    private static readonly object LogLock = new object();

    private static CategoryContext _currentCategory;
    private static string _logPath = "";

    public static void OnLoad()
    {
        string logsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts", "logs");
        Directory.CreateDirectory(logsDir);
        _logPath = Path.Combine(logsDir, "ToolbarGroupLogger.log");

        HarmonyInstance.UnpatchSelf();
        HarmonyInstance.PatchAll(typeof(ToolbarGroupLogger).Assembly);

        Log("");
        Log("============================================================");
        Log("ToolbarGroupLogger loaded");
        Log("Watching operator toolbar category builds.");
        Log($"Log file: {_logPath}");
        MelonLogger.Msg($"[ToolbarGroupLogger] Loaded. Writing to {_logPath}");
    }

    public static void OnUnload()
    {
        HarmonyInstance.UnpatchSelf();
        CategoryCounters.Clear();
        ButtonRecords.Clear();
        _currentCategory = null;
        Log("ToolbarGroupLogger unloaded");
        MelonLogger.Msg("[ToolbarGroupLogger] Unloaded.");
    }

    internal static void BeginBarBuild(OperatorBar bar)
    {
        if (bar == null)
        {
            return;
        }

        CategoryCounters[bar.GetInstanceID()] = 0;
        Log($"BAR buildmode={bar.BuildMode} object={bar.name}");
    }

    internal static void BeginCategory(OperatorBar bar, OperatorBarCategory category)
    {
        if (bar == null)
        {
            return;
        }

        int id = bar.GetInstanceID();
        int index;
        if (!CategoryCounters.TryGetValue(id, out index))
        {
            index = 0;
        }
        index++;
        CategoryCounters[id] = index;

        _currentCategory = new CategoryContext
        {
            BuildMode = bar.BuildMode,
            CategoryIndex = index,
            CategoryColor = category.CategoryColor
        };

        Log($"CATEGORY begin mode={_currentCategory.BuildMode} index={_currentCategory.CategoryIndex} color={FormatColor(_currentCategory.CategoryColor)}");
    }

    internal static void RegisterButton(ToolBarButton button, OperatorBarButtonSO data)
    {
        if (button == null)
        {
            return;
        }

        ButtonRecord record = new ButtonRecord
        {
            Label = DescribeToolBarButton(button),
            ButtonType = button.GetType().Name,
            BreadcrumbId = SafeReadBreadcrumbId(button),
            HierarchyPath = BuildHierarchyPath(button.transform),
            PartOfInputActionGroup = data != null && data.PartOfInputActionGroup,
            IsGroupStart = data != null && data.IsGroupStart
        };

        ButtonRecords[button.GetInstanceID()] = record;
        if (_currentCategory != null)
        {
            _currentCategory.Buttons.Add(record);
        }

        Log($"BUTTON mode={(_currentCategory != null ? _currentCategory.BuildMode.ToString() : "?")} category={(_currentCategory != null ? _currentCategory.CategoryIndex.ToString() : "?")} label={record.Label} breadcrumb={record.BreadcrumbId} grouped={record.PartOfInputActionGroup} groupStart={record.IsGroupStart} path={record.HierarchyPath}");
    }

    internal static void RegisterShortcut(ToolBarButtonShortcut shortcut)
    {
        if (shortcut == null)
        {
            return;
        }

        ToolBarButton button = Traverse.Create(shortcut).Field("_toolBarButton").GetValue<ToolBarButton>();
        if (button == null)
        {
            return;
        }

        ButtonRecord record;
        if (!ButtonRecords.TryGetValue(button.GetInstanceID(), out record))
        {
            record = new ButtonRecord
            {
                Label = DescribeToolBarButton(button),
                ButtonType = button.GetType().Name,
                BreadcrumbId = SafeReadBreadcrumbId(button),
                HierarchyPath = BuildHierarchyPath(button.transform)
            };
            ButtonRecords[button.GetInstanceID()] = record;
            if (_currentCategory != null)
            {
                _currentCategory.Buttons.Add(record);
            }
        }

        InputActionReference inputAction = Traverse.Create(shortcut).Field("_groupInputAction").GetValue<InputActionReference>();
        int groupIndex = Traverse.Create(shortcut).Field("_groupIndex").GetValue<int>();
        bool hasBoundInput = Traverse.Create(shortcut).Field("_hasBoundInput").GetValue<bool>();

        record.HasShortcut = inputAction != null;
        record.HasBoundInput = hasBoundInput;
        record.ShortcutAction = DescribeInputAction(inputAction);
        record.ShortcutIndex = groupIndex;

        Log($"SHORTCUT label={record.Label} action={record.ShortcutAction} index={record.ShortcutIndex} bound={record.HasBoundInput}");
    }

    internal static void EndCategory()
    {
        CategoryContext context = _currentCategory;
        _currentCategory = null;
        if (context == null)
        {
            return;
        }

        Log($"CATEGORY summary mode={context.BuildMode} index={context.CategoryIndex} color={FormatColor(context.CategoryColor)} buttons={context.Buttons.Count}");

        List<ButtonRecord> groupedButtons = context.Buttons
            .Where(static button => button.HasShortcut && !string.IsNullOrEmpty(button.ShortcutAction))
            .ToList();

        foreach (IGrouping<string, ButtonRecord> shortcutGroup in groupedButtons
            .GroupBy(static button => button.ShortcutAction)
            .OrderBy(static group => group.Key))
        {
            List<ButtonRecord> members = shortcutGroup
                .OrderBy(static button => button.ShortcutIndex)
                .ThenBy(static button => button.Label)
                .ToList();

            string summary = string.Join(" | ", members.Select(static button => $"{button.ShortcutIndex}:{button.Label}"));
            string singletonTag = members.Count == 1 ? " singleton=true" : "";
            Log($"GROUP mode={context.BuildMode} category={context.CategoryIndex} action={shortcutGroup.Key} count={members.Count}{singletonTag} members={summary}");
        }

        List<ButtonRecord> noShortcut = context.Buttons
            .Where(static button => !button.HasShortcut)
            .OrderBy(static button => button.Label)
            .ToList();
        if (noShortcut.Count > 0)
        {
            Log($"NO_SHORTCUT mode={context.BuildMode} category={context.CategoryIndex} count={noShortcut.Count} members={string.Join(" | ", noShortcut.Select(static button => button.Label))}");
        }

        List<ButtonRecord> ungroupedByAsset = context.Buttons
            .Where(static button => !button.PartOfInputActionGroup)
            .OrderBy(static button => button.Label)
            .ToList();
        if (ungroupedByAsset.Count > 0)
        {
            Log($"UNGROUPED_BY_ASSET mode={context.BuildMode} category={context.CategoryIndex} count={ungroupedByAsset.Count} members={string.Join(" | ", ungroupedByAsset.Select(static button => button.Summary()))}");
        }
    }

    private static string DescribeToolBarButton(ToolBarButton button)
    {
        if (button == null)
        {
            return "<null>";
        }

        if (button is SelectObjectToPlaceButton)
        {
            FactoryObjectData factoryObjectData = Traverse.Create(button).Field("_factoryObjectData").GetValue<FactoryObjectData>();
            if (factoryObjectData != null)
            {
                string nameLocKey = factoryObjectData.NameLocKey ?? "";
                return $"place:{factoryObjectData.name}#{factoryObjectData.ID} loca={nameLocKey}";
            }
        }

        if (button is SelectToolButton)
        {
            object factoryTool = Traverse.Create(button).Field("_factoryTool").GetValue();
            if (factoryTool != null)
            {
                string breadcrumb = ReadStringProperty(factoryTool, "BreadcrumbId");
                return $"tool:{factoryTool.GetType().Name} breadcrumb={breadcrumb}";
            }
        }

        if (button is SetIntToPlaceButton)
        {
            int id = Traverse.Create(button).Field("_id").GetValue<int>();
            return $"set-int:{id}";
        }

        return $"{button.GetType().Name} breadcrumb={SafeReadBreadcrumbId(button)}";
    }

    private static string SafeReadBreadcrumbId(ToolBarButton button)
    {
        try
        {
            return button.BreadcrumbId ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string ReadStringProperty(object instance, string propertyName)
    {
        if (instance == null)
        {
            return "";
        }

        PropertyInfo property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        object value = property != null ? property.GetValue(instance, null) : null;
        return value != null ? value.ToString() : "";
    }

    private static string DescribeInputAction(InputActionReference inputAction)
    {
        if (inputAction == null)
        {
            return "<none>";
        }

        if (inputAction.action != null && !string.IsNullOrEmpty(inputAction.action.name))
        {
            return inputAction.action.name;
        }

        return inputAction.name ?? "<unnamed>";
    }

    private static string BuildHierarchyPath(Transform transform)
    {
        if (transform == null)
        {
            return "";
        }

        List<string> parts = new List<string>();
        Transform current = transform;
        while (current != null)
        {
            parts.Add(current.name);
            current = current.parent;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static string FormatColor(Color color)
    {
        return "#" + ColorUtility.ToHtmlStringRGBA(color);
    }

    private static void Log(string message)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        lock (LogLock)
        {
            File.AppendAllText(_logPath, line + Environment.NewLine);
        }
        MelonLogger.Msg($"[ToolbarGroupLogger] {message}");
    }
}

[HarmonyPatch(typeof(OperatorBar), "BuildOperatorBar")]
static class ToolbarGroupLogger_OperatorBar_BuildOperatorBar_Patch
{
    static void Prefix(OperatorBar __instance)
    {
        ToolbarGroupLogger.BeginBarBuild(__instance);
    }
}

[HarmonyPatch(typeof(OperatorBar), "BuildCategory")]
static class ToolbarGroupLogger_OperatorBar_BuildCategory_Patch
{
    static void Prefix(OperatorBar __instance, OperatorBarCategory operatorButtons)
    {
        ToolbarGroupLogger.BeginCategory(__instance, operatorButtons);
    }

    static void Postfix()
    {
        ToolbarGroupLogger.EndCategory();
    }
}

[HarmonyPatch(typeof(ToolBarButton), nameof(ToolBarButton.Init))]
static class ToolbarGroupLogger_ToolBarButton_Init_Patch
{
    static void Postfix(ToolBarButton __instance, OperatorBarButtonSO data, BuildMode buildMode)
    {
        ToolbarGroupLogger.RegisterButton(__instance, data);
    }
}

[HarmonyPatch(typeof(ToolBarButtonShortcut), nameof(ToolBarButtonShortcut.Init))]
static class ToolbarGroupLogger_ToolBarButtonShortcut_Init_Patch
{
    static void Postfix(ToolBarButtonShortcut __instance)
    {
        ToolbarGroupLogger.RegisterShortcut(__instance);
    }
}
