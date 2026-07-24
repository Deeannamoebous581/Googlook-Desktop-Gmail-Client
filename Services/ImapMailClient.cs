using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using Googlook.Models;
using MailFolder = Googlook.Models.MailFolder;   // MailKit has its own MailFolder type

namespace Googlook.Services;

/// <summary>
/// IMAP implementation of <see cref="IMailClient"/> (MailKit). One connection is
/// kept alive and every operation is serialized through a semaphore (MailKit
/// clients are not thread-safe); a dropped connection gets one transparent
/// reconnect. Differences from Gmail, by design ("limited features"):
///  - no conversation threading — every message is its own single-message thread;
///  - message ids are "folderFullName␟uid";
///  - search covers Subject + From in the Inbox (server-side IMAP SEARCH);
///  - sent mail is best-effort appended to the server's Sent folder.
/// </summary>
public sealed class ImapMailClient : IMailClient
{
    private const char Sep = '\u001F';   // folder/uid separator inside message ids
    private const int  CacheCap = 12;    // recently opened full messages (attachment fetches)

    private readonly ImapAccountConfig _cfg;
    private readonly ImapClient _imap = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, MimeMessage> _cache = new();
    private readonly Queue<string> _cacheOrder = new();
    private bool _disposed;

    public string UserEmail => _cfg.EmailAddress;

    public ImapMailClient(ImapAccountConfig cfg) => _cfg = cfg;

    // ---- connection management -------------------------------------------

