using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Googlook.Models;
using Googlook.ViewModels;

namespace Googlook.Views;

public partial class MailView : UserControl
{
    public MailView() => AvaloniaXamlLoader.Load(this);

    // ---- compose / reply / forward --------------------------------------

    private async void OnComposeClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is MainViewModel vm)
                await OpenComposeAsync(vm, null, null, null, null, null, null);
        }
        catch (Exception ex) { Fail(ex); }
    }

    private async void OnReply(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm) return;
            var m = vm.SelectedThread?.SelectedMessage;
            if (m is null) return;
            await OpenComposeAsync(vm, m.FromRaw, EnsurePrefix(m.Subject, "Re: "), BuildQuote(m),
                null, NullIfEmpty(m.ThreadId), NullIfEmpty(m.Rfc822MessageId));
        }
        catch (Exception ex) { Fail(ex); }
    }

    private async void OnReplyAll(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm) return;
            var m = vm.SelectedThread?.SelectedMessage;
            if (m is null) return;
            var to = ReplyAllRecipients(m, vm.ActiveAccountEmail);
            await OpenComposeAsync(vm, to, EnsurePrefix(m.Subject, "Re: "), BuildQuote(m),
                null, NullIfEmpty(m.ThreadId), NullIfEmpty(m.Rfc822MessageId));
        }
        catch (Exception ex) { Fail(ex); }
    }

    private async void OnForward(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm) return;
            var m = vm.SelectedThread?.SelectedMessage;
            if (m is null) return;
            var preset = await FetchAttachmentsAsync(m);   // re-attach the originals
            await OpenComposeAsync(vm, "", EnsurePrefix(m.Subject, "Fwd: "), BuildForward(m),
                preset, null, null);
        }
        catch (Exception ex) { Fail(ex); }
    }

    private async Task OpenComposeAsync(MainViewModel vm, string? to, string? subject, string? body,
        IEnumerable<OutgoingAttachment>? preset, string? threadId, string? inReplyTo)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var dialog = new ComposeWindow
        {
            PrefillTo = to,
            PrefillSubject = subject,
            PrefillBody = body,
            PresetAttachments = preset,
            DrivePicker = async parent =>
            {
                try
                {
                    var drive = vm.CreateDriveClientForActive();
                    if (drive is null) { vm.NotifyStatus("Sign in to attach from Drive."); return null; }
                    return await new DrivePickerWindow(drive).ShowDialog<OutgoingAttachment?>(parent);
                }
                catch (Exception ex) { vm.NotifyStatus("Drive error: " + ex.Message); return null; }
            },
        };

        // Load contacts in the background so a slow People API call never blocks opening.
        LoadContactsInto(vm, dialog);

        var send = await dialog.ShowDialog<bool>(owner);
        if (send)
            await vm.SendMailAsync(dialog.To, dialog.Subject, dialog.Body, dialog.Attachments,
                threadId, inReplyTo);
    }

    private static async void LoadContactsInto(MainViewModel vm, ComposeWindow dialog)
    {
        try
        {
            var contacts = await vm.GetContactSuggestionsAsync();
            Dispatcher.UIThread.Post(() => dialog.SetContactSuggestions(contacts));
        }
        catch { /* no suggestions is fine */ }
    }

    private async void OnSaveAttachment(object? sender, RoutedEventArgs e)
    {
        var vm = DataContext as MainViewModel;
        try
        {
            if (sender is not Button { DataContext: AttachmentVM att }) return;
            if (TopLevel.GetTopLevel(this) is not { } top) return;

            var bytes = await att.FetchAsync();
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = att.Filename,
            });
            if (file is null) return;

            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(bytes);
            vm?.NotifyStatus("Saved " + att.Filename);
        }
        catch (Exception ex)
        {
            vm?.NotifyStatus("Couldn't save attachment: " + ex.Message);
        }
    }

    private void Fail(Exception ex) => (DataContext as MainViewModel)?.NotifyStatus("Error: " + ex.Message);

    // ---- reply/forward text helpers -------------------------------------

    private static async Task<List<OutgoingAttachment>> FetchAttachmentsAsync(MessageVM m)
    {
        var list = new List<OutgoingAttachment>();
        foreach (var a in m.Attachments)
        {
            try
            {
                var bytes = await a.FetchAsync();
                list.Add(new OutgoingAttachment
                {
                    Filename = a.Filename, MimeType = a.MimeType, Data = bytes, Source = "Forward",
                });
            }
            catch { /* skip an attachment that won't download */ }
        }
        return list;
    }

    private static string EnsurePrefix(string subject, string prefix)
    {
        var s = subject == "(no subject)" ? "" : subject;
        return s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? s : prefix + s;
    }

    private static string BuildQuote(MessageVM m)
    {
        var sb = new StringBuilder();
        sb.Append("\n\nOn ").Append(m.Date.ToString("g")).Append(", ").Append(m.Sender).Append(" wrote:\n");
        foreach (var line in (m.QuoteText ?? "").Replace("\r\n", "\n").Split('\n'))
            sb.Append("> ").Append(line).Append('\n');
        return sb.ToString();
    }

    private static string BuildForward(MessageVM m)
    {
        var sb = new StringBuilder();
        sb.Append("\n\n---------- Forwarded message ----------\n");
        sb.Append("From: ").Append(m.FromRaw).Append('\n');
        sb.Append("Date: ").Append(m.Date.ToString("g")).Append('\n');
        sb.Append("Subject: ").Append(m.Subject).Append('\n');
        if (!string.IsNullOrWhiteSpace(m.ToRaw)) sb.Append("To: ").Append(m.ToRaw).Append('\n');
        sb.Append('\n').Append(m.QuoteText ?? "");
        return sb.ToString();
    }

    private static string ReplyAllRecipients(MessageVM m, string? self)
    {
        var parts = (m.FromRaw + "," + m.ToRaw)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keep = new List<string>();
        foreach (var p in parts)
        {
            if (!string.IsNullOrEmpty(self) && p.Contains(self, StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(p)) keep.Add(p);
        }
        return keep.Count > 0 ? string.Join(", ", keep) : m.FromRaw;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
