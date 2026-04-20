using System;
using System.Collections.Generic;
using Data.FactoryFloor;
using Data.FactoryFloor.Behaviours;
using Data.FactoryFloor.Resources;
using Data.Operator;
using HarmonyLib;
using Presentation.FactoryFloor.FactoryObjectViews.Arrows;
using ScriptEngine;
using UnityEngine;

[ScriptEntry]
public sealed class ExoportMoreInputPorts : ScriptMod
{
    private static ExoportMoreInputPorts? _instance;

    protected override void OnEnable()
    {
        _instance = this;

        int expanded = ExpandLoadedExoportDatas();
        int relinked = RelinkExistingExoports();

        Log($"expanded exoport belt ports on {expanded} data asset(s), relinked {relinked} placed exoport(s).");
    }

    protected override void OnDisable()
    {
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }
    }

    public static void EnsureExoportPorts(FactoryObjectData factoryObjectData)
    {
        if (factoryObjectData == null || factoryObjectData.GetFactoryObjectBehaviour<ExoportBehaviour>() == null)
        {
            return;
        }

        EnsureExpandedSideInputs(factoryObjectData);
    }

    private static int ExpandLoadedExoportDatas()
    {
        int changed = 0;
        foreach (FactoryObjectData factoryObjectData in GetExoportDatas())
        {
            if (EnsureExpandedSideInputs(factoryObjectData))
            {
                changed++;
            }
        }
        return changed;
    }

    private static List<FactoryObjectData> GetExoportDatas()
    {
        List<FactoryObjectData> result = new List<FactoryObjectData>();
        foreach (FactoryObjectData factoryObjectData in Resources.FindObjectsOfTypeAll<FactoryObjectData>())
        {
            if (factoryObjectData != null && factoryObjectData.GetFactoryObjectBehaviour<ExoportBehaviour>() != null)
            {
                result.Add(factoryObjectData);
            }
        }
        return result;
    }

    private static bool EnsureExpandedSideInputs(FactoryObjectData factoryObjectData)
    {
        List<Vector3Int> relativePositions = factoryObjectData.RelativePositions;
        if (relativePositions == null || relativePositions.Count == 0)
        {
            return false;
        }

        List<FactoryObjectData.InputData> inputPositions = factoryObjectData.InputPositionsData;
        if (inputPositions == null)
        {
            return false;
        }

        HashSet<Vector3Int> occupied = new HashSet<Vector3Int>(relativePositions);
        HashSet<string> existingPorts = new HashSet<string>(StringComparer.Ordinal);
        HashSet<Vector3Int> existingDirections = new HashSet<Vector3Int>();

        for (int i = 0; i < inputPositions.Count; i++)
        {
            FactoryObjectData.InputData inputData = inputPositions[i];
            existingPorts.Add(GetPortKey(inputData.Position, inputData.Direction));
            existingDirections.Add(inputData.Direction);
        }

        if (existingDirections.Count == 0)
        {
            return false;
        }

        List<ResourceDataSO> allowedTypesTemplate = GetAllowedTypesTemplate(inputPositions);
        bool changed = false;

        foreach (Vector3Int direction in existingDirections)
        {
            if (direction == Vector3Int.zero)
            {
                continue;
            }

            Vector3Int outsideOffset = -direction;
            foreach (Vector3Int cell in occupied)
            {
                if (occupied.Contains(cell + outsideOffset))
                {
                    continue;
                }

                string key = GetPortKey(cell, direction);
                if (!existingPorts.Add(key))
                {
                    continue;
                }

                inputPositions.Add(new FactoryObjectData.InputData
                {
                    Position = cell,
                    Direction = direction,
                    AllowedResourceTypes = new List<ResourceDataSO>(allowedTypesTemplate)
                });
                changed = true;
            }
        }

        if (changed)
        {
            factoryObjectData.UpdateIndex();
        }

        return changed;
    }

    private static List<ResourceDataSO> GetAllowedTypesTemplate(List<FactoryObjectData.InputData> inputPositions)
    {
        for (int i = 0; i < inputPositions.Count; i++)
        {
            List<ResourceDataSO> allowedTypes = inputPositions[i].AllowedResourceTypes;
            if (allowedTypes != null && allowedTypes.Count > 0)
            {
                return new List<ResourceDataSO>(allowedTypes);
            }
        }

        return new List<ResourceDataSO>();
    }

    private static string GetPortKey(Vector3Int position, Vector3Int direction)
    {
        return position.x + "," + position.y + "," + position.z + "|" + direction.x + "," + direction.y + "," + direction.z;
    }

    private static int RelinkExistingExoports()
    {
        int relinked = 0;
        List<FactoryObjectData> exoportDatas = GetExoportDatas();

        foreach (FactoryLayer factoryLayer in Resources.FindObjectsOfTypeAll<FactoryLayer>())
        {
            if (factoryLayer == null)
            {
                continue;
            }

            for (int dataIndex = 0; dataIndex < exoportDatas.Count; dataIndex++)
            {
                FactoryObjectData factoryObjectData = exoportDatas[dataIndex];
                if (!factoryLayer.TryGetObjectsFromData(factoryObjectData, out List<FactoryObject> factoryObjects) || factoryObjects == null)
                {
                    continue;
                }

                for (int i = 0; i < factoryObjects.Count; i++)
                {
                    FactoryObject factoryObject = factoryObjects[i];
                    if (factoryObject == null)
                    {
                        continue;
                    }

                    try
                    {
                        Traverse.Create(factoryObject).Method("InitInputObjectsList").GetValue();
                        relinked++;
                    }
                    catch (Exception ex)
                    {
                        _instance?.Warn($"failed to relink exoport at {factoryObject.Position}: {ex.Message}");
                    }
                }
            }
        }

        return relinked;
    }
}

