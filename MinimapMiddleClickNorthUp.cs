using Data.Minimap;
using HarmonyLib;
using Presentation.CameraView;
using Presentation.Locators;
using ScriptEngine;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Middle-click on the minimap resets the camera yaw to north (0 degrees).
/// </summary>
[ScriptEntry]
public sealed class MinimapMiddleClickNorthUp : ScriptMod
{
    private static MinimapMiddleClickNorthUp _instance;

    protected override void OnEnable()
    {
        _instance = this;
    }

    protected override void OnDisable()
    {
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }
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
            _instance?.Log("Camera rotation reset to north.");
            return;
        }

        _instance?.Warn("Could not find CameraViewLocator.");
    }
}

/// <summary>
/// Added to the minimap GameObject at runtime to intercept pointer click events.
/// </summary>
public class MinimapNorthUpClickHandler : MonoBehaviour, IPointerClickHandler
{
    public void OnPointerClick(PointerEventData eventData)
    {
        if (eventData.button == PointerEventData.InputButton.Middle)
        {
            MinimapMiddleClickNorthUp.ResetCameraRotation();
        }
    }
}

[HarmonyPatch(typeof(MinimapScrollViewControls), "Awake")]
static class MinimapScrollViewControls_Awake_Patch
{
    static void Postfix(MinimapScrollViewControls __instance)
    {
        if (__instance.GetComponent<MinimapNorthUpClickHandler>() == null)
        {
            __instance.gameObject.AddComponent<MinimapNorthUpClickHandler>();
        }
    }
}
