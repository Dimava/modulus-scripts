using System.Collections.Generic;
using System.Text;
using Data.Minimap;
using HarmonyLib;
using ScriptEngine;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Adds a maximize toggle for the minimap window, including its title bar.
/// Falls back to Ctrl+M if the button placement is not visible.
/// </summary>
[ScriptEntry]
public sealed class MinimapFullscreenToggle : ScriptMod
{
    private static MinimapFullscreenToggle _instance;

    protected override void OnEnable()
    {
        _instance = this;
    }

    protected override void OnDisable()
    {
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }
    }

    internal static void LogInfo(string message)
    {
        _instance?.Log(message);
    }

    internal static void LogWarn(string message)
    {
        _instance?.Warn(message);
    }
}

public sealed class MinimapFullscreenToggleController : MonoBehaviour
{
    private struct RectTransformState
    {
        public RectTransform Rect;
        public Vector2 AnchorMin;
        public Vector2 AnchorMax;
        public Vector2 OffsetMin;
        public Vector2 OffsetMax;
        public Vector2 Pivot;
        public Vector2 SizeDelta;
        public Vector3 AnchoredPosition3D;
        public Vector3 LocalScale;
        public Quaternion LocalRotation;
    }

    private struct BehaviourState
    {
        public Behaviour Behaviour;
        public bool Enabled;
    }

    private const string FullscreenLabel = "MAX";
    private const string WindowedLabel = "REST";
    private static readonly Vector2 CenterAnchor = new Vector2(0.5f, 0.5f);
    private const float SideMargin = 48f;
    private const float TopSafeMargin = 96f;
    private const float BottomMargin = 48f;

    private static MinimapFullscreenToggleController _lastActive;

    private MinimapUI _minimapUI;
    private RectTransform _panelRect;
    private RectTransform _contentsRect;
    private RectTransform _titleRect;
    private RectTransform _targetRect;
    private MinimapScrollViewControls _scrollControls;
    private Button _closeButton;
    private Button _toggleButton;
    private Text _toggleText;

    private Transform _originalParent;
    private int _originalSiblingIndex;
    private Vector2 _originalAnchorMin;
    private Vector2 _originalAnchorMax;
    private Vector2 _originalOffsetMin;
    private Vector2 _originalOffsetMax;
    private Vector2 _originalPivot;
    private Vector2 _originalSizeDelta;
    private Vector3 _originalAnchoredPosition3D;
    private Vector3 _originalLocalScale;
    private Quaternion _originalLocalRotation;

    private readonly List<RectTransformState> _modifiedRects = new List<RectTransformState>();
    private readonly List<BehaviourState> _modifiedBehaviours = new List<BehaviourState>();
    private GameObject _overlay;
    private bool _isFullscreen;
    private bool _didLogTarget;
    private float _appliedZoomFactor = 1f;

    public static void ToggleAnyVisible()
    {
        if (_lastActive != null && _lastActive.isActiveAndEnabled)
        {
            _lastActive.ToggleFullscreen();
            return;
        }

        foreach (MinimapFullscreenToggleController controller in Resources.FindObjectsOfTypeAll<MinimapFullscreenToggleController>())
        {
            if (controller != null && controller.isActiveAndEnabled)
            {
                controller.ToggleFullscreen();
                return;
            }
        }

        MinimapFullscreenToggle.LogWarn("No active minimap window found for fullscreen toggle.");
    }

    public static void DumpAnyVisible(string reason)
    {
        bool dumped = false;
        if (_lastActive != null)
        {
            _lastActive.DumpDebugTree(reason);
            dumped = true;
        }

        foreach (MinimapFullscreenToggleController controller in Resources.FindObjectsOfTypeAll<MinimapFullscreenToggleController>())
        {
            if (controller == null || ReferenceEquals(controller, _lastActive))
            {
                continue;
            }

            controller.DumpDebugTree(reason);
            dumped = true;
        }

        if (!dumped)
        {
            MinimapFullscreenToggle.LogWarn($"No minimap controller instances found to dump for '{reason}'.");
        }
    }

    public void Setup(MinimapUI minimapUI)
    {
        _minimapUI = minimapUI;
        _panelRect = minimapUI != null ? minimapUI.GetComponent<RectTransform>() : null;
        _contentsRect = _panelRect != null ? _panelRect.parent as RectTransform : null;
        _scrollControls = minimapUI != null ? minimapUI.GetComponentInChildren<MinimapScrollViewControls>(includeInactive: true) : null;
        ResolveTargets();
        EnsureButton();
    }

