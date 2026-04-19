using System;
using System.Collections.Generic;
using HarmonyLib;
using Presentation.FactoryFloor;
using Presentation.UI;
using ScriptEngine;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Toolbar <see cref="ButtonShortCut"/> simulates a button click on <c>performed</c>. If that binding shares the mouse
/// with camera orbit (e.g. RMB), a drag release can still <c>performed</c> and fire the shortcut. This mod records
/// pointer + time on <c>started</c> and only invokes <c>onClick</c> when movement and hold match the same gate as
/// <see cref="ToolSystem"/> cancel-click (read from the first live <c>ToolSystem</c> when possible).
/// </summary>
[ScriptEntry]
public sealed class ButtonShortCutDragGate : ScriptMod
{
}

static class ButtonShortCutDragGateImpl
{
    internal static readonly object GateLock = new();

    internal sealed class Subscription
    {
        internal readonly InputAction Action;
        internal readonly Action<InputAction.CallbackContext> StartedHandler;

        internal Subscription(InputAction action, Action<InputAction.CallbackContext> startedHandler)
        {
            Action = action;
            StartedHandler = startedHandler;
        }
    }

    internal static readonly Dictionary<ButtonShortCut, Subscription> Subs = new();
    internal static readonly Dictionary<ButtonShortCut, (Vector2 pos, DateTime utc)> Snaps = new();

    internal static float MaxMovePx = 80f;
    internal static double MaxHoldSec = 0.35;
    static bool s_thresholdsLoaded;

    internal static void EnsureThresholds()
    {
        if (s_thresholdsLoaded)
            return;
        s_thresholdsLoaded = true;
        try
        {
            foreach (var ts in Resources.FindObjectsOfTypeAll<ToolSystem>())
            {
                if (ts == null || !ts.gameObject.scene.IsValid())
                    continue;
                var t = Traverse.Create(ts);
                MaxMovePx = t.Field<float>("_maxMousePointerDistance").Value;
                MaxHoldSec = t.Field<double>("_maxButtonHoldDuration").Value;
                return;
            }
        }
        catch
        {
            // keep defaults
        }
    }

    internal static Vector2 ReadScreenPointer()
    {
        if (Mouse.current != null)
            return Mouse.current.position.ReadValue();
        if (Pointer.current != null)
            return Pointer.current.position.ReadValue();
        return default;
    }

    internal static bool IsPointerLikeDevice(InputAction.CallbackContext ctx)
    {
        var d = ctx.control?.device;
        return d is Mouse or Pen or Touchscreen;
    }

    internal static void RecordStart(ButtonShortCut owner)
    {
        lock (GateLock)
            Snaps[owner] = (ReadScreenPointer(), DateTime.UtcNow);
    }
}

[HarmonyPatch(typeof(ButtonShortCut), nameof(ButtonShortCut.Init))]
static class ButtonShortCut_Init_SubscribeStartedPatch
{
    static void Postfix(ButtonShortCut __instance)
    {
        if (!Traverse.Create(__instance).Field<bool>("_initialized").Value)
            return;

        var button = Traverse.Create(__instance).Field<Button>("_button").Value;
        var iar = Traverse.Create(__instance).Field<InputActionReference>("_inputAction").Value;
        if (button == null || iar?.action == null)
            return;

        lock (ButtonShortCutDragGateImpl.GateLock)
        {
            if (ButtonShortCutDragGateImpl.Subs.ContainsKey(__instance))
                return;

            InputAction action = iar.action;
            void OnStarted(InputAction.CallbackContext _) => ButtonShortCutDragGateImpl.RecordStart(__instance);
            action.started += OnStarted;
            ButtonShortCutDragGateImpl.Subs[__instance] = new ButtonShortCutDragGateImpl.Subscription(action, OnStarted);
        }
    }
}

[HarmonyPatch(typeof(ButtonShortCut), "OnDestroy")]
static class ButtonShortCut_OnDestroy_UnsubscribeStartedPatch
{
    static void Postfix(ButtonShortCut __instance)
    {
        lock (ButtonShortCutDragGateImpl.GateLock)
        {
            if (ButtonShortCutDragGateImpl.Subs.TryGetValue(__instance, out var sub))
            {
                sub.Action.started -= sub.StartedHandler;
                ButtonShortCutDragGateImpl.Subs.Remove(__instance);
            }

            ButtonShortCutDragGateImpl.Snaps.Remove(__instance);
        }
    }
}

[HarmonyPatch(typeof(ButtonShortCut), "ActionPerformed")]
static class ButtonShortCut_ActionPerformed_DragGatePatch
{
    static bool Prefix(ButtonShortCut __instance, InputAction.CallbackContext obj)
    {
        var button = Traverse.Create(__instance).Field<Button>("_button").Value;
        if (button == null || !button.interactable)
            return false;

        if (!ButtonShortCutDragGateImpl.IsPointerLikeDevice(obj))
        {
            button.onClick.Invoke();
            return false;
        }

        ButtonShortCutDragGateImpl.EnsureThresholds();

        (Vector2 pos, DateTime utc) snap;
        lock (ButtonShortCutDragGateImpl.GateLock)
        {
            if (!ButtonShortCutDragGateImpl.Snaps.TryGetValue(__instance, out snap))
            {
                button.onClick.Invoke();
                return false;
            }
        }

        Vector2 cur = ButtonShortCutDragGateImpl.ReadScreenPointer();
        float dist = Vector2.Distance(cur, snap.pos);
        double held = (DateTime.UtcNow - snap.utc).TotalSeconds;

        bool clickLike = dist <= ButtonShortCutDragGateImpl.MaxMovePx && held <= ButtonShortCutDragGateImpl.MaxHoldSec;
        if (!clickLike)
            return false;

        button.onClick.Invoke();
        return false;
    }
}
