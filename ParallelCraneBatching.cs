using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Data.Buildings;
using Data.FactoryFloor.Behaviours;
using Data.FactoryFloor.Buildings;
using Data.FactoryFloor.Resources;
using Data.Variables;
using HarmonyLib;
using ScriptEngine;

[ScriptEntry]
public sealed class ParallelCraneBatching : ScriptMod
{
    protected override void OnEnable()
    {
        Log("Parallel crane batching enabled.");
    }
}

[HarmonyPatch(typeof(BuildingCranesBehaviour), nameof(BuildingCranesBehaviour.Process))]
static class BuildingCranesBehaviour_Process_ParallelCraneBatching_Patch
{
    private static int _diagnosticLogCount;
    private static int _craneProcessLogCount;
    private static int _rejectLogCount;

    private sealed class CraneStep
    {
        public BuildingCraneBehaviour Behaviour;
        public ConveyorBehaviour Conveyor;
        public Resource Resource;
        public IntVariableSO UpdateFrequency;
        public int TicksWaited;
        public int DownTicksWaited;
        public bool Ready;
        public bool CanOperateAtBatchStart;
    }

    static bool Prefix(BuildingCranesBehaviour __instance, int step)
    {
        if (__instance == null || __instance.Cranes == null || __instance.Cranes.Count == 0)
        {
            return false;
        }

        List<BuildingCraneBehaviour> craneBehaviours = __instance.Cranes
            .Select(crane => crane.Behaviour)
            .Where(behaviour => behaviour != null)
            .ToList();

        List<CraneStep> steps = new List<CraneStep>(craneBehaviours.Count);
        foreach (BuildingCraneBehaviour behaviour in craneBehaviours)
        {
            if (!StillOwnedBy(__instance, behaviour))
            {
                continue;
            }

            Traverse craneTraverse = Traverse.Create(behaviour);
            craneTraverse.Method("GetConveyorBehaviour").GetValue();
            if (!StillOwnedBy(__instance, behaviour))
            {
                continue;
            }

            IntVariableSO updateFrequency = craneTraverse.Field("_updateFrequency").GetValue<IntVariableSO>();
            int ticksWaited = craneTraverse.Field("_ticksWaited").GetValue<int>();
            int downTicksWaited = craneTraverse.Field("_downTicksWaited").GetValue<int>();
            bool hasConveyor = craneTraverse.Field("_hasConveyor").GetValue<bool>();
            ConveyorBehaviour conveyor = craneTraverse.Field("_conveyorBehaviour").GetValue<ConveyorBehaviour>();
            BuildingBehaviour building = craneTraverse.Field("_buildingBehaviour").GetValue<BuildingBehaviour>();

            bool ready = updateFrequency != null && ticksWaited >= updateFrequency.Value;
            Resource resource = hasConveyor && conveyor != null && conveyor.HasResource() ? conveyor.Resource : null;

            steps.Add(new CraneStep
            {
                Behaviour = behaviour,
                Conveyor = conveyor,
                Resource = resource,
                UpdateFrequency = updateFrequency,
                TicksWaited = ticksWaited,
                DownTicksWaited = downTicksWaited,
                Ready = ready,
                CanOperateAtBatchStart = ready && resource != null && building != null
            });
        }

        BuildingBehaviour batchBuilding = steps.Count > 0
            ? Traverse.Create(steps[0].Behaviour).Field("_buildingBehaviour").GetValue<BuildingBehaviour>()
            : null;
        bool anyCraneStillWorking = steps.Any(IsStillWorking);
        bool canCompleteCurrentCraft = CanCompleteCurrentCraft(batchBuilding, steps);
        if (!anyCraneStillWorking || canCompleteCurrentCraft)
        {
            ApplyRequirementReservations(steps);
        }
        else
        {
            foreach (CraneStep craneStep in steps)
            {
                craneStep.CanOperateAtBatchStart = false;
            }
        }
        LogDiagnostics(__instance, steps);

        foreach (CraneStep craneStep in steps)
        {
            if (craneStep.CanOperateAtBatchStart)
            {
                Operate(craneStep);
            }
            else if (craneStep.Ready)
            {
                TryEndActivity(craneStep);
            }

            IncrementWaitCounters(craneStep);
        }

        for (int i = craneBehaviours.Count - 1; i >= 0; i--)
        {
            BuildingCraneBehaviour behaviour = craneBehaviours[i];
            if (behaviour != null && StillOwnedBy(__instance, behaviour))
            {
                behaviour.CallCanReceiveOnConveyor();
            }
        }

        return false;
    }

    private static bool IsStillWorking(CraneStep craneStep)
    {
        return craneStep.UpdateFrequency != null && craneStep.TicksWaited < craneStep.UpdateFrequency.Value;
    }

