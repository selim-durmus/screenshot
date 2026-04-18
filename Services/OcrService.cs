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
    // Windows.Media.Ocr returns word bboxes whose vertical extent is
    // inconsistent: words with descenders (e.g. "deps.json") report a
    // taller bbox; words of the same line WITHOUT descenders (e.g.
    // "witcher.exe", "2026-04") often report a bbox that only covers
    // cap-height down to x-height-ish, cutting off the baseline.
    //
    // Per-word pixel-sniff suffered from this: for a lone descenderless
    // word, the seed bbox + small pad doesn't reach the real baseline,
    // so the measured bottom lands somewhere in the middle of the glyphs.
    //
    // Line-level sniff fixes this. For each line we:
    //   1. Compute the union (minX..maxX, minY..maxY) of all its words'
    //      OCR bboxes.
    //   2. Scan rows in an EXPANDED range (±line-height) across the full
    //      horizontal union. Even if every word on the line has no
    //      descender, the generous scan window and wide horizontal
    //      sampling reliably find the true top and bottom ink rows.
    //   3. Apply the measured (Y, H) to every word on that line, so the
    //      selection band has uniform height per line.
    //
    // Background luma is still estimated from rows OUTSIDE the bbox
    // (above/below the line), using the median to shrug off stray pixels.

    private const int InkDeltaThreshold = 18;
    private const int MinInkPixelsPerRow = 2;
    private const int BgPadRows = 3;

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

            var lineBounds = new Dictionary<int, (double Y, double H)>();
            foreach (var group in words.GroupBy(w => w.LineIndex))
            {
                double ux0 = group.Min(w => w.X);
                double ux1 = group.Max(w => w.X + w.Width);
                double uy0 = group.Min(w => w.Y);
                double uy1 = group.Max(w => w.Y + w.Height);

                int x0 = Math.Max(0, (int)Math.Floor(ux0));
                int y0 = Math.Max(0, (int)Math.Floor(uy0));
                int x1 = Math.Min(bitmap.Width, (int)Math.Ceiling(ux1));
                int y1 = Math.Min(bitmap.Height, (int)Math.Ceiling(uy1));

                var measured = MeasureLineInk(pixels, stride, bitmap.Width, bitmap.Height, x0, y0, x1, y1);
                lineBounds[group.Key] = measured;
            }

            var refined = new List<OcrWord>(words.Count);
            foreach (var w in words)
            {
                var (lineY, lineH) = lineBounds.TryGetValue(w.LineIndex, out var b)
                    ? b
                    : (w.Y, w.Height);
                refined.Add(w with { Y = lineY, Height = lineH });
            }
            return refined;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static (double Y, double H) MeasureLineInk(
        byte[] pixels, int stride, int imgW, int imgH,
        int x0, int y0, int x1, int y1)
    {
        int w = x1 - x0;
        int hBox = y1 - y0;
        if (w <= 1 || hBox <= 1) return (y0, hBox);

        // Generous scan pad: at least one full line-height beyond OCR's bbox
        // on each side, with an absolute minimum of 8 px. This makes the
        // algorithm robust to OCR bboxes that undershoot in any direction.
        int pad = Math.Max(8, hBox);
        int scanY0 = Math.Max(0, y0 - pad);
        int scanY1 = Math.Min(imgH, y1 + pad);

        int bgLuma = EstimateBackgroundLuma(pixels, stride, imgH, x0, x1, y0, y1);

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
                    if (inkCount >= MinInkPixelsPerRow) break;
                }
            }
            if (inkCount >= MinInkPixelsPerRow)
            {
                if (firstInkRow == -1) firstInkRow = y;
                lastInkRow = y;
            }
        }

        if (firstInkRow == -1 || lastInkRow < firstInkRow)
            return (y0, hBox);

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
