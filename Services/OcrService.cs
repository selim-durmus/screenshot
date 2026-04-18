using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using OcrResult = ScreenshotOCR.Models.OcrResult;
using OcrWord = ScreenshotOCR.Models.OcrWord;

namespace ScreenshotOCR.Services;

public static class OcrService
{
    public static async Task<OcrResult> RecognizeAsync(Bitmap bitmap, string languageTag = "en-US")
    {
        var lang = new Language(languageTag);
        var engine = OcrEngine.IsLanguageSupported(lang)
            ? OcrEngine.TryCreateFromLanguage(lang)
            : OcrEngine.TryCreateFromUserProfileLanguages();

        if (engine is null)
            return new OcrResult(string.Empty, Array.Empty<OcrWord>(), bitmap.Width, bitmap.Height);

        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Bmp);
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
                words.Add(new OcrWord(w.Text, r.X, r.Y, r.Width, r.Height, lineIdx, wordIdx));
                wordIdx++;
            }
            lineIdx++;
        }

        return new OcrResult(ocr.Text ?? string.Empty, words, bitmap.Width, bitmap.Height);
    }
}
