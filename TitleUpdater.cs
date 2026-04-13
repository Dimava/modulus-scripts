using System;
using System.Runtime.InteropServices;
using MelonLoader;
using UnityEngine;
off
/// <summary>
/// Sample ScriptEngine script.
/// Updates the window title and logs to console every second.
/// OnLoad() is called on load and on hot-reload.
/// OnUnload() is called before reload/removal.
/// </summary>
public static class TitleUpdaterScript
{
    static GameObject? _go;

    public static void OnLoad()
    {
        // Clean up previous instance if hot-reloaded
        if (_go != null) GameObject.Destroy(_go);

        _go = new GameObject("__TitleUpdater__");
        GameObject.DontDestroyOnLoad(_go);
        _go.AddComponent<TitleUpdater>();

        MelonLogger.Msg("[TitleUpdater] Loaded!");
    }

    public static void OnUnload()
    {
        if (_go != null)
        {
            GameObject.Destroy(_go);
            _go = null;
        }
        MelonLogger.Msg("[TitleUpdater] Unloaded.");
    }
}

public class TitleUpdater : MonoBehaviour
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetWindowText(IntPtr hWnd, string lpString);

    float _logTimer = 0f;
    int _frameCount = 0;

    void Update()
    {
        _frameCount++;
        float fps = 1f / Time.unscaledDeltaTime;

        // Update window title every frame
        string title = $"Modulus | FPS: {fps:F0} | Frame: {_frameCount} | Time: {Time.time:F1}s";
        var hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
        SetWindowText(hwnd, title);

        // Log to MelonLoader console once per second
        _logTimer += Time.unscaledDeltaTime;
        if (_logTimer >= 1f)
        {
            _logTimer = 0f;
            MelonLogger.Msg($"[TitleUpdater] FPS: {fps:F0} | Frame: {_frameCount} | Time: {Time.time:F1}s");
        }
    }
}
