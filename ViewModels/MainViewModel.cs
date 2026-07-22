using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Googlook.Models;
using Googlook.Security;
using Googlook.Services;

namespace Googlook.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    public ObservableCollection<ServiceTabVM> ServiceTabs { get; } = new();
    public ObservableCollection<AccountVM>    Accounts    { get; } = new();
    public ObservableCollection<ThreadVM>     Threads     { get; } = new();

    [ObservableProperty] private ServiceTabVM? _selectedTab;
    [ObservableProperty] private ThreadVM?     _selectedThread;
    [ObservableProperty] private AccountVM?    _activeAccount;

    // Lock state ----------------------------------------------------------
    [ObservableProperty] private bool   _isLocked;
    [ObservableProperty] private bool   _needsPasscodeSetup;
    [ObservableProperty] private string _lockError = "";

    // Mail state ----------------------------------------------------------
    [ObservableProperty] private bool   _isDemoMode;
    [ObservableProperty] private bool   _isBusy;
    [ObservableProperty] private string _statusText = "";

    private readonly ConfigVault _vault;
    private readonly HtmlSanitizerService _sanitizer = new();
    private readonly List<GmailAccountSession> _sessions = new();
    private GmailAccountSession? _activeSession;   // session whose folder is open (send-from)
    private string _activeLabelId = "INBOX";
    private PushWatchService? _push;
    private readonly DispatcherTimer _pollTimer = new();
    private readonly Dictionary<string, int> _lastInboxUnread = new();
    private AppConfig  _config = new();
    private MailService? _mail;

    /// <summary>Raised (on the UI thread) to show a corner desktop notification: (title, message).</summary>
    public event Action<string, string>? NotificationRequested;

    public MainViewModel(ConfigVault vault)
    {
        _vault = vault;
        BuildServiceTabs();
        _pollTimer.Tick += (_, _) => _ = PollTickAsync();

        if (_vault.AutoKeyExists)
            _ = AutoStartAsync();      // "stay signed in" — DPAPI auto-unlock, no passcode
        else if (_vault.Exists)
            IsLocked = true;           // existing passcode vault → prompt on launch
        else
            _ = FirstRunAsync();       // fresh install: stay signed in by default (DPAPI)
    }

    // Derived visibility / state for the tab content host.
    public bool    IsMailSelected     => SelectedTab?.Kind == ServiceKind.Mail;
    public bool    IsBrowserSelected  => SelectedTab?.Kind == ServiceKind.Browser;
    public bool    IsTerminalSelected => SelectedTab?.Kind == ServiceKind.Terminal;
    public string? BrowserUrl         => SelectedTab?.Url;
    /// <summary>Profile folder the browser tabs use — follows the selected account, so switching
    /// accounts switches which Google identity Drive/Gemini/My-Account are signed in as.</summary>
    public string  ActiveProfileDir   => BrowserProfile.DirFor(ActiveAccount?.Id ?? "default");

    partial void OnSelectedTabChanged(ServiceTabVM? value)
    {
        OnPropertyChanged(nameof(IsMailSelected));
        OnPropertyChanged(nameof(IsBrowserSelected));
        OnPropertyChanged(nameof(IsTerminalSelected));
        OnPropertyChanged(nameof(BrowserUrl));
    }

    partial void OnActiveAccountChanged(AccountVM? value) => OnPropertyChanged(nameof(ActiveProfileDir));

    // ---- theme + notification settings (bound to the top-bar toggles) ---

    public bool IsDarkMode
    {
        get => _config.Settings.Theme == "Dark";
        set
        {
            _config.Settings.Theme = value ? "Dark" : "Light";
            ApplyTheme();
            PersistSettings();
            OnPropertyChanged();
        }
    }

    public bool ShowNotifications
    {
        get => _config.Settings.ShowNotifications;
        set { _config.Settings.ShowNotifications = value; PersistSettings(); OnPropertyChanged(); }
    }

    public bool PrivateNotifications
    {
        get => _config.Settings.NotificationPrivacy;
        set { _config.Settings.NotificationPrivacy = value; PersistSettings(); OnPropertyChanged(); }
    }

    private void ApplyTheme()
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = IsDarkMode ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    private void PersistSettings()
    {
        try { if (_vault.IsUnlocked) _vault.Save(_config); } catch { /* locked / not yet initialised */ }
    }

    partial void OnSelectedThreadChanged(ThreadVM? value)
    {
        if (value is not null) _ = value.OpenAsync(); // lazy-load the conversation + mark read
    }

    // ---- Lock button ----------------------------------------------------

    [RelayCommand]
    private void Lock()
    {
        _pollTimer.Stop();
        _push?.Dispose();
        _push = null;

        if (!_vault.Exists && !_vault.IsUnlocked)
        {
            NeedsPasscodeSetup = true;   // no passcode yet → overlay in "create" mode
            IsLocked = true;
            return;
        }
        _vault.Save(_config);            // re-encrypt latest state at rest
        _vault.Lock();                   // wipe key from memory
        IsLocked = true;
    }

    // ---- account + passcode management (Settings) -----------------------

    /// <summary>Shows the create-passcode overlay; TryUnlock finishes the switch to passcode mode.</summary>
    [RelayCommand]
    private void AddPasscode()
    {
        NeedsPasscodeSetup = true;
        IsLocked = true;
    }

    /// <summary>Backs out of the create-passcode overlay (only valid while the vault is still open).</summary>
    public void CancelPasscodeSetup()
    {
        if (!NeedsPasscodeSetup || !_vault.IsUnlocked) return;
        NeedsPasscodeSetup = false;
        LockError = "";
        IsLocked = false;
    }

    /// <summary>Drops the passcode and re-enables passcode-free "stay signed in" (DPAPI).</summary>
    [RelayCommand]
    private void RemovePasscode()
    {
        try
        {
            _vault.EnableAutoUnlock(_config);
            OnPropertyChanged(nameof(HasPasscodeLock));
            StatusText = "Passcode removed — Googlook will stay signed in on this PC.";
        }
        catch (Exception ex) { StatusText = "Couldn't remove passcode: " + ex.Message; }
    }

    /// <summary>Removes an account (and its saved tokens), or just drops a demo row.</summary>
    [RelayCommand]
    private async Task RemoveAccountAsync(AccountVM? account)
    {
        if (account is null) return;
        try
        {
            var real = _config.Accounts.RemoveAll(a => a.Id == account.Id) > 0;
            if (real)
            {
                foreach (var k in _config.OAuthTokens.Keys.Where(k => k.Contains(account.Id)).ToList())
                    _config.OAuthTokens.Remove(k);
                PersistSettings();
                StatusText = "Removed " + account.Email + ".";
                await RefreshAccountsAsync();
            }
            else
            {
                Accounts.Remove(account);   // demo row
                if (ReferenceEquals(ActiveAccount, account))
                    ActiveAccount = Accounts.FirstOrDefault();
            }
        }
        catch (Exception ex)
        {
            Log.Error("RemoveAccount", ex);
            StatusText = "Couldn't remove account: " + ex.Message;
        }
    }

    /// <summary>True when a passcode is required (no machine-bound key). Drives the lock UI + settings.</summary>
    public bool HasPasscodeLock => !_vault.AutoKeyExists && _vault.Exists;

    /// <summary>Called from the lock overlay. Returns false (and sets LockError) on a bad passcode.</summary>
    public bool TryUnlock(string passcode)
    {
        try
        {
            if (NeedsPasscodeSetup || !_vault.Exists || _vault.AutoKeyExists)
            {
                if (passcode.Length < 4)
                {
                    LockError = "Choose a passcode of at least 4 characters.";
                    return false;
                }
                _vault.Initialize(passcode, _config);
                _vault.DisableAutoUnlock();     // a passcode replaces "stay signed in"
                NeedsPasscodeSetup = false;
            }
            else
            {
                _config = _vault.Unlock(passcode);
            }
            LockError = "";
            IsLocked = false;
            OnPropertyChanged(nameof(HasPasscodeLock));
            _ = BootstrapAsync();        // now that the vault is open, bring mail online
            return true;
        }
        catch
        {
            LockError = "Incorrect passcode.";
            return false;
        }
    }

    /// <summary>Launch path when a DPAPI key exists: unlock silently, no passcode.</summary>
    private async Task AutoStartAsync()
    {
        try { _config = _vault.AutoUnlock(); }
        catch { _config = new AppConfig(); }   // e.g. copied to a different Windows user
        IsLocked = false;
        OnPropertyChanged(nameof(HasPasscodeLock));
        await BootstrapAsync();
    }

    /// <summary>First run: turn on "stay signed in" (DPAPI) so accounts persist with no passcode.</summary>
    private async Task FirstRunAsync()
    {
        try { _vault.EnableAutoUnlock(_config); } catch { /* DPAPI unavailable: in-memory only */ }
        IsLocked = false;
        OnPropertyChanged(nameof(HasPasscodeLock));
        await BootstrapAsync();
    }

    // ---- mail bootstrap -------------------------------------------------

    private async Task BootstrapAsync()
    {
        try
        {
            ApplyTheme();                                    // honour persisted dark/light
            OnPropertyChanged(nameof(IsDarkMode));
            OnPropertyChanged(nameof(ShowNotifications));
            OnPropertyChanged(nameof(PrivateNotifications));

            GoogleClientLoader.Ensure(_config);              // pull client id/secret from json/env if unset
            // PersistSettings never throws — a token write mid-OAuth can't kill the sign-in.
            _mail = new MailService(_config, PersistSettings);

            if (!_mail.IsConfigured || _config.Accounts.Count == 0)
            {
                IsDemoMode = true;
                StatusText = _mail.IsConfigured
                    ? "No accounts yet — click + to add a Gmail account."
                    : "Add Google credentials to sign in (see README).";
                LoadDemoData();
                return;
            }

            IsDemoMode = false;
            await RefreshAccountsAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Bootstrap", ex);
            StatusText = "Startup problem: " + ex.Message;
            IsDemoMode = true;
            LoadDemoData();
        }
    }

    /// <summary>Re-signs in every configured account and rebuilds the sidebar.</summary>
    [RelayCommand]
    private async Task RefreshAccountsAsync()
    {
        if (_mail is null || !_mail.IsConfigured) return;

        IsBusy = true;
        StatusText = "Signing in…";
        _contactSuggestions = null;   // reload contacts for the refreshed account
        try
        {
            foreach (var s in _sessions) s.Dispose();
            _sessions.Clear();
            _sessions.AddRange(await _mail.RestoreSessionsAsync());

            Accounts.Clear();
            foreach (var session in _sessions)
            {
                var captured = session;
                var display = string.IsNullOrWhiteSpace(session.Account.DisplayName)
                    ? session.Account.EmailAddress
                    : session.Account.DisplayName;

                var avm = new AccountVM(session.Account.Id, session.Account.EmailAddress, display,
                    onActivate: () => ActiveAccount = null); // set below to the real vm
                avm.SetActivateTarget(() => ActiveAccount = avm);
                avm.SetRemoveTarget(() => _ = RemoveAccountAsync(avm));

                var folders = await session.Client.FoldersAsync();
                foreach (var f in folders)
                {
                    var labelId = f.LabelId;
                    avm.Folders.Add(new FolderVM(f.Name, f.Unread, labelId,
                        onSelected: _ => LoadFolderAsync(captured, labelId)));
                }
                _lastInboxUnread[session.Account.Id] =
                    folders.FirstOrDefault(f => f.LabelId == "INBOX")?.Unread ?? 0;
                Accounts.Add(avm);
            }

            ActiveAccount = Accounts.Count > 0 ? Accounts[0] : null;
            if (_sessions.Count > 0) await LoadFolderAsync(_sessions[0], "INBOX");
            StatusText = "";
            StartPush();
            StartPolling();
        }
        catch (Exception ex)
        {
            StatusText = "Sign-in failed: " + ex.Message;
            IsDemoMode = true;
            LoadDemoData();
        }
        finally { IsBusy = false; }
    }

    /// <summary>Interactive add of a new Gmail account (opens the system browser once).</summary>
    [RelayCommand]
    private async Task AddAccount()
    {
        if (_mail is null || !_mail.IsConfigured)
        {
            StatusText = "Add Google credentials first (see README).";
            return;
        }

        IsBusy = true;
        StatusText = "Opening browser to sign in…";
        try
        {
            await _mail.AddAccountAsync();  // persists the account into _config
            _vault.Save(_config);           // encrypt the new account + its refresh token
            IsDemoMode = false;
            await RefreshAccountsAsync();
        }
        catch (Exception ex)
        {
            StatusText = "Couldn't add account: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>Sends a message from the account whose folder is currently open.</summary>
    public async Task<bool> SendMailAsync(string to, string subject, string body,
                                          IReadOnlyList<OutgoingAttachment>? attachments = null,
                                          string? threadId = null, string? inReplyTo = null)
    {
        if (_activeSession is null)
        {
            StatusText = "Sign in and open an account before composing.";
            return false;
        }
        IsBusy = true;
        StatusText = attachments is { Count: > 0 } ? "Sending with attachments…" : "Sending…";
        try
        {
            await _activeSession.Client.SendAsync(to, subject, body, isHtml: false,
                attachments: attachments, threadId: threadId, inReplyTo: inReplyTo);
            StatusText = "Sent.";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = "Send failed: " + ex.Message;
            return false;
        }
        finally { IsBusy = false; }
    }

    /// <summary>Email address of the account currently in view (for reply-all self-filtering).</summary>
    public string? ActiveAccountEmail => _activeSession?.Client.UserEmail;

    private List<string>? _contactSuggestions;

    /// <summary>Loads (once) the active account's contacts as "Name &lt;email&gt;" strings for autocomplete.</summary>
    public async Task<IReadOnlyList<string>> GetContactSuggestionsAsync()
    {
        if (_contactSuggestions is not null) return _contactSuggestions;
        var result = new List<string>();
        if (_activeSession is not null)
        {
            try
            {
                using var contacts = new ContactsClient(_activeSession.Client.Credential);
                var list = await contacts.LoadAsync();
                result = list.Select(c => c.Display).ToList();
            }
            catch { /* no contacts scope / offline — empty suggestions */ }
        }
        _contactSuggestions = result;
        return _contactSuggestions;
    }

    /// <summary>Lets views surface a status message (attachment saved, errors, etc.).</summary>
    public void NotifyStatus(string message) => StatusText = message;

    /// <summary>A Drive client for the active account, used by the compose "from Drive" picker.</summary>
    public DriveClient? CreateDriveClientForActive() =>
        _activeSession is null ? null : new DriveClient(_activeSession.Client.Credential);

    /// <summary>Starts Gmail push (Pub/Sub) for every signed-in account if enabled + configured.</summary>
    private void StartPush()
    {
        _push?.Dispose();
        _push = null;

        var s = _config.Settings;
        if (!s.UsePushWherePossible ||
            string.IsNullOrWhiteSpace(s.PubSubTopic) ||
            string.IsNullOrWhiteSpace(s.PubSubSubscription) ||
            _sessions.Count == 0)
            return;

        try
        {
            var clients = _sessions.Select(x => x.Client).ToList();
            _push = new PushWatchService(clients, s.PubSubTopic, s.PubSubSubscription);
            _push.MailArrived += email => Dispatcher.UIThread.Post(() => _ = GuardAsync(OnPushAsync(email), "Push"));
            _ = GuardAsync(_push.StartAsync(), "PushStart");
            StatusText = $"Push enabled for {clients.Count} account(s).";
        }
        catch (Exception ex)
        {
            Log.Error("StartPush", ex);
            StatusText = "Push unavailable — the interval poller still covers new mail.";
        }
    }

    /// <summary>A push notification names the changed mailbox; route it to that account.</summary>
    private async Task OnPushAsync(string email)
    {
        try
        {
            var session = _sessions.FirstOrDefault(x =>
                string.Equals(x.Client.UserEmail, email, StringComparison.OrdinalIgnoreCase));
            if (session is null) return;

            await RefreshUnreadAsync(session);                 // update that account's badges

            if (ReferenceEquals(session, _activeSession))
                await LoadFolderAsync(session, _activeLabelId); // reload the folder we're viewing
            else
                StatusText = "New activity in " + email;
        }
        catch (Exception ex) { Log.Error("OnPush", ex); }
    }

    /// <summary>Refreshes one account's folder unread counts and notifies on new mail.</summary>
    private async Task RefreshUnreadAsync(GmailAccountSession session)
    {
        var avm = Accounts.FirstOrDefault(a => a.Id == session.Account.Id);
        try
        {
            var folders = await session.Client.FoldersAsync();
            if (avm is not null)
                foreach (var f in folders)
                {
                    var fvm = avm.Folders.FirstOrDefault(x => x.LabelId == f.LabelId);
                    if (fvm is not null) fvm.Unread = f.Unread;
                }

            var inbox = folders.FirstOrDefault(f => f.LabelId == "INBOX")?.Unread ?? 0;
            await MaybeNotifyAsync(session, inbox);
        }
        catch { /* transient — badges just won't update this round */ }
    }

    /// <summary>Fires a corner notification when an account's inbox unread count goes up.</summary>
    private async Task MaybeNotifyAsync(GmailAccountSession session, int inboxUnread)
    {
        var id = session.Account.Id;
        var had = _lastInboxUnread.TryGetValue(id, out var prev);
        _lastInboxUnread[id] = inboxUnread;

        if (!had || inboxUnread <= prev || !ShowNotifications) return;

        var account = session.Client.UserEmail;
        if (PrivateNotifications)
        {
            NotificationRequested?.Invoke("New mail", account + " — you have new mail");
            return;
        }

        try
        {
            var newest = await session.Client.ListAsync("INBOX", 1);
            if (newest.Count > 0)
            {
                var m = newest[0];
                var who = SenderName(m.From);
                NotificationRequested?.Invoke(
                    who.Length > 0 ? who : "New mail",
                    string.IsNullOrWhiteSpace(m.Subject) ? account : m.Subject);
                return;
            }
        }
        catch { /* fall through to a generic notice */ }

        NotificationRequested?.Invoke("New mail", account);
    }

    private static string SenderName(string from)
    {
        var lt = from.IndexOf('<');
        var name = lt > 0 ? from[..lt].Trim().Trim('"') : from.Trim();
        return string.IsNullOrWhiteSpace(name) ? from : name;
    }

    /// <summary>Interval fallback so badges + notifications work even without Pub/Sub push.</summary>
    private void StartPolling()
    {
        _pollTimer.Stop();
        _pollTimer.Interval = TimeSpan.FromSeconds(Math.Max(30, _config.Settings.PollIntervalSeconds));
        if (_sessions.Count > 0 && !IsDemoMode) _pollTimer.Start();
    }

    private async Task PollTickAsync()
    {
        try
        {
            foreach (var s in _sessions.ToList())
                await RefreshUnreadAsync(s);   // badges + new-mail notifications
        }
        catch (Exception ex) { Log.Error("PollTick", ex); }
    }

    /// <summary>Observes a fire-and-forget task so a failure is logged instead of lost.</summary>
    private static async Task GuardAsync(Task task, string context)
    {
        try { await task; } catch (Exception ex) { Log.Error(context, ex); }
    }

    /// <summary>Stops all background work and releases sessions — called when the window closes.</summary>
    public void Dispose()
    {
        try { _pollTimer.Stop(); } catch { }
        try { _push?.Dispose(); } catch { }
        _push = null;
        foreach (var s in _sessions) { try { s.Dispose(); } catch { } }
        _sessions.Clear();
    }

    private async Task LoadFolderAsync(GmailAccountSession session, string labelId)
    {
        _activeSession = session;
        _activeLabelId = labelId;
        IsBusy = true;
        try
        {
            var threads = await session.Client.ListThreadsAsync(labelId, 20);
            Threads.Clear();
            foreach (var t in threads)
                Threads.Add(new ThreadVM(t, session, _sanitizer, _config.Settings.BlockRemoteContent));
            SelectedThread = Threads.Count > 0 ? Threads[0] : null;
        }
        catch (Exception ex) { StatusText = "Couldn't load conversations: " + ex.Message; }
        finally { IsBusy = false; }
    }

    // ---- setup / demo ---------------------------------------------------

    private void BuildServiceTabs()
    {
        ServiceTabs.Add(new ServiceTabVM("Mail",       ServiceKind.Mail));
        ServiceTabs.Add(new ServiceTabVM("Drive",      ServiceKind.Browser,  "https://drive.google.com"));
        ServiceTabs.Add(new ServiceTabVM("Gemini",     ServiceKind.Browser,  "https://gemini.google.com"));
        ServiceTabs.Add(new ServiceTabVM("Gemini CLI", ServiceKind.Terminal));
        ServiceTabs.Add(new ServiceTabVM("Browser",    ServiceKind.Browser,  "https://myaccount.google.com"));
        SelectedTab = ServiceTabs[0];
    }

    private void LoadDemoData()
    {
        Accounts.Clear();
        Threads.Clear();

        var primary = new AccountVM("demo-primary", "you@gmail.com", "You");
        primary.SetActivateTarget(() => ActiveAccount = primary);
        primary.SetRemoveTarget(() => _ = RemoveAccountAsync(primary));
        primary.Folders.Add(new FolderVM("Inbox", 3));
        primary.Folders.Add(new FolderVM("Starred", 0));
        primary.Folders.Add(new FolderVM("Sent", 0));
        primary.Folders.Add(new FolderVM("Drafts", 1));
        Accounts.Add(primary);

        var work = new AccountVM("demo-work", "work@gmail.com", "Work");
        work.SetActivateTarget(() => ActiveAccount = work);
        work.SetRemoveTarget(() => _ = RemoveAccountAsync(work));
        work.Folders.Add(new FolderVM("Inbox", 12));
        work.Folders.Add(new FolderVM("Starred", 2));
        Accounts.Add(work);

        ActiveAccount = primary;

        Threads.Add(new ThreadVM("GitHub", "GitHub", "main #482 finished in 2m 14s",
            DateTimeOffset.Now.AddMinutes(-4), unread: true, starred: false, new[]
            {
                new MessageVM("GitHub", "Your build passed", "main #482 finished in 2m 14s",
                    DateTimeOffset.Now.AddMinutes(-4), true),
            }));

        Threads.Add(new ThreadVM("Google", "Google", "New sign-in on Windows",
            DateTimeOffset.Now.AddHours(-2), unread: true, starred: true, new[]
            {
                new MessageVM("Google", "Security alert", "New sign-in on Windows",
                    DateTimeOffset.Now.AddHours(-2), true),
            }));

        // A multi-message conversation to show threading + the conversation strip.
        Threads.Add(new ThreadVM("Alex Rivera, You", "Alex Rivera", "Sounds good — shipping it 🚀",
            DateTimeOffset.Now.AddHours(-5), unread: false, starred: false, new[]
            {
                new MessageVM("Alex Rivera", "Re: Design review", "Can you take a look at the new layout?",
                    DateTimeOffset.Now.AddHours(-7), false),
                new MessageVM("You", "Re: Design review", "Looks great, one note on the spacing.",
                    DateTimeOffset.Now.AddHours(-6), false),
                new MessageVM("Alex Rivera", "Re: Design review", "Sounds good — shipping it 🚀",
                    DateTimeOffset.Now.AddHours(-5), false),
            }));

        Threads.Add(new ThreadVM("Stripe", "Stripe", "You received a payment of $240.00",
            DateTimeOffset.Now.AddHours(-9), unread: false, starred: false, new[]
            {
                new MessageVM("Stripe", "Payment received", "You received a payment of $240.00",
                    DateTimeOffset.Now.AddHours(-9), false),
            }));

        SelectedThread = Threads.Count > 0 ? Threads[0] : null;
    }
}

// ---- item view models ---------------------------------------------------

public enum ServiceKind { Mail, Browser, Terminal }

public sealed class ServiceTabVM
{
    public string      Title { get; }
    public ServiceKind Kind  { get; }
    public string?     Url   { get; }
    public ServiceTabVM(string title, ServiceKind kind, string? url = null)
    { Title = title; Kind = kind; Url = url; }
}

public partial class AccountVM : ObservableObject
{
    public string Id      { get; }
    public string Email   { get; }
    public string Display { get; }
    public string Initial => string.IsNullOrEmpty(Display) ? "?" : Display.Substring(0, 1).ToUpperInvariant();
    public ObservableCollection<FolderVM> Folders { get; } = new();
    [ObservableProperty] private bool _isExpanded = true;

    private Action? _onActivate;
    private Action? _onRemove;

    public AccountVM(string id, string email, string display, Action? onActivate = null)
    { Id = id; Email = email; Display = display; _onActivate = onActivate; }

    /// <summary>Lets the owner point activation at the finished VM instance.</summary>
    public void SetActivateTarget(Action onActivate) => _onActivate = onActivate;

    /// <summary>Lets the owner handle "remove this account" from the settings list.</summary>
    public void SetRemoveTarget(Action onRemove) => _onRemove = onRemove;

    // Clicking the account header both expands it and makes it the active profile.
    [RelayCommand]
    private void Activate()
    {
        IsExpanded = !IsExpanded;
        _onActivate?.Invoke();
    }

    [RelayCommand]
    private void Remove() => _onRemove?.Invoke();
}

public partial class FolderVM : ObservableObject
{
    public string Name    { get; }
    public string LabelId { get; }
    [ObservableProperty] private int _unread;

    private readonly Func<FolderVM, Task>? _onSelected;

    public FolderVM(string name, int unread, string labelId = "", Func<FolderVM, Task>? onSelected = null)
    { Name = name; _unread = unread; LabelId = labelId; _onSelected = onSelected; }

    [RelayCommand]
    private async Task Select()
    {
        if (_onSelected is not null) await _onSelected(this);
    }
}

public partial class MessageVM : ObservableObject
{
    public string         Id      { get; }
    public string         Sender  { get; }
    public string         Subject { get; }
    public string         Snippet { get; }
    public DateTimeOffset Date    { get; }
    public string         TimeLabel => Date.ToString("t");

    // Raw fields used to build replies / forwards.
    public string FromRaw         { get; } = "";
    public string ToRaw           { get; } = "";
    public string ThreadId        { get; } = "";
    public string Rfc822MessageId { get; } = "";
    public string QuoteText       { get; } = "";

    [ObservableProperty] private bool _isUnread;
    [ObservableProperty] private bool _isStarred;

    /// <summary>Sanitized HTML body. Re-derived (with remote content) when the user shows images.</summary>
    [ObservableProperty] private string _bodyText = "";

    /// <summary>True while remote content is being stripped (the "Show images" banner shows).</summary>
    [ObservableProperty] private bool _remoteBlocked;

    /// <summary>Inverse of <see cref="RemoteBlocked"/> — bound to the reader's AllowRemote.</summary>
    public bool ShowRemote => !RemoteBlocked;
    partial void OnRemoteBlockedChanged(bool value) => OnPropertyChanged(nameof(ShowRemote));

    public ObservableCollection<AttachmentVM> Attachments { get; } = new();
    public bool HasAttachments => Attachments.Count > 0;

    private readonly Func<string, Task>? _markRead;
    private readonly Func<string, bool, Task>? _setStar;
    private readonly HtmlSanitizerService? _sanitizer;
    private readonly string _displayHtml = "";   // cid-inlined body, before remote stripping

    /// <summary>Live message from the Gmail API. The VM sanitizes so it can un-block on demand.</summary>
    public MessageVM(EmailMessage m, Func<string, Task>? markRead,
                     string displayHtml, HtmlSanitizerService sanitizer, bool blockRemote,
                     Func<string, bool, Task>? setStar = null,
                     Func<string, string, Task<byte[]>>? getAttachment = null)
    {
        Id        = m.Id;
        Sender    = ParseSender(m.From);
        Subject   = string.IsNullOrWhiteSpace(m.Subject) ? "(no subject)" : m.Subject;
        Snippet   = m.Snippet;
        Date      = m.Date;
        _isUnread = m.IsUnread;
        _isStarred = m.IsStarred;
        _markRead = markRead;
        _setStar  = setStar;
        _sanitizer = sanitizer;
        _displayHtml = displayHtml;
        try { _bodyText = sanitizer.Sanitize(displayHtml, blockRemote); }
        catch (Exception ex) { Log.Error("Sanitize", ex); _bodyText = WebUtility.HtmlEncode(m.Snippet); }
        _remoteBlocked = blockRemote;

        FromRaw         = m.From;
        ToRaw           = m.To;
        ThreadId        = m.ThreadId;
        Rfc822MessageId = m.Rfc822MessageId;
        QuoteText       = string.IsNullOrWhiteSpace(m.PlainBody) ? m.Snippet : m.PlainBody;

        if (getAttachment is not null)
            foreach (var a in m.Attachments)
                Attachments.Add(new AttachmentVM(a, getAttachment));
    }

    /// <summary>Demo constructor (no backing Gmail message).</summary>
    public MessageVM(string sender, string subject, string snippet, DateTimeOffset date, bool unread)
    {
        Id = ""; Sender = sender; Subject = subject; Snippet = snippet; Date = date;
        _isUnread = unread; _bodyText = snippet; _remoteBlocked = false;
    }

    /// <summary>"Show images" — re-sanitize keeping remote URLs and let the reader load them.</summary>
    [RelayCommand]
    private void ShowRemoteContent()
    {
        if (_sanitizer is null) return;
        try
        {
            RemoteBlocked = false;                                             // reader AllowRemote → true
            BodyText = _sanitizer.Sanitize(_displayHtml, blockRemote: false);  // restore remote refs + re-render
        }
        catch (Exception ex) { Log.Error("ShowRemote", ex); }
    }

    /// <summary>Opening a message marks it read locally, then tells Gmail (best-effort).</summary>
    public async Task OpenAsync()
    {
        if (!IsUnread) return;
        IsUnread = false;
        if (_markRead is not null && Id.Length > 0)
        {
            try { await _markRead(Id); }
            catch { /* offline: the UI already reflects the change, Gmail syncs next time */ }
        }
    }

    /// <summary>Toggles the star locally, then in Gmail (best-effort).</summary>
    [RelayCommand]
    private async Task ToggleStar()
    {
        IsStarred = !IsStarred;
        if (_setStar is not null && Id.Length > 0)
        {
            try { await _setStar(Id, IsStarred); }
            catch { /* offline: UI already updated */ }
        }
    }

    private static string ParseSender(string from)
    {
        var lt = from.IndexOf('<');
        var name = lt > 0 ? from.Substring(0, lt).Trim().Trim('"') : from.Trim();
        return string.IsNullOrWhiteSpace(name) ? from : name;
    }
}

/// <summary>A conversation (Gmail thread): a summary for the list plus its messages, loaded lazily.</summary>
public partial class ThreadVM : ObservableObject
{
    public string          Id           { get; } = "";
    public string          Subject      { get; }
    public string          Participants { get; }
    public string          Snippet      { get; }
    public DateTimeOffset  Date         { get; }
    public string          TimeLabel    => Date.ToString("t");
    public int             Count        { get; }
    public string          CountLabel   => Count > 1 ? Count.ToString() : "";
    public bool            HasMultiple  => Count > 1;

    [ObservableProperty] private bool _isUnread;
    [ObservableProperty] private bool _isStarred;
    [ObservableProperty] private bool _isLoading;
    private bool _loaded;

    public ObservableCollection<MessageVM> Messages { get; } = new();
    [ObservableProperty] private MessageVM? _selectedMessage;

    private readonly GmailAccountSession? _session;
    private readonly HtmlSanitizerService? _sanitizer;
    private readonly bool _blockRemote;
    private readonly string _lastMessageId = "";

    /// <summary>Live conversation summary from Gmail (messages fetched on open).</summary>
    public ThreadVM(EmailThreadSummary s, GmailAccountSession session,
                    HtmlSanitizerService sanitizer, bool blockRemote)
    {
        Id = s.Id; Subject = s.Subject; Participants = s.Participants; Snippet = s.Snippet;
        Date = s.Date; Count = s.Count; _isUnread = s.Unread; _isStarred = s.Starred;
        _lastMessageId = s.LastMessageId;
        _session = session; _sanitizer = sanitizer; _blockRemote = blockRemote;
    }

    /// <summary>Demo conversation with pre-built messages.</summary>
    public ThreadVM(string participants, string leadSender, string snippet, DateTimeOffset date,
                    bool unread, bool starred, IEnumerable<MessageVM> messages)
    {
        Participants = string.IsNullOrWhiteSpace(participants) ? leadSender : participants;
        Snippet = snippet; Date = date; _isUnread = unread; _isStarred = starred;
        foreach (var m in messages) Messages.Add(m);
        Count = Messages.Count;
        Subject = Messages.Count > 0 ? Messages[0].Subject : "(no subject)";
        SelectedMessage = Messages.Count > 0 ? Messages[^1] : null;
        _loaded = true;
    }

    /// <summary>Loads the full conversation on first open and marks unread messages read.</summary>
    public async Task OpenAsync()
    {
        if (_loaded || _session is null)
        {
            if (SelectedMessage is null && Messages.Count > 0) SelectedMessage = Messages[^1];
            IsUnread = false;
            return;
        }
        IsLoading = true;
        try
        {
            var msgs = await _session.Client.GetThreadAsync(Id);
            Messages.Clear();
            foreach (var m in msgs)
            {
                var htmlWithImages = m.InlineImages.Count > 0
                    ? await _session.Client.BuildDisplayHtmlAsync(m)
                    : m.HtmlBody;
                var displayHtml = string.IsNullOrEmpty(htmlWithImages)
                    ? WebUtility.HtmlEncode(m.PlainBody)
                    : htmlWithImages;
                Messages.Add(new MessageVM(m,
                    id => _session.Client.MarkReadAsync(id),
                    displayHtml, _sanitizer!, _blockRemote,
                    (id, star) => _session.Client.SetStarAsync(id, star),
                    (mid, aid) => _session.Client.GetAttachmentAsync(mid, aid)));
            }
            SelectedMessage = Messages.Count > 0 ? Messages[^1] : null; // newest expanded
            _loaded = true;

            foreach (var mvm in Messages) if (mvm.IsUnread) _ = mvm.OpenAsync();
            IsUnread = false;
        }
        catch { /* keep the summary; body just won't populate */ }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task ToggleStar()
    {
        IsStarred = !IsStarred;
        if (_session is not null && _lastMessageId.Length > 0)
        {
            try { await _session.Client.SetStarAsync(_lastMessageId, IsStarred); } catch { }
        }
    }
}

/// <summary>A received attachment: name/size for the chip, bytes fetched on demand for Save.</summary>
public sealed class AttachmentVM
{
    public string Filename  { get; }
    public string MimeType  { get; }
    public long   Size      { get; }
    public string SizeLabel => Format(Size);

    private readonly Func<Task<byte[]>> _fetch;

    public AttachmentVM(EmailAttachment a, Func<string, string, Task<byte[]>> fetch)
    {
        Filename = a.Filename; MimeType = a.MimeType; Size = a.Size;
        _fetch = () => fetch(a.MessageId, a.AttachmentId);
    }

    public Task<byte[]> FetchAsync() => _fetch();

    private static string Format(long b) =>
        b >= 1 << 20 ? $"{b / 1024.0 / 1024:0.#} MB" :
        b >= 1024    ? $"{b / 1024.0:0.#} KB" : $"{b} B";
}
