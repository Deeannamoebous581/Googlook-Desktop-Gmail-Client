using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Pubsub.v1;
using Google.Apis.Pubsub.v1.Data;
using Google.Apis.Services;

namespace Googlook.Services;

/// <summary>
/// Real Gmail push for one or more accounts, desktop-friendly. Gmail can't call a
/// laptop directly, so each mailbox is registered with users.watch() against a Cloud
/// Pub/Sub topic; this service then *pulls* from a subscription (no public webhook)
/// and routes each notification to the mailbox it names. The Gmail watch expires after
/// 7 days, so it's renewed roughly daily. Falls back to nothing on error — the interval
/// poller remains the safety net.
///
/// All added accounts publish to the same topic (Gmail's push service account can
/// publish for any mailbox); the subscription is pulled with the first account's
/// credential. See README for the one-time Cloud setup.
/// </summary>
public sealed class PushWatchService : IDisposable
{
    /// <summary>Raised (off the UI thread) with the email address of the changed mailbox.</summary>
    public event Action<string>? MailArrived;

    private readonly IReadOnlyList<GmailClient> _clients;
    private readonly string _topic;
    private readonly string _subscription;
    private readonly PubsubService _pubsub;
    private readonly CancellationTokenSource _cts = new();
    private DateTimeOffset _watchRenewedAt = DateTimeOffset.MinValue;

    public PushWatchService(IReadOnlyList<GmailClient> clients, string topic, string subscription)
    {
        _clients = clients;
        _topic = topic;
        _subscription = subscription;
        _pubsub = new PubsubService(new BaseClientService.Initializer
        {
            // Any account with the pubsub scope can pull the shared subscription.
            HttpClientInitializer = clients[0].Credential,
            ApplicationName = "Googlook",
        });
    }

    public async Task StartAsync()
    {
        await RenewWatchesAsync();
        _ = Task.Run(() => PullLoopAsync(_cts.Token));
    }

    private async Task RenewWatchesAsync()
    {
        foreach (var c in _clients)
        {
            try { await c.WatchAsync(_topic); }
            catch { /* this mailbox can't publish yet — see README; poller still covers it */ }
        }
        _watchRenewedAt = DateTimeOffset.UtcNow;
    }

    private async Task PullLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow - _watchRenewedAt > TimeSpan.FromHours(24))
                    await RenewWatchesAsync();

                var pull = await _pubsub.Projects.Subscriptions
                    .Pull(new PullRequest { MaxMessages = 10, ReturnImmediately = false }, _subscription)
                    .ExecuteAsync(ct);

                if (pull.ReceivedMessages is { Count: > 0 })
                {
                    var ackIds = new List<string>();
                    foreach (var rm in pull.ReceivedMessages)
                    {
                        if (rm.AckId is not null) ackIds.Add(rm.AckId);
                        var email = ExtractEmail(rm.Message?.Data);
                        if (email is not null) MailArrived?.Invoke(email);
                    }

                    if (ackIds.Count > 0)
                        await _pubsub.Projects.Subscriptions
                            .Acknowledge(new AcknowledgeRequest { AckIds = ackIds }, _subscription)
                            .ExecuteAsync(ct);
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch { break; }
            }
        }
    }

    // Gmail's Pub/Sub payload is base64 JSON: {"emailAddress":"user@x","historyId":123}.
    private static string? ExtractEmail(string? base64Data)
    {
        if (string.IsNullOrEmpty(base64Data)) return null;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64Data));
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("emailAddress", out var e) ? e.GetString() : null;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        foreach (var c in _clients)
        {
            try { _ = c.StopWatchAsync(); } catch { }
        }
        _pubsub.Dispose();
        _cts.Dispose();
    }
}
