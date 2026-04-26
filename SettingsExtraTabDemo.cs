using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Presentation.UI;
using Presentation.UI.Menus.FullscreenPage;
using ScriptEngine;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// @Name: Settings ScriptEngine Tab
// @Description: Adds a Settings tab with ScriptEngine mod enable toggles.
// @Version: 1.1.0
// @Author: Dimava

[ScriptEntry]
public sealed class SettingsExtraTabDemo : ScriptMod
{
    private const string TabObjectName = "TabScriptEngine_Injected";
    private const string TabButtonName = "SettingsTabScriptEngine_Injected";
    private const string RowsObjectName = "ScriptEngineRows";

    internal static SettingsExtraTabDemo Instance;

    private readonly List<InjectedTab> _injectedTabs = new List<InjectedTab>();
    private readonly List<Toggle> _toggles = new List<Toggle>();
    private TMP_Text _templateText;

    protected override void OnEnable()
    {
        Instance = this;
        InjectAll();
    }

    protected override void OnDisable()
    {
        CleanupAllKnownMenus();

        if (ReferenceEquals(Instance, this))
        {
            Instance = null;
        }
    }

    internal void InjectAll()
    {
        foreach (SettingsMenu menu in Resources.FindObjectsOfTypeAll<SettingsMenu>())
        {
            Inject(menu);
        }
    }

    private void Inject(SettingsMenu menu)
    {
        if (menu == null || _injectedTabs.Any(tab => tab.Menu == menu))
        {
            return;
        }

        RemoveOrphanObjects(menu);

        Traverse menuTraverse = Traverse.Create(menu);
        GameObject[] oldTabs = menuTraverse.Field("_tabs").GetValue<GameObject[]>();
        PageButton[] oldButtons = menuTraverse.Field("_tabButtons").GetValue<PageButton[]>();
        ScrollRect scrollRect = menuTraverse.Field("_scrollRect").GetValue<ScrollRect>();

        if (oldTabs == null || oldButtons == null || oldTabs.Length == 0 || oldButtons.Length == 0 || scrollRect == null)
        {
            Warn("Settings tab injection skipped: menu fields were not ready.");
            return;
        }

        oldTabs = oldTabs.Where(tab => tab != null && tab.name != TabObjectName).ToArray();
        oldButtons = oldButtons.Where(button => button != null && button.name != TabButtonName).ToArray();

        _templateText = menu.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(text => text != null);

        int tabIndex = oldTabs.Length;
        GameObject tabObject = CreateTabContent(oldTabs[0]);
        PageButton tabButton = CreateTabButton(oldButtons[oldButtons.Length - 1], tabIndex);

        menuTraverse.Field("_tabs").SetValue(oldTabs.Concat(new[] { tabObject }).ToArray());
        menuTraverse.Field("_tabButtons").SetValue(oldButtons.Concat(new[] { tabButton }).ToArray());

        tabObject.SetActive(false);
        tabButton.ID = tabIndex;
        tabButton.ActiveState = false;
        tabButton.OnClick += OpenSettingsTab;

        _injectedTabs.Add(new InjectedTab(menu, oldTabs, oldButtons, tabObject, tabButton, tabIndex));
        Log("Injected Settings tab at index " + tabIndex + ".");
    }

    private GameObject CreateTabContent(GameObject templateTab)
    {
        var tab = new GameObject(TabObjectName);
        tab.transform.SetParent(templateTab.transform.parent, false);

        var rect = tab.AddComponent<RectTransform>();
        CopyRect(rect, templateTab.transform as RectTransform);

        var layout = tab.AddComponent<VerticalLayoutGroup>();
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        layout.spacing = 14f;
        layout.padding = new RectOffset(20, 20, 20, 20);

        var fitter = tab.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        AddText(tab.transform, "ScriptEngine Mods", 30f, FontStyles.Bold);

        var rows = new GameObject(RowsObjectName);
        rows.transform.SetParent(tab.transform, false);
        var rowsRect = rows.AddComponent<RectTransform>();
        rowsRect.anchorMin = new Vector2(0f, 1f);
        rowsRect.anchorMax = new Vector2(1f, 1f);
        rowsRect.pivot = new Vector2(0f, 1f);
        rowsRect.sizeDelta = new Vector2(0f, 0f);

        var rowsLayout = rows.AddComponent<VerticalLayoutGroup>();
        rowsLayout.childAlignment = TextAnchor.UpperLeft;
        rowsLayout.childControlWidth = true;
        rowsLayout.childControlHeight = true;
        rowsLayout.childForceExpandWidth = true;
        rowsLayout.childForceExpandHeight = false;
        rowsLayout.spacing = 8f;

        var rowsFitter = rows.AddComponent<ContentSizeFitter>();
        rowsFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        RebuildRows(rows.transform);

        return tab;
    }

