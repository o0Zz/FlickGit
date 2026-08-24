using System.Runtime.InteropServices;
using FlickGit.Ai;
using FlickGit.Logging;

namespace FlickGit.App.Settings;

/// <summary>
/// Every secret FlickGit holds, in Windows Credential Manager.
///
/// <b>Never in settings.json.</b> <see cref="FlickSettings"/>'s own doc comment says so and has no
/// property to put one in, which is the point — a secret in a plain JSON file under
/// <c>%LOCALAPPDATA%</c> is a secret in every backup and every screen share of that folder.
///
/// Credential Manager rather than DPAPI. DPAPI would mean a fourth PackageReference against a csproj
/// comment that says "and nothing else", plus a ciphertext file of our own to name, place and
/// version. Credential Manager gives the user entries they can inspect and delete in Windows' own
/// UI — which matters for a tool that will never print a secret back to them.
///
/// <b>It keys on a target string rather than on an AI provider.</b> It was <c>ApiKeyStore</c> and
/// took an <see cref="AiProvider"/>, which meant the pull-request feature's forge tokens had nowhere
/// to go but a second copy of these four P/Invokes. What varies between an API key and a forge token
/// is only the name it is filed under, so that became the parameter and the two naming rules became
/// the statics below — a static that merely names a location, which Hard Requirement 3 allows.
///
/// In <c>FlickGit.App</c> because it is Windows-specific: <c>FlickGit.Core</c> targets plain
/// <c>net9.0</c> so that the "no UI, no OS" rule is structural. Callers in Core take a
/// <c>Func&lt;string?&gt;</c> instead of this type for the same reason.
/// </summary>
public sealed partial class CredentialStore(ILog log)
{
    private const uint GenericCredential = 1;

    /// <summary>
    /// Local machine, not <c>ENTERPRISE</c>. An API key should not roam to another machine because
    /// the user happens to have a domain profile.
    /// </summary>
    private const uint PersistLocalMachine = 2;

    /// <summary>
    /// One target per AI provider, so switching provider does not throw away the other key.
    ///
    /// The <c>FlickGit</c> prefix is what a user searching Credential Manager for this tool will
    /// find, which is the whole reason to have a naming convention at all.
    /// </summary>
    public static string AiTarget(AiProvider provider) => $"FlickGit:{provider.ToString().ToLowerInvariant()}";

    /// <summary>
    /// One target per forge <b>host</b>, not per service and not per repository.
    ///
    /// Per host because that is what a credential is actually scoped to: one token opens pull
    /// requests on every repository on <c>github.com</c>, and a company with both
    /// <c>dev.azure.com</c> and an internal GitLab needs two. Per repository would ask the same
    /// question once per clone; per service would break the moment a second instance appeared.
    ///
    /// Lower-cased, because a host name is case-insensitive and Credential Manager's target is not —
    /// <c>GitHub.com</c> typed into a remote once would otherwise file a second, invisible token.
    /// </summary>
    public static string ForgeTarget(string host) => $"FlickGit:forge:{host.ToLowerInvariant()}";

    /// <summary>Whether something is stored under <paramref name="target"/>, without reading it.</summary>
    public bool Has(string target) => Read(target) is { Length: > 0 };

    /// <summary>
    /// The stored secret, or null.
    ///
    /// Read per request rather than cached in a field. .NET cannot zero a <c>string</c>, so the only
    /// thing this code can honestly control is how long one exists — and a cached secret would live
    /// for the whole session of a process that stays up for weeks.
    /// </summary>
    public string? Read(string target)
    {
        if (target.Length == 0)
            return null;

        nint blob = 0;

        try
        {
            if (!CredReadW(target, GenericCredential, 0, out blob))
                return null;

            Credential credential = Marshal.PtrToStructure<Credential>(blob);

            return credential.CredentialBlobSize == 0 || credential.CredentialBlob == 0
                ? null
                : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        catch (Exception ex)
        {
            //Never log the secret, and there is nothing else here worth a warning: a missing
            //credential is the ordinary case on a fresh install.
            log.Debug($"Reading {target} failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (blob != 0)
                CredFree(blob);
        }
    }

    public bool Write(string target, string secret)
    {
        if (target.Length == 0 || secret.Length == 0)
            return false;

        nint blob = Marshal.StringToCoTaskMemUni(secret);

        try
        {
            var credential = new Credential
            {
                Type = GenericCredential,
                TargetName = target,
                CredentialBlobSize = (uint)(secret.Length * 2),
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = "FlickGit",
            };

            if (CredWriteW(ref credential, 0))
            {
                log.Info($"Stored {target} in Windows Credential Manager.");
                return true;
            }

            log.Warn($"Storing {target} failed (Windows error {Marshal.GetLastWin32Error()}).");
            return false;
        }
        finally
        {
            //Zeroed and freed immediately either way. The secret exists in unmanaged memory for the
            //duration of one call and no longer -- the managed string it came from cannot be zeroed
            //at all, so this is the only part of its lifetime that can actually be controlled.
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }
    }

    public bool Clear(string target)
    {
        if (target.Length == 0)
            return false;

        if (CredDeleteW(target, GenericCredential, 0))
        {
            log.Info($"Removed {target}.");
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
