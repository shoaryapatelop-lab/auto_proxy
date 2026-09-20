using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AutoProxy.Core.Abstractions;

namespace AutoProxy.Windows;

/// <summary>
/// Stores proxy credentials in the Windows Credential Manager.
/// Falls back to a DPAPI-protected local file if Credential Manager is unavailable.
/// Passwords are never persisted in plaintext and never appear in logs.
/// </summary>
public class CredentialManager : ICredentialManager
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int CredMaxCredentialBlobSize = 2560;

    private readonly string _fallbackPath;

    public CredentialManager()
    {
        _fallbackPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AutoProxy", "credentials.dpapi");
    }

    public bool SaveCredential(string target, string username, string password)
    {
        if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(username))
            return false;

        // Prefer the Windows Credential Manager. It can reject very large
        // secrets (CRED_MAX_CREDENTIAL_BLOB_SIZE) or fail for other reasons,
        // in which case we fall back to the DPAPI-protected store.
        try
        {
            if (TryWriteCredentialManager(target, username, password))
                return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Credential Manager write failed for '{target}': {ex.Message}");
        }

        return TryWriteDpapi(target, username, password);
    }

    public (string Username, string Password)? ReadCredential(string target)
    {
        if (string.IsNullOrEmpty(target))
            return null;

        var fromCm = TryReadCredentialManager(target);
        if (fromCm is not null)
            return fromCm;

        return TryReadDpapi(target);
    }

    public bool DeleteCredential(string target)
    {
        if (string.IsNullOrEmpty(target))
            return false;

        var removed = false;
        try
        {
            removed |= CredDeleteW(target, CredTypeGeneric, 0);
        }
        catch
        {
            // Reported through the verification read below.
        }

        try
        {
            var store = LoadDpapiStore();
            if (store.Remove(target))
            {
                SaveDpapiStore(store);
                removed = true;
            }
        }
        catch
        {
            // Reported through the verification read below.
        }

        if (removed)
            return true;

        // Nothing was deleted: succeed only if the credential is genuinely gone
        // (deleting a non-existent credential is treated as success).
        return ReadCredential(target) is null;
    }

    // ---------- Credential Manager ----------

    private bool TryWriteCredentialManager(string target, string username, string password)
    {
        var bytes = Encoding.UTF8.GetBytes(password);
        if (bytes.Length > CredMaxCredentialBlobSize)
            return false;

        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);

            var credential = new CREDENTIAL
            {
                Flags = 0,
                Type = CredTypeGeneric,
                TargetName = target,
                Comment = "AutoProxy proxy credential",
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = username,
            };

            return CredWriteW(ref credential, 0);
        }
        finally
        {
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = 0;
            Marshal.FreeHGlobal(blob);
        }
    }

    private (string Username, string Password)? TryReadCredentialManager(string target)
    {
        if (!CredReadW(target, CredTypeGeneric, 0, out var credentialPtr))
            return null;

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                return null;

            var buffer = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, buffer, 0, buffer.Length);
            var password = Encoding.UTF8.GetString(buffer);
            Array.Clear(buffer, 0, buffer.Length);
            return (credential.UserName ?? string.Empty, password);
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    // ---------- DPAPI fallback ----------

    private bool TryWriteDpapi(string target, string username, string password)
    {
        try
        {
            var store = LoadDpapiStore();
            store[target] = Json.Pack(new DpapiEntry { UserName = username, Password = password });
            SaveDpapiStore(store);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"DPAPI credential write failed for '{target}': {ex.Message}");
            return false;
        }
    }

    private (string Username, string Password)? TryReadDpapi(string target)
    {
        var store = LoadDpapiStore();
        if (!store.TryGetValue(target, out var payload))
            return null;

        var entry = Json.Unpack(payload);
        return entry is null ? null : (entry.UserName, entry.Password);
    }

    private Dictionary<string, byte[]> LoadDpapiStore()
    {
        if (!File.Exists(_fallbackPath))
            return new Dictionary<string, byte[]>();

        try
        {
            var encrypted = File.ReadAllBytes(_fallbackPath);
            var decrypted = ProtectedData.Unprotect(
                encrypted, null, DataProtectionScope.CurrentUser);
            return Json.UnpackDictionary(decrypted) ?? new Dictionary<string, byte[]>();
        }
        catch
        {
            return new Dictionary<string, byte[]>();
        }
    }

    private void SaveDpapiStore(Dictionary<string, byte[]> store)
    {
        var jsonBytes = Json.Pack(store);
        var encrypted = ProtectedData.Protect(
            jsonBytes, null, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(_fallbackPath)!);
        File.WriteAllBytes(_fallbackPath, encrypted);
    }

    // ---------- P/Invoke ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, int type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, int type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    // ---------- Tiny JSON helpers (avoid a dedicated dependency) ----------

    private static class Json
    {
        public static byte[] Pack(DpapiEntry entry) =>
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(entry);

        public static DpapiEntry? Unpack(byte[] bytes)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<DpapiEntry>(bytes);
            }
            catch
            {
                return null;
            }
        }

        public static byte[] Pack(Dictionary<string, byte[]> store) =>
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(store);

        public static Dictionary<string, byte[]>? UnpackDictionary(byte[] bytes)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, byte[]>>(bytes);
            }
            catch
            {
                return null;
            }
        }
    }

    private class DpapiEntry
    {
        public string UserName { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }
}