// SPDX-License-Identifier: MPL-2.0
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Veil.VolumeBroker;

// Diagnostic role observation only. Command lines are mutable process data;
// a recognized role is neither a sandbox verdict nor a launch grant.
internal sealed record CefCommandRoleObservation(ClientIdentity Identity, CefObservedRole Role);

internal static class CefCommandRoleObserver
{
    private static readonly HashSet<string> Disallowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "no-sandbox", "disable-gpu-sandbox", "disable-setuid-sandbox",
        "disable-namespace-sandbox", "disable-seccomp-filter-sandbox",
        "disable-sandbox", "disable-sandbox-for-testing", "no-zygote",
        "single-process", "in-process-gpu", "no-zygote-sandbox",
        "disable-win32k-lockdown", "disable-appcontainer", "disable-renderer-sandbox",
        "allow-no-sandbox-job", "renderer-cmd-prefix", "gpu-launcher", "utility-cmd-prefix"
    };

    internal static CefObservedRole Classify(IReadOnlyList<string> args, bool isRoot)
    {
        if (args.Count is < 1 or > 1024 || string.IsNullOrWhiteSpace(args[0]))
            return CefObservedRole.Unknown;
        string? type = null, subtype = null, sandboxType = null;
        for (int i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.IsNullOrWhiteSpace(arg) || arg.Contains('\0') || arg == "--")
                return CefObservedRole.Unknown;
            // Chromium accepts Windows slash and single-dash switches as
            // well. Inspect all prefixes for disallowed options, and require
            // canonical syntax for role-bearing switches.
            int prefix = arg.StartsWith("--", StringComparison.Ordinal) ? 2 :
                arg[0] is '-' or '/' ? 1 : 0;
            if (prefix == 0) return CefObservedRole.Unknown;
            string body = arg[prefix..];
            int equal = body.IndexOf('=');
            string name = equal < 0 ? body : body[..equal];
            string? value = equal < 0 ? null : body[(equal + 1)..];
            if (name.Length == 0 || name[0] is '-' or '/' || Disallowed.Contains(name))
                return CefObservedRole.Unknown;
            // Reject common security-sensitive feature overrides; this is
            // deliberately not an exhaustive Chromium switch allowlist.
            if (name.Equals("disable-features", StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(value) || value.Contains("sandbox", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("appcontainer", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("win32k", StringComparison.OrdinalIgnoreCase)))
                return CefObservedRole.Unknown;
            if (name.Equals("type", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("utility-sub-type", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("service-sandbox-type", StringComparison.OrdinalIgnoreCase))
            {
                if (prefix != 2 || string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
                    return CefObservedRole.Unknown;
                switch (name)
                {
                    case "type" when type is null: type = value; break;
                    case "utility-sub-type" when subtype is null: subtype = value; break;
                    case "service-sandbox-type" when sandboxType is null: sandboxType = value; break;
                    default: return CefObservedRole.Unknown;
                }
            }
        }
        if (sandboxType is not null && (sandboxType.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            sandboxType.Equals("no-sandbox", StringComparison.OrdinalIgnoreCase) ||
            sandboxType.Equals("unsandboxed", StringComparison.OrdinalIgnoreCase) ||
            sandboxType.Equals("none_and_elevated", StringComparison.OrdinalIgnoreCase)))
            return CefObservedRole.Unknown;
        if (isRoot)
            return type is null && subtype is null && sandboxType is null
                ? CefObservedRole.Browser : CefObservedRole.Unknown;
        if (type != "utility" && (subtype is not null || sandboxType is not null))
            return CefObservedRole.Unknown;
        return type switch
        {
            "renderer" => CefObservedRole.Renderer,
            "gpu-process" => CefObservedRole.Gpu,
            "utility" when subtype == "network.mojom.NetworkService" => CefObservedRole.Network,
            "utility" when subtype is not null => CefObservedRole.Utility,
            _ => CefObservedRole.Unknown
        };
    }

    internal static CefCommandRoleObservation Observe(SafeProcessHandle process,
        ClientIdentity expected, ClientIdentity root)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(root);
        if (!Environment.Is64BitProcess || Native.ObserveProcess(process) != expected ||
            (expected.ProcessId == root.ProcessId && expected != root))
            throw new UnauthorizedAccessException("CEF role process identity mismatch.");
        string first = ReadCommandLine(process);
        var args = Split(first);
        var role = string.Equals(args[0], expected.ImagePath, StringComparison.OrdinalIgnoreCase)
            ? Classify(args, expected == root) : CefObservedRole.Unknown;
        if (!string.Equals(first, ReadCommandLine(process), StringComparison.Ordinal) ||
            Native.ObserveProcess(process) != expected)
            throw new UnauthorizedAccessException("CEF role observation changed.");
        // Never return or log the command line, argument values or paths.
        return new(expected, role);
    }

    private static string ReadCommandLine(SafeProcessHandle process)
    {
        const int infoClass = 60, header = 16; // x64 UNICODE_STRING
        int status = NtQueryInformationProcess(process, infoClass, IntPtr.Zero, 0, out uint needed);
        if (status is not (unchecked((int)0xC0000004) or unchecked((int)0xC0000023) or
            unchecked((int)0x80000005)) || needed < header || needed > 131072)
            throw new UnauthorizedAccessException("CEF command line observation unavailable.");
        IntPtr buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (NtQueryInformationProcess(process, infoClass, buffer, needed, out uint returned) != 0 ||
                returned < header || returned > needed)
                throw new UnauthorizedAccessException("CEF command line observation failed.");
            int length = unchecked((ushort)Marshal.ReadInt16(buffer));
            int maximum = unchecked((ushort)Marshal.ReadInt16(buffer, 2));
            IntPtr value = Marshal.ReadIntPtr(buffer, 8);
            long offset = value.ToInt64() - buffer.ToInt64();
            if (length == 0 || length % 2 != 0 || maximum < length ||
                offset < header || offset > (long)returned - length)
                throw new UnauthorizedAccessException("Invalid CEF command line buffer.");
            string? result = Marshal.PtrToStringUni(value, length / 2);
            if (string.IsNullOrEmpty(result) || result.Contains('\0'))
                throw new UnauthorizedAccessException("Invalid CEF command line.");
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string[] Split(string commandLine)
    {
        IntPtr argv = CommandLineToArgvW(commandLine, out int count);
        if (argv == IntPtr.Zero) throw new Win32Exception();
        try
        {
            if (count is < 1 or > 1024)
                throw new UnauthorizedAccessException("Invalid CEF argument count.");
            var result = new string[count];
            for (int i = 0; i < count; i++)
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))
                    ?? throw new UnauthorizedAccessException("Invalid CEF argument.");
            return result;
        }
        finally { LocalFree(argv); }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass,
        IntPtr data, uint length, out uint returned);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
