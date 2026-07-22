using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using Googlook.Models;

namespace Googlook.Services;

/// <summary>
/// Thin wrapper over the Gmail REST API. Because we read structured data here
/// (never the gmail.com web page), Google's web-app trackers are simply never
/// loaded. In-email tracking pixels are stripped later by HtmlSanitizerService.
/// </summary>
public sealed class GmailClient : IDisposable
{
    private readonly GmailService _svc;
    private readonly UserCredential _credential;
    public string UserEmail { get; }
    /// <summary>The OAuth credential behind this client (reused by the push service for Pub/Sub).</summary>
    public UserCredential Credential => _credential;

    public GmailClient(UserCredential credential, string appName = "Googlook")
    {
        _credential = credential;
        _svc = new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = appName,
        });
        UserEmail = _svc.Users.GetProfile("me").Execute().EmailAddress;
    }

    public async Task<List<EmailMessage>> ListAsync(string labelId = "INBOX", int max = 50)
    {
        var req = _svc.Users.Messages.List("me");
        req.LabelIds = new[] { labelId };
        req.MaxResults = max;

        var resp = await req.ExecuteAsync();
        var result = new List<EmailMessage>();
        if (resp.Messages is null) return result;

        foreach (var m in resp.Messages)
            result.Add(Map(await _svc.Users.Messages.Get("me", m.Id).ExecuteAsync()));
        return result;
    }

    public async Task<EmailMessage> GetAsync(string id) =>
        Map(await _svc.Users.Messages.Get("me", id).ExecuteAsync());

    public Task MarkReadAsync(string id) =>
        _svc.Users.Messages.Modify(
            new ModifyMessageRequest { RemoveLabelIds = new[] { "UNREAD" } }, "me", id)
            .ExecuteAsync();

    /// <summary>Adds or removes the STARRED label on a message.</summary>
    public Task SetStarAsync(string id, bool starred)
    {
        var req = starred
            ? new ModifyMessageRequest { AddLabelIds    = new[] { "STARRED" } }
            : new ModifyMessageRequest { RemoveLabelIds = new[] { "STARRED" } };
        return _svc.Users.Messages.Modify(req, "me", id).ExecuteAsync();
    }

    /// <summary>Sends a message from this account, optionally with attachments / as a reply.</summary>
    public Task SendAsync(string to, string subject, string body, bool isHtml = false,
                          IReadOnlyList<OutgoingAttachment>? attachments = null,
                          string? threadId = null, string? inReplyTo = null)
    {
        var mime = BuildMime(UserEmail, to, subject, body, isHtml, attachments, inReplyTo);
        var msg = new Message { Raw = ToBase64Url(mime) };
        if (!string.IsNullOrEmpty(threadId)) msg.ThreadId = threadId;  // attach to the conversation
        return _svc.Users.Messages.Send(msg, "me").ExecuteAsync();
    }

    /// <summary>
    /// Registers Gmail push for this mailbox: Gmail will publish change notifications to
    /// the given Cloud Pub/Sub topic. Returns the baseline history id + expiration
    /// (watch must be renewed roughly daily). See PushWatchService + README.
    /// </summary>
    public async Task<(string? historyId, DateTimeOffset expiration)> WatchAsync(string topicName)
    {
        var resp = await _svc.Users.Watch(
            new WatchRequest { TopicName = topicName, LabelIds = new[] { "INBOX" } }, "me")
            .ExecuteAsync();
        var exp = resp.Expiration is long ms
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : DateTimeOffset.UtcNow.AddDays(1);
        return (resp.HistoryId?.ToString(), exp);
    }

    /// <summary>Stops Gmail push for this mailbox.</summary>
    public Task StopWatchAsync() => _svc.Users.Stop("me").ExecuteAsync();

    // ---- outgoing MIME helpers ------------------------------------------

    private static string BuildMime(string from, string to, string subject, string body, bool isHtml,
                                    IReadOnlyList<OutgoingAttachment>? attachments, string? inReplyTo = null)
    {
        var sb = new StringBuilder();
        sb.Append("From: ").Append(from).Append("\r\n");
        sb.Append("To: ").Append(to).Append("\r\n");
        sb.Append("Subject: ").Append(EncodeHeader(subject)).Append("\r\n");
        sb.Append("Date: ").Append(DateTime.UtcNow.ToString("r")).Append("\r\n");
        sb.Append("MIME-Version: 1.0\r\n");
        if (!string.IsNullOrEmpty(inReplyTo))
        {
            sb.Append("In-Reply-To: ").Append(inReplyTo).Append("\r\n");
            sb.Append("References: ").Append(inReplyTo).Append("\r\n");
        }

        var bodyB64 = ChunkBase64(Convert.ToBase64String(Encoding.UTF8.GetBytes(body)));

        if (attachments is null || attachments.Count == 0)
        {
            sb.Append("Content-Type: text/").Append(isHtml ? "html" : "plain").Append("; charset=utf-8\r\n");
            sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
            sb.Append(bodyB64);
            return sb.ToString();
        }

        var boundary = "==Googlook_" + Guid.NewGuid().ToString("N");
        sb.Append("Content-Type: multipart/mixed; boundary=\"").Append(boundary).Append("\"\r\n\r\n");

        // Body part
        sb.Append("--").Append(boundary).Append("\r\n");
        sb.Append("Content-Type: text/").Append(isHtml ? "html" : "plain").Append("; charset=utf-8\r\n");
        sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
        sb.Append(bodyB64).Append("\r\n");

        // Attachment parts
        foreach (var a in attachments)
        {
            var name = EncodeHeader(a.Filename);
            sb.Append("--").Append(boundary).Append("\r\n");
            sb.Append("Content-Type: ").Append(a.MimeType).Append("; name=\"").Append(name).Append("\"\r\n");
            sb.Append("Content-Disposition: attachment; filename=\"").Append(name).Append("\"\r\n");
            sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
            sb.Append(ChunkBase64(Convert.ToBase64String(a.Data))).Append("\r\n");
        }

        sb.Append("--").Append(boundary).Append("--\r\n");
        return sb.ToString();
    }

    private static bool IsAscii(string s)
    {
        foreach (var c in s) if (c > 127) return false;
        return true;
    }

    private static string EncodeHeader(string s) =>
        IsAscii(s) ? s : "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(s)) + "?=";

    private static string ChunkBase64(string b64)
    {
        var sb = new StringBuilder(b64.Length + b64.Length / 76 * 2);
        for (int i = 0; i < b64.Length; i += 76)
        {
            sb.Append(b64, i, Math.Min(76, b64.Length - i));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    private static string ToBase64Url(string s) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(s))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>
    /// Incremental sync via the History API — returns ids added since
    /// <paramref name="startHistoryId"/>. Far cheaper than re-listing on every
    /// poll, and the basis for near-real-time interval checks.
    /// </summary>
    public async Task<(List<string> newIds, string? latest)> ChangesAsync(string startHistoryId)
    {
        var req = _svc.Users.History.List("me");
        req.StartHistoryId = ulong.Parse(startHistoryId);

        var resp = await req.ExecuteAsync();
        var ids = new List<string>();
        if (resp.History is not null)
            foreach (var h in resp.History.Where(h => h.MessagesAdded is not null))
                ids.AddRange(h.MessagesAdded.Select(x => x.Message.Id));
        return (ids, resp.HistoryId?.ToString());
    }

    /// <summary>Current mailbox history id — seed this after the first full fetch.</summary>
    public async Task<string?> CurrentHistoryIdAsync() =>
        (await _svc.Users.GetProfile("me").ExecuteAsync()).HistoryId?.ToString();

    /// <summary>
    /// Well-known folders with live unread counts, in Gmail's sidebar order.
    /// Uses the Labels API so the counts match what gmail.com shows.
    /// </summary>
    public async Task<List<MailFolder>> FoldersAsync()
    {
        var wanted = new (string id, string name)[]
        {
            ("INBOX", "Inbox"), ("STARRED", "Starred"), ("SENT", "Sent"),
            ("DRAFT", "Drafts"), ("SPAM", "Spam"), ("TRASH", "Trash"),
        };

        var result = new List<MailFolder>();
        foreach (var (id, name) in wanted)
        {
            try
            {
                var lbl = await _svc.Users.Labels.Get("me", id).ExecuteAsync();
                result.Add(new MailFolder { Name = name, LabelId = id, Unread = (int)(lbl.MessagesUnread ?? 0) });
            }
            catch
            {
                result.Add(new MailFolder { Name = name, LabelId = id, Unread = 0 });
            }
        }
        return result;
    }

    // ---- conversations (threads) ----------------------------------------

    /// <summary>Lists conversations in a label (newest first), each summarised for the list.</summary>
    public async Task<List<EmailThreadSummary>> ListThreadsAsync(string labelId = "INBOX", int max = 20)
    {
        var req = _svc.Users.Threads.List("me");
        req.LabelIds = new[] { labelId };
        req.MaxResults = max;

        var resp = await req.ExecuteAsync();
        if (resp.Threads is null) return new List<EmailThreadSummary>();

        // One metadata fetch per thread (concurrent) gives subject/participants/counts.
        var summaries = await Task.WhenAll(resp.Threads.Select(t => SummariseThreadAsync(t.Id)));
        return summaries.ToList();
    }

    private async Task<EmailThreadSummary> SummariseThreadAsync(string threadId)
    {
        try
        {
            var req = _svc.Users.Threads.Get("me", threadId);
            req.Format = UsersResource.ThreadsResource.GetRequest.FormatEnum.Metadata;
            req.MetadataHeaders = new[] { "From", "Subject" };
            var t = await req.ExecuteAsync();
            var msgs = t.Messages ?? new List<Message>();

            string HeaderOf(Message m, string name) => m.Payload?.Headers?
                .FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))?.Value ?? "";

            string subject = "";
            var names = new List<string>();
            bool unread = false, starred = false;
            long latest = 0;
            foreach (var m in msgs)
            {
                if (subject.Length == 0) subject = HeaderOf(m, "Subject");
                var disp = DisplayName(HeaderOf(m, "From"));
                if (disp.Length > 0 && !names.Contains(disp)) names.Add(disp);
                unread  |= m.LabelIds?.Contains("UNREAD")  == true;
                starred |= m.LabelIds?.Contains("STARRED") == true;
                latest = Math.Max(latest, m.InternalDate ?? 0);
            }

            return new EmailThreadSummary
            {
                Id            = threadId,
                Subject       = string.IsNullOrWhiteSpace(subject) ? "(no subject)" : subject,
                Participants  = string.Join(", ", names),
                Snippet       = System.Net.WebUtility.HtmlDecode(msgs.LastOrDefault()?.Snippet ?? ""),
                Date          = DateTimeOffset.FromUnixTimeMilliseconds(latest),
                Unread        = unread,
                Starred       = starred,
                Count         = msgs.Count,
                LastMessageId = msgs.LastOrDefault()?.Id ?? "",
            };
        }
        catch
        {
            return new EmailThreadSummary { Id = threadId, Subject = "(conversation)", Count = 0 };
        }
    }

    /// <summary>Full conversation: every message (oldest first) with bodies + attachments.</summary>
    public async Task<List<EmailMessage>> GetThreadAsync(string threadId)
    {
        var req = _svc.Users.Threads.Get("me", threadId);
        req.Format = UsersResource.ThreadsResource.GetRequest.FormatEnum.Full;
        var t = await req.ExecuteAsync();
        return (t.Messages ?? new List<Message>()).Select(Map).ToList();
    }

    private static string DisplayName(string from)
    {
        if (string.IsNullOrWhiteSpace(from)) return "";
        var lt = from.IndexOf('<');
        var name = lt > 0 ? from[..lt].Trim().Trim('"') : from.Trim();
        if (name.Length == 0)
        {
            var start = from.IndexOf('<') + 1;
            var end = from.IndexOf('>');
            if (end > start && start > 0) name = from[start..end];
        }
        return name;
    }

    // ---- mapping helpers -------------------------------------------------

    private static EmailMessage Map(Message msg)
    {
        string Header(string name) => msg.Payload?.Headers?
            .FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))
            ?.Value ?? "";

        var (html, plain, atts, inlines) = ExtractContent(msg.Payload, msg.Id);
        return new EmailMessage
        {
            Id        = msg.Id,
            ThreadId  = msg.ThreadId,
            From      = Header("From"),
            To        = Header("To"),
            Subject   = Header("Subject"),
            Snippet   = System.Net.WebUtility.HtmlDecode(msg.Snippet ?? ""),
            Date      = DateTimeOffset.FromUnixTimeMilliseconds(msg.InternalDate ?? 0),
            IsUnread  = msg.LabelIds?.Contains("UNREAD")  == true,
            IsStarred = msg.LabelIds?.Contains("STARRED") == true,
            HtmlBody  = html,
            PlainBody = plain,
            Rfc822MessageId = Header("Message-Id"),
            Attachments = atts,
            InlineImages = inlines,
        };
    }

    private static (string html, string plain, List<EmailAttachment> atts, List<InlineImage> inlines)
        ExtractContent(MessagePart? part, string messageId)
    {
        string html = "", plain = "";
        var atts = new List<EmailAttachment>();
        var inlines = new List<InlineImage>();

        string PartHeader(MessagePart p, string name) => p.Headers?
            .FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))?.Value ?? "";

        void Walk(MessagePart? p)
        {
            if (p is null) return;

            var cid = PartHeader(p, "Content-Id").Trim().TrimStart('<').TrimEnd('>');
            var isImage = (p.MimeType ?? "").StartsWith("image/", StringComparison.OrdinalIgnoreCase);

            if (cid.Length > 0 && isImage)
            {
                // Embedded image referenced by the body via cid: — inlined for display.
                inlines.Add(new InlineImage
                {
                    ContentId    = cid,
                    MimeType     = p.MimeType ?? "image/png",
                    AttachmentId = p.Body?.AttachmentId ?? "",
                    InlineData   = p.Body?.Data ?? "",
                });
            }
            else if (!string.IsNullOrEmpty(p.Filename))
            {
                atts.Add(new EmailAttachment
                {
                    Filename     = p.Filename,
                    MimeType     = p.MimeType ?? "application/octet-stream",
                    Size         = p.Body?.Size ?? 0,
                    AttachmentId = p.Body?.AttachmentId ?? "",
                    MessageId    = messageId,
                });
            }
            else if (p.MimeType == "text/html"  && p.Body?.Data is not null) html  = Decode(p.Body.Data);
            else if (p.MimeType == "text/plain" && p.Body?.Data is not null) plain = Decode(p.Body.Data);

            if (p.Parts is not null) foreach (var child in p.Parts) Walk(child);
        }

        Walk(part);
        return (html, plain, atts, inlines);
    }

    /// <summary>
    /// Produces the body HTML with cid: images inlined as data URIs so embedded art
    /// renders in the (network-blocked) reader. Fetches image bytes lazily as needed.
    /// </summary>
    public async Task<string> BuildDisplayHtmlAsync(EmailMessage m)
    {
        var html = m.HtmlBody;
        if (string.IsNullOrEmpty(html) || m.InlineImages.Count == 0) return html;

        foreach (var img in m.InlineImages)
        {
            try
            {
                byte[] bytes = !string.IsNullOrEmpty(img.InlineData)
                    ? DecodeBytes(img.InlineData)
                    : !string.IsNullOrEmpty(img.AttachmentId)
                        ? await GetAttachmentAsync(m.Id, img.AttachmentId)
                        : Array.Empty<byte>();
                if (bytes.Length == 0) continue;

                var dataUri = $"data:{img.MimeType};base64,{Convert.ToBase64String(bytes)}";
                html = html.Replace("cid:" + img.ContentId, dataUri);
            }
            catch { /* skip an image that won't load */ }
        }
        return html;
    }

    /// <summary>Downloads the raw bytes of a received attachment.</summary>
    public async Task<byte[]> GetAttachmentAsync(string messageId, string attachmentId)
    {
        var att = await _svc.Users.Messages.Attachments.Get("me", messageId, attachmentId).ExecuteAsync();
        return DecodeBytes(att.Data);
    }

    private static string Decode(string base64Url) => Encoding.UTF8.GetString(DecodeBytes(base64Url));

    private static byte[] DecodeBytes(string base64Url)
    {
        var s = base64Url.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(s);
    }

    public void Dispose() => _svc.Dispose();
}
