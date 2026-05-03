using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using Data.Buildings;
using Data.FactoryFloor;
using Data.FactoryFloor.Behaviours;
using Data.FactoryFloor.Resources;
using Data.Shapes;
using HarmonyLib;
using Presentation.Buildings;
using Presentation.FactoryFloor;
using Presentation.UI.OperatorUIs;
using Presentation.UI.OperatorUIs.InsideOperatorUIs;
using ScriptEngine;
using UnityEngine;

// @Name: Floating Output Shape Preview Minimal
// @Description: Minimal static output previews above supported machines and buildings.
// @Version: 1.0.0
// @Author: Dimava

[ScriptEntry]
public sealed class FloatingOutputShapePreviewMinimal : ScriptMod
{
    private static readonly Quaternion PreviewRotation = Quaternion.Inverse(Quaternion.Euler(33.51f, 315f, 0f));
    private const float FloatHeight = 2.65f;
    private const float LargeBuildingClearance = 0.8f;
    private const float AssemblerInputBias = 0.5f;
    private const int ShapeScalePercent = 120;
    private const int LargeBuildingShapeScalePercent = 480;

    private readonly object _trackedLock = new object();
    private readonly Dictionary<int, FloatingShape> _floatingByCreatedId = new Dictionary<int, FloatingShape>();
    private readonly Dictionary<int, TrackedMachine> _trackedByCreatedId = new Dictionary<int, TrackedMachine>();
    private bool _subscribedToViewManager;
    private bool _trackedExistingViews;
    private RefreshScheduler _refreshScheduler;
    internal static FloatingOutputShapePreviewMinimal Instance;

    protected override void OnEnable()
    {
        Instance = this;
        _refreshScheduler = gameObject.GetComponent<RefreshScheduler>();
        if (_refreshScheduler == null)
            _refreshScheduler = gameObject.AddComponent<RefreshScheduler>();
        _refreshScheduler.Initialize(this);
        TrySubscribeToViewManager();
    }

    protected override void OnDisable()
    {
        UnsubscribeFromViewManager();
        ClearAll();
        if (_refreshScheduler != null)
            _refreshScheduler.Clear();
        if (ReferenceEquals(Instance, this))
            Instance = null;
    }

    protected override void OnUpdate()
    {
        if (!_subscribedToViewManager)
            TrySubscribeToViewManager();
    }

