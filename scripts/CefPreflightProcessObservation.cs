// SPDX-License-Identifier: MPL-2.0
// Windows PowerShell 5.1 compatible diagnostic helper. No process mutation.
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Veil.Preflight
{
    public sealed class ProcessObservation
    {
        public int ProcessId { get; private set; }
        public int SessionId { get; private set; }
        public string ImagePath { get; private set; }
        public string UserSid { get; private set; }
        public string IntegritySid { get; private set; }
        public int ElevationType { get; private set; }
        public bool Elevated { get; private set; }
        public bool Administrator { get; private set; }

        public static ProcessObservation Inspect(Process process)
        {
            // Use the handle retained by Process.Start, not a process looked
            // up later from a PID or a name. A vanished process is a failure.
            IntPtr handle = process.Handle;
            if (WaitForSingleObject(handle, 0) != 258)
                throw new InvalidOperationException("Process is not live.");
            uint pid = GetProcessId(handle);
            if (pid == 0 || pid != (uint)process.Id)
                throw new InvalidOperationException("Process identity unavailable.");
            var path = new StringBuilder(32768);
            uint length = (uint)path.Capacity;
            if (!QueryFullProcessImageName(handle, 0, path, ref length))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr token;
            if (!OpenProcessToken(handle, 0x0008, out token))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                // TOKEN_QUERY is sufficient for these observations. Do not
                // construct WindowsIdentity/WindowsPrincipal from this handle:
                // Framework implementations may require token duplication or
                // impersonation rights, neither needed for this inspection.
                var observed = new ProcessObservation
                {
                    ProcessId = checked((int)pid),
                    ImagePath = path.ToString(),
                    UserSid = ReadSid(token, 1), // TokenUser
                    Administrator = HasAdministratorGroup(token),
                    SessionId = ReadInt(token, 12), // TokenSessionId
                    ElevationType = ReadInt(token, 18), // TokenElevationType
                    Elevated = ReadInt(token, 20) != 0, // TokenElevation
                    IntegritySid = ReadSid(token, 25) // TokenIntegrityLevel
                };
                if (WaitForSingleObject(handle, 0) != 258)
                    throw new InvalidOperationException("Process exited during observation.");
                return observed;
            }
            finally { CloseHandle(token); }
        }

        public void RequireStandard(string expectedPath, string expectedSid, int expectedSession)
        {
            if (!String.Equals(ImagePath, expectedPath, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(UserSid, expectedSid, StringComparison.Ordinal) ||
                SessionId != expectedSession || SessionId <= 0 ||
                Administrator || Elevated || ElevationType != 1 ||
                IntegritySid != "S-1-16-8192")
                throw new InvalidOperationException("Expected registered medium-integrity standard user process.");
        }

        private static int ReadInt(IntPtr token, int info)
        {
            int value, size;
            if (!GetTokenInformationInt(token, info, out value, 4, out size) || size != 4)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return value;
        }

        // TokenUser and TokenIntegrityLevel both begin with SID_AND_ATTRIBUTES.
        private static string ReadSid(IntPtr token, int info)
        {
            return ReadVariable<string>(token, info, delegate(IntPtr buffer, int size)
            {
                if (size < IntPtr.Size * 2)
                    throw new InvalidOperationException("Incomplete token SID structure.");
                return ReadBoundedSid(buffer, size, Marshal.ReadIntPtr(buffer));
            });
        }

        private static bool HasAdministratorGroup(IntPtr token)
        {
            // TOKEN_GROUPS: DWORD count followed by an aligned array of
            // SID_AND_ATTRIBUTES (pointer + DWORD + platform padding).
            // Inspect all groups, including deny-only Administrators in a
            // filtered UAC token. Do not use effective membership alone.
            return ReadVariable<bool>(token, 2, delegate(IntPtr buffer, int size)
            {
                if (size < IntPtr.Size)
                    throw new InvalidOperationException("Incomplete token group structure.");
                int count = Marshal.ReadInt32(buffer);
                int stride = IntPtr.Size * 2;
                if (count < 0 || count > (size - IntPtr.Size) / stride)
                    throw new InvalidOperationException("Invalid token group count.");
                bool administrator = false;
                for (int i = 0; i < count; i++)
                {
                    IntPtr sid = Marshal.ReadIntPtr(buffer, IntPtr.Size + i * stride);
                    if (ReadBoundedSid(buffer, size, sid) == "S-1-5-32-544")
                        administrator = true;
                }
                return administrator;
            });
        }

        private static string ReadBoundedSid(IntPtr buffer, int size, IntPtr sid)
        {
            long offset = sid.ToInt64() - buffer.ToInt64();
            if (offset < 0 || offset > size - 8)
                throw new InvalidOperationException("Token SID pointer outside query buffer.");
            // SID header is 8 bytes; each sub-authority is a DWORD.
            int subAuthorities = Marshal.ReadByte(sid, 1);
            if (subAuthorities > 15 || 8 + 4 * subAuthorities > size - offset)
                throw new InvalidOperationException("Token SID length outside query buffer.");
            return new SecurityIdentifier(sid).Value;
        }

        private static T ReadVariable<T>(IntPtr token, int info, Func<IntPtr, int, T> read)
        {
            int size;
            if (GetTokenInformation(token, info, IntPtr.Zero, 0, out size) ||
                Marshal.GetLastWin32Error() != 122 || size < IntPtr.Size || size > 1024 * 1024)
                throw new InvalidOperationException("Invalid token information size: " + info);
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int actual;
                if (!GetTokenInformation(token, info, buffer, size, out actual))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Token query failed: " + info);
                if (actual < IntPtr.Size || actual > size)
                    throw new InvalidOperationException("Invalid returned token information size.");
                return read(buffer, actual);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetProcessId(IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int info, IntPtr buffer, int length, out int returned);
        [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
        private static extern bool GetTokenInformationInt(IntPtr token, int info, out int value, int length, out int returned);
    }
}
