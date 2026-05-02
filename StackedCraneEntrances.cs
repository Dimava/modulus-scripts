using System;
using System.Collections.Generic;
using System.Linq;
using Data.Buildings;
using Data.FactoryFloor;
using Data.FactoryFloor.Buildings;
using Data.FactoryFloor.PlacementValidators;
using Data.SaveData.PersistentSOs;
using Data.Variables;
using HarmonyLib;
using ScriptEngine;
using UnityEngine;

[ScriptEntry]
public sealed class StackedCraneEntrances : ScriptMod
{
    protected override void OnEnable()
    {
        Log("Stacked crane entrances enabled.");
    }
}

internal static class StackedCraneEntrancesState
{
    private static readonly Dictionary<CranesLibrarySO, Dictionary<Vector3Int, (BuildingCranesBehaviour behaviour, BuildingCranesBehaviour.Crane crane)>> CranesByPickup
        = new Dictionary<CranesLibrarySO, Dictionary<Vector3Int, (BuildingCranesBehaviour behaviour, BuildingCranesBehaviour.Crane crane)>>();

    public static bool TryGetCraneAtPickup(CranesLibrarySO library, Vector3Int pickupPos, out (BuildingCranesBehaviour behaviour, BuildingCranesBehaviour.Crane crane) crane)
    {
        crane = default;
        return library != null
            && EnsureLibraryState(library).TryGetValue(pickupPos, out crane);
    }

    private static Dictionary<Vector3Int, (BuildingCranesBehaviour behaviour, BuildingCranesBehaviour.Crane crane)> EnsureLibraryState(CranesLibrarySO library)
    {
        if (!CranesByPickup.TryGetValue(library, out Dictionary<Vector3Int, (BuildingCranesBehaviour behaviour, BuildingCranesBehaviour.Crane crane)> cranes))
        {
            cranes = new Dictionary<Vector3Int, (BuildingCranesBehaviour behaviour, BuildingCranesBehaviour.Crane crane)>();
            foreach ((BuildingCranesBehaviour behaviour, BuildingCranesBehaviour.Crane crane) entry in library.Cranes.Values)
            {
                cranes[entry.crane.PickupPosition] = entry;
            }

            CranesByPickup.Add(library, cranes);
        }

        return cranes;
    }

    public static bool AddCrane(BuildingCranesBehaviour behaviour, Vector3Int entrancePos, Vector3Int pickupPos)
    {
        Traverse t = Traverse.Create(behaviour);
        Dictionary<Vector3Int, Vector3Int> possiblePositions = t.Field("_possibleCranePositions").GetValue<Dictionary<Vector3Int, Vector3Int>>();
        List<BuildingCranesBehaviour.Crane> cranes = t.Field("_cranes").GetValue<List<BuildingCranesBehaviour.Crane>>();
        CranesLibrarySO library = t.Field("_cranesLibrary").GetValue<CranesLibrarySO>();

        if (!TryGetEntranceDirection(behaviour, entrancePos, out Vector3Int direction))
        {
            return false;
        }

        if (TryGetCraneAtPickup(library, pickupPos, out _))
        {
            return false;
        }

        if (!IsValidCranePosition(behaviour, pickupPos, entrancePos, direction))
        {
            return false;
        }

        FactoryLayer factoryLayer = t.Field("_factoryLayer").GetValue<FactoryLayer>();
        BuildingBehaviour buildingBehaviour = t.Field("_buildingBehaviour").GetValue<BuildingBehaviour>();
        BuildingCranesBehaviour.Crane crane = new BuildingCranesBehaviour.Crane
        {
            Position = entrancePos,
            PickupPosition = pickupPos,
            Direction = direction,
            Behaviour = new BuildingCraneBehaviour(entrancePos, pickupPos, factoryLayer, buildingBehaviour, behaviour)
        };

        cranes.Add(crane);
        RegisterCrane(library, behaviour, crane);
        possiblePositions[entrancePos] = direction;

        Action<BuildingCranesBehaviour.Crane> added = t.Field("OnCraneAddedEvent").GetValue<Action<BuildingCranesBehaviour.Crane>>();
        added?.Invoke(crane);
        InvokeEntrancesChanged(t);
        return true;
    }

