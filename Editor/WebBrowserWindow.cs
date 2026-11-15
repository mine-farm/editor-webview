using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace MF.WebBrowser.Editor
{
    [EditorWindowTitle(title = "Web Browser", icon = "BuildSettings.Web.Small")]
    public sealed class WebBrowserWindow : EditorWindow
    {
        private const string DefaultUrl = "https://www.google.com";
        private const string BookmarksPrefsKey = "MF.WebBrowser.Editor.Bookmarks";
        private const string BookmarkBarFolder = "북마크 바";
        private const string OtherBookmarksFolder = "기타 북마크";
        private const string NewFolderOption = "새 폴더 만들기…";
        private const string StyleSheetPath =
            "Packages/com.mine-farm.editor-webview/Editor/WebBrowserWindow.uss";

        private static readonly List<string> DefaultBookmarkFolders = new()
        {
            BookmarkBarFolder,
            OtherBookmarksFolder,
            NewFolderOption
        };

        private FixedWebViewElement browser;
        private TextField addressField;
        private Button bookmarkToggleButton;
        private VisualElement bookmarkItems;
        private VisualElement bookmarkBarActions;
        private VisualElement bookmarkEditorOverlay;
        private Label bookmarkEditorTitle;
        private Label bookmarkPreviewGlyph;
        private TextField bookmarkNameField;
        private PopupField<string> bookmarkFolderField;
        private VisualElement bookmarkNewFolderRow;
        private TextField bookmarkNewFolderField;
        private Label statusLabel;
        private BookmarkCollection bookmarkCollection = new();
        private BookmarkEntry editingBookmark;
        private bool editingBookmarkIsNew;

        [MenuItem("Window/MF/Web Browser")]
        private static void Open()
        {
            var window = CreateWindow<WebBrowserWindow>(
                "Web Browser",
                typeof(WebBrowserWindow),
                typeof(SceneView));
            window.Show();
            window.Focus();
        }

        private void OnEnable()
        {
            titleContent = EditorGUIUtility.TrTextContentWithIcon(
                "Web Browser",
                "BuildSettings.Web.Small");
            minSize = new Vector2(560f, 360f);
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

            var navigation = new VisualElement();
            navigation.AddToClassList("web-browser-navigation");
            navigation.Add(CreateButton("←", "Back", () => browser?.GoBack()));
            navigation.Add(CreateButton("→", "Forward", () => browser?.GoForward()));
            navigation.Add(CreateButton("↻", "Refresh", () => browser?.Refresh()));
            navigation.Add(CreateButton("+", "New browser tab", Open));
            toolbar.Add(navigation);

            var omnibox = new VisualElement
            {
                name = "omnibox"
            };
            omnibox.AddToClassList("web-browser-omnibox");

            var siteBadge = new Label("◉")
            {
                name = "siteBadge",
                tooltip = "Site information"
            };
            siteBadge.AddToClassList("web-browser-site-badge");
            omnibox.Add(siteBadge);

            addressField = new TextField
            {
                name = "addressField",
                value = DefaultUrl
            };
            addressField.AddToClassList("web-browser-address");
            addressField.RegisterCallback<KeyDownEvent>(OnAddressKeyDown);
            omnibox.Add(addressField);

            bookmarkToggleButton = new Button(ToggleCurrentBookmark)
            {
                name = "bookmarkToggleButton",
                text = "☆",
                tooltip = "Bookmark this page"
            };
            bookmarkToggleButton.AddToClassList("web-browser-bookmark-toggle");
            omnibox.Add(bookmarkToggleButton);

            var goButton = new Button(() => Navigate(addressField.value))
            {
                name = "goButton",
                text = "↵",
                tooltip = "Go to address"
            };
            goButton.AddToClassList("web-browser-go-button");
            omnibox.Add(goButton);
            toolbar.Add(omnibox);
            rootVisualElement.Add(toolbar);

            var bookmarkBar = new VisualElement
            {
                name = "bookmarkBar"
            };
            bookmarkBar.AddToClassList("web-browser-bookmark-bar");

            var bookmarkIcon = new Label("▦")
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

            bookmarkBarActions = new VisualElement
            {
                name = "bookmarkBarActions"
            };
            bookmarkBarActions.AddToClassList("web-browser-bookmark-actions");
            bookmarkBar.Add(bookmarkBarActions);
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

            CreateBookmarkEditor();
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
            SetStatus($"Loading {url}", true);
            browser?.Navigate(url);
        }

        private void CreateBookmarkEditor()
        {
            bookmarkEditorOverlay = new VisualElement
            {
                name = "bookmarkEditorOverlay",
                focusable = true
            };
            bookmarkEditorOverlay.AddToClassList("bookmark-dialog-overlay");
            bookmarkEditorOverlay.AddToClassList("bookmark-dialog-overlay--hidden");
            bookmarkEditorOverlay.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (ReferenceEquals(evt.target, bookmarkEditorOverlay))
                    FinishBookmarkEditor(true);
            });
            bookmarkEditorOverlay.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.Escape)
                    return;

                FinishBookmarkEditor(true);
                evt.StopPropagation();
            });

            var dialog = new VisualElement
            {
                name = "bookmarkEditorDialog"
            };
            dialog.AddToClassList("bookmark-dialog");

            var header = new VisualElement();
            header.AddToClassList("bookmark-dialog-header");

            bookmarkEditorTitle = new Label("북마크 추가됨")
            {
                name = "bookmarkEditorTitle"
            };
            bookmarkEditorTitle.AddToClassList("bookmark-dialog-title");
            header.Add(bookmarkEditorTitle);

            var closeButton = new Button(() => FinishBookmarkEditor(true))
            {
                name = "bookmarkEditorCloseButton",
                text = "×",
                tooltip = "닫기"
            };
            closeButton.AddToClassList("bookmark-dialog-close");
            header.Add(closeButton);
            dialog.Add(header);

            var body = new VisualElement();
            body.AddToClassList("bookmark-dialog-body");

            var preview = new VisualElement();
            preview.AddToClassList("bookmark-dialog-preview");
            bookmarkPreviewGlyph = new Label("★")
            {
                name = "bookmarkPreviewGlyph"
            };
            bookmarkPreviewGlyph.AddToClassList("bookmark-dialog-preview-glyph");
            preview.Add(bookmarkPreviewGlyph);
            body.Add(preview);

            var fields = new VisualElement();
            fields.AddToClassList("bookmark-dialog-fields");

            var nameRow = new VisualElement();
            nameRow.AddToClassList("bookmark-dialog-field-row");
            var nameLabel = new Label("이름");
            nameLabel.AddToClassList("bookmark-dialog-field-label");
            nameRow.Add(nameLabel);

            bookmarkNameField = new TextField
            {
                name = "bookmarkNameField"
            };
            bookmarkNameField.AddToClassList("bookmark-dialog-input");
            bookmarkNameField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter)
                    return;

                FinishBookmarkEditor(true);
                evt.StopPropagation();
            });
            nameRow.Add(bookmarkNameField);
            fields.Add(nameRow);

            var folderRow = new VisualElement();
            folderRow.AddToClassList("bookmark-dialog-field-row");
            var folderLabel = new Label("폴더");
            folderLabel.AddToClassList("bookmark-dialog-field-label");
            folderRow.Add(folderLabel);

            bookmarkFolderField = new PopupField<string>(
                new List<string>(DefaultBookmarkFolders),
                0)
            {
                name = "bookmarkFolderField"
            };
            bookmarkFolderField.AddToClassList("bookmark-dialog-folder");
            bookmarkFolderField.RegisterValueChangedCallback(evt =>
                ToggleNewFolderField(evt.newValue == NewFolderOption));
            folderRow.Add(bookmarkFolderField);
            fields.Add(folderRow);

            bookmarkNewFolderRow = new VisualElement
            {
                name = "bookmarkNewFolderRow"
            };
            bookmarkNewFolderRow.AddToClassList("bookmark-dialog-field-row");
            bookmarkNewFolderRow.AddToClassList("bookmark-dialog-new-folder--hidden");
            var newFolderLabel = new Label("새 폴더");
            newFolderLabel.AddToClassList("bookmark-dialog-field-label");
            bookmarkNewFolderRow.Add(newFolderLabel);

            bookmarkNewFolderField = new TextField
            {
                name = "bookmarkNewFolderField"
            };
            bookmarkNewFolderField.AddToClassList("bookmark-dialog-input");
            bookmarkNewFolderField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter)
                    return;

                FinishBookmarkEditor(true);
                evt.StopPropagation();
            });
            bookmarkNewFolderRow.Add(bookmarkNewFolderField);
            fields.Add(bookmarkNewFolderRow);

            body.Add(fields);
            dialog.Add(body);

            var footer = new VisualElement();
            footer.AddToClassList("bookmark-dialog-footer");

            var doneButton = new Button(() => FinishBookmarkEditor(true))
            {
                name = "bookmarkDoneButton",
                text = "완료"
            };
            doneButton.AddToClassList("bookmark-dialog-button");
            doneButton.AddToClassList("bookmark-dialog-done");
            footer.Add(doneButton);

            var deleteButton = new Button(DeleteEditingBookmark)
            {
                name = "bookmarkDeleteButton",
                text = "삭제"
            };
            deleteButton.AddToClassList("bookmark-dialog-button");
            deleteButton.AddToClassList("bookmark-dialog-delete");
            footer.Add(deleteButton);

            dialog.Add(footer);
            bookmarkEditorOverlay.Add(dialog);
            rootVisualElement.Add(bookmarkEditorOverlay);
        }

        private void ToggleCurrentBookmark()
        {
            var url = NormalizeUrl(addressField?.value);
            if (string.IsNullOrEmpty(url))
                return;

            var index = FindBookmarkIndex(url);
            var isNew = index < 0;
            BookmarkEntry bookmark;
            if (isNew)
            {
                bookmark = new BookmarkEntry(GetDefaultTitle(url), url, BookmarkBarFolder);
                bookmarkCollection.Items.Add(bookmark);
                SetStatus("Bookmark added", false);
                SaveBookmarks();
                RefreshBookmarkBar();
                UpdateBookmarkToggle();
            }
            else
            {
                bookmark = bookmarkCollection.Items[index];
            }

            OpenBookmarkEditor(bookmark, isNew);
        }

        private void OpenBookmarkEditor(BookmarkEntry bookmark, bool isNew)
        {
            if (bookmark == null || bookmarkEditorOverlay == null)
                return;

            editingBookmark = bookmark;
            editingBookmarkIsNew = isNew;
            bookmarkEditorTitle.text = isNew ? "북마크 추가됨" : "북마크 수정";
            bookmarkNameField.SetValueWithoutNotify(bookmark.Title);
            var normalizedFolder = NormalizeFolder(bookmark.Folder);
            var folderChoices = BuildBookmarkFolderChoices();
            if (!folderChoices.Contains(normalizedFolder))
                folderChoices.Insert(folderChoices.Count - 2, normalizedFolder);
            bookmarkFolderField.choices = folderChoices;
            bookmarkFolderField.SetValueWithoutNotify(normalizedFolder);
            bookmarkNewFolderField.SetValueWithoutNotify(string.Empty);
            ToggleNewFolderField(false);
            bookmarkPreviewGlyph.text = GetBookmarkGlyph(bookmark.Url);
            bookmarkEditorOverlay.RemoveFromClassList("bookmark-dialog-overlay--hidden");
            bookmarkEditorOverlay.BringToFront();
            bookmarkNameField.schedule.Execute(() =>
            {
                bookmarkNameField.Focus();
                bookmarkNameField.SelectAll();
            });
        }

        private List<string> BuildBookmarkFolderChoices()
        {
            var choices = new List<string> { BookmarkBarFolder };
            foreach (var bookmark in bookmarkCollection.Items)
            {
                var folder = NormalizeFolder(bookmark.Folder);
                if (folder == BookmarkBarFolder ||
                    folder == OtherBookmarksFolder ||
                    choices.Contains(folder))
                {
                    continue;
                }

                choices.Add(folder);
            }

            choices.Add(OtherBookmarksFolder);
            choices.Add(NewFolderOption);
            return choices;
        }

        private void ToggleNewFolderField(bool visible)
        {
            if (bookmarkNewFolderRow == null)
                return;

            bookmarkNewFolderRow.EnableInClassList(
                "bookmark-dialog-new-folder--hidden",
                !visible);
            if (!visible)
                return;

            bookmarkNewFolderField.schedule.Execute(() =>
            {
                bookmarkNewFolderField.Focus();
                bookmarkNewFolderField.SelectAll();
            });
        }

        private void FinishBookmarkEditor(bool saveChanges)
        {
            if (editingBookmark == null)
                return;

            if (saveChanges)
            {
                editingBookmark.Title = string.IsNullOrWhiteSpace(bookmarkNameField.value)
                    ? GetDefaultTitle(editingBookmark.Url)
                    : bookmarkNameField.value.Trim();
                var selectedFolder = bookmarkFolderField.value;
                if (selectedFolder == NewFolderOption)
                {
                    selectedFolder = string.IsNullOrWhiteSpace(bookmarkNewFolderField.value)
                        ? BookmarkBarFolder
                        : bookmarkNewFolderField.value.Trim();
                }

                editingBookmark.Folder = NormalizeFolder(selectedFolder);
                SaveBookmarks();
                RefreshBookmarkBar();
                UpdateBookmarkToggle();
                SetStatus(
                    editingBookmarkIsNew ? "Bookmark added" : "Bookmark updated",
                    false);
            }

            editingBookmark = null;
            editingBookmarkIsNew = false;
            bookmarkNewFolderField.SetValueWithoutNotify(string.Empty);
            ToggleNewFolderField(false);
            bookmarkEditorOverlay.AddToClassList("bookmark-dialog-overlay--hidden");
        }

        private void DeleteEditingBookmark()
        {
            if (editingBookmark == null)
                return;

            bookmarkCollection.Items.Remove(editingBookmark);
            editingBookmark = null;
            editingBookmarkIsNew = false;
            SaveBookmarks();
            RefreshBookmarkBar();
            UpdateBookmarkToggle();
            bookmarkEditorOverlay.AddToClassList("bookmark-dialog-overlay--hidden");
            SetStatus("Bookmark removed", false);
        }

        private void RefreshBookmarkBar()
        {
            if (bookmarkItems == null || bookmarkBarActions == null)
                return;

            bookmarkItems.Clear();
            bookmarkBarActions.Clear();

            var visibleEntryCount = 0;
            var folderOrder = new List<string>();
            var folderGroups = new Dictionary<string, List<BookmarkEntry>>(
                StringComparer.Ordinal);
            foreach (var bookmark in bookmarkCollection.Items)
            {
                var folder = NormalizeFolder(bookmark.Folder);
                if (folder == BookmarkBarFolder)
                {
                    visibleEntryCount++;
                    bookmarkItems.Add(CreateBookmarkButton(bookmark));
                    continue;
                }

                if (!folderGroups.TryGetValue(folder, out var entries))
                {
                    entries = new List<BookmarkEntry>();
                    folderGroups.Add(folder, entries);
                    folderOrder.Add(folder);
                }

                entries.Add(bookmark);
            }

            foreach (var folder in folderOrder)
            {
                if (folder == OtherBookmarksFolder)
                    continue;

                visibleEntryCount++;
                bookmarkItems.Add(CreateBookmarkFolderMenu(folder, folderGroups[folder]));
            }

            if (visibleEntryCount == 0)
            {
                var emptyLabel = new Label("북마크 없음 — ☆를 눌러 추가");
                emptyLabel.AddToClassList("web-browser-bookmark-empty");
                bookmarkItems.Add(emptyLabel);
            }

            if (!folderGroups.TryGetValue(OtherBookmarksFolder, out var otherBookmarks))
                return;

            var otherMenu = CreateBookmarkFolderMenu(
                OtherBookmarksFolder,
                otherBookmarks);
            otherMenu.AddToClassList("web-browser-other-bookmarks");
            bookmarkBarActions.Add(otherMenu);
        }

        private ToolbarMenu CreateBookmarkFolderMenu(
            string folderName,
            List<BookmarkEntry> bookmarks)
        {
            var folderMenu = new ToolbarMenu
            {
                text = $"▱  {folderName}",
                tooltip = $"{folderName} · {bookmarks.Count}개 북마크"
            };
            folderMenu.AddToClassList("web-browser-bookmark-folder");
            foreach (var bookmark in bookmarks)
            {
                var capturedBookmark = bookmark;
                folderMenu.menu.AppendAction(
                    $"{GetBookmarkGlyph(capturedBookmark.Url)}   {capturedBookmark.Title}",
                    _ => Navigate(capturedBookmark.Url));
            }

            return folderMenu;
        }

        private Button CreateBookmarkButton(BookmarkEntry bookmark)
        {
            var button = new Button(() => Navigate(bookmark.Url))
            {
                text = string.Empty,
                tooltip = bookmark.Url
            };
            button.AddToClassList("web-browser-bookmark-entry");

            var icon = new Label(GetBookmarkGlyph(bookmark.Url));
            icon.AddToClassList("web-browser-bookmark-entry-icon");
            button.Add(icon);

            var title = new Label(bookmark.Title);
            title.AddToClassList("web-browser-bookmark-entry-title");
            button.Add(title);

            button.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                evt.menu.AppendAction("Open", _ => Navigate(bookmark.Url));
                evt.menu.AppendAction("Edit…", _ => OpenBookmarkEditor(bookmark, false));
                evt.menu.AppendAction(
                    "Copy URL",
                    _ => EditorGUIUtility.systemCopyBuffer = bookmark.Url);
                evt.menu.AppendSeparator();
                evt.menu.AppendAction("Remove", _ => RemoveBookmark(bookmark));
            }));
            return button;
        }

        private void RemoveBookmark(BookmarkEntry bookmark)
        {
            bookmarkCollection.Items.Remove(bookmark);
            SaveBookmarks();
            RefreshBookmarkBar();
            UpdateBookmarkToggle();
            SetStatus("Bookmark removed", false);
        }

        private void UpdateBookmarkToggle()
        {
            if (bookmarkToggleButton == null)
                return;

            var bookmarked = FindBookmarkIndex(addressField?.value) >= 0;
            bookmarkToggleButton.text = bookmarked ? "★" : "☆";
            bookmarkToggleButton.EnableInClassList(
                "web-browser-bookmark-toggle--active",
                bookmarked);
            bookmarkToggleButton.tooltip = bookmarked
                ? "Edit bookmark"
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
                foreach (var bookmark in collection.Items)
                    bookmark.Folder = NormalizeFolder(bookmark.Folder);
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

        private static string NormalizeFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) ||
                string.Equals(folder, NewFolderOption, StringComparison.Ordinal))
            {
                return BookmarkBarFolder;
            }

            var trimmed = folder.Trim();
            if (string.Equals(trimmed, BookmarkBarFolder, StringComparison.Ordinal))
                return BookmarkBarFolder;
            if (string.Equals(trimmed, OtherBookmarksFolder, StringComparison.Ordinal))
                return OtherBookmarksFolder;

            return trimmed;
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

        private static string GetBookmarkGlyph(string url)
        {
            var title = GetDefaultTitle(url);
            return string.IsNullOrEmpty(title)
                ? "★"
                : char.ToUpperInvariant(title[0]).ToString();
        }

        private void SetStatus(string message, bool visible)
        {
            if (statusLabel == null)
                return;

            statusLabel.text = message;
            statusLabel.EnableInClassList("web-browser-status--hidden", !visible);
        }

        private void OnNavigationCompleted(string url, bool succeeded)
        {
            if (!string.IsNullOrEmpty(url))
                addressField?.SetValueWithoutNotify(url);

            SetStatus(succeeded ? "Ready" : $"Failed to load {url}", !succeeded);

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
                    SetStatus("Paused during Play Mode", true);
                    break;

                case PlayModeStateChange.EnteredEditMode:
                    SetStatus("Starting WebView…", true);
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
            public BookmarkEntry(string title, string url, string folder)
            {
                Title = title;
                Url = url;
                Folder = folder;
            }

            public string Title;
            public string Url;
            public string Folder;
        }
    }
}
