using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClaudeUsageTracker.App.Storage;

namespace ClaudeUsageTracker.App.Notifications;

/// <summary>
/// The image notifications show for the app: the port's own ring logo (as on the About page) in the accent colour,
/// drawn at startup, because no artwork ships with the app and upstream's is not reused.
/// </summary>
internal static class NotificationIcon
{
    private const int Size = 64;

    /// <summary>Writes the PNG and returns its path, or null when it could not be written (notifications then use the shell's default).</summary>
    public static string? Write(string path)
    {
        var accent = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x35));
        accent.Freeze();
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var centre = new Point(Size / 2.0, Size / 2.0);
            context.DrawEllipse(null, new Pen(accent, 6), centre, Size / 2.0 - 5, Size / 2.0 - 5);
            context.DrawEllipse(accent, null, centre, 7, 7);
        }

        var bitmap = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        try
        {
            using var stream = new MemoryStream();
            encoder.Save(stream);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllBytes(path, stream.ToArray());
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
