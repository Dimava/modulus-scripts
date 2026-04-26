using System.Collections.Generic;
using System.Reflection;
using Data.Buildings;
using Data.FactoryFloor;
using Data.FactoryFloor.Behaviours;
using Data.FactoryFloor.Resources;
using Data.Shapes;
using Presentation.Buildings;
using Presentation.FactoryFloor;
using ScriptEngine;
using UnityEngine;

/// <summary>
/// Shows the configured first output resource as a small floating world object above machines and large buildings.
/// </summary>
[ScriptEntry]
public sealed class FloatingOutputShapePreview : ScriptMod
{
    private static readonly Quaternion PreviewShapeCameraRotation = Quaternion.Euler(33.51f, 315f, 0f);
    private const float RefreshInterval = 0.5f;
    private const float FloatHeight = 2.65f;
    private const float LargeBuildingClearance = 0.8f;
    private const float BobAmount = 0.08f;
    private const float SpinDegreesPerSecond = 35f;
    private const float AssemblerInputBias = 0.5f;
    internal static bool UseDiagonalProjection = true;
    internal static int ShapeScalePercent = 120;
    internal static int LargeBuildingShapeScalePercent = 480;

    private readonly object _trackedLock = new object();
    private readonly Dictionary<int, FloatingShape> _floatingByCreatedId = new Dictionary<int, FloatingShape>();
    private readonly Dictionary<int, TrackedMachine> _trackedByCreatedId = new Dictionary<int, TrackedMachine>();
    private float _nextRefreshTime;
    private bool _subscribedToViewManager;
    private bool _trackedExistingViews;

    protected override void OnEnable()
    {
        ReloadConfig();
        Log("Floating output shapes enabled.");
        TrySubscribeToViewManager();
    }

    protected override void OnConfigChanged()
    {
        ReloadConfig();
    }

    private void ReloadConfig()
    {
        UseDiagonalProjection = BindBool("UseDiagonalProjection", true).Value;
        ShapeScalePercent = BindInt("ShapeScalePercent", 120).Value;
        LargeBuildingShapeScalePercent = BindInt("LargeBuildingShapeScalePercent", 480).Value;
    }

    protected override void OnDisable()
    {
        UnsubscribeFromViewManager();
        ClearAll();
    }

    protected override void OnUpdate()
    {
        if (!_subscribedToViewManager)
            TrySubscribeToViewManager();

        if (Time.unscaledTime >= _nextRefreshTime)
        {
            _nextRefreshTime = Time.unscaledTime + RefreshInterval;
            RefreshTrackedFloatingShapes();
        }

        AnimateFloatingShapes();
    }

    private void TrySubscribeToViewManager()
    {
        FactoryObjectViewManager manager = FactoryObjectViewManager.Instance;
        if (manager == null)
            return;

        manager.OnFactoryObjectViewCreated += OnFactoryObjectViewCreated;
        manager.OnFactoryObjectViewRemoved += OnFactoryObjectViewRemoved;
        _subscribedToViewManager = true;

        if (!_trackedExistingViews)
        {
            TrackExistingViews(manager);
            _trackedExistingViews = true;
        }
    }

    private void UnsubscribeFromViewManager()
    {
        FactoryObjectViewManager manager = FactoryObjectViewManager.Instance;
        if (manager != null && _subscribedToViewManager)
        {
            manager.OnFactoryObjectViewCreated -= OnFactoryObjectViewCreated;
            manager.OnFactoryObjectViewRemoved -= OnFactoryObjectViewRemoved;
        }

        _subscribedToViewManager = false;
        _trackedExistingViews = false;
        lock (_trackedLock)
            _trackedByCreatedId.Clear();
    }

    private void TrackExistingViews(FactoryObjectViewManager manager)
    {
        FieldInfo field = typeof(FactoryObjectViewManager).GetField("_createdObjects", BindingFlags.Instance | BindingFlags.NonPublic);
        var createdObjects = field?.GetValue(manager) as Dictionary<int, FactoryObjectView>;
        if (createdObjects == null)
            return;

        foreach (FactoryObjectView view in createdObjects.Values)
            TrackFactoryObjectView(view, view?.FactoryObject);
    }

    private void OnFactoryObjectViewCreated(FactoryObjectView view, FactoryObject factoryObject)
    {
        TrackFactoryObjectView(view, factoryObject);
    }

