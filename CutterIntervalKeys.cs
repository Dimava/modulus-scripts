using HarmonyLib;
using Presentation.UI.OperatorUIs;
using Presentation.UI.OperatorUIs.InsideOperatorUIs;
using ScriptEngine;
using UnityEngine;

/// <summary>
/// Press 1/2/3/4 while the cutter interior UI is open to select the matching cut interval.
/// Press E to accept (Ready button) in the Cutter, Assembler, Stamper, or StamperMK2 UIs -
/// only when the Ready button is actually interactable (i.e. the machine is configured).
/// Press Q to reset in any of those UIs - only when the Reset button is interactable.
/// </summary>
[ScriptEntry]
public sealed class CutterIntervalKeys : ScriptMod
{
    protected override void OnEnable()
    {
        BindKey("keyCutInterval1", "1");
        BindKey("keyCutInterval2", "2");
        BindKey("keyCutInterval3", "3");
        BindKey("keyCutInterval4", "4");
        BindKey("keyAccept", "E");
        BindKey("keyReset", "Q");
    }

    protected override void OnUpdate()
    {
        CutterUIInterval interval = FindActiveObjectOfType<CutterUIInterval>();
        if (interval != null)
        {
            if (WasPressed("keyCutInterval1"))
            {
                TrySetInterval(interval, 1);
                return;
            }

            if (WasPressed("keyCutInterval2"))
            {
                TrySetInterval(interval, 2);
                return;
            }

            if (WasPressed("keyCutInterval3"))
            {
                TrySetInterval(interval, 3);
                return;
            }

            if (WasPressed("keyCutInterval4"))
            {
                TrySetInterval(interval, 4);
                return;
            }
        }

        if (WasPressed("keyAccept"))
        {
            _ = TryPressButton<CutterUI>("_readyButton")
             || TryPressButton<AssemblerUI>("_readyButton")
             || TryPressButton<StamperUI>("_readyButton")
             || TryPressButton<StamperMK2UI>("_readyButton");
        }

        if (WasPressed("keyReset"))
        {
            _ = TryPressButton<CutterUI>("_resetButton")
             || TryPressButton<AssemblerUI>("_resetButton")
             || TryPressButton<StamperUI>("_resetButton")
             || TryPressButton<StamperMK2UI>("_resetButton");
        }
    }

    private static bool TryPressButton<T>(string buttonFieldName) where T : MonoBehaviour
    {
        T ui = FindActiveObjectOfType<T>();
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