    private void Awake()
    {
        _lastActive = this;
        if (_minimapUI == null)
        {
            _minimapUI = GetComponent<MinimapUI>();
        }

        if (_panelRect == null)
        {
            _panelRect = GetComponent<RectTransform>();
        }

        if (_contentsRect == null && _panelRect != null)
        {
            _contentsRect = _panelRect.parent as RectTransform;
        }

        ResolveTargets();
    }

    private void OnEnable()
    {
        _lastActive = this;
        ResolveTargets();
        EnsureButton();
        UpdateButtonLabel();
    }

    private void LateUpdate()
    {
        if (_toggleButton == null)
        {
            EnsureButton();
        }
        else if (_toggleButton.gameObject.activeSelf != gameObject.activeInHierarchy)
        {
            _toggleButton.gameObject.SetActive(gameObject.activeInHierarchy);
        }
    }

    private void OnDisable()
    {
        ExitFullscreen();
    }

    private void OnDestroy()
    {
        if (ReferenceEquals(_lastActive, this))
        {
            _lastActive = null;
        }

        ExitFullscreen();
    }

    private void EnsureButton()
    {
        ResolveTargets();
        if (_panelRect == null)
        {
            return;
        }

        if (_toggleButton != null)
        {
            RepositionButton();
            return;
        }

        GameObject buttonObject = new GameObject("DimavaMinimapFullscreenToggle", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        RectTransform buttonRect = buttonObject.GetComponent<RectTransform>();
        Transform buttonParent = (_closeButton != null && _closeButton.transform.parent != null) ? _closeButton.transform.parent : _targetRect;
        buttonObject.transform.SetParent(buttonParent, false);

        Image image = buttonObject.GetComponent<Image>();
        image.color = new Color(0.17f, 0.24f, 0.38f, 0.98f);

        _toggleButton = buttonObject.GetComponent<Button>();
        _toggleButton.targetGraphic = image;
        _toggleButton.onClick.AddListener(ToggleFullscreen);

        GameObject textObject = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        textObject.transform.SetParent(buttonObject.transform, false);

        RectTransform textRect = textObject.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(6f, 3f);
        textRect.offsetMax = new Vector2(-6f, -3f);

        _toggleText = textObject.GetComponent<Text>();
        _toggleText.alignment = TextAnchor.MiddleCenter;
        _toggleText.color = Color.white;
        _toggleText.resizeTextForBestFit = true;
        _toggleText.resizeTextMinSize = 10;
        _toggleText.resizeTextMaxSize = 18;
        _toggleText.fontStyle = FontStyle.Bold;
        _toggleText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");

        RepositionButton();
        UpdateButtonLabel();
    }

    private void RepositionButton()
    {
        if (_toggleButton == null)
        {
            return;
        }

        RectTransform buttonRect = _toggleButton.GetComponent<RectTransform>();
        if (buttonRect == null)
        {
            return;
        }

        if (_closeButton != null)
        {
            RectTransform closeRect = _closeButton.GetComponent<RectTransform>();
            if (closeRect != null)
            {
                buttonRect.anchorMin = closeRect.anchorMin;
                buttonRect.anchorMax = closeRect.anchorMax;
                buttonRect.pivot = new Vector2(1f, closeRect.pivot.y);
                buttonRect.sizeDelta = new Vector2(72f, Mathf.Max(28f, closeRect.rect.height));
                buttonRect.localScale = Vector3.one;
                buttonRect.localRotation = Quaternion.identity;
                buttonRect.anchoredPosition3D = closeRect.anchoredPosition3D + new Vector3(-closeRect.rect.width - 10f, 0f, 0f);
                buttonRect.SetAsLastSibling();
            }
        }
        else
        {
            buttonRect.anchorMin = new Vector2(1f, 1f);
            buttonRect.anchorMax = new Vector2(1f, 1f);
            buttonRect.pivot = new Vector2(1f, 1f);
            buttonRect.sizeDelta = new Vector2(72f, 30f);
            buttonRect.anchoredPosition3D = new Vector3(-12f, -12f, 0f);
        }
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            ExitFullscreen();
        }
        else
        {
            EnterFullscreen();
        }
    }

