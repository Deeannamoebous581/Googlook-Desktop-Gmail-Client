using System;
using System.Collections.Generic;

namespace Googlook.Models;

/// <summary>Everything persisted to the encrypted vault.</summary>
public sealed class AppConfig
{
    public List<Account> Accounts { get; set; } = new();
    /// <summary>Non-Google mail accounts (IMAP or POP3 + SMTP). Passwords live in this
    /// encrypted vault, same as the Google refresh tokens. Capped at 10.</summary>
    public List<ImapAccountConfig> ImapAccounts { get; set; } = new();
    /// <summary>Google OAuth token blobs, keyed by account id (managed by VaultDataStore).</summary>
    public Dictionary<string, string> OAuthTokens { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
    /// <summary>User-supplied Google OAuth client (from Google Cloud Console).</summary>
    public string GoogleClientId { get; set; } = "";
    public string GoogleClientSecret { get; set; } = "";
}

public sealed class AppSettings
{
    public int  PollIntervalSeconds { get; set; } = 60;
    public bool BlockRemoteContent  { get; set; } = true;   // Thunderbird-style pixel blocking
    public bool UsePushWherePossible { get; set; } = false; // requires Cloud Pub/Sub (see README)
    public string Theme { get; set; } = "GmailLight";       // "Dark" enables dark mode

    /// <summary>Show corner desktop notifications for new mail.</summary>
    public bool ShowNotifications { get; set; } = true;
    /// <summary>When true, notifications omit sender/subject and just say new mail arrived.</summary>
    public bool NotificationPrivacy { get; set; } = false;

    /// <summary>Full Pub/Sub topic name for Gmail push, e.g. projects/&lt;proj&gt;/topics/&lt;topic&gt;.</summary>
    public string PubSubTopic { get; set; } = "";
    /// <summary>Full Pub/Sub subscription the client pulls, e.g. projects/&lt;proj&gt;/subscriptions/&lt;sub&gt;.</summary>
    public string PubSubSubscription { get; set; } = "";
}

public sealed class Account
{
    public string  Id           { get; set; } = Guid.NewGuid().ToString("N");
    public string  EmailAddress { get; set; } = "";
    public string  DisplayName  { get; set; } = "";
    /// <summary>Gmail History API cursor for cheap incremental sync.</summary>
    public string? LastHistoryId { get; set; }
    /// <summary>Isolated WebView2 UserDataFolder so 10 profiles never cross-contaminate.</summary>
    public string  ProfileDir   { get; set; } = "";
}

/// <summary>A non-Google mail account: IMAP or POP3 for receiving, SMTP for sending.</summary>
public sealed class ImapAccountConfig
{
    public string Id           { get; set; } = Guid.NewGuid().ToString("N");
    public string EmailAddress { get; set; } = "";
    public string DisplayName  { get; set; } = "";
    /// <summary>"imap" or "pop3".</summary>
    public string Protocol     { get; set; } = "imap";
    /// <summary>Login name; empty means "use the email address".</summary>
    public string Username     { get; set; } = "";
    public string Password     { get; set; } = "";
    /// <summary>Incoming (IMAP/POP3) server. TLS mode is derived from the port.</summary>
    public string IncomingHost { get; set; } = "";
    public int    IncomingPort { get; set; } = 993;
    public string SmtpHost     { get; set; } = "";
    public int    SmtpPort     { get; set; } = 587;

    public string EffectiveUsername => string.IsNullOrWhiteSpace(Username) ? EmailAddress : Username;
}

public enum WellKnownFolder { Inbox, Starred, Snoozed, Sent, Drafts, Spam, Trash, AllMail }

public sealed class MailFolder
{
    public string Name    { get; set; } = "";
    public string LabelId { get; set; } = "";
    public int    Unread  { get; set; }
}

public sealed class EmailMessage
{
    public string          Id        { get; set; } = "";
    public string          ThreadId  { get; set; } = "";
    public string          From      { get; set; } = "";
    public string          To        { get; set; } = "";
    public string          Cc        { get; set; } = "";
    public string          Subject   { get; set; } = "";
    public string          Snippet   { get; set; } = "";
    public DateTimeOffset  Date      { get; set; }
    public bool            IsUnread  { get; set; }
    public bool            IsStarred { get; set; }
    public string          HtmlBody  { get; set; } = "";
    public string          PlainBody { get; set; } = "";
    /// <summary>RFC 822 Message-ID header, used to thread replies (In-Reply-To/References).</summary>
    public string          Rfc822MessageId { get; set; } = "";
    public List<EmailAttachment> Attachments  { get; set; } = new();
    /// <summary>Embedded images referenced by the body via cid: (inlined as data URIs for display).</summary>
    public List<InlineImage>     InlineImages { get; set; } = new();
}

/// <summary>An image embedded in the body and referenced by &lt;img src="cid:..."&gt;.</summary>
public sealed class InlineImage
{
    public string ContentId    { get; set; } = "";  // without the angle brackets
    public string MimeType     { get; set; } = "image/png";
    public string AttachmentId { get; set; } = "";  // fetch bytes if not inline
    public string InlineData    { get; set; } = "";  // base64url bytes when present inline
}

/// <summary>An attachment on a received message (bytes fetched lazily from Gmail).</summary>
public sealed class EmailAttachment
{
    public string Filename     { get; set; } = "";
    public string MimeType     { get; set; } = "application/octet-stream";
    public long   Size         { get; set; }
    public string AttachmentId { get; set; } = "";  // Gmail attachment id
    public string MessageId    { get; set; } = "";  // owning message (needed to fetch)
}

/// <summary>A file to send: name, MIME type, and raw bytes (local disk or Google Drive).</summary>
public sealed class OutgoingAttachment
{
    public string Filename { get; set; } = "";
    public string MimeType { get; set; } = "application/octet-stream";
    public byte[] Data     { get; set; } = Array.Empty<byte>();
    /// <summary>Where it came from, for the compose chip ("Drive" or "File").</summary>
    public string Source   { get; set; } = "File";
}

/// <summary>Lightweight summary of a conversation for the message list.</summary>
public sealed class EmailThreadSummary
{
    public string          Id            { get; set; } = "";
    public string          Subject       { get; set; } = "";
    public string          Participants  { get; set; } = "";
    public string          Snippet       { get; set; } = "";
    public DateTimeOffset  Date          { get; set; }
    public bool            Unread        { get; set; }
    public bool            Starred       { get; set; }
    public int             Count         { get; set; }
    public string          LastMessageId { get; set; } = "";
}
