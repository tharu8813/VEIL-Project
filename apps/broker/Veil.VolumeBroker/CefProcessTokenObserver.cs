// SPDX-License-Identifier: MPL-2.0
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Veil.VolumeBroker;

// Read-only observation of a retained live Windows process. These fields are
// not a role-specific sandbox verdict or proof of continuous token state.
internal sealed record CefProcessTokenObservation(ClientIdentity Identity, uint IntegrityRid,
    bool Restricted, bool AppContainer, bool Elevated, uint ElevationType);

// Launch-source policy is stricter than the provisional role diagnostic.
// The authenticated interactive owner and browser root must be distinct
// process objects in the same non-elevated logon session. Never treat a
// filtered administrator token as a separate standard-user account.
internal sealed record CefLaunchTokenSnapshot(CefProcessTokenObservation Token,
    long AuthenticationId, long TokenId, long ModifiedId,
    bool AdministratorGroupPresent, bool OnlyStandardPrivileges);

internal static class CefLaunchTokenPolicy
{
    internal static void RequireStandard(CefLaunchTokenSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var token = snapshot.Token;
        if (CefRoleTokenPolicy.Evaluate(token.Identity, CefObservedRole.Browser, token) !=
            CefRoleTokenDecision.MatchesProvisionalBaseline ||
            snapshot.AuthenticationId == 0 || snapshot.AdministratorGroupPresent ||
            !snapshot.OnlyStandardPrivileges ||
            token.Identity.UserSid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20")
            throw new UnauthorizedAccessException("CEF requires a separate standard-user launch token.");
    }

    internal static void RequirePair(CefLaunchTokenSnapshot owner, CefLaunchTokenSnapshot root)
    {
        RequireStandard(owner);
        RequireStandard(root);
        if (owner.Token.Identity.ProcessId == root.Token.Identity.ProcessId ||
            root.Token.Identity.CreatedFileTime < owner.Token.Identity.CreatedFileTime ||
            owner.Token.Identity.SessionId != root.Token.Identity.SessionId ||
            !string.Equals(owner.Token.Identity.UserSid, root.Token.Identity.UserSid, StringComparison.OrdinalIgnoreCase) ||
            owner.AuthenticationId != root.AuthenticationId)
            throw new UnauthorizedAccessException("CEF root is outside the authenticated owner's logon.");
    }

    internal static void RequireSame(CefLaunchTokenSnapshot expected, CefLaunchTokenSnapshot current)
    {
        RequireStandard(current);
        if (expected != current)
            throw new UnauthorizedAccessException("CEF launch token changed.");
    }
}

internal static class CefProcessTokenObserver
{
    internal static CefProcessTokenObservation Observe(SafeProcessHandle process, ClientIdentity expected) =>
        ReadSnapshot(process, expected, inspectLaunchPrivileges: false).Token;

    internal static CefLaunchTokenSnapshot ObserveLaunchToken(SafeProcessHandle process, ClientIdentity expected) =>
        ReadSnapshot(process, expected, inspectLaunchPrivileges: true);

    private static CefLaunchTokenSnapshot ReadSnapshot(SafeProcessHandle process, ClientIdentity expected,
        bool inspectLaunchPrivileges)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(expected);
        if (Native.ObserveProcess(process) != expected)
            throw new UnauthorizedAccessException("CEF token process identity mismatch.");

        using var token = OpenQueryToken(process);
        var before = ReadFixed<TokenStatistics>(token, 10); // TokenStatistics
        if (before.TokenType != 1)
            throw new UnauthorizedAccessException("Expected a primary process token.");
        using var identity = new WindowsIdentity(token.DangerousGetHandle());
        if (!string.Equals(identity.User?.Value, expected.UserSid, StringComparison.OrdinalIgnoreCase) ||
            ReadFixed<uint>(token, 12) != (uint)expected.SessionId)
            throw new UnauthorizedAccessException("CEF token account or session mismatch.");

        uint integrity = ReadIntegrity(token);
        bool restricted = IsTokenRestricted(token);
        bool appContainer = ReadFlag(token, 29); // TokenIsAppContainer
        bool elevated = ReadFlag(token, 20); // TokenElevation
        uint elevationType = ReadFixed<uint>(token, 18); // TokenElevationType
        if (elevationType is < 1 or > 3)
            throw new UnauthorizedAccessException("Unknown token elevation type.");

