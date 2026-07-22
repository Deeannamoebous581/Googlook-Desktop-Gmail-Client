using System;
using System.Text.Json;
using System.Threading.Tasks;
using Google.Apis.Util.Store;
using Googlook.Models;

namespace Googlook.Security;

/// <summary>
/// Bridges Google's OAuth token storage into our encrypted vault. The default
/// FileDataStore writes tokens to %AppData%\...\credentials in the clear; this
/// keeps every refresh token inside the AES-GCM-encrypted AppConfig instead.
/// </summary>
public sealed class VaultDataStore : IDataStore
{
    private readonly AppConfig _config;
    private readonly Action _persist;   // re-encrypts + writes the vault

    public VaultDataStore(AppConfig config, Action persist)
    {
        _config = config;
        _persist = persist;
    }

    public Task StoreAsync<T>(string key, T value)
    {
        _config.OAuthTokens[key] = JsonSerializer.Serialize(value);
        _persist();
        return Task.CompletedTask;
    }

    public Task DeleteAsync<T>(string key)
    {
        _config.OAuthTokens.Remove(key);
        _persist();
        return Task.CompletedTask;
    }

    public Task<T> GetAsync<T>(string key)
    {
        try
        {
            return Task.FromResult(_config.OAuthTokens.TryGetValue(key, out var json)
                ? JsonSerializer.Deserialize<T>(json)!
                : default!);
        }
        catch
        {
            // A corrupt entry reads as "no stored token" — OAuth just re-prompts,
            // instead of one bad record breaking every account restore.
            return Task.FromResult<T>(default!);
        }
    }

    public Task ClearAsync()
    {
        _config.OAuthTokens.Clear();
        _persist();
        return Task.CompletedTask;
    }
}
