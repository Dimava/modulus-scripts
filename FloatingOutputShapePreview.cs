using System;
using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using Data.Buildings;
using Data.FactoryFloor;
using Data.FactoryFloor.Behaviours;
using Data.FactoryFloor.Resources;
using Data.Shapes;
using Data.Variables.Recipes;
using HarmonyLib;
using Presentation.Buildings;
using Presentation.FactoryFloor;
using Presentation.UI.OperatorUIs.InsideOperatorUIs;
using Presentation.UI.OperatorUIs.OperatorPanelUIs;
using ScriptEngine;
using UnityEngine;

/// <summary>
/// Shows the configured first output resource as a small floating world object above machines and large buildings.
/// </summary>
[ScriptEntry]
public sealed class FloatingOutputShapePreview : ScriptMod
{
    private static readonly Quaternion PreviewShapeCameraRotation = Quaternion.Euler(33.51f, 315f, 0f);
    private const float CountLogInterval = 5f;
    private const float FloatHeight = 2.65f;
    private const float LargeBuildingClearance = 0.8f;
    private const float BobAmount = 0.08f;
    private const float SpinDegreesPerSecond = 35f;
    private const float AssemblerInputBias = 0.5f;
    private const float PositionUpdateEpsilonSqr = 0.000001f;
    private const float RotationUpdateEpsilonDegrees = 0.1f;
    private const bool DefaultUseDiagonalProjection = true;
    private const bool DefaultEnableBobbing = false;
    internal static bool UseDiagonalProjection = true;
    internal static bool EnableBobbing = false;
    internal static int ShapeScalePercent = 120;
    internal static int LargeBuildingShapeScalePercent = 480;

    private readonly object _trackedLock = new object();
    private readonly Dictionary<int, FloatingShape> _floatingByCreatedId = new Dictionary<int, FloatingShape>();
    private readonly Dictionary<int, TrackedMachine> _trackedByCreatedId = new Dictionary<int, TrackedMachine>();
    private bool _subscribedToViewManager;
    private bool _trackedExistingViews;
    private FactoryObjectViewManager _subscribedViewManager;
    private Quaternion _cachedDiagonalRotation;
    private Quaternion _lastCameraRotation;
    private bool _hasCachedDiagonalRotation;
    private Transform _animationRoot;
    private float _nextCountLogTime;
    private RefreshScheduler _refreshScheduler;
    internal static FloatingOutputShapePreview Instance;

    protected override void OnEnable()
    {
        Instance = this;
        EnsureAnimationRoot();
        EnsureRefreshScheduler();
        ReloadConfig();
        Log("Floating output shapes enabled.");
        BuildingBehaviour.OnBuildingUpgraded += OnBuildingUpgraded;
        TrySubscribeToViewManager();
    }

    protected override void OnConfigChanged()
    {
        ReloadConfig();
        ResetRotationCache();
        RefreshTrackedFloatingShapes();
    }

    private void ReloadConfig()
    {
        UseDiagonalProjection = DefaultUseDiagonalProjection;
        EnableBobbing = DefaultEnableBobbing;
        ShapeScalePercent = BindInt("ShapeScalePercent", 120).Value;
        LargeBuildingShapeScalePercent = BindInt("LargeBuildingShapeScalePercent", 480).Value;
    }

    protected override void OnDisable()
    {
        UnsubscribeFromViewManager();
        BuildingBehaviour.OnBuildingUpgraded -= OnBuildingUpgraded;
        ClearAll();
        if (_refreshScheduler != null)
            _refreshScheduler.Clear();
        if (ReferenceEquals(Instance, this))
            Instance = null;
    }

    protected override void OnUpdate()
    {
        TrySubscribeToViewManager();

        EnsureAnimationRoot();
        EnsureRefreshScheduler();
        AnimateFloatingShapes();

        if (Time.unscaledTime >= _nextCountLogTime)
        {
            _nextCountLogTime = Time.unscaledTime + CountLogInterval;
            LogCounts();
        }
    }

