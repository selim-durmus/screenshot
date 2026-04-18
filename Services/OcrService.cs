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
    // Windows.Media.Ocr returns word bboxes that are loose and sometimes
    // asymmetric — too tall on top, too short on bottom, varying per line.
    // To make selection outlines hug the glyphs, we lock the source bitmap
    // once, scan per-word row-by-row in an expanded region around the OCR
    // bbox, and measure where the ink actually begins and ends.
    //
    // Key subtleties:
    //  - Background luma is estimated from rows OUTSIDE the OCR bbox (where
    //    pixels are almost certainly the page background). Estimating it
    //    from inside the bbox is unreliable — the "top row" can already
    //    contain glyph ink, which flips the luma comparison and produces
    //    nonsense results (the original bug the user reported: the bottom
    //    of the outline cutting through the middle of the glyphs).
    //  - The median of those samples is used rather than the mean, so one
    //    stray dark pixel in the padding strip doesn't skew the estimate.
    //  - The scan range expands 30% past OCR's bbox in each direction to
    //    catch descenders/ascenders that fall outside the reported bbox.
    //  - Thresholds are tuned low enough that antialiased glyph edges
    //    (low contrast with background) still count as ink.

    private const int InkDeltaThreshold = 22;        // luma diff from bg to count as "ink"
    private const double InkRowPixelFraction = 0.02; // row is ink if ≥ this fraction of width deviates from bg
    private const int InkRowPixelCap = 3;            // ...but never require more than this many pixels (so the bottom
                                                     // row of a long word, which may only have faint stroke ends, still counts)
    private const double ScanPadFraction = 0.35;    // expand scan range above/below OCR bbox by this much
    private const int BgPadRows = 3;                 // rows outside bbox to sample for background estimate

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
        int hBox = y1 - y0;
        if (w <= 1 || hBox <= 1) return (word.Y, word.Height);

        int pad = Math.Max(2, (int)Math.Round(hBox * ScanPadFraction));
        int scanY0 = Math.Max(0, y0 - pad);
        int scanY1 = Math.Min(imgH, y1 + pad);

        int bgLuma = EstimateBackgroundLuma(pixels, stride, imgH, x0, x1, y0, y1);
        int minInkPixelsPerRow = Math.Clamp(
            (int)Math.Round(w * InkRowPixelFraction),
            1,
            InkRowPixelCap);

        int firstInkRow = -1;
        int lastInkRow = -1;
        for (int y = scanY0; y < scanY1; y++)
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

        // Generous bottom bleed + modest top bleed to cover sub-threshold
        // antialiased stroke tails (e.g., the very last row of a serif).
        // inkH = 1 (top bleed) + span + 1 (row inclusive) + 2 (bottom bleed) = span + 4
        double inkY = Math.Max(0, firstInkRow - 1);
        double inkH = Math.Min(imgH - inkY, lastInkRow - firstInkRow + 4);
        return (inkY, inkH);
    }

    // Sample background luma from the padding strips immediately above and
    // below the OCR bbox. Falls back to the bbox's corners if the word is
    // too close to the image edge. Uses median rather than mean to shrug
    // off the occasional dark pixel in the padding region.
    private static int EstimateBackgroundLuma(
        byte[] pixels, int stride, int imgH, int x0, int x1, int y0, int y1)
    {
        var samples = new List<int>(capacity: (x1 - x0) * BgPadRows * 2);

        int padTopStart = Math.Max(0, y0 - BgPadRows);
        for (int y = padTopStart; y < y0; y++)
            for (int x = x0; x < x1; x++)
                samples.Add(Luma(pixels, stride, x, y));

        int padBotEnd = Math.Min(imgH, y1 + BgPadRows);
        for (int y = y1; y < padBotEnd; y++)
            for (int x = x0; x < x1; x++)
                samples.Add(Luma(pixels, stride, x, y));

        if (samples.Count == 0)
        {
            // Word touches image edge — fall back to sampling the four corners
            // of the bbox, which are usually background even in tight bboxes.
            samples.Add(Luma(pixels, stride, x0, y0));
            samples.Add(Luma(pixels, stride, x1 - 1, y0));
            samples.Add(Luma(pixels, stride, x0, y1 - 1));
            samples.Add(Luma(pixels, stride, x1 - 1, y1 - 1));
        }

        samples.Sort();
        return samples[samples.Count / 2];
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