    private void TrySubscribeToViewManager()
    {
        FactoryObjectViewManager manager = FactoryObjectViewManager.Instance;
        if (manager == null)
            return;

        manager.OnFactoryObjectViewCreated += OnFactoryObjectViewCreated;
        manager.OnFactoryObjectViewRemoved += OnFactoryObjectViewRemoved;
        _subscribedToViewManager = true;

        if (_trackedExistingViews)
            return;

        TrackExistingViews(manager);
        RefreshAllTrackedMachines();
        _trackedExistingViews = true;
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
        if (factoryObject != null)
            RefreshTrackedMachine(factoryObject.CreatedId);
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

        lock (_trackedLock)
            _trackedByCreatedId[factoryObject.CreatedId] = trackedMachine;
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
        }
        catch
        {
            RemoveTrackedMachine(createdId);
        }
    }

    private void RefreshAllTrackedMachines()
    {
        if (ResourceViewManager.Instance == null)
            return;

        int[] createdIds;
        lock (_trackedLock)
            createdIds = new List<int>(_trackedByCreatedId.Keys).ToArray();

        foreach (int createdId in createdIds)
            RefreshTrackedMachine(createdId);
    }

    internal void RefreshTrackedMachine(ResourceHolderBehaviour behaviour)
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
            RefreshTrackedMachine(createdId.Value);
    }

    internal void NotifyViewDestroyed(FactoryObjectView view)
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

    internal void NotifyViewReset(FactoryObjectView view)
    {
        NotifyViewDestroyed(view);
    }

    internal void ScheduleRefresh(ResourceHolderBehaviour behaviour)
    {
        if (behaviour == null || _refreshScheduler == null)
            return;

        _refreshScheduler.Schedule(behaviour);
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
            floating.ResourceView.name = "FloatingOutputShapeMinimal";
            floating.ResourceData = outputResource.Data;
            floating.ShapeData = (outputResource as ShapeResource)?.ShapeData;
        }

        if (tracked.UsesRendererHeight)
            tracked.TargetOffset = GetTrackedOffset(tracked.Target, tracked.FactoryObject, tracked.Behaviour, true, tracked.HeightRenderers);

        floating.ResourceView.transform.position = tracked.Target.position + tracked.TargetOffset;
        floating.ResourceView.transform.rotation = PreviewRotation;
        int scalePercent = tracked.UsesRendererHeight ? LargeBuildingShapeScalePercent : ShapeScalePercent;
        floating.ResourceView.transform.localScale = Vector3.one * (Mathf.Max(1, scalePercent) / 100f);
        floating.ResourceView.Show(true);
    }

    private void RemoveTrackedMachine(int createdId)
    {
        lock (_trackedLock)
            _trackedByCreatedId.Remove(createdId);

        RemoveFloatingShape(createdId);
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
        if (floating.ResourceData != resource.Data)
            return false;

        ShapeResource shapeResource = resource as ShapeResource;
        if (shapeResource != null)
            return floating.ShapeData == shapeResource.ShapeData;

        return floating.ShapeData == null;
    }

    private static Vector3 GetTrackedOffset(
        Transform target,
        FactoryObject factoryObject,
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

    private static Vector3 GetFloatingOffset(Transform targetTransform, FactoryObject factoryObject, float height)
    {
        if (factoryObject == null || factoryObject.OccupiedPositions == null || factoryObject.OccupiedPositions.Count == 0)
            return Vector3.up * height;

        Vector3 center = Vector3.zero;
        foreach (Vector3Int position in factoryObject.OccupiedPositions)
            center += position;

        center /= factoryObject.OccupiedPositions.Count;
        center += new Vector3(0.5f, 0f, 0.5f);
        center.y = targetTransform.position.y;
        return center - targetTransform.position + Vector3.up * height;
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

    private static void ReturnResourceView(ResourceView resourceView)
    {
        if (resourceView == null)
            return;

        if (ResourceViewManager.Instance != null)
            ResourceViewManager.Instance.ReturnResourceToPool(resourceView);
        else
            Object.Destroy(resourceView.gameObject);
    }

    private sealed class FloatingShape
    {
        public ResourceView ResourceView;
        public ResourceDataSO ResourceData;
        public ShapeData ShapeData;
    }

    private sealed class TrackedMachine
    {
        public FactoryObjectView View;
        public FactoryObject FactoryObject;
        public ResourceHolderBehaviour Behaviour;
        public Transform Target;
        public bool UsesRendererHeight;
        public List<Renderer> HeightRenderers;
        public Vector3 TargetOffset;
    }

    private sealed class RefreshScheduler : MonoBehaviour
    {
        private FloatingOutputShapePreviewMinimal _owner;
        private readonly HashSet<int> _pending = new HashSet<int>();

        public void Initialize(FloatingOutputShapePreviewMinimal owner)
        {
            _owner = owner;
        }

        public void Schedule(ResourceHolderBehaviour behaviour)
        {
            if (_owner == null || behaviour == null)
                return;

            int id = behaviour.GetInstanceID();
            if (!_pending.Add(id))
                return;

            StartCoroutine(RefreshAfterCommit(behaviour, id));
        }

        public void Clear()
        {
            StopAllCoroutines();
            _pending.Clear();
            _owner = null;
        }

        private IEnumerator RefreshAfterCommit(ResourceHolderBehaviour behaviour, int id)
        {
            yield return null;
            yield return new WaitForEndOfFrame();
            _pending.Remove(id);
            _owner?.RefreshTrackedMachine(behaviour);
        }
    }
}

[HarmonyPatch(typeof(FactoryObjectView), "OnDestroy")]
static class FloatingOutputShapePreviewMinimal_FactoryObjectView_OnDestroy_Patch
{
    static void Postfix(FactoryObjectView __instance)
    {
        FloatingOutputShapePreviewMinimal.Instance?.NotifyViewDestroyed(__instance);
    }
}

[HarmonyPatch(typeof(FactoryObjectView), nameof(FactoryObjectView.Reset))]
static class FloatingOutputShapePreviewMinimal_FactoryObjectView_Reset_Patch
{
    static void Prefix(FactoryObjectView __instance)
    {
        FloatingOutputShapePreviewMinimal.Instance?.NotifyViewReset(__instance);
    }
}

[HarmonyPatch(typeof(CutterUIInterval), nameof(CutterUIInterval.OnCuttingIntervalClicked))]
static class FloatingOutputShapePreviewMinimal_CutterUIInterval_OnCuttingIntervalClicked_Patch
{
    static void Postfix(CutterUIInterval __instance)
    {
        var instance = FloatingOutputShapePreviewMinimal.Instance;
        if (instance == null)
            return;

        var cutterUi = __instance != null ? __instance.GetComponentInParent<CutterUI>() : null;
        var behaviour = cutterUi != null
            ? Traverse.Create(cutterUi).Field("_behaviour").GetValue<ResourceHolderBehaviour>()
            : null;
        instance.Log("Cutter interval changed.");
        instance.ScheduleRefresh(behaviour);
    }
}

[HarmonyPatch(typeof(MachineButton), nameof(MachineButton.OnPointerUp))]
static class FloatingOutputShapePreviewMinimal_MachineButton_OnPointerUp_Patch
{
    static void Postfix(MachineButton __instance)
    {
        if (__instance == null)
            return;

        var instance = FloatingOutputShapePreviewMinimal.Instance;
        if (instance == null)
            return;

        if (TryRefreshUiButton<CutterUI>(__instance, instance, "_readyButton", "_resetButton"))
            return;
        if (TryRefreshUiButton<AssemblerUI>(__instance, instance, "_readyButton", "_resetButton"))
            return;
        if (TryRefreshUiButton<StamperUI>(__instance, instance, "_readyButton", "_resetButton"))
            return;
        _ = TryRefreshUiButton<StamperMK2UI>(__instance, instance, "_readyButton", "_resetButton");
    }

    static bool TryRefreshUiButton<T>(MachineButton clickedButton, FloatingOutputShapePreviewMinimal instance, params string[] fieldNames)
        where T : MonoBehaviour
    {
        var ui = clickedButton.GetComponentInParent<T>();
        if (ui == null)
            return false;

        var traverse = Traverse.Create(ui);
        foreach (string fieldName in fieldNames)
        {
            var candidate = traverse.Field(fieldName).GetValue<MachineButton>();
            if (!ReferenceEquals(candidate, clickedButton))
                continue;

            var behaviour = traverse.Field("_behaviour").GetValue<ResourceHolderBehaviour>();
            string action = fieldName == "_readyButton" ? "ready" : "reset";
            instance.Log($"{typeof(T).Name} {action} pressed.");
            instance.ScheduleRefresh(behaviour);
            return true;
        }

        return false;
    }
}

[HarmonyPatch(typeof(RecipeOperatorBehaviour), nameof(RecipeOperatorBehaviour.ChangeRecipe))]
static class FloatingOutputShapePreviewMinimal_RecipeOperatorBehaviour_ChangeRecipe_Patch
{
    static void Postfix(RecipeOperatorBehaviour __instance)
    {
        FloatingOutputShapePreviewMinimal.Instance?.ScheduleRefresh(__instance);
    }
}
