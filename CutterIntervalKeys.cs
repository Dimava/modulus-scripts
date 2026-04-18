using HarmonyLib;
using Presentation.UI.OperatorUIs;
using Presentation.UI.OperatorUIs.InsideOperatorUIs;
using ScriptEngine;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Press 1/2/3/4 while the cutter interior UI is open to select the matching cut interval.
/// Press E to accept (Ready button) in the Cutter, Assembler, Stamper, or StamperMK2 UIs -
/// only when the Ready button is actually interactable (i.e. the machine is configured).
/// Press Q to reset in any of those UIs - only when the Reset button is interactable.
/// </summary>
[ScriptEntry]
public sealed class CutterIntervalKeys : ScriptMod
{
    private static readonly Key[] IntervalKeys =
    {
        Key.Digit1,
        Key.Digit2,
        Key.Digit3,
        Key.Digit4,
    };

    protected override void OnUpdate()
    {
        if (Keyboard.current == null)
            return;

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
            _ = TryPressButton<CutterUI>("_readyButton")
             || TryPressButton<AssemblerUI>("_readyButton")
             || TryPressButton<StamperUI>("_readyButton")
             || TryPressButton<StamperMK2UI>("_readyButton");
        }

        if (Keyboard.current[Key.Q].wasPressedThisFrame)
        {
            _ = TryPressButton<CutterUI>("_resetButton")
             || TryPressButton<AssemblerUI>("_resetButton")
             || TryPressButton<StamperUI>("_resetButton")
             || TryPressButton<StamperMK2UI>("_resetButton");
        }
    }

    private static bool TryPressButton<T>(string buttonFieldName) where T : MonoBehaviour
    {
        T ui = FindObjectOfType<T>();
        if (ui == null)
            return false;

        var button = Traverse.Create(ui).Field(buttonFieldName).GetValue<MachineButton>();
        if (button == null || !button.Interactable)
            return true;

        button.OnPointerDown(null);
        button.OnPointerUp(null);
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
