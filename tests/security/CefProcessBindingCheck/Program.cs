// SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using Veil.VolumeBroker;

if (args is ["--child"])
{
    Console.WriteLine("ready");
    return Console.ReadLine() == "exit" ? 0 : 1;
}

using var owner = Process.GetCurrentProcess();
var identity = Native.ObserveProcess(owner.SafeHandle);
using var child = Process.Start(new ProcessStartInfo
{
    FileName = Environment.ProcessPath ?? throw new InvalidOperationException("No test apphost."),
    UseShellExecute = false,
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    ArgumentList = { "--child" },
    CreateNoWindow = true,
}) ?? throw new InvalidOperationException("Test child did not start.");

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static void Reject(Action action)
{
    try { action(); }
    catch (Exception error) when (error is UnauthorizedAccessException or System.ComponentModel.Win32Exception)
    { return; }
    throw new InvalidOperationException("Invalid child nomination accepted.");
}

try
{
    Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "ready",
        "Test child failed to become ready.");
    var expected = Native.ObserveProcess(child.SafeHandle);
    var binding = Native.DuplicateNominatedChild(identity, owner.SafeHandle,
        child.SafeHandle.DangerousGetHandle());
    using var retained = binding.Handle;
    Check(binding.Identity == expected && Native.IsRunning(retained), "Child identity not retained.");
    Reject(() => Native.DuplicateNominatedChild(identity, owner.SafeHandle, IntPtr.Zero).Handle.Dispose());
    Reject(() => Native.DuplicateNominatedChild(identity, owner.SafeHandle, owner.SafeHandle.DangerousGetHandle()).Handle.Dispose());
    Reject(() => Native.DuplicateNominatedChild(identity with { CreatedFileTime = identity.CreatedFileTime + 1 },
        owner.SafeHandle, child.SafeHandle.DangerousGetHandle()).Handle.Dispose());
    Reject(() => Native.DuplicateNominatedChild(identity, child.SafeHandle, child.SafeHandle.DangerousGetHandle()).Handle.Dispose());
    // A non-process handle must never be accepted as an executable child.
    using var signal = new EventWaitHandle(false, EventResetMode.ManualReset);
    Reject(() => Native.DuplicateNominatedChild(identity, owner.SafeHandle,
        signal.SafeWaitHandle.DangerousGetHandle()).Handle.Dispose());
    await child.StandardInput.WriteLineAsync("exit");
    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Check(child.ExitCode == 0 && !Native.IsRunning(retained), "Exited child still considered live.");
    Reject(() => Native.DuplicateNominatedChild(identity, owner.SafeHandle, child.SafeHandle.DangerousGetHandle()).Handle.Dispose());
    Console.WriteLine("PASS: synthetic Windows child handle duplication, generation binding and exit detection");
    return 0;
}
finally
{
    if (!child.HasExited)
    {
        child.Kill();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
}