    private void OnFactoryObjectViewRemoved(FactoryObjectView view, FactoryObject factoryObject)
    {
        if (factoryObject == null)
            return;

        RemoveTrackedMachine(factoryObject.CreatedId);
    }

    private void TrackFactoryObjectView(FactoryObjectView view, FactoryObject factoryObject)
    {
        if (view == null || factoryObject == null)
            return;

        ResourceHolderBehaviour behaviour = null;
        Transform target = null;
        List<Renderer> heightRenderers = null;

        AssemblerView assemblerView = GetViewComponent<AssemblerView>(view);
        if (assemblerView != null)
        {
            behaviour = factoryObject.GetFactoryObjectBehaviour<AssemblerBehaviour>();
            target = assemblerView.transform;
        }

        CutterView cutterView = GetViewComponent<CutterView>(view);
        if (target == null && cutterView != null)
        {
            behaviour = factoryObject.GetFactoryObjectBehaviour<CutterBehaviour>();
            target = cutterView.transform;
        }

        StamperView stamperView = GetViewComponent<StamperView>(view);
        if (target == null && stamperView != null)
        {
            behaviour = factoryObject.GetFactoryObjectBehaviour<StamperBehaviour>();
            target = stamperView.transform;
        }

        StamperMK2View stamperMk2View = GetViewComponent<StamperMK2View>(view);
        if (target == null && stamperMk2View != null)
        {
            behaviour = factoryObject.GetFactoryObjectBehaviour<StamperMK2Behaviour>();
            target = stamperMk2View.transform;
        }

        bool usesRendererHeight = false;

        BuildingView buildingView = GetViewComponent<BuildingView>(view);
        if (target == null && buildingView != null)
        {
            behaviour = factoryObject.GetFactoryObjectBehaviour<BuildingBehaviour>();
            target = buildingView.transform;
            usesRendererHeight = true;
            heightRenderers = GetCullableRenderers(buildingView);
        }

        GNNGateView gnnGateView = GetViewComponent<GNNGateView>(view);
        if (target == null && gnnGateView != null)
        {
            behaviour = factoryObject.GetFactoryObjectBehaviour<GNNGateBehaviour>();
            target = gnnGateView.transform;
            usesRendererHeight = true;
            heightRenderers = GetCullableRenderers(gnnGateView);
        }

        if (target == null || behaviour == null)
            return;

        var trackedMachine = new TrackedMachine
        {
            FactoryObject = factoryObject,
            Behaviour = behaviour,
            Target = target,
            UsesRendererHeight = usesRendererHeight,
            HeightRenderers = heightRenderers,
            TargetOffset = GetTrackedOffset(target, factoryObject, behaviour, usesRendererHeight, heightRenderers)
        };

        lock (_trackedLock)
            _trackedByCreatedId[factoryObject.CreatedId] = trackedMachine;
    }

