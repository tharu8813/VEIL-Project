// SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace Veil.Cef;

internal enum VaultScenario { Truncated, Redirect, Saturated, Full, ParallelCanceled, ParallelFull }

internal sealed class FixtureVault
{
    internal const int BodySize = 768 * 1024;
    private const int ChunkSize = 16 * 1024;
    private readonly Channel<byte[]> chunks = Channel.CreateBounded<byte[]>(8);
    private readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource releaseReader = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool fullResponse;
    private int completed;
    private int receivedBytes;
    internal int ReceivedBytes => Volatile.Read(ref receivedBytes);

    internal FixtureVault(string executable, string profile, VaultScenario scenario)
    {
        ChunkCallback = AddChunk;
        Finished = Task.Run(() => RunAsync(executable, profile, scenario));
    }

    internal Task<bool> Ready => ready.Task;
    internal Task<bool> Finished { get; }
    internal Veil.Desktop.CefNative.OwnedChunk ChunkCallback { get; }

    internal void Complete(bool succeeded)
    {
        if (Interlocked.Exchange(ref completed, 1) != 0) return;
        fullResponse = succeeded;
        chunks.Writer.TryComplete();
        releaseReader.TrySetResult();
    }

    private int AddChunk(nint data, int length)
    {
        if (data == 0 || length is <= 0 or > ChunkSize) return 0;
        var chunk = new byte[length];
        Marshal.Copy(data, chunk, 0, length);
        if (chunks.Writer.TryWrite(chunk))
        {
            Interlocked.Add(ref receivedBytes, length);
            return 1;
        }
        CryptographicOperations.ZeroMemory(chunk);
        return 0;
    }

    private async Task<bool> RunAsync(string executable, string profile, VaultScenario scenario)
    {
        try
        {
            var directory = Path.Combine(profile, scenario switch
            {
                VaultScenario.ParallelCanceled => "fixture-vault-parallel-cancel",
                VaultScenario.ParallelFull => "fixture-vault-parallel-keep",
                _ => "fixture-vault",
            });
            Directory.CreateDirectory(directory);
            var objectPath = Path.Combine(directory, scenario switch
            {
                VaultScenario.Truncated => "truncated.veil",
                VaultScenario.Redirect => "redirect.veil",
                VaultScenario.Saturated => "saturated.veil",
                VaultScenario.ParallelCanceled => "canceled.veil",
                _ => "fixture.veil",
            });
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
            };
            start.ArgumentList.Add("serve");
            start.ArgumentList.Add(directory);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Vault failed to start.");
            try
            {
                var input = process.StandardInput.BaseStream;
                var output = process.StandardOutput.BaseStream;
                WriteLine(input, scenario is VaultScenario.Truncated or VaultScenario.ParallelCanceled or VaultScenario.ParallelFull
                    ? "create\tcef-fixture-passphrase\tTESTA-TESTB-TESTC-TESTD-TESTE-TESTF-TESTG-TESTH"
                    : "unlock\tcef-fixture-passphrase");
                if (ReadLine(output) != "ok") return false;
                WriteLine(input, $"seal\t{objectPath}");
                ready.TrySetResult(true);
                // A paused consumer makes a full queue deterministic.
                if (scenario == VaultScenario.Saturated) await releaseReader.Task.ConfigureAwait(false);
                var total = 0;
                using (var frames = new BinaryWriter(input, Encoding.UTF8, leaveOpen: true))
                {
                    await foreach (var chunk in chunks.Reader.ReadAllAsync().ConfigureAwait(false))
                    {
                        try
                        {
                            frames.Write((uint)chunk.Length);
                            frames.Write(chunk);
                            total += chunk.Length;
                        }
                        finally { CryptographicOperations.ZeroMemory(chunk); }
                    }
                    if (fullResponse && total == BodySize)
                    {
                        frames.Write(0u);
                        frames.Write((ulong)total);
                    }
                    else
                    {
                        frames.Write(uint.MaxValue);
                    }
                }
                var reply = ReadLine(output);
                if (scenario is not (VaultScenario.Full or VaultScenario.ParallelFull))
                {
                    bool expectedBytes = scenario == VaultScenario.Redirect ? total == 0 : total > 0 && total < BodySize;
                    if (fullResponse || !expectedBytes || !reply.StartsWith("err\t", StringComparison.Ordinal) ||
                        File.Exists(objectPath)) return false;
                }
                else
                {
                    if (!fullResponse || total != BodySize || reply != $"ok\t{total}") return false;
                    WriteLine(input, $"read\t{objectPath}");
                    if (ReadLine(output) != $"ok\t{total}") return false;
                    var restored = new byte[ChunkSize];
                    try
                    {
                        for (var offset = 0; offset < total; offset += restored.Length)
                        {
                            output.ReadExactly(restored);
                            for (var i = 0; i < restored.Length; ++i)
                                if (restored[i] != (offset + i) % 251) return false;
                        }
                    }
                    finally { CryptographicOperations.ZeroMemory(restored); }
                }
                WriteLine(input, "quit");
                return process.WaitForExit(5000) && process.ExitCode == 0;
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            ready.TrySetResult(false);
            chunks.Writer.TryComplete();
            while (chunks.Reader.TryRead(out var chunk)) CryptographicOperations.ZeroMemory(chunk);
        }
    }

    private static void WriteLine(Stream stream, string line)
    {
        stream.Write(Encoding.UTF8.GetBytes(line + "\n"));
        stream.Flush();
    }

    private static string ReadLine(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[128];
        for (var i = 0; i < bytes.Length; ++i)
        {
            int value = stream.ReadByte();
            if (value == '\n') return Encoding.UTF8.GetString(bytes[..i]).TrimEnd('\r');
            if (value < 0) break;
            bytes[i] = (byte)value;
        }
        throw new IOException("Invalid vault fixture response.");
    }
}
