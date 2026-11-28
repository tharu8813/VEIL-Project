// SPDX-License-Identifier: MPL-2.0
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Veil.VolumeBroker;

// Windows fixture only, not compiled into the installed broker. This
// experiments with the actual process account's DPAPI user scope; it is
// NOT proof of installed service identity, ACLs or rollback resistance.
internal static class CefLibraryRootProtectionFixture
{
    internal const int MaximumBlobBytes = 16 * 1024;
    private const int UiForbidden = 1;

    private static void RequireAuthority(Func<bool> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        using var before = WindowsIdentity.GetCurrent(true);
        if (before is not null || !current())
            throw new UnauthorizedAccessException("Fixture root authority unavailable.");
        using var after = WindowsIdentity.GetCurrent(true);
        if (after is not null)
            throw new UnauthorizedAccessException("Fixture root impersonation refused.");
    }

    private static byte[] Entropy(Guid storeId)
    {
        if (storeId == Guid.Empty)
            throw new ArgumentException("Fixture store identity unavailable.");
        byte[] result = new byte[16 + "VEIL-CEF-ROOT-DPAPI-v1"u8.Length];
        if (!storeId.TryWriteBytes(result.AsSpan(0, 16)))
            throw new InvalidOperationException("Cannot encode store identity.");
        "VEIL-CEF-ROOT-DPAPI-v1"u8.CopyTo(result.AsSpan(16));
        return result;
    }

    // Explicit provisioning only. Open never generates a replacement key.
    internal static byte[] Create(Guid storeId, Func<bool> current)
    {
        RequireAuthority(current);
        byte[] entropy = Entropy(storeId);
        byte[] root = RandomNumberGenerator.GetBytes(CefLibraryGenerationChain.TagSize);
        try
        {
            byte[] protectedRoot = Transform(root, entropy, protect: true);
            RequireAuthority(current);
            return protectedRoot;
        }
        finally { CryptographicOperations.ZeroMemory(root); }
    }

    internal static CefLibraryKeyLease Open(Guid storeId, ReadOnlySpan<byte> protectedRoot, Func<bool> current)
    {
        RequireAuthority(current);
        if (protectedRoot.Length == 0 || protectedRoot.Length > MaximumBlobBytes)
            throw new InvalidDataException("Invalid protected fixture root.");
        byte[] entropy = Entropy(storeId);
        byte[] root = Transform(protectedRoot.ToArray(), entropy, protect: false);
        try
        {
            RequireAuthority(current);
            return CefLibraryKeyLease.Open(storeId, root, () =>
            {
                RequireAuthority(current);
                return true;
            });
        }
        finally { CryptographicOperations.ZeroMemory(root); }
    }

    private static byte[] Transform(byte[] input, byte[] entropy, bool protect)
    {
        GCHandle pinnedInput = default, pinnedEntropy = default;
        DataBlob output = default;
        try
        {
            pinnedInput = GCHandle.Alloc(input, GCHandleType.Pinned);
            pinnedEntropy = GCHandle.Alloc(entropy, GCHandleType.Pinned);
            var data = new DataBlob { Size = input.Length, Data = pinnedInput.AddrOfPinnedObject() };
            var optional = new DataBlob { Size = entropy.Length, Data = pinnedEntropy.AddrOfPinnedObject() };
            // No machine-wide flag, UI, password prompts or plaintext fallback.
            bool ok = protect
                ? CryptProtectData(ref data, null, ref optional, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref data, IntPtr.Zero, ref optional, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Fixture DPAPI operation refused.");
            if (output.Data == IntPtr.Zero || output.Size <= 0 ||
                output.Size > MaximumBlobBytes ||
                (!protect && output.Size != CefLibraryGenerationChain.TagSize))
                throw new InvalidDataException("Invalid fixture DPAPI output.");
            byte[] result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                // DPAPI owns this allocation; clear its plaintext before free.
                if (!protect)
                    for (int i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
                _ = LocalFree(output.Data);
            }
            if (pinnedEntropy.IsAllocated) pinnedEntropy.Free();
            if (pinnedInput.IsAllocated) pinnedInput.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob data, string? description,
        ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob data, IntPtr description,
        ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
