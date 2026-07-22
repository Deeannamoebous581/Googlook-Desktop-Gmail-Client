using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
#endif

namespace Googlook.Controls;

/// <summary>
/// Renders an email body with full HTML fidelity while staying locked down:
/// JavaScript is disabled, every network request is blocked (the body is already
/// sanitized, so nothing should phone home even if a tag slipped through), and any
/// link the user clicks opens in their real browser instead of navigating here.
/// Windows-only (WebView2); elsewhere it renders nothing and the snippet shows through.
/// </summary>
public class HtmlMessageView : NativeControlHost
{
    public static readonly StyledProperty<string?> HtmlProperty =
        AvaloniaProperty.Register<HtmlMessageView, string?>(nameof(Html));

    /// <summary>When true, remote (http/https) resources are allowed to load ("Show images").</summary>
    public static readonly StyledProperty<bool> AllowRemoteProperty =
        AvaloniaProperty.Register<HtmlMessageView, bool>(nameof(AllowRemote));

    public string? Html
    {
        get => GetValue(HtmlProperty);
        set => SetValue(HtmlProperty, value);
    }

    public bool AllowRemote
    {
        get => GetValue(AllowRemoteProperty);
        set => SetValue(AllowRemoteProperty, value);
    }

    static HtmlMessageView()
    {
        HtmlProperty.Changed.AddClassHandler<HtmlMessageView>((v, _) => v.Render());
        // Re-navigate when the user toggles "Show images" so the remote fetch actually happens.
        AllowRemoteProperty.Changed.AddClassHandler<HtmlMessageView>((v, _) => v.Render());
    }

    private static string Wrap(string? body) =>
        "<!doctype html><html><head><meta charset='utf-8'><base target='_blank'>" +
        "<style>html,body{margin:0;padding:16px;font:14px/1.5 Roboto,'Segoe UI',sans-serif;" +
        "color:#202124;word-wrap:break-word;overflow-wrap:anywhere}" +
        "img{max-width:100%;height:auto}a{color:#1a73e8}</style></head><body>" +
        (body ?? string.Empty) + "</body></html>";

#if WINDOWS
    private CoreWebView2Environment? _env;
    private CoreWebView2Controller? _controller;
    private IntPtr _hwnd;
    private bool _ready;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _hwnd = CreateChildWindow(parent.Handle);
        _ = InitializeAsync();
        SizeChanged += (_, e) =>
        {
            try
            {
                if (_controller is not null)
                    _controller.Bounds = new System.Drawing.Rectangle(
                        0, 0, (int)e.NewSize.Width, (int)e.NewSize.Height);
            }
            catch { /* controller mid-teardown */ }
        };
        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        try { _controller?.Close(); } catch { }
        _controller = null; _ready = false;
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
    }

    private async Task InitializeAsync()
    {
        try
        {
            // A dedicated, cookie-free profile — the reader never needs an identity.
            var folder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Googlook", "profiles", "reader");
            System.IO.Directory.CreateDirectory(folder);

            _env = await CoreWebView2Environment.CreateAsync(userDataFolder: folder);
            _controller = await _env.CreateCoreWebView2ControllerAsync(_hwnd);
            _controller.Bounds = new System.Drawing.Rectangle(0, 0, (int)Bounds.Width, (int)Bounds.Height);
            _controller.IsVisible = true;
            // An unpainted native window renders black; white blends with the reading pane
            // and prevents the "black box" that otherwise flashes when a dialog closes over it.
            _controller.DefaultBackgroundColor = System.Drawing.Color.White;

            var core = _controller.CoreWebView2;
            var s = core.Settings;
            s.IsScriptEnabled = false;              // no JS in emails
            s.AreDevToolsEnabled = false;
            s.IsWebMessageEnabled = false;
            s.AreDefaultContextMenusEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsBuiltInErrorPageEnabled = false;

            // Block ALL resource loads — defense in depth over the sanitizer.
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                var uri = e.Request.Uri ?? "";
                if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return; // inline images ok
                if (AllowRemote &&
                    (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                    return;                                            // user chose "Show images"
                e.Response = _env!.CreateWebResourceResponse(null, 403, "Blocked", "");
            };

            // Clicking a link opens the system browser rather than navigating the reader.
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                OpenExternal(e.Uri);
            };
            core.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    OpenExternal(e.Uri);
                }
            };

            _ready = true;
            Render();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("HtmlMessageView init failed: " + ex.Message);
        }
    }

    private void Render()
    {
        if (!_ready || _controller?.CoreWebView2 is not { } core) return;
        var html = Html ?? "";
        // NavigateToString rejects very large payloads (~2 MB); trim pathological
        // bodies so the message still shows instead of silently staying blank.
        if (html.Length > 1_500_000) html = html[..1_500_000];
        Dispatcher.UIThread.Post(() =>
        {
            try { core.NavigateToString(Wrap(html)); } catch { /* not ready / teardown */ }
        });
    }

    private static void OpenExternal(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    private static IntPtr CreateChildWindow(IntPtr parent)
    {
        const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000;
        return CreateWindowEx(0, "STATIC", string.Empty, WS_CHILD | WS_VISIBLE,
            0, 0, 0, 0, parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
        int style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);
#else
    private void Render() { /* no-op off Windows */ }
#endif
}
