using Data.Minimap;
using HarmonyLib;
using MelonLoader;
using Presentation.CameraView;
using Presentation.Locators;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Middle-click on the minimap resets the camera yaw to north (0 degrees).
/// </summary>
public static class MinimapMiddleClickResetRotation
{
    private static readonly HarmonyLib.Harmony HarmonyInstance = new HarmonyLib.Harmony("minimap-middleclick-reset-rotation");

    public static void OnLoad()
    {
        HarmonyInstance.UnpatchSelf();
        HarmonyInstance.PatchAll(typeof(MinimapMiddleClickResetRotation).Assembly);
        MelonLogger.Msg("[MinimapMiddleClickResetRotation] Loaded.");
    }

    public static void OnUnload()
    {
        HarmonyInstance.UnpatchSelf();
        MelonLogger.Msg("[MinimapMiddleClickResetRotation] Unloaded.");
    }

    internal static void ResetCameraRotation()
    {
        foreach (CameraViewLocator locator in Resources.FindObjectsOfTypeAll<CameraViewLocator>())
        {
            CameraView cameraView = locator.CameraView;
            if (cameraView == null)
            {
                continue;
            }

            Traverse.Create(cameraView).Method("LerpYaw", new object[] { 0f }).GetValue();
            MelonLogger.Msg("[MinimapMiddleClickResetRotation] Camera rotation reset to north.");
            return;
        }

        MelonLogger.Warning("[MinimapMiddleClickResetRotation] Could not find CameraViewLocator.");
    }
}

/// <summary>
/// Added to the minimap GameObject at runtime to intercept pointer click events.
/// </summary>
public class MinimapClickHandler : MonoBehaviour, IPointerClickHandler
{
    public void OnPointerClick(PointerEventData eventData)
    {
        if (eventData.button == PointerEventData.InputButton.Middle)
        {
            MinimapMiddleClickResetRotation.ResetCameraRotation();
        }
    }
}

[HarmonyPatch(typeof(MinimapScrollViewControls), "Awake")]
static class MinimapScrollViewControls_Awake_Patch
{
    static void Postfix(MinimapScrollViewControls __instance)
    {
        if (__instance.GetComponent<MinimapClickHandler>() == null)
        {
            __instance.gameObject.AddComponent<MinimapClickHandler>();
        }
    }
}
