using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using FlickGit.App.Settings;
using FlickGit.Logging;

namespace FlickGit.App.Mac;

/// <summary>
/// <see cref="ISecretStore"/> on the macOS Keychain, through Security.framework.
///
/// <b>Why the interop rather than the <c>security</c> command.</b> The obvious shortcut is
/// <c>security add-generic-password -w &lt;secret&gt;</c>, and it is wrong for one specific reason:
/// that puts the secret in <c>argv</c>, where any local user can read it out of <c>ps</c> for as
/// long as the process lives. A tool whose own rules say API keys never touch a file it writes
/// cannot then hand them to the process table.
///
/// <b>Generic passwords, keyed by service.</b> The target string — <c>FlickGit:anthropic</c>,
/// <c>FlickGit:forge:github.com</c> — goes in <c>kSecAttrService</c>, which is what Keychain Access
/// shows as the item's name. <see cref="SecretTargets"/> builds them, identically on both platforms,
/// so a key is filed under the same string wherever it was stored.
///
/// <b>Every failure is an answer, never an exception.</b> A keystore can be locked, and the
/// interface already has a shape for "could not": null from a read, false from a write. The status
/// code goes to the log so the sentence the user sees does not have to carry it.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed partial class KeychainSecretStore(ILog log) : ISecretStore
{
    private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const int Success = 0;
    private const int ItemNotFound = -25300;
    private const int DuplicateItem = -25299;

    /// <summary>kCFStringEncodingUTF8.</summary>
    private const uint Utf8 = 0x08000100;

    private static readonly IntPtr Security = NativeLibrary.Load(SecurityFramework);
    private static readonly IntPtr CoreFoundation = NativeLibrary.Load(CoreFoundationFramework);

    //The kSec* names are CFStringRef *globals*: the export is the address of the pointer, so it has
    //to be dereferenced once. Reading the export address itself would pass a pointer-to-pointer into
    //every dictionary and match nothing.
    private static readonly IntPtr SecClass = Global(Security, "kSecClass");
    private static readonly IntPtr SecClassGenericPassword = Global(Security, "kSecClassGenericPassword");
    private static readonly IntPtr SecAttrService = Global(Security, "kSecAttrService");
    private static readonly IntPtr SecAttrAccount = Global(Security, "kSecAttrAccount");
    private static readonly IntPtr SecValueData = Global(Security, "kSecValueData");
    private static readonly IntPtr SecReturnData = Global(Security, "kSecReturnData");
    private static readonly IntPtr SecMatchLimit = Global(Security, "kSecMatchLimit");
    private static readonly IntPtr SecMatchLimitOne = Global(Security, "kSecMatchLimitOne");
    private static readonly IntPtr CFBooleanTrue = Global(CoreFoundation, "kCFBooleanTrue");

    //These two are the structs themselves, not pointers to them, so the export address *is* the
    //argument. The asymmetry with the kSec* globals above is the easiest thing to get wrong here.
    private static readonly IntPtr TypeDictionaryKeyCallBacks =
        NativeLibrary.GetExport(CoreFoundation, "kCFTypeDictionaryKeyCallBacks");

    private static readonly IntPtr TypeDictionaryValueCallBacks =
        NativeLibrary.GetExport(CoreFoundation, "kCFTypeDictionaryValueCallBacks");

    /// <summary>
    /// One account name for every item.
    ///
    /// The service already identifies the secret uniquely, and a generic password is keyed by the
    /// pair — so this has to be *something*, stable, and the same on every read and write. It is what
    /// Keychain Access shows in the "Account" column.
    /// </summary>
    private const string Account = "FlickGit";

    public bool Has(string target) => Read(target) is { Length: > 0 };

    public string? Read(string target)
    {
        IntPtr query = IntPtr.Zero;

        try
        {
            query = Dictionary(
                [SecClass, SecAttrService, SecAttrAccount, SecReturnData, SecMatchLimit],
                [SecClassGenericPassword, CFString(target), CFString(Account), CFBooleanTrue, SecMatchLimitOne]);

            int status = SecItemCopyMatching(query, out IntPtr data);

            if (status == ItemNotFound)
                return null;

            if (status != Success)
            {
                log.Warn($"Keychain read of {target} failed with status {status}.");

                return null;
            }

            try
            {
                return Utf8Of(data);
            }
            finally
            {
                CFRelease(data);
            }
        }
        finally
        {
            Release(query);
        }
    }

    public bool Write(string target, string secret)
    {
        //Add first and update on a duplicate, rather than delete-then-add: a delete that succeeded
        //followed by an add that failed would leave the user with no key and no way to tell that is
        //what happened.
        IntPtr add = IntPtr.Zero;

        try
        {
            add = Dictionary(
                [SecClass, SecAttrService, SecAttrAccount, SecValueData],
                [SecClassGenericPassword, CFString(target), CFString(Account), CFData(secret)]);

            int status = SecItemAdd(add, IntPtr.Zero);

            if (status == Success)
                return true;

            if (status != DuplicateItem)
            {
                log.Warn($"Keychain write of {target} failed with status {status}.");

                return false;
            }
        }
        finally
        {
            Release(add);
        }

        return Update(target, secret);
    }

    public bool Clear(string target)
    {
        IntPtr query = IntPtr.Zero;

        try
        {
            query = Dictionary(
                [SecClass, SecAttrService, SecAttrAccount],
                [SecClassGenericPassword, CFString(target), CFString(Account)]);

            int status = SecItemDelete(query);

            //Not found is the state the caller asked for, so it is success.
            if (status is Success or ItemNotFound)
                return true;

            log.Warn($"Keychain delete of {target} failed with status {status}.");

            return false;
        }
        finally
        {
            Release(query);
        }
    }

    private bool Update(string target, string secret)
    {
        IntPtr query = IntPtr.Zero;
        IntPtr changes = IntPtr.Zero;

        try
        {
            query = Dictionary(
                [SecClass, SecAttrService, SecAttrAccount],
                [SecClassGenericPassword, CFString(target), CFString(Account)]);

            changes = Dictionary([SecValueData], [CFData(secret)]);

            int status = SecItemUpdate(query, changes);

            if (status == Success)
                return true;

            log.Warn($"Keychain update of {target} failed with status {status}.");

            return false;
        }
        finally
        {
            Release(query);
            Release(changes);
        }
    }

    /// <summary>
    /// Builds a CFDictionary and takes ownership of the values.
    ///
    /// The values are created by the caller with <see cref="CFString"/> and <see cref="CFData"/> and
    /// released here: <c>CFDictionaryCreate</c> retains what it stores, so releasing immediately
    /// after leaves the dictionary holding the only reference. Doing it any other way means either a
    /// leak per call or a per-value <c>finally</c> at every call site.
    /// </summary>
    private static IntPtr Dictionary(IntPtr[] keys, IntPtr[] values)
    {
        IntPtr dictionary = CFDictionaryCreate(
            IntPtr.Zero,
            keys,
            values,
            keys.Length,
            TypeDictionaryKeyCallBacks,
            TypeDictionaryValueCallBacks);

        foreach (IntPtr value in values)
        {
            //Only the ones this class created. The kSec* constants are framework-owned globals and
            //releasing one would corrupt every later use of it.
            if (!IsConstant(value))
                CFRelease(value);
        }

        return dictionary;
    }

    private static bool IsConstant(IntPtr value) =>
        value == SecClassGenericPassword
        || value == CFBooleanTrue
        || value == SecMatchLimitOne;

    private static void Release(IntPtr value)
    {
        if (value != IntPtr.Zero)
            CFRelease(value);
    }

    private static IntPtr Global(IntPtr library, string name) =>
        Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));

    private static IntPtr CFString(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value + '\0');

        return CFStringCreateWithCString(IntPtr.Zero, utf8, Utf8);
    }

    private static IntPtr CFData(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);

        return CFDataCreate(IntPtr.Zero, utf8, utf8.Length);
    }

    private static string? Utf8Of(IntPtr data)
    {
        if (data == IntPtr.Zero)
            return null;

        IntPtr bytes = CFDataGetBytePtr(data);
        long length = CFDataGetLength(data);

        return bytes == IntPtr.Zero || length <= 0
            ? null
            : Marshal.PtrToStringUTF8(bytes, (int)length);
    }

    [LibraryImport(SecurityFramework)]
    private static partial int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [LibraryImport(SecurityFramework)]
    private static partial int SecItemAdd(IntPtr attributes, IntPtr result);

    [LibraryImport(SecurityFramework)]
    private static partial int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);

    [LibraryImport(SecurityFramework)]
    private static partial int SecItemDelete(IntPtr query);

    [LibraryImport(CoreFoundationFramework)]
    private static partial IntPtr CFDictionaryCreate(
        IntPtr allocator,
        IntPtr[] keys,
        IntPtr[] values,
        nint count,
        IntPtr keyCallBacks,
        IntPtr valueCallBacks);

    [LibraryImport(CoreFoundationFramework)]
    private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, byte[] cString, uint encoding);

    [LibraryImport(CoreFoundationFramework)]
    private static partial IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

    [LibraryImport(CoreFoundationFramework)]
    private static partial IntPtr CFDataGetBytePtr(IntPtr data);

    [LibraryImport(CoreFoundationFramework)]
    private static partial long CFDataGetLength(IntPtr data);

    [LibraryImport(CoreFoundationFramework)]
    private static partial void CFRelease(IntPtr value);
}
