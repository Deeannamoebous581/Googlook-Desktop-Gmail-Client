using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Pop3;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;
using Googlook.Models;

namespace Googlook.Services;

/// <summary>
/// POP3 implementation of <see cref="IMailClient"/> (MailKit). POP3 is the truly
/// minimal protocol, and the feature set here is honest about that:
///  - one folder (Inbox), no unread counts, no flags — mark-read/star are local no-ops;
///  - no server-side search (returns no results);
///  - the list shows the newest messages by downloading their headers (TOP);
///  - send goes out via SMTP; there is no server Sent folder to append to.
/// Connections are short-lived (connect → operate → quit) because many POP3
/// servers lock the maildrop for the duration of a session.
/// </summary>
public sealed class Pop3MailClient : IMailClient
{
    private const string IdPrefix = "pop3:";

    private readonly ImapAccountConfig _cfg;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, MimeMessage> _cache = new();
    private readonly Queue<string> _cacheOrder = new();

    public string UserEmail => _cfg.EmailAddress;

    public Pop3MailClient(ImapAccountConfig cfg) => _cfg = cfg;

    private async Task<T> RunAsync<T>(Func<Pop3Client, Task<T>> op)
    {
        await _gate.WaitAsync();
        try
        {
            using var pop = new Pop3Client();
            await pop.ConnectAsync(_cfg.IncomingHost, _cfg.IncomingPort, SecureSocketOptions.Auto);
            await pop.AuthenticateAsync(_cfg.EffectiveUsername, _cfg.Password);
            var result = await op(pop);
            await pop.DisconnectAsync(true);
            return result;
        }
        finally { _gate.Release(); }
    }

    public Task<List<MailFolder>> FoldersAsync() => RunAsync(async pop =>
    {
        var count = await pop.GetMessageCountAsync();
        // POP3 has no unread concept; showing the total would bold every launch.
        _ = count;
        return new List<MailFolder> { new() { Name = "Inbox", LabelId = "INBOX", Unread = 0 } };
    });

    public Task<List<EmailThreadSummary>> ListThreadsAsync(string labelId = "INBOX", int max = 20) =>
        RunAsync(async pop =>
        {
            var list = new List<EmailThreadSummary>();
            var count = await pop.GetMessageCountAsync();
            for (int i = count - 1; i >= 0 && list.Count < max; i--)
            {
                try
                {
                    var h = await pop.GetMessageHeadersAsync(i);
                    DateUtils.TryParse(h[HeaderId.Date] ?? "", out var date);
                    list.Add(new EmailThreadSummary
                    {
                        Id            = IdPrefix + i,
                        Subject       = string.IsNullOrWhiteSpace(h[HeaderId.Subject]) ? "(no subject)" : h[HeaderId.Subject]!,
                        Participants  = DisplayFrom(h[HeaderId.From] ?? ""),
                        Snippet       = "",
                        Date          = date,
                        Unread        = false,
                        Starred       = false,
                        Count         = 1,
                        LastMessageId = IdPrefix + i,
                    });
                }
                catch { /* skip a message whose headers won't parse */ }
            }
            return list;
        });

    /// <summary>POP3 has no server-side search; the UI reports zero results.</summary>
    public Task<List<EmailThreadSummary>> SearchThreadsAsync(string query, int max = 20) =>
        Task.FromResult(new List<EmailThreadSummary>());

    public async Task<List<EmailMessage>> ListAsync(string labelId = "INBOX", int max = 50)
    {
        var threads = await ListThreadsAsync(labelId, max);
        return threads.ConvertAll(t => new EmailMessage
        {
            Id = t.Id, ThreadId = t.Id, From = t.Participants,
            Subject = t.Subject, Date = t.Date,
        });
    }

    public Task<List<EmailMessage>> GetThreadAsync(string threadId) => RunAsync(async pop =>
    {
        var index = ParseIndex(threadId);
        var msg = await pop.GetMessageAsync(index);
        Remember(threadId, msg);
        return new List<EmailMessage> { MailMime.Map(threadId, msg) };
    });

    // POP3 has no flags — the UI state is local-only, which is all it promises.
    public Task MarkReadAsync(string id) => Task.CompletedTask;
    public Task SetStarAsync(string id, bool starred) => Task.CompletedTask;

    public async Task SendAsync(string to, string subject, string body, bool isHtml = false,
                                IReadOnlyList<OutgoingAttachment>? attachments = null,
                                string? threadId = null, string? inReplyTo = null)
    {
        var msg = SmtpMailer.Build(_cfg, to, subject, body, isHtml, attachments, inReplyTo);
        await SmtpMailer.SendAsync(_cfg, msg);
    }

    public async Task<byte[]> GetAttachmentAsync(string messageId, string attachmentId)
    {
        if (!_cache.TryGetValue(messageId, out var msg))
        {
            var fetched = await RunAsync(pop => pop.GetMessageAsync(ParseIndex(messageId)));
            Remember(messageId, fetched);
            msg = fetched;
        }
        return await MailMime.DecodePartAsync(msg, attachmentId);
    }

    public async Task<string> BuildDisplayHtmlAsync(EmailMessage m)
    {
        var html = m.HtmlBody;
        if (string.IsNullOrEmpty(html) || m.InlineImages.Count == 0) return html;
        foreach (var img in m.InlineImages)
        {
            try
            {
                var bytes = await GetAttachmentAsync(m.Id, img.AttachmentId);
                if (bytes.Length == 0) continue;
                html = html.Replace("cid:" + img.ContentId,
                    $"data:{img.MimeType};base64,{Convert.ToBase64String(bytes)}");
            }
            catch { }
        }
        return html;
    }

    private static int ParseIndex(string id) =>
        id.StartsWith(IdPrefix, StringComparison.Ordinal) && int.TryParse(id[IdPrefix.Length..], out var i)
            ? i
            : throw new FormatException("Not a POP3 message id: " + id);

    private static string DisplayFrom(string from)
    {
        try
        {
            var list = InternetAddressList.Parse(from);
            if (list.Count > 0 && list[0] is MailboxAddress mb)
                return string.IsNullOrWhiteSpace(mb.Name) ? mb.Address : mb.Name;
        }
        catch { }
        return from;
    }

    private void Remember(string id, MimeMessage msg)
    {
        if (_cache.ContainsKey(id)) { _cache[id] = msg; return; }
        _cache[id] = msg;
        _cacheOrder.Enqueue(id);
        while (_cacheOrder.Count > 12)
            _cache.Remove(_cacheOrder.Dequeue());
    }

    public void Dispose() => _gate.Dispose();
}
