using System.Collections.Generic;
using Data.Buildings;
using Data.FactoryFloor;
using Data.FactoryFloor.Buildings;
using Data.FactoryFloor.Resources;
using Data.Minimap;
using HarmonyLib;
using Presentation.FactoryFloor;
using ScriptEngine;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Draws produced item icons on the minimap for large buildings that currently have cranes.
/// </summary>
[ScriptEntry]
public sealed class MinimapLargeBuildingOutputIcons : ScriptMod
{
    private static MinimapLargeBuildingOutputIcons _instance;

    protected override void OnEnable()
    {
        _instance = this;
        RefreshAllMinimaps("loaded");
    }

    protected override void OnDisable()
    {
        foreach (MinimapLargeBuildingOutputIconController controller in Resources.FindObjectsOfTypeAll<MinimapLargeBuildingOutputIconController>())
        {
            if (controller != null)
            {
                UnityEngine.Object.Destroy(controller);
            }
        }

        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }
    }

    internal static void Attach(MinimapUI minimapUI)
    {
        if (minimapUI == null)
        {
            return;
        }

        MinimapLargeBuildingOutputIconController controller = minimapUI.GetComponent<MinimapLargeBuildingOutputIconController>();
        if (controller == null)
        {
            controller = minimapUI.gameObject.AddComponent<MinimapLargeBuildingOutputIconController>();
        }

        controller.Setup(minimapUI);
        controller.Refresh();
    }

    internal static void LogInfo(string message)
    {
        _instance?.Log(message);
    }

    private void RefreshAllMinimaps(string reason)
    {
        int count = 0;
        foreach (MinimapUI minimapUI in Resources.FindObjectsOfTypeAll<MinimapUI>())
        {
            if (minimapUI == null)
            {
                continue;
            }

            Attach(minimapUI);
            count++;
        }

        Log($"Minimap building output icons {reason}; refreshed {count} minimap UI instance(s).");
    }
}

public sealed class MinimapLargeBuildingOutputIconController : MonoBehaviour
{
    private const float RefreshInterval = 1f;
    private static readonly Vector2 MinimapCellCenterCorrection = new Vector2(0.5f, 0.5f);

    private readonly List<GameObject> _icons = new List<GameObject>();
    private MinimapUI _minimapUI;
    private GameObject _rootObject;
    private RectTransform _rootRect;
    private float _nextRefreshTime;
    private int _lastIconCount = -1;

    public void Setup(MinimapUI minimapUI)
    {
        _minimapUI = minimapUI;
    }

    private void LateUpdate()
    {
        if (Time.unscaledTime < _nextRefreshTime)
        {
            return;
        }

        _nextRefreshTime = Time.unscaledTime + RefreshInterval;
        Refresh();
    }

    public void Refresh()
    {
        RectTransform content = ResolveContent();
        MinimapData minimapData = ResolveMinimapData();
        if (content == null || minimapData == null)
        {
            ClearIcons();
            return;
        }

        EnsureRoot(content);
        ClearIcons();

        int count = 0;
        foreach (FactoryObjectView view in Resources.FindObjectsOfTypeAll<FactoryObjectView>())
        {
            if (!TryGetMarker(view, minimapData, out Sprite sprite, out Vector2 position, out Vector2 size))
            {
                continue;
            }

            CreateIcon(sprite, position, size);
            count++;
        }

        if (count != _lastIconCount)
        {
            _lastIconCount = count;
            MinimapLargeBuildingOutputIcons.LogInfo($"Showing {count} crane building output icon(s) on minimap.");
        }
    }

    private void OnDestroy()
    {
        ClearIcons();
        if (_rootObject != null)
        {
            UnityEngine.Object.Destroy(_rootObject);
            _rootObject = null;
            _rootRect = null;
        }
    }

    private void EnsureRoot(RectTransform content)
    {
        if (_rootObject != null && _rootObject.transform.parent == content)
        {
            _rootObject.transform.SetAsLastSibling();
            return;
        }

        if (_rootObject != null)
        {
            UnityEngine.Object.Destroy(_rootObject);
        }

        _rootObject = new GameObject("DimavaMinimapLargeBuildingOutputIcons", typeof(RectTransform));
        _rootObject.transform.SetParent(content, false);
        _rootObject.transform.SetAsLastSibling();

        _rootRect = _rootObject.GetComponent<RectTransform>();
        _rootRect.anchorMin = Vector2.zero;
        _rootRect.anchorMax = Vector2.one;
        _rootRect.offsetMin = Vector2.zero;
        _rootRect.offsetMax = Vector2.zero;
        _rootRect.pivot = new Vector2(0.5f, 0.5f);
    }

    private void ClearIcons()
    {
        for (int i = _icons.Count - 1; i >= 0; i--)
        {
            if (_icons[i] != null)
            {
                UnityEngine.Object.Destroy(_icons[i]);
            }
        }
        _icons.Clear();
    }

