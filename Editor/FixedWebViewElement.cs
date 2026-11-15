using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using WebViewToolkit.Native;
using NativeMouseButton = WebViewToolkit.Native.MouseButton;

namespace MF.WebBrowser.Editor
{
    internal sealed class FixedWebViewElement : Image, IDisposable
    {
        private const int RenderWidth = 1280;
        private const int RenderHeight = 720;
        private const double RenderUpdateInterval = 0.03d;
        private const double HostWindowQuarantineInterval = 1d;

#if UNITY_EDITOR_WIN
        private const string NativeHostWindowClass = "WebViewToolkitHostWindow";
        private const string NativeHostWindowTitle = "WebViewToolkitHost";
        private const int NativeHostOffscreenPosition = -32000;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
#endif

        private static readonly ConcurrentDictionary<uint, FixedWebViewElement> Owners = new();
        private static readonly ConcurrentQueue<NativeLog> NativeLogs = new();
        private static readonly object NativeLifecycleLock = new();
        private static readonly LogCallback LogCallback = OnNativeLog;
        private static readonly NavigationCallback NavigationCallback = OnNativeNavigation;
        private static readonly MessageCallback MessageCallback = OnNativeMessage;
        private static readonly DeviceEventCallback DeviceEventCallback = OnNativeDeviceEvent;
        private static IntPtr renderEventFunction;
        private static bool nativeInitialized;
        private static double nextRenderUpdateAt;
        private static double nextHostWindowQuarantineAt;

        private readonly string initialUrl;
        private readonly ConcurrentQueue<NavigationResult> navigationResults = new();
        private Texture2D displayTexture;
        private IVisualElementScheduledItem tickItem;
        private uint handle;
        private bool refreshTexture;
        private bool disposed;
        private string currentUrl;

        public event Action<string, bool> NavigationCompleted;

        public FixedWebViewElement(string initialUrl)
        {
            this.initialUrl = initialUrl;
            scaleMode = ScaleMode.StretchToFill;
            focusable = true;
            pickingMode = PickingMode.Position;

            RegisterCallback<AttachToPanelEvent>(OnAttachToPanel);
            RegisterCallback<DetachFromPanelEvent>(OnDetachFromPanel);
            RegisterCallback<MouseDownEvent>(OnMouseDown);
            RegisterCallback<MouseUpEvent>(OnMouseUp);
            RegisterCallback<MouseMoveEvent>(OnMouseMove);
            RegisterCallback<WheelEvent>(OnMouseWheel);
            RegisterCallback<MouseLeaveEvent>(OnMouseLeave);
            RegisterCallback<KeyDownEvent>(OnKeyDown);
            RegisterCallback<KeyUpEvent>(OnKeyUp);
        }

        public bool Navigate(string url)
        {
            if (handle == 0)
                return false;

            var result = (NativeResult)WebViewNative.WebViewToolkit_Navigate(handle, url);
            if (result == NativeResult.Success)
                currentUrl = url;

            return result == NativeResult.Success;
        }

        public void GoBack()
        {
            if (handle != 0)
                WebViewNative.WebViewToolkit_GoBack(handle);
        }

        public void GoForward()
        {
            if (handle != 0)
                WebViewNative.WebViewToolkit_GoForward(handle);
        }

        public void Refresh()
        {
            if (!string.IsNullOrEmpty(currentUrl))
                Navigate(currentUrl);
        }

        public void Suspend()
        {
            ReleaseWebView();
        }

        public void Resume()
        {
            if (disposed || panel == null)
                return;

            if (handle != 0)
            {
                tickItem?.Resume();
                QuarantineNativeHostWindows();
                MarkDirtyRepaint();
                return;
            }

            schedule.Execute(CreateWebView).ExecuteLater(100);
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            ReleaseWebView();
        }

        private void OnAttachToPanel(AttachToPanelEvent evt)
        {
            Resume();
        }

        private void OnDetachFromPanel(DetachFromPanelEvent evt)
        {
            tickItem?.Pause();
        }

