using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Googlook.Models;
using Googlook.Services;

namespace Googlook.Views;

/// <summary>
/// Collects IMAP/POP3 + SMTP settings for a non-Google account, verifies them
/// against both servers ("Test &amp; add"), and returns the validated
/// <see cref="ImapAccountConfig"/> to the caller (null on cancel).
/// </summary>
public partial class AddImapAccountWindow : Window
{
    private TextBox _email = null!, _name = null!, _password = null!, _user = null!;
    private TextBox _inHost = null!, _inPort = null!, _smtpHost = null!, _smtpPort = null!;
    private ComboBox _protocol = null!;
    private TextBlock _error = null!;
    private ProgressBar _busy = null!;
    private Button _add = null!;

    public AddImapAccountWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _email    = this.FindControl<TextBox>("EmailBox")!;
        _name     = this.FindControl<TextBox>("NameBox")!;
        _password = this.FindControl<TextBox>("PasswordBox")!;
        _user     = this.FindControl<TextBox>("UserBox")!;
        _inHost   = this.FindControl<TextBox>("InHostBox")!;
        _inPort   = this.FindControl<TextBox>("InPortBox")!;
        _smtpHost = this.FindControl<TextBox>("SmtpHostBox")!;
        _smtpPort = this.FindControl<TextBox>("SmtpPortBox")!;
        _protocol = this.FindControl<ComboBox>("ProtocolBox")!;
        _error    = this.FindControl<TextBlock>("ErrorText")!;
        _busy     = this.FindControl<ProgressBar>("Busy")!;
        _add      = this.FindControl<Button>("AddButton")!;
    }

    private bool IsPop3 => _protocol.SelectedIndex == 1;

    /// <summary>Prefill host guesses from the address's domain (only into empty fields).</summary>
    private void OnEmailLostFocus(object? sender, RoutedEventArgs e)
    {
        var at = (_email.Text ?? "").IndexOf('@');
        if (at < 0) return;
        var domain = (_email.Text ?? "")[(at + 1)..].Trim();
        if (domain.Length == 0) return;

        if (string.IsNullOrWhiteSpace(_inHost.Text))
            _inHost.Text = (IsPop3 ? "pop." : "imap.") + domain;
        if (string.IsNullOrWhiteSpace(_smtpHost.Text))
            _smtpHost.Text = "smtp." + domain;
    }

    /// <summary>Swap the default port when switching protocol (only if still at the other default).</summary>
    private void OnProtocolChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_inPort is null) return;   // fires during XAML load
        var text = (_inPort.Text ?? "").Trim();
        if (IsPop3 && text is "" or "993") _inPort.Text = "995";
        if (!IsPop3 && text is "" or "995") _inPort.Text = "993";
    }

    private async void OnTestAndAdd(object? sender, RoutedEventArgs e)
    {
        var cfg = new ImapAccountConfig
        {
            EmailAddress = (_email.Text ?? "").Trim(),
            DisplayName  = (_name.Text ?? "").Trim(),
            Password     = _password.Text ?? "",
            Username     = (_user.Text ?? "").Trim(),
            Protocol     = IsPop3 ? "pop3" : "imap",
            IncomingHost = (_inHost.Text ?? "").Trim(),
            IncomingPort = ParsePort(_inPort.Text, IsPop3 ? 995 : 993),
            SmtpHost     = (_smtpHost.Text ?? "").Trim(),
            SmtpPort     = ParsePort(_smtpPort.Text, 587),
        };

        if (!cfg.EmailAddress.Contains('@')) { _error.Text = "Enter a valid email address."; return; }
        if (cfg.Password.Length == 0)        { _error.Text = "Enter the account password."; return; }
        if (cfg.IncomingHost.Length == 0)    { _error.Text = "Enter the incoming (IMAP/POP3) server."; return; }
        if (cfg.SmtpHost.Length == 0)        { _error.Text = "Enter the SMTP server."; return; }

        _busy.IsVisible = true;
        _add.IsEnabled = false;
        _error.Text = "Connecting…";
        try
        {
            var problem = await MailProbe.TestAsync(cfg);
            if (problem is null)
            {
                Close(cfg);
                return;
            }
            _error.Text = "Couldn't sign in: " + problem;
        }
        catch (Exception ex) { _error.Text = "Couldn't sign in: " + ex.Message; }
        finally
        {
            _busy.IsVisible = false;
            _add.IsEnabled = true;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private static int ParsePort(string? text, int fallback) =>
        int.TryParse((text ?? "").Trim(), out var p) && p is > 0 and < 65536 ? p : fallback;
}