    private void CreateIcon(Sprite sprite, Vector2 position, Vector2 size)
    {
        GameObject iconObject = new GameObject("BuildingOutputIcon_" + sprite.name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        iconObject.transform.SetParent(_rootObject.transform, false);
        iconObject.transform.SetAsLastSibling();

        RectTransform rect = iconObject.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = size;
        rect.anchoredPosition = position;

        Image image = iconObject.GetComponent<Image>();
        image.sprite = sprite;
        image.preserveAspect = true;
        image.raycastTarget = false;
        image.color = Color.white;

        _icons.Add(iconObject);
    }

    private bool TryGetMarker(FactoryObjectView view, MinimapData minimapData, out Sprite sprite, out Vector2 position, out Vector2 size)
    {
        sprite = null;
        position = Vector2.zero;
        size = Vector2.zero;

        if (view == null || view.FactoryObject == null || !view.gameObject.scene.IsValid())
        {
            return false;
        }

        FactoryObject factoryObject = view.FactoryObject;
        if (!factoryObject.TryGetFactoryObjectBehaviour<BuildingCranesBehaviour>(out BuildingCranesBehaviour cranesBehaviour) ||
            cranesBehaviour.Cranes == null ||
            cranesBehaviour.Cranes.Count == 0)
        {
            return false;
        }

        if (!factoryObject.TryGetFactoryObjectBehaviour<BuildingBehaviour>(out BuildingBehaviour buildingBehaviour) ||
            buildingBehaviour.BuildingObjectData == null ||
            buildingBehaviour.BuildingObjectData.ResourceOutputs == null ||
            buildingBehaviour.BuildingObjectData.ResourceOutputs.Count == 0)
        {
            return false;
        }

        ResourceDataSO output = buildingBehaviour.BuildingObjectData.ResourceOutputs[0].ResourceData;
        NonShapeResourceDataSO nonShapeOutput = output as NonShapeResourceDataSO;
        if (nonShapeOutput == null || nonShapeOutput.Sprite == null)
        {
            return false;
        }

        sprite = nonShapeOutput.Sprite;
        position = minimapData.WorldPosToLocalPos(GetFactoryObjectCenter(factoryObject)) + MinimapCellCenterCorrection;
        size = GetFactoryObjectFootprintSize(factoryObject);
        return true;
    }

    private static Vector3 GetFactoryObjectCenter(FactoryObject factoryObject)
    {
        if (factoryObject.OccupiedPositions == null || factoryObject.OccupiedPositions.Count == 0)
        {
            Vector3Int fallback = factoryObject.Position;
            return new Vector3(fallback.x, 0f, fallback.z);
        }

        float x = 0f;
        float z = 0f;
        for (int i = 0; i < factoryObject.OccupiedPositions.Count; i++)
        {
            Vector3Int pos = factoryObject.OccupiedPositions[i];
            x += pos.x;
            z += pos.z;
        }

        float count = factoryObject.OccupiedPositions.Count;
        return new Vector3(x / count, 0f, z / count);
    }

    private static Vector2 GetFactoryObjectFootprintSize(FactoryObject factoryObject)
    {
        if (factoryObject.OccupiedPositions == null || factoryObject.OccupiedPositions.Count == 0)
        {
            return Vector2.one;
        }

        int minX = int.MaxValue;
        int maxX = int.MinValue;
        int minZ = int.MaxValue;
        int maxZ = int.MinValue;
        for (int i = 0; i < factoryObject.OccupiedPositions.Count; i++)
        {
            Vector3Int pos = factoryObject.OccupiedPositions[i];
            minX = Mathf.Min(minX, pos.x);
            maxX = Mathf.Max(maxX, pos.x);
            minZ = Mathf.Min(minZ, pos.z);
            maxZ = Mathf.Max(maxZ, pos.z);
        }

        return new Vector2(Mathf.Max(1, maxX - minX + 1), Mathf.Max(1, maxZ - minZ + 1));
    }

    private RectTransform ResolveContent()
    {
        MinimapScrollViewControls scrollControls = _minimapUI != null
            ? _minimapUI.GetComponentInChildren<MinimapScrollViewControls>(includeInactive: true)
            : null;
        if (scrollControls == null)
        {
            return null;
        }

        return Traverse.Create(scrollControls).Field("_content").GetValue<RectTransform>();
    }

    private MinimapData ResolveMinimapData()
    {
        if (_minimapUI == null)
        {
            return null;
        }

        MinimapData data = _minimapUI.MinimapData;
        if (data != null)
        {
            return data;
        }

        return Traverse.Create(_minimapUI).Field("_minimapData").GetValue<MinimapData>();
    }
}

[HarmonyPatch(typeof(MinimapUI), "Awake")]
static class MinimapLargeBuildingOutputIcons_MinimapUI_Awake_Patch
{
    static void Postfix(MinimapUI __instance)
    {
        MinimapLargeBuildingOutputIcons.Attach(__instance);
    }
}

[HarmonyPatch(typeof(MinimapUI), "ShowPanel")]
static class MinimapLargeBuildingOutputIcons_MinimapUI_ShowPanel_Patch
{
    static void Postfix(MinimapUI __instance)
    {
        MinimapLargeBuildingOutputIcons.Attach(__instance);
    }
}

[HarmonyPatch(typeof(MinimapUI), "OnMinimapDataCreated")]
static class MinimapLargeBuildingOutputIcons_MinimapUI_OnMinimapDataCreated_Patch
{
    static void Postfix(MinimapUI __instance)
    {
        MinimapLargeBuildingOutputIcons.Attach(__instance);
    }
}
