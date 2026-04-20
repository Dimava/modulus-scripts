using System.Collections.Generic;
using ScriptEngine;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Keeps the game updating while unfocused, but suppresses scene/UI rendering until focus returns.
/// </summary>
[ScriptEntry]
public sealed class UnfocusedNoRender : ScriptMod
{
    private const int BackgroundRenderFrameInterval = 1000;
    private const float BackgroundSweepIntervalSeconds = 0.5f;

    private readonly List<Camera> _disabledCameras = new();
    private readonly List<Canvas> _disabledCanvases = new();

    private bool _isSuspended;
    private bool _previousRunInBackground;
    private int _previousRenderFrameInterval;
    private float _nextSweepTime;

    protected override void OnEnable()
    {
        _previousRunInBackground = Application.runInBackground;
        Application.runInBackground = true;
    }

    protected override void OnDisable()
    {
        RestoreRendering();
        Application.runInBackground = _previousRunInBackground;
    }

    protected override void OnUpdate()
    {
        bool shouldSuspendRendering = !Application.isFocused;
        if (shouldSuspendRendering != _isSuspended)
        {
            if (shouldSuspendRendering)
            {
                SuspendRendering();
            }
            else
            {
                RestoreRendering();
            }
        }

        if (_isSuspended && Time.unscaledTime >= _nextSweepTime)
        {
            _nextSweepTime = Time.unscaledTime + BackgroundSweepIntervalSeconds;
            DisableActiveRenderingComponents();
        }
    }

    private void SuspendRendering()
    {
        _isSuspended = true;
        _previousRenderFrameInterval = OnDemandRendering.renderFrameInterval;
        _nextSweepTime = 0f;

        OnDemandRendering.renderFrameInterval = BackgroundRenderFrameInterval;
        DisableActiveRenderingComponents();
        Log("Suspended cameras/canvases while unfocused.");
    }

    private void RestoreRendering()
    {
        if (!_isSuspended)
        {
            return;
        }

        _isSuspended = false;
        OnDemandRendering.renderFrameInterval = _previousRenderFrameInterval;

        foreach (Camera camera in _disabledCameras)
        {
            if (camera != null)
            {
                camera.enabled = true;
            }
        }

        foreach (Canvas canvas in _disabledCanvases)
        {
            if (canvas != null)
            {
                canvas.enabled = true;
            }
        }

        _disabledCameras.Clear();
        _disabledCanvases.Clear();
        Log("Restored cameras/canvases after refocus.");
    }

    private void DisableActiveRenderingComponents()
    {
        foreach (Camera camera in Resources.FindObjectsOfTypeAll<Camera>())
        {
            if (!ShouldManage(camera) || !camera.enabled || _disabledCameras.Contains(camera))
            {
                continue;
            }

            camera.enabled = false;
            _disabledCameras.Add(camera);
        }

        foreach (Canvas canvas in Resources.FindObjectsOfTypeAll<Canvas>())
        {
            if (!ShouldManage(canvas) || !canvas.enabled || _disabledCanvases.Contains(canvas))
            {
                continue;
            }

            canvas.enabled = false;
            _disabledCanvases.Add(canvas);
        }
    }

    private static bool ShouldManage(Behaviour behaviour)
    {
        return behaviour != null
            && behaviour.gameObject != null
            && behaviour.gameObject.scene.IsValid();
    }
}
