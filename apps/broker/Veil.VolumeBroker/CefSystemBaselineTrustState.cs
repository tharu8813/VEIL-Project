// SPDX-License-Identifier: MPL-2.0
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Veil.VolumeBroker;

// Immutable snapshot of an independently provisioned exact manifest digest,
// release sequence floor and verification key. No enrollment or writes here.
internal sealed class CefSystemBaselineTrustState
{
    private readonly byte[] record;
    private readonly byte[] digest;
    private readonly byte[] publicKey;
    internal long MinimumSequence { get; }
    private CefSystemBaselineTrustState(byte[] record, byte[] digest, byte[] publicKey, long floor)
    {
        this.record = record;
        this.digest = digest;
        this.publicKey = publicKey;
        MinimumSequence = floor;
    }

    internal byte[] CopyPublicKey() => (byte[])publicKey.Clone();

    internal static CefSystemBaselineTrustState Parse(ReadOnlySpan<byte> bytes)
    {
        // Magic (8), sequence LE64, exact payload SHA256 (32), SPKI length LE32, SPKI.
        if (bytes.Length is < 53 or > 4148)
            throw new UnauthorizedAccessException("Invalid baseline trust record size.");
        byte[] copy = bytes.ToArray();
        var data = copy.AsSpan();
        if (!data[..8].SequenceEqual("VEILBST1"u8))
            throw new UnauthorizedAccessException("Invalid baseline trust record schema.");
        long floor = BinaryPrimitives.ReadInt64LittleEndian(data[8..16]);
        int keyLength = BinaryPrimitives.ReadInt32LittleEndian(data[48..52]);
        if (floor <= 0 || keyLength <= 0 || keyLength != data.Length - 52)
            throw new UnauthorizedAccessException("Invalid baseline trust record fields.");
        byte[] key = data[52..].ToArray();
        using var rsa = RSA.Create();
        try
        {
            rsa.ImportSubjectPublicKeyInfo(key, out int consumed);
            if (consumed != key.Length || rsa.KeySize is not (3072 or 4096))
                throw new UnauthorizedAccessException("Invalid baseline trust key.");
        }
        catch (CryptographicException e)
        {
            throw new UnauthorizedAccessException("Invalid baseline trust key.", e);
        }
        return new(copy, data[16..48].ToArray(), key, floor);
    }

    internal void RequirePayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is 0 or > 1048576 ||
            !CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(payload)))
            throw new UnauthorizedAccessException("Baseline does not match installed trust digest.");
    }

    internal void RequireSame(CefSystemBaselineTrustState current)
    {
        if (current is null || !CryptographicOperations.FixedTimeEquals(record, current.record))
            throw new UnauthorizedAccessException("System baseline trust state changed.");
    }
}

// Separate fixed machine key: do not alter the strict VEIL/CefBootstrap
// layout. No IPC, installation, registry mutation or fallback to user state.
internal static class CefSystemBaselineTrustRegistry
{
    private const string Leaf = "VEIL.CefSystemBaselineTrust";
    private const string Value = "TrustRecord";
    private const string Descriptor = "O:BAG:BAD:P(A;;KA;;;SY)(A;;KA;;;BA)(A;;KR;;;BU)";
    private const int Read64 = 0x20119;
    private const int OpenLink = 8;

    internal static void RequireDescriptor(byte[] bytes)
    {
        var expected = new RawSecurityDescriptor(Descriptor);
        var actual = new RawSecurityDescriptor(bytes, 0);
        var sections = AccessControlSections.Owner | AccessControlSections.Access;
        if (expected.GetSddlForm(sections) != actual.GetSddlForm(sections))
            throw new UnauthorizedAccessException("Unexpected system baseline trust registry owner or DACL.");
    }

    internal static CefSystemBaselineTrustState ReadStable()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64 ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Expected native Windows x64.");
        var first = ReadOnce();
        first.RequireSame(ReadOnce());
        return first;
    }

    private static CefSystemBaselineTrustState ReadOnce()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var software = Open(machine, "SOFTWARE");
        RequireOrdinary(software);
        using var leaf = Open(software, Leaf);
        RequireProtected(leaf);
        if (leaf.SubKeyCount != 0 || leaf.ValueCount != 1 ||
            !leaf.GetValueNames().SequenceEqual(new[] { Value }, StringComparer.Ordinal) ||
            leaf.GetValueKind(Value) != RegistryValueKind.Binary ||
            leaf.GetValue(Value, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not byte[] data)
            throw new UnauthorizedAccessException("Unexpected system baseline trust registry layout.");
        var state = CefSystemBaselineTrustState.Parse(data);
        RequireProtected(leaf);
        return state;
    }

    private static void RequireOrdinary(RegistryKey key)
    {
        if (key.GetValueNames().Any(n => string.Equals(n, "SymbolicLinkValue", StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("Registry link refused.");
    }

    private static void RequireProtected(RegistryKey key)
    {
        RequireOrdinary(key);
        RequireDescriptor(key.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            .GetSecurityDescriptorBinaryForm());
    }

    private static RegistryKey Open(RegistryKey parent, string name)
    {
        int result = RegOpenKeyEx(parent.Handle, name, OpenLink, Read64, out var handle);
        if (result != 0)
        {
            handle?.Dispose();
            throw new Win32Exception(result);
        }
        try { return RegistryKey.FromHandle(handle, RegistryView.Registry64); }
        catch { handle.Dispose(); throw; }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegOpenKeyExW")]
    private static extern int RegOpenKeyEx(SafeRegistryHandle parent, string name, int options,
        int access, out SafeRegistryHandle key);
}
