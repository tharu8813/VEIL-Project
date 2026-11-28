// SPDX-License-Identifier: MPL-2.0
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Veil.VolumeBroker;

// Offline disk signature observation only. Require Authenticode trust AND
// Microsoft's production root-key policy on the actual verified signer chain.
// The actual primary signer certificate must also match an independently
// authenticated module-specific SHA-256 pin. No live revocation guarantee.
internal static class CefSystemSignatureTrust
{
    private static readonly Guid GenericVerify = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private static readonly Guid DriverSubsystem = new("F750E6C3-38EE-11D1-85E5-00C04FC295EE");

    internal static bool ValidSignerPin(string? pin) =>
        pin is { Length: 64 } && pin.All(Uri.IsHexDigit);

    internal static bool MatchesSignerPin(string? expected, byte[]? observed) =>
        ValidSignerPin(expected) && observed is { Length: 32 } &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected!), observed);

    internal static bool Verify(string path, SafeFileHandle retained, string signerSha256)
    {
        if (!ValidSignerPin(signerSha256)) return false;
        return MatchesSignerPin(signerSha256, ObserveSignerSha256(path, retained));
    }

    // Root-policy observation is not authorization; never generate a baseline
    // from this result. A mismatched signer does not trigger catalog fallback.
    internal static byte[]? ObserveSignerSha256(string path, SafeFileHandle retained)
    {
        ArgumentNullException.ThrowIfNull(retained);
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess ||
            retained.IsInvalid || retained.IsClosed) return null;
        bool added = false;
        try
        {
            retained.DangerousAddRef(ref added);
            var member = retained.DangerousGetHandle();
            var file = new FileInfoNative { Size = Marshal.SizeOf<FileInfoNative>(), Path = path, File = member };
            if (!Rewind(retained)) return null;
            return VerifySubject(file, 1) ?? VerifyCatalog(path, retained, member);
        }
        finally { if (added) retained.DangerousRelease(); }
    }

    private static byte[]? VerifyCatalog(string path, SafeFileHandle retained, IntPtr member)
    {
        var subsystem = DriverSubsystem;
        if (!CryptCATAdminAcquireContext2(out var admin, ref subsystem, "SHA256", IntPtr.Zero, 0))
            return null;
        try
        {
            uint length = 0;
            if (!Rewind(retained) || !CryptCATAdminCalcHashFromFileHandle2(admin, retained, ref length, null, 0) || length != 32)
                return null;
            byte[] hash = new byte[32];
            if (!Rewind(retained) || !CryptCATAdminCalcHashFromFileHandle2(admin, retained, ref length, hash, 0) || length != 32)
                return null;
            // Deliberately first matching catalog only. No weaker hash fallback
            // or search past a failed trust check. Additional catalogs may be
            // legitimate but are not accepted without a reviewed policy.
            IntPtr catalog = CryptCATAdminEnumCatalogFromHash(admin, hash, length, 0, IntPtr.Zero);
            if (catalog == IntPtr.Zero) return null;
            try
            {
                var info = new CatalogPath { Size = Marshal.SizeOf<CatalogPath>(), Path = "" };
                if (!CryptCATCatalogInfoFromContext(catalog, ref info, 0) || string.IsNullOrWhiteSpace(info.Path))
                    return null;
                var data = new CatalogInfo
                {
                    Size = Marshal.SizeOf<CatalogInfo>(),
                    CatalogPath = info.Path, MemberTag = Convert.ToHexString(hash),
                    MemberPath = path, MemberFile = member, Admin = admin
                };
                return Rewind(retained) ? VerifySubject(data, 2) : null;
            }
            finally { CryptCATAdminReleaseCatalogContext(admin, catalog, 0); }
        }
        finally { CryptCATAdminReleaseContext(admin, 0); }
    }

    private static byte[]? VerifySubject<T>(T subject, int choice) where T : struct
    {
        IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        bool initialized = false;
        var action = GenericVerify;
        var data = new TrustData
        {
            Size = Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = choice,
            Subject = pointer, StateAction = 1, ProviderFlags = 0x10 | 0x1000
        };
        try
        {
            Marshal.StructureToPtr(subject, pointer, false);
            initialized = true;
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0 ||
                data.StateData == IntPtr.Zero) return null;
            IntPtr provider = WTHelperProvDataFromStateData(data.StateData);
            if (provider == IntPtr.Zero) return null;
            IntPtr signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            if (signer == IntPtr.Zero || Marshal.ReadInt32(signer) < Marshal.SizeOf<ProviderSigner>())
                return null;
            var signed = Marshal.PtrToStructure<ProviderSigner>(signer);
            if (signed.Error != 0 || signed.ChainContext == IntPtr.Zero) return null;
            var para = new ChainPolicyPara { Size = Marshal.SizeOf<ChainPolicyPara>() };
            var status = new ChainPolicyStatus { Size = Marshal.SizeOf<ChainPolicyStatus>() };
            // CERT_CHAIN_POLICY_MICROSOFT_ROOT. No test-root or alternate
            // application-root flags. This supplements, never replaces WVT.
            if (!CertVerifyCertificateChainPolicy(new IntPtr(7), signed.ChainContext, ref para, ref status)
                || status.Error != 0 || signed.ChainCount == 0) return null;
            // Use the leaf from the exact primary signer verified above, not
            // a certificate selected by name, timestamp or a separate lookup.
            IntPtr certificate = WTHelperGetProvCertFromChain(signer, 0);
            if (certificate == IntPtr.Zero ||
                Marshal.ReadInt32(certificate) < Marshal.SizeOf<ProviderCertificatePrefix>())
                return null;
            var providerCertificate = Marshal.PtrToStructure<ProviderCertificatePrefix>(certificate);
            if (providerCertificate.Context == IntPtr.Zero) return null;
            var context = Marshal.PtrToStructure<CertificateContextPrefix>(providerCertificate.Context);
            if (context.Encoded == IntPtr.Zero || context.EncodedLength is 0 or > 1048576)
                return null;
            byte[] encoded = new byte[(int)context.EncodedLength];
            Marshal.Copy(context.Encoded, encoded, 0, encoded.Length);
            return SHA256.HashData(encoded);
        }
        finally
        {
            if (data.StateData != IntPtr.Zero)
            {
                data.StateAction = 2;
                WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            }
            if (initialized) Marshal.DestroyStructure<T>(pointer);
            Marshal.FreeHGlobal(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfoNative
    {
        public int Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public IntPtr File, Subject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CatalogPath
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Path;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CatalogInfo
    {
        public int Size, Version;
        [MarshalAs(UnmanagedType.LPWStr)] public string CatalogPath, MemberTag, MemberPath;
        public IntPtr MemberFile, CalculatedHash;
        public uint HashLength;
        public IntPtr CatalogContext, Admin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public int Size;
        public IntPtr PolicyData, SipData;
        public int UiChoice, RevocationChecks, UnionChoice;
        public IntPtr Subject;
        public int StateAction;
        public IntPtr StateData, Url;
        public int ProviderFlags, UiContext;
        public IntPtr SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderSigner
    {
        public int Size;
        public System.Runtime.InteropServices.ComTypes.FILETIME VerifyAsOf;
        public uint ChainCount;
        public IntPtr Certificates;
        public uint SignerType;
        public IntPtr SignerInfo;
        public uint Error, CounterSigners;
        public IntPtr CounterSignerArray, ChainContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertificatePrefix { public uint Size; public IntPtr Context; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertificateContextPrefix
    {
        public uint Encoding;
        public IntPtr Encoded;
        public uint EncodedLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ChainPolicyPara { public int Size; public uint Flags; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ChainPolicyStatus { public int Size; public uint Error; public int ChainIndex, ElementIndex; public IntPtr Extra; }

    private static bool Rewind(SafeFileHandle file) => SetFilePointerEx(file, 0, out long position, 0) && position == 0;

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFilePointerEx(SafeFileHandle file, long distance, out long position, uint method);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminAcquireContext2(out IntPtr admin, ref Guid subsystem, string algorithm, IntPtr policy, uint flags);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr admin, SafeFileHandle file, ref uint length, [Out] byte[]? hash, uint flags);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr admin, byte[] hash, uint length, uint flags, IntPtr previous);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATCatalogInfoFromContext(IntPtr catalog, ref CatalogPath info, uint flags);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr admin, IntPtr catalog, uint flags);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr admin, uint flags);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr data, uint signer, [MarshalAs(UnmanagedType.Bool)] bool counter, uint index);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint index);
    [DllImport("crypt32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertVerifyCertificateChainPolicy(IntPtr policy, IntPtr chain, ref ChainPolicyPara para, ref ChainPolicyStatus status);
}
