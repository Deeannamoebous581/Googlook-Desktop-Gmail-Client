using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Gmail.v1;
using Google.Apis.Util.Store;

namespace Googlook.Services;

/// <summary>
/// Google OAuth 2.0 for a desktop app using the loopback flow. Google.Apis.Auth
/// spins up http://127.0.0.1:{random-port}, opens the system browser, receives
/// the auth code, and exchanges it for tokens — no deprecated OOB, no secrets
/// in the URL bar. Tokens land in the supplied IDataStore (our encrypted vault).
/// </summary>
public sealed class GoogleAuthService
{
    private static readonly string[] Scopes =
    {
        GmailService.Scope.GmailModify,     // read + mark-read + label changes + send
        DriveService.Scope.DriveReadonly,   // Drive tab / metadata / attach-from-Drive
        "https://www.googleapis.com/auth/pubsub",                 // pull Gmail push (optional)
        "https://www.googleapis.com/auth/contacts.readonly",      // compose autocomplete
        "https://www.googleapis.com/auth/contacts.other.readonly",// auto-saved contacts
    };

    private readonly ClientSecrets _secrets;
    private readonly IDataStore _store;

    public GoogleAuthService(string clientId, string clientSecret, IDataStore store)
    {
        _secrets = new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret };
        _store = store;
    }

    /// <summary>
    /// Interactive sign-in for one account. <paramref name="userId"/> keys the
    /// stored token, so up to 10 accounts each keep an independent refresh token
    /// and stay logged in across restarts.
    /// </summary>
    public Task<UserCredential> AuthorizeAsync(string userId, CancellationToken ct = default) =>
        GoogleWebAuthorizationBroker.AuthorizeAsync(_secrets, Scopes, userId, ct, _store);
}
