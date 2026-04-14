using Data.Buildings;
using Data.FactoryFloor;
using Data.FactoryFloor.Behaviours;
using Data.FactoryFloor.Buildings;
using HarmonyLib;
using Logic.FactoryTools;
using MelonLoader;
using Presentation.FactoryFloor;
using Presentation.FactoryFloor.FactoryObjectViews;
using Presentation.UI.OperatorUIs.OperatorPanelUIs;
using Presentation.UI.OperatorUIs.OperatorPanelUIs.Buildings;
using Presentation.UI.OperatorUIs.OperatorPanelUIs.HarvesterPad;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Press R while a Building panel is open      → crane-placement mode (Add Crane button).
/// Press R while a Harvester Pad panel is open → link-building mode (Link Building button).
/// Press R while a Supply Tank panel is open   → link-recipient mode (Link Building button).
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
public static class ConnectionHotkey
{
    static readonly HarmonyLib.Harmony _h = new HarmonyLib.Harmony("connection-hotkey");
    private static GameObject _go;

    public static void OnLoad()
    {
        _h.UnpatchSelf();
        _h.PatchAll(typeof(ConnectionHotkey).Assembly);
        if (_go != null) GameObject.Destroy(_go);
        _go = new GameObject("__ConnectionHotkey__");
        GameObject.DontDestroyOnLoad(_go);
        _go.AddComponent<ConnectionHotkeyBehaviour>();
        MelonLogger.Msg("[ConnectionHotkey] Loaded.");
    }

    public static void OnUnload()
    {
        _h.UnpatchSelf();
        if (_go != null) { GameObject.Destroy(_go); _go = null; }
        MelonLogger.Msg("[ConnectionHotkey] Unloaded.");
    }
}