    private void EnterFullscreen()
    {
        ResolveTargets();
        if (_isFullscreen || _targetRect == null)
        {
            return;
        }

        Canvas rootCanvas = FindRootCanvas();
        if (rootCanvas == null)
        {
            MinimapFullscreenToggle.LogWarn("Could not find root canvas for fullscreen minimap.");
            return;
        }

        _originalParent = _targetRect.parent;
        _originalSiblingIndex = _targetRect.GetSiblingIndex();
        _originalAnchorMin = _targetRect.anchorMin;
        _originalAnchorMax = _targetRect.anchorMax;
        _originalOffsetMin = _targetRect.offsetMin;
        _originalOffsetMax = _targetRect.offsetMax;
        _originalPivot = _targetRect.pivot;
        _originalSizeDelta = _targetRect.sizeDelta;
        _originalAnchoredPosition3D = _targetRect.anchoredPosition3D;
        _originalLocalScale = _targetRect.localScale;
        _originalLocalRotation = _targetRect.localRotation;

        Rect activeBounds = GetActiveDirectChildrenBounds(_targetRect);
        RectTransform rootCanvasRect = rootCanvas.GetComponent<RectTransform>();
        float widthScale;
        float heightScale;

        _overlay = CreateOverlay(rootCanvas.transform);
        _targetRect.SetParent(_overlay.transform, false);
        _targetRect.anchorMin = CenterAnchor;
        _targetRect.anchorMax = CenterAnchor;
        _targetRect.pivot = CenterAnchor;
        _targetRect.anchoredPosition3D = Vector3.zero;
        _targetRect.localScale = Vector3.one;
        _targetRect.localRotation = Quaternion.identity;

        ApplyMaximizedLayout(rootCanvasRect, out widthScale, out heightScale);
        float viewportScale = Mathf.Max(1f, Mathf.Min(widthScale, heightScale));
        _appliedZoomFactor = 1f / viewportScale;
        ApplyRelativeMinimapZoom(_appliedZoomFactor, "maximize-enter");

        _isFullscreen = true;
        UpdateButtonLabel();
        MinimapFullscreenToggle.LogInfo($"Maximized minimap group '{GetPath(_targetRect)}' bounds={activeBounds.width:0.#}x{activeBounds.height:0.#} widthScale={widthScale:0.###} heightScale={heightScale:0.###}.");
    }

    private void ExitFullscreen()
    {
        if (!_isFullscreen || _targetRect == null)
        {
            return;
        }

        ApplyRelativeMinimapZoom(1f / Mathf.Max(0.01f, _appliedZoomFactor), "maximize-exit");
        RestoreModifiedRects();
        RestoreModifiedBehaviours();

        if (_originalParent != null)
        {
            _targetRect.SetParent(_originalParent, false);
            _targetRect.SetSiblingIndex(_originalSiblingIndex);
            _targetRect.anchorMin = _originalAnchorMin;
            _targetRect.anchorMax = _originalAnchorMax;
            _targetRect.offsetMin = _originalOffsetMin;
            _targetRect.offsetMax = _originalOffsetMax;
            _targetRect.pivot = _originalPivot;
            _targetRect.sizeDelta = _originalSizeDelta;
            _targetRect.anchoredPosition3D = _originalAnchoredPosition3D;
            _targetRect.localScale = _originalLocalScale;
            _targetRect.localRotation = _originalLocalRotation;
        }

        if (_overlay != null)
        {
            Object.Destroy(_overlay);
            _overlay = null;
        }

        _isFullscreen = false;
        _appliedZoomFactor = 1f;
        UpdateButtonLabel();
    }

    private void UpdateButtonLabel()
    {
        if (_toggleText != null)
        {
            _toggleText.text = _isFullscreen ? WindowedLabel : FullscreenLabel;
        }
    }

    private Canvas FindRootCanvas()
    {
        RectTransform sourceRect = _targetRect != null ? _targetRect : _panelRect;
        Canvas canvas = (sourceRect != null) ? sourceRect.GetComponentInParent<Canvas>() : null;
        if (canvas != null)
        {
            return canvas.rootCanvas;
        }

        foreach (Canvas candidate in Resources.FindObjectsOfTypeAll<Canvas>())
        {
            if (candidate != null && candidate.isRootCanvas)
            {
                return candidate;
            }
        }

        return null;
    }

    private void ResolveTargets()
    {
        _closeButton = null;
        _titleRect = null;
        _targetRect = _panelRect;

        if (_panelRect == null)
        {
            return;
        }

        _contentsRect = _panelRect.parent as RectTransform;
        RectTransform windowRect = _contentsRect != null ? _contentsRect.parent as RectTransform : null;
        if (windowRect != null)
        {
            _targetRect = windowRect;
            _titleRect = FindActiveTitleSibling(_panelRect);
            _closeButton = FindCloseLikeButton(_titleRect != null ? _titleRect : _contentsRect, includeInactive: false)
                ?? FindCloseLikeButton(_contentsRect, includeInactive: false)
                ?? FindCloseLikeButton(_contentsRect, includeInactive: true)
                ?? FindCloseLikeButton(_targetRect, includeInactive: true);
        }

        if (!_didLogTarget && _targetRect != null)
        {
            _didLogTarget = true;
            string closeName = _closeButton != null ? _closeButton.name : "<none>";
            string titleName = _titleRect != null ? _titleRect.name : "<none>";
            MinimapFullscreenToggle.LogInfo($"Resolved minimap maximize target='{GetPath(_targetRect)}' title='{titleName}' close='{closeName}'.");
        }
    }

