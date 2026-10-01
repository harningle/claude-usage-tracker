using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClaudeUsageTracker.App.Interop;

namespace ClaudeUsageTracker.App.Tray;

/// <summary>Turns a WPF drawing into an HICON with per-pixel alpha.</summary>
internal static class IconFactory
{
    /// <summary>
    /// Renders <paramref name="draw"/> onto a square canvas of <paramref name="pixels"/> physical pixels
    /// (drawing units are pixels) and returns an owned HICON; the caller destroys it.
    /// </summary>
    public static IntPtr Create(int pixels, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            draw(context);
        }

        var target = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        // Icons take straight (not premultiplied) alpha.
        var straight = new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0);
        var stride = pixels * 4;
        var pixelsBuffer = new byte[stride * pixels];
        straight.CopyPixels(pixelsBuffer, stride, 0);
        return FromBgra(pixels, pixelsBuffer);
    }

    private static IntPtr FromBgra(int size, byte[] bgra)
    {
        var header = new NativeMethods.BitmapInfoHeader
        {
            biSize = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
            biWidth = size,
            biHeight = -size, // top-down rows, matching WPF's buffer
            biPlanes = 1,
            biBitCount = 32,
        };
        var colour = NativeMethods.CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (colour == IntPtr.Zero)
        {
            throw new ExternalException("CreateDIBSection failed.", Marshal.GetLastWin32Error());
        }

        var mask = IntPtr.Zero;
        try
        {
            Marshal.Copy(bgra, 0, bits, bgra.Length);
            // An all-zero AND mask: the alpha channel alone decides transparency. Rows are WORD-aligned.
            var maskBits = new byte[(size + 15) / 16 * 2 * size];
            var pinned = GCHandle.Alloc(maskBits, GCHandleType.Pinned);
            try
            {
                mask = NativeMethods.CreateBitmap(size, size, 1, 1, pinned.AddrOfPinnedObject());
            }
            finally
            {
                pinned.Free();
            }

            if (mask == IntPtr.Zero)
            {
                throw new ExternalException("CreateBitmap failed.", Marshal.GetLastWin32Error());
            }

            var info = new NativeMethods.IconInfo { fIcon = true, hbmMask = mask, hbmColor = colour };
            var icon = NativeMethods.CreateIconIndirect(ref info);
            return icon != IntPtr.Zero
                ? icon
                : throw new ExternalException("CreateIconIndirect failed.", Marshal.GetLastWin32Error());
        }
        finally
        {
            NativeMethods.DeleteObject(colour);
            if (mask != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(mask);
            }
        }
    }
}
