using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Data.Operator;
using HarmonyLib;
using Presentation.FactoryFloor.Toolbar;
using Presentation.UI.Toolbar;
using ScriptEngine;
using UnityEngine;
using UnityEngine.InputSystem;

[ScriptEntry]
public sealed class ToolbarGroupLogger : ScriptMod
{
    private sealed class ButtonRecord
    {
        public string Label = "";
        public string ButtonType = "";
        public string BreadcrumbId = "";
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

    private static ToolbarGroupLogger? _instance;
    private static readonly Dictionary<int, int> CategoryCounters = new Dictionary<int, int>();
    private static readonly Dictionary<int, ButtonRecord> ButtonRecords = new Dictionary<int, ButtonRecord>();

    private static CategoryContext? _currentCategory;

    protected override void OnEnable()
    {
        _instance = this;
        CategoryCounters.Clear();
        ButtonRecords.Clear();
        _currentCategory = null;
        Log("Watching operator toolbar category builds.");
    }

    protected override void OnDisable()
    {
        CategoryCounters.Clear();
        ButtonRecords.Clear();
        _currentCategory = null;
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }
    }

    internal static void BeginBarBuild(OperatorBar bar)
    {
        if (bar == null)
        {
            return;
        }

        CategoryCounters[bar.GetInstanceID()] = 0;
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
            PartOfInputActionGroup = data != null && data.PartOfInputActionGroup,
            IsGroupStart = data != null && data.IsGroupStart
        };

        ButtonRecords[button.GetInstanceID()] = record;
        if (_currentCategory != null)
        {
            _currentCategory.Buttons.Add(record);
        }

        // Too noisy during normal play; keep end-of-category summary logs instead.
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
                BreadcrumbId = SafeReadBreadcrumbId(button)
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
    }

    internal static void EndCategory()
    {
        CategoryContext context = _currentCategory;
        _currentCategory = null;
        if (context == null)
        {
            return;
        }

        LogLine($"CATEGORY summary mode={context.BuildMode} index={context.CategoryIndex} color={FormatColor(context.CategoryColor)} buttons={context.Buttons.Count}");

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
            LogLine($"GROUP mode={context.BuildMode} category={context.CategoryIndex} action={shortcutGroup.Key} count={members.Count}{singletonTag} members={summary}");
        }

        List<ButtonRecord> noShortcut = context.Buttons
            .Where(static button => !button.HasShortcut)
            .OrderBy(static button => button.Label)
            .ToList();
        if (noShortcut.Count > 0)
        {
            LogLine($"NO_SHORTCUT mode={context.BuildMode} category={context.CategoryIndex} count={noShortcut.Count} members={string.Join(" | ", noShortcut.Select(static button => button.Label))}");
        }

        List<ButtonRecord> ungroupedByAsset = context.Buttons
            .Where(static button => !button.PartOfInputActionGroup)
            .OrderBy(static button => button.Label)
            .ToList();
        if (ungroupedByAsset.Count > 0)
        {
            LogLine($"UNGROUPED_BY_ASSET mode={context.BuildMode} category={context.CategoryIndex} count={ungroupedByAsset.Count} members={string.Join(" | ", ungroupedByAsset.Select(static button => button.Summary()))}");
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

    private static string FormatColor(Color color)
    {
        return "#" + ColorUtility.ToHtmlStringRGBA(color);
    }

    private static void LogLine(string message)
    {
        _instance?.Log(message);
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