    public static bool RemoveCrane(BuildingCranesBehaviour behaviour, Vector3Int entrancePos, Vector3Int pickupPos)
    {
        Traverse t = Traverse.Create(behaviour);
        List<BuildingCranesBehaviour.Crane> cranes = t.Field("_cranes").GetValue<List<BuildingCranesBehaviour.Crane>>();
        for (int i = 0; i < cranes.Count; i++)
        {
            BuildingCranesBehaviour.Crane crane = cranes[i];
            if (crane.Position != entrancePos || crane.PickupPosition != pickupPos)
            {
                continue;
            }

            CranesLibrarySO library = t.Field("_cranesLibrary").GetValue<CranesLibrarySO>();
            UnregisterCrane(library, pickupPos);
            cranes.RemoveAt(i);

            Dictionary<Vector3Int, Vector3Int> possiblePositions = t.Field("_possibleCranePositions").GetValue<Dictionary<Vector3Int, Vector3Int>>();
            possiblePositions[crane.Position] = crane.Direction;

            Action<BuildingCranesBehaviour.Crane> removed = t.Field("OnCraneRemovedEvent").GetValue<Action<BuildingCranesBehaviour.Crane>>();
            removed?.Invoke(crane);
            InvokeEntrancesChanged(t);
            return true;
        }

        return false;
    }

    public static void ReaddUsedEntrances(BuildingCranesBehaviour behaviour)
    {
        Traverse t = Traverse.Create(behaviour);
        Dictionary<Vector3Int, Vector3Int> possiblePositions = t.Field("_possibleCranePositions").GetValue<Dictionary<Vector3Int, Vector3Int>>();
        foreach (BuildingCranesBehaviour.Crane crane in behaviour.Cranes)
        {
            possiblePositions[crane.Position] = crane.Direction;
        }

        InvokeEntrancesChanged(t);
    }

    public static bool IsValidCranePosition(BuildingCranesBehaviour behaviour, Vector3Int pickupPos, Vector3Int entrancePos)
    {
        if (!TryGetEntranceDirection(behaviour, entrancePos, out Vector3Int direction))
        {
            return false;
        }

        return IsValidCranePosition(behaviour, pickupPos, entrancePos, direction);
    }

    private static bool TryGetEntranceDirection(BuildingCranesBehaviour behaviour, Vector3Int entrancePos, out Vector3Int direction)
    {
        Dictionary<Vector3Int, Vector3Int> possiblePositions = Traverse.Create(behaviour)
            .Field("_possibleCranePositions")
            .GetValue<Dictionary<Vector3Int, Vector3Int>>();

        if (possiblePositions != null && possiblePositions.TryGetValue(entrancePos, out direction))
        {
            return true;
        }

        foreach (BuildingCranesBehaviour.Crane crane in behaviour.Cranes)
        {
            if (crane.Position == entrancePos)
            {
                direction = crane.Direction;
                return true;
            }
        }

        direction = Vector3Int.zero;
        return false;
    }

