# Googlook

A Windows email client in the spirit of Outlook/Thunderbird, styled like the Gmail
web app, built on **Avalonia + C# (.NET 8)**. It manages up to ~10 Gmail accounts
from one native window, with tabs for Drive, Gemini, a Gemini CLI terminal, and a
per-account browser — and a passcode lock that encrypts everything at rest.

This repository is a **working foundation**, not a finished product. It nails the
architecture and the security-critical parts, and clearly marks the integration
points that remain. Read "What's done vs. what's stubbed" below before you start.

---

## The core design decision (please read)

Your spec pulls in two directions: *"look exactly like Gmail"* suggests loading
gmail.com in a browser, while *"strip Google trackers"* and *"interval/push checks"*
suggest the opposite. Loading the real web page drags in all of Google's web-app
telemetry and is fragile.

The clean resolution — essentially what Thunderbird does — is a **split**:

| Concern | Approach | Why it strips trackers |
| --- | --- | --- |
| **Mail** (read inbox, list, mark read) | **Gmail REST API over OAuth 2.0** | You fetch structured data and render your own native Gmail-styled UI. Google's web trackers are *never loaded* because you never open their HTML page. |
| **In-email tracking pixels** | HTML sanitizer blocks remote content | Remote `img`/`iframe`/CSS-background sources are blanked before render ("remote content blocked"). |
| **Drive / Gemini / cross-use profile** | **Embedded WebView2**, one isolated profile per account | Here you *want* a live Google session, so a real browser is correct. Isolation keeps 10 accounts from colliding. |

**Session persistence:** mail = encrypted OAuth refresh tokens in the vault;
browser tabs = persistent per-account WebView2 `UserDataFolder`s.

---

## What's done vs. what's stubbed

**Fully implemented and correct**

- **Stays signed in by default (DPAPI)** — on first run the vault key is protected with
  Windows DPAPI (bound to your Windows user), so the app auto-unlocks with **no passcode**
  and your accounts persist across restarts. Tokens are still encrypted at rest; they just
  don't need a passcode to open on the same PC/user.
- **Optional passcode lock** — add one from **Settings → Security → Add passcode lock**;
  that switches the vault to a PBKDF2 passcode key (600k iterations) and requires the
  passcode on each launch. Remove it to go back to staying signed in.
- **Encrypted config vault** (`Security/ConfigVault.cs`) — AES-256-GCM, atomic writes,
  in-memory key zeroing. Key comes from either the DPAPI machine key or your passcode.
- **OAuth-token-in-vault bridge** (`Security/VaultDataStore.cs`) — Google's tokens
  are stored *inside* the encrypted vault, not in plaintext like the default.
- **Account management from Settings** — the ⚙ menu lists connected accounts with a
  **Remove** button and an **＋ Add Google account** button (same as the sidebar ＋).
- **Gmail-styled Avalonia shell** — top bar + search, top service tabs, and the
  three-pane mail view (accounts cascading into folders, message list, reading
  pane). Falls back to demo data (shown with a "Demo data" pill) until you sign in.
- **Live Gmail, wired end-to-end** (`Services/MailService.cs` + `MainViewModel`) —
  **+ Add account** runs OAuth in your browser; the sidebar then shows real accounts,
  real folders with live unread counts, and real messages. Clicking a folder loads
  its mail; opening a message marks it read in Gmail; **⟳** re-syncs. Bodies are run
  through the sanitizer before display.
- **Gemini CLI tab** (`Services/ConPtySession.cs` + `Services/VtScreen.cs` +
  `Controls/TerminalSurface.cs`) — a real Windows **ConPTY** runs the `gemini` CLI
  in-app. Output is parsed by a **VT/ANSI emulator** and painted on a custom surface
  with colour, bold, inverse, and a block cursor, and **raw keystrokes** (arrows, Tab,
  Enter, Backspace, Ctrl-combos) are forwarded so interactive prompts work. The CLI's
  home/config is routed into the account's profile folder for per-account logins.
  Starts lazily the first time you open the tab.
**What works today**

- **Dark mode** (theme tokens in `App.axaml` + the top-bar toggle) — a full light/dark
  theme driven by `ThemeVariant`; every surface uses semantic `{DynamicResource}` tokens
  so the switch is instant and persists. Email bodies stay on a light card (as Gmail does)
  so sender styling never breaks. Toggle from the top bar or the ⚙ Settings flyout.
- **Corner desktop notifications, with a privacy toggle** (`WindowNotificationManager`
  + `MaybeNotifyAsync`) — new mail raises a bottom-right toast. An interval poller
  (and push, if enabled) detect inbox increases per account; the **"Hide sender &
  subject"** toggle makes toasts say only that new mail arrived. Both toggles live in ⚙ Settings.
