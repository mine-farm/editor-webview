using System;
using System.Collections.Concurrent;
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

        private static FixedWebViewElement activeOwner;
        private static readonly LogCallback LogCallback = OnNativeLog;
        private static readonly NavigationCallback NavigationCallback = OnNativeNavigation;
        private static readonly MessageCallback MessageCallback = OnNativeMessage;
        private static readonly DeviceEventCallback DeviceEventCallback = OnNativeDeviceEvent;

        private readonly string initialUrl;
        private readonly ConcurrentQueue<NavigationResult> navigationResults = new();
        private readonly ConcurrentQueue<NativeLog> nativeLogs = new();
        private Texture2D displayTexture;
        private IVisualElementScheduledItem tickItem;
        private IntPtr renderEventFunction;
        private uint handle;
        private bool nativeInitialized;
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

            return (NativeResult)WebViewNative.WebViewToolkit_Navigate(handle, url) ==
                   NativeResult.Success;
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
            if (!disposed && panel != null)
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
            ReleaseWebView();
        }

        private void CreateWebView()
        {
            if (disposed || handle != 0 || panel == null ||
                EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            if (activeOwner != null && activeOwner != this)
                activeOwner.ReleaseWebView();

            activeOwner = this;
            WebViewNative.WebViewToolkit_SetLogCallback(LogCallback);
            WebViewNative.WebViewToolkit_SetNavigationCallback(NavigationCallback);
            WebViewNative.WebViewToolkit_SetMessageCallback(MessageCallback);
            WebViewNative.WebViewToolkit_SetDeviceEventCallback(DeviceEventCallback);

            var graphicsApi = SystemInfo.graphicsDeviceType switch
            {
                GraphicsDeviceType.Direct3D11 => GraphicsAPI.Direct3D11,
                GraphicsDeviceType.Direct3D12 => GraphicsAPI.Direct3D12,
                _ => GraphicsAPI.Unknown
            };

            if (graphicsApi == GraphicsAPI.Unknown)
            {
                ReleaseWebView();
                return;
            }

            var initializeResult =
                (NativeResult)WebViewNative.WebViewToolkit_Initialize((int)graphicsApi);
            if (initializeResult == NativeResult.ErrorAlreadyInitialized)
            {
                WebViewNative.WebViewToolkit_Shutdown();
                initializeResult =
                    (NativeResult)WebViewNative.WebViewToolkit_Initialize((int)graphicsApi);
            }

            if (initializeResult != NativeResult.Success)
            {
                ReleaseWebView();
                return;
            }

            nativeInitialized = true;
            renderEventFunction = WebViewNative.WebViewToolkit_GetRenderEventFunc();

            var createResult = (NativeResult)WebViewNative.WebViewToolkit_CreateWebView(
                RenderWidth,
                RenderHeight,
                null,
                initialUrl,
                0,
                out handle);
            if (createResult != NativeResult.Success)
            {
                ReleaseWebView();
                return;
            }

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
                WebViewNative.WebViewToolkit_DestroyWebView(handle);
                handle = 0;
            }

            if (nativeInitialized)
            {
                WebViewNative.WebViewToolkit_Shutdown();
                nativeInitialized = false;
            }

            renderEventFunction = IntPtr.Zero;
            refreshTexture = false;
            currentUrl = null;
            while (navigationResults.TryDequeue(out _)) { }
            while (nativeLogs.TryDequeue(out _)) { }

            if (activeOwner == this)
                activeOwner = null;
        }

        private void Tick()
        {
            if (disposed || handle == 0)
                return;

            if (refreshTexture)
            {
                refreshTexture = false;
                var nativeTexture = WebViewNative.WebViewToolkit_GetTexturePtr(handle);
                if (nativeTexture != IntPtr.Zero)
                    displayTexture?.UpdateExternalTexture(nativeTexture);
            }

            if (renderEventFunction != IntPtr.Zero)
                GL.IssuePluginEvent(renderEventFunction, (int)RenderEventType.UpdateTexture);

            while (navigationResults.TryDequeue(out var result))
            {
                currentUrl = result.Url;
                NavigationCompleted?.Invoke(result.Url, result.Succeeded);
            }

            while (nativeLogs.TryDequeue(out var entry))
            {
                if (entry.Level == 1)
                    Debug.LogWarning($"[Editor WebView] {entry.Message}");
                else if (entry.Level >= 2)
                    Debug.LogError($"[Editor WebView] {entry.Message}");
            }

            MarkDirtyRepaint();
        }

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
            activeOwner?.nativeLogs.Enqueue(new NativeLog(level, message));
        }

        [AOT.MonoPInvokeCallback(typeof(NavigationCallback))]
        private static void OnNativeNavigation(uint webViewHandle, string url, bool succeeded)
        {
            var owner = activeOwner;
            if (owner != null && owner.handle == webViewHandle)
                owner.navigationResults.Enqueue(new NavigationResult(url, succeeded));
        }

        [AOT.MonoPInvokeCallback(typeof(MessageCallback))]
        private static void OnNativeMessage(uint webViewHandle, string message)
        {
        }

        [AOT.MonoPInvokeCallback(typeof(DeviceEventCallback))]
        private static void OnNativeDeviceEvent(DeviceEventType eventType)
        {
            if (eventType == DeviceEventType.DeviceRestored && activeOwner != null)
                activeOwner.refreshTexture = true;
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