    private static bool CanCompleteCurrentCraft(BuildingBehaviour building, List<CraneStep> steps)
    {
        if (building == null || building.BuildRequirements == null)
        {
            return false;
        }

        bool hasPartialInputs = building.BuildRequirements.Any(requirement => requirement.Count > 0);
        if (!hasPartialInputs)
        {
            return false;
        }

        List<int> virtualCounts = building.BuildRequirements.Select(requirement => requirement.Count).ToList();
        foreach (CraneStep craneStep in steps)
        {
            if (!craneStep.Ready || craneStep.Resource == null)
            {
                continue;
            }

            int index = FindReceivableRequirementIndex(building, craneStep.Resource, ToReservedCounts(building, virtualCounts));
            if (index < 0)
            {
                continue;
            }

            virtualCounts[index]++;
            if (IsComplete(building, virtualCounts))
            {
                return true;
            }
        }

        return false;
    }

    private static List<int> ToReservedCounts(BuildingBehaviour building, List<int> virtualCounts)
    {
        List<int> reserved = new List<int>(virtualCounts.Count);
        for (int i = 0; i < virtualCounts.Count; i++)
        {
            reserved.Add(virtualCounts[i] - building.BuildRequirements[i].Count);
        }

        return reserved;
    }

    private static bool IsComplete(BuildingBehaviour building, List<int> counts)
    {
        for (int i = 0; i < building.BuildRequirements.Count; i++)
        {
            if (counts[i] < building.BuildRequirements[i].Max)
            {
                return false;
            }
        }

        return true;
    }

    private static void ApplyRequirementReservations(List<CraneStep> steps)
    {
        Dictionary<BuildingBehaviour, List<int>> reservedByBuilding = new Dictionary<BuildingBehaviour, List<int>>();

        foreach (CraneStep craneStep in steps)
        {
            if (!craneStep.CanOperateAtBatchStart)
            {
                continue;
            }

            BuildingBehaviour building = Traverse.Create(craneStep.Behaviour)
                .Field("_buildingBehaviour")
                .GetValue<BuildingBehaviour>();

            if (building == null || !building.CanReceiveResource(craneStep.Resource))
            {
                LogReject("can-receive=false", building, craneStep.Resource);
                craneStep.CanOperateAtBatchStart = false;
                continue;
            }

            if (!reservedByBuilding.TryGetValue(building, out List<int> reserved))
            {
                reserved = building.BuildRequirements.Select(_ => 0).ToList();
                reservedByBuilding.Add(building, reserved);
            }

            int requirementIndex = FindReceivableRequirementIndex(building, craneStep.Resource, reserved);
            if (requirementIndex < 0)
            {
                LogReject("no-requirement-capacity", building, craneStep.Resource);
                craneStep.CanOperateAtBatchStart = false;
                continue;
            }

            reserved[requirementIndex]++;
        }
    }

    private static int FindReceivableRequirementIndex(BuildingBehaviour building, Resource resource, List<int> reserved)
    {
        for (int i = 0; i < building.BuildRequirements.Count; i++)
        {
            BuildingConstructionResource requirement = building.BuildRequirements[i];
            int reservedCount = i < reserved.Count ? reserved[i] : 0;
            if (requirement.Count + reservedCount >= requirement.Max)
            {
                continue;
            }

            if (resource is ShapeResource shapeResource)
            {
                if (requirement is ShapeConstructionResource shapeRequirement && shapeRequirement.IsShape(shapeResource.ShapeData))
                {
                    return i;
                }

                continue;
            }

            if (requirement.ResourceData == resource.Data)
            {
                return i;
            }
        }

        return -1;
    }

    private static void LogReject(string reason, BuildingBehaviour building, Resource resource)
    {
        if (_rejectLogCount >= 30)
        {
            return;
        }

        _rejectLogCount++;
        if (building == null)
        {
            Diagnostics.Write($"reject reason={reason} building=null resource={DescribeResource(resource)}");
            return;
        }

        bool hasResources = Traverse.Create(building).Field("_hasResources").GetValue<bool>();
        string requirements = string.Join(";",
            building.BuildRequirements.Select((requirement, index) =>
                $"{index}:{DescribeRequirement(requirement)}={requirement.Count}/{requirement.Max}"));
        Diagnostics.Write(
            $"reject reason={reason} building={building.FactoryObject.CreatedId} active={building.IsBuildingActive} upgrading={building.IsUpgrading} completed={building.BuildingCompleted} hasResources={hasResources} resource={DescribeResource(resource)} requirements=[{requirements}]");
    }

    private static string DescribeRequirement(BuildingConstructionResource requirement)
    {
        if (requirement is ShapeConstructionResource shapeRequirement)
        {
            return "shape:" + shapeRequirement.Hash;
        }

        return requirement.ResourceData != null ? "resource:" + requirement.ResourceData.ID : "resource:null";
    }