    private void ApplyMaximizedLayout(RectTransform rootCanvasRect, out float widthScale, out float heightScale)
    {
        RectTransform mapContainerRect = FindChildRect(_panelRect, "Minimap");
        RectTransform footerRect = FindChildRect(_panelRect, "Footer");
        RectTransform scrollViewRect = _scrollControls != null ? _scrollControls.GetComponent<RectTransform>() : FindDescendantRect(_panelRect, "Scroll View");
        List<RectTransform> dividerRects = FindDirectChildren(_contentsRect, "Divider");

        float originalWidth = Mathf.Max(1f, _panelRect != null ? _panelRect.rect.width : 1f);
        float originalPanelHeight = Mathf.Max(1f, _panelRect != null ? _panelRect.rect.height : 1f);
        float originalMapHeight = Mathf.Max(1f, mapContainerRect != null ? mapContainerRect.rect.height : originalPanelHeight - 46f);
        float originalViewportWidth = Mathf.Max(1f, scrollViewRect != null ? scrollViewRect.rect.width : originalWidth - 12f);
        float originalViewportHeight = Mathf.Max(1f, scrollViewRect != null ? scrollViewRect.rect.height : originalMapHeight - 12f);

        float topDividerHeight = 8f;
        float titleHeight = _titleRect != null ? Mathf.Max(1f, _titleRect.rect.height) : 64f;
        float middleDividerHeight = 8f;
        float footerHeight = footerRect != null ? Mathf.Max(1f, footerRect.rect.height) : Mathf.Max(1f, originalPanelHeight - originalMapHeight);
        float usableWidth = Mathf.Max(originalWidth, rootCanvasRect.rect.width - SideMargin * 2f);
        float usableHeight = Mathf.Max(titleHeight + middleDividerHeight + footerHeight + 100f, rootCanvasRect.rect.height - TopSafeMargin - BottomMargin);
        float newPanelHeight = Mathf.Max(footerHeight + 100f, usableHeight - topDividerHeight - titleHeight - middleDividerHeight);
        float newMapHeight = Mathf.Max(100f, newPanelHeight - footerHeight);
        float newViewportWidth = Mathf.Max(100f, usableWidth - 12f);
        float newViewportHeight = Mathf.Max(100f, newMapHeight - 12f);
        float totalHeight = topDividerHeight + titleHeight + middleDividerHeight + newPanelHeight;

        widthScale = newViewportWidth / originalViewportWidth;
        heightScale = newViewportHeight / originalViewportHeight;

        DisableLayoutDrivers(_targetRect);
        DisableLayoutDrivers(_contentsRect);
        SaveRectState(_titleRect);
        SaveRectState(_targetRect);
        SaveRectState(_contentsRect);
        SaveRectState(_panelRect);
        SaveRectState(mapContainerRect);
        SaveRectState(footerRect);
        SaveRectState(scrollViewRect);
        for (int i = 0; i < dividerRects.Count; i++)
        {
            SaveRectState(dividerRects[i]);
        }

        if (_targetRect != null)
        {
            _targetRect.sizeDelta = new Vector2(usableWidth, totalHeight);
            _targetRect.anchoredPosition3D = new Vector3(0f, (BottomMargin - TopSafeMargin) * 0.5f, 0f);
        }

        if (_contentsRect != null)
        {
            _contentsRect.anchorMin = new Vector2(0f, 1f);
            _contentsRect.anchorMax = new Vector2(0f, 1f);
            _contentsRect.pivot = new Vector2(0f, 1f);
            _contentsRect.offsetMin = new Vector2(0f, -totalHeight);
            _contentsRect.offsetMax = new Vector2(usableWidth, 0f);
            _contentsRect.anchoredPosition3D = Vector3.zero;
        }

        if (_titleRect != null)
        {
            SetFixedTopRect(_titleRect, usableWidth, titleHeight, topDividerHeight);
        }

        if (_panelRect != null)
        {
            SetFixedTopRect(_panelRect, usableWidth, newPanelHeight, topDividerHeight + titleHeight + middleDividerHeight);
        }

        if (mapContainerRect != null)
        {
            SetFixedTopRect(mapContainerRect, usableWidth, newMapHeight, 0f);
        }

        if (footerRect != null)
        {
            footerRect.offsetMin = new Vector2(0f, 0f);
            footerRect.offsetMax = new Vector2(usableWidth, footerHeight);
        }

        if (scrollViewRect != null)
        {
            scrollViewRect.sizeDelta = new Vector2(newViewportWidth, newViewportHeight);
            scrollViewRect.anchoredPosition3D = Vector3.zero;
        }

        for (int i = 0; i < dividerRects.Count; i++)
        {
            float dividerTop = i == 0 ? 0f : topDividerHeight + titleHeight;
            SetFixedTopRect(dividerRects[i], usableWidth, 8f, dividerTop);
        }

        MinimapFullscreenToggle.LogInfo($"Applied maximized layout width={usableWidth:0.#} totalHeight={totalHeight:0.#} panelHeight={newPanelHeight:0.#} mapHeight={newMapHeight:0.#} viewport={newViewportWidth:0.#}x{newViewportHeight:0.#}.");
    }

