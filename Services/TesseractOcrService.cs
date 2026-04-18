using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Tesseract;
using OcrResult = ScreenshotOCR.Models.OcrResult;
using OcrWord = ScreenshotOCR.Models.OcrWord;

namespace ScreenshotOCR.Services;

// Tesseract-backed OCR. Picked over Windows.Media.Ocr specifically because
// it exposes typographic metrics per line through hOCR: baseline slope +
// x_size/x_ascenders/x_descenders. That lets the selection band be built
// from real typography rather than the loose bbox Windows.Media.Ocr hands
// back, which was the root cause of all the alignment-by-pixel-sniffing
// pain we went through.
public static class TesseractOcrService
{
    // Upscale small crops so Tesseract sees ~20px x-height glyphs. Tesseract's
    // accuracy falls off fast below that — same reason we upscaled for
    // Windows.Media.Ocr.
    private const int UpscaleMinDim = 1200;
    private const double UpscaleMax = 3.0;

    private static readonly Regex TitleAttrRegex = new(
        @"(?<key>\w+)\s+(?<vals>[^;""]+?)(?=;|""|$)",
        RegexOptions.Compiled);

    public static Task<OcrResult> RecognizeAsync(Bitmap bitmap, string languageTag = "eng")
    {
        // Tesseract is CPU-bound and blocking — run off the UI thread.
        return Task.Run(() => Recognize(bitmap, languageTag));
    }

    private static OcrResult Recognize(Bitmap bitmap, string languageTag)
    {
        var lang = NormalizeLanguage(languageTag);
        var tessdataPath = FindTessdataPath();
        if (tessdataPath is null)
            return new OcrResult(string.Empty, Array.Empty<OcrWord>(), bitmap.Width, bitmap.Height);

        var (sourceBitmap, scale, ownsBitmap) = PrepareForOcr(bitmap);
        try
        {
            using var engine = new TesseractEngine(tessdataPath, lang, EngineMode.LstmOnly);
            // Treat the crop as a single uniform block of text — mirrors the
            // user's mental model when they drag a rectangle over a paragraph.
            engine.DefaultPageSegMode = PageSegMode.SingleBlock;

            using var pix = BitmapToPix(sourceBitmap);
            using var page = engine.Process(pix);

            var fullText = page.GetText() ?? string.Empty;
            var hocr = page.GetHOCRText(0);
            var words = ParseHOcr(hocr, scale);
            return new OcrResult(fullText, words, bitmap.Width, bitmap.Height);
        }
        finally
        {
            if (ownsBitmap) sourceBitmap.Dispose();
        }
    }

    // --- Language + path ---------------------------------------------------

    private static string NormalizeLanguage(string tag)
    {
        // Accept both Tesseract codes ("eng", "tur") and the BCP-47 tags the
        // old Windows.Media.Ocr service used ("en-US", "tr-TR").
        if (string.IsNullOrWhiteSpace(tag)) return "eng";
        tag = tag.Trim().ToLowerInvariant();
        if (tag.StartsWith("en")) return "eng";
        if (tag.StartsWith("tr")) return "tur";
        return tag.Length == 3 ? tag : "eng";
    }

