using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Googlook.Models;
using Googlook.Security;

namespace Googlook.Services;

/// <summary>A signed-in account: the persisted <see cref="Account"/> plus a live mail client
/// (Gmail REST for Google accounts, IMAP/POP3 via MailKit for everything else).</summary>
public sealed class MailAccountSession : IDisposable
{
    public Account     Account { get; }
    public IMailClient Client  { get; }

    /// <summary>Non-null only for Google accounts — gates push/contacts/Drive extras.</summary>
    public GmailClient? Gmail => Client as GmailClient;

    public MailAccountSession(Account account, IMailClient client)
    {
        Account = account;
        Client  = client;
    }

    public void Dispose() => Client.Dispose();
}

/// <summary>
/// Signs accounts in and hands back live <see cref="IMailClient"/>s. Google OAuth
/// tokens are read from / written to the encrypted vault via <see cref="VaultDataStore"/>;
/// IMAP/POP3 credentials live in the same vault. Restoring an already-authorized
/// Google account is silent; adding a new one opens the system browser once.
/// </summary>
public sealed class MailService
{
    private readonly AppConfig         _config;
    private readonly GoogleAuthService _auth;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_config.GoogleClientId) &&
        !string.IsNullOrWhiteSpace(_config.GoogleClientSecret);

    public MailService(AppConfig config, Action persistVault)
    {
        _config = config;
        var store = new VaultDataStore(config, persistVault);
        _auth = new GoogleAuthService(config.GoogleClientId, config.GoogleClientSecret, store);
    }

    /// <summary>
    /// Signs in every saved account — Google (silent when tokens are valid) and
    /// IMAP/POP3 (constructed lazily; the first server call surfaces any error).
    /// One broken account no longer takes the rest down: it's skipped and reported.
    /// </summary>
    public async Task<(List<MailAccountSession> sessions, List<string> failures)>
        RestoreSessionsAsync(CancellationToken ct = default)
    {
        var sessions = new List<MailAccountSession>();
        var failures = new List<string>();

        if (IsConfigured)
        {
            foreach (var acct in _config.Accounts)
            {
                try
                {
                    var cred = await _auth.AuthorizeAsync(acct.Id, ct);
                    sessions.Add(new MailAccountSession(acct, await GmailClient.CreateAsync(cred)));
                }
                catch (Exception ex)
                {
                    Log.Error("Restore " + acct.EmailAddress, ex);
                    failures.Add(acct.EmailAddress);
                }
            }
        }

        foreach (var cfg in _config.ImapAccounts)
        {
            IMailClient client = string.Equals(cfg.Protocol, "pop3", StringComparison.OrdinalIgnoreCase)
                ? new Pop3MailClient(cfg)
                : new ImapMailClient(cfg);
            sessions.Add(new MailAccountSession(ToAccount(cfg), client));
        }

        return (sessions, failures);
    }

    /// <summary>Interactive add of a new Google account. Opens the browser, then persists it to the config.</summary>
    public async Task<MailAccountSession> AddAccountAsync(CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var cred = await _auth.AuthorizeAsync(id, ct);
        var client = await GmailClient.CreateAsync(cred);

        // Signing in an address that's already added? Replace the old entry (and its
        // stored tokens) so the account list stays unique instead of accumulating twins.
        var stale = _config.Accounts
            .Where(a => string.Equals(a.EmailAddress, client.UserEmail, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var old in stale)
        {
            _config.Accounts.Remove(old);
            foreach (var k in _config.OAuthTokens.Keys.Where(k => k.Contains(old.Id)).ToList())
                _config.OAuthTokens.Remove(k);
        }

        var acct = new Account
        {
            Id           = id,
            EmailAddress = client.UserEmail,
            DisplayName  = client.UserEmail.Split('@')[0],
            ProfileDir   = BrowserProfile.DirFor(id),
        };
        _config.Accounts.Add(acct);
        return new MailAccountSession(acct, client);
    }

    private static Account ToAccount(ImapAccountConfig c) => new()
    {
        Id           = c.Id,
        EmailAddress = c.EmailAddress,
        DisplayName  = string.IsNullOrWhiteSpace(c.DisplayName)
                           ? c.EmailAddress.Split('@')[0]
                           : c.DisplayName,
        ProfileDir   = "",   // no Google browser profile for these accounts
    };
}
