namespace ScreenshotOCR.Models;

public record OcrWord(string Text, double X, double Y, double Width, double Height, int LineIndex, int WordIndex);

public record OcrResult(string FullText, IReadOnlyList<OcrWord> Words, int ImageWidth, int ImageHeight);
