using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
#if DEBUG
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
#endif

namespace Veil.Desktop;

/// <summary>Experimental one-page raster worker. A child process is not a security sandbox.</summary>
internal static class VaultPdfPreview
{
#if DEBUG
    internal static bool Enabled => true;
#else
    internal static bool Enabled => false;
#endif
    internal const int MaxPages = 200, MaxSide = 1800;
    internal const int MaxInput = VaultPresentation.ImagePreviewLimit;
    internal const int MaxOutput = 8 * 1024 * 1024;

    internal static bool ValidHeader(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 8 && bytes.Length <= MaxInput && bytes[..5].SequenceEqual("%PDF-"u8) &&
        bytes[5] is (byte)'1' or (byte)'2' && bytes[6] == (byte)'.' && bytes[7] is >= (byte)'0' and <= (byte)'9';

    internal static (int Width, int Height)? RenderSize(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1 ||
            width > 14400 || height > 14400 || Math.Max(width, height) / Math.Min(width, height) > 50) return null;
        double scale = MaxSide / Math.Max(width, height);
        return ((int)Math.Clamp(Math.Round(width * scale), 1, MaxSide),
            (int)Math.Clamp(Math.Round(height * scale), 1, MaxSide));
    }

    internal sealed record Page(int Count, BitmapSource Bitmap);

    internal static async Task<Page> RenderAsync(byte[] bytes, int page, CancellationToken cancel)
    {
        if (!Enabled || !ValidHeader(bytes) || page < 0 || page >= MaxPages) throw new InvalidDataException("PDF preview refused.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("No application host.");
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(VaultPdfPreview).Assembly.Location);
        else if (!string.Equals(Path.GetFileNameWithoutExtension(exe), "Veil.Desktop", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected PDF worker host.");
        info.ArgumentList.Add("--pdf-preview-worker");
        using var child = Process.Start(info) ?? throw new IOException("PDF worker unavailable.");
        byte[]? rendered = null;
        byte[] request = bytes.ToArray();
        try
        {
            // Fixed binary protocol: byte count, page index, PDF bytes. No URL/path/secret in arguments.
            var input = child.StandardInput.BaseStream;
            await input.WriteAsync(BitConverter.GetBytes(bytes.Length), token);
            await input.WriteAsync(BitConverter.GetBytes(page), token);
            await input.WriteAsync(request, token);
            await input.FlushAsync(token);
            child.StandardInput.Close();
            var output = child.StandardOutput.BaseStream;
            byte[] header = new byte[8];
            await output.ReadExactlyAsync(header, token);
            int count = BitConverter.ToInt32(header, 0), length = BitConverter.ToInt32(header, 4);
            if (count < 1 || count > MaxPages || page >= count || length < 1 || length > MaxOutput)
                throw new InvalidDataException("Invalid PDF worker response.");
            rendered = new byte[length];
            await output.ReadExactlyAsync(rendered, token);
            byte[] extra = new byte[1];
            if (await output.ReadAsync(extra, token) != 0) throw new InvalidDataException("Trailing PDF worker data.");
            await child.WaitForExitAsync(token);
            if (child.ExitCode != 0) throw new IOException("PDF render failed.");
            var size = VaultImagePreview.Dimensions(rendered, VaultPreviewKind.Png);
            if (size.Width > MaxSide || size.Height > MaxSide) throw new InvalidDataException("PDF raster exceeds budget.");
            // Only raster pixels, never a document or interactive PDF surface, reach the UI.
            return new Page(count, VaultImagePreview.Decode(rendered, VaultPreviewKind.Png));
        }
        finally
        {
            try { if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(2000); } }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            CryptographicOperations.ZeroMemory(request);
            if (rendered is not null) CryptographicOperations.ZeroMemory(rendered);
        }
    }

#if DEBUG
    internal static int RunWorker()
    {
        // Bound orphaned workers too. The parent separately enforces a shorter deadline.
        using var watchdog = new Timer(_ => Environment.Exit(3), null, TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan);
        try
        {
            ProfileGate.RejectElevated();
            RenderWorkerAsync().GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception) { return 1; } // no document contents or exception strings in diagnostics
    }

    private static async Task RenderWorkerAsync()
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        byte[] header = new byte[8];
        await input.ReadExactlyAsync(header);
        int length = BitConverter.ToInt32(header, 0), pageIndex = BitConverter.ToInt32(header, 4);
        if (length < 8 || length > MaxInput || pageIndex < 0 || pageIndex >= MaxPages)
            throw new InvalidDataException("Invalid PDF request.");
        byte[] bytes = new byte[length];
        byte[]? raster = null;
        try
        {
            await input.ReadExactlyAsync(bytes);
            if (input.ReadByte() != -1 || !ValidHeader(bytes)) throw new InvalidDataException("Invalid PDF data.");
            using var source = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(source.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
            }
            source.Seek(0);
            var document = await PdfDocument.LoadFromStreamAsync(source);
            if (document.PageCount == 0 || document.PageCount > MaxPages || pageIndex >= document.PageCount)
                throw new InvalidDataException("PDF page limit.");
            using var page = document.GetPage((uint)pageIndex);
            var size = RenderSize(page.Size.Width, page.Size.Height) ?? throw new InvalidDataException("PDF page size.");
            using var destination = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(destination, new PdfPageRenderOptions
            {
                DestinationWidth = (uint)size.Width, DestinationHeight = (uint)size.Height,
                BitmapEncoderId = Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
                IsIgnoringHighContrast = true,
            });
            if (destination.Size == 0 || destination.Size > MaxOutput) throw new InvalidDataException("PDF raster size.");
            using var reader = new DataReader(destination.GetInputStreamAt(0));
            uint count = (uint)destination.Size;
            if (await reader.LoadAsync(count) != count) throw new EndOfStreamException();
            raster = new byte[(int)count];
            reader.ReadBytes(raster);
            await output.WriteAsync(BitConverter.GetBytes((int)document.PageCount));
            await output.WriteAsync(BitConverter.GetBytes(raster.Length));
            await output.WriteAsync(raster);
            await output.FlushAsync();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (raster is not null) CryptographicOperations.ZeroMemory(raster);
        }
    }
#endif
}