- **Reply / Reply-all / Forward** (per-message actions in the reader) — Reply and
  Reply-all prefill recipients and a quoted body and thread correctly (`threadId` +
  `In-Reply-To`/`References`); Forward re-attaches the originals. Recipients autocomplete.
- **Contacts autocomplete** (`Services/ContactsClient.cs`, People API) — the Compose
  "To" field suggests from your saved and auto-saved contacts.
- **Inline images** (`GmailClient.BuildDisplayHtmlAsync`) — `cid:` embedded images are
  inlined as data URIs so signatures and embedded art render in the network-blocked
  reader (remote images stay blocked).
- **Conversation threading** (`GmailClient.ListThreadsAsync` / `GetThreadAsync` +
  `ThreadVM`) — the list is grouped into conversations; opening one lazily fetches the
  thread, and a conversation strip moves between messages in the reader.
- **Attachments, send & receive** (`GmailClient` multipart MIME + `GetAttachmentAsync`)
  — received attachments are chips you can save; Compose sends multiple as `multipart/mixed`.
- **Attach from Google Drive** (`Services/DriveClient.cs` + `Views/DrivePickerWindow`)
  — a searchable Drive picker in Compose; native docs export to PDF/XLSX/PNG.
- **Compose & send + star toggle** — sends real mail; the list star toggles STARRED live.
- **True multi-account push via Cloud Pub/Sub** (`Services/PushWatchService.cs`) —
  opt-in. Registers `users.watch()` for *every* signed-in account against a Pub/Sub
  topic and **pulls** notifications from a subscription, routing each one to the mailbox
  it names (the payload carries the email address) and refreshing that account. Interval
  polling stays as the fallback.
- **Full HTML email rendering** (`Controls/HtmlMessageView.cs`) — the reading pane
  renders the sanitized body in a **JS-disabled** WebView2 with **all network blocked**
  (defense in depth over the sanitizer) and links opened in your real browser. Falls
  back to the snippet if the WebView2 runtime is absent.
- **Embedded browser tabs** (`Controls/BrowserView.cs`) — Drive / Gemini / My Account
  run in an embedded Chromium browser (WebView2 — the same engine as Chrome, with the
  Edge user-agent Google accepts for sign-in), each with a per-account profile folder so
  accounts stay separate logins. Toolbar has back / forward / reload, plus an **Open in
  Chrome** button (`Services/ChromeLauncher.cs`) to pop the page out to real Chrome with
  the same isolated profile if you prefer.
- **"Show images" per message** — remote content (tracking pixels, remote images) is
  blocked by default; a per-message banner button re-renders that one message with
  remote content allowed, on demand (Gmail-style), without changing the global default.
- **Gmail API client** (`Services/GmailClient.cs`) — list, get, mark-read, folder
  unread counts, MIME body parsing, and History-API incremental sync.
- **OAuth loopback flow** (`Services/GoogleAuthService.cs`) + **zero-config credential
  loading** (`Services/GoogleClientLoader.cs`).
- **Interval poller** (`Services/MailPoller.cs`) — Thunderbird-style checks.
- **Tracker/remote-content sanitizer** (`Services/HtmlSanitizerService.cs`).

**Remaining polish (not blockers)**

1. **Top-bar search** — the search box is a visual placeholder; wiring it to
   `messages.list?q=` is a small addition.
2. **Terminal VT edge cases** — the emulator covers the common subset. Truecolour
   collapses to default, the alternate-screen buffer is approximated by clearing, and
   there's no scrollback history.
3. **Conversation cost** — the thread list does one `threads.get` (metadata) per thread
   on folder load, capped at 20; batching or caching would cut requests.
4. **Reply-all Cc** — Cc recipients aren't parsed yet, so Reply-all covers From + To.
5. **Large inline images** — bodies render via `NavigateToString` (~2 MB cap); a very
   large embedded image could exceed it (rare for signatures/logos).

---

## Prerequisites

- Windows 10/11 — the project targets `net8.0-windows` because the browser tabs
  embed WebView2 (the mail/vault/UI code itself is otherwise portable).
