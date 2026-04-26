using DG.Tweening;
using HarmonyLib;
using MelonLoader;
using Presentation.Shapes;
using ScriptEngine;
using UnityEngine;

/// <summary>
/// Makes R/T rotation in the cutter (and any other inside-operator UI) respond
/// immediately instead of waiting for the current spin animation to finish.
///
/// Strategy: when a rotate is requested while an animation is in flight, we
/// call DOTween.Complete on the animating object (with callbacks = true) so the
/// sequence's AppendCallback fires synchronously — destroying the temp mesh,
/// re-enabling the real renderer, and clearing _isAnimating — then the normal
/// method body runs as if no animation was in progress.
/// </summary>
[ScriptEntry]
public sealed class InstantShapeRotation : ScriptMod
{
    /// <summary>
    /// If a rotation animation is currently running, complete it immediately
    /// (jump to end values + fire callbacks) so <c>_isAnimating</c> becomes
    /// false before the caller tries to start a new animation.
    /// </summary>
    internal static void FlushAnimation(ShapeLoader instance)
    {
        var t = Traverse.Create(instance);

        if (!t.Field("_isAnimating").GetValue<bool>())
            return;

        ShapeLoader animLoader = t.Field("_animationShapeLoader").GetValue<ShapeLoader>();

        if (animLoader != null)
        {
            // Complete all tweens on the temp shape's transform.
            // withCallbacks:true fires the Sequence AppendCallback which:
            //   - destroys animLoader.gameObject
            //   - re-enables instance.MeshRenderer
            //   - sets _isAnimating = false
            DOTween.Complete(animLoader.transform, withCallbacks: true);
        }

        // Safety fallback — if the callback somehow didn't clear the flag
        // (e.g. the sequence was not yet playing), clean up manually.
        if (t.Field("_isAnimating").GetValue<bool>())
        {
            if (animLoader != null)
                Object.Destroy(animLoader.gameObject);

            instance.MeshRenderer.enabled = true;
            t.Field("_isAnimating").SetValue(false);
            t.Field("_animationShapeLoader").SetValue(null);
        }
    }
}

[HarmonyPatch(typeof(ShapeLoader), nameof(ShapeLoader.RotateShapeXAnimated))]
static class ShapeLoader_RotateShapeXAnimated_Patch
{
    static void Prefix(ShapeLoader __instance)
    {
        InstantShapeRotation.FlushAnimation(__instance);
    }
}

[HarmonyPatch(typeof(ShapeLoader), nameof(ShapeLoader.RotateShapeYAnimated))]
static class ShapeLoader_RotateShapeYAnimated_Patch
{
    static void Prefix(ShapeLoader __instance)
    {
        InstantShapeRotation.FlushAnimation(__instance);
    }
}
