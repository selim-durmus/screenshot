using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using OcrResult = ScreenshotOCR.Models.OcrResult;
using OcrWord = ScreenshotOCR.Models.OcrWord;

namespace ScreenshotOCR.Services;

public static class OcrService
{
    private const int UpscaleMinDim = 1200;
    private const double UpscaleMax = 3.0;

    public static async Task<OcrResult> RecognizeAsync(Bitmap bitmap, string languageTag = "en-US")
    {
        var lang = new Language(languageTag);
        var engine = OcrEngine.IsLanguageSupported(lang)
            ? OcrEngine.TryCreateFromLanguage(lang)
            : OcrEngine.TryCreateFromUserProfileLanguages();

        if (engine is null)
            return new OcrResult(string.Empty, Array.Empty<OcrWord>(), bitmap.Width, bitmap.Height);

        var (sourceBitmap, scale, ownsBitmap) = PrepareForOcr(bitmap);
        try
        {
            using var ms = new MemoryStream();
            sourceBitmap.Save(ms, ImageFormat.Bmp);
            ms.Position = 0;

            using var ras = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(ras.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(ms.ToArray());
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            var decoder = await BitmapDecoder.CreateAsync(ras);
            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

            var ocr = await engine.RecognizeAsync(softwareBitmap);

            var words = new List<OcrWord>();
            int lineIdx = 0;
            foreach (var line in ocr.Lines)
            {
                int wordIdx = 0;
                foreach (var w in line.Words)
                {
                    var r = w.BoundingRect;
                    words.Add(new OcrWord(
                        w.Text,
                        r.X / scale,
                        r.Y / scale,
                        r.Width / scale,
                        r.Height / scale,
                        lineIdx, wordIdx));
                    wordIdx++;
                }
                lineIdx++;
            }

            // Refine each word's vertical bounds to the actual ink rows in the
            // captured image, not OCR's loose typographic bounding box. This is
            // what makes selection highlights visually hug the text.
            var refined = RefineVerticalBoundsToInk(bitmap, words);

            return new OcrResult(ocr.Text ?? string.Empty, refined, bitmap.Width, bitmap.Height);
        }
        finally
        {
            if (ownsBitmap) sourceBitmap.Dispose();
        }
    }

    private static (Bitmap bmp, double scale, bool ownsBitmap) PrepareForOcr(Bitmap src)
    {
        int minDim = Math.Min(src.Width, src.Height);
        if (minDim <= 0 || minDim >= UpscaleMinDim) return (src, 1.0, false);

        double scale = Math.Min(UpscaleMinDim / (double)minDim, UpscaleMax);
        int nw = (int)Math.Round(src.Width * scale);
        int nh = (int)Math.Round(src.Height * scale);

        var up = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(up))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.DrawImage(src, 0, 0, nw, nh);
        }
        return (up, scale, true);
    }

    // --- Ink-bound refinement ---------------------------------------------
    //
    // Windows.Media.Ocr returns word bounding boxes that are loose vertically —
    // they include typographic padding above the ink, variable per line/font.
    // Selection highlights therefore appear "above" the text by an inconsistent
    // amount. To fix this we lock the source bitmap once, then for each word
    // scan row-by-row within its reported bbox and measure where the ink
    // actually starts and ends (rows where enough pixels differ sufficiently
    // from the estimated background). We replace OCR's Y/Height with those
    // measured values. Fast — O(image pixels) — and self-correcting per line.

    private const int InkDeltaThreshold = 48;      // luma diff from background to count as "ink"
    private const double InkRowPixelFraction = 0.05; // row is ink if ≥ this fraction of its width is ink

    private static List<OcrWord> RefineVerticalBoundsToInk(Bitmap bitmap, List<OcrWord> words)
    {
        if (words.Count == 0) return words;
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            var pixels = new byte[stride * bitmap.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

            var refined = new List<OcrWord>(words.Count);
            foreach (var w in words)
            {
                var (newY, newH) = MeasureInkRows(pixels, stride, bitmap.Width, bitmap.Height, w);
                refined.Add(w with { Y = newY, Height = newH });
            }
            return refined;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static (double Y, double H) MeasureInkRows(
        byte[] pixels, int stride, int imgW, int imgH, OcrWord word)
    {
        int x0 = Math.Max(0, (int)Math.Floor(word.X));
        int y0 = Math.Max(0, (int)Math.Floor(word.Y));
        int x1 = Math.Min(imgW, (int)Math.Ceiling(word.X + word.Width));
        int y1 = Math.Min(imgH, (int)Math.Ceiling(word.Y + word.Height));
        int w = x1 - x0;
        int h = y1 - y0;
        if (w <= 1 || h <= 1) return (word.Y, word.Height);

        // Estimate background luma from the first and last rows of the bbox.
        long bgSum = 0; int bgCount = 0;
        for (int x = x0; x < x1; x++)
        {
            bgSum += Luma(pixels, stride, x, y0);
            bgSum += Luma(pixels, stride, x, y1 - 1);
            bgCount += 2;
        }
        int bgLuma = bgCount > 0 ? (int)(bgSum / bgCount) : 255;

        int minInkPixelsPerRow = Math.Max(1, (int)(w * InkRowPixelFraction));

        int firstInkRow = -1;
        int lastInkRow = -1;
        for (int y = y0; y < y1; y++)
        {
            int inkCount = 0;
            for (int x = x0; x < x1; x++)
            {
                int d = Luma(pixels, stride, x, y) - bgLuma;
                if (d < -InkDeltaThreshold || d > InkDeltaThreshold)
                {
                    inkCount++;
                    if (inkCount >= minInkPixelsPerRow) break;
                }
            }
            if (inkCount >= minInkPixelsPerRow)
            {
                if (firstInkRow == -1) firstInkRow = y;
                lastInkRow = y;
            }
        }

        if (firstInkRow == -1 || lastInkRow < firstInkRow)
            return (word.Y, word.Height);

        // Small 1-pixel top/bottom bleed so antialiased glyph edges aren't clipped.
        double inkY = Math.Max(0, firstInkRow - 1);
        double inkH = Math.Min(imgH - inkY, lastInkRow - firstInkRow + 1 + 2);
        return (inkY, inkH);
    }

    private static int Luma(byte[] pixels, int stride, int x, int y)
    {
        int o = y * stride + x * 4;
        int b = pixels[o];
        int g = pixels[o + 1];
        int r = pixels[o + 2];
        return (299 * r + 587 * g + 114 * b) / 1000;
    }
}