    private static bool IsValidCranePosition(BuildingCranesBehaviour behaviour, Vector3Int pickupPos, Vector3Int entrancePos, Vector3Int direction)
    {
        Traverse t = Traverse.Create(behaviour);
        List<Vector3Int> forcedPickupPositions = t.Field("_possibleCranePickupPositions").GetValue<List<Vector3Int>>();
        if (forcedPickupPositions != null && forcedPickupPositions.Count > 0 && !forcedPickupPositions.Contains(pickupPos))
        {
            return false;
        }

        Vector3Int delta = pickupPos - entrancePos;
        int distance = Math.Abs(delta.x) + Math.Abs(delta.z);
        if (distance <= 0 || delta.y != 0 || entrancePos + direction * distance != pickupPos)
        {
            return false;
        }

        FactoryLayer factoryLayer = t.Field("_factoryLayer").GetValue<FactoryLayer>();
        FactoryLayer terrainLayer = t.Field("_terrainLayer").GetValue<FactoryLayer>();
        List<FactoryObjectPlacementValidator> validators = t.Field("_cranePlacementValidators").GetValue<List<FactoryObjectPlacementValidator>>();
        foreach (FactoryObjectPlacementValidator validator in validators)
        {
            if (validator == null || validator is CantBePlacedOnTopOfCranes)
            {
                continue;
            }

            if (!validator.IsValidPosition(null, entrancePos, pickupPos, 0, factoryLayer, terrainLayer, 0))
            {
                return false;
            }
        }

        CanOverrideLowObjects canOverrideLowObjects = t.Field("_canOverrideLowObjectsValidator").GetValue<CanOverrideLowObjects>();
        CantBePlacedOnTopOfCranes cantBePlacedOnTopOfCranes = t.Field("_cantBePlacedOnTopOfCranesValidator").GetValue<CantBePlacedOnTopOfCranes>();
        for (int i = 1; i < distance; i++)
        {
            Vector3Int position = entrancePos + direction * i;
            if (i < distance - 1 && canOverrideLowObjects != null && !canOverrideLowObjects.IsValidPosition(null, Vector3Int.zero, position, 0, factoryLayer, terrainLayer, 0))
            {
                return false;
            }

            if (!IsAllowedSharedCraneTile(behaviour, entrancePos, direction, position)
                && cantBePlacedOnTopOfCranes != null
                && !cantBePlacedOnTopOfCranes.IsValidPosition(null, Vector3Int.zero, position, 0, factoryLayer, terrainLayer, 0))
            {
                return false;
            }
        }

        if (!IsAllowedSharedCraneTile(behaviour, entrancePos, direction, pickupPos)
            && cantBePlacedOnTopOfCranes != null
            && !cantBePlacedOnTopOfCranes.IsValidPosition(null, Vector3Int.zero, pickupPos, 0, factoryLayer, terrainLayer, 0))
        {
            return false;
        }

        return true;
    }