    private static string? FindTessdataPath()
    {
        // Single-file self-extract puts Content files next to the extracted exe,
        // which AppContext.BaseDirectory points at. Fall back to a tessdata/
        // folder next to the exe for non-packaged runs.
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tessdata"),
            Path.Combine(Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "", "tessdata")
        };
        foreach (var c in candidates)
        {
            if (Directory.Exists(c) && Directory.EnumerateFiles(c, "*.traineddata").Any())
                return c;
        }
        return null;
    }

    // --- Preprocessing ----------------------------------------------------

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

    private static Pix BitmapToPix(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return Pix.LoadFromMemory(ms.ToArray());
    }

    // --- hOCR parsing -----------------------------------------------------
    //
    // hOCR example emitted by Tesseract:
    //   <span class='ocr_line' id='line_1_1'
    //         title='bbox 12 40 210 64;
    //                baseline -0.003 58;
    //                x_size 18.5;
    //                x_ascenders 5.1;
    //                x_descenders 3.2'>
    //     <span class='ocrx_word' id='word_1_1_1' title='bbox 12 40 58 62;...'>Hello</span>
    //     ...
    //   </span>
    //
    // We extract the line's baseline coefficients (slope, offset) plus
    // x_size / x_ascenders / x_descenders, then compute the line's
    // typographic vertical band as:
    //   top    = baseline_y(midX) − x_size − x_ascenders
    //   bottom = baseline_y(midX) + x_descenders
    // Every word on that line gets the same (Y, Height), so the selection
    // band has consistent height across the line.

    // Regex-matching balanced HTML is unreliable; instead we find every
    // ocr_line opening tag, then look at each line's slice of the document
    // (up to the next ocr_line or EOF) and pull word spans from there.
    private static readonly Regex LineStartRegex = new(
        @"<span\s+class=['""]ocr_line['""][^>]*title=['""](?<title>[^'""]+)['""][^>]*>",
        RegexOptions.Compiled);

    private static readonly Regex WordRegex = new(
        @"<span\s+class=['""]ocrx_word['""][^>]*title=['""](?<title>[^'""]+)['""][^>]*>(?<text>[^<]*)</span>",
        RegexOptions.Compiled);

    private static List<OcrWord> ParseHOcr(string hocr, double upscaleFactor)
    {
        var result = new List<OcrWord>();
        if (string.IsNullOrWhiteSpace(hocr)) return result;

        var lineStarts = LineStartRegex.Matches(hocr);
        int lineIdx = 0;
        for (int i = 0; i < lineStarts.Count; i++)
        {
            var lineMatch = lineStarts[i];
            int sliceStart = lineMatch.Index + lineMatch.Length;
            int sliceEnd = (i + 1 < lineStarts.Count) ? lineStarts[i + 1].Index : hocr.Length;
            var inner = hocr.Substring(sliceStart, sliceEnd - sliceStart);

            var lineTitle = lineMatch.Groups["title"].Value;
            var meta = ParseTitleAttrs(lineTitle);

            if (!meta.TryGetValue("bbox", out var bboxLine) || bboxLine.Length < 4)
                continue;

            double lineX0 = bboxLine[0];
            double lineY0 = bboxLine[1];
            double lineX1 = bboxLine[2];
            double lineY1 = bboxLine[3];

            // baseline "slope offset" — in hOCR, y = slope*(x - lineX0) + (lineY1 + offset).
            // Offset is typically 0 for near-horizontal text; treat absent as 0.
            double slope = 0, yOffset = 0;
            if (meta.TryGetValue("baseline", out var bl) && bl.Length >= 2)
            {
                slope = bl[0];
                yOffset = bl[1];
            }

            double xSize = meta.TryGetValue("x_size", out var xs) && xs.Length >= 1 ? xs[0] : (lineY1 - lineY0);
            double xAsc = meta.TryGetValue("x_ascenders", out var xa) && xa.Length >= 1 ? xa[0] : xSize * 0.25;
            double xDesc = meta.TryGetValue("x_descenders", out var xd) && xd.Length >= 1 ? xd[0] : xSize * 0.2;

            double midX = (lineX0 + lineX1) / 2;
            double baselineY = slope * (midX - lineX0) + lineY1 + yOffset;

            double bandTop = Math.Max(0, baselineY - xSize - xAsc);
            double bandBottom = baselineY + xDesc;
            double bandY = bandTop / upscaleFactor;
            double bandH = (bandBottom - bandTop) / upscaleFactor;

            int wordIdx = 0;
            foreach (Match wm in WordRegex.Matches(inner))
            {
                var wMeta = ParseTitleAttrs(wm.Groups["title"].Value);
                if (!wMeta.TryGetValue("bbox", out var wb) || wb.Length < 4) continue;

                string text = DecodeEntities(wm.Groups["text"].Value).Trim();
                if (string.IsNullOrEmpty(text)) continue;

                double wx = wb[0] / upscaleFactor;
                double ww = (wb[2] - wb[0]) / upscaleFactor;

                result.Add(new OcrWord(text, wx, bandY, ww, bandH, lineIdx, wordIdx));
                wordIdx++;
            }
            if (wordIdx > 0) lineIdx++;
        }
        return result;
    }

    private static Dictionary<string, double[]> ParseTitleAttrs(string title)
    {
        var dict = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in title.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0) continue;

            int firstSpace = trimmed.IndexOf(' ');
            if (firstSpace <= 0) continue;

            var key = trimmed.Substring(0, firstSpace);
            var valsPart = trimmed.Substring(firstSpace + 1).Trim();

            var tokens = valsPart.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var nums = new List<double>(tokens.Length);
            foreach (var t in tokens)
            {
                if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    nums.Add(d);
            }
            if (nums.Count > 0) dict[key] = nums.ToArray();
        }
        return dict;
    }

    private static string DecodeEntities(string s) => s
        .Replace("&amp;", "&")
        .Replace("&lt;", "<")
        .Replace("&gt;", ">")
        .Replace("&quot;", "\"")
        .Replace("&#39;", "'");
}
