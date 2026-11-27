// SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using Veil.Desktop;

// Child mode uses only an isolated synthetic marker, never a browser/profile.
if (Environment.GetEnvironmentVariable("VEIL_SYNTHETIC_CHILD_MARKER") is { } marker)
{
    File.WriteAllText(marker, "started");
    Thread.Sleep(Timeout.Infinite);
    return 0;
}
static void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}
static void Reject(Action action)
{
    try { action(); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return; }
    throw new InvalidOperationException("Expected refusal.");
}
var work = Path.Combine(Path.GetTempPath(), "veil-containment-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
try
{
    string image = Environment.ProcessPath ?? throw new InvalidOperationException("Missing test apphost.");
    string markerPath = Path.Combine(work, "started.txt");
    var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        if (entry.Key is string key && entry.Value is string value)
            environment[key] = value;
    environment["VEIL_SYNTHETIC_CHILD_MARKER"] = markerPath;
    Check(CefContainedProcess.BuildEnvironmentBlock(new Dictionary<string,string?> { ["B"]="2", ["a"]="1" }) ==
        "a=1\0B=2\0\0", "Environment ordering or termination invalid.");
    Reject(() => CefContainedProcess.BuildEnvironmentBlock(new Dictionary<string,string?> { ["a"]="1", ["A"]="2" }));
    Reject(() => CefContainedProcess.BuildEnvironmentBlock(new Dictionary<string,string?> { ["a"]="x\0y" }));
    using (var denied = CefContainedProcess.CreateSuspended(image, environment))
    {
        Check(denied.IsAlive && denied.NominatedHandle > 0 && !File.Exists(markerPath), "Child was not initially suspended.");
        Reject(() => denied.ResumeAfterVerification(() => false));
        Check(!denied.IsAlive && !File.Exists(markerPath), "Denied child started.");
    }
    using (var child = CefContainedProcess.CreateSuspended(image, environment))
    {
        Check(child.IsAlive && !child.WaitForExit(50) && !File.Exists(markerPath), "Child suspension failed.");
        int pid = GetProcessId(new IntPtr(child.NominatedHandle));
        using var observer = Process.GetProcessById(pid);
        // Retain an independent process handle before closing the job.
        _ = observer.SafeHandle;
        child.ResumeAfterVerification(() => true);
        Check(SpinWait.SpinUntil(() => File.Exists(markerPath), TimeSpan.FromSeconds(10)), "Resumed synthetic child did not start.");
        child.Dispose();
        Check(observer.WaitForExit(10000), "Child survived job close.");
    }
    Console.WriteLine("PASS: actual synthetic Windows suspended launch, denied resume and job-close termination");
    return 0;
}
finally { Directory.Delete(work, recursive: true); }

[System.Runtime.InteropServices.DllImport("kernel32.dll")]
static extern int GetProcessId(IntPtr process);
