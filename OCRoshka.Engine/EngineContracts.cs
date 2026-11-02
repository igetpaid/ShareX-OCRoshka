using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;

namespace OCRoshka.Engine
{
    /// <summary>
    /// Text recognition back end, independent of any host application.
    ///
    /// The engine lives here rather than inside a host so that the accuracy work survives host
    /// updates: ShareX compiles a thin adapter over this library, and a standalone tool can
    /// reference the same assembly.
    /// </summary>
    public sealed class OcrOptions
    {
        /// <summary>
        /// Full path to tesseract.exe. When empty it is looked up next to the running program, in a
        /// 'tesseract' folder, in tools\tesseract, and finally on PATH.
        /// </summary>
        public string ExecutablePath { get; set; } = string.Empty;

        /// <summary>
        /// Language list, '+' separated, e.g. "rus+eng". Languages that are not installed are
        /// dropped rather than failing the recognition.
        /// </summary>
        public string Languages { get; set; } = TesseractEngine.PreferredLanguage;

        /// <summary>
        /// Base scale applied before recognition. 1 keeps the image as captured, which measures
        /// most accurate for text that is already large enough.
        /// </summary>
        public float ScaleFactor { get; set; } = 1f;

        /// <summary>
        /// Run a second, larger pass when the first one reports low confidence. This is how small
        /// text is rescued without paying the cost on every capture.
        /// </summary>
        public bool AutoScale { get; set; } = true;

        /// <summary>Scale used for the retry pass.</summary>
        public float RetryScaleFactor { get; set; } = 2f;

        /// <summary>Join all lines with spaces instead of newlines.</summary>
        public bool SingleLine { get; set; }

        public OcrOptions Clone()
        {
            return new OcrOptions
            {
                ExecutablePath = ExecutablePath,
                Languages = Languages,
                ScaleFactor = ScaleFactor,
                AutoScale = AutoScale,
                RetryScaleFactor = RetryScaleFactor,
                SingleLine = SingleLine
            };
        }
    }

    /// <summary>
    /// A language the engine can actually use, discovered from the installed traineddata files.
    /// </summary>
    public sealed class OcrLanguage
    {
        public string DisplayName { get; }

        /// <summary>
        /// Value for <see cref="OcrOptions.Languages"/>. A single language, or several joined with
        /// '+' for a combined pass.
        /// </summary>
        public string Code { get; }

        public OcrLanguage(string displayName, string code)
        {
            DisplayName = displayName;
            Code = code;
        }

        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// Result of a recognition, including the confidence statistics the adaptive retry is based on.
    /// </summary>
    public sealed class OcrResult
    {
        public string Text { get; }

        public IReadOnlyList<string> Lines { get; }

        public double MeanConfidence { get; }

        public double MinConfidence { get; }

        public int DurationMs { get; }

        /// <summary>True when the second pass was run and its result was kept.</summary>
        public bool UsedRetry { get; }

        public OcrResult(string text, IReadOnlyList<string> lines, double meanConfidence, double minConfidence, int durationMs, bool usedRetry)
        {
            Text = text;
            Lines = lines;
            MeanConfidence = meanConfidence;
            MinConfidence = minConfidence;
            DurationMs = durationMs;
            UsedRetry = usedRetry;
        }

        public static OcrResult Empty { get; } = new OcrResult(string.Empty, new List<string>(), 0, 0, 0, false);
    }
}
