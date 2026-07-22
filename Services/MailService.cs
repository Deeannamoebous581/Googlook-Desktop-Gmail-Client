using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Googlook.Models;
using Googlook.Security;

namespace Googlook.Services;

/// <summary>A signed-in account: the persisted <see cref="Account"/> plus a live Gmail client.</summary>
public sealed class GmailAccountSession : IDisposable
{
    public Account     Account { get; }
    public GmailClient Client  { get; }

    public GmailAccountSession(Account account, GmailClient client)
    {
        Account = account;
        Client  = client;
    }

    public void Dispose() => Client.Dispose();
}

/// <summary>
/// Signs accounts in and hands back live <see cref="GmailClient"/>s. OAuth tokens
/// are read from / written to the encrypted vault via <see cref="VaultDataStore"/>,
/// so each of the (up to ten) accounts stays logged in across restarts with no
/// plaintext credential ever touching disk. Restoring an already-authorized
/// account is silent; adding a new one opens the system browser once.
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

    /// <summary>Signs in every account already saved in the config (silent when tokens are valid).</summary>
    public async Task<List<GmailAccountSession>> RestoreSessionsAsync(CancellationToken ct = default)
    {
        var sessions = new List<GmailAccountSession>();
        foreach (var acct in _config.Accounts)
        {
            var cred = await _auth.AuthorizeAsync(acct.Id, ct);
            sessions.Add(new GmailAccountSession(acct, new GmailClient(cred)));
        }
        return sessions;
    }

    /// <summary>Interactive add of a new account. Opens the browser, then persists it to the config.</summary>
    public async Task<GmailAccountSession> AddAccountAsync(CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var cred = await _auth.AuthorizeAsync(id, ct);
        var client = new GmailClient(cred);

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
        return new GmailAccountSession(acct, client);
    }
}
