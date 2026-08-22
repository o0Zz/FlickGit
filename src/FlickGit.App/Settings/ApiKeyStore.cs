using System.Runtime.InteropServices;
using System.Text;
using FlickGit.Ai;
using FlickGit.Logging;

namespace FlickGit.App.Settings;

/// <summary>
/// The API key, in Windows Credential Manager.
///
/// <b>Never in settings.json.</b> <see cref="FlickSettings"/>'s own doc comment says so and has no
/// property to put one in, which is the point — a key in a plain JSON file under
/// <c>%LOCALAPPDATA%</c> is a key in every backup and every screen share of that folder.
///
/// Credential Manager rather than DPAPI. DPAPI would mean a fourth PackageReference against a csproj
/// comment that says "and nothing else", plus a ciphertext file of our own to name, place and
/// version. Credential Manager gives the user an entry they can inspect and delete in Windows' own
/// UI — which matters for a tool that will never print the key back to them.
///
/// In <c>FlickGit.App</c> because it is Windows-specific: <c>FlickGit.Core</c> targets plain
/// <c>net9.0</c> so that the "no UI, no OS" rule is structural. The generators take a
/// <c>Func&lt;string?&gt;</c> instead of this type for the same reason.
/// </summary>
public sealed partial class ApiKeyStore(ILog log)
{
    private const uint GenericCredential = 1;

    /// <summary>
    /// Local machine, not <c>ENTERPRISE</c>. An API key should not roam to another machine because
    /// the user happens to have a domain profile.
    /// </summary>
    private const uint PersistLocalMachine = 2;

    /// <summary>
    /// One target per provider, so switching provider does not throw away the other key.
    ///
    /// The prefix is what a user searching Credential Manager for "FlickGit" will find.
    /// </summary>
    public static string TargetFor(AiProvider provider) => $"FlickGit:{provider.ToString().ToLowerInvariant()}";

    /// <summary>Whether a key is stored, without reading it.</summary>
    public bool Has(AiProvider provider) => Read(provider) is { Length: > 0 };

    /// <summary>
    /// The stored key, or null.
    ///
    /// Read per request rather than cached in a field. .NET cannot zero a <c>string</c>, so the only
    /// thing this code can honestly control is how long one exists — and a cached key would live for
    /// the whole session of a process that stays up for weeks.
    /// </summary>
    public string? Read(AiProvider provider)
    {
        if (provider == AiProvider.Disabled)
            return null;

        nint blob = 0;

        try
        {
            if (!CredReadW(TargetFor(provider), GenericCredential, 0, out blob))
                return null;

            Credential credential = Marshal.PtrToStructure<Credential>(blob);

            return credential.CredentialBlobSize == 0 || credential.CredentialBlob == 0
                ? null
                : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        catch (Exception ex)
        {
            //Never log the key, and there is nothing else here worth a warning: a missing
            //credential is the ordinary case on a fresh install.
            log.Debug($"Reading the {provider} key failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (blob != 0)
                CredFree(blob);
        }
    }

    public bool Write(AiProvider provider, string key)
    {
        if (provider == AiProvider.Disabled || key.Length == 0)
            return false;

        nint blob = Marshal.StringToCoTaskMemUni(key);

        try
        {
            var credential = new Credential
            {
                Type = GenericCredential,
                TargetName = TargetFor(provider),
                CredentialBlobSize = (uint)(key.Length * 2),
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = "FlickGit",
            };

            if (CredWriteW(ref credential, 0))
            {
                log.Info($"Stored the {provider} API key in Windows Credential Manager.");
                return true;
            }

            log.Warn($"Storing the {provider} API key failed (Windows error {Marshal.GetLastWin32Error()}).");
            return false;
        }
        finally
        {
            //Zeroed and freed immediately either way. The key exists in unmanaged memory for the
            //duration of one call and no longer -- the managed string it came from cannot be zeroed
            //at all, so this is the only part of its lifetime that can actually be controlled.
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }
    }

    public bool Clear(AiProvider provider)
    {
        if (provider == AiProvider.Disabled)
            return false;

        if (CredDeleteW(TargetFor(provider), GenericCredential, 0))
        {
            log.Info($"Removed the {provider} API key.");
            return true;
        }

        //1168 is ERROR_NOT_FOUND, which for "clear" is success: there is nothing there.
        return Marshal.GetLastWin32Error() == 1168;
    }

    /// <summary>
    /// <c>CREDENTIALW</c>. Only the fields this code sets or reads are named; the rest are padding
    /// that has to be the right size and nothing more.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string TargetName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Comment;

        public long LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetAlias;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, uint type, uint flags, out nint credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(nint buffer);
}