public class ConnectionHotkeyBehaviour : MonoBehaviour
{
    private void Update()
    {
        if (Keyboard.current == null) return;
        if (!Keyboard.current[Key.R].wasPressedThisFrame) return;

        // Building panel open → crane placement mode
        var buildingPanel = FindObjectOfType<BuildingPanelUI>();
        if (buildingPanel != null)
        {
            Traverse.Create(buildingPanel).Method("AddCrane").GetValue();
            return;
        }

        // Harvester pad panel open → link building mode
        var harvesterPanel = FindObjectOfType<HarvesterPadUI>();
        if (harvesterPanel != null)
        {
            Traverse.Create(harvesterPanel).Method("LinkBuildingBtnPressed").GetValue();
            return;
        }

        // Supply tank panel open → link recipient mode
        var supplyTankPanel = FindObjectOfType<SupplyTankUI>();
        if (supplyTankPanel != null)
        {
            Traverse.Create(supplyTankPanel).Method("LinkRecipientBtnPressed").GetValue();
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Harvester pad steal patches
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// After UpdateLandingPadPreview runs, if the hover was blocked solely because the target
/// building already has a harvester linked, and this harvester still has link capacity,
/// override _hoverTargetIsValid to true so the user can steal the connection.
/// The cursor is reset back to the linking cursor.
/// </summary>
[HarmonyPatch(typeof(HarvesterPadUI), "UpdateLandingPadPreview")]
public static class HarvesterPadUI_UpdateLandingPadPreview_Patch
{
    static void Postfix(HarvesterPadUI __instance, FactoryObject factoryObject, bool isValid)
    {
        // isValid==false means SelectFactoryObjectTool rejected the object type entirely
        if (!isValid || factoryObject == null) return;

        var t = Traverse.Create(__instance);

        // Nothing to override if it's already valid
        if (t.Field("_hoverTargetIsValid").GetValue<bool>()) return;

        var behaviour = t.Field("_behaviour").GetValue<HarvesterPadBehaviour>();
        if (behaviour == null) return;

        // Only override if the block is due to an existing landing pad on a *different* harvester
        var building = factoryObject.GetFactoryObjectBehaviour<BuildingBehaviour>();
        if (building == null
            || !building.BuildingLandingPad.Exists
            || !building.BuildingLandingPad.HasHarvesterPadBehaviour
            || building.BuildingLandingPad.HarvesterPadBehaviour == behaviour)
            return;

        // Steal requires capacity on this harvester
        if (behaviour.LinkedBuildingsCount >= behaviour.MaxLinkedBuildings) return;

        // Allow the steal
        t.Field("_hoverTargetIsValid").SetValue(true);

        // Reset cursor to the tool's linking cursor (UpdateLandingPadPreview left it "blocked")
        t.Field("_selectFactoryObjectTool").GetValue<SelectFactoryObjectTool>()?.SetCursor();
    }
}

/// <summary>
/// Before linking a building to this harvester, if the building is already connected to a
/// different harvester pad, remove that connection first.  This causes BuildingLandingPad.Exists
/// to become false so GenerateLandingPad (called by the normal link flow) can proceed.
/// </summary>
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

        // Disconnect the existing harvester.
        // RemoveReference is bidirectional: it fires OnRemovedReferencedObject on the building
        // side → UnlinkFromHarvesterPad → DestroyLandingPad → _exists=false, then normal
        // linking proceeds and GenerateLandingPad runs cleanly.
        var existingHarvesterRef = building.BuildingLandingPad.HarvesterPadBehaviour.FactoryObject
            .GetFactoryObjectBehaviour<ReferenceFactoryObjectBehaviour>();
        var buildingRef = factoryObject
            .GetFactoryObjectBehaviour<ReferenceFactoryObjectBehaviour>();

        if (existingHarvesterRef != null && buildingRef != null)
            existingHarvesterRef.RemoveReference(buildingRef);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Supply tank steal patches
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// After LinkingHoverOverObject runs, if the recipient was blocked solely because it is
/// already serviced by a different supply tank, and this tank still has link capacity,
/// set _isShowingLinkLine = true and show the link line so LinkSelectedRecipient proceeds.
/// </summary>
[HarmonyPatch(typeof(SupplyTankUI), "LinkingHoverOverObject")]
public static class SupplyTankUI_LinkingHoverOverObject_Patch
{
    static void Postfix(SupplyTankUI __instance, FactoryObject factoryObject, bool isNotNull)
    {
        if (!isNotNull || factoryObject == null) return;

        var t = Traverse.Create(__instance);

        // Already showing a valid link line — nothing to override
        if (t.Field("_isShowingLinkLine").GetValue<bool>()) return;

        var behaviour = t.Field("_behaviour").GetValue<SupplyTankBehaviour>();
        if (behaviour == null) return;

        // Check whether the block is because this recipient is already serviced
        if (!factoryObject.TryGetFactoryObjectBehaviour<ReferenceFactoryObjectBehaviour>(out var recipientRef))
            return;
        if (recipientRef.ReferencedObjects.Count <= 0) return;  // blocked for another reason

        // Don't steal from ourselves
        var selfRef = t.Field("_referenceBehaviour").GetValue<ReferenceFactoryObjectBehaviour>();
        if (selfRef != null && recipientRef.ReferencedObjects.Contains(selfRef)) return;

        // Steal requires capacity
        if (behaviour.LinkedRecipientsCount >= behaviour.MaxLinkedRecipients) return;

        // Override: show the link line and mark as valid
        var linksView = t.Field("_linksView").GetValue<ReferenceBehaviourLinksView>();
        if (linksView != null)
        {
            linksView.HideLinks();
            linksView.ShowLinks();
            linksView.ShowLineToFactoryObject(factoryObject, 0.7f, new Color(1f, 1f, 1f, 0.6f));
        }
        t.Field("_lastLinkLineObject").SetValue(factoryObject);
        t.Field("_isShowingLinkLine").SetValue(true);

        // Reset cursor from "blocked" back to linking cursor
        t.Field("_selectFactoryObjectTool").GetValue<SelectFactoryObjectTool>()?.SetCursor();
    }
}

/// <summary>
/// Before linking a recipient to this supply tank, if the recipient is already serviced by a
/// different supply tank, remove that connection first so AddReference can proceed normally.
/// </summary>
[HarmonyPatch(typeof(SupplyTankUI), "LinkSelectedRecipient")]
public static class SupplyTankUI_LinkSelectedRecipient_Patch
{
    static void Prefix(FactoryObject factoryObject)
    {
        if (factoryObject == null) return;

        if (!factoryObject.TryGetFactoryObjectBehaviour<ReferenceFactoryObjectBehaviour>(out var recipientRef))
            return;
        if (recipientRef.ReferencedObjects.Count <= 0) return;

        // Disconnect the existing supply tank from this recipient.
        // RemoveReference is bidirectional, so both sides are cleaned up.
        var existingSupplyTankRef = recipientRef.ReferencedObjects[0];
        existingSupplyTankRef.RemoveReference(recipientRef);
    }
}
