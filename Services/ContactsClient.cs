using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.PeopleService.v1;
using Google.Apis.PeopleService.v1.Data;
using Google.Apis.Services;

namespace Googlook.Services;

public sealed record ContactEntry(string Name, string Email)
{
    /// <summary>What the autocomplete shows and inserts, e.g. "Ada Lovelace &lt;ada@x.com&gt;".</summary>
    public string Display => string.IsNullOrWhiteSpace(Name) ? Email : $"{Name} <{Email}>";
}

/// <summary>
/// Loads the account's contacts (saved + auto-saved "other" contacts) through the
/// People API to feed compose autocomplete. Read-only; both lookups fail soft so a
/// missing scope just yields no suggestions rather than an error.
/// </summary>
public sealed class ContactsClient : IDisposable
{
    private readonly PeopleServiceService _svc;

    public ContactsClient(UserCredential credential, string appName = "Googlook")
    {
        _svc = new PeopleServiceService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = appName,
        });
    }

    public async Task<List<ContactEntry>> LoadAsync(int max = 500)
    {
        var byEmail = new Dictionary<string, ContactEntry>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var req = _svc.People.Connections.List("people/me");
            req.PersonFields = "names,emailAddresses";
            req.PageSize = Math.Min(max, 1000);
            Collect((await req.ExecuteAsync()).Connections, byEmail);
        }
        catch { /* contacts.readonly not granted — skip */ }

        try
        {
            var req = _svc.OtherContacts.List();
            req.ReadMask = "names,emailAddresses";
            req.PageSize = Math.Min(max, 1000);
            Collect((await req.ExecuteAsync()).OtherContacts, byEmail);
        }
        catch { /* contacts.other.readonly not granted — skip */ }

        return byEmail.Values.OrderBy(c => c.Name).ThenBy(c => c.Email).ToList();
    }

    private static void Collect(IList<Person>? people, Dictionary<string, ContactEntry> byEmail)
    {
        if (people is null) return;
        foreach (var p in people)
        {
            var name = p.Names?.FirstOrDefault()?.DisplayName ?? "";
            if (p.EmailAddresses is null) continue;
            foreach (var e in p.EmailAddresses)
            {
                if (string.IsNullOrWhiteSpace(e.Value)) continue;
                if (!byEmail.ContainsKey(e.Value))
                    byEmail[e.Value] = new ContactEntry(name, e.Value);
            }
        }
    }

    public void Dispose() => _svc.Dispose();
}
