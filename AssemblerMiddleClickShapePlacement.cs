using HarmonyLib;
using Logic.Assembling;
using Presentation.UI.OperatorUIs;
using Presentation.UI.OperatorUIs.InsideOperatorUIs;
using ScriptEngine;
using UnityEngine;
using UnityEngine.InputSystem;
using Utils;

/// <summary>
/// Middle-click while the Assembler is open:
///   • Zone empty (first shape)  → instantly place it at the zone center.
///   • Zone has shapes already   → steal it into drag mode at the cursor so you can
///                                  position it relative to what's already there.
/// </summary>
[ScriptEntry]
public sealed class AssemblerMiddleClickShapePlacement : ScriptMod
{
    protected override void OnUpdate()
    {
        if (Mouse.current == null) return;
        if (!Mouse.current.middleButton.wasPressedThisFrame) return;

        var assemblerUI = FindActiveObjectOfType<AssemblerUI>();
        if (assemblerUI == null || !assemblerUI.isActiveAndEnabled)
        {
            _ = TryPressReady<CutterUI>()
             || TryPressReady<StamperUI>()
             || TryPressReady<StamperMK2UI>();
            return;
        }

        var tUI = Traverse.Create(assemblerUI);
        var assembleStack = tUI.Field("_assembleStack").GetValue<AssembleStack>();
        var assembleZone  = tUI.Field("_assembleZone").GetValue<AssembleZone>();
        if (assembleStack == null || assembleZone == null) return;

        // Find the first stack slot that still has its shape on the stack
        var stackShapesRaw = Traverse.Create(assembleStack).Field("_stackShapes").GetValue();
        if (stackShapesRaw == null) return;

        var stackArr = (System.Array)stackShapesRaw;

        // Third middle-click: if we're already dragging the last remaining shape,
        // place it and accept the recipe.
        if (assembleZone.IsHoldingShape)
        {
            if (!HasAnyShapeOnStack(stackArr))
            {
                var heldShape = assembleZone.CurrentHoldingShape;
                if (heldShape != null)
                {
                    Traverse.Create(assembleZone).Method("StopHoldingShape", heldShape).GetValue();
                    PressReadyIfInteractable(assemblerUI);
                }
            }
            return;
        }

        // Plain middle-click should also behave like the regular accept button
        // when there is nothing left to take from the stack.
        if (!HasAnyShapeOnStack(stackArr))
        {
            PressReadyIfInteractable(assemblerUI);
            return;
        }

        ClickableShape firstShape = null;
        for (int i = 0; i < stackArr.Length; i++)
        {
            var elem = Traverse.Create(stackArr.GetValue(i));
            if (!elem.Field("IsOnStack").GetValue<bool>()) continue;
            var shape = elem.Field("Shape").GetValue<ClickableShape>();
            if (shape == null) continue;
            firstShape = shape;
            break;
        }
        if (firstShape == null) return;

        bool zoneIsEmpty = assembleZone.PlacedShapes.Count == 0;

        // Take from stack → fires OnTakeStackShape → AddShapeToZone → HoldShape (synchronous).
        // Passing firstShape.transform.position gives a zero holding offset.
        assembleStack.TakeShapeFromStack(firstShape, firstShape.transform.position);

        if (!assembleZone.IsHoldingShape) return;
        var held = assembleZone.CurrentHoldingShape;

        if (zoneIsEmpty)
        {
            // First shape: drop it at the zone center.
            held.ShapeLoader.Position = ShapeUtils.SnapPositionToVoxelGrid(
                assembleZone.transform.position, held.ShapeLoader.Shape);
            Traverse.Create(assembleZone).Method("StopHoldingShape", held).GetValue();
        }
        else
        {
            var tZone = Traverse.Create(assembleZone);
            var planeTf = tZone.Field("_plane").GetValue<Transform>();

            // Use the game's built-in drag path. The drag plane appears to sit above
            // the assembler floor, so compensate only in Y and let DragShape handle
            // cursor projection, clamping, snapping, and overlap resolution.
            float floorOffsetY = planeTf != null
                ? assembleZone.transform.position.y - planeTf.position.y
                : 0f;
            tZone.Field("_holdingOffset").SetValue(new Vector3(0f, floorOffsetY, 0f));
            tZone.Method("DragShape").GetValue();
        }
    }

    private static bool HasAnyShapeOnStack(System.Array stackArr)
    {
        for (int i = 0; i < stackArr.Length; i++)
        {
            var elem = Traverse.Create(stackArr.GetValue(i));
            if (elem.Field("IsOnStack").GetValue<bool>())
                return true;
        }

        return false;
    }

    private static void PressReadyIfInteractable(AssemblerUI assemblerUI)
    {
        var readyButton = Traverse.Create(assemblerUI).Field("_readyButton").GetValue<MachineButton>();
        if (readyButton == null || !readyButton.Interactable)
            return;

        readyButton.OnPointerDown(null);
        readyButton.OnPointerUp(null);
    }

    private static bool TryPressReady<T>() where T : MonoBehaviour
    {
        T ui = FindActiveObjectOfType<T>();
        if (ui == null)
            return false;

        var readyButton = Traverse.Create(ui).Field("_readyButton").GetValue<MachineButton>();
        if (readyButton == null || !readyButton.Interactable)
            return true;

        readyButton.OnPointerDown(null);
        readyButton.OnPointerUp(null);
        return true;
    }


}
