using System.Collections.Generic;
using System.Threading.Tasks;
using Googlook.Models;

namespace Googlook.Services;

/// <summary>
/// The mail operations the UI actually needs, provider-agnostic. GmailClient
/// (REST API) and Imap/Pop3MailClient (MailKit) both implement it, so the shell,
/// view models, poller, and compose flow don't care which kind of account is
/// active. Google-only extras (push, Drive, contacts) stay on GmailClient and are
/// reached by downcasting where a session is known to be Google.
/// </summary>
public interface IMailClient : System.IDisposable
{
    string UserEmail { get; }

    Task<List<MailFolder>> FoldersAsync();
    Task<List<EmailThreadSummary>> ListThreadsAsync(string labelId = "INBOX", int max = 20);
    Task<List<EmailThreadSummary>> SearchThreadsAsync(string query, int max = 20);
    Task<List<EmailMessage>> GetThreadAsync(string threadId);
    /// <summary>Newest-first lightweight list (used for notification peeking).</summary>
    Task<List<EmailMessage>> ListAsync(string labelId = "INBOX", int max = 50);

    Task MarkReadAsync(string id);
    Task SetStarAsync(string id, bool starred);

    Task SendAsync(string to, string subject, string body, bool isHtml = false,
                   IReadOnlyList<OutgoingAttachment>? attachments = null,
                   string? threadId = null, string? inReplyTo = null);

    /// <summary>Body HTML with cid: images inlined as data URIs for the network-blocked reader.</summary>
    Task<string> BuildDisplayHtmlAsync(EmailMessage m);
    Task<byte[]> GetAttachmentAsync(string messageId, string attachmentId);
}
