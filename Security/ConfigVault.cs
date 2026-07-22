using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Googlook.Models;

namespace Googlook.Security;

/// <summary>
/// Encrypts the entire app configuration (including Google OAuth refresh
/// tokens) at rest using AES-256-GCM. The key is derived from a user passcode
/// with PBKDF2-HMAC-SHA256. Nothing sensitive is ever written to disk in the
/// clear. On-disk format: [salt(16)][nonce(12)][tag(16)][ciphertext].
/// </summary>
public sealed class ConfigVault
{
    private const int SaltSize   = 16;
    private const int NonceSize  = 12;       // AES-GCM standard nonce length
    private const int TagSize    = 16;       // 128-bit authentication tag
    private const int KeySize    = 32;       // AES-256
    private const int Iterations = 600_000;  // OWASP 2023 guidance, PBKDF2-SHA256

    private readonly string _path;
    private readonly string _keyPath;         // DPAPI-protected key for passcode-free unlock
    private byte[]? _key;                     // held in memory only while unlocked

    public bool IsUnlocked   => _key is not null;
    public bool Exists       => File.Exists(_path);
    /// <summary>True when a machine-bound (DPAPI) key exists — the app can auto-unlock, no passcode.</summary>
    public bool AutoKeyExists => File.Exists(_keyPath);

    public ConfigVault(string? path = null)
    {
        _path = path ?? DefaultPath();
        _keyPath = Path.Combine(Path.GetDirectoryName(_path)!, "vaultkey.bin");
    }

    public static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Googlook");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "vault.bin");
    }

    /// <summary>First run: choose a passcode and write the initial config.</summary>
    public void Initialize(string passcode, AppConfig seed)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        _key = DeriveKey(passcode, salt);
        WriteInternal(seed, salt);
    }

    /// <summary>Unlock an existing vault. Throws CryptographicException on a wrong passcode.</summary>
    public AppConfig Unlock(string passcode)
    {
        var salt = File.ReadAllBytes(_path).AsSpan(0, SaltSize).ToArray();
        _key = DeriveKey(passcode, salt);
        return DecryptCurrent();   // GCM tag mismatch => wrong passcode
    }

    /// <summary>
    /// Enables passcode-free "stay signed in": generates a random key, protects it with
    /// Windows DPAPI (bound to the current user), and re-encrypts the config with it.
    /// </summary>
    public void EnableAutoUnlock(AppConfig config)
    {
        _key = RandomNumberGenerator.GetBytes(KeySize);
        var protectedKey = ProtectedData.Protect(_key, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_keyPath, protectedKey);
        Save(config);              // writes vault.bin encrypted with the new key
    }

    /// <summary>Unlocks using the DPAPI-protected key — no passcode. Returns empty config if none saved yet.</summary>
    public AppConfig AutoUnlock()
    {
        var protectedKey = File.ReadAllBytes(_keyPath);
        _key = ProtectedData.Unprotect(protectedKey, null, DataProtectionScope.CurrentUser);
        return Exists ? DecryptCurrent() : new AppConfig();
    }

    /// <summary>Removes the machine-bound key (used when switching to passcode-only protection).</summary>
    public void DisableAutoUnlock()
    {
        try { if (File.Exists(_keyPath)) File.Delete(_keyPath); } catch { }
    }

    private AppConfig DecryptCurrent()
    {
        var blob   = File.ReadAllBytes(_path);
        var nonce  = blob.AsSpan(SaltSize, NonceSize).ToArray();
        var tag    = blob.AsSpan(SaltSize + NonceSize, TagSize).ToArray();
        var cipher = blob.AsSpan(SaltSize + NonceSize + TagSize).ToArray();

        var plain = new byte[cipher.Length];
        using (var gcm = new AesGcm(_key!, TagSize))
            gcm.Decrypt(nonce, cipher, tag, plain);

        var json = Encoding.UTF8.GetString(plain);
        CryptographicOperations.ZeroMemory(plain);
        return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
    }

    /// <summary>Persist the current config. Requires the vault to be unlocked.</summary>
    public void Save(AppConfig config)
    {
        if (_key is null) throw new InvalidOperationException("Vault is locked.");
        // Reuse the existing salt so the same passcode keeps working.
        var salt = Exists
            ? File.ReadAllBytes(_path).AsSpan(0, SaltSize).ToArray()
            : RandomNumberGenerator.GetBytes(SaltSize);
        WriteInternal(config, salt);
    }

    /// <summary>Wipe the key from memory. Called by the Lock button.</summary>
    public void Lock()
    {
        if (_key is null) return;
        CryptographicOperations.ZeroMemory(_key);
        _key = null;
    }

    private void WriteInternal(AppConfig config, byte[] salt)
    {
        var json   = JsonSerializer.SerializeToUtf8Bytes(config);
        var nonce  = RandomNumberGenerator.GetBytes(NonceSize);
        var tag    = new byte[TagSize];
        var cipher = new byte[json.Length];
        using (var gcm = new AesGcm(_key!, TagSize))
            gcm.Encrypt(nonce, json, cipher, tag);
        CryptographicOperations.ZeroMemory(json);

        // Write to a temp file then move, so a crash can't corrupt the vault.
        var tmp = _path + ".tmp";
        using (var fs = File.Create(tmp))
        {
            fs.Write(salt);
            fs.Write(nonce);
            fs.Write(tag);
            fs.Write(cipher);
        }
        File.Move(tmp, _path, overwrite: true);
    }

    private static byte[] DeriveKey(string passcode, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passcode), salt, Iterations,
            HashAlgorithmName.SHA256, KeySize);
}
