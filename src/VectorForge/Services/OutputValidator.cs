using System.Text;
using System.Windows.Media.Imaging;

namespace VectorForge.Services;

public enum OutputHealth { Valid, Invalid, Deferred }
public sealed record OutputStamp(long Length, DateTime ModifiedUtc, OutputHealth Health);

public sealed class OutputValidator
{
    private readonly Dictionary<string, OutputStamp> _cache;
    public OutputValidator(Dictionary<string, OutputStamp>? cache = null) => _cache = cache ?? new(StringComparer.OrdinalIgnoreCase);
    public OutputHealth Check(string path, ExportKind kind)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return OutputHealth.Deferred;
            var info = new FileInfo(path);
            var length = info.Length;
            var modified = info.LastWriteTimeUtc;
            if (_cache.TryGetValue(path, out var cached) && cached.Length == length &&
                cached.ModifiedUtc == modified) return cached.Health;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            bool valid;
            if (kind == ExportKind.Jpg)
            {
                try
                {
                    var decoder = new JpegBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);
                    var frame = decoder.Frames[0];
                    // Copy in stripes to force decoding without allocating the full image twice.
                    var stride = checked((frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8);
                    var rows = Math.Min(32, frame.PixelHeight);
                    var buffer = new byte[checked(stride * rows)];
                    for (var y = 0; y < frame.PixelHeight; y += rows)
                        frame.CopyPixels(new System.Windows.Int32Rect(0, y, frame.PixelWidth,
                            Math.Min(rows, frame.PixelHeight - y)), buffer, stride, 0);
                    valid = frame.PixelWidth > 0 && frame.PixelHeight > 0;
                }
                catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException)
                { valid = false; }
            }
            else
            {
                var head = new byte[(int)Math.Min(length, 1024)];
                stream.ReadExactly(head);
                stream.Position = Math.Max(0, length - 4096);
                var tail = new byte[(int)(length - stream.Position)];
                stream.ReadExactly(tail);
                var header = Encoding.ASCII.GetString(head);
                var footer = Encoding.ASCII.GetString(tail);
                valid = header.StartsWith("%PDF-", StringComparison.Ordinal) &&
                    footer.Contains("%%EOF", StringComparison.Ordinal) &&
                    System.Text.RegularExpressions.Regex.IsMatch(footer, @"startxref\s+\d+\s+%%EOF");
            }
            var health = valid ? OutputHealth.Valid : OutputHealth.Invalid;
            if (!path.Contains(".vectorforge-temp-", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileName(path).StartsWith("vectorforge_temp_", StringComparison.OrdinalIgnoreCase))
                _cache[path] = new(length, modified, health);
            return health;
        }
        catch (FileNotFoundException) { return OutputHealth.Invalid; }
        catch (DirectoryNotFoundException) { return OutputHealth.Deferred; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        { return OutputHealth.Deferred; }
    }
}
