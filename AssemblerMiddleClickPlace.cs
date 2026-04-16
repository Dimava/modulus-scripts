using HarmonyLib;
using Logic.Assembling;
using MelonLoader;
using Presentation.UI.OperatorUIs;
using UnityEngine;
using UnityEngine.InputSystem;
using Utils;

/// <summary>
/// Middle-click while the Assembler is open:
///   • Zone empty (first shape)  → instantly place it at the zone center.
///   • Zone has shapes already   → steal it into drag mode at the cursor so you can
///                                  position it relative to what's already there.
/// </summary>
public static class AssemblerMiddleClickPlace
{
    private static GameObject _go;

    public static void OnLoad()
    {
        if (_go != null) GameObject.Destroy(_go);
        _go = new GameObject("__AssemblerMiddleClickPlace__");
        GameObject.DontDestroyOnLoad(_go);
        _go.AddComponent<AssemblerMiddleClickBehaviour>();
        MelonLogger.Msg("[AssemblerMiddleClickPlace] Loaded.");
    }

    public static void OnUnload()
    {
        if (_go != null) { GameObject.Destroy(_go); _go = null; }
        MelonLogger.Msg("[AssemblerMiddleClickPlace] Unloaded.");
    }
}

public class AssemblerMiddleClickBehaviour : MonoBehaviour
{
    private void Update()
    {
        if (Mouse.current == null) return;
        if (!Mouse.current.middleButton.wasPressedThisFrame) return;

        var assemblerUI = FindObjectOfType<AssemblerUI>();
        if (assemblerUI == null || !assemblerUI.isActiveAndEnabled) return;

        var tUI = Traverse.Create(assemblerUI);
        var assembleStack = tUI.Field("_assembleStack").GetValue<AssembleStack>();
        var assembleZone  = tUI.Field("_assembleZone").GetValue<AssembleZone>();
        if (assembleStack == null || assembleZone == null) return;

        // Don't interrupt an in-progress drag
        if (assembleZone.IsHoldingShape) return;

        // Find the first stack slot that still has its shape on the stack
        var stackShapesRaw = Traverse.Create(assembleStack).Field("_stackShapes").GetValue();
        if (stackShapesRaw == null) return;

        var stackArr = (System.Array)stackShapesRaw;
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
            // Subsequent shape: pre-position at the cursor so it doesn't flash at the
            // stack's world position before DragShape runs next frame.
            var tZone   = Traverse.Create(assembleZone);
            var camera  = tZone.Field("_camera").GetValue<Camera>();
            var planeTf = tZone.Field("_plane").GetValue<Transform>();
            if (camera != null && planeTf != null)
            {
                Ray ray = camera.ScreenPointToRay(Mouse.current.position.ReadValue());
                if (new UnityEngine.Plane(Vector3.up, planeTf.position).Raycast(ray, out float enter))
                {
                    Vector3 pos = ray.GetPoint(enter);
                    pos.y = Mathf.Max(pos.y, assembleZone.transform.position.y);
                    held.ShapeLoader.Position = ShapeUtils.SnapPositionToVoxelGrid(pos, held.ShapeLoader.Shape);
                    tZone.Method("MoveShapeToNotOverlap", held).GetValue();
                }
            }
            // Left-click release will drop it via the normal StopHoldingShape flow.
        }
    }
}
