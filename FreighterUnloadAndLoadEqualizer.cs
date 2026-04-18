using Data.FactoryFloor.FactoryObjectBehaviours;
using Data.FactoryFloor.Freighter.Actions;
using Data.Variables;
using HarmonyLib;
using ScriptEngine;

[ScriptEntry]
public sealed class FreighterUnloadAndLoadEqualizer : ScriptMod
{
    protected override void OnEnable()
    {
        Log("Unload And Load now equalizes cargo by half-difference.");
    }

    static int GetHalfDifference(int higherAmount, int lowerAmount)
    {
        if (higherAmount <= lowerAmount)
            return 0;

        return (higherAmount - lowerAmount) / 2;
    }

    static int GetFreighterMaxSlotAmount(FreighterSlotActionUnloadAndLoad unloadAndLoadAction)
    {
        var loadAction = Traverse.Create(unloadAndLoadAction)
            .Field("_freigherSlotLoadAction")
            .GetValue<FreighterSlotActionLoad>();
        if (loadAction == null)
            return int.MaxValue;

        var maxSlotAmount = Traverse.Create(loadAction)
            .Field("_maxFreighterSlotAmount")
            .GetValue<IntVariableSO>();
        if (maxSlotAmount == null)
            return int.MaxValue;

        return maxSlotAmount.Value;
    }

    static void TryBalancedUnload(FreightHubBehaviour freightHub, int slotIndex, ref FreightHubBehaviour.FreightHubSlot freighterSlot)
    {
        if (!freighterSlot.HasResource)
            return;

        var outSlot = freightHub.GetOutSlot(slotIndex);
        if (outSlot.HasResource && !freightHub.IsSameResourceAsOutSlot(freighterSlot.Resource, slotIndex))
            return;

        int outAmount = outSlot.HasResource ? outSlot.Amount : 0;
        int transferAmount = GetHalfDifference(freighterSlot.Amount, outAmount);
        int outCapacity = freightHub.MaxOutStorage - outAmount;
        if (transferAmount > outCapacity)
            transferAmount = outCapacity;

        if (transferAmount <= 0)
            return;

        if (!outSlot.HasResource)
            outSlot.Resource = freighterSlot.Resource;

        outSlot.Amount += transferAmount;
        freighterSlot.Amount -= transferAmount;

        freightHub.SetOutSlot(slotIndex, outSlot);

        if (freighterSlot.Amount <= 0)
            freighterSlot = default(FreightHubBehaviour.FreightHubSlot);

        freightHub.UnloadCrateFromFreighter(slotIndex, freighterSlot, freighterSlot.HasResource);
    }

    static void TryBalancedLoad(FreightHubBehaviour freightHub, int slotIndex, ref FreightHubBehaviour.FreightHubSlot freighterSlot, int maxFreighterSlotAmount)
    {
        var inSlot = freightHub.GetInSlot(slotIndex);
        if (!inSlot.HasResource)
            return;

        if (freighterSlot.HasResource && !freightHub.IsSameResourceAsInSlot(freighterSlot.Resource, slotIndex))
            return;

        int freighterAmount = freighterSlot.HasResource ? freighterSlot.Amount : 0;
        int transferAmount = GetHalfDifference(inSlot.Amount, freighterAmount);
        int freighterCapacity = maxFreighterSlotAmount - freighterAmount;
        if (transferAmount > freighterCapacity)
            transferAmount = freighterCapacity;

        if (transferAmount <= 0)
            return;

        bool alreadyHasResource = freighterSlot.HasResource;

        freighterSlot.Resource = inSlot.Resource;
        freighterSlot.Amount = freighterAmount + transferAmount;
        inSlot.Amount -= transferAmount;

        freightHub.SetInSlot(slotIndex, inSlot);
        freightHub.LoadCrateIntoFreighter(slotIndex, freighterSlot, alreadyHasResource);
    }

    [HarmonyPatch(typeof(FreighterSlotActionUnloadAndLoad), nameof(FreighterSlotActionUnloadAndLoad.Apply))]
    static class FreighterSlotActionUnloadAndLoad_Apply_Patch
    {
        static bool Prefix(FreighterSlotActionUnloadAndLoad __instance, FreightHubBehaviour freightHub, int slotIndex, ref FreightHubBehaviour.FreightHubSlot freighterSlot)
        {
            TryBalancedUnload(freightHub, slotIndex, ref freighterSlot);
            TryBalancedLoad(freightHub, slotIndex, ref freighterSlot, GetFreighterMaxSlotAmount(__instance));
            return false;
        }
    }
}
