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
Right-click a bookmark to edit it, copy its URL, or remove it. Only one native
WebView is kept active at a time, and it is suspended while entering Play Mode.

## Current limitations

- Windows only
- Fixed 1280x720 backing texture
- One active editor WebView at a time
