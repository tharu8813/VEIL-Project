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
                using (var identity = new WindowsIdentity(token))
                {
                    bool admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                    foreach (IdentityReference group in identity.Groups)
                        if (group.Value == "S-1-5-32-544") admin = true;
                    var observed = new ProcessObservation
                    {
                        ProcessId = checked((int)pid),
                        ImagePath = path.ToString(),
                        UserSid = identity.User.Value,
                        Administrator = admin,
                        SessionId = ReadInt(token, 12), // TokenSessionId
                        ElevationType = ReadInt(token, 18), // TokenElevationType
                        Elevated = ReadInt(token, 20) != 0, // TokenElevation
                        IntegritySid = ReadIntegrity(token)
                    };
                    if (WaitForSingleObject(handle, 0) != 258)
                        throw new InvalidOperationException("Process exited during observation.");
                    return observed;
                }
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

        private static string ReadIntegrity(IntPtr token)
        {
            int size;
            if (GetTokenInformation(token, 25, IntPtr.Zero, 0, out size) ||
                Marshal.GetLastWin32Error() != 122 || size < IntPtr.Size || size > 65536)
                throw new InvalidOperationException("Invalid token integrity information size.");
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int actual;
                if (!GetTokenInformation(token, 25, buffer, size, out actual) || actual > size || actual < IntPtr.Size)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
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
