// SPDX-License-Identifier: MPL-2.0
// Explicit disposable-VM registration only. No replacement or deletion.
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Veil.Preflight
{
    public static class BootstrapRegistry
    {
        private const string Descriptor = "O:BAG:BAD:P(A;;KA;;;SY)(A;;KA;;;BA)(A;;KR;;;BU)";
        private const int Read64 = 0x20119, Full64 = 0xf013f;
        private const int OpenLink = 8;

        public static void ValidateDescriptor(byte[] bytes)
        {
            var expected = new RawSecurityDescriptor(Descriptor);
            var observed = new RawSecurityDescriptor(bytes, 0);
            var sections = AccessControlSections.Owner | AccessControlSections.Access;
            if (!String.Equals(expected.GetSddlForm(sections), observed.GetSddlForm(sections), StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected bootstrap registry owner or DACL.");
        }

        private static void Ordinary(RegistryKey key)
        {
            foreach (string name in key.GetValueNames())
                if (String.Equals(name, "SymbolicLinkValue", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Registry link refused.");
        }

        private static void Protected(RegistryKey key)
        {
            Ordinary(key);
            ValidateDescriptor(key.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
                .GetSecurityDescriptorBinaryForm());
        }

        private static RegistryKey Open(RegistryKey parent, string name)
        {
            SafeRegistryHandle handle;
            int code = RegOpenKeyEx(parent.Handle, name, OpenLink, Read64, out handle);
            if (code != 0) { if (handle != null) handle.Dispose(); throw new Win32Exception(code); }
            try { return RegistryKey.FromHandle(handle, RegistryView.Registry64); }
            catch { handle.Dispose(); throw; }
        }

        private static RegistryKey CreateNew(RegistryKey parent, string name)
        {
            var security = new RawSecurityDescriptor(Descriptor);
            var bytes = new byte[security.BinaryLength];
            security.GetBinaryForm(bytes, 0);
            IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
                var attributes = new SecurityAttributes
                {
                    Length = Marshal.SizeOf(typeof(SecurityAttributes)), Descriptor = pointer
                };
                SafeRegistryHandle handle;
                int disposition;
                int code = RegCreateKeyEx(parent.Handle, name, 0, null, 0, Full64,
                    ref attributes, out handle, out disposition);
                if (code != 0 || disposition != 1)
                {
                    if (handle != null) handle.Dispose();
                    if (code != 0) throw new Win32Exception(code);
                    throw new InvalidOperationException("Existing registry key refused; preserve it.");
                }
                try { return RegistryKey.FromHandle(handle, RegistryView.Registry64); }
                catch { handle.Dispose(); throw; }
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }

        public static void Create(string root, string guid, string id)
        {
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var software = Open(machine, "SOFTWARE"))
            {
                Ordinary(software);
                // Read-only SOFTWARE handle cannot create a subkey. Reopen
                // with exact creation rights and OPEN_LINK, never a user hive.
                SafeRegistryHandle handle;
                int code = RegOpenKeyEx(machine.Handle, "SOFTWARE", OpenLink, Read64 | 4, out handle);
                if (code != 0) { if (handle != null) handle.Dispose(); throw new Win32Exception(code); }
                using (handle)
                using (var writable = RegistryKey.FromHandle(handle, RegistryView.Registry64))
                {
                    Ordinary(writable);
                    using (var veil = CreateNew(writable, "VEIL"))
                    {
                        Protected(veil);
                        using (var leaf = CreateNew(veil, "CefBootstrap"))
                        {
                            Protected(leaf);
                            leaf.SetValue("ProductRoot", root, RegistryValueKind.String);
                            leaf.SetValue("VolumeGuid", guid, RegistryValueKind.String);
                            leaf.SetValue("VolumeId", id, RegistryValueKind.String);
                            leaf.Flush();
                            Protected(leaf);
                        }
                        veil.Flush();
                    }
                }
            }
            Verify(root, guid, id);
        }

        public static void Verify(string root, string guid, string id)
        {
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var software = Open(machine, "SOFTWARE"))
            using (var veil = Open(software, "VEIL"))
            using (var leaf = Open(veil, "CefBootstrap"))
            {
                Ordinary(software);
                Protected(veil);
                Protected(leaf);
                var children = veil.GetSubKeyNames();
                if (veil.ValueCount != 0 || children.Length != 1 || children[0] != "CefBootstrap" ||
                    leaf.SubKeyCount != 0 || leaf.ValueCount != 3)
                    throw new InvalidOperationException("Unexpected bootstrap registry layout.");
                RequireValue(leaf, "ProductRoot", root);
                RequireValue(leaf, "VolumeGuid", guid);
                RequireValue(leaf, "VolumeId", id);
                Protected(veil);
                Protected(leaf);
            }
        }

        private static void RequireValue(RegistryKey key, string name, string expected)
        {
            if (key.GetValueKind(name) != RegistryValueKind.String ||
                !String.Equals(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                    expected, StringComparison.Ordinal))
                throw new InvalidOperationException("Bootstrap registry value mismatch: " + name);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr Descriptor;
            public int Inherit;
        }

        [DllImport("advapi32.dll", CharSet=CharSet.Unicode, EntryPoint="RegOpenKeyExW")]
        private static extern int RegOpenKeyEx(SafeRegistryHandle parent, string name, int options,
            int access, out SafeRegistryHandle key);

        [DllImport("advapi32.dll", CharSet=CharSet.Unicode, EntryPoint="RegCreateKeyExW")]
        private static extern int RegCreateKeyEx(SafeRegistryHandle parent, string name, int reserved,
            string keyClass, int options, int access, ref SecurityAttributes attributes,
            out SafeRegistryHandle key, out int disposition);
    }
}