        bool administratorGroup = inspectLaunchPrivileges && HasAdministratorGroup(token);
        bool standardPrivileges = inspectLaunchPrivileges && HasOnlyStandardPrivileges(token);
        var after = ReadFixed<TokenStatistics>(token, 10);
        // Reopen the token of the same retained process, not a PID. A
        // replaced or modified token must not be accepted as this snapshot.
        using var current = OpenQueryToken(process);
        var currentStats = ReadFixed<TokenStatistics>(current, 10);
        if (!SameToken(before, after) || !SameToken(after, currentStats) ||
            Native.ObserveProcess(process) != expected)
            throw new UnauthorizedAccessException("CEF token changed during observation.");
        return new(new(expected, integrity, restricted, appContainer, elevated, elevationType),
            after.AuthenticationId, after.TokenId, after.ModifiedId, administratorGroup, standardPrivileges);
    }

    // Inspect raw TokenGroups, including disabled and deny-only SIDs.
    // WindowsIdentity.Groups alone may omit groups that are not enabled.
    private static bool HasAdministratorGroup(SafeAccessTokenHandle token)
    {
        var (buffer, length) = ReadVariable(token, 2); // TokenGroups
        try
        {
            int count = Marshal.ReadInt32(buffer);
            int entrySize = IntPtr.Size * 2;
            if (count < 0 || count > 4096 || (long)IntPtr.Size + (long)count * entrySize > length)
                throw new UnauthorizedAccessException("Invalid token group inventory.");
            bool administrator = false;
            for (int i = 0; i < count; i++)
            {
                IntPtr sid = Marshal.ReadIntPtr(buffer, IntPtr.Size + i * entrySize);
                long offset = sid.ToInt64() - buffer.ToInt64();
                if (offset < IntPtr.Size + (long)count * entrySize || offset > length - 8L ||
                    Marshal.ReadByte(sid) != 1)
                    throw new UnauthorizedAccessException("Invalid token group SID.");
                int subAuthorities = Marshal.ReadByte(sid, 1);
                int sidLength = 8 + 4 * subAuthorities;
                if (subAuthorities > 15 || offset > length - sidLength)
                    throw new UnauthorizedAccessException("Invalid token group SID length.");
                byte[] bytes = new byte[sidLength];
                Marshal.Copy(sid, bytes, 0, sidLength);
                string value = new SecurityIdentifier(bytes, 0).Value;
                // Builtin Administrators, domain Administrators and
                // Enterprise/Schema Admins are refused even if deny-only.
                administrator |= value == "S-1-5-32-544" ||
                    (value.StartsWith("S-1-5-21-", StringComparison.Ordinal) &&
                        (value.EndsWith("-512", StringComparison.Ordinal) ||
                         value.EndsWith("-518", StringComparison.Ordinal) ||
                         value.EndsWith("-519", StringComparison.Ordinal)));
            }
            return administrator;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool HasOnlyStandardPrivileges(SafeAccessTokenHandle token)
    {
        // Conservative allowlist of ordinary desktop privileges, including
        // disabled privileges. Unknown or additional privileges fail closed.
        var allowed = new HashSet<long>();
        foreach (string name in new[] { "SeChangeNotifyPrivilege", "SeShutdownPrivilege",
            "SeUndockPrivilege", "SeIncreaseWorkingSetPrivilege", "SeTimeZonePrivilege" })
        {
            if (!LookupPrivilegeValueW(null, name, out long luid))
                throw new Win32Exception();
            allowed.Add(luid);
        }
        var (buffer, length) = ReadVariable(token, 3); // TokenPrivileges
        try
        {
            int count = Marshal.ReadInt32(buffer);
            // LUID (8) + attributes (4), DWORD aligned, after count.
            if (count < 0 || count > 1024 || 4L + 12L * count != length)
                throw new UnauthorizedAccessException("Invalid token privilege inventory.");
            var seen = new HashSet<long>();
            bool standard = true;
            for (int i = 0; i < count; i++)
            {
                long luid = Marshal.ReadInt64(buffer, 4 + 12 * i);
                if (!seen.Add(luid))
                    throw new UnauthorizedAccessException("Duplicate token privilege.");
                standard &= allowed.Contains(luid);
            }
            return standard;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static (IntPtr Buffer, int Length) ReadVariable(SafeAccessTokenHandle token, int infoClass)
    {
        if (GetTokenInformation(token, infoClass, IntPtr.Zero, 0, out uint needed) ||
            Marshal.GetLastWin32Error() != 122 || needed < 4 || needed > 262144)
            throw new UnauthorizedAccessException("Invalid variable token information size.");
        IntPtr buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!GetTokenInformation(token, infoClass, buffer, needed, out uint returned))
                throw new Win32Exception();
            if (returned < 4 || returned > needed)
                throw new UnauthorizedAccessException("Invalid returned token information size.");
            return (buffer, (int)returned);
        }
        catch { Marshal.FreeHGlobal(buffer); throw; }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);

    private static SafeAccessTokenHandle OpenQueryToken(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, 8, out var token))
        {
            int error = Marshal.GetLastWin32Error();
            token?.Dispose();
            throw new Win32Exception(error);
        }
        return token;
    }

