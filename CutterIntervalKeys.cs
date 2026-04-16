using HarmonyLib;
using MelonLoader;
using Presentation.UI.OperatorUIs;
using Presentation.UI.OperatorUIs.InsideOperatorUIs;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Press 1/2/3/4 while the cutter interior UI is open to select the matching cut interval.
/// Press E to accept (Ready button) in the Cutter, Assembler, Stamper, or StamperMK2 UIs —
/// only when the Ready button is actually interactable (i.e. the machine is configured).
/// Press R to reset in any of those UIs — only when the Reset button is interactable.
/// </summary>
public static class CutterIntervalKeys
{
    private static GameObject _go;

    public static void OnLoad()
    {
        if (_go != null) GameObject.Destroy(_go);
        _go = new GameObject("CutterIntervalKeyHandler");
        Object.DontDestroyOnLoad(_go);
        _go.AddComponent<CutterIntervalKeyHandler>();
        MelonLogger.Msg("[CutterIntervalKeys] Loaded.");
    }

    public static void OnUnload()
    {
        if (_go != null) { GameObject.Destroy(_go); _go = null; }
        MelonLogger.Msg("[CutterIntervalKeys] Unloaded.");
    }
}

public class CutterIntervalKeyHandler : MonoBehaviour
{
    private static readonly Key[] IntervalKeys =
    {
        Key.Digit1,
        Key.Digit2,
        Key.Digit3,
        Key.Digit4,
    };

    private void Update()
    {
        if (Keyboard.current == null)
            return;

        // FindObjectOfType only finds active (enabled) components,
        // so these are non-null only while the cutter menu is open.
        CutterUIInterval interval = FindObjectOfType<CutterUIInterval>();
        if (interval != null)
        {
            for (int i = 0; i < IntervalKeys.Length; i++)
            {
                if (Keyboard.current[IntervalKeys[i]].wasPressedThisFrame)
                {
                    TrySetInterval(interval, i + 1);
                    return;
                }
            }
        }

        if (Keyboard.current[Key.E].wasPressedThisFrame)
        {
            // Try each UI type in turn; stop at the first one that's open and ready.
            _ = TryPressReady<CutterUI>()
             || TryPressReady<AssemblerUI>()
             || TryPressReady<StamperUI>()
             || TryPressReady<StamperMK2UI>();
        }

        if (Keyboard.current[Key.R].wasPressedThisFrame)
        {
            // Reset only fires when the Reset button is interactable.
            _ = TryPressReset<CutterUI>()
             || TryPressReset<AssemblerUI>()
             || TryPressReset<StamperUI>()
             || TryPressReset<StamperMK2UI>();
        }
    }

    /// <summary>
    /// If a UI of type T is currently open and its Ready button is interactable, click it.
    /// Returns true if the UI was found (regardless of whether Ready fired).
    /// </summary>
    private static bool TryPressReady<T>() where T : MonoBehaviour
    {
        T ui = FindObjectOfType<T>();
        if (ui == null)
            return false;

        // _readyButton is declared on InsideOperatorUI; Traverse searches inherited fields.
        var readyButton = Traverse.Create(ui).Field("_readyButton").GetValue<MachineButton>();
        if (readyButton == null || !readyButton.Interactable)
            return true; // UI is open but not ready — consume the search, don't fall through.

        Traverse.Create(ui).Method("Ready", new object[] { 0 }).GetValue();
        return true;
    }

    /// <summary>
    /// If a UI of type T is currently open and its Reset button is interactable, click it.
    /// Returns true if the UI was found (regardless of whether Reset fired).
    /// </summary>
    private static bool TryPressReset<T>() where T : MonoBehaviour
    {
        T ui = FindObjectOfType<T>();
        if (ui == null)
            return false;

        // _resetButton is declared on InsideOperatorUI; Traverse searches inherited fields.
        var resetButton = Traverse.Create(ui).Field("_resetButton").GetValue<MachineButton>();
        if (resetButton == null || !resetButton.Interactable)
            return true; // UI is open but reset is disabled — consume, don't fall through.

        Traverse.Create(ui).Method("Reset", new object[] { 0 }).GetValue();
        return true;
    }

    private static void TrySetInterval(CutterUIInterval interval, int intervalNumber)
    {
        var buttons = Traverse.Create(interval)
            .Field("_cutIntervalButtons")
            .GetValue<MachineButton[]>();

        if (buttons == null || intervalNumber < 1 || intervalNumber > buttons.Length)
            return;

        MachineButton button = buttons[intervalNumber - 1];
        if (button == null || !button.Interactable)
            return;

        interval.OnCuttingIntervalClicked(intervalNumber, button);
    }
}
