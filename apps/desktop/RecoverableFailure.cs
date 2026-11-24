using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Veil.Desktop;

/// <summary>
/// Failures a UI action can report and survive: storage, vault and credential tools, the engine (COM,
/// disposed views), timeouts and malformed data. An exception escaping an <c>async void</c> handler
/// terminates the browser, so every such handler catches this set. Programming errors
/// (NullReference, InvalidCast, IndexOutOfRange, OutOfMemory) are deliberately not included.
/// </summary>
internal static class RecoverableFailure
{
    internal static bool Is(Exception error) => error is
        IOException or InvalidDataException or UnauthorizedAccessException or
        InvalidOperationException or COMException or JsonException or
        OperationCanceledException or TimeoutException or Win32Exception or
        HttpRequestException or FormatException or OverflowException or ArgumentException or
        NotSupportedException;
}
