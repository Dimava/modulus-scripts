using Data.Buildings;
using Data.FactoryFloor;
using Data.FactoryFloor.Behaviours;
using Data.FactoryFloor.Buildings;
using HarmonyLib;
using Logic.FactoryTools;
using Presentation.FactoryFloor;
using Presentation.FactoryFloor.FactoryObjectViews;
using Presentation.UI.OperatorUIs.OperatorPanelUIs;
using Presentation.UI.OperatorUIs.OperatorPanelUIs.Buildings;
using Presentation.UI.OperatorUIs.OperatorPanelUIs.HarvesterPad;
using ScriptEngine;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Press R while a Building panel is open      -> crane-placement mode (Add Crane button).
/// Press R while a Harvester Pad panel is open -> link-building mode (Link Building button).
/// Press R while a Supply Tank panel is open   -> link-recipient mode (Link Building button).
///
/// Steal connection (harvester pads): if the target building is already linked to a different
/// harvester pad, that link is silently removed so this harvester takes over.
///
/// Steal connection (supply tanks): if the target recipient is already serviced by a different
/// supply tank, that link is silently removed so this supply tank takes over.
///
/// Steal is only allowed when this operator still has link capacity.
/// Resource-mismatch confirmation dialogs (harvester pads) still apply as normal.
/// </summary>
[ScriptEntry]
public sealed class ConnectionHotkey : ScriptMod
{
    protected override void OnUpdate()
    {
        if (Keyboard.current == null || !Keyboard.current[Key.Q].wasPressedThisFrame)
        {
            return;
        }

        BuildingPanelUI buildingPanel = FindObjectOfType<BuildingPanelUI>();
        if (buildingPanel != null)
        {
            Traverse.Create(buildingPanel).Method("AddCrane").GetValue();
            return;
        }

        HarvesterPadUI harvesterPanel = FindObjectOfType<HarvesterPadUI>();
        if (harvesterPanel != null)
        {
            Traverse.Create(harvesterPanel).Method("LinkBuildingBtnPressed").GetValue();
            return;
        }

        SupplyTankUI supplyTankPanel = FindObjectOfType<SupplyTankUI>();
        if (supplyTankPanel != null)
        {
            Traverse.Create(supplyTankPanel).Method("LinkRecipientBtnPressed").GetValue();
        }
    }
}

[HarmonyPatch(typeof(HarvesterPadUI), "UpdateLandingPadPreview")]
public static class HarvesterPadUI_UpdateLandingPadPreview_Patch
{
    static void Postfix(HarvesterPadUI __instance, FactoryObject factoryObject, bool isValid)
    {
        if (!isValid || factoryObject == null) return;

        var t = Traverse.Create(__instance);
        if (t.Field("_hoverTargetIsValid").GetValue<bool>()) return;

        var behaviour = t.Field("_behaviour").GetValue<HarvesterPadBehaviour>();
        if (behaviour == null) return;

        var building = factoryObject.GetFactoryObjectBehaviour<BuildingBehaviour>();
        if (building == null
            || !building.BuildingLandingPad.Exists
            || !building.BuildingLandingPad.HasHarvesterPadBehaviour
            || building.BuildingLandingPad.HarvesterPadBehaviour == behaviour)
            return;

        if (behaviour.LinkedBuildingsCount >= behaviour.MaxLinkedBuildings) return;

        t.Field("_hoverTargetIsValid").SetValue(true);
        t.Field("_selectFactoryObjectTool").GetValue<SelectFactoryObjectTool>()?.SetCursor();
    }
}

[HarmonyPatch(typeof(HarvesterPadUI), "TryLinkSelectedBuilding")]
public static class HarvesterPadUI_TryLinkSelectedBuilding_Patch
{
    static void Prefix(FactoryObject factoryObject)
    {
        if (factoryObject == null) return;

        var building = factoryObject.GetFactoryObjectBehaviour<BuildingBehaviour>();
        if (building == null
            || !building.BuildingLandingPad.Exists
            || !building.BuildingLandingPad.HasHarvesterPadBehaviour)
            return;

        var existingHarvesterRef = building.BuildingLandingPad.HarvesterPadBehaviour.FactoryObject
            .GetFactoryObjectBehaviour<ReferenceFactoryObjectBehaviour>();
        var buildingRef = factoryObject
            .GetFactoryObjectBehaviour<ReferenceFactoryObjectBehaviour>();

        if (existingHarvesterRef != null && buildingRef != null)
            existingHarvesterRef.RemoveReference(buildingRef);
    }
}

[HarmonyPatch(typeof(SupplyTankUI), "LinkingHoverOverObject")]
public static class SupplyTankUI_LinkingHoverOverObject_Patch
{
    static void Postfix(SupplyTankUI __instance, FactoryObject factoryObject, bool isNotNull)
    {
        if (!isNotNull || factoryObject == null) return;

        var t = Traverse.Create(__instance);
        if (t.Field("_isShowingLinkLine").GetValue<bool>()) return;

        var behaviour = t.Field("_behaviour").GetValue<SupplyTankBehaviour>();
        if (behaviour == null) return;

        if (!factoryObject.TryGetFactoryObjectBehaviour<ReferenceFactoryObjectBehaviour>(out var recipientRef))
            return;
        if (recipientRef.ReferencedObjects.Count <= 0) return;

        var selfRef = t.Field("_referenceBehaviour").GetValue<ReferenceFactoryObjectBehaviour>();
        if (selfRef != null && recipientRef.ReferencedObjects.Contains(selfRef)) return;

        if (behaviour.LinkedRecipientsCount >= behaviour.MaxLinkedRecipients) return;

        var linksView = t.Field("_linksView").GetValue<ReferenceBehaviourLinksView>();
        if (linksView != null)
        {
            linksView.HideLinks();
            linksView.ShowLinks();
            linksView.ShowLineToFactoryObject(factoryObject, 0.7f, new Color(1f, 1f, 1f, 0.6f));
        }
        t.Field("_lastLinkLineObject").SetValue(factoryObject);
        t.Field("_isShowingLinkLine").SetValue(true);
        t.Field("_selectFactoryObjectTool").GetValue<SelectFactoryObjectTool>()?.SetCursor();
    }
}

[HarmonyPatch(typeof(SupplyTankUI), "LinkSelectedRecipient")]
public static class SupplyTankUI_LinkSelectedRecipient_Patch
{
    static void Prefix(FactoryObject factoryObject)
    {
        if (factoryObject == null) return;

        if (!factoryObject.TryGetFactoryObjectBehaviour<ReferenceFactoryObjectBehaviour>(out var recipientRef))
            return;
        if (recipientRef.ReferencedObjects.Count <= 0) return;

        var existingSupplyTankRef = recipientRef.ReferencedObjects[0];
        existingSupplyTankRef.RemoveReference(recipientRef);
    }
}
