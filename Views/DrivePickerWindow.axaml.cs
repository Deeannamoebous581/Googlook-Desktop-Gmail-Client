using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Googlook.Services;

namespace Googlook.Views;

public partial class DrivePickerWindow : Window
{
    private readonly DriveClient _drive = null!;
    private TextBox _search = null!;
    private ListBox _files = null!;
    private ProgressBar _busy = null!;
    private TextBlock _err = null!;

    // Parameterless constructor required by the Avalonia XAML compiler / previewer.
    // At run time the app always uses the DriveClient overload below.
    public DrivePickerWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _search = this.FindControl<TextBox>("SearchBox")!;
        _files  = this.FindControl<ListBox>("FilesList")!;
        _busy   = this.FindControl<ProgressBar>("Busy")!;
        _err    = this.FindControl<TextBlock>("Err")!;

        Opened += (_, _) => { if (_drive is not null) _ = LoadAsync(null); };
    }

    public DrivePickerWindow(DriveClient drive) : this()
    {
        _drive = drive;
    }

    private async Task LoadAsync(string? search)
    {
        if (_drive is null) return;   // designer / previewer safety
        _busy.IsVisible = true;
        _err.Text = "";
        try
        {
            var files = await _drive.ListAsync(search);
            _files.ItemsSource = files;
            if (files.Count == 0) _err.Text = "No files found.";
        }
        catch (Exception ex) { _err.Text = "Couldn't reach Drive: " + ex.Message; }
        finally { _busy.IsVisible = false; }
    }

    private void OnSearch(object? sender, RoutedEventArgs e) => _ = LoadAsync(_search.Text);

    private void OnSearchKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = LoadAsync(_search.Text);
    }

    private async void OnAttach(object? sender, RoutedEventArgs e)
    {
        if (_drive is null) return;
        if (_files.SelectedItem is not DriveFileInfo file)
        {
            _err.Text = "Pick a file first.";
            return;
        }

        _busy.IsVisible = true;
        _err.Text = "Downloading…";
        try
        {
            var attachment = await _drive.DownloadAsync(file);
            Close(attachment);
        }
        catch (Exception ex)
        {
            _err.Text = "Download failed: " + ex.Message;
            _busy.IsVisible = false;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
