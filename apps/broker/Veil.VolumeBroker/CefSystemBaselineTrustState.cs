// SPDX-License-Identifier: MPL-2.0
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
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
    internal bool Revoked { get; }
    private CefSystemBaselineTrustState(byte[] record, byte[] digest, byte[] publicKey, long floor, bool revoked)
    {
        this.record = record;
        this.digest = digest;
        this.publicKey = publicKey;
        MinimumSequence = floor;
        Revoked = revoked;
    }

    internal byte[] CopyPublicKey() => (byte[])publicKey.Clone();
    internal byte[] CopyRecord() => (byte[])record.Clone();

    internal static CefSystemBaselineTrustState CreateVerifiedRecord(
        ReadOnlySpan<byte> payload, ReadOnlySpan<byte> publicKey, long sequence)
    {
        if (payload.Length is 0 or > 1048576 || publicKey.Length is 0 or > 4096 || sequence <= 0)
            throw new UnauthorizedAccessException("Invalid baseline trust record inputs.");
        byte[] data = new byte[52 + publicKey.Length];
        "VEILBST1"u8.CopyTo(data);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), sequence);
        SHA256.HashData(payload).CopyTo(data, 16);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(48), publicKey.Length);
        publicKey.CopyTo(data.AsSpan(52));
        return Parse(data);
    }

    internal static CefSystemBaselineTrustState Parse(ReadOnlySpan<byte> bytes)
    {
        // Magic (8), sequence LE64, exact payload SHA256 (32), SPKI length LE32, SPKI.
        if (bytes.Length is < 53 or > 4148)
            throw new UnauthorizedAccessException("Invalid baseline trust record size.");
        byte[] copy = bytes.ToArray();
        var data = copy.AsSpan();
        bool revoked = data[..8].SequenceEqual("VEILBSR1"u8);
        if (!revoked && !data[..8].SequenceEqual("VEILBST1"u8))
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
        return new(copy, data[16..48].ToArray(), key, floor, revoked);
    }

    internal void RequireActive()
    {
        if (Revoked)
            throw new UnauthorizedAccessException("System baseline signing key revoked.");
    }

    // Preserve the exact last sequence, payload digest and key. Never delete
    // trust or reset enrollment on revocation.
    internal CefSystemBaselineTrustState CreateRevokedRecord()
    {
        RequireActive();
        byte[] copy = CopyRecord();
        "VEILBSR1"u8.CopyTo(copy);
        return Parse(copy);
    }

    internal void RequirePayload(ReadOnlySpan<byte> payload)
    {
        RequireActive();
        if (payload.Length is 0 or > 1048576 ||
            !CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(payload)))
            throw new UnauthorizedAccessException("Baseline does not match installed trust digest.");
    }

    internal bool Matches(CefSystemBaselineTrustState? current) =>
        current is not null && CryptographicOperations.FixedTimeEquals(record, current.record);

    internal void RequireSame(CefSystemBaselineTrustState current)
    {
        if (!Matches(current))
            throw new UnauthorizedAccessException("System baseline trust state changed.");
    }
}