[HarmonyPatch(typeof(FactoryObject), nameof(FactoryObject.Initialize))]
static class ExoportMoreInputPorts_FactoryObjectInitialize_Patch
{
    static void Prefix(FactoryObject __instance)
    {
        ExoportMoreInputPorts.EnsureExoportPorts(__instance.FactoryObjectData);
    }
}

[HarmonyPatch(typeof(FactoryObjectInputOutputArrows), nameof(FactoryObjectInputOutputArrows.ShowEmptyInputs))]
static class ExoportMoreInputPorts_ShowEmptyInputs_Patch
{
    static void Prefix(FactoryObjectInputOutputArrows __instance)
    {
        ExoportMoreInputPortsArrowHelper.EnsureInputArrows(__instance);
    }
}

[HarmonyPatch(typeof(FactoryObjectInputOutputArrows), nameof(FactoryObjectInputOutputArrows.ShowAll))]
static class ExoportMoreInputPorts_ShowAll_Patch
{
    static void Prefix(FactoryObjectInputOutputArrows __instance)
    {
        ExoportMoreInputPortsArrowHelper.EnsureInputArrows(__instance);
    }
}

static class ExoportMoreInputPortsArrowHelper
{
    public static void EnsureInputArrows(FactoryObjectInputOutputArrows arrowsComponent)
    {
        if (arrowsComponent == null)
        {
            return;
        }

        FactoryObjectInputOutputArrows traverseTarget = arrowsComponent;
        Traverse traverse = Traverse.Create(traverseTarget);
        var objectView = traverse.Field("_objectView").GetValue<Presentation.FactoryFloor.FactoryObjectView>();
        if (objectView?.FactoryObject == null || objectView.FactoryObject.FactoryObjectData.GetFactoryObjectBehaviour<ExoportBehaviour>() == null)
        {
            return;
        }

        List<InputOutputArrow> arrows = traverse.Field("_arrows").GetValue<List<InputOutputArrow>>();
        InputOutputArrow inputPrefab = traverse.Field("_inputArrowPrefab").GetValue<InputOutputArrow>();
        Vector3 offset = traverse.Field("_offset").GetValue<Vector3>();

        if (arrows == null || inputPrefab == null)
        {
            return;
        }

        HashSet<string> existingArrowKeys = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < arrows.Count; i++)
        {
            InputOutputArrow arrow = arrows[i];
            if (arrow == null || arrow.ArrowType != InputOutputArrow.EArrowType.Input)
            {
                continue;
            }

            existingArrowKeys.Add(GetPortKey(arrow.RelativePosition, GetArrowDirection(arrow, objectView.FactoryObject)));
        }

        List<FactoryObjectData.InputData> dataInputs = objectView.FactoryObject.DataInputPositions;
        for (int i = 0; i < dataInputs.Count; i++)
        {
            FactoryObjectData.InputData inputData = dataInputs[i];
            string key = GetPortKey(inputData.Position, inputData.Direction);
            if (!existingArrowKeys.Add(key))
            {
                continue;
            }

            InputOutputArrow arrow = UnityEngine.Object.Instantiate(inputPrefab, arrowsComponent.transform);
            arrow.SetArrow(InputOutputArrow.EArrowType.Input, inputData.Position);
            arrow.transform.localPosition = (Vector3)inputData.Position - (Vector3)inputData.Direction * 0.5f + offset;
            arrow.transform.localRotation = Quaternion.Euler(0f, DirectionToYaw(inputData.Direction), 0f);
            arrows.Add(arrow);
        }
    }

    private static Vector3Int GetArrowDirection(InputOutputArrow arrow, FactoryObject factoryObject)
    {
        List<FactoryObjectData.InputData> dataInputs = factoryObject.DataInputPositions;
        for (int i = 0; i < dataInputs.Count; i++)
        {
            if (dataInputs[i].Position == arrow.RelativePosition)
            {
                return dataInputs[i].Direction;
            }
        }

        return Vector3Int.zero;
    }

    private static float DirectionToYaw(Vector3Int direction)
    {
        if (direction == Vector3Int.right) return 270f;
        if (direction == Vector3Int.left) return 90f;
        if (direction == Vector3Int.forward) return 180f;
        if (direction == Vector3Int.back) return 0f;
        return 0f;
    }

    private static string GetPortKey(Vector3Int position, Vector3Int direction)
    {
        return position.x + "," + position.y + "," + position.z + "|" + direction.x + "," + direction.y + "," + direction.z;
    }
}
