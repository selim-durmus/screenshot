using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using OcrResult = ScreenshotOCR.Models.OcrResult;
using OcrWord = ScreenshotOCR.Models.OcrWord;

namespace ScreenshotOCR.Services;

// Windows.Media.Ocr-backed service. We moved to Tesseract earlier thinking
// OCR bboxes were the cause of misaligned selection highlights — they
// weren't. The real bug was in the render transform (see
// ResultWindow.ImageTransform). With that fixed, Windows.Media.Ocr is the
// better fit: 3-5x faster, no first-run model load, smaller binary, and
// generally more accurate on anti-aliased screen text than Tesseract.
public static class OcrService
{
    // Upscale small crops so small UI text (10-12px glyphs) reliably
    // triggers OCR. Below ~20px x-height accuracy falls off a cliff.
    private const int UpscaleMinDim = 1200;
    private const double UpscaleMax = 3.0;

    public static async Task<OcrResult> RecognizeAsync(Bitmap bitmap, string languageTag = "en-US")
    {
        var lang = new Language(NormalizeLanguage(languageTag));
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

            return new OcrResult(ocr.Text ?? string.Empty, words, bitmap.Width, bitmap.Height);
        }
        finally
        {
            if (ownsBitmap) sourceBitmap.Dispose();
        }
    }

    // Legacy settings files may hold Tesseract-style codes ("eng", "tur");
    // Windows.Media.Ocr expects BCP-47. Normalize so old settings don't
    // silently break.
    private static string NormalizeLanguage(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return "en-US";
        var t = tag.Trim().ToLowerInvariant();
        return t switch
        {
            "eng" => "en-US",
            "tur" => "tr-TR",
            _ => tag
        };
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
}