        private void CreateWebView()
        {
            if (disposed || handle != 0 || panel == null ||
                EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            if (!EnsureNativeInitialized())
                return;

            var startUrl = string.IsNullOrEmpty(currentUrl) ? initialUrl : currentUrl;
            var createResult = (NativeResult)WebViewNative.WebViewToolkit_CreateWebView(
                RenderWidth,
                RenderHeight,
                null,
                startUrl,
                0,
                out handle);
            if (createResult != NativeResult.Success)
            {
                handle = 0;
                ReleaseNativeIfUnused();
                return;
            }

            currentUrl = startUrl;
            Owners[handle] = this;
            QuarantineNativeHostWindows();

            var nativeTexture = WebViewNative.WebViewToolkit_GetTexturePtr(handle);
            if (nativeTexture == IntPtr.Zero)
            {
                ReleaseWebView();
                return;
            }

            // The native resource is BGRA UNORM. A linear wrapper prevents Unity
            // from requesting an incompatible BGRA SRGB shader-resource view.
            displayTexture = Texture2D.CreateExternalTexture(
                RenderWidth,
                RenderHeight,
                TextureFormat.BGRA32,
                mipChain: false,
                linear: true,
                nativeTexture);
            displayTexture.name = $"EditorWebViewTexture_{handle}";
            displayTexture.filterMode = FilterMode.Bilinear;
            displayTexture.wrapMode = TextureWrapMode.Clamp;
            image = displayTexture;

            // The backing texture stays fixed because WebViewToolkit 1.3.0 can
            // crash while replacing its D3D texture during a native resize.
            tickItem = schedule.Execute(Tick).Every(33);
        }

        private void ReleaseWebView()
        {
            tickItem?.Pause();
            tickItem = null;
            image = null;

            if (displayTexture != null)
            {
                UnityEngine.Object.DestroyImmediate(displayTexture);
                displayTexture = null;
            }

            if (handle != 0)
            {
                var releasedHandle = handle;
                handle = 0;
                Owners.TryRemove(releasedHandle, out _);
                WebViewNative.WebViewToolkit_DestroyWebView(releasedHandle);
            }

            refreshTexture = false;
            while (navigationResults.TryDequeue(out _)) { }
            ReleaseNativeIfUnused();
        }

        private void Tick()
        {
            if (disposed || handle == 0)
                return;

            var now = EditorApplication.timeSinceStartup;
            if (now >= nextHostWindowQuarantineAt)
            {
                QuarantineNativeHostWindows();
                nextHostWindowQuarantineAt = now + HostWindowQuarantineInterval;
            }

            if (refreshTexture)
            {
                refreshTexture = false;
                var nativeTexture = WebViewNative.WebViewToolkit_GetTexturePtr(handle);
                if (nativeTexture != IntPtr.Zero)
                    displayTexture?.UpdateExternalTexture(nativeTexture);
            }

            if (renderEventFunction != IntPtr.Zero && now >= nextRenderUpdateAt)
            {
                GL.IssuePluginEvent(renderEventFunction, (int)RenderEventType.UpdateTexture);
                nextRenderUpdateAt = now + RenderUpdateInterval;
            }

            while (navigationResults.TryDequeue(out var result))
            {
                currentUrl = result.Url;
                NavigationCompleted?.Invoke(result.Url, result.Succeeded);
            }

            while (NativeLogs.TryDequeue(out var entry))
            {
                if (entry.Level == 1)
                    Debug.LogWarning($"[Editor WebView] {entry.Message}");
                else if (entry.Level >= 2)
                    Debug.LogError($"[Editor WebView] {entry.Message}");
            }

            MarkDirtyRepaint();
        }

        private static bool EnsureNativeInitialized()
        {
            lock (NativeLifecycleLock)
            {
                if (nativeInitialized && WebViewNative.WebViewToolkit_IsInitialized() != 0)
                    return true;

                var graphicsApi = SystemInfo.graphicsDeviceType switch
                {
                    GraphicsDeviceType.Direct3D11 => GraphicsAPI.Direct3D11,
                    GraphicsDeviceType.Direct3D12 => GraphicsAPI.Direct3D12,
                    _ => GraphicsAPI.Unknown
                };

                if (graphicsApi == GraphicsAPI.Unknown)
                    return false;

                WebViewNative.WebViewToolkit_SetLogCallback(LogCallback);
                WebViewNative.WebViewToolkit_SetNavigationCallback(NavigationCallback);
                WebViewNative.WebViewToolkit_SetMessageCallback(MessageCallback);
                WebViewNative.WebViewToolkit_SetDeviceEventCallback(DeviceEventCallback);

                var initializeResult =
                    (NativeResult)WebViewNative.WebViewToolkit_Initialize((int)graphicsApi);
                if (initializeResult == NativeResult.ErrorAlreadyInitialized)
                {
                    WebViewNative.WebViewToolkit_Shutdown();
                    initializeResult =
                        (NativeResult)WebViewNative.WebViewToolkit_Initialize((int)graphicsApi);
                }

                if (initializeResult != NativeResult.Success)
                    return false;

                renderEventFunction = WebViewNative.WebViewToolkit_GetRenderEventFunc();
                nativeInitialized = renderEventFunction != IntPtr.Zero;
                if (!nativeInitialized)
                    WebViewNative.WebViewToolkit_Shutdown();

                return nativeInitialized;
            }
        }

        private static void ReleaseNativeIfUnused()
        {
            lock (NativeLifecycleLock)
            {
                if (!nativeInitialized || !Owners.IsEmpty)
                    return;

                WebViewNative.WebViewToolkit_Shutdown();
                nativeInitialized = false;
                renderEventFunction = IntPtr.Zero;
                nextRenderUpdateAt = 0d;
                nextHostWindowQuarantineAt = 0d;
                while (NativeLogs.TryDequeue(out _)) { }
            }
        }

        private static void QuarantineNativeHostWindows()
        {
#if UNITY_EDITOR_WIN
            var currentProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            var candidate = IntPtr.Zero;

            while ((candidate = FindWindowEx(
                       IntPtr.Zero,
                       candidate,
                       NativeHostWindowClass,
                       NativeHostWindowTitle)) != IntPtr.Zero)
            {
                GetWindowThreadProcessId(candidate, out var ownerProcessId);
                if (ownerProcessId != currentProcessId)
                    continue;

                // WebViewToolkit captures real top-level HWNDs. They must remain
                // visible for capture, so park every host off-screen and let UI
                // Toolkit forward input to the corresponding native handle.
                EnableWindow(candidate, false);
                SetWindowPos(
                    candidate,
                    IntPtr.Zero,
                    NativeHostOffscreenPosition,
                    NativeHostOffscreenPosition,
                    0,
                    0,
                    SwpNoSize | SwpNoZOrder | SwpNoActivate);
            }
#endif
        }

#if UNITY_EDITOR_WIN
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(
            IntPtr parent,
            IntPtr childAfter,
            string className,
            string windowName);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            IntPtr window,
            out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnableWindow(IntPtr window, bool enable);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);
#endif