    private static List<Renderer> GetCullableRenderers(Component view)
    {
        if (view == null)
            return null;

        FieldInfo field = view.GetType().GetField("_cullableRenderers", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(view) as List<Renderer>;
    }

    private static T GetViewComponent<T>(FactoryObjectView view) where T : Component
    {
        T component = view.GetComponent<T>();
        if (component != null)
            return component;

        component = view.GetComponentInChildren<T>(includeInactive: true);
        if (component != null)
            return component;

        return view.GetComponentInParent<T>();
    }

    private void RemoveTrackedMachine(int createdId)
    {
        lock (_trackedLock)
            _trackedByCreatedId.Remove(createdId);

        RemoveFloatingShape(createdId);
    }

    private void RefreshTrackedFloatingShapes()
    {
        if (ResourceViewManager.Instance == null)
            return;

        var trackedSnapshot = new List<KeyValuePair<int, TrackedMachine>>();
        lock (_trackedLock)
        {
            foreach (KeyValuePair<int, TrackedMachine> pair in _trackedByCreatedId)
                trackedSnapshot.Add(pair);
        }

        var toRemove = new List<int>();
        foreach (KeyValuePair<int, TrackedMachine> pair in trackedSnapshot)
        {
            lock (_trackedLock)
            {
                if (!_trackedByCreatedId.ContainsKey(pair.Key))
                    continue;
            }

            TrackedMachine tracked = pair.Value;
            if (tracked.Target == null || tracked.FactoryObject == null || tracked.Behaviour == null)
            {
                toRemove.Add(pair.Key);
                continue;
            }

            try
            {
                UpsertFloatingShape(pair.Key, tracked);
            }
            catch
            {
                toRemove.Add(pair.Key);
            }
        }

        foreach (int createdId in toRemove)
            RemoveTrackedMachine(createdId);
    }

    private void UpsertFloatingShape(int createdId, TrackedMachine tracked)
    {
        Resource outputResource = GetFirstOutputResource(tracked.Behaviour);
        if (outputResource == null)
        {
            RemoveFloatingShape(createdId);
            return;
        }

        if (!_floatingByCreatedId.TryGetValue(createdId, out FloatingShape floating))
        {
            floating = new FloatingShape();
            _floatingByCreatedId[createdId] = floating;
        }

        if (floating.ResourceView == null || !IsSameResource(floating, outputResource))
        {
            ReturnResourceView(floating.ResourceView);
            floating.ResourceView = ResourceViewManager.Instance.CreateNewResourceView(outputResource);
            floating.ResourceView.name = "FloatingOutputShape";
            floating.ResourceData = outputResource.Data;
            floating.ShapeData = (outputResource as ShapeResource)?.ShapeData;
        }

        floating.Target = tracked.Target;
        if (tracked.UsesRendererHeight)
            tracked.TargetOffset = GetTrackedOffset(tracked.Target, tracked.FactoryObject, tracked.Behaviour, usesRendererHeight: true, tracked.HeightRenderers);

        floating.TargetOffset = tracked.TargetOffset;
        floating.BasePosition = tracked.Target.position + tracked.TargetOffset;
        int scalePercent = tracked.UsesRendererHeight ? LargeBuildingShapeScalePercent : ShapeScalePercent;
        floating.ResourceView.transform.localScale = Vector3.one * (Mathf.Max(1, scalePercent) / 100f);
        floating.ResourceView.Show(true);
    }

    private static Resource GetFirstOutputResource(ResourceHolderBehaviour behaviour)
    {
        if (behaviour == null)
            return null;

        if (behaviour is BuildingBehaviour buildingBehaviour)
            return GetFirstBuildingOutputResource(buildingBehaviour);

        foreach (Resource resource in behaviour.GetOutputResources())
        {
            if (resource is ShapeResource shapeResource && shapeResource.ShapeData != null)
                return resource;

            if (resource != null && resource.Data is NonShapeResourceDataSO)
                return resource;
        }

        return null;
    }

    private static Resource GetFirstBuildingOutputResource(BuildingBehaviour behaviour)
    {
        if (behaviour.BuildingObjectData == null || behaviour.BuildingObjectData.ResourceOutputs == null)
            return null;

        foreach (BuildingObjectData.BuildingResourceData output in behaviour.BuildingObjectData.ResourceOutputs)
        {
            ResourceDataSO resourceData = output.ResourceData;
            if (resourceData == null)
                continue;

            PaintResourceDataSO paintResourceData = resourceData as PaintResourceDataSO;
            if (paintResourceData != null)
                return new ColorResource(resourceData, paintResourceData.Color);

            if (resourceData is NonShapeResourceDataSO)
                return new Resource(resourceData);
        }

        return null;
    }

    private static bool IsSameResource(FloatingShape floating, Resource resource)
    {
        if (floating.ResourceData != resource.Data)
            return false;

        ShapeResource shapeResource = resource as ShapeResource;
        if (shapeResource != null)
            return floating.ShapeData == shapeResource.ShapeData;

        return floating.ShapeData == null;
    }

    private static Vector3 GetTrackedOffset(
        Transform target,
        Data.FactoryFloor.FactoryObject factoryObject,
        ResourceHolderBehaviour behaviour,
        bool usesRendererHeight,
        List<Renderer> heightRenderers)
    {
        float height = usesRendererHeight ? GetRendererTopOffset(target, heightRenderers) + LargeBuildingClearance : FloatHeight;
        Vector3 offset = GetFloatingOffset(target, factoryObject, height);
        if (behaviour is AssemblerBehaviour)
            offset += GetInputBiasOffset(factoryObject, AssemblerInputBias);

        return offset;
    }

    private static float GetRendererTopOffset(Transform target, List<Renderer> heightRenderers)
    {
        if (target == null)
            return FloatHeight;

        float top = target.position.y + FloatHeight;
        if (heightRenderers != null && heightRenderers.Count > 0)
        {
            foreach (Renderer renderer in heightRenderers)
                top = IncludeRendererTop(top, renderer);
        }
        else
        {
            Renderer[] renderers = target.GetComponentsInChildren<Renderer>(includeInactive: false);
            foreach (Renderer renderer in renderers)
                top = IncludeRendererTop(top, renderer);
        }


        return Mathf.Max(FloatHeight, top - target.position.y);
    }

    private static float IncludeRendererTop(float top, Renderer renderer)
    {
        if (renderer == null || !renderer.enabled)
            return top;

        return Mathf.Max(top, renderer.bounds.max.y);
    }

    private static Vector3 GetFloatingOffset(Transform assemblerTransform, Data.FactoryFloor.FactoryObject factoryObject, float height)
    {
        if (factoryObject == null || factoryObject.OccupiedPositions == null || factoryObject.OccupiedPositions.Count == 0)
            return Vector3.up * height;

        Vector3 center = Vector3.zero;
        foreach (Vector3Int position in factoryObject.OccupiedPositions)
            center += position;

        center /= factoryObject.OccupiedPositions.Count;
        center += new Vector3(0.5f, 0f, 0.5f);
        center.y = assemblerTransform.position.y;
        return center - assemblerTransform.position + Vector3.up * height;
    }

    private static Vector3 GetInputBiasOffset(FactoryObject factoryObject, float distance)
    {
        if (factoryObject == null || factoryObject.DataInputPositions == null || factoryObject.DataInputPositions.Count == 0)
            return Vector3.zero;

        Vector3 center = Vector3.zero;
        foreach (Vector3Int position in factoryObject.OccupiedPositions)
            center += position;
        center /= factoryObject.OccupiedPositions.Count;
        center += new Vector3(0.5f, 0f, 0.5f);

        Vector3 inputCenter = Vector3.zero;
        foreach (Data.Operator.FactoryObjectData.InputData input in factoryObject.DataInputPositions)
            inputCenter += (Vector3)factoryObject.DataPosToWorldPos(input.Position) + new Vector3(0.5f, 0f, 0.5f);
        inputCenter /= factoryObject.DataInputPositions.Count;

        Vector3 direction = inputCenter - center;
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f)
            return Vector3.zero;

        return direction.normalized * distance;
    }