    private static bool IsAllowedSharedCraneTile(BuildingCranesBehaviour behaviour, Vector3Int entrancePos, Vector3Int direction, Vector3Int position)
    {
        foreach (BuildingCranesBehaviour.Crane crane in behaviour.Cranes)
        {
            if (crane.Position != entrancePos || crane.Direction != direction)
            {
                continue;
            }

            int existingDistance = Math.Abs(crane.PickupPosition.x - crane.Position.x) + Math.Abs(crane.PickupPosition.z - crane.Position.z);
            for (int i = 1; i <= existingDistance; i++)
            {
                if (crane.Position + crane.Direction * i == position)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void RegisterCrane(CranesLibrarySO library, BuildingCranesBehaviour behaviour, BuildingCranesBehaviour.Crane crane)
    {
        if (library == null)
        {
            return;
        }

        Dictionary<Vector3Int, (BuildingCranesBehaviour behaviour, BuildingCranesBehaviour.Crane crane)> cranes = EnsureLibraryState(library);
        cranes[crane.PickupPosition] = (behaviour, crane);
        RebuildLibrary(library, cranes.Values.ToList());
    }

    private static void UnregisterCrane(CranesLibrarySO library, Vector3Int pickupPos)
    {
        if (library == null)
        {
            return;
        }

        Dictionary<Vector3Int, (BuildingCranesBehaviour behaviour, BuildingCranesBehaviour.Crane crane)> cranes = EnsureLibraryState(library);
        cranes.Remove(pickupPos);
        RebuildLibrary(library, cranes.Values.ToList());
    }

    private static void RebuildLibrary(CranesLibrarySO library, List<(BuildingCranesBehaviour behaviour, BuildingCranesBehaviour.Crane crane)> cranes)
    {
        Dictionary<Vector3Int, (BuildingCranesBehaviour behaviour, BuildingCranesBehaviour.Crane crane)> libraryCranes = library.Cranes;
        HashSet<Vector3Int> rails = library.Rails;
        libraryCranes.Clear();
        rails.Clear();

        foreach ((BuildingCranesBehaviour behaviour, BuildingCranesBehaviour.Crane crane) entry in cranes)
        {
            BuildingCranesBehaviour.Crane crane = entry.crane;
            int distance = Math.Abs(crane.PickupPosition.x - crane.Position.x) + Math.Abs(crane.PickupPosition.z - crane.Position.z);
            for (int i = 1; i <= distance; i++)
            {
                Vector3Int position = crane.Position + crane.Direction * i;
                if (i < distance)
                {
                    rails.Add(position);
                }

                if (!libraryCranes.ContainsKey(position))
                {
                    libraryCranes.Add(position, entry);
                }
            }
        }
    }

    private static void InvokeEntrancesChanged(Traverse t)
    {
        Action changed = t.Field("OnCraneEntrancesChangedEvent").GetValue<Action>();
        changed?.Invoke();
    }
}

[HarmonyPatch(typeof(BuildingCranesBehaviour), nameof(BuildingCranesBehaviour.AddCrane))]
internal static class BuildingCranesBehaviour_AddCrane_StackedEntrances_Patch
{
    static bool Prefix(BuildingCranesBehaviour __instance, Vector3Int entrancePos, Vector3Int pickupPos, ref bool __result)
    {
        __result = StackedCraneEntrancesState.AddCrane(__instance, entrancePos, pickupPos);
        return false;
    }
}

[HarmonyPatch(typeof(BuildingCranesBehaviour), nameof(BuildingCranesBehaviour.IsValidCranePosition))]
internal static class BuildingCranesBehaviour_IsValidCranePosition_StackedEntrances_Patch
{
    static bool Prefix(BuildingCranesBehaviour __instance, Vector3Int cranePickupPos, Vector3Int craneEntrancePos, ref bool __result)
    {
        __result = StackedCraneEntrancesState.IsValidCranePosition(__instance, cranePickupPos, craneEntrancePos);
        return false;
    }
}

[HarmonyPatch(typeof(BuildingCranesBehaviour), nameof(BuildingCranesBehaviour.CalculatePossibleCranePositions))]
internal static class BuildingCranesBehaviour_CalculatePossibleCranePositions_StackedEntrances_Patch
{
    static void Postfix(BuildingCranesBehaviour __instance)
    {
        StackedCraneEntrancesState.ReaddUsedEntrances(__instance);
    }
}

[HarmonyPatch(typeof(Logic.FactoryTools.PlaceCraneFromBuildingTool), nameof(Logic.FactoryTools.PlaceCraneFromBuildingTool.IsCranePlacementValid))]
internal static class PlaceCraneFromBuildingTool_IsCranePlacementValid_StackedEntrances_Patch
{
    static bool Prefix(Logic.FactoryTools.PlaceCraneFromBuildingTool __instance, Vector3Int pickupPos, Vector3Int entrancePos, ref bool __result)
    {
        BuildingCranesBehaviour behaviour = Traverse.Create(__instance)
            .Field("_currentBuildingCranesBehaviour")
            .GetValue<BuildingCranesBehaviour>();
        if (behaviour == null)
        {
            return true;
        }

        __result = StackedCraneEntrancesState.IsValidCranePosition(behaviour, pickupPos, entrancePos);
        return false;
    }
}

[HarmonyPatch(typeof(PlaceCraneFromBuildingCommand), "TryDelete")]
internal static class PlaceCraneFromBuildingCommand_TryDelete_StackedEntrances_Patch
{
    static bool Prefix(PlaceCraneFromBuildingCommand __instance, ref bool __result)
    {
        Traverse t = Traverse.Create(__instance);
        BuildingCranesBehaviour behaviour = t.Field("_buildingCranesBehaviour").GetValue<BuildingCranesBehaviour>();
        Vector3Int pickupPos = t.Field("_position").GetValue<Vector3Int>();
        Vector3Int entrancePos = t.Field("_entrancePosition").GetValue<Vector3Int>();
        __result = StackedCraneEntrancesState.RemoveCrane(behaviour, entrancePos, pickupPos);
        if (__result)
        {
            Presentation.Locators.AudioManagerLocator audioManagerLocator = t.Field("_audioManagerLocator").GetValue<Presentation.Locators.AudioManagerLocator>();
            audioManagerLocator.AudioManager.PlayDeleteObject(pickupPos);
        }

        return false;
    }
}

[HarmonyPatch(typeof(CranesLibrarySO), nameof(CranesLibrarySO.TryGetCrane))]
internal static class CranesLibrarySO_TryGetCrane_StackedEntrances_Patch
{
    static bool Prefix(CranesLibrarySO __instance, Vector3Int position, ref (BuildingCranesBehaviour behaviour, BuildingCranesBehaviour.Crane crane) crane, ref bool __result)
    {
        if (!StackedCraneEntrancesState.TryGetCraneAtPickup(__instance, position, out crane))
        {
            return true;
        }

        __result = true;
        return false;
    }
}
