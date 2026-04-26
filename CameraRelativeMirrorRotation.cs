using HarmonyLib;
using Logic.Factory.Blueprint;
using Logic.FactoryTools;
using ScriptEngine;
using UnityEngine;

/// <summary>After mirror, +180° when <see cref="IsEven"/> for camera yaw XOR <see cref="IsEven"/> for blueprint yaw.</summary>
[ScriptEntry]
public sealed class CameraRelativeMirrorRotation : ScriptMod
{
    /// <summary>True when <see cref="Mathf.Repeat"/>(yaw, 180) lies in [45, 135] (the 90°/270° ±45° windows folded mod 180).</summary>
    public static bool IsEven(float yaw)
    {
        float y = Mathf.Repeat(yaw, 180f);
        return y >= 45f && y <= 135f;
    }

    /// <summary>True when mirror should be followed by 180° (<c>IsEven(camera)</c> XOR <c>IsEven(blueprint)</c>).</summary>
    public static bool ShouldRotateAfterMirror(Blueprint? blueprint)
    {
        if (Camera.main == null)
            return false;

        float cameraRotation = Camera.main.transform.eulerAngles.y;
        float bpRotation = GetBlueprintYawDegreesForMirror(blueprint);
        return IsEven(cameraRotation) ^ IsEven(bpRotation);
    }

    /// <summary>Yaw degrees combined the same way as <c>Blueprint.MirrorRelativePositions</c> for mirror axis (blueprint rotation + element rotation when there is a single element).</summary>
    public static int GetBlueprintYawDegreesForMirror(Blueprint? blueprint)
    {
        if (blueprint == null)
            return 0;

        int yaw = blueprint.Rotation;
        if (blueprint.Elements is { Count: 1 })
            yaw += Traverse.Create(blueprint.Elements[0]).Property("Rotation").GetValue<int>();

        yaw %= 360;
        if (yaw < 0)
            yaw += 360;
        return yaw;
    }
}

[HarmonyPatch(typeof(PlacementTool), nameof(PlacementTool.Mirror))]
static class PlacementToolMirrorPostRotatePatch
{
    static void Postfix(PlacementTool __instance)
    {
        var blueprint = Traverse.Create(__instance).Field<Blueprint>("_selectedBlueprint").Value;
        if (CameraRelativeMirrorRotation.ShouldRotateAfterMirror(blueprint))
            __instance.Rotate(180);
    }
}

[HarmonyPatch(typeof(SelectionFactoryTool), nameof(SelectionFactoryTool.Mirror))]
static class SelectionFactoryToolMirrorPostRotatePatch
{
    static void Postfix(SelectionFactoryTool __instance)
    {
        var blueprint = Traverse.Create(__instance).Field<Blueprint>("_selection").Value;
        if (CameraRelativeMirrorRotation.ShouldRotateAfterMirror(blueprint))
            __instance.Rotate(180);
    }
}
