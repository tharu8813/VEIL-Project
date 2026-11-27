using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Veil.Desktop;

/// <summary>One-shot, authenticated Rust age store. No keys cached in the browser session.</summary>
internal sealed class PasswordStore(string profileRoot)
{
    private const int Limit = 1024 * 1024;
    private readonly string path = Path.Combine(profileRoot, "passwords.age");
    private readonly string tool = Path.Combine(AppContext.BaseDirectory, "veil-vault.exe");

    internal bool Exists => File.Exists(path);
    internal bool Available => File.Exists(tool);

    internal Task<List<SavedLogin>> ReadAsync(char[] password) => ReadFileAsync(path, password);

    internal Task<List<SavedLogin>> ReadBackupAsync(string source, char[] password) => ReadFileAsync(source, password);

    private async Task<List<SavedLogin>> ReadFileAsync(string source, char[] password)
    {
        var bytes = await RunAsync("passwords-read", password, null, source);
        try
        {
            var entries = JsonSerializer.Deserialize<List<SavedLogin>>(bytes);
            if (entries is null || !Valid(entries))
                throw new InvalidDataException("Invalid credential store");
            return entries;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal Task WriteAsync(char[] password, List<SavedLogin> entries, bool replace) =>
        WriteFileAsync(path, password, entries, replace);

    internal Task BackupAsync(string destination, char[] password, List<SavedLogin> entries) =>
        WriteFileAsync(destination, password, entries, replace: false);

    internal async Task RotateAsync(char[] current, char[] replacement)
    {
        var wire = EncodePassphrase(replacement);
        try
        {
            var reply = await RunAsync("passwords-rotate", current, wire, path);
            CryptographicOperations.ZeroMemory(reply);
        }
        finally { CryptographicOperations.ZeroMemory(wire); }
    }

    // Restore adds only absent origin/account pairs. Existing credentials always win.
    internal static List<SavedLogin> MergeBackup(List<SavedLogin> current, List<SavedLogin> backup)
    {
        var seen = current.Select(e => (e.Origin, e.UserName)).ToHashSet();
        var merged = current.Concat(backup.Where(e => seen.Add((e.Origin, e.UserName)))).ToList();
        if (!Valid(merged)) throw new InvalidDataException("Merged credential store exceeds limits");
        return merged;
    }

    private async Task WriteFileAsync(string destination, char[] password, List<SavedLogin> entries, bool replace)
    {
        if (!Valid(entries)) throw new InvalidDataException("Invalid credential store");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entries);
        try
        {
            if (bytes.Length > Limit) throw new InvalidDataException("Credential store is too large");
            var reply = await RunAsync(replace ? "passwords-replace" : "passwords-create", password, bytes, destination);
            CryptographicOperations.ZeroMemory(reply);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool Valid(List<SavedLogin> entries)
    {
        var seen = new HashSet<(string, string)>();
        return entries.Count <= 1000 && entries.All(entry => entry is not null &&
            entry.Origin is not null && SitePermissionPolicy.Origin(entry.Origin) == entry.Origin &&
            entry.UserName is { Length: > 0 and <= 512 } &&
            entry.Password is { Length: > 0 and <= 2048 } &&
            seen.Add((entry.Origin, entry.UserName)));
    }

    private static byte[] EncodePassphrase(char[] password)
    {
        byte[] secret = Encoding.UTF8.GetBytes(password);
        try
        {
            var wire = new byte[secret.Length * 2 + 1];
            const string digits = "0123456789abcdef";
            for (int i = 0; i < secret.Length; i++)
            {
                wire[i * 2] = (byte)digits[secret[i] >> 4];
                wire[i * 2 + 1] = (byte)digits[secret[i] & 15];
            }
            wire[^1] = (byte)'\n';
            return wire;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    private async Task<byte[]> RunAsync(string command, char[] password, byte[]? payload, string target)
    {
        if (!Available) throw new IOException("Password store unavailable");
        var info = new ProcessStartInfo(tool)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        info.ArgumentList.Add(command);
        info.ArgumentList.Add(target);
        using var process = Process.Start(info) ?? throw new IOException("Password store unavailable");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        byte[] wire = [];
        try
        {
            // Never put the passphrase in an argument, environment variable, or diagnostic log.
            wire = EncodePassphrase(password);
            var read = ReadBoundedAsync(process.StandardOutput.BaseStream, token);
            var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
            var input = process.StandardInput.BaseStream;
            await input.WriteAsync(wire, token);
            if (payload is not null) await input.WriteAsync(payload, token);
            await input.FlushAsync(token);
            // Close the raw pipe; no StreamWriter BOM or text framing is used.
            input.Close();
            await process.WaitForExitAsync(token);
            var bytes = await read;
            try
            {
                await errors;
                if (process.ExitCode != 0) throw new InvalidDataException("Password store authentication or I/O failed");
                return bytes;
            }
            catch { CryptographicOperations.ZeroMemory(bytes); throw; }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wire);
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken token)
    {
        byte[] buffer = new byte[Limit + 1];
        try
        {
            int used = 0;
            while (used < buffer.Length)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(used), token);
                if (count == 0) return buffer.AsSpan(0, used).ToArray();
                used += count;
            }
            throw new InvalidDataException("Credential store is too large");
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
}
