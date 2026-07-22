using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Googlook.Services;

namespace Googlook.Controls;

public partial class BrowserTab : UserControl
{
    public static readonly StyledProperty<string?> UrlProperty =
        AvaloniaProperty.Register<BrowserTab, string?>(nameof(Url));

    public static readonly StyledProperty<string?> ProfileDirProperty =
        AvaloniaProperty.Register<BrowserTab, string?>(nameof(ProfileDir));

    public string? Url
    {
        get => GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    /// <summary>Isolated browser profile directory for the account currently in focus.</summary>
    public string? ProfileDir
    {
        get => GetValue(ProfileDirProperty);
        set => SetValue(ProfileDirProperty, value);
    }

    private BrowserView? _web;

    public BrowserTab()
    {
        AvaloniaXamlLoader.Load(this);
        _web = this.FindControl<BrowserView>("Web");
    }

    private void OnBack(object? sender, RoutedEventArgs e)    => _web?.Back();
    private void OnForward(object? sender, RoutedEventArgs e) => _web?.Forward();
    private void OnReload(object? sender, RoutedEventArgs e)  => _web?.Reload();

    private void OnOpenExternal(object? sender, RoutedEventArgs e)
    {
        try { ChromeLauncher.Launch(Url ?? string.Empty, ProfileDir); } catch { }
    }
}
