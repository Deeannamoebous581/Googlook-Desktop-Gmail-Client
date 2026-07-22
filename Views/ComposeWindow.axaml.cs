using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Googlook.Models;

namespace Googlook.Views;

public partial class ComposeWindow : Window
{
    private AutoCompleteBox _to = null!;
    private TextBox _subject = null!;
    private TextBox _body = null!;
    private TextBlock _error = null!;
    private ItemsControl _attachList = null!;

    private readonly ObservableCollection<OutgoingAttachment> _attachments = new();

    public string To      => _to.Text ?? string.Empty;
    public string Subject => _subject.Text ?? string.Empty;
    public string Body    => _body.Text ?? string.Empty;

    public IReadOnlyList<OutgoingAttachment> Attachments => _attachments;

    /// <summary>Set by the caller: opens a Drive picker and returns the chosen file (or null).</summary>
    public Func<Window, Task<OutgoingAttachment?>>? DrivePicker { get; set; }

    // Prefill + suggestions (set before ShowDialog; applied on open).
    public IEnumerable<string>? ContactSuggestions { get; set; }
    public string? PrefillTo { get; set; }
    public string? PrefillSubject { get; set; }
    public string? PrefillBody { get; set; }
    public IEnumerable<OutgoingAttachment>? PresetAttachments { get; set; }

    public ComposeWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _to         = this.FindControl<AutoCompleteBox>("ToBox")!;
        _subject    = this.FindControl<TextBox>("SubjBox")!;
        _body       = this.FindControl<TextBox>("BodyBox")!;
        _error      = this.FindControl<TextBlock>("ErrorText")!;
        _attachList = this.FindControl<ItemsControl>("AttachList")!;
        _attachList.ItemsSource = _attachments;

        Opened += (_, _) => ApplyInitial();
    }

    private void ApplyInitial()
    {
        if (ContactSuggestions is not null) _to.ItemsSource = ContactSuggestions;
        if (PrefillTo is not null) _to.Text = PrefillTo;
        if (PrefillSubject is not null) _subject.Text = PrefillSubject;
        if (PrefillBody is not null) _body.Text = PrefillBody;
        if (PresetAttachments is not null)
            foreach (var a in PresetAttachments) _attachments.Add(a);

        // Focus the most useful field: recipient for new mail, body for replies.
        if (string.IsNullOrEmpty(PrefillTo)) _to.Focus(); else _body.Focus();
    }

    /// <summary>Applied later when contacts finish loading in the background (non-blocking open).</summary>
    public void SetContactSuggestions(System.Collections.Generic.IEnumerable<string> items)
    {
        try { _to.ItemsSource = items; } catch { /* window already closing */ }
    }

    private async void OnAttachFile(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = true,
                Title = "Attach files",
            });

            foreach (var f in files)
            {
                try
                {
                    await using var stream = await f.OpenReadAsync();
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms);
                    _attachments.Add(new OutgoingAttachment
                    {
                        Filename = f.Name,
                        MimeType = GuessMime(f.Name),
                        Data = ms.ToArray(),
                        Source = "File",
                    });
                }
                catch (Exception ex) { _error.Text = "Couldn't attach " + f.Name + ": " + ex.Message; }
            }
        }
        catch (Exception ex) { _error.Text = "Couldn't open the file picker: " + ex.Message; }
    }

    private async void OnFromDrive(object? sender, RoutedEventArgs e)
    {
        if (DrivePicker is null) { _error.Text = "Drive isn't available."; return; }
        try
        {
            var att = await DrivePicker(this);
            if (att is not null) _attachments.Add(att);
        }
        catch (Exception ex) { _error.Text = "Drive error: " + ex.Message; }
    }

    private void OnRemoveAttachment(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: OutgoingAttachment att })
            _attachments.Remove(att);
    }

    private void OnSend(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(To))
        {
            _error.Text = "Add a recipient.";
            return;
        }
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private static string GuessMime(string filename)
    {
        var dot = filename.LastIndexOf('.');
        var ext = dot >= 0 ? filename[(dot + 1)..].ToLowerInvariant() : "";
        return ext switch
        {
            "pdf" => "application/pdf",
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            "txt" or "log" or "md" => "text/plain",
            "csv" => "text/csv",
            "zip" => "application/zip",
            "doc" => "application/msword",
            "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "xls" => "application/vnd.ms-excel",
            "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "ppt" => "application/vnd.ms-powerpoint",
            "pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            _ => "application/octet-stream",
        };
    }
}
