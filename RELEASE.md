# Googlook

**A Gmail-style desktop mail client for Windows.** Up to 10 Gmail accounts and 10
IMAP/POP3 accounts in one native window — with Google service tabs, real-time mail,
and everything encrypted at rest on your PC.

---

## Getting started (60 seconds)

1. Run **`Googlook.exe`** — no installation, no runtime to download. The first
   launch takes a few extra seconds while it unpacks itself.
2. Add an account:
   - **Any IMAP/POP3 provider** (Outlook.com, Yahoo, Fastmail, your own server):
     **⚙ Settings → ＋ Add IMAP/POP3 account**. Type your address — server names
     are pre-filled — then **Test & add**. That's it. *(Tip: most providers require
     an "app password" for IMAP; check their account-security page.)*
   - **Gmail**: requires a one-time, free Google Cloud OAuth client (about 5
     minutes — see `README.md`, "Google Cloud setup"). After that one-time step,
     every Gmail account is just **＋ Add account** → sign in through your browser.
3. You stay signed in between launches. Optional: **⚙ Settings → Security → Add
   passcode lock** to require a passcode on every launch instead.

## What you get

- **Mail** — conversations, reply/reply-all/forward, attachments (local files or
  straight from Google Drive), contact autocomplete, star/unread sync, full
  Gmail-syntax search (`from:amy has:attachment`), desktop notifications with an
  optional privacy mode, light/dark theme.
- **Privacy by default** — email bodies render in a JavaScript-disabled,
  network-blocked view; tracking pixels and remote images are stripped until you
  press "Show images" on a message.
- **Google tabs** — Drive, Calendar, Gemini, Keep, Photos, Docs, YouTube, and a
  Google-account browser, each isolated per account. Plus a built-in **Gemini CLI**
  terminal.
- **Security** — OAuth tokens and IMAP passwords are stored in an AES-256-GCM
  encrypted vault, keyed to your Windows user (DPAPI) or to your passcode
  (PBKDF2, 600k iterations). Nothing sensitive is written to disk in the clear.

## Requirements

- Windows 10/11 (64-bit).
- Microsoft Edge **WebView2 Runtime** for the email reader and browser tabs —
  already present on nearly all Windows 11 machines (free from Microsoft if not).

## Troubleshooting

| Problem | Fix |
| --- | --- |
| Email bodies or tabs are black boxes | You're likely in a VM / over RDP. Email reading is already GPU-free; for the browser tabs set the environment variable `GOOGLOOK_DISABLE_GPU=1` and relaunch. |
| Browser tab stays blank | Install the WebView2 Runtime, or use the **Open in Chrome** button. |
| IMAP sign-in fails | Use an app-specific password (Yahoo/Fastmail/iCloud all require one), and confirm IMAP is enabled in your provider's settings. |
| Something else went wrong | Check `%AppData%\Googlook\googlook.log` and include it when reporting. |

## Fine print

Googlook is an independent open project. It is **not affiliated with or endorsed
by Google**; Gmail, Google Drive, and related marks belong to Google LLC. The app
talks to Google exclusively through Google's official public APIs with credentials
you create yourself. Bundled Roboto font © Google, licensed under Apache 2.0
(license included). This software is provided as-is, without warranty; it has not
had a third-party security audit — review `README.md` before trusting it with
sensitive mail.
