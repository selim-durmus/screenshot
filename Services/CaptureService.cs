using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ScreenshotOCR.Services;

public static class CaptureService
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    public record VirtualScreen(int X, int Y, int Width, int Height);

    public static VirtualScreen GetVirtualScreen()
    {
        int x = System.Windows.Forms.SystemInformation.VirtualScreen.X;
        int y = System.Windows.Forms.SystemInformation.VirtualScreen.Y;
        int w = System.Windows.Forms.SystemInformation.VirtualScreen.Width;
        int h = System.Windows.Forms.SystemInformation.VirtualScreen.Height;
        return new VirtualScreen(x, y, w, h);
    }

    public static Bitmap CaptureVirtualScreen()
    {
        var vs = GetVirtualScreen();
        var bmp = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(vs.X, vs.Y, 0, 0, new Size(vs.Width, vs.Height), CopyPixelOperation.SourceCopy);
        return bmp;
    }

    public static Bitmap CropBitmap(Bitmap source, Rectangle region)
    {
        region = Rectangle.Intersect(region, new Rectangle(0, 0, source.Width, source.Height));
        if (region.Width <= 0 || region.Height <= 0)
            return new Bitmap(1, 1);

        var cropped = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(cropped);
        g.DrawImage(source, new Rectangle(0, 0, region.Width, region.Height), region, GraphicsUnit.Pixel);
        return cropped;
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    public static BitmapSource ToBitmapSource(Bitmap bmp)
    {
        // HBitmap interop preserves pixel dimensions without any DPI round-trip.
        // This keeps image-pixel space == WPF display space (modulo a constant scale).
        IntPtr hBitmap = bmp.GetHbitmap();
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }
}
