// SPDX-License-Identifier: MPL-2.0
using System.Buffers.Binary;
using System.Security.AccessControl;
using System.Security.Cryptography;
using Veil.VolumeBroker;

internal static class CefSystemBaselineTrustStateTests
{
    private static byte[] Record(byte[] key, byte[] payload, long floor)
    {
        byte[] data = new byte[52 + key.Length];
        "VEILBST1"u8.CopyTo(data);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), floor);
        SHA256.HashData(payload).CopyTo(data, 16);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(48), key.Length);
        key.CopyTo(data, 52);
        return data;
    }

    internal static void Run(byte[] key, byte[] payload, byte[] signature)
    {
        void Reject(Action action)
        {
            try { action(); }
            catch (UnauthorizedAccessException) { return; }
            throw new InvalidOperationException("Invalid installed trust state accepted.");
        }
        byte[] record = Record(key, payload, 7);
        var state = CefSystemBaselineTrustState.Parse(record);
        state.RequirePayload(payload);
        state.RequireSame(CefSystemBaselineTrustState.Parse(record));
        if (state.MinimumSequence != 7 || !state.CopyPublicKey().SequenceEqual(key))
            throw new InvalidOperationException("Trust state roundtrip regression.");
        byte[] copy = state.CopyPublicKey();
        copy[0] ^= 1;
        if (!state.CopyPublicKey().SequenceEqual(key))
            throw new InvalidOperationException("Mutable trust key escaped.");
        byte[] changed = (byte[])payload.Clone();
        changed[0] ^= 1;
        Reject(() => state.RequirePayload(changed));
        Reject(() => state.RequireSame(CefSystemBaselineTrustState.Parse(Record(key, payload, 8))));
        Reject(() => state.RequireSame(CefSystemBaselineTrustState.Parse(Record(key, changed, 7))));
        Reject(() => CefSystemBaselineTrustState.Parse(Record(key, payload, 0)));
        Reject(() => CefSystemBaselineTrustState.Parse(record[..^1]));
        Reject(() => CefSystemBaselineTrustState.Parse([.. record, 0]));
        using var other = RSA.Create(3072);
        Reject(() => state.RequireSame(CefSystemBaselineTrustState.Parse(
            Record(other.ExportSubjectPublicKeyInfo(), payload, 7))));
        using var weak = RSA.Create(2048);
        Reject(() => CefSystemBaselineTrustState.Parse(Record(weak.ExportSubjectPublicKeyInfo(), payload, 7)));
        record[0] ^= 1;
        Reject(() => CefSystemBaselineTrustState.Parse(record));
        state.RequirePayload(payload);
        static byte[] Descriptor(string sddl)
        {
            var raw = new RawSecurityDescriptor(sddl);
            byte[] bytes = new byte[raw.BinaryLength];
            raw.GetBinaryForm(bytes, 0);
            return bytes;
        }
        CefSystemBaselineTrustRegistry.RequireDescriptor(Descriptor("O:BAG:BAD:P(A;;KA;;;SY)(A;;KA;;;BA)(A;;KR;;;BU)"));
        Reject(() => CefSystemBaselineTrustRegistry.RequireDescriptor(Descriptor("O:BAG:BAD:P(A;;KA;;;SY)(A;;KA;;;BA)(A;;KA;;;BU)")));
        Reject(() => CefSystemBaselineTrustRegistry.RequireDescriptor(Descriptor("O:BAG:BAD:(A;;KA;;;SY)(A;;KA;;;BA)(A;;KR;;;BU)")));
        Reject(() => CefSystemBaselineTrustRegistry.RequireDescriptor(Descriptor("O:BUG:BAD:P(A;;KA;;;SY)(A;;KA;;;BA)(A;;KR;;;BU)")));
        var verifier = CefSignedSystemModuleBaseline.VerifyTrustedSnapshot(payload, signature, state, () => state);
        verifier.RequireModules([]);
        var newer = CefSystemBaselineTrustState.Parse(Record(key, payload, 8));
        int reads = 0;
        Reject(() => CefSignedSystemModuleBaseline.VerifyTrustedSnapshot(payload, signature, state,
            () => ++reads == 1 ? state : newer));
        var current = state;
        var guarded = CefSignedSystemModuleBaseline.VerifyTrustedSnapshot(payload, signature, state, () => current);
        current = newer;
        Reject(() => guarded.RequireModules([]));
        Reject(() => CefSignedSystemModuleBaseline.VerifyTrustedSnapshot(changed, signature, state, () => state));
        Console.WriteLine("PASS: protected baseline trust record, exact digest, descriptor and stale snapshot refusal");
    }
}
