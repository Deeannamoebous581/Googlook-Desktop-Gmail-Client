using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Googlook.Models;

namespace Googlook.Services;

/// <summary>
/// Interval inbox checking (Thunderbird-style). On each tick it asks the Gmail
/// History API what changed since the last cursor per account — cheap and needs
/// no server.
///
/// True *push* requires Google Cloud Pub/Sub: call users.watch() to have Gmail
/// publish notifications, with a public HTTPS endpoint to receive them. That is
/// documented in the README as an optional upgrade; interval polling is the
/// pragmatic default for a desktop client.
/// </summary>
public sealed class MailPoller : IAsyncDisposable
{
    private readonly Func<IEnumerable<(Account acct, GmailClient client)>> _accounts;
    private readonly int _intervalSeconds;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>Raised on the thread pool when new message ids arrive for an account.</summary>
    public event Action<Account, List<string>>? NewMessages;

    public MailPoller(Func<IEnumerable<(Account, GmailClient)>> accounts, int intervalSeconds)
    {
        _accounts = accounts;
        _intervalSeconds = Math.Max(15, intervalSeconds); // don't hammer the API
    }

    public void Start() => _loop ??= Task.Run(RunAsync);

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            // Snapshot inside a try: if the account list is swapped/mutated mid-tick,
            // the enumeration failure must not silently kill the whole poll loop.
            List<(Account acct, GmailClient client)> accounts;
            try { accounts = new List<(Account, GmailClient)>(_accounts()); }
            catch { accounts = new(); }

            foreach (var (acct, client) in accounts)
            {
                try
                {
                    if (string.IsNullOrEmpty(acct.LastHistoryId))
                    {
                        // First run for this account: seed the cursor so the next
                        // tick can do a cheap incremental diff.
                        acct.LastHistoryId = await client.CurrentHistoryIdAsync();
                        continue;
                    }

                    var (ids, latest) = await client.ChangesAsync(acct.LastHistoryId!);
                    if (latest is not null) acct.LastHistoryId = latest;
                    if (ids.Count > 0) NewMessages?.Invoke(acct, ids);
                }
                catch
                {
                    // Transient network/auth hiccup — swallow and retry next tick.
                }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), _cts.Token); }
            catch (TaskCanceledException) { break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null) { try { await _loop; } catch { /* ignore */ } }
        _cts.Dispose();
    }
}
