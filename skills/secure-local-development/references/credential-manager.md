# Windows Credential Manager access (P/Invoke sketch)

Minimal, dependency-free wrapper over `advapi32`. **Not compiled here: build and test it (including the canary test) before relying on it.** A vetted NuGet wrapper is an alternative, but each added package widens the supply chain; the API surface below is small.

```csharp
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace PgMonitor.Security;

public static class CredentialStore
{
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;   // this machine, survives reboot, no roaming
    private const int  MaxBlobBytes = 5 * 512;            // CRED_MAX_CREDENTIAL_BLOB_SIZE
    private const int  ErrorNotFound = 1168;              // ERROR_NOT_FOUND

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    public static void Save(string target, string userName, string secret)
    {
        byte[] blob = Encoding.Unicode.GetBytes(secret);
        IntPtr mem = IntPtr.Zero;
        try
        {
            if (blob.Length > MaxBlobBytes)
                throw new ArgumentException("Secret too long for Credential Manager.");

            mem = Marshal.AllocHGlobal(blob.Length);
            Marshal.Copy(blob, 0, mem, blob.Length);

            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                UserName = userName,
                CredentialBlob = mem,
                CredentialBlobSize = (uint)blob.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE
            };

            if (!CredWrite(ref cred, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            if (mem != IntPtr.Zero)
            {
                Marshal.Copy(new byte[blob.Length], 0, mem, blob.Length);   // wipe native copy
                Marshal.FreeHGlobal(mem);
            }
            Array.Clear(blob);
        }
    }

    /// Returns null when absent. The returned string cannot be wiped: use it immediately, do not store it.
    public static (string UserName, string Secret)? Read(string target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out IntPtr ptr))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ErrorNotFound) return null;
            throw new Win32Exception(err);                 // message carries no secret
        }

        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            byte[] blob = new byte[c.CredentialBlobSize];
            Marshal.Copy(c.CredentialBlob, blob, 0, blob.Length);
            try { return (c.UserName ?? "", Encoding.Unicode.GetString(blob)); }
            finally { Array.Clear(blob); }
        }
        finally { CredFree(ptr); }
    }

    public static bool Delete(string target)
    {
        if (CredDelete(target, CRED_TYPE_GENERIC, 0)) return true;
        int err = Marshal.GetLastWin32Error();
        if (err == ErrorNotFound) return false;
        throw new Win32Exception(err);
    }
}
```

## Usage rules

- Build target names with an invariant formatter: `$"PgMonitor/Postgres/{host.ToLowerInvariant()}:{port}/{user}"` (Turkish-locale safe).
- Call `Read` inside the connect routine and pass the result straight to `NpgsqlDataSourceBuilder`; do not keep it in a field or log the tuple.
- Wrap `Win32Exception` before surfacing: UI shows "Credential could not be read/saved"; the log gets only the Win32 error code.
- SMTP uses the same class with a different target; call MailKit `Authenticate(user, secret)` right after `Read`.
- Test seam: `ICredentialStore` with this class as the Windows implementation and an in-memory fake for tests. Tests must not touch the real Credential Manager except an explicitly opt-in, uniquely named, self-deleting entry.
- `Win32 P/Invoke` here uses `DllImport` because `CREDENTIAL` contains strings (not blittable); `LibraryImport` would need custom marshalling.