    private void AnimateFloatingShapes()
    {
        float time = Time.unscaledTime;
        foreach (FloatingShape floating in _floatingByCreatedId.Values)
        {
            if (floating.ResourceView == null)
                continue;

            if (floating.Target != null)
                floating.BasePosition = floating.Target.position + floating.TargetOffset;

            Transform transform = floating.ResourceView.transform;
            transform.position = floating.BasePosition + Vector3.up * (Mathf.Sin(time * 2.2f) * BobAmount);
            transform.rotation = UseDiagonalProjection
                ? GetDiagonalProjectionRotation()
                : Quaternion.Euler(22f, time * SpinDegreesPerSecond, 0f);
        }
    }

    private static Quaternion GetDiagonalProjectionRotation()
    {
        Camera mainCamera = Camera.main;
        if (mainCamera == null)
            return Quaternion.Inverse(PreviewShapeCameraRotation);

        return mainCamera.transform.rotation * Quaternion.Inverse(PreviewShapeCameraRotation);
    }

    private void RemoveFloatingShape(int createdId)
    {
        if (!_floatingByCreatedId.TryGetValue(createdId, out FloatingShape floating))
            return;

        ReturnResourceView(floating.ResourceView);
        _floatingByCreatedId.Remove(createdId);
    }

    private void ClearAll()
    {
        foreach (FloatingShape floating in _floatingByCreatedId.Values)
            ReturnResourceView(floating.ResourceView);

        _floatingByCreatedId.Clear();
        lock (_trackedLock)
            _trackedByCreatedId.Clear();
    }

    private static void ReturnResourceView(ResourceView resourceView)
    {
        if (resourceView == null)
            return;

        if (ResourceViewManager.Instance != null)
            ResourceViewManager.Instance.ReturnResourceToPool(resourceView);
        else
            UnityEngine.Object.Destroy(resourceView.gameObject);
    }

    private sealed class FloatingShape
    {
        public ResourceView ResourceView;
        public ResourceDataSO ResourceData;
        public ShapeData ShapeData;
        public Transform Target;
        public Vector3 TargetOffset;
        public Vector3 BasePosition;
    }

    private sealed class TrackedMachine
    {
        public FactoryObject FactoryObject;
        public ResourceHolderBehaviour Behaviour;
        public Transform Target;
        public bool UsesRendererHeight;
        public List<Renderer> HeightRenderers;
        public Vector3 TargetOffset;
    }
}