// Separate fixed machine key: do not alter the strict VEIL/CefBootstrap
// layout. Explicit administrator maintenance only; no IPC or user fallback.
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
        return ReadRecord(leaf);
    }

    private static CefSystemBaselineTrustState ReadRecord(RegistryKey leaf)
    {
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

    private static RegistryKey Open(RegistryKey parent, string name, int access = Read64) =>
        TryOpen(parent, name, access) ?? throw new Win32Exception(2);

    private static RegistryKey? TryOpen(RegistryKey parent, string name, int access)
    {
        int result = RegOpenKeyEx(parent.Handle, name, OpenLink, access, out var handle);
        if (result == 2)
        {
            handle?.Dispose();
            return null;
        }
        if (result != 0)
        {
            handle?.Dispose();
            throw new Win32Exception(result);
        }
        try { return RegistryKey.FromHandle(handle, RegistryView.Registry64); }
        catch { handle.Dispose(); throw; }
    }

    // This deliberately disconnected maintenance entry accepts only a prepared
    // signed transition, not raw registry data. All cooperative writers share
    // the protected machine mutex. Administrator bypass is outside this boundary.
    internal static void Publish(CefSystemBaselineTrustUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        RequireMaintenanceAuthority();
        using var mutex = CefSystemBaselineWriterLock.Acquire();
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var software = Open(machine, "SOFTWARE", Read64 | 4);
        RequireOrdinary(software);
        using var existing = TryOpen(software, Leaf, Read64 | 2);
        var current = existing is null ? null : ReadRecord(existing);
        // Validate before creating a key. An existing empty/partial key is
        // never treated as absent and is not deleted or repaired automatically.
        byte[] record = update.RequireExpectedAndCopyRecord(current, CefSystemBaselineHostObserver.Observe());
        using var created = existing is null ? CreateNew(software) : null;
        var target = existing ?? created!;
        RequireProtected(target);
        if (existing is null)
        {
            if (target.ValueCount != 0 || target.SubKeyCount != 0)
                throw new UnauthorizedAccessException("New baseline key is not empty.");
        }
        else
        {
            current!.RequireSame(ReadRecord(target));
        }
        // Recheck host immediately before the sole record write.
        _ = update.RequireExpectedAndCopyRecord(current, CefSystemBaselineHostObserver.Observe());
        target.SetValue(Value, record, RegistryValueKind.Binary);
        int flush = RegFlushKey(target.Handle);
        if (flush != 0) throw new Win32Exception(flush);
        update.RequirePublished(ReadRecord(target), CefSystemBaselineHostObserver.Observe());
        update.RequirePublished(ReadStable(), CefSystemBaselineHostObserver.Observe());
        // Never undo a write on an uncertain result: rollback could lower the
        // floor. Operators must inspect the installed state before retry.
    }

    private static void RequireMaintenanceAuthority()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64 ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Expected native Windows x64.");
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Elevated administrator required.");
    }

    // Read-only diagnosis against the exact independently retained prepared
    // transition. Missing, partial or foreign records are never fixed here.
    // Invalid ACLs/layouts and IO errors propagate rather than become absence.
    internal static CefBaselinePublicationStatus InspectInterruptedPublication(CefSystemBaselineTrustUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        RequireMaintenanceAuthority();
        using var mutex = CefSystemBaselineWriterLock.Acquire();
        var host = CefSystemBaselineHostObserver.Observe();
        var first = ObservePublication(update, host);
        var second = ObservePublication(update, CefSystemBaselineHostObserver.Observe());
        if (first != second)
            throw new UnauthorizedAccessException("Baseline publication changed during inspection.");
        return first;
    }

    private static CefBaselinePublicationStatus ObservePublication(CefSystemBaselineTrustUpdate update,
        CefSystemBaselineHost host)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var software = Open(machine, "SOFTWARE");
        RequireOrdinary(software);
        using var leaf = TryOpen(software, Leaf, Read64);
        if (leaf is null) return update.ClassifyPublication(false, null, host);
        RequireProtected(leaf);
        if (leaf.ValueCount == 0 && leaf.SubKeyCount == 0)
        {
            RequireProtected(leaf);
            return update.ClassifyPublication(true, null, host);
        }
        return update.ClassifyPublication(true, ReadRecord(leaf), host);
    }

    // Explicit confirmation only when the exact intended record is already
    // present. Flush and re-read; no SetValue, creation, deletion or rollback.
    // This does not prove power-loss durability or resolve a partial key.
    internal static void ConfirmInterruptedPublication(CefSystemBaselineTrustUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        RequireMaintenanceAuthority();
        using var mutex = CefSystemBaselineWriterLock.Acquire();
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var software = Open(machine, "SOFTWARE");
        RequireOrdinary(software);
        using var leaf = Open(software, Leaf);
        update.RequirePublished(ReadRecord(leaf), CefSystemBaselineHostObserver.Observe());
        int flush = RegFlushKey(leaf.Handle);
        if (flush != 0) throw new Win32Exception(flush);
        update.RequirePublished(ReadRecord(leaf), CefSystemBaselineHostObserver.Observe());
        update.RequirePublished(ReadStable(), CefSystemBaselineHostObserver.Observe());
    }

    private static RegistryKey CreateNew(RegistryKey parent)
    {
        var raw = new RawSecurityDescriptor(Descriptor);
        byte[] bytes = new byte[raw.BinaryLength];
        raw.GetBinaryForm(bytes, 0);
        IntPtr descriptor = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, descriptor, bytes.Length);
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor
            };
            int result = RegCreateKeyEx(parent.Handle, Leaf, 0, null, 0, Read64 | 2,
                ref attributes, out var handle, out int disposition);
            if (result != 0 || disposition != 1)
            {
                handle?.Dispose();
                if (result != 0) throw new Win32Exception(result);
                throw new UnauthorizedAccessException("Existing trust key refused during enrollment.");
            }
            try { return RegistryKey.FromHandle(handle, RegistryView.Registry64); }
            catch { handle.Dispose(); throw; }
        }
        finally { Marshal.FreeHGlobal(descriptor); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int Inherit;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegCreateKeyExW")]
    private static extern int RegCreateKeyEx(SafeRegistryHandle parent, string name,
        int reserved, string? keyClass, int options, int access,
        ref SecurityAttributes attributes, out SafeRegistryHandle key, out int disposition);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern int RegFlushKey(SafeRegistryHandle key);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegOpenKeyExW")]
    private static extern int RegOpenKeyEx(SafeRegistryHandle parent, string name, int options,
        int access, out SafeRegistryHandle key);
}
