# Editor WebView

A dockable web browser window for the Unity Editor, powered by
[WebView Toolkit](https://github.com/cantetfelix/WebViewToolkit).

## Requirements

- Unity 6000.3 or newer
- Windows with Direct3D 11 or Direct3D 12
- Microsoft Edge WebView2 Runtime
- WebView Toolkit 1.3.0

## Installation

Add both Git dependencies to the Unity project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.webviewtoolkit.core": "https://github.com/cantetfelix/WebViewToolkit.git#upm",
    "com.mine-farm.editor-webview": "https://github.com/mine-farm/editor-webview.git"
  }
}
```

Unity only supports Git dependencies in a project manifest, so WebView Toolkit
must be listed directly alongside this package.

## Usage

Open `Window > MF > Web Browser` in the Unity Editor.

The window provides a Chrome-inspired toolbar with back and forward navigation,
refresh, a rounded omnibox, and a persistent bookmark bar. Click `☆` inside the
omnibox to add the current page and open the
bookmark editor, where its name and destination folder can be changed. Bookmark
bar entries remain visible for one-click navigation; entries moved to `Other
Bookmarks` are available from the folder menu at the right of the bar. Choose
`새 폴더 만들기…` in the bookmark editor to create a named folder. Named folders are
shown directly on the bookmark bar and open their bookmark list when clicked.
Right-click a bookmark to edit it, copy its URL, or remove it. The bookmark area now
has separate **프로젝트** (shared) and **개인** (personal) rows. Choose the destination
in the bookmark editor when adding or editing a bookmark; changing its destination
moves it between the rows. **프로젝트** bookmarks are saved to
`ProjectSettings/MF.WebBrowser.Bookmarks.json` so the JSON can be committed to Git.
**개인** bookmarks are stored in `UserSettings/MF.WebBrowser.Bookmarks.json` and
should remain local and Git-ignored. Existing bookmarks from the older global
EditorPrefs key are automatically copied into the personal collection on first use;
the legacy preference is not deleted. A URL can exist once in each collection.
Use the `+` toolbar
button or open `Window > MF > Web Browser` again to create another independent
browser tab. Open WebViews are suspended while entering Play Mode.
The fixed 1920x1080 backing texture is always displayed at a 16:9 aspect ratio;
unused space is filled with black letterboxing or pillarboxing.

## Bookmark storage

- Share the project bookmark JSON through Git to give teammates the same bookmarks.
- Keep the Unity project's `UserSettings/` folder out of version control.
- The star button adds bookmarks to the personal bar by default; use the
  **저장 위치** dropdown to choose **프로젝트** instead.
- When the same URL exists in both scopes, the star editor opens the personal copy.
- Existing global bookmarks are imported to personal bookmarks only if the new
  personal JSON does not already exist.

## Current limitations

- Windows only
- Fixed 1920x1080 backing texture
