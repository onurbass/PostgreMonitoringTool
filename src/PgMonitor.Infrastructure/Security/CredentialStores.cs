using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace PgMonitor.Infrastructure.Security;

/// <summary>Where secrets live. Implementations never log or expose the secret.</summary>
public interface ICredentialStore
{
    string? Read(string target);

    void Write(string target, string userName, string secret);

    bool Delete(string target);
}

/// <summary>Credential Manager target names. They contain no secret, only what the secret is for.</summary>
public static class CredentialTargets
{
    public static string Postgres(string user, string host, int port) =>
        string.Create(CultureInfo.InvariantCulture, $"PgMonitor:postgres:{user}@{host}:{port}");

    public static string Smtp(string user, string host) =>
        string.Create(CultureInfo.InvariantCulture, $"PgMonitor:smtp:{user}@{host}");
}

/// <summary>Session-only secrets ("do not save" option) and tests. Gone when the app exits.</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.Ordinal);

    public string? Read(string target) => _secrets.GetValueOrDefault(target);

    public void Write(string target, string userName, string secret) => _secrets[target] = secret;

    public bool Delete(string target) => _secrets.TryRemove(target, out _);
}

/// <summary>Session secrets first, then the saved ones.</summary>
public sealed class LayeredCredentialStore(ICredentialStore session, ICredentialStore saved) : ICredentialStore
{
    public ICredentialStore Session { get; } = session;
    public ICredentialStore Saved { get; } = saved;

    public string? Read(string target) => Session.Read(target) ?? Saved.Read(target);

    public void Write(string target, string userName, string secret) => Saved.Write(target, userName, secret);

    public bool Delete(string target) => Session.Delete(target) | Saved.Delete(target);
}

/// <summary>
/// Windows Credential Manager (generic credentials, this user on this machine). The secret is protected by Windows
/// with the user's logon; it is never written to any file of this application.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore : ICredentialStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public string? Read(string target)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        if (!NativeMethods.CredRead(target, CredTypeGeneric, 0, out var ptr))
        {
            var error = Marshal.GetLastWin32Error();
            return error == ErrorNotFound ? null : throw new InvalidOperationException($"Credential Manager read failed (error {error}).");
        }
        try
        {
            var cred = Marshal.PtrToStructure<NativeMethods.Credential>(ptr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0)
            {
                return string.Empty;
            }
            return Marshal.PtrToStringUni(cred.CredentialBlob, cred.CredentialBlobSize / 2);
        }
        finally
        {
            NativeMethods.CredFree(ptr);
        }
    }

    public void Write(string target, string userName, string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        ArgumentNullException.ThrowIfNull(userName);
        ArgumentNullException.ThrowIfNull(secret);

        var bytes = Encoding.Unicode.GetByteCount(secret);
        var blob = Marshal.StringToCoTaskMemUni(secret);
        var targetPtr = Marshal.StringToCoTaskMemUni(target);
        var userPtr = Marshal.StringToCoTaskMemUni(userName);
        try
        {
            var cred = new NativeMethods.Credential
            {
                Type = CredTypeGeneric,
                TargetName = targetPtr,
                CredentialBlobSize = bytes,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = userPtr,
            };
            if (!NativeMethods.CredWrite(ref cred, 0))
            {
                throw new InvalidOperationException($"Credential Manager write failed (error {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            // Overwrite the unmanaged copy of the secret before freeing it.
            Marshal.Copy(new byte[bytes], 0, blob, bytes);
            Marshal.FreeCoTaskMem(blob);
            Marshal.FreeCoTaskMem(targetPtr);
            Marshal.FreeCoTaskMem(userPtr);
        }
    }

    public bool Delete(string target)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        if (NativeMethods.CredDelete(target, CredTypeGeneric, 0))
        {
            return true;
        }
        var error = Marshal.GetLastWin32Error();
        return error == ErrorNotFound ? false : throw new InvalidOperationException($"Credential Manager delete failed (error {error}).");
    }

#pragma warning disable SYSLIB1054 // DllImport kept for readability; no marshalling of managed strings in the struct.
    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Credential
        {
            public int Flags;
            public int Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredWrite(ref Credential credential, int flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredDelete(string target, int type, int flags);

        [DllImport("advapi32.dll", EntryPoint = "CredFree")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern void CredFree(IntPtr buffer);
    }
#pragma warning restore SYSLIB1054
}