    private void SaveRectState(RectTransform rect)
    {
        if (rect == null)
        {
            return;
        }

        for (int i = 0; i < _modifiedRects.Count; i++)
        {
            if (ReferenceEquals(_modifiedRects[i].Rect, rect))
            {
                return;
            }
        }

        _modifiedRects.Add(new RectTransformState
        {
            Rect = rect,
            AnchorMin = rect.anchorMin,
            AnchorMax = rect.anchorMax,
            OffsetMin = rect.offsetMin,
            OffsetMax = rect.offsetMax,
            Pivot = rect.pivot,
            SizeDelta = rect.sizeDelta,
            AnchoredPosition3D = rect.anchoredPosition3D,
            LocalScale = rect.localScale,
            LocalRotation = rect.localRotation
        });
    }

    private void RestoreModifiedRects()
    {
        for (int i = _modifiedRects.Count - 1; i >= 0; i--)
        {
            RectTransformState state = _modifiedRects[i];
            if (state.Rect == null)
            {
                continue;
            }

            state.Rect.anchorMin = state.AnchorMin;
            state.Rect.anchorMax = state.AnchorMax;
            state.Rect.offsetMin = state.OffsetMin;
            state.Rect.offsetMax = state.OffsetMax;
            state.Rect.pivot = state.Pivot;
            state.Rect.sizeDelta = state.SizeDelta;
            state.Rect.anchoredPosition3D = state.AnchoredPosition3D;
            state.Rect.localScale = state.LocalScale;
            state.Rect.localRotation = state.LocalRotation;
        }

        _modifiedRects.Clear();
    }

    private void SaveBehaviourState(Behaviour behaviour)
    {
        if (behaviour == null)
        {
            return;
        }

        for (int i = 0; i < _modifiedBehaviours.Count; i++)
        {
            if (ReferenceEquals(_modifiedBehaviours[i].Behaviour, behaviour))
            {
                return;
            }
        }

        _modifiedBehaviours.Add(new BehaviourState
        {
            Behaviour = behaviour,
            Enabled = behaviour.enabled
        });
    }

    private void RestoreModifiedBehaviours()
    {
        for (int i = _modifiedBehaviours.Count - 1; i >= 0; i--)
        {
            BehaviourState state = _modifiedBehaviours[i];
            if (state.Behaviour != null)
            {
                state.Behaviour.enabled = state.Enabled;
            }
        }

        _modifiedBehaviours.Clear();
    }

    private void DisableLayoutDrivers(RectTransform rect)
    {
        if (rect == null)
        {
            return;
        }

        LayoutGroup layoutGroup = rect.GetComponent<LayoutGroup>();
        if (layoutGroup != null)
        {
            SaveBehaviourState(layoutGroup);
            layoutGroup.enabled = false;
        }

        ContentSizeFitter fitter = rect.GetComponent<ContentSizeFitter>();
        if (fitter != null)
        {
            SaveBehaviourState(fitter);
            fitter.enabled = false;
        }
    }

    private static void SetFixedTopRect(RectTransform rect, float width, float height, float topOffset)
    {
        if (rect == null)
        {
            return;
        }

        rect.offsetMin = new Vector2(0f, -topOffset - height);
        rect.offsetMax = new Vector2(width, -topOffset);
    }