    private void RebuildRows(Transform rows)
    {
        _toggles.Clear();
        foreach (ScriptModRecord info in ScriptEngineMod.Mods.Values)
        {
            AddModToggle(rows, info);
        }
    }

    private void AddModToggle(Transform parent, ScriptModRecord info)
    {
        var row = new GameObject("ModRow_" + SafeName(info.Id));
        row.transform.SetParent(parent, false);

        var rect = row.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.sizeDelta = new Vector2(0f, 36f);

        var layout = row.AddComponent<HorizontalLayoutGroup>();
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = false;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        layout.spacing = 10f;

        var rowElement = row.AddComponent<LayoutElement>();
        rowElement.preferredHeight = 36f;

        Toggle toggle = CreateToggle(row.transform, info.Enabled.Value);
        _toggles.Add(toggle);

        TMP_Text label = AddText(row.transform, info.Id + (info.IsLoaded ? "  [loaded]" : ""), 18f, info.HasError ? FontStyles.Bold : FontStyles.Normal);
        var labelElement = label.gameObject.GetComponent<LayoutElement>();
        labelElement.preferredWidth = 850f;
        labelElement.flexibleWidth = 1f;

        if (info.HasError)
        {
            label.color = Color.red;
        }

        toggle.onValueChanged.AddListener(value =>
        {
            info.Enabled.Value = value;
            Log((value ? "Enabled " : "Disabled ") + info.Id);
        });
    }

    private Toggle CreateToggle(Transform parent, bool value)
    {
        var go = new GameObject("Enabled");
        go.transform.SetParent(parent, false);

        var rect = go.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(28f, 28f);

        var image = go.AddComponent<Image>();
        image.color = new Color(0.1f, 0.12f, 0.14f, 0.95f);

        var toggle = go.AddComponent<Toggle>();
        toggle.targetGraphic = image;

        var checkGo = new GameObject("Checkmark");
        checkGo.transform.SetParent(go.transform, false);
        var checkRect = checkGo.AddComponent<RectTransform>();
        checkRect.anchorMin = new Vector2(0.2f, 0.2f);
        checkRect.anchorMax = new Vector2(0.8f, 0.8f);
        checkRect.offsetMin = Vector2.zero;
        checkRect.offsetMax = Vector2.zero;
        var checkImage = checkGo.AddComponent<Image>();
        checkImage.color = new Color(0.1f, 0.85f, 0.45f, 1f);
        toggle.graphic = checkImage;
        toggle.SetIsOnWithoutNotify(value);

        var element = go.AddComponent<LayoutElement>();
        element.preferredWidth = 28f;
        element.preferredHeight = 28f;

        return toggle;
    }

    private PageButton CreateTabButton(PageButton template, int tabIndex)
    {
        PageButton button = UnityEngine.Object.Instantiate(template, template.transform.parent);
        button.name = TabButtonName;
        button.ID = tabIndex;
        button.ActiveState = false;
        button.ShowButton();

        foreach (TMP_Text text in button.GetComponentsInChildren<TMP_Text>(true))
        {
            text.SetText("Scripts");
        }

        return button;
    }

    private TMP_Text AddText(Transform parent, string value, float size, FontStyles style)
    {
        var go = new GameObject("Text");
        go.transform.SetParent(parent, false);

        var rect = go.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.sizeDelta = new Vector2(0f, size * 2.1f);

        TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
        if (_templateText != null)
        {
            text.font = _templateText.font;
            text.color = _templateText.color;
        }

        text.fontSize = size;
        text.fontStyle = style;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.SetText(value);

        var layout = go.AddComponent<LayoutElement>();
        layout.preferredHeight = size * 1.8f;

        return text;
    }

    private void CleanupAllKnownMenus()
    {
        foreach (Toggle toggle in _toggles)
        {
            if (toggle != null)
            {
                toggle.onValueChanged.RemoveAllListeners();
            }
        }

        _toggles.Clear();

        foreach (InjectedTab injected in _injectedTabs.ToArray())
        {
            Remove(injected);
        }

        _injectedTabs.Clear();

        foreach (SettingsMenu menu in Resources.FindObjectsOfTypeAll<SettingsMenu>())
        {
            RemoveOrphanObjects(menu);
        }
    }

