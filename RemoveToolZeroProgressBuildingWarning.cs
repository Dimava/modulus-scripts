using System.Reflection;
using Data.Buildings;
using Data.FactoryFloor;
using Data.FactoryFloor.Buildings;
using Data.Variables;
using HarmonyLib;
using Logic.Factory;
using Logic.Factory.Blueprint;
using Logic.FactoryTools;
using ScriptEngine;

/// <summary>
/// Suppresses the remove-tool demolish confirmation for buildings that have
/// just been placed and still have no initial construction progress.
/// </summary>
[ScriptEntry]
public sealed class RemoveToolZeroProgressBuildingWarning : ScriptMod { }

static class RemoveToolZeroProgressBuildingWarningShared
{
    private static readonly MethodInfo DeleteSelectionInternalMethod =
        AccessTools.Method(typeof(DeleteTool), "DeleteSelectionInternal");

    private static readonly MethodInfo SelectFactoryObjectMethod =
        AccessTools.Method(typeof(SelectionFactoryTool), "SelectFactoryObject");

    internal static bool ShouldWarnForBuilding(FactoryObject factoryObject)
    {
        if (factoryObject == null)
        {
            return true;
        }

        if (!factoryObject.TryGetFactoryObjectBehaviour<BuildingBehaviour>(out var behaviour))
        {
            return true;
        }

        return behaviour.CurrentBuildingStage != 0 || behaviour.CurrentProgress > 0f;
    }

    internal static bool TryGetSelectedObject(DeleteTool tool, Blueprint selection, BlueprintElement element, out FactoryObject factoryObject)
    {
        factoryObject = null;
        if (tool == null || selection == null || element == null || element.RelativePositions == null || element.RelativePositions.Count == 0)
        {
            return false;
        }

        var currentLayer = Traverse.Create(tool).Field("_factoryLayer").GetValue<CurrentFactoryLayer>();
        var layer = currentLayer?.Value;
        if (layer == null)
        {
            return false;
        }

        var position = selection.Position + element.RelativePositions[0];
        return layer.TryGetObjectAt(position, out factoryObject);
    }

    internal static void SelectFactoryObject(DeleteTool tool, FactoryObject factoryObject)
    {
        SelectFactoryObjectMethod?.Invoke(tool, new object[] { factoryObject });
    }

    internal static void DeleteSelectionInternal(DeleteTool tool, bool deleteCranes)
    {
        DeleteSelectionInternalMethod?.Invoke(tool, new object[] { deleteCranes });
    }
}

[HarmonyPatch(typeof(DeleteTool), "DeleteSelection")]
static class RemoveToolZeroProgressBuildingWarning_DeleteSelection_Patch
{
    static bool Prefix(DeleteTool __instance, bool deleteCranes)
    {
        var selection = Traverse.Create(__instance).Field("_selection").GetValue<Blueprint>();
        if (selection?.Elements == null)
        {
            return true;
        }

        var hasBuilding = false;
        foreach (var element in selection.Elements)
        {
            if (!(element.ObjectData is BuildingObjectData))
            {
                continue;
            }

            hasBuilding = true;
            if (!RemoveToolZeroProgressBuildingWarningShared.TryGetSelectedObject(__instance, selection, element, out var factoryObject)
                || RemoveToolZeroProgressBuildingWarningShared.ShouldWarnForBuilding(factoryObject))
            {
                return true;
            }
        }

        if (!hasBuilding)
        {
            return true;
        }

        RemoveToolZeroProgressBuildingWarningShared.DeleteSelectionInternal(__instance, deleteCranes);
        return false;
    }
}

[HarmonyPatch(typeof(DeleteTool), "DeleteSingleObjectInternal", typeof(FactoryObject))]
static class RemoveToolZeroProgressBuildingWarning_DeleteSingleObjectInternal_Patch
{
    static bool Prefix(DeleteTool __instance, FactoryObject factoryObject)
    {
        if (!(factoryObject?.FactoryObjectData is BuildingObjectData)
            || RemoveToolZeroProgressBuildingWarningShared.ShouldWarnForBuilding(factoryObject))
        {
            return true;
        }

        RemoveToolZeroProgressBuildingWarningShared.SelectFactoryObject(__instance, factoryObject);
        RemoveToolZeroProgressBuildingWarningShared.DeleteSelectionInternal(__instance, deleteCranes: false);
        return false;
    }
}