    public void DumpDebugTree(string reason)
    {
        if (_panelRect == null)
        {
            _panelRect = GetComponent<RectTransform>();
        }

        ResolveTargets();

        StringBuilder sb = new StringBuilder(4096);
        sb.AppendLine($"===== Minimap dump: {reason} =====");
        sb.AppendLine(DescribeNode("panel", _panelRect));
        sb.AppendLine(DescribeNode("title", _titleRect));
        sb.AppendLine(DescribeNode("target", _targetRect));
        sb.AppendLine(DescribeNode("close", _closeButton != null ? _closeButton.GetComponent<RectTransform>() : null));
        sb.AppendLine(DescribeScrollState());
        sb.AppendLine("Ancestor chain:");
        AppendAncestorChain(sb, _panelRect);
        sb.AppendLine("Subtree from panel parent:");
        Transform subtreeRoot = _panelRect != null && _panelRect.parent != null ? _panelRect.parent : _panelRect;
        AppendTree(sb, subtreeRoot, 0, 4, 12);
        sb.AppendLine("Buttons near panel parent:");
        AppendButtons(sb, subtreeRoot);
        MinimapFullscreenToggle.LogInfo(sb.ToString().TrimEnd());
    }

    private void ApplyRelativeMinimapZoom(float factor, string reason)
    {
        if (_scrollControls == null)
        {
            _scrollControls = _minimapUI != null ? _minimapUI.GetComponentInChildren<MinimapScrollViewControls>(includeInactive: true) : null;
        }

        if (_scrollControls == null || Mathf.Approximately(factor, 1f))
        {
            return;
        }

        Traverse scroll = Traverse.Create(_scrollControls);
        RectTransform content = scroll.Field("_content").GetValue<RectTransform>();
        if (content == null)
        {
            MinimapFullscreenToggle.LogWarn($"Minimap zoom adjustment skipped during {reason}: no content rect.");
            return;
        }

        float currentScale = scroll.Field("_currentScale").GetValue<float>();
        float targetScale = scroll.Field("_targetScale").GetValue<float>();
        float newCurrentScale = Mathf.Max(0.01f, currentScale * factor);
        float newTargetScale = Mathf.Max(0.01f, targetScale * factor);
        content.anchoredPosition *= factor;
        content.localScale = Vector3.one * newCurrentScale;
        scroll.Field("_currentScale").SetValue(newCurrentScale);
        scroll.Field("_targetScale").SetValue(newTargetScale);
        MinimapFullscreenToggle.LogInfo($"Adjusted minimap zoom during {reason}: factor={factor:0.###} current={currentScale:0.###}->{newCurrentScale:0.###} target={targetScale:0.###}->{newTargetScale:0.###} anchored={content.anchoredPosition.x:0.#},{content.anchoredPosition.y:0.#}.");
    }

    private string DescribeScrollState()
    {
        if (_scrollControls == null)
        {
            _scrollControls = _minimapUI != null ? _minimapUI.GetComponentInChildren<MinimapScrollViewControls>(includeInactive: true) : null;
        }

        if (_scrollControls == null)
        {
            return "scroll: <none>";
        }

        Traverse scroll = Traverse.Create(_scrollControls);
        RectTransform content = scroll.Field("_content").GetValue<RectTransform>();
        float currentScale = scroll.Field("_currentScale").GetValue<float>();
        float targetScale = scroll.Field("_targetScale").GetValue<float>();
        Vector2 anchoredPosition = content != null ? content.anchoredPosition : Vector2.zero;
        Vector3 localScale = content != null ? content.localScale : Vector3.zero;
        return $"scroll: current={currentScale:0.###} target={targetScale:0.###} contentPos={anchoredPosition.x:0.#},{anchoredPosition.y:0.#} contentScale={localScale.x:0.###},{localScale.y:0.###},{localScale.z:0.###}";
    }

    private static Button FindCloseLikeButton(Transform root, bool includeInactive)
    {
        if (root == null)
        {
            return null;
        }

        Button[] buttons = root.GetComponentsInChildren<Button>(includeInactive);
        foreach (Button button in buttons)
        {
            if (!includeInactive && !button.gameObject.activeInHierarchy)
            {
                continue;
            }

            string n = button.name.ToLowerInvariant();
            if (n.Contains("close") || n.Contains("cross") || n.Contains("exit") || n.Contains("cancel") || n == "x")
            {
                return button;
            }
        }

        return null;
    }

    private static RectTransform FindActiveTitleSibling(RectTransform panelRect)
    {
        Transform parent = panelRect != null ? panelRect.parent : null;
        if (parent == null)
        {
            return null;
        }

        string panelName = panelRect.name.ToLowerInvariant();
        RectTransform fallback = null;
        for (int i = 0; i < parent.childCount; i++)
        {
            RectTransform child = parent.GetChild(i) as RectTransform;
            if (child == null || !child.gameObject.activeInHierarchy)
            {
                continue;
            }

            string childName = child.name.ToLowerInvariant();
            if (!childName.Contains("hudpaneltitle"))
            {
                continue;
            }

            if (childName.Contains(panelName))
            {
                return child;
            }

            fallback = child;
        }

        return fallback;
    }