    private void Remove(InjectedTab injected)
    {
        if (injected.Menu != null)
        {
            Traverse menuTraverse = Traverse.Create(injected.Menu);
            ScrollRect scrollRect = menuTraverse.Field("_scrollRect").GetValue<ScrollRect>();
            int currentTabIndex = menuTraverse.Field("_currentTabIndex").GetValue<int>();

            if (currentTabIndex >= injected.TabIndex)
            {
                menuTraverse.Field("_currentTabIndex").SetValue(0);
                if (injected.OriginalTabs.Length > 0 && injected.OriginalTabs[0] != null)
                {
                    injected.OriginalTabs[0].SetActive(true);
                    if (scrollRect != null)
                    {
                        scrollRect.content = injected.OriginalTabs[0].transform as RectTransform;
                        scrollRect.normalizedPosition = Vector2.one;
                    }
                }
            }

            for (int i = 0; i < injected.OriginalButtons.Length; i++)
            {
                if (injected.OriginalButtons[i] != null)
                {
                    injected.OriginalButtons[i].ID = i;
                    injected.OriginalButtons[i].ActiveState = i == 0;
                }
            }

            if (injected.Button != null)
            {
                injected.Button.OnClick -= OpenSettingsTab;
            }

            menuTraverse.Field("_tabs").SetValue(injected.OriginalTabs);
            menuTraverse.Field("_tabButtons").SetValue(injected.OriginalButtons);
        }

        DestroyIfAlive(injected.Button != null ? injected.Button.gameObject : null);
        DestroyIfAlive(injected.TabObject);
    }

    private void RemoveOrphanObjects(SettingsMenu menu)
    {
        if (menu == null)
        {
            return;
        }

        Traverse menuTraverse = Traverse.Create(menu);
        GameObject[] tabs = menuTraverse.Field("_tabs").GetValue<GameObject[]>();
        PageButton[] buttons = menuTraverse.Field("_tabButtons").GetValue<PageButton[]>();

        if (tabs != null)
        {
            menuTraverse.Field("_tabs").SetValue(tabs.Where(tab => tab != null && tab.name != TabObjectName).ToArray());
        }

        if (buttons != null)
        {
            menuTraverse.Field("_tabButtons").SetValue(buttons.Where(button => button != null && button.name != TabButtonName).ToArray());
        }

        foreach (Transform child in menu.GetComponentsInChildren<Transform>(true))
        {
            if (child != null && (child.name == TabObjectName || child.name == TabButtonName))
            {
                DestroyIfAlive(child.gameObject);
            }
        }
    }

    private void OpenSettingsTab(int tabIndex)
    {
        InjectedTab injected = _injectedTabs.FirstOrDefault(tab => tab.Menu != null && tab.TabIndex == tabIndex);
        if (injected == null)
        {
            return;
        }

        Traverse.Create(injected.Menu).Method("OpenSettingsTab", tabIndex).GetValue();
    }

    private static void DestroyIfAlive(GameObject go)
    {
        if (go != null)
        {
            go.SetActive(false);
            UnityEngine.Object.Destroy(go);
        }
    }

    private static void CopyRect(RectTransform target, RectTransform source)
    {
        if (source == null)
        {
            target.anchorMin = Vector2.zero;
            target.anchorMax = Vector2.one;
            target.offsetMin = Vector2.zero;
            target.offsetMax = Vector2.zero;
            return;
        }

        target.anchorMin = source.anchorMin;
        target.anchorMax = source.anchorMax;
        target.anchoredPosition = source.anchoredPosition;
        target.sizeDelta = source.sizeDelta;
        target.pivot = source.pivot;
        target.localScale = source.localScale;
    }

    private static string SafeName(string value)
    {
        return new string(value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
    }

    private sealed class InjectedTab
    {
        public readonly SettingsMenu Menu;
        public readonly GameObject[] OriginalTabs;
        public readonly PageButton[] OriginalButtons;
        public readonly GameObject TabObject;
        public readonly PageButton Button;
        public readonly int TabIndex;

        public InjectedTab(SettingsMenu menu, GameObject[] originalTabs, PageButton[] originalButtons, GameObject tabObject, PageButton button, int tabIndex)
        {
            Menu = menu;
            OriginalTabs = originalTabs;
            OriginalButtons = originalButtons;
            TabObject = tabObject;
            Button = button;
            TabIndex = tabIndex;
        }
    }
}

[HarmonyPatch(typeof(SettingsMenu), "Awake")]
static class SettingsExtraTabDemo_SettingsMenu_Awake_Patch
{
    static void Postfix()
    {
        SettingsExtraTabDemo.Instance?.InjectAll();
    }
}
