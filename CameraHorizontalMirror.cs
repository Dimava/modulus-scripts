using Events.FactoryFloor;
using HarmonyLib;
using Logic.FactoryTools;
using ScriptEngine;
using UnityEngine;

/// <summary>After mirror, +180° when camera yaw is outside the 90°/270° ±45° bands.</summary>
[ScriptEntry]
public sealed class CameraHorizontalMirror : ScriptMod
{
    /// <summary>True when mirror should be followed by 180° (yaw outside 90°/270° ±45°); false if no yaw source.</summary>
    public static bool ShouldRotateAfterMirror()
    {
        if (Camera.main == null)
            return false;

        float y = Mathf.Repeat(Camera.main.transform.eulerAngles.y, 360f);
        return !((y >= 45f && y <= 135f) || (y >= 225f && y <= 315f));
    }
}

[HarmonyPatch(typeof(PlacementTool), nameof(PlacementTool.Mirror))]
static class PlacementToolMirrorPostRotatePatch
{
    static void Postfix(PlacementTool __instance)
    {
        if (CameraHorizontalMirror.ShouldRotateAfterMirror())
        {
            __instance.Rotate(180);
        }
    }
}

[HarmonyPatch(typeof(SelectionFactoryTool), nameof(SelectionFactoryTool.Mirror))]
static class SelectionFactoryToolMirrorPostRotatePatch
{
    static void Postfix(SelectionFactoryTool __instance)
    {
        if (CameraHorizontalMirror.ShouldRotateAfterMirror())
        {
            __instance.Rotate(180);
        }
    }
}
