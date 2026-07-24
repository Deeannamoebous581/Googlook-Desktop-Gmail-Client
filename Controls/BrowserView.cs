using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
#if WINDOWS
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
#endif

namespace Googlook.Controls;

/// <summary>
/// An embedded Chromium browser hosted inside the Avalonia window via NativeControlHost.
/// It uses WebView2 — the same Chromium engine Chrome is built on, with the Edge
/// user-agent that Google accepts for sign-in — and a per-account profile folder so each
/// account is an isolated Google login. Windows-only; elsewhere the fallback card shows.
///
/// Bounds are re-asserted on every Bounds change (not just SizeChanged), which is what
/// makes it paint correctly when a hidden tab first becomes visible.
/// </summary>
public class BrowserView : NativeControlHost
{
    public static readonly StyledProperty<string?> AddressProperty =
        AvaloniaProperty.Register<BrowserView, string?>(nameof(Address));

    public static readonly StyledProperty<string?> ProfileDirProperty =
        AvaloniaProperty.Register<BrowserView, string?>(nameof(ProfileDir));

    public string? Address
    {
        get => GetValue(AddressProperty);
        set => SetValue(AddressProperty, value);
    }

    public string? ProfileDir
    {
        get => GetValue(ProfileDirProperty);
        set => SetValue(ProfileDirProperty, value);
    }

    static BrowserView()
    {
        AddressProperty.Changed.AddClassHandler<BrowserView>((v, _) => v.NavigateToAddress());
    }

#if WINDOWS
    private CoreWebView2Environment? _env;
    private CoreWebView2Controller? _controller;
    private IntPtr _hwnd;
    private bool _ready;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _hwnd = CreateChildWindow(parent.Handle);
        _ = InitializeAsync();
        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        try { _controller?.Close(); } catch { }
        _controller = null; _ready = false;
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty) UpdateBounds();
    }

    private void UpdateBounds()
    {
        try
        {
            if (_controller is null) return;
            // Physical pixels, not Avalonia's logical units — at >100% display scale
            // the unscaled size leaves an unpainted black band beside the page.
            var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            _controller.Bounds = new System.Drawing.Rectangle(
                0, 0,
                Math.Max(0, (int)Math.Ceiling(Bounds.Width * scale)),
                Math.Max(0, (int)Math.Ceiling(Bounds.Height * scale)));
        }
        catch { /* controller mid-teardown */ }
    }

    private async Task InitializeAsync()
    {
        try
        {
            var folder = string.IsNullOrWhiteSpace(ProfileDir)
                ? System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Googlook", "profiles", "browser")
                : ProfileDir!;
            System.IO.Directory.CreateDirectory(folder);

            // Real web apps (Drive/Gemini) keep GPU compositing; set GOOGLOOK_DISABLE_GPU=1
            // if the tabs render as black boxes (VMs / RDP without hardware acceleration).
            var opts = new CoreWebView2EnvironmentOptions();
            var noGpu = Environment.GetEnvironmentVariable("GOOGLOOK_DISABLE_GPU");
            if (noGpu is "1" or "true") opts.AdditionalBrowserArguments = "--disable-gpu";

            _env = await CoreWebView2Environment.CreateAsync(null, folder, opts);
            _controller = await _env.CreateCoreWebView2ControllerAsync(_hwnd);
            _controller.DefaultBackgroundColor = System.Drawing.Color.White;
            _controller.IsVisible = true;
            UpdateBounds();

            var s = _controller.CoreWebView2.Settings;
            s.IsScriptEnabled = true;                 // it's a real browser
            s.AreDefaultContextMenusEnabled = true;
            s.IsStatusBarEnabled = false;

            // Keep popups (e.g. Google sign-in) inside this view.
            _controller.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                try { _controller!.CoreWebView2.Navigate(e.Uri); } catch { }
            };

            _ready = true;
            NavigateToAddress();
        }
        catch (Exception ex)
        {
            Googlook.Services.Log.Error("BrowserView init", ex);
            // Hide the unpainted (black) native child so the fallback card
            // behind it is actually visible.
            Dispatcher.UIThread.Post(() => IsVisible = false);
        }
    }

    private void NavigateToAddress()
    {
        if (!_ready || _controller?.CoreWebView2 is not { } core) return;
        var url = Address;
        if (string.IsNullOrWhiteSpace(url)) return;
        Dispatcher.UIThread.Post(() => { try { core.Navigate(url); } catch { } });
    }

    public void Reload()  { try { _controller?.CoreWebView2?.Reload(); } catch { } }
    public void Back()    { try { var c = _controller?.CoreWebView2; if (c?.CanGoBack == true) c.GoBack(); } catch { } }
    public void Forward() { try { var c = _controller?.CoreWebView2; if (c?.CanGoForward == true) c.GoForward(); } catch { } }

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
    private void NavigateToAddress() { }
    public void Reload() { }
    public void Back() { }
    public void Forward() { }
#endif
}