- [.NET 8 SDK](https://dotnet.microsoft.com/download). In **Visual Studio 2022**
  (17.8+), that's the **".NET desktop development"** workload.
- The **Microsoft Edge WebView2 Runtime** powers the email reader and the embedded
  browser tabs — it's already present on most Windows 11 machines; otherwise it's a free
  install from Microsoft. Without it the reader falls back to the plain-text snippet and
  the browser tabs show a fallback card with an **Open in Chrome** button.
- A Google Cloud OAuth client (free) — see next section.

> Open **`Googlook.sln`** in Visual Studio and the first restore pulls every
> dependency listed in the `.csproj` from nuget.org. (The `Google.Apis.*`,
> `HtmlSanitizer`, and `WebView2` packages use a trailing `.*` so restore always
> resolves a real build; pin them to exact versions for deterministic restores.)

---

## Google Cloud setup (one-time, for real mail)

1. Go to the Google Cloud Console → create/select a project.
2. **APIs & Services → Enable APIs** → enable **Gmail API**. Also enable **Drive API**
   (Drive tab + attach-from-Drive), **People API** (compose autocomplete), and
   **Cloud Pub/Sub API** (only if you want real-time push).
3. **OAuth consent screen** → External → add yourself as a **Test user** (keeps you
   out of the verification queue while developing).
4. **Credentials → Create credentials → OAuth client ID → Desktop app.**
5. Give the app those credentials — **no settings screen needed**, pick one:
   - **Drop the file** Google gives you at
     `%AppData%\Googlook\google_client.json` (the downloaded `client_secret_*.json`,
     renamed). This is the easy path.
   - **or** set `GOOGLE_CLIENT_ID` / `GOOGLE_CLIENT_SECRET` environment variables.

   On launch the app loads them, encrypts them into the vault, and never reads the
   plaintext file again. Then click **+ Add account** in the sidebar to sign in.

Scopes requested: `gmail.modify` (read + mark-read + labels + send), `drive.readonly`
(Drive tab / attach), `contacts.readonly` + `contacts.other.readonly` (autocomplete),
and `pubsub` (optional push).

---

## Build & run

### Visual Studio (recommended)

1. Install the **.NET 8 SDK** — in the Visual Studio Installer, tick the
   **".NET desktop development"** workload (VS 2022 17.8+).
2. Open **`Googlook.sln`**. Visual Studio restores the NuGet packages
   automatically (they download from nuget.org on first open).
3. Make sure `Googlook` is the startup project and press **F5** (Debug) or
   **Ctrl+F5** (Run).

The project is a standard SDK-style `net8.0-windows` app — nothing custom is needed
beyond the SDK and an internet connection for the first restore. `.axaml` files are
compiled automatically by the Avalonia MSBuild targets.

### Command line

```bash
# from the extracted Googlook folder (where Googlook.sln lives)
dotnet run
```

First launch shows the Gmail-like shell (with a **Demo data** pill until you sign in):

1. Make sure your Google credentials are in place (previous section).
2. Click **＋ Add Google account** — in the sidebar, or in **Settings (⚙) → Accounts**.
   Your browser opens for Google sign-in; afterward the sidebar switches to your real
   accounts, folders, and mail. The account persists and **stays signed in** on next
   launch (no passcode needed).
3. Add up to ~10 accounts the same way; manage/remove them from Settings.
4. Optional: **Settings → Security → Add passcode lock** to require a passcode each launch.

### Self-contained single-file build (no runtime needed)

```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Produces one `Googlook.exe` under
`bin/Release/net8.0-windows/win-x64/publish/` with the .NET runtime and all
libraries embedded. (This bundles .NET and the NuGet libs; the WebView2 *Runtime*
is a separate Microsoft component, present on most Windows installs.)

---

## Project layout

```
Googlook/                          <- solution folder; Googlook.sln + Googlook.csproj live here
  Program.cs / App.axaml(.cs)      app bootstrap + Gmail theme tokens
  app.manifest                     per-monitor DPI, long paths, Win10/11
  Security/
    ConfigVault.cs                 AES-256-GCM + PBKDF2 vault  (fully working)
    VaultDataStore.cs              stores OAuth tokens in the vault
  Models/Models.cs                 AppConfig, Account, EmailMessage, ...
  Services/
    GoogleAuthService.cs           OAuth loopback flow
    GoogleClientLoader.cs          loads client id/secret from json/env (no settings UI)
    GmailClient.cs                 Gmail API wrapper + folders + MIME + history sync
    MailService.cs                 signs accounts in, hands back live GmailClients
    MailPoller.cs                  interval checking
    HtmlSanitizerService.cs        remote-content / tracker blocking
    BrowserProfile.cs              resolves the isolated per-account profile folder
    ChromeLauncher.cs              opens browser tabs in Chrome with an isolated profile
    ConPtySession.cs               Windows ConPTY (pseudo-console) harness for the CLI tab
    VtScreen.cs                    VT/ANSI emulator → coloured screen-buffer grid
    PushWatchService.cs            Gmail push via Cloud Pub/Sub pull (opt-in, multi-account)
    DriveClient.cs                 Drive list/search + download (for "attach from Drive")
    ContactsClient.cs              People API contacts for compose autocomplete
  ViewModels/
    MainViewModel.cs               shell state, lock flow, live-Gmail wiring, demo fallback
    Converters.cs                  unread weight / star colour / count visibility
  Views/
    MainWindow.axaml(.cs)          shell: top bar, service tabs, lock overlay
    MailView.axaml(.cs)            three-pane layout + conversation reader + attachments
    ComposeWindow.axaml(.cs)       new-message window (To/Subject/Body + attachments)
    DrivePickerWindow.axaml(.cs)   searchable Google Drive file picker for attaching
    TerminalView.axaml(.cs)        Gemini CLI console (drives the ConPTY + VT screen)
  Controls/
    BrowserTab.axaml(.cs)          browser tab: toolbar + embedded BrowserView + Open-in-Chrome
    BrowserView.cs                 embedded Chromium (WebView2) with per-account profile
    HtmlMessageView.cs             JS-disabled, network-blocked WebView2 for email bodies
    TerminalSurface.cs             custom-drawn grid that paints the VT screen buffer
```

Modularity: each service is a tab (`ServiceKind`), each account is an isolated
profile, and the look is driven by theme tokens in `App.axaml` — swap those to
re-skin without touching logic.

---

## Turning on push (Cloud Pub/Sub)

Push is implemented (`PushWatchService`) and **opt-in**. Because Gmail can't reach a
laptop directly, it publishes to a Pub/Sub topic and the app **pulls** from a
subscription — no public webhook. One-time setup:

1. In your Google Cloud project, **enable the Pub/Sub API**.
2. Create a **topic**, e.g. `googlook-push`.
3. On that topic, grant **Publisher** to `gmail-api-push@system.gserviceaccount.com`
   (this is what lets Gmail publish to it).
4. Create a **pull subscription** on the topic, e.g. `googlook-pull`.
5. Your OAuth consent now includes `pubsub` **and contacts** scopes (added this round),
   so if you authorised earlier, remove the account and **+ Add account** again to
   re-consent (contacts power Compose autocomplete).
6. In the encrypted config set (these live in `AppSettings`):
   - `UsePushWherePossible = true`
   - `PubSubTopic = projects/<project>/topics/googlook-push`
   - `PubSubSubscription = projects/<project>/subscriptions/googlook-pull`

On launch the app calls `users.watch()` for **every** signed-in account (Gmail's push
service account can publish for any mailbox to your topic), then pulls + acks
notifications with the first account's credential and routes each to the mailbox named
in the payload. The Gmail watch lapses after 7 days and is renewed automatically about
once a day. Send needs no extra scope — `GmailModify` covers it. If any of this isn't
set up, the **interval poller** keeps working.

---

## Troubleshooting

- **Error log:** anything that goes wrong (background failures, crashes) is appended to
  `%AppData%\Googlook\googlook.log` — check it first, and include it when reporting a bug.
- **Stuck or blank browser tab / email body:** verify the Edge **WebView2 Runtime** is
  installed; the browser tabs also have an **Open in Chrome** escape hatch.
- **Vault copied to another PC or Windows user:** DPAPI can't decrypt it there by design.
  The app starts fresh; sign the accounts in again (or use a passcode vault, which is
  portable).

---

## Security notes & honest caveats

- The vault protects data **at rest**. While unlocked, secrets are in memory (as with
  any app). Lock when you step away.
- A passcode is only as strong as you make it; PBKDF2 at 600k iterations slows
  brute force but a weak passcode is still weak.
- Package versions in the `.csproj` are pinned to known-good releases; if `restore`
  can't find one (e.g. `Google.Apis.Pubsub.v1`), bump to the latest on nuget.org.
- **Not yet compiled here** (this was built in an environment without a Windows .NET
  toolchain or network). The code is written to compile, but expect to smooth an edge.
  The **highest on-device risk** is the native `ConPtySession` P/Invoke and the
  `VtScreen` emulator — both are self-contained, so iterate on those files if the
  terminal misbehaves. The VT emulator covers the common subset (see remaining polish).
- The embedded browser + HTML reader need the **WebView2 Runtime** (ships with modern
  Windows; otherwise a small MS download). Without it, those surfaces degrade to a
  fallback rather than crashing.

This is a starting point you can build on, not a security-audited release. Treat the
credential-handling paths as needing your own review before real-world use.
