// SPDX-License-Identifier: MPL-2.0
using System.Security.Cryptography;

namespace Veil.VolumeBroker;

// Prospective in-memory key custody, not a persisted root key provider.
// Root material must be high-entropy, independently protected and bound to
// the authenticated profile by the caller. Never accept it from client IPC.
// Authority observation must be synchronous and must not wait on other
// operations or disposal. The retained broker gate must serialize actual
// publication/revocation; this lease alone is not a disk commit permit.
internal sealed class CefLibraryKeyLease : IDisposable
{
    private readonly object sync = new();
    private readonly Guid storeId;
    private readonly Func<bool> authorityCurrent;
    private readonly byte[] key = new byte[CefLibraryGenerationChain.TagSize];
    private int revoked;
    private bool busy;

    private CefLibraryKeyLease(Guid storeId, Func<bool> authorityCurrent)
    {
        this.storeId = storeId;
        this.authorityCurrent = authorityCurrent;
    }

    internal static CefLibraryKeyLease Open(Guid storeId, ReadOnlySpan<byte> rootKey,
        Func<bool> authorityCurrent)
    {
        ArgumentNullException.ThrowIfNull(authorityCurrent);
        if (storeId == Guid.Empty || rootKey.Length != CefLibraryGenerationChain.TagSize)
            throw new ArgumentException("Invalid library key context.");
        if (!authorityCurrent())
            throw new UnauthorizedAccessException("Library key authority unavailable.");
        var lease = new CefLibraryKeyLease(storeId, authorityCurrent);
        try
        {
            // HKDF domain separates this key from all other VEIL purposes.
            Span<byte> salt = stackalloc byte[16];
            if (!storeId.TryWriteBytes(salt))
                throw new InvalidOperationException("Cannot encode library store identity.");
            HKDF.DeriveKey(HashAlgorithmName.SHA256, rootKey, lease.key, salt,
                "VEIL-CEF-LIBRARY-HMAC-v1"u8);
            lock (lease.sync)
            {
                lease.busy = true;
                try { lease.RequireCurrent(); }
                finally { lease.busy = false; }
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal bool IsRevoked => Volatile.Read(ref revoked) != 0;

    private void RetireLocked()
    {
        Interlocked.Exchange(ref revoked, 1);
        CryptographicOperations.ZeroMemory(key);
    }

    private void RequireCurrent()
    {
        if (IsRevoked)
            throw new UnauthorizedAccessException("Library key lease revoked.");
        try
        {
            if (authorityCurrent() && !IsRevoked) return;
        }
        catch
        {
            RetireLocked();
            throw;
        }
        RetireLocked();
        throw new UnauthorizedAccessException("Library key authority changed.");
    }

    private void Begin()
    {
        if (busy || IsRevoked)
            throw new UnauthorizedAccessException("Library key operation unavailable.");
        busy = true;
    }

    // Typed operations only: no generic callback receives raw key material.
    internal byte[] Encode(int generation, ReadOnlySpan<byte> previous, ReadOnlySpan<byte> payload)
    {
        lock (sync)
        {
            Begin();
            byte[]? encoded = null;
            bool accepted = false;
            try
            {
                RequireCurrent();
                encoded = CefLibraryGenerationEnvelope.Encode(storeId, key, generation, previous, payload);
                RequireCurrent();
                accepted = true;
                return encoded;
            }
            finally
            {
                if (!accepted && encoded is not null) CryptographicOperations.ZeroMemory(encoded);
                busy = false;
            }
        }
    }

    internal CefLibraryGenerationRecord Decode(int generation, ReadOnlySpan<byte> previous,
        ReadOnlySpan<byte> encoded)
    {
        lock (sync)
        {
            Begin();
            CefLibraryGenerationRecord? record = null;
            bool accepted = false;
            try
            {
                RequireCurrent();
                record = CefLibraryGenerationEnvelope.Decode(storeId, key, generation, previous, encoded);
                RequireCurrent();
                accepted = true;
                return record;
            }
            finally
            {
                if (!accepted && record is not null) CryptographicOperations.ZeroMemory(record.Payload);
                busy = false;
            }
        }
    }

    // Latch first, then wait for any in-flight crypto to leave the monitor
    // before clearing the owned key. A result already returned cannot be
    // recalled; callers own and must clear their result buffers.
    internal void Revoke()
    {
        Interlocked.Exchange(ref revoked, 1);
        lock (sync) RetireLocked();
    }

    public void Dispose() => Revoke();
}