    private static bool SameToken(TokenStatistics a, TokenStatistics b) =>
        a.TokenId == b.TokenId && a.ModifiedId == b.ModifiedId && a.TokenType == b.TokenType;

    private static T ReadFixed<T>(SafeAccessTokenHandle token, int infoClass) where T : struct
    {
        int bytes = Marshal.SizeOf<T>();
        IntPtr buffer = Marshal.AllocHGlobal(bytes);
        try
        {
            if (!GetTokenInformation(token, infoClass, buffer, (uint)bytes, out uint returned))
                throw new Win32Exception();
            if (returned != (uint)bytes)
                throw new UnauthorizedAccessException("Unexpected token information length.");
            return Marshal.PtrToStructure<T>(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool ReadFlag(SafeAccessTokenHandle token, int infoClass) =>
        ReadFixed<uint>(token, infoClass) switch
        {
            0 => false,
            1 => true,
            _ => throw new UnauthorizedAccessException("Unknown token flag value.")
        };

    private static uint ReadIntegrity(SafeAccessTokenHandle token)
    {
        // TokenIntegrityLevel = TOKEN_MANDATORY_LABEL, whose first field is a
        // pointer to the mandatory label SID inside the returned buffer.
        if (GetTokenInformation(token, 25, IntPtr.Zero, 0, out uint needed) ||
            Marshal.GetLastWin32Error() != 122 || needed < (uint)(IntPtr.Size + 4 + 12) || needed > 4096)
            throw new UnauthorizedAccessException("Invalid mandatory label length.");
        IntPtr buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!GetTokenInformation(token, 25, buffer, needed, out uint returned))
                throw new Win32Exception();
            if (returned < (uint)(IntPtr.Size + 4 + 12) || returned > needed)
                throw new UnauthorizedAccessException("Incomplete mandatory label.");
            IntPtr sid = Marshal.ReadIntPtr(buffer);
            long offset = sid.ToInt64() - buffer.ToInt64();
            // Require one-subauthority S-1-16-<RID>, entirely within the buffer.
            if (offset < IntPtr.Size + 4 || offset > (long)returned - 12 ||
                Marshal.ReadByte(sid, 0) != 1 || Marshal.ReadByte(sid, 1) != 1)
                throw new UnauthorizedAccessException("Invalid mandatory label SID.");
            for (int i = 2; i < 7; i++)
                if (Marshal.ReadByte(sid, i) != 0)
                    throw new UnauthorizedAccessException("Invalid mandatory label authority.");
            if (Marshal.ReadByte(sid, 7) != 16)
                throw new UnauthorizedAccessException("Invalid mandatory label authority.");
            return unchecked((uint)Marshal.ReadInt32(sid, 8));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenStatistics
    {
        public long TokenId, AuthenticationId, ExpirationTime;
        public int TokenType, ImpersonationLevel;
        public uint DynamicCharged, DynamicAvailable, GroupCount, PrivilegeCount;
        public long ModifiedId;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access,
        out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass,
        IntPtr data, uint length, out uint returned);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsTokenRestricted(SafeAccessTokenHandle token);
}
