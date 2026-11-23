using System;
using System.Collections.Generic;
using System.IO;
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
        private const string LegacyBookmarksPrefsKey = "MF.WebBrowser.Editor.Bookmarks";
        private const string BookmarksFileName = "MF.WebBrowser.Bookmarks.json";
        private const string ProjectScopeName = "프로젝트";
        private const string PersonalScopeName = "개인";
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
        private VisualElement projectBookmarkItems;
        private VisualElement projectBookmarkActions;
        private VisualElement personalBookmarkItems;
        private VisualElement personalBookmarkActions;
        private VisualElement bookmarkEditorOverlay;
        private Label bookmarkEditorTitle;
        private Label bookmarkPreviewGlyph;
        private TextField bookmarkNameField;
        private PopupField<string> bookmarkScopeField;
        private PopupField<string> bookmarkFolderField;
        private VisualElement bookmarkNewFolderRow;
        private TextField bookmarkNewFolderField;
        private Label statusLabel;
        private BookmarkCollection projectBookmarks = new();
        private BookmarkCollection personalBookmarks = new();
        private BookmarkEntry editingBookmark;
        private BookmarkScope editingScope;
        private bool editingBookmarkIsNew;

        private enum BookmarkScope
        {
            Project,
            Personal
        }

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

        private void OnFocus()
        {
            // Multiple browser windows can edit these files; re-read on focus.
            if (editingBookmark != null)
                return;

            projectBookmarks = LoadBookmarks(BookmarkScope.Project);
            personalBookmarks = LoadBookmarks(BookmarkScope.Personal);
            RefreshBookmarkBars();
            UpdateBookmarkToggle();
        }

        public void CreateGUI()
        {
            rootVisualElement.Clear();
            rootVisualElement.AddToClassList("web-browser-root");
            projectBookmarks = LoadBookmarks(BookmarkScope.Project);
            personalBookmarks = LoadBookmarks(BookmarkScope.Personal);

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

            rootVisualElement.Add(CreateBookmarkBar(
                BookmarkScope.Project,
                out projectBookmarkItems,
                out projectBookmarkActions));
            rootVisualElement.Add(CreateBookmarkBar(
                BookmarkScope.Personal,
                out personalBookmarkItems,
                out personalBookmarkActions));
            RefreshBookmarkBars();

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

        private static VisualElement CreateBookmarkBar(
            BookmarkScope scope,
            out VisualElement items,
            out VisualElement actions)
        {
            var project = scope == BookmarkScope.Project;
            var bar = new VisualElement
            {
                name = project ? "projectBookmarkBar" : "personalBookmarkBar"
            };
            bar.AddToClassList("web-browser-bookmark-bar");
            bar.AddToClassList(project
                ? "web-browser-bookmark-bar--project"
                : "web-browser-bookmark-bar--personal");

            var label = new Label(project ? "▦  프로젝트" : "♙  개인")
            {
                tooltip = project
                    ? "프로젝트 전체에 공유되는 북마크 (ProjectSettings)"
                    : "이 프로젝트에서 나만 사용하는 북마크 (UserSettings)"
            };
            label.AddToClassList("web-browser-bookmark-icon");
            bar.Add(label);

            items = new VisualElement
            {
                name = project ? "projectBookmarkItems" : "personalBookmarkItems"
            };
            items.AddToClassList("web-browser-bookmark-items");
            bar.Add(items);

            actions = new VisualElement
            {
                name = project ? "projectBookmarkActions" : "personalBookmarkActions"
            };
            actions.AddToClassList("web-browser-bookmark-actions");
            bar.Add(actions);
            return bar;
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

            var scopeRow = new VisualElement();
            scopeRow.AddToClassList("bookmark-dialog-field-row");
            var scopeLabel = new Label("저장 위치");
            scopeLabel.AddToClassList("bookmark-dialog-field-label");
            scopeRow.Add(scopeLabel);

            bookmarkScopeField = new PopupField<string>(
                new List<string> { PersonalScopeName, ProjectScopeName }, 0)
            {
                name = "bookmarkScopeField"
            };
            bookmarkScopeField.AddToClassList("bookmark-dialog-folder");
            bookmarkScopeField.RegisterValueChangedCallback(evt =>
            {
                if (editingBookmark != null)
                    RefreshEditorFolderChoices(bookmarkFolderField.value);
            });
            scopeRow.Add(bookmarkScopeField);
            fields.Add(scopeRow);

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

            // Prefer the personal bookmark if a URL is saved in both scopes.
            var bookmark = FindBookmark(url, BookmarkScope.Personal);
            var scope = BookmarkScope.Personal;
            if (bookmark == null)
            {
                bookmark = FindBookmark(url, BookmarkScope.Project);
                if (bookmark != null)
                    scope = BookmarkScope.Project;
            }

            var isNew = bookmark == null;
            bookmark ??= new BookmarkEntry(GetDefaultTitle(url), url, BookmarkBarFolder);
            OpenBookmarkEditor(bookmark, isNew, scope);
        }

        private void OpenBookmarkEditor(
            BookmarkEntry bookmark,
            bool isNew,
            BookmarkScope scope)
        {
            if (bookmark == null || bookmarkEditorOverlay == null)
                return;

            editingBookmark = bookmark;
            editingScope = scope;
            editingBookmarkIsNew = isNew;
            bookmarkEditorTitle.text = isNew ? "북마크 추가" : "북마크 수정";
            bookmarkNameField.SetValueWithoutNotify(bookmark.Title);
            bookmarkScopeField.SetValueWithoutNotify(
                scope == BookmarkScope.Project ? ProjectScopeName : PersonalScopeName);
            RefreshEditorFolderChoices(NormalizeFolder(bookmark.Folder));
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

        private void RefreshEditorFolderChoices(string preferredFolder)
        {
            var scope = ScopeFromName(bookmarkScopeField.value);
            var choices = BuildBookmarkFolderChoices(scope);
            var folder = NormalizeFolder(preferredFolder);
            if (!choices.Contains(folder))
                choices.Insert(choices.Count - 2, folder);
            bookmarkFolderField.choices = choices;
            bookmarkFolderField.SetValueWithoutNotify(folder);
            ToggleNewFolderField(false);
        }

        private List<string> BuildBookmarkFolderChoices(BookmarkScope scope)
        {
            var choices = new List<string> { BookmarkBarFolder };
            foreach (var bookmark in GetBookmarks(scope).Items)
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

        private static BookmarkScope ScopeFromName(string name)
        {
            return name == ProjectScopeName ? BookmarkScope.Project : BookmarkScope.Personal;
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
                var targetScope = ScopeFromName(bookmarkScopeField.value);
                var duplicate = FindBookmark(editingBookmark.Url, targetScope);
                if (duplicate != null && !ReferenceEquals(duplicate, editingBookmark))
                {
                    SetStatus("이 저장 위치에 같은 URL의 북마크가 이미 있습니다.", true);
                    return;
                }

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
                var moved = !editingBookmarkIsNew && targetScope != editingScope;
                if (moved)
                    GetBookmarks(editingScope).Items.Remove(editingBookmark);
                if (editingBookmarkIsNew || moved)
                    GetBookmarks(targetScope).Items.Add(editingBookmark);

                if (moved)
                    SaveBookmarks(editingScope);
                SaveBookmarks(targetScope);
                RefreshBookmarkBars();
                UpdateBookmarkToggle();
                SetStatus(
                    editingBookmarkIsNew ? "Bookmark added" : "Bookmark updated", false);
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

            if (!editingBookmarkIsNew)
            {
                GetBookmarks(editingScope).Items.Remove(editingBookmark);
                SaveBookmarks(editingScope);
            }

            editingBookmark = null;
            editingBookmarkIsNew = false;
            RefreshBookmarkBars();
            UpdateBookmarkToggle();
            bookmarkEditorOverlay.AddToClassList("bookmark-dialog-overlay--hidden");
            SetStatus("Bookmark removed", false);
        }

        private void RefreshBookmarkBars()
        {
            RefreshBookmarkBar(
                BookmarkScope.Project, projectBookmarkItems, projectBookmarkActions);
            RefreshBookmarkBar(
                BookmarkScope.Personal, personalBookmarkItems, personalBookmarkActions);
        }

        private void RefreshBookmarkBar(
            BookmarkScope scope,
            VisualElement items,
            VisualElement actions)
        {
            if (items == null || actions == null)
                return;

            items.Clear();
            actions.Clear();

            var visibleEntryCount = 0;
            var folderOrder = new List<string>();
            var folderGroups = new Dictionary<string, List<BookmarkEntry>>(
                StringComparer.Ordinal);
            foreach (var bookmark in GetBookmarks(scope).Items)
            {
                var folder = NormalizeFolder(bookmark.Folder);
                if (folder == BookmarkBarFolder)
                {
                    visibleEntryCount++;
                    items.Add(CreateBookmarkButton(bookmark, scope));
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
                items.Add(CreateBookmarkFolderMenu(folder, folderGroups[folder]));
            }

            if (visibleEntryCount == 0)
            {
                var emptyLabel = new Label(
                    scope == BookmarkScope.Project
                        ? "공유 북마크 없음"
                        : "개인 북마크 없음 — ☆로 추가");
                emptyLabel.AddToClassList("web-browser-bookmark-empty");
                items.Add(emptyLabel);
            }

            if (folderGroups.TryGetValue(OtherBookmarksFolder, out var otherBookmarks))
            {
                var otherMenu = CreateBookmarkFolderMenu(
                    OtherBookmarksFolder, otherBookmarks);
                otherMenu.AddToClassList("web-browser-other-bookmarks");
                actions.Add(otherMenu);
            }
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

        private Button CreateBookmarkButton(BookmarkEntry bookmark, BookmarkScope scope)
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
                evt.menu.AppendAction("Edit…", _ => OpenBookmarkEditor(bookmark, false, scope));
                evt.menu.AppendAction(
                    "Copy URL",
                    _ => EditorGUIUtility.systemCopyBuffer = bookmark.Url);
                evt.menu.AppendSeparator();
                evt.menu.AppendAction("Remove", _ => RemoveBookmark(bookmark, scope));
            }));
            return button;
        }

        private void RemoveBookmark(BookmarkEntry bookmark, BookmarkScope scope)
        {
            GetBookmarks(scope).Items.Remove(bookmark);
            SaveBookmarks(scope);
            RefreshBookmarkBars();
            UpdateBookmarkToggle();
            SetStatus("Bookmark removed", false);
        }

        private void UpdateBookmarkToggle()
        {
            if (bookmarkToggleButton == null)
                return;

            var bookmarked =
                FindBookmark(addressField?.value, BookmarkScope.Personal) != null ||
                FindBookmark(addressField?.value, BookmarkScope.Project) != null;
            bookmarkToggleButton.text = bookmarked ? "★" : "☆";
            bookmarkToggleButton.EnableInClassList(
                "web-browser-bookmark-toggle--active", bookmarked);
            bookmarkToggleButton.tooltip = bookmarked
                ? "Edit bookmark"
                : "Bookmark this page";
        }

        private BookmarkEntry FindBookmark(string url, BookmarkScope scope)
        {
            var normalized = NormalizeUrl(url);
            if (string.IsNullOrEmpty(normalized))
                return null;

            return GetBookmarks(scope).Items.Find(bookmark => string.Equals(
                NormalizeUrl(bookmark.Url),
                normalized,
                StringComparison.OrdinalIgnoreCase));
        }

        private BookmarkCollection GetBookmarks(BookmarkScope scope)
        {
            return scope == BookmarkScope.Project ? projectBookmarks : personalBookmarks;
        }

        private static string GetBookmarkFilePath(BookmarkScope scope)
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var folder = scope == BookmarkScope.Project ? "ProjectSettings" : "UserSettings";
            return Path.Combine(projectRoot ?? ".", folder, BookmarksFileName);
        }

        private void SaveBookmarks(BookmarkScope scope)
        {
            var path = GetBookmarkFilePath(scope);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(GetBookmarks(scope), true));
            }
            catch (Exception exception) when (
                exception is IOException || exception is UnauthorizedAccessException)
            {
                Debug.LogError($"[MF Web Browser] Failed to save bookmarks to {path}: {exception}");
                SetStatus("북마크 저장에 실패했습니다. Console을 확인하세요.", true);
            }
        }

        private static BookmarkCollection LoadBookmarks(BookmarkScope scope)
        {
            var path = GetBookmarkFilePath(scope);
            if (File.Exists(path))
            {
                try
                {
                    return DeserializeBookmarks(File.ReadAllText(path));
                }
                catch (Exception exception) when (
                    exception is IOException ||
                    exception is UnauthorizedAccessException ||
                    exception is ArgumentException)
                {
                    Debug.LogError($"[MF Web Browser] Failed to read bookmarks from {path}: {exception}");
                    return new BookmarkCollection();
                }
            }

            if (scope == BookmarkScope.Personal &&
                EditorPrefs.HasKey(LegacyBookmarksPrefsKey))
            {
                try
                {
                    // One-time migration from the old global EditorPrefs key.
                    // The original preference is retained for recovery.
                    var migrated = DeserializeBookmarks(
                        EditorPrefs.GetString(LegacyBookmarksPrefsKey));
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, JsonUtility.ToJson(migrated, true));
                    return migrated;
                }
                catch (Exception exception) when (
                    exception is IOException ||
                    exception is UnauthorizedAccessException ||
                    exception is ArgumentException)
                {
                    Debug.LogError($"[MF Web Browser] Bookmark migration failed: {exception}");
                }
            }

            return new BookmarkCollection();
        }

        private static BookmarkCollection DeserializeBookmarks(string json)
        {
            var collection = string.IsNullOrWhiteSpace(json)
                ? new BookmarkCollection()
                : JsonUtility.FromJson<BookmarkCollection>(json) ?? new BookmarkCollection();
            collection.Items ??= new List<BookmarkEntry>();
            collection.Items.RemoveAll(item => item == null || string.IsNullOrWhiteSpace(item.Url));
            foreach (var bookmark in collection.Items)
                bookmark.Folder = NormalizeFolder(bookmark.Folder);
            return collection;
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
