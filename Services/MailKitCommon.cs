using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MailKit.Security;
using MimeKit;
using Googlook.Models;

namespace Googlook.Services;

/// <summary>
/// Shared plumbing for the non-Google (MailKit) accounts: outgoing message
/// building + SMTP send, MimeMessage → EmailMessage mapping, and the
/// connection probe used by the "Test &amp; add" button.
/// </summary>
internal static class SmtpMailer
{
    /// <summary>
    /// Builds the outgoing message with MimeKit (which handles header encoding —
    /// no hand-rolled MIME, no CRLF-injection surface for these accounts).
    /// </summary>
    public static MimeMessage Build(ImapAccountConfig cfg, string to, string subject, string body,
                                    bool isHtml, IReadOnlyList<OutgoingAttachment>? attachments,
                                    string? inReplyTo)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(cfg.DisplayName ?? "", cfg.EmailAddress));
        msg.To.AddRange(InternetAddressList.Parse(to));   // throws a clear error on a bad list
        msg.Subject = subject ?? "";

        if (!string.IsNullOrWhiteSpace(inReplyTo))
        {
            var id = inReplyTo.Trim().Trim('<', '>');
            if (id.Length > 0) { msg.InReplyTo = id; msg.References.Add(id); }
        }

        var bb = new BodyBuilder();
        if (isHtml) bb.HtmlBody = body; else bb.TextBody = body;
        if (attachments is not null)
            foreach (var a in attachments)
                bb.Attachments.Add(a.Filename, a.Data, ContentType.Parse(a.MimeType));
        msg.Body = bb.ToMessageBody();
        return msg;
    }

    public static async Task SendAsync(ImapAccountConfig cfg, MimeMessage msg)
    {
        using var smtp = new MailKit.Net.Smtp.SmtpClient();
        await smtp.ConnectAsync(cfg.SmtpHost, cfg.SmtpPort, SecureSocketOptions.Auto);
        await smtp.AuthenticateAsync(cfg.EffectiveUsername, cfg.Password);
        await smtp.SendAsync(msg);
        await smtp.DisconnectAsync(true);
    }
}

/// <summary>Maps a fully-fetched MimeMessage into the app's EmailMessage model.</summary>
internal static class MailMime
{
    /// <summary>The stable part list both mapping and attachment fetch index into.</summary>
    public static List<MimePart> Parts(MimeMessage m) => m.BodyParts.OfType<MimePart>().ToList();

    public static EmailMessage Map(string id, MimeMessage m)
    {
        var em = new EmailMessage
        {
            Id        = id,
            ThreadId  = id,
            From      = m.From.ToString(),
            To        = m.To.ToString(),
            Cc        = m.Cc.ToString(),
            Subject   = m.Subject ?? "",
            Snippet   = "",
            Date      = m.Date,
            HtmlBody  = m.HtmlBody ?? "",
            PlainBody = m.TextBody ?? "",
            Rfc822MessageId = string.IsNullOrEmpty(m.MessageId) ? "" : "<" + m.MessageId + ">",
        };

        var parts = Parts(m);
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            var cid = p.ContentId ?? "";
            var isInlineImage = cid.Length > 0 &&
                                string.Equals(p.ContentType.MediaType, "image", StringComparison.OrdinalIgnoreCase);
            if (isInlineImage)
            {
                em.InlineImages.Add(new InlineImage
                {
                    ContentId    = cid.Trim('<', '>'),
                    MimeType     = p.ContentType.MimeType,
                    AttachmentId = i.ToString(),
                });
            }
            else if (p.IsAttachment)
            {
                em.Attachments.Add(new EmailAttachment
                {
                    Filename     = string.IsNullOrWhiteSpace(p.FileName) ? "attachment" : p.FileName,
                    MimeType     = p.ContentType.MimeType,
                    Size         = TryLength(p),
                    AttachmentId = i.ToString(),
                    MessageId    = id,
                });
            }
        }
        return em;
    }

    public static async Task<byte[]> DecodePartAsync(MimeMessage m, string attachmentId)
    {
        var parts = Parts(m);
        if (!int.TryParse(attachmentId, out var idx) || idx < 0 || idx >= parts.Count)
            return Array.Empty<byte>();
        var content = parts[idx].Content;
        if (content is null) return Array.Empty<byte>();
        using var ms = new MemoryStream();
        await content.DecodeToAsync(ms);
        return ms.ToArray();
    }

    private static long TryLength(MimePart p)
    {
        try { return p.Content?.Stream?.Length ?? 0; } catch { return 0; }
    }
}

/// <summary>Connection test for the add-account dialog: incoming + SMTP auth, nothing else.</summary>
internal static class MailProbe
{
    /// <summary>Returns null on success, otherwise a human-readable failure.</summary>
    public static async Task<string?> TestAsync(ImapAccountConfig cfg)
    {
        try
        {
            if (string.Equals(cfg.Protocol, "pop3", StringComparison.OrdinalIgnoreCase))
            {
                using var pop = new MailKit.Net.Pop3.Pop3Client();
                await pop.ConnectAsync(cfg.IncomingHost, cfg.IncomingPort, SecureSocketOptions.Auto);
                await pop.AuthenticateAsync(cfg.EffectiveUsername, cfg.Password);
                await pop.DisconnectAsync(true);
            }
            else
            {
                using var imap = new MailKit.Net.Imap.ImapClient();
                await imap.ConnectAsync(cfg.IncomingHost, cfg.IncomingPort, SecureSocketOptions.Auto);
                await imap.AuthenticateAsync(cfg.EffectiveUsername, cfg.Password);
                await imap.DisconnectAsync(true);
            }

            using var smtp = new MailKit.Net.Smtp.SmtpClient();
            await smtp.ConnectAsync(cfg.SmtpHost, cfg.SmtpPort, SecureSocketOptions.Auto);
            await smtp.AuthenticateAsync(cfg.EffectiveUsername, cfg.Password);
            await smtp.DisconnectAsync(true);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
