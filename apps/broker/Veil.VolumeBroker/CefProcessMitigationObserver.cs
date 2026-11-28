// SPDX-License-Identifier: MPL-2.0
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Veil.VolumeBroker;

// Raw kernel policy snapshots, not a reviewed role baseline or sandbox verdict.
// Preserve zero and all flag bits exactly; failed queries must never become zero.
internal enum CefMitigationPolicy
{
    Aslr = 1, DynamicCode = 2, SystemCallDisable = 4,
    ControlFlowGuard = 7, ImageLoad = 10, ChildProcess = 13
}

internal sealed record CefMitigationSnapshot(uint Aslr, uint DynamicCode,
    uint SystemCallDisable, uint ControlFlowGuard, uint ImageLoad, uint ChildProcess)
{
    internal static CefMitigationSnapshot Read(Func<CefMitigationPolicy, uint> query) =>
        new(query(CefMitigationPolicy.Aslr), query(CefMitigationPolicy.DynamicCode),
            query(CefMitigationPolicy.SystemCallDisable), query(CefMitigationPolicy.ControlFlowGuard),
            query(CefMitigationPolicy.ImageLoad), query(CefMitigationPolicy.ChildProcess));

    internal static void RequireSame(CefMitigationSnapshot expected, CefMitigationSnapshot current)
    {
        if (expected != current)
            throw new UnauthorizedAccessException("CEF process mitigation policy changed.");
    }
}

internal static class CefProcessMitigationObserver
{
    internal static CefMitigationSnapshot Observe(SafeProcessHandle retained, ClientIdentity expected)
    {
        ArgumentNullException.ThrowIfNull(retained);
        ArgumentNullException.ThrowIfNull(expected);
        if (Native.ObserveProcess(retained) != expected)
            throw new UnauthorizedAccessException("CEF mitigation process identity mismatch.");
        // GetProcessMitigationPolicy requires PROCESS_QUERY_INFORMATION.
        // Reopen only for read-only query/synchronize; the original process
        // object remains retained and the new handle must match it.
        const uint queryAccess = 0x0400 | 0x1000 | 0x00100000;
        using var query = OpenProcess(queryAccess, false, (uint)expected.ProcessId);
        if (query.IsInvalid) throw new Win32Exception();
        void RequireIdentity()
        {
            if (!Native.SameLiveProcess(retained, query) || Native.ObserveProcess(query) != expected ||
                Native.ObserveProcess(retained) != expected)
                throw new UnauthorizedAccessException("CEF mitigation process changed.");
        }
        RequireIdentity();
        uint Read(CefMitigationPolicy policy)
        {
            // These six Windows SDK policy structures consist of a DWORD
            // union. DEP and other differently sized policies are excluded.
            if (!GetProcessMitigationPolicy(query, policy, out uint flags, (UIntPtr)sizeof(uint)))
                throw new Win32Exception();
            return flags;
        }
        var before = CefMitigationSnapshot.Read(Read);
        RequireIdentity();
        CefMitigationSnapshot.RequireSame(before, CefMitigationSnapshot.Read(Read));
        RequireIdentity();
        return before;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMitigationPolicy(SafeProcessHandle process,
        CefMitigationPolicy policy, out uint flags, UIntPtr bytes);
}