    private async Task<T> RunAsync<T>(Func<ImapClient, Task<T>> op)
    {
        await _gate.WaitAsync();
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (!_imap.IsConnected)
                        await _imap.ConnectAsync(_cfg.IncomingHost, _cfg.IncomingPort, SecureSocketOptions.Auto);
                    if (!_imap.IsAuthenticated)
                        await _imap.AuthenticateAsync(_cfg.EffectiveUsername, _cfg.Password);
                    return await op(_imap);
                }
                catch (Exception ex) when (attempt == 0 && ex is not AuthenticationException)
                {
                    // Stale/dropped connection — reset once and retry the operation.
                    try { await _imap.DisconnectAsync(false); } catch { }
                }
            }
        }
        finally { _gate.Release(); }
    }

    private static (string folder, UniqueId uid) ParseId(string id)
    {
        var i = id.IndexOf(Sep);
        if (i < 0) throw new FormatException("Not an IMAP message id: " + id);
        return (id[..i], new UniqueId(uint.Parse(id[(i + 1)..])));
    }

    private static async Task<IMailFolder> GetFolderAsync(ImapClient c, string fullName) =>
        string.Equals(fullName, "INBOX", StringComparison.OrdinalIgnoreCase)
            ? c.Inbox
            : await c.GetFolderAsync(fullName);

    // ---- folders -----------------------------------------------------------

    public Task<List<MailFolder>> FoldersAsync() => RunAsync(async c =>
    {
        var result = new List<MailFolder>();

        async Task AddAsync(IMailFolder? f, string name)
        {
            if (f is null) return;
            try
            {
                await f.StatusAsync(StatusItems.Unread);
                result.Add(new MailFolder { Name = name, LabelId = f.FullName, Unread = f.Unread });
            }
            catch { /* folder exists but STATUS failed — skip it */ }
        }

        IMailFolder? Special(SpecialFolder s)
        {
            try { return c.GetFolder(s); } catch { return null; }
        }

        await AddAsync(c.Inbox, "Inbox");
        await AddAsync(Special(SpecialFolder.Sent),   "Sent");
        await AddAsync(Special(SpecialFolder.Drafts), "Drafts");
        await AddAsync(Special(SpecialFolder.Junk),   "Spam");
        await AddAsync(Special(SpecialFolder.Trash),  "Trash");
        return result;
    });

    // ---- listing -----------------------------------------------------------

    public Task<List<EmailThreadSummary>> ListThreadsAsync(string labelId = "INBOX", int max = 20) =>
        RunAsync(async c =>
        {
            var folder = await GetFolderAsync(c, labelId);
            await folder.OpenAsync(FolderAccess.ReadOnly);
            if (folder.Count == 0) return new List<EmailThreadSummary>();

            int first = Math.Max(0, folder.Count - max);
            var sums = await folder.FetchAsync(first, folder.Count - 1,
                MessageSummaryItems.Envelope | MessageSummaryItems.Flags | MessageSummaryItems.UniqueId);
            return sums.OrderByDescending(s => s.Date)
                       .Select(s => Summarise(folder.FullName, s))
                       .ToList();
        });

    public Task<List<EmailThreadSummary>> SearchThreadsAsync(string query, int max = 20) =>
        RunAsync(async c =>
        {
            var folder = c.Inbox;
            await folder.OpenAsync(FolderAccess.ReadOnly);
            var q = SearchQuery.SubjectContains(query).Or(SearchQuery.FromContains(query));
            var uids = await folder.SearchAsync(q);
            var newest = uids.Skip(Math.Max(0, uids.Count - max)).ToList();
            if (newest.Count == 0) return new List<EmailThreadSummary>();

            var sums = await folder.FetchAsync(newest,
                MessageSummaryItems.Envelope | MessageSummaryItems.Flags | MessageSummaryItems.UniqueId);
            return sums.OrderByDescending(s => s.Date)
                       .Select(s => Summarise(folder.FullName, s))
                       .ToList();
        });

    public Task<List<EmailMessage>> ListAsync(string labelId = "INBOX", int max = 50) =>
        RunAsync(async c =>
        {
            var folder = await GetFolderAsync(c, labelId);
            await folder.OpenAsync(FolderAccess.ReadOnly);
            if (folder.Count == 0) return new List<EmailMessage>();

            int first = Math.Max(0, folder.Count - max);
            var sums = await folder.FetchAsync(first, folder.Count - 1,
                MessageSummaryItems.Envelope | MessageSummaryItems.Flags | MessageSummaryItems.UniqueId);
            return sums.OrderByDescending(s => s.Date).Select(s =>
            {
                var t = Summarise(folder.FullName, s);
                return new EmailMessage
                {
                    Id = t.Id, ThreadId = t.Id,
                    From = s.Envelope?.From?.ToString() ?? t.Participants,
                    Subject = t.Subject, Date = t.Date,
                    IsUnread = t.Unread, IsStarred = t.Starred,
                };
            }).ToList();
        });

    private static EmailThreadSummary Summarise(string folderName, IMessageSummary s)
    {
        var from = s.Envelope?.From?.OfType<MailboxAddress>().FirstOrDefault();
        var display = from is null ? "" :
            string.IsNullOrWhiteSpace(from.Name) ? from.Address : from.Name;
        var flags = s.Flags ?? MessageFlags.None;
        var id = folderName + Sep + s.UniqueId.Id;

        return new EmailThreadSummary
        {
            Id            = id,
            Subject       = string.IsNullOrWhiteSpace(s.Envelope?.Subject) ? "(no subject)" : s.Envelope!.Subject!,
            Participants  = display,
            Snippet       = "",
            Date          = s.Date,
            Unread        = !flags.HasFlag(MessageFlags.Seen),
            Starred       = flags.HasFlag(MessageFlags.Flagged),
            Count         = 1,
            LastMessageId = id,
        };
    }

    // ---- single message ("thread") ------------------------------------------

    public Task<List<EmailMessage>> GetThreadAsync(string threadId) => RunAsync(async c =>
    {
        var (folderName, uid) = ParseId(threadId);
        var folder = await GetFolderAsync(c, folderName);
        await folder.OpenAsync(FolderAccess.ReadOnly);
        var msg = await folder.GetMessageAsync(uid);
        Remember(threadId, msg);
        return new List<EmailMessage> { MailMime.Map(threadId, msg) };
    });

    public Task MarkReadAsync(string id) => RunAsync<object?>(async c =>
    {
        var (folderName, uid) = ParseId(id);
        var folder = await GetFolderAsync(c, folderName);
        await folder.OpenAsync(FolderAccess.ReadWrite);
        await folder.AddFlagsAsync(new[] { uid }, MessageFlags.Seen, true);
        return null;
    });

    public Task SetStarAsync(string id, bool starred) => RunAsync<object?>(async c =>
    {
        var (folderName, uid) = ParseId(id);
        var folder = await GetFolderAsync(c, folderName);
        await folder.OpenAsync(FolderAccess.ReadWrite);
        if (starred) await folder.AddFlagsAsync(new[] { uid }, MessageFlags.Flagged, true);
        else         await folder.RemoveFlagsAsync(new[] { uid }, MessageFlags.Flagged, true);
        return null;
    });

    // ---- send ---------------------------------------------------------------

    public async Task SendAsync(string to, string subject, string body, bool isHtml = false,
                                IReadOnlyList<OutgoingAttachment>? attachments = null,
                                string? threadId = null, string? inReplyTo = null)
    {
        var msg = SmtpMailer.Build(_cfg, to, subject, body, isHtml, attachments, inReplyTo);
        await SmtpMailer.SendAsync(_cfg, msg);

        // Many IMAP servers don't copy SMTP-sent mail into Sent — append it ourselves.
        try
        {
            await RunAsync<object?>(async c =>
            {
                IMailFolder? sent = null;
                try { sent = c.GetFolder(SpecialFolder.Sent); } catch { }
                if (sent is not null) await sent.AppendAsync(msg, MessageFlags.Seen);
                return null;
            });
        }
        catch { /* message went out; the Sent copy is best-effort */ }
    }

    // ---- attachments / display body -----------------------------------------

    public Task<byte[]> GetAttachmentAsync(string messageId, string attachmentId) =>
        RunAsync(async c =>
        {
            if (!_cache.TryGetValue(messageId, out var msg))
            {
                var (folderName, uid) = ParseId(messageId);
                var folder = await GetFolderAsync(c, folderName);
                await folder.OpenAsync(FolderAccess.ReadOnly);
                msg = await folder.GetMessageAsync(uid);
                Remember(messageId, msg);
            }
            return await MailMime.DecodePartAsync(msg, attachmentId);
        });

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
            catch { /* skip an image that won't load */ }
        }
        return html;
    }

    private void Remember(string id, MimeMessage msg)
    {
        if (_cache.ContainsKey(id)) { _cache[id] = msg; return; }
        _cache[id] = msg;
        _cacheOrder.Enqueue(id);
        while (_cacheOrder.Count > CacheCap)
            _cache.Remove(_cacheOrder.Dequeue());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_imap.IsConnected) _imap.Disconnect(true); } catch { }
        _imap.Dispose();
        _gate.Dispose();
    }
}
