// SPDX-License-Identifier: MPL-2.0
using System.IO;

namespace Veil.Desktop;

// Path layout only: neither an authorization nor permission to start CEF.
// Never share the WebView2 data directory or the existing product scratch.
internal sealed class CefProfileLayout
{
    internal string ProductRoot { get; }
    internal string EngineRoot { get; }
    internal string Cache { get; }
    internal string Scratch { get; }
    internal string Logs { get; }
    internal string LogFile { get; }

    internal CefProfileLayout(string productRoot)
    {
        if (!Path.IsPathFullyQualified(productRoot))
            throw new ArgumentException("An absolute product profile path is required.", nameof(productRoot));
        ProductRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(productRoot));
        EngineRoot = Path.Combine(ProductRoot, "cef");
        Cache = Path.Combine(EngineRoot, "cache");
        Scratch = Path.Combine(EngineRoot, "temp");
        Logs = Path.Combine(EngineRoot, "logs");
        LogFile = Path.Combine(Logs, "cef.log");
    }

    internal IEnumerable<string> Directories => [EngineRoot, Cache, Scratch, Logs];

    // Immediate parent must be verified before every child preparation.
    internal IEnumerable<(string Parent, string Child)> PreparationSteps =>
        [(ProductRoot, EngineRoot), (EngineRoot, Cache), (EngineRoot, Scratch), (EngineRoot, Logs)];
}
