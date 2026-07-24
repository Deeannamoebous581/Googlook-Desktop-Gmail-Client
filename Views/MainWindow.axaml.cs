using System;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Googlook.ViewModels;

namespace Googlook.Views;

public partial class MainWindow : Window
{
    private WindowNotificationManager? _notifier;
    private MainViewModel? _wired;

    public MainWindow() => AvaloniaXamlLoader.Load(this);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Corner toast host (bottom-right). Created once the window is shown.
        _notifier = new WindowNotificationManager(this)
        {
            Position = NotificationPosition.BottomRight,
            MaxItems = 3,
        };
        WireNotifications();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        WireNotifications();
    }

    private void WireNotifications()
    {
        if (DataContext is not MainViewModel vm || ReferenceEquals(vm, _wired)) return;
        if (_wired is not null) _wired.NotificationRequested -= OnNotificationRequested;
        _wired = vm;
        vm.NotificationRequested += OnNotificationRequested;
    }

    private void OnNotificationRequested(string title, string message) =>
        Dispatcher.UIThread.Post(() =>
            _notifier?.Show(new Notification(title, message, NotificationType.Information)));

    protected override void OnClosed(EventArgs e)
    {
        // Clean shutdown: unhook events and stop the VM's background work so the
        // process exits promptly instead of lingering on push/poll threads.
        if (_wired is not null) _wired.NotificationRequested -= OnNotificationRequested;
        (DataContext as MainViewModel)?.Dispose();
        base.OnClosed(e);
    }

    private void OnUnlockClick(object? sender, RoutedEventArgs e) => Unlock();

    private void OnCancelPasscode(object? sender, RoutedEventArgs e) =>
        (DataContext as MainViewModel)?.CancelPasscodeSetup();

    private void OnPasscodeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Unlock();
    }

    private async void OnAddImapAccount(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        try
        {
            var dialog = new AddImapAccountWindow();
            var cfg = await dialog.ShowDialog<Googlook.Models.ImapAccountConfig?>(this);
            if (cfg is not null) await vm.AddImapAccountAsync(cfg);
        }
        catch (Exception ex)
        {
            Services.Log.Error("AddImapAccount", ex);
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (e.Key == Key.Enter)
        {
            _ = vm.SearchCommand.ExecuteAsync(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.SearchQuery = "";              // Esc clears and returns to the open folder
            _ = vm.SearchCommand.ExecuteAsync(null);
            e.Handled = true;
        }
    }

    private void Unlock()
    {
        if (DataContext is not MainViewModel vm) return;
        var box = this.FindControl<TextBox>("PasscodeBox");
        if (vm.TryUnlock(box?.Text ?? string.Empty) && box is not null)
            box.Text = string.Empty; // don't leave the passcode sitting in the UI
    }
}