    private void TrySubscribeToViewManager()
    {
        FactoryObjectViewManager manager = FactoryObjectViewManager.Instance;
        if (manager == null)
            return;

        if (_subscribedToViewManager && !ReferenceEquals(manager, _subscribedViewManager))
            UnsubscribeFromViewManager();

        if (_subscribedToViewManager)
            return;

        manager.OnFactoryObjectViewCreated += OnFactoryObjectViewCreated;
        manager.OnFactoryObjectViewRemoved += OnFactoryObjectViewRemoved;
        _subscribedToViewManager = true;
        _subscribedViewManager = manager;

        if (!_trackedExistingViews)
        {
            TrackExistingViews(manager);
            RefreshTrackedFloatingShapes();
            _trackedExistingViews = true;
        }
    }

    private void UnsubscribeFromViewManager()
    {
        FactoryObjectViewManager manager = _subscribedViewManager;
        if (manager != null && _subscribedToViewManager)
        {
            manager.OnFactoryObjectViewCreated -= OnFactoryObjectViewCreated;
            manager.OnFactoryObjectViewRemoved -= OnFactoryObjectViewRemoved;
        }

        _subscribedToViewManager = false;
        _subscribedViewManager = null;
        _trackedExistingViews = false;
        ClearAll();
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
        NotifyFactoryObjectSet(view, factoryObject);
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

        RecipeOperatorView recipeOperatorView = GetViewComponent<RecipeOperatorView>(view);
        if (target == null && recipeOperatorView != null)
        {
            behaviour = factoryObject.GetFactoryObjectBehaviour<RecipeOperatorBehaviour>();
            target = recipeOperatorView.transform;
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
            View = view,
            FactoryObject = factoryObject,
            Behaviour = behaviour,
            Target = target,
            UsesRendererHeight = usesRendererHeight,
            HeightRenderers = heightRenderers,
            TargetOffset = GetTrackedOffset(target, factoryObject, behaviour, usesRendererHeight, heightRenderers)
        };
        UpdateOutputSnapshot(trackedMachine);

        if (behaviour is RecipeOperatorBehaviour recipeBehaviour)
        {
            int createdId = factoryObject.CreatedId;
            trackedMachine.RecipeBehaviour = recipeBehaviour;
            trackedMachine.RecipeChangedHandler = _ => RefreshRecipeOperator(recipeBehaviour);
            recipeBehaviour.OnChangedRecipe.RegisterMainThread(trackedMachine.RecipeChangedHandler);
        }

        TrackedMachine previous = null;
        lock (_trackedLock)
        {
            _trackedByCreatedId.TryGetValue(factoryObject.CreatedId, out previous);
            _trackedByCreatedId[factoryObject.CreatedId] = trackedMachine;
        }

        UnsubscribeRecipeChanged(previous);
    }

    internal void NotifyFactoryObjectSet(FactoryObjectView view, FactoryObject factoryObject)
    {
        if (view == null || factoryObject == null)
            return;

        TrackFactoryObjectView(view, factoryObject);
        ScheduleRefresh(factoryObject.CreatedId);
    }

    private void OnBuildingUpgraded(BuildingBehaviour behaviour, int stage)
    {
        int? createdId = null;
        lock (_trackedLock)
        {
            foreach (KeyValuePair<int, TrackedMachine> pair in _trackedByCreatedId)
            {
                if (ReferenceEquals(pair.Value.Behaviour, behaviour))
                {
                    createdId = pair.Key;
                    break;
                }
            }
        }

        if (createdId.HasValue)
            ScheduleRefresh(createdId.Value);
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
        TrackedMachine removed = null;
        lock (_trackedLock)
        {
            if (_trackedByCreatedId.TryGetValue(createdId, out removed))
                _trackedByCreatedId.Remove(createdId);
        }

        UnsubscribeRecipeChanged(removed);

        RemoveFloatingShape(createdId);
    }

    private static void UnsubscribeRecipeChanged(TrackedMachine tracked)
    {
        if (tracked?.RecipeBehaviour == null || tracked.RecipeChangedHandler == null)
            return;

        tracked.RecipeBehaviour.OnChangedRecipe.UnRegisterMainThread(tracked.RecipeChangedHandler);
        tracked.RecipeBehaviour = null;
        tracked.RecipeChangedHandler = null;
    }

