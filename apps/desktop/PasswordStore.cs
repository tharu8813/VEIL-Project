using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Veil.Desktop;

internal sealed record SavedLogin(string Origin, string UserName, string Password);

/// <summary>One-shot, authenticated Rust age store. No keys cached in the browser session.</summary>
internal sealed class PasswordStore(string profileRoot)
{
    private const int Limit = 1024 * 1024;
    private readonly string path = Path.Combine(profileRoot, "passwords.age");
    private readonly string tool = Path.Combine(AppContext.BaseDirectory, "veil-vault.exe");

    internal bool Exists => File.Exists(path);
    internal bool Available => File.Exists(tool);

    internal async Task<List<SavedLogin>> ReadAsync(char[] password)
    {
        var bytes = await RunAsync("passwords-read", password, null);
        try
        {
            var entries = JsonSerializer.Deserialize<List<SavedLogin>>(bytes);
            if (entries is null || !Valid(entries))
                throw new InvalidDataException("Invalid credential store");
            return entries;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal async Task WriteAsync(char[] password, List<SavedLogin> entries, bool replace)
    {
        if (!Valid(entries)) throw new InvalidDataException("Invalid credential store");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entries);
        try
        {
            if (bytes.Length > Limit) throw new InvalidDataException("Credential store is too large");
            var reply = await RunAsync(replace ? "passwords-replace" : "passwords-create", password, bytes);
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

    private async Task<byte[]> RunAsync(string command, char[] password, byte[]? payload)
    {
        if (!Available) throw new IOException("Password store unavailable");
        var info = new ProcessStartInfo(tool)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        info.ArgumentList.Add(command);
        info.ArgumentList.Add(path);
        using var process = Process.Start(info) ?? throw new IOException("Password store unavailable");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        byte[] secret = [];
        byte[] wire = [];
        try
        {
            // Never put the passphrase in an argument, environment variable, or diagnostic log.
            secret = Encoding.UTF8.GetBytes(password);
            wire = new byte[secret.Length * 2 + 1];
            const string digits = "0123456789abcdef";
            for (int i = 0; i < secret.Length; i++)
            {
                wire[i * 2] = (byte)digits[secret[i] >> 4];
                wire[i * 2 + 1] = (byte)digits[secret[i] & 15];
            }
            wire[^1] = (byte)'\n';
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
            CryptographicOperations.ZeroMemory(secret);
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