    private static string DescribeResource(Resource resource)
    {
        if (resource == null)
        {
            return "null";
        }

        if (resource is ShapeResource shapeResource)
        {
            return "shape:" + shapeResource.ShapeData.RotationIndependantHash;
        }

        return resource.Data != null ? "resource:" + resource.Data.ID : "resource:null";
    }

    private static void LogDiagnostics(BuildingCranesBehaviour cranesBehaviour, List<CraneStep> steps)
    {
        if (_diagnosticLogCount >= 12 || steps.Count <= 1)
        {
            return;
        }

        int ready = steps.Count(step => step.Ready);
        int loaded = steps.Count(step => step.Resource != null);
        int accepted = steps.Count(step => step.CanOperateAtBatchStart);
        if (ready == 0 && loaded == 0)
        {
            return;
        }

        _diagnosticLogCount++;
        Diagnostics.Write(
            $"building-batch id={cranesBehaviour.FactoryObject.CreatedId} cranes={steps.Count} ready={ready} loaded={loaded} accepted={accepted}");
    }

    private static bool StillOwnedBy(BuildingCranesBehaviour cranesBehaviour, BuildingCraneBehaviour behaviour)
    {
        for (int i = 0; i < cranesBehaviour.Cranes.Count; i++)
        {
            if (ReferenceEquals(cranesBehaviour.Cranes[i].Behaviour, behaviour))
            {
                return true;
            }
        }

        return false;
    }

    private static void Operate(CraneStep craneStep)
    {
        int value = craneStep.UpdateFrequency.Value;
        craneStep.Behaviour.OnTakeResource.Fire(craneStep.Resource, value);
        craneStep.Behaviour.StartActivity();

        BuildingBehaviour building = Traverse.Create(craneStep.Behaviour)
            .Field("_buildingBehaviour")
            .GetValue<BuildingBehaviour>();
        building.AddResource(craneStep.Resource);

        craneStep.Conveyor.StopTryingToOutput();
        craneStep.Conveyor.RemoveResourceWithoutCallingClearBuffers(craneStep.Resource, false);

        Traverse craneTraverse = Traverse.Create(craneStep.Behaviour);
        craneTraverse.Field("_ticksWaited").SetValue(0);
        craneTraverse.Field("_downTicksWaited").SetValue(0);
        craneStep.TicksWaited = 0;
        craneStep.DownTicksWaited = 0;
    }

    private static void TryEndActivity(CraneStep craneStep)
    {
        if (craneStep.UpdateFrequency == null || craneStep.DownTicksWaited <= craneStep.UpdateFrequency.Value)
        {
            return;
        }

        craneStep.Behaviour.EndActivity();
        Traverse.Create(craneStep.Behaviour).Field("_downTicksWaited").SetValue(0);
        craneStep.DownTicksWaited = 0;
    }

    private static void IncrementWaitCounters(CraneStep craneStep)
    {
        Traverse craneTraverse = Traverse.Create(craneStep.Behaviour);
        craneTraverse.Field("_ticksWaited").SetValue(craneStep.TicksWaited + 1);
        craneTraverse.Field("_downTicksWaited").SetValue(craneStep.DownTicksWaited + 1);
    }

    public static void LogCraneProcess(BuildingCraneBehaviour behaviour)
    {
        if (_craneProcessLogCount >= 12)
        {
            return;
        }

        Traverse t = Traverse.Create(behaviour);
        BuildingBehaviour building = t.Field("_buildingBehaviour").GetValue<BuildingBehaviour>();
        ConveyorBehaviour conveyor = t.Field("_conveyorBehaviour").GetValue<ConveyorBehaviour>();
        bool hasConveyor = t.Field("_hasConveyor").GetValue<bool>();
        int ticksWaited = t.Field("_ticksWaited").GetValue<int>();
        IntVariableSO updateFrequency = t.Field("_updateFrequency").GetValue<IntVariableSO>();
        _craneProcessLogCount++;
        Diagnostics.Write(
            $"crane-process building={(building != null ? building.FactoryObject.CreatedId : -1)} hasConveyor={hasConveyor} conveyor={(conveyor != null)} hasResource={(conveyor != null && conveyor.HasResource())} ticks={ticksWaited}/{(updateFrequency != null ? updateFrequency.Value : -1)}");
    }
}

[HarmonyPatch(typeof(BuildingCraneBehaviour), nameof(BuildingCraneBehaviour.Process))]
static class BuildingCraneBehaviour_Process_ParallelCraneBatching_DiagnosticPatch
{
    static void Prefix(BuildingCraneBehaviour __instance)
    {
        BuildingCranesBehaviour_Process_ParallelCraneBatching_Patch.LogCraneProcess(__instance);
    }
}

static class Diagnostics
{
    private static readonly object LockObject = new object();

    public static void Write(string message)
    {
        try
        {
            string path = Path.Combine(
                Directory.GetCurrentDirectory(),
                "Scripts",
                "logs",
                "Dimava",
                "ParallelCraneBatching.diagnostics.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            lock (LockObject)
            {
                File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}