    internal void NotifyViewReset(FactoryObjectView view)
    {
        if (view == null)
            return;

        int? createdId = null;
        lock (_trackedLock)
        {
            foreach (KeyValuePair<int, TrackedMachine> pair in _trackedByCreatedId)
            {
                if (ReferenceEquals(pair.Value.View, view))
                {
                    createdId = pair.Key;
                    break;
                }
            }
        }

        if (createdId.HasValue)
            RemoveTrackedMachine(createdId.Value);
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

    private void RefreshTrackedMachine(int createdId)
    {
        if (ResourceViewManager.Instance == null)
            return;

        TrackedMachine tracked;
        lock (_trackedLock)
        {
            if (!_trackedByCreatedId.TryGetValue(createdId, out tracked))
                return;
        }

        if (tracked.Target == null || tracked.FactoryObject == null || tracked.Behaviour == null)
        {
            RemoveTrackedMachine(createdId);
            return;
        }

        try
        {
            UpsertFloatingShape(createdId, tracked);
            UpdateOutputSnapshot(tracked);
        }
        catch
        {
            RemoveTrackedMachine(createdId);
        }
    }

    private void ScheduleRefresh(int createdId)
    {
        EnsureRefreshScheduler();
        if (_refreshScheduler != null)
            _refreshScheduler.Schedule(createdId);
        else
            RefreshTrackedMachine(createdId);
    }

    internal void ScheduleRefresh(ResourceHolderBehaviour behaviour)
    {
        if (behaviour == null)
            return;

        int? createdId = null;
        lock (_trackedLock)
        {
            foreach (KeyValuePair<int, TrackedMachine> pair in _trackedByCreatedId)
            {
                if (ReferenceEquals(pair.Value.Behaviour, behaviour))
                {
                    createdId = pair.Key;
                    break;
                }
            }
        }

        if (createdId.HasValue)
            ScheduleRefresh(createdId.Value);
    }

    internal void RefreshRecipeOperator(RecipeOperatorBehaviour behaviour)
    {
        if (behaviour == null)
            return;

        int? createdId = FindTrackedCreatedId(behaviour);
        if (!createdId.HasValue)
        {
            FactoryObjectViewManager manager = FactoryObjectViewManager.Instance;
            if (manager != null)
            {
                TrackExistingViews(manager);
                createdId = FindTrackedCreatedId(behaviour);
            }
        }

        if (createdId.HasValue)
            RefreshTrackedMachine(createdId.Value);
    }

    private int? FindTrackedCreatedId(ResourceHolderBehaviour behaviour)
    {
        lock (_trackedLock)
        {
            foreach (KeyValuePair<int, TrackedMachine> pair in _trackedByCreatedId)
            {
                if (ReferenceEquals(pair.Value.Behaviour, behaviour))
                    return pair.Key;
            }
        }

        return null;
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
            floating.Anchor = CreateFloatingAnchor(createdId);
            _floatingByCreatedId[createdId] = floating;
        }

        if (floating.ResourceView == null || !IsSameResource(floating, outputResource))
        {
            ReturnResourceView(floating.ResourceView);
            floating.ResourceView = ResourceViewManager.Instance.CreateNewResourceView(outputResource);
            floating.ResourceView.name = "FloatingOutputShape";
            SetFloatingResourceSnapshot(floating, outputResource);
            AttachResourceViewToAnchor(floating);
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

        if (behaviour is RecipeOperatorBehaviour recipeOperatorBehaviour)
            return GetFirstRecipeOutputResource(recipeOperatorBehaviour);

        foreach (Resource resource in behaviour.GetOutputResources())
        {
            if (resource is ShapeResource shapeResource && shapeResource.ShapeData != null)
                return resource;

            if (resource != null && resource.Data is NonShapeResourceDataSO)
                return resource;
        }

        return null;
    }

    private static bool UpdateOutputSnapshot(TrackedMachine tracked)
    {
        if (tracked == null)
            return false;

        Resource outputResource = GetFirstOutputResource(tracked.Behaviour);
        ResourceDataSO resourceData;
        bool hasShapeHash;
        ShapeHashPair shapeHash;
        bool hasColor;
        Color color;
        GetResourceSignature(outputResource, out resourceData, out hasShapeHash, out shapeHash, out hasColor, out color);
        bool outputPresent = resourceData != null;

        bool changed = !tracked.HasOutputSnapshot
            || tracked.LastOutputPresent != outputPresent
            || tracked.LastOutputResourceData != resourceData
            || tracked.LastOutputHasShapeHash != hasShapeHash
            || (hasShapeHash && tracked.LastOutputShapeHash != shapeHash)
            || tracked.LastOutputHasColor != hasColor
            || (hasColor && tracked.LastOutputColor != color);

        tracked.HasOutputSnapshot = true;
        tracked.LastOutputPresent = outputPresent;
        tracked.LastOutputResourceData = resourceData;
        tracked.LastOutputHasShapeHash = hasShapeHash;
        tracked.LastOutputShapeHash = shapeHash;
        tracked.LastOutputHasColor = hasColor;
        tracked.LastOutputColor = color;

        return changed;
    }

    private static Resource GetFirstRecipeOutputResource(RecipeOperatorBehaviour behaviour)
    {
        if (behaviour == null || !behaviour.HasRecipeSet)
            return null;

        foreach (ResourceRecipe.Output output in behaviour.CurrentRecipe.Outputs)
        {
            ResourceDataSO resourceData = output.resourceDataSO;
            if (resourceData == null)
                continue;

            ShapeData shapeData = output.ShapeData;
            if (shapeData != null)
                return new ShapeResource(resourceData, shapeData);

            PaintResourceDataSO paintResourceData = resourceData as PaintResourceDataSO;
            if (paintResourceData != null)
                return new ColorResource(resourceData, paintResourceData.Color);

            if (resourceData is NonShapeResourceDataSO)
                return new Resource(resourceData);
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
        ResourceDataSO resourceData;
        bool hasShapeHash;
        ShapeHashPair shapeHash;
        bool hasColor;
        Color color;
        GetResourceSignature(resource, out resourceData, out hasShapeHash, out shapeHash, out hasColor, out color);

        if (floating.ResourceData != resourceData)
            return false;

        if (floating.HasShapeHash != hasShapeHash)
            return false;

        if (hasShapeHash && floating.ShapeHash != shapeHash)
            return false;

        if (floating.HasResourceColor != hasColor)
            return false;

        if (hasColor && floating.ResourceColor != color)
            return false;

        return true;
    }

    private static void SetFloatingResourceSnapshot(FloatingShape floating, Resource resource)
    {
        ResourceDataSO resourceData;
        bool hasShapeHash;
        ShapeHashPair shapeHash;
        bool hasColor;
        Color color;
        GetResourceSignature(resource, out resourceData, out hasShapeHash, out shapeHash, out hasColor, out color);

        floating.ResourceData = resourceData;
        floating.ShapeData = (resource as ShapeResource)?.ShapeData;
        floating.HasShapeHash = hasShapeHash;
        floating.ShapeHash = shapeHash;
        floating.HasResourceColor = hasColor;
        floating.ResourceColor = color;
    }

    private static void GetResourceSignature(
        Resource resource,
        out ResourceDataSO resourceData,
        out bool hasShapeHash,
        out ShapeHashPair shapeHash,
        out bool hasColor,
        out Color color)
    {
        resourceData = resource?.Data;
        ShapeResource shapeResource = resource as ShapeResource;
        hasShapeHash = shapeResource?.ShapeData != null;
        shapeHash = hasShapeHash ? shapeResource.ShapeData.GetShapeHash() : default(ShapeHashPair);
        IColorResource colorResource = resource as IColorResource;
        hasColor = colorResource != null;
        color = hasColor ? colorResource.GetColor() : default(Color);
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
        var seen = new HashSet<Renderer>();
        if (heightRenderers != null && heightRenderers.Count > 0)
        {
            foreach (Renderer renderer in heightRenderers)
            {
                if (renderer != null && seen.Add(renderer))
                    top = IncludeRendererTop(top, renderer);
            }
        }

        Renderer[] renderers = target.GetComponentsInChildren<Renderer>(includeInactive: false);
        foreach (Renderer renderer in renderers)
        {
            if (renderer != null && seen.Add(renderer))
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
        EnsureAnimationRoot();
        float time = Time.unscaledTime;
        Quaternion sharedRotation = GetSharedFloatingRotation(time);
        Quaternion inverseSharedRotation = Quaternion.Inverse(sharedRotation);
        float bobOffset = EnableBobbing ? Mathf.Sin(time * 2.2f) * BobAmount : 0f;
        Vector3 sharedPosition = Vector3.up * bobOffset;

        if ((_animationRoot.position - sharedPosition).sqrMagnitude > PositionUpdateEpsilonSqr)
            _animationRoot.position = sharedPosition;
        if (Quaternion.Angle(_animationRoot.rotation, sharedRotation) > RotationUpdateEpsilonDegrees)
            _animationRoot.rotation = sharedRotation;

        foreach (FloatingShape floating in _floatingByCreatedId.Values)
        {
            if (floating.ResourceView == null || floating.Anchor == null)
                continue;

            if (floating.Target != null)
                floating.BasePosition = floating.Target.position + floating.TargetOffset;

            Vector3 nextLocalPosition = inverseSharedRotation * floating.BasePosition;
            if ((floating.Anchor.localPosition - nextLocalPosition).sqrMagnitude > PositionUpdateEpsilonSqr)
                floating.Anchor.localPosition = nextLocalPosition;
        }
    }

    private Quaternion GetSharedFloatingRotation(float time)
    {
        if (!UseDiagonalProjection)
            return Quaternion.Euler(22f, time * SpinDegreesPerSecond, 0f);

        Camera mainCamera = Camera.main;
        if (mainCamera == null)
            return Quaternion.Inverse(PreviewShapeCameraRotation);

        Quaternion cameraRotation = mainCamera.transform.rotation;
        if (!_hasCachedDiagonalRotation || Quaternion.Angle(_lastCameraRotation, cameraRotation) > RotationUpdateEpsilonDegrees)
        {
            _lastCameraRotation = cameraRotation;
            _cachedDiagonalRotation = cameraRotation * Quaternion.Inverse(PreviewShapeCameraRotation);
            _hasCachedDiagonalRotation = true;
        }

        return _cachedDiagonalRotation;
    }

    private void ResetRotationCache()
    {
        _hasCachedDiagonalRotation = false;
        _cachedDiagonalRotation = default;
        _lastCameraRotation = default;
    }

    private void RemoveFloatingShape(int createdId)
    {
        if (!_floatingByCreatedId.TryGetValue(createdId, out FloatingShape floating))
            return;

        ReturnResourceView(floating.ResourceView);
        if (floating.Anchor != null)
            UnityEngine.Object.Destroy(floating.Anchor.gameObject);
        _floatingByCreatedId.Remove(createdId);
    }

    private void ClearAll()
    {
        foreach (FloatingShape floating in _floatingByCreatedId.Values)
        {
            ReturnResourceView(floating.ResourceView);
            if (floating.Anchor != null)
                UnityEngine.Object.Destroy(floating.Anchor.gameObject);
        }

        _floatingByCreatedId.Clear();
        ResetRotationCache();
        if (_animationRoot != null)
        {
            UnityEngine.Object.Destroy(_animationRoot.gameObject);
            _animationRoot = null;
        }
        var trackedSnapshot = new List<TrackedMachine>();
        lock (_trackedLock)
        {
            foreach (TrackedMachine tracked in _trackedByCreatedId.Values)
                trackedSnapshot.Add(tracked);
            _trackedByCreatedId.Clear();
        }

        foreach (TrackedMachine tracked in trackedSnapshot)
            UnsubscribeRecipeChanged(tracked);
    }

    private void LogCounts()
    {
        int trackedCount;
        lock (_trackedLock)
            trackedCount = _trackedByCreatedId.Count;

        Log($"Counts: tracked={trackedCount}, floating={_floatingByCreatedId.Count}, constPreviewAngle=(33.51, 315, 0)");
    }

    private void EnsureAnimationRoot()
    {
        if (_animationRoot != null)
            return;

        var root = new GameObject("FloatingOutputShapePreviewRoot");
        root.transform.SetParent(gameObject.transform, false);
        root.transform.position = Vector3.zero;
        root.transform.rotation = Quaternion.identity;
        _animationRoot = root.transform;
    }

    private void EnsureRefreshScheduler()
    {
        if (_refreshScheduler != null)
            return;

        _refreshScheduler = gameObject.GetComponent<RefreshScheduler>();
        if (_refreshScheduler == null)
            _refreshScheduler = gameObject.AddComponent<RefreshScheduler>();
        _refreshScheduler.Initialize(this);
    }

    private Transform CreateFloatingAnchor(int createdId)
    {
        EnsureAnimationRoot();
        var anchor = new GameObject($"FloatingOutputShapeAnchor_{createdId}");
        anchor.transform.SetParent(_animationRoot, false);
        anchor.transform.localPosition = Vector3.zero;
        anchor.transform.localRotation = Quaternion.identity;
        return anchor.transform;
    }

    private static void AttachResourceViewToAnchor(FloatingShape floating)
    {
        if (floating.ResourceView == null || floating.Anchor == null)
            return;

        Transform transform = floating.ResourceView.transform;
        transform.SetParent(floating.Anchor, false);
        transform.localPosition = Vector3.zero;
        transform.localRotation = Quaternion.identity;
    }

    private static void ReturnResourceView(ResourceView resourceView)
    {
        if (resourceView == null)
            return;

        UnityEngine.Object.Destroy(resourceView.gameObject);
    }

    private sealed class FloatingShape
    {
        public Transform Anchor;
        public ResourceView ResourceView;
        public ResourceDataSO ResourceData;
        public ShapeData ShapeData;
        public bool HasShapeHash;
        public ShapeHashPair ShapeHash;
        public bool HasResourceColor;
        public Color ResourceColor;
        public Transform Target;
        public Vector3 TargetOffset;
        public Vector3 BasePosition;
    }

    private sealed class TrackedMachine
    {
        public FactoryObjectView View;
        public FactoryObject FactoryObject;
        public ResourceHolderBehaviour Behaviour;
        public RecipeOperatorBehaviour RecipeBehaviour;
        public Action<ResourceRecipe> RecipeChangedHandler;
        public bool HasOutputSnapshot;
        public bool LastOutputPresent;
        public ResourceDataSO LastOutputResourceData;
        public bool LastOutputHasShapeHash;
        public ShapeHashPair LastOutputShapeHash;
        public bool LastOutputHasColor;
        public Color LastOutputColor;
        public Transform Target;
        public bool UsesRendererHeight;
        public List<Renderer> HeightRenderers;
        public Vector3 TargetOffset;
    }

    private sealed class RefreshScheduler : MonoBehaviour
    {
        private FloatingOutputShapePreview _owner;
        private readonly HashSet<int> _pending = new HashSet<int>();

        public void Initialize(FloatingOutputShapePreview owner)
        {
            _owner = owner;
        }

        public void Schedule(int createdId)
        {
            if (_owner == null)
                return;

            if (!_pending.Add(createdId))
                return;

            StartCoroutine(RefreshAfterVisualsSettled(createdId));
        }

        public void Clear()
        {
            StopAllCoroutines();
            _pending.Clear();
            _owner = null;
        }

        private IEnumerator RefreshAfterVisualsSettled(int createdId)
        {
            yield return null;
            yield return new WaitForEndOfFrame();
            yield return null;
            _pending.Remove(createdId);
            _owner?.RefreshTrackedMachine(createdId);
        }
    }
}

[HarmonyPatch(typeof(FactoryObjectView), nameof(FactoryObjectView.Reset))]
static class FloatingOutputShapePreview_FactoryObjectView_Reset_Patch
{
    static void Prefix(FactoryObjectView __instance)
    {
        FloatingOutputShapePreview.Instance?.NotifyViewReset(__instance);
    }
}

[HarmonyPatch(typeof(FactoryObjectView), nameof(FactoryObjectView.SetFactoryObject))]
static class FloatingOutputShapePreview_FactoryObjectView_SetFactoryObject_Patch
{
    static void Postfix(FactoryObjectView __instance, FactoryObject factoryObject)
    {
        FloatingOutputShapePreview.Instance?.NotifyFactoryObjectSet(__instance, factoryObject);
    }
}

[HarmonyPatch(typeof(CutterUIInterval), nameof(CutterUIInterval.OnCuttingIntervalClicked))]
static class FloatingOutputShapePreview_CutterUIInterval_OnCuttingIntervalClicked_Patch
{
    static void Postfix(CutterUIInterval __instance)
    {
        var instance = FloatingOutputShapePreview.Instance;
        if (instance == null)
            return;

        var cutterUi = __instance != null ? __instance.GetComponentInParent<CutterUI>() : null;
        var behaviour = cutterUi != null
            ? Traverse.Create(cutterUi).Field("_behaviour").GetValue<ResourceHolderBehaviour>()
            : null;
        instance.ScheduleRefresh(behaviour);
    }
}

[HarmonyPatch(typeof(CutterBehaviour), nameof(CutterBehaviour.SetCuttingConfig))]
static class FloatingOutputShapePreview_CutterBehaviour_SetCuttingConfig_Patch
{
    static void Postfix(CutterBehaviour __instance)
    {
        FloatingOutputShapePreview.Instance?.ScheduleRefresh(__instance);
    }
}

[HarmonyPatch(typeof(CutterBehaviour), nameof(CutterBehaviour.ResetCutterConfig))]
static class FloatingOutputShapePreview_CutterBehaviour_ResetCutterConfig_Patch
{
    static void Postfix(CutterBehaviour __instance)
    {
        FloatingOutputShapePreview.Instance?.ScheduleRefresh(__instance);
    }
}

[HarmonyPatch(typeof(AssemblerBehaviour), nameof(AssemblerBehaviour.SetConfiguration))]
static class FloatingOutputShapePreview_AssemblerBehaviour_SetConfiguration_Patch
{
    static void Postfix(AssemblerBehaviour __instance)
    {
        FloatingOutputShapePreview.Instance?.ScheduleRefresh(__instance);
    }
}

[HarmonyPatch(typeof(AssemblerBehaviour), "TryAssembleShapeNewColors")]
static class FloatingOutputShapePreview_AssemblerBehaviour_TryAssembleShapeNewColors_Patch
{
    static void Postfix(AssemblerBehaviour __instance)
    {
        FloatingOutputShapePreview.Instance?.ScheduleRefresh(__instance);
    }
}

[HarmonyPatch(typeof(AssemblerBehaviour), nameof(AssemblerBehaviour.Reset))]
static class FloatingOutputShapePreview_AssemblerBehaviour_Reset_Patch
{
    static void Postfix(AssemblerBehaviour __instance)
    {
        FloatingOutputShapePreview.Instance?.ScheduleRefresh(__instance);
    }
}

[HarmonyPatch(typeof(StamperBehaviour), nameof(StamperBehaviour.SetStampConfig))]
static class FloatingOutputShapePreview_StamperBehaviour_SetStampConfig_Patch
{
    static void Postfix(StamperBehaviour __instance)
    {
        FloatingOutputShapePreview.Instance?.ScheduleRefresh(__instance);
    }
}

[HarmonyPatch(typeof(StamperBehaviour), nameof(StamperBehaviour.ResetStampConfig))]
static class FloatingOutputShapePreview_StamperBehaviour_ResetStampConfig_Patch
{
    static void Postfix(StamperBehaviour __instance)
    {
        FloatingOutputShapePreview.Instance?.ScheduleRefresh(__instance);
    }
}

[HarmonyPatch(typeof(StamperMK2Behaviour), nameof(StamperMK2Behaviour.ResetStampConfig))]
static class FloatingOutputShapePreview_StamperMK2Behaviour_ResetStampConfig_Patch
{
    static void Postfix(StamperMK2Behaviour __instance)
    {
        FloatingOutputShapePreview.Instance?.ScheduleRefresh(__instance);
    }
}

[HarmonyPatch(typeof(RecipeOperatorBehaviour), nameof(RecipeOperatorBehaviour.ChangeRecipe))]
static class FloatingOutputShapePreview_RecipeOperatorBehaviour_ChangeRecipe_Patch
{
    static void Postfix(RecipeOperatorBehaviour __instance)
    {
        FloatingOutputShapePreview.Instance?.RefreshRecipeOperator(__instance);
    }
}

[HarmonyPatch(typeof(RecipeOperatorUI), "SetRecipe")]
static class FloatingOutputShapePreview_RecipeOperatorUI_SetRecipe_Patch
{
    static void Postfix(RecipeOperatorUI __instance)
    {
        RecipeOperatorBehaviour behaviour = Traverse.Create(__instance).Field("_behaviour").GetValue<RecipeOperatorBehaviour>();
        FloatingOutputShapePreview.Instance?.RefreshRecipeOperator(behaviour);
    }
}