        private void OnMouseDown(MouseDownEvent evt)
        {
            if (handle == 0)
                return;

            Focus();
            SendMouseEvent(MouseEventType.Down, ConvertMouseButton(evt.button), evt.localMousePosition);
            this.CaptureMouse();
            evt.StopPropagation();
        }

        private void OnMouseUp(MouseUpEvent evt)
        {
            if (handle == 0)
                return;

            SendMouseEvent(MouseEventType.Up, ConvertMouseButton(evt.button), evt.localMousePosition);
            if (this.HasMouseCapture())
                this.ReleaseMouse();
            evt.StopPropagation();
        }

        private void OnMouseMove(MouseMoveEvent evt)
        {
            if (handle != 0)
                SendMouseEvent(MouseEventType.Move, NativeMouseButton.None, evt.localMousePosition);
        }

        private void OnMouseWheel(WheelEvent evt)
        {
            if (handle == 0)
                return;

            SendMouseEvent(
                MouseEventType.Wheel,
                NativeMouseButton.None,
                evt.localMousePosition,
                -evt.delta.y);
            evt.StopImmediatePropagation();
        }

        private void OnMouseLeave(MouseLeaveEvent evt)
        {
            SendMouseEvent(MouseEventType.Leave, NativeMouseButton.None, Vector2.zero);
        }

        private void SendMouseEvent(
            MouseEventType eventType,
            NativeMouseButton button,
            Vector2 localPosition,
            float wheelDelta = 0f)
        {
            if (handle == 0 || resolvedStyle.width <= 0f || resolvedStyle.height <= 0f)
                return;

            var normalizedX = Mathf.Clamp01(localPosition.x / resolvedStyle.width);
            var normalizedY = Mathf.Clamp01(localPosition.y / resolvedStyle.height);
            WebViewNative.WebViewToolkit_SendMouseEvent(
                handle,
                (int)eventType,
                (int)button,
                normalizedX,
                normalizedY,
                wheelDelta);
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (SendKeyEvent(evt.keyCode, evt.altKey, true))
                evt.StopPropagation();
        }

        private void OnKeyUp(KeyUpEvent evt)
        {
            if (SendKeyEvent(evt.keyCode, evt.altKey, false))
                evt.StopPropagation();
        }

        private bool SendKeyEvent(KeyCode keyCode, bool isSystemKey, bool isKeyDown)
        {
            var virtualKey = ToVirtualKey(keyCode);
            if (handle == 0 || virtualKey == 0)
                return false;

            return (NativeResult)WebViewNative.WebViewToolkit_SendKeyEvent(
                       handle,
                       virtualKey,
                       0,
                       isKeyDown ? 1 : 0,
                       isSystemKey ? 1 : 0) == NativeResult.Success;
        }

