using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace MF.WebBrowser.Editor
{
    public sealed class WebBrowserWindow : EditorWindow
    {
        private const string DefaultUrl = "https://www.google.com";
        private const string StyleSheetPath =
            "Packages/com.mine-farm.editor-webview/Editor/WebBrowserWindow.uss";

        private FixedWebViewElement browser;
        private TextField addressField;
        private Label statusLabel;

        [MenuItem("Window/MF/Web Browser")]
        private static void Open()
        {
            GetWindow<WebBrowserWindow>();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("Web Browser");
            minSize = new Vector2(480f, 320f);
            AssemblyReloadEvents.beforeAssemblyReload += DisposeBrowser;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private void OnDisable()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= DisposeBrowser;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            DisposeBrowser();
        }

        public void CreateGUI()
        {
            rootVisualElement.Clear();
            rootVisualElement.AddToClassList("web-browser-root");

            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (styleSheet != null && !rootVisualElement.styleSheets.Contains(styleSheet))
                rootVisualElement.styleSheets.Add(styleSheet);

            var toolbar = new VisualElement();
            toolbar.AddToClassList("web-browser-toolbar");

            toolbar.Add(CreateButton("◀", "Back", () => browser?.GoBack()));
            toolbar.Add(CreateButton("▶", "Forward", () => browser?.GoForward()));
            toolbar.Add(CreateButton("↻", "Refresh", () => browser?.Refresh()));
            toolbar.Add(CreateButton("⌂", "Home", () => Navigate(DefaultUrl)));

            addressField = new TextField
            {
                name = "addressField",
                value = DefaultUrl
            };
            addressField.AddToClassList("web-browser-address");
            addressField.RegisterCallback<KeyDownEvent>(OnAddressKeyDown);
            toolbar.Add(addressField);

            var goButton = new Button(() => Navigate(addressField.value))
            {
                name = "goButton",
                text = "Go"
            };
            goButton.AddToClassList("web-browser-go-button");
            toolbar.Add(goButton);
            rootVisualElement.Add(toolbar);

            browser = new FixedWebViewElement(DefaultUrl)
            {
                name = "webBrowser"
            };
            browser.AddToClassList("web-browser-content");
            browser.NavigationCompleted += OnNavigationCompleted;
            rootVisualElement.Add(browser);

            statusLabel = new Label("Starting WebView…")
            {
                name = "statusLabel"
            };
            statusLabel.AddToClassList("web-browser-status");
            rootVisualElement.Add(statusLabel);
        }

        private static Button CreateButton(string text, string tooltip, System.Action clicked)
        {
            var button = new Button(clicked)
            {
                text = text,
                tooltip = tooltip
            };
            button.AddToClassList("web-browser-nav-button");
            return button;
        }

        private void OnAddressKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter)
                return;

            Navigate(addressField.value);
            evt.StopPropagation();
        }

        private void Navigate(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;

            if (!url.Contains("://"))
                url = $"https://{url}";

            addressField.SetValueWithoutNotify(url);
            statusLabel.text = $"Loading {url}";
            browser?.Navigate(url);
        }

        private void OnNavigationCompleted(string url, bool succeeded)
        {
            if (!string.IsNullOrEmpty(url))
                addressField?.SetValueWithoutNotify(url);

            if (statusLabel != null)
                statusLabel.text = succeeded ? "Ready" : $"Failed to load {url}";
        }

        private void DisposeBrowser()
        {
            if (browser == null)
                return;

            browser.NavigationCompleted -= OnNavigationCompleted;
            browser.Dispose();
            browser = null;
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.ExitingEditMode:
                    browser?.Suspend();
                    if (statusLabel != null)
                        statusLabel.text = "Paused during Play Mode";
                    break;

                case PlayModeStateChange.EnteredEditMode:
                    if (statusLabel != null)
                        statusLabel.text = "Starting WebView…";
                    browser?.Resume();
                    break;
            }
        }
    }
}
