using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace MF.WebBrowser.Editor
{
    public sealed class WebBrowserWindow : EditorWindow
    {
        private const string DefaultUrl = "https://www.google.com";
        private const string BookmarksPrefsKey = "MF.WebBrowser.Editor.Bookmarks";
        private const string StyleSheetPath =
            "Packages/com.mine-farm.editor-webview/Editor/WebBrowserWindow.uss";

        private FixedWebViewElement browser;
        private TextField addressField;
        private Button bookmarkToggleButton;
        private VisualElement bookmarkItems;
        private Label statusLabel;
        private BookmarkCollection bookmarkCollection = new();

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
            bookmarkCollection = LoadBookmarks();

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

            bookmarkToggleButton = new Button(ToggleCurrentBookmark)
            {
                name = "bookmarkToggleButton",
                text = "☆",
                tooltip = "Bookmark this page"
            };
            bookmarkToggleButton.AddToClassList("web-browser-bookmark-toggle");
            toolbar.Add(bookmarkToggleButton);

            var goButton = new Button(() => Navigate(addressField.value))
            {
                name = "goButton",
                text = "Go"
            };
            goButton.AddToClassList("web-browser-go-button");
            toolbar.Add(goButton);
            rootVisualElement.Add(toolbar);

            var bookmarkBar = new VisualElement
            {
                name = "bookmarkBar"
            };
            bookmarkBar.AddToClassList("web-browser-bookmark-bar");

            var bookmarkIcon = new Label("★")
            {
                tooltip = "Bookmarks"
            };
            bookmarkIcon.AddToClassList("web-browser-bookmark-icon");
            bookmarkBar.Add(bookmarkIcon);

            bookmarkItems = new VisualElement
            {
                name = "bookmarkItems"
            };
            bookmarkItems.AddToClassList("web-browser-bookmark-items");
            bookmarkBar.Add(bookmarkItems);
            rootVisualElement.Add(bookmarkBar);
            RefreshBookmarkBar();

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
            UpdateBookmarkToggle();
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

        private void ToggleCurrentBookmark()
        {
            var url = NormalizeUrl(addressField?.value);
            if (string.IsNullOrEmpty(url))
                return;

            var index = FindBookmarkIndex(url);
            if (index >= 0)
            {
                bookmarkCollection.Items.RemoveAt(index);
                statusLabel.text = "Bookmark removed";
            }
            else
            {
                bookmarkCollection.Items.Add(new BookmarkEntry(GetDefaultTitle(url), url));
                statusLabel.text = "Bookmark added";
            }

            SaveBookmarks();
            RefreshBookmarkBar();
            UpdateBookmarkToggle();
        }

        private void RefreshBookmarkBar()
        {
            if (bookmarkItems == null)
                return;

            bookmarkItems.Clear();
            if (bookmarkCollection.Items.Count == 0)
            {
                var emptyLabel = new Label("No bookmarks — click ☆ to add this page");
                emptyLabel.AddToClassList("web-browser-bookmark-empty");
                bookmarkItems.Add(emptyLabel);
                return;
            }

            foreach (var bookmark in bookmarkCollection.Items)
            {
                var button = new Button(() => Navigate(bookmark.Url))
                {
                    text = bookmark.Title,
                    tooltip = bookmark.Url
                };
                button.AddToClassList("web-browser-bookmark-entry");
                button.AddManipulator(new ContextualMenuManipulator(evt =>
                {
                    evt.menu.AppendAction("Open", _ => Navigate(bookmark.Url));
                    evt.menu.AppendAction("Rename", _ => BeginRename(bookmark, button));
                    evt.menu.AppendAction(
                        "Copy URL",
                        _ => EditorGUIUtility.systemCopyBuffer = bookmark.Url);
                    evt.menu.AppendSeparator();
                    evt.menu.AppendAction("Remove", _ => RemoveBookmark(bookmark));
                }));
                bookmarkItems.Add(button);
            }
        }

        private void BeginRename(BookmarkEntry bookmark, VisualElement currentElement)
        {
            var index = bookmarkItems.IndexOf(currentElement);
            if (index < 0)
                return;

            var renameField = new TextField
            {
                value = bookmark.Title,
                tooltip = bookmark.Url
            };
            renameField.AddToClassList("web-browser-bookmark-rename");

            var finished = false;
            void FinishRename(bool save)
            {
                if (finished)
                    return;

                finished = true;
                if (save && !string.IsNullOrWhiteSpace(renameField.value))
                {
                    bookmark.Title = renameField.value.Trim();
                    SaveBookmarks();
                }

                RefreshBookmarkBar();
            }

            renameField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    FinishRename(true);
                    evt.StopPropagation();
                }
                else if (evt.keyCode == KeyCode.Escape)
                {
                    FinishRename(false);
                    evt.StopPropagation();
                }
            });
            renameField.RegisterCallback<FocusOutEvent>(_ => FinishRename(true));

            bookmarkItems.RemoveAt(index);
            bookmarkItems.Insert(index, renameField);
            renameField.schedule.Execute(() =>
            {
                renameField.Focus();
                renameField.SelectAll();
            });
        }

        private void RemoveBookmark(BookmarkEntry bookmark)
        {
            bookmarkCollection.Items.Remove(bookmark);
            SaveBookmarks();
            RefreshBookmarkBar();
            UpdateBookmarkToggle();
            statusLabel.text = "Bookmark removed";
        }

        private void UpdateBookmarkToggle()
        {
            if (bookmarkToggleButton == null)
                return;

            var bookmarked = FindBookmarkIndex(addressField?.value) >= 0;
            bookmarkToggleButton.text = bookmarked ? "★" : "☆";
            bookmarkToggleButton.tooltip = bookmarked
                ? "Remove bookmark"
                : "Bookmark this page";
        }

        private int FindBookmarkIndex(string url)
        {
            var normalized = NormalizeUrl(url);
            if (string.IsNullOrEmpty(normalized))
                return -1;

            return bookmarkCollection.Items.FindIndex(
                bookmark => string.Equals(
                    NormalizeUrl(bookmark.Url),
                    normalized,
                    StringComparison.OrdinalIgnoreCase));
        }

        private void SaveBookmarks()
        {
            EditorPrefs.SetString(BookmarksPrefsKey, JsonUtility.ToJson(bookmarkCollection));
        }

        private static BookmarkCollection LoadBookmarks()
        {
            var json = EditorPrefs.GetString(BookmarksPrefsKey, string.Empty);
            if (string.IsNullOrEmpty(json))
                return new BookmarkCollection();

            try
            {
                var collection = JsonUtility.FromJson<BookmarkCollection>(json) ??
                                 new BookmarkCollection();
                collection.Items ??= new List<BookmarkEntry>();
                return collection;
            }
            catch (ArgumentException)
            {
                return new BookmarkCollection();
            }
        }

        private static string NormalizeUrl(string url)
        {
            return string.IsNullOrWhiteSpace(url) ? string.Empty : url.Trim().TrimEnd('/');
        }

        private static string GetDefaultTitle(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var host = uri.Host;
                return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                    ? host.Substring(4)
                    : host;
            }

            return url.Length <= 28 ? url : $"{url.Substring(0, 27)}…";
        }

        private void OnNavigationCompleted(string url, bool succeeded)
        {
            if (!string.IsNullOrEmpty(url))
                addressField?.SetValueWithoutNotify(url);

            if (statusLabel != null)
                statusLabel.text = succeeded ? "Ready" : $"Failed to load {url}";

            UpdateBookmarkToggle();
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

        [Serializable]
        private sealed class BookmarkCollection
        {
            public List<BookmarkEntry> Items = new();
        }

        [Serializable]
        private sealed class BookmarkEntry
        {
            public BookmarkEntry(string title, string url)
            {
                Title = title;
                Url = url;
            }

            public string Title;
            public string Url;
        }
    }
}
