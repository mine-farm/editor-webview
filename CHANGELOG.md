# Changelog

## [0.9.1] - 2026-11-15

- Kept web page rendering at a fixed 16:9 aspect ratio.
- Corrected pointer coordinates for the letterboxed or pillarboxed viewport.

## [0.9.0] - 2026-11-15

- Added independent multi-tab Editor WebViews.
- Added a new-tab toolbar button and made the Window menu open a new tab.
- Shared the native WebView lifecycle safely across all open browser tabs.

## [0.8.0] - 2026-11-15

- Kept the native WebView alive while switching between docked Editor tabs.
- Restored the last URL after an intentional WebView suspension.

## [0.7.0] - 2026-11-15

- Added a web icon to the Unity Editor dock tab.

## [0.6.0] - 2026-11-15

- Moved WebViewToolkit's native capture host off-screen to remove the Windows
  Graphics Capture yellow border from the Unity Editor.
- Disabled direct input on the native host so pointer input is handled only by
  the docked UI Toolkit browser element.

## [0.5.0] - 2026-11-15

- Added named bookmark folders to the bookmark bar.
- Added click-to-open folder menus with favicon-style bookmark rows.
- Added inline folder creation to the bookmark editor.

## [0.4.0] - 2026-11-15

- Redesigned the editor window with a Chrome-inspired toolbar and omnibox.
- Moved bookmark and navigation actions into the rounded address bar.
- Added favicon-style monograms to bookmark bar entries.

## [0.3.0] - 2026-11-15

- Added a Chrome-style bookmark editor with editable names and folders.
- Added separate bookmark bar and other-bookmarks destinations.
- Kept existing bookmark data compatible by migrating it to the bookmark bar.

## [0.2.0] - 2026-11-15

- Added a persistent bookmark bar below the address toolbar.
- Added bookmark navigation, inline rename, URL copy, and removal actions.

## [0.1.0] - 2026-11-15

- Added a dockable Unity Editor browser window.
- Added address navigation, history controls, refresh, and home actions.
- Added native mouse and keyboard input forwarding.
- Added safe cleanup for assembly reloads and Play Mode transitions.
