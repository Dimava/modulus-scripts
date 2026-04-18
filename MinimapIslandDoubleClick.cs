using Data.FactoryFloor.Maps;
using Data.Minimap;
using HarmonyLib;
using Presentation.CameraView;
using Presentation.Locators;
using ScriptEngine;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Double-clicking an island on the minimap resets yaw to north, moves the camera
/// to the island center, and zooms to a good overview distance.
/// </summary>
[ScriptEntry]
public sealed class MinimapIslandDoubleClick : ScriptMod
{
    private static MinimapIslandDoubleClick _instance;

    // Zoom percentage: 0 = fully zoomed out, 1 = fully zoomed in.
    // 0.25 gives a comfortable island overview.
    public const float IslandZoomPercentage = 0.25f;

    // Pitch in degrees. CameraView clamps between _minPitch (25) and _maxPitch (70).
    public const float IslandPitch = 45f;

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

    internal static CameraView FindCameraView()
    {
        foreach (CameraViewLocator locator in Resources.FindObjectsOfTypeAll<CameraViewLocator>())
        {
            if (locator.CameraView != null)
            {
                return locator.CameraView;
            }
        }
        return null;
    }

    internal static void LogInfo(string message)
    {
        _instance?.Log(message);
    }

    internal static void LogWarn(string message)
    {
        _instance?.Warn(message);
    }
}

/// <summary>
/// Attached to each MinimapIslandUI GameObject at runtime.
/// Handles double-click → fly-to-island.
/// </summary>
public class MinimapIslandClickHandler : MonoBehaviour, IPointerClickHandler
{
    private IslandObject _islandObject;

    public void SetIsland(IslandObject islandObject)
    {
        _islandObject = islandObject;
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (eventData.clickCount < 2 || _islandObject == null)
        {
            return;
        }

        CameraView cameraView = MinimapIslandDoubleClick.FindCameraView();
        if (cameraView == null)
        {
            MinimapIslandDoubleClick.LogWarn("CameraView not found.");
            return;
        }

        Vector3Int pos = _islandObject.Position;
        Vector3 worldCenter = new Vector3(pos.x, 0f, pos.z);

        cameraView.LerpToTarget(
            worldCenter,
            MinimapIslandDoubleClick.IslandZoomPercentage,
            0f,                                      // targetYaw  — north
            MinimapIslandDoubleClick.IslandPitch,    // targetPitch
            false                                    // blockInput
        );

        MinimapIslandDoubleClick.LogInfo($"Flying to island at {worldCenter}.");
    }
}

[HarmonyPatch(typeof(MinimapIslandUI), nameof(MinimapIslandUI.SetIslandTexture))]
static class MinimapIslandUI_SetIslandTexture_Patch
{
    static void Postfix(MinimapIslandUI __instance, IslandObject islandObject)
    {
        MinimapIslandClickHandler handler = __instance.GetComponent<MinimapIslandClickHandler>();
        if (handler == null)
        {
            handler = __instance.gameObject.AddComponent<MinimapIslandClickHandler>();
        }
        handler.SetIsland(islandObject);
    }
}