        private static NativeMouseButton ConvertMouseButton(int button)
        {
            return button switch
            {
                0 => NativeMouseButton.Left,
                1 => NativeMouseButton.Right,
                2 => NativeMouseButton.Middle,
                _ => NativeMouseButton.None
            };
        }

        private static uint ToVirtualKey(KeyCode keyCode)
        {
            if (keyCode >= KeyCode.A && keyCode <= KeyCode.Z)
                return (uint)(0x41 + (int)keyCode - (int)KeyCode.A);
            if (keyCode >= KeyCode.Alpha0 && keyCode <= KeyCode.Alpha9)
                return (uint)(0x30 + (int)keyCode - (int)KeyCode.Alpha0);
            if (keyCode >= KeyCode.Keypad0 && keyCode <= KeyCode.Keypad9)
                return (uint)(0x60 + (int)keyCode - (int)KeyCode.Keypad0);
            if (keyCode >= KeyCode.F1 && keyCode <= KeyCode.F12)
                return (uint)(0x70 + (int)keyCode - (int)KeyCode.F1);

            return keyCode switch
            {
                KeyCode.Backspace => 0x08,
                KeyCode.Tab => 0x09,
                KeyCode.Return or KeyCode.KeypadEnter => 0x0D,
                KeyCode.Escape => 0x1B,
                KeyCode.Space => 0x20,
                KeyCode.PageUp => 0x21,
                KeyCode.PageDown => 0x22,
                KeyCode.End => 0x23,
                KeyCode.Home => 0x24,
                KeyCode.LeftArrow => 0x25,
                KeyCode.UpArrow => 0x26,
                KeyCode.RightArrow => 0x27,
                KeyCode.DownArrow => 0x28,
                KeyCode.Insert => 0x2D,
                KeyCode.Delete => 0x2E,
                KeyCode.KeypadMultiply => 0x6A,
                KeyCode.KeypadPlus => 0x6B,
                KeyCode.KeypadMinus => 0x6D,
                KeyCode.KeypadPeriod => 0x6E,
                KeyCode.KeypadDivide => 0x6F,
                KeyCode.LeftShift => 0xA0,
                KeyCode.RightShift => 0xA1,
                KeyCode.LeftControl => 0xA2,
                KeyCode.RightControl => 0xA3,
                KeyCode.LeftAlt => 0xA4,
                KeyCode.RightAlt => 0xA5,
                KeyCode.Semicolon => 0xBA,
                KeyCode.Equals => 0xBB,
                KeyCode.Comma => 0xBC,
                KeyCode.Minus => 0xBD,
                KeyCode.Period => 0xBE,
                KeyCode.Slash => 0xBF,
                KeyCode.BackQuote => 0xC0,
                KeyCode.LeftBracket => 0xDB,
                KeyCode.Backslash => 0xDC,
                KeyCode.RightBracket => 0xDD,
                KeyCode.Quote => 0xDE,
                _ => 0
            };
        }

        [AOT.MonoPInvokeCallback(typeof(LogCallback))]
        private static void OnNativeLog(int level, string message)
        {
            NativeLogs.Enqueue(new NativeLog(level, message));
        }

        [AOT.MonoPInvokeCallback(typeof(NavigationCallback))]
        private static void OnNativeNavigation(uint webViewHandle, string url, bool succeeded)
        {
            if (Owners.TryGetValue(webViewHandle, out var owner))
                owner.navigationResults.Enqueue(new NavigationResult(url, succeeded));
        }

        [AOT.MonoPInvokeCallback(typeof(MessageCallback))]
        private static void OnNativeMessage(uint webViewHandle, string message)
        {
        }

        [AOT.MonoPInvokeCallback(typeof(DeviceEventCallback))]
        private static void OnNativeDeviceEvent(DeviceEventType eventType)
        {
            if (eventType != DeviceEventType.DeviceRestored)
                return;

            foreach (var owner in Owners.Values)
                owner.refreshTexture = true;
        }

        private readonly struct NavigationResult
        {
            public NavigationResult(string url, bool succeeded)
            {
                Url = url;
                Succeeded = succeeded;
            }

            public string Url { get; }
            public bool Succeeded { get; }
        }

        private readonly struct NativeLog
        {
            public NativeLog(int level, string message)
            {
                Level = level;
                Message = message;
            }

            public int Level { get; }
            public string Message { get; }
        }
    }
}