    private static RectTransform FindChildRect(RectTransform parent, string childName)
    {
        if (parent == null)
        {
            return null;
        }

        for (int i = 0; i < parent.childCount; i++)
        {
            RectTransform child = parent.GetChild(i) as RectTransform;
            if (child != null && child.name == childName)
            {
                return child;
            }
        }

        return null;
    }

    private static RectTransform FindDescendantRect(Transform root, string childName)
    {
        if (root == null)
        {
            return null;
        }

        foreach (RectTransform rect in root.GetComponentsInChildren<RectTransform>(includeInactive: true))
        {
            if (rect != null && rect.name == childName)
            {
                return rect;
            }
        }

        return null;
    }

    private static List<RectTransform> FindDirectChildren(RectTransform parent, string childName)
    {
        List<RectTransform> result = new List<RectTransform>();
        if (parent == null)
        {
            return result;
        }

        for (int i = 0; i < parent.childCount; i++)
        {
            RectTransform child = parent.GetChild(i) as RectTransform;
            if (child != null && child.name == childName)
            {
                result.Add(child);
            }
        }

        return result;
    }

    private static Rect GetActiveDirectChildrenBounds(RectTransform root)
    {
        if (root == null)
        {
            return new Rect(0f, 0f, 1f, 1f);
        }

        bool hasBounds = false;
        Vector2 min = Vector2.zero;
        Vector2 max = Vector2.zero;

        for (int i = 0; i < root.childCount; i++)
        {
            RectTransform child = root.GetChild(i) as RectTransform;
            if (child == null || !child.gameObject.activeInHierarchy)
            {
                continue;
            }

            Vector3[] corners = new Vector3[4];
            child.GetWorldCorners(corners);
            for (int c = 0; c < corners.Length; c++)
            {
                Vector3 local = root.InverseTransformPoint(corners[c]);
                Vector2 point = new Vector2(local.x, local.y);
                if (!hasBounds)
                {
                    min = point;
                    max = point;
                    hasBounds = true;
                }
                else
                {
                    min = Vector2.Min(min, point);
                    max = Vector2.Max(max, point);
                }
            }
        }

        if (!hasBounds)
        {
            Rect fallback = root.rect;
            return new Rect(fallback.xMin, fallback.yMin, Mathf.Max(1f, fallback.width), Mathf.Max(1f, fallback.height));
        }

        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }

    private static void AppendAncestorChain(StringBuilder sb, Transform start)
    {
        if (start == null)
        {
            sb.AppendLine("  <none>");
            return;
        }

        Transform current = start;
        int depth = 0;
        while (current != null && depth < 12)
        {
            sb.Append("  ");
            sb.Append(depth);
            sb.Append(": ");
            sb.AppendLine(DescribeNode(GetPath(current), current as RectTransform));
            current = current.parent;
            depth++;
        }
    }

    private static void AppendTree(StringBuilder sb, Transform node, int depth, int maxDepth, int maxChildren)
    {
        if (node == null)
        {
            sb.AppendLine("  <none>");
            return;
        }

        sb.Append(' ', depth * 2);
        sb.Append("- ");
        sb.AppendLine(DescribeNode(GetPath(node), node as RectTransform));

        if (depth >= maxDepth)
        {
            return;
        }

        int childCount = node.childCount;
        int limit = Mathf.Min(childCount, maxChildren);
        for (int i = 0; i < limit; i++)
        {
            AppendTree(sb, node.GetChild(i), depth + 1, maxDepth, maxChildren);
        }

        if (childCount > limit)
        {
            sb.Append(' ', (depth + 1) * 2);
            sb.AppendLine($"... {childCount - limit} more children");
        }
    }

    private static void AppendButtons(StringBuilder sb, Transform root)
    {
        if (root == null)
        {
            sb.AppendLine("  <none>");
            return;
        }

        Button[] buttons = root.GetComponentsInChildren<Button>(includeInactive: true);
        if (buttons.Length == 0)
        {
            sb.AppendLine("  <none>");
            return;
        }

        int limit = Mathf.Min(buttons.Length, 24);
        for (int i = 0; i < limit; i++)
        {
            Button button = buttons[i];
            RectTransform rect = button.GetComponent<RectTransform>();
            sb.Append("  - ");
            sb.Append(GetPath(button.transform));
            sb.Append(" | ");
            sb.Append(GetRectSummary(rect));
            sb.Append(" | active=");
            sb.Append(button.gameObject.activeInHierarchy);
            sb.Append(" | closeLike=");
            sb.Append(IsCloseLikeName(button.name));
            sb.AppendLine();
        }

        if (buttons.Length > limit)
        {
            sb.AppendLine($"  ... {buttons.Length - limit} more buttons");
        }
    }

