// SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Veil.VolumeBroker;

// Read-only point-in-time host observation. Registry UBR is servicing
// metadata, not proof of the provenance of loaded images.
internal static class CefSystemBaselineHostObserver
{
    private const string VersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    internal static CefSystemBaselineHost Observe()
    {
        if (!OperatingSystem.IsWindows() ||
            RuntimeInformation.OSArchitecture != Architecture.X64 ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Expected native Windows x64.");

        // Compare the native OS build with machine-only 64-bit registry
        // metadata, twice. No environment or user-registry fallback.
        return RequireStable(ReadOnce);
    }

    internal static CefSystemBaselineHost RequireStable(Func<CefSystemBaselineHost> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var first = read();
        RequireCurrent(first, read());
        return first;
    }

    internal static void RequireCurrent(CefSystemBaselineHost expected, CefSystemBaselineHost current)
    {
        if (expected is null || current is null || expected.Architecture != "x64" ||
            expected.Build <= 0 || expected.UpdateRevision < 0 || expected != current)
            throw new UnauthorizedAccessException("System baseline host changed or is invalid.");
    }

    internal static CefSystemBaselineHost Reconcile(int nativeBuild, object? registryBuild,
        RegistryValueKind buildKind, object? revision, RegistryValueKind revisionKind)
    {
        if (nativeBuild <= 0 || buildKind != RegistryValueKind.String ||
            registryBuild is not string buildText || buildText.Length is 0 or > 10 ||
            !int.TryParse(buildText, NumberStyles.None, CultureInfo.InvariantCulture, out int build) ||
            buildText != build.ToString(CultureInfo.InvariantCulture) || build != nativeBuild ||
            revisionKind != RegistryValueKind.DWord || revision is not int ubr || ubr < 0)
            throw new UnauthorizedAccessException("Windows build or update revision unavailable or inconsistent.");
        return new("x64", build, ubr);
    }

    private static CefSystemBaselineHost ReadOnce()
    {
        var version = new NativeVersion { Size = (uint)Marshal.SizeOf<NativeVersion>(), ServicePack = "" };
        if (RtlGetVersion(ref version) != 0 || version.Major != 10 || version.Minor != 0 ||
            version.Build == 0 || version.Build > int.MaxValue)
            throw new UnauthorizedAccessException("Native Windows version unavailable or unsupported.");
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(VersionKey, writable: false) ??
            throw new UnauthorizedAccessException("Windows servicing metadata unavailable.");
        return Reconcile((int)version.Build,
            key.GetValue("CurrentBuildNumber", null, RegistryValueOptions.DoNotExpandEnvironmentNames),
            key.GetValueKind("CurrentBuildNumber"),
            key.GetValue("UBR", null, RegistryValueOptions.DoNotExpandEnvironmentNames),
            key.GetValueKind("UBR"));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeVersion
    {
        public uint Size, Major, Minor, Build, Platform;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string ServicePack;
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int RtlGetVersion(ref NativeVersion version);
}