    private static string DescribeNode(string label, RectTransform rect)
    {
        if (rect == null)
        {
            return $"{label}: <none>";
        }

        return $"{label}: {GetPath(rect)} | {GetRectSummary(rect)} | activeSelf={rect.gameObject.activeSelf} activeInHierarchy={rect.gameObject.activeInHierarchy} | comps={GetInterestingComponents(rect.gameObject)}";
    }

    private static string GetInterestingComponents(GameObject go)
    {
        List<string> names = new List<string>(8);
        if (go.GetComponent<Canvas>() != null) names.Add("Canvas");
        if (go.GetComponent<GraphicRaycaster>() != null) names.Add("GraphicRaycaster");
        if (go.GetComponent<CanvasScaler>() != null) names.Add("CanvasScaler");
        if (go.GetComponent<Button>() != null) names.Add("Button");
        if (go.GetComponent<Image>() != null) names.Add("Image");
        if (go.GetComponent<RawImage>() != null) names.Add("RawImage");
        if (go.GetComponent<ScrollRect>() != null) names.Add("ScrollRect");
        if (go.GetComponent<LayoutGroup>() != null) names.Add("LayoutGroup");
        if (go.GetComponent<ContentSizeFitter>() != null) names.Add("ContentSizeFitter");
        if (go.GetComponent<MinimapUI>() != null) names.Add("MinimapUI");
        return names.Count > 0 ? string.Join(",", names) : "<none>";
    }

    private static string GetRectSummary(RectTransform rect)
    {
        if (rect == null)
        {
            return "rect=<none>";
        }

        Rect r = rect.rect;
        return $"rect=({r.width:0.#}x{r.height:0.#}) anchorMin={FormatVector2(rect.anchorMin)} anchorMax={FormatVector2(rect.anchorMax)} offsetMin={FormatVector2(rect.offsetMin)} offsetMax={FormatVector2(rect.offsetMax)} pivot={FormatVector2(rect.pivot)}";
    }

    private static string FormatVector2(Vector2 value)
    {
        return $"({value.x:0.###},{value.y:0.###})";
    }

    private static string GetPath(Transform transform)
    {
        if (transform == null)
        {
            return "<null>";
        }

        Stack<string> segments = new Stack<string>();
        Transform current = transform;
        int guard = 0;
        while (current != null && guard < 64)
        {
            segments.Push(current.name);
            current = current.parent;
            guard++;
        }

        return string.Join("/", segments.ToArray());
    }

    private static bool IsCloseLikeName(string name)
    {
        string n = name.ToLowerInvariant();
        return n.Contains("close") || n.Contains("cross") || n.Contains("exit") || n.Contains("cancel") || n == "x";
    }

    private static GameObject CreateOverlay(Transform rootCanvasTransform)
    {
        GameObject overlay = new GameObject("DimavaMinimapFullscreenOverlay", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        overlay.transform.SetParent(rootCanvasTransform, false);
        overlay.transform.SetAsLastSibling();

        RectTransform overlayRect = overlay.GetComponent<RectTransform>();
        overlayRect.anchorMin = Vector2.zero;
        overlayRect.anchorMax = Vector2.one;
        overlayRect.offsetMin = Vector2.zero;
        overlayRect.offsetMax = Vector2.zero;

        Image overlayImage = overlay.GetComponent<Image>();
        overlayImage.color = new Color(0f, 0f, 0f, 0.72f);
        overlayImage.raycastTarget = true;

        return overlay;
    }
}

[HarmonyPatch(typeof(MinimapUI), "Awake")]
static class MinimapFullscreenToggle_MinimapUI_Awake_Patch
{
    static void Postfix(MinimapUI __instance)
    {
        MinimapFullscreenToggleController controller = __instance.GetComponent<MinimapFullscreenToggleController>();
        if (controller == null)
        {
            controller = __instance.gameObject.AddComponent<MinimapFullscreenToggleController>();
        }

        controller.Setup(__instance);
    }
}

[HarmonyPatch(typeof(MinimapUI), "ShowPanel")]
static class MinimapFullscreenToggle_MinimapUI_ShowPanel_Patch
{
    static void Postfix(MinimapUI __instance)
    {
        MinimapFullscreenToggleController controller = __instance.GetComponent<MinimapFullscreenToggleController>();
        if (controller == null)
        {
            controller = __instance.gameObject.AddComponent<MinimapFullscreenToggleController>();
        }

        controller.Setup(__instance);
    }
}
