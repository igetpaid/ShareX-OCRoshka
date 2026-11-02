using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OCRoshka.Engine
{
    /// <summary>
    /// Text recognition back end built on the Tesseract command line engine.
    ///
    /// Two things make it a good fit for mixed Russian/English screen text, and both were measured
    /// on real captures rather than assumed:
    ///
    /// 1. Languages are combined in one pass ("-l rus+eng"). Engines that take a single language
    ///    per image destroy the other script completely, and a screenshot rarely contains only one.
    /// 2. Page segmentation is left on automatic (mode 3). Mode 6 keeps line structure but silently
    ///    drops text whose font differs from the dominant one; mode 11 keeps that text but splits
    ///    loosely aligned columns into separate lines. Mode 3 does neither.
    ///
    /// The TSV output is requested because it carries per-word confidence in the same run, which is
    /// what drives the optional second, larger pass.
    /// </summary>
    public sealed class TesseractEngine
    {
        public const string PreferredLanguage = "rus+eng";
        public const string PreferredLanguageDisplayName = "Russian + English (recommended)";

        private const string TesseractExeName = "tesseract.exe";
        private const string TessDataDirName = "tessdata";

        // The retry starts when the first pass looks unreliable. Measured on screen captures: clean
        // text averages ~92 confidence, a page that also contains small text drops to ~84. Only the
        // average decides this: a single odd word must not trigger a second pass, but it must not
        // veto the retry either, because one low scoring word is normal even on a good page.
        private const double RetryMeanConfidenceThreshold = 88.0;

        /// <summary>Page segmentation for the first pass: automatic layout analysis.</summary>
        private const int PrimaryPageSegmentation = 3;

        /// <summary>
        /// Page segmentation for the retry. Sparse mode reads low confidence and small text better,
        /// so it is used once the first pass has already reported that it struggled.
        /// </summary>
        private const int RetryPageSegmentation = 11;

        private readonly string exePath;
        private readonly string tessDataDir;
        private readonly string unavailableReason;
        private OcrLanguage[] availableLanguages;

        public bool IsAvailable => unavailableReason == null;

        public string UnavailableReason => unavailableReason;

        /// <summary>Folder the engine executable was found in, or null when unavailable.</summary>
        public string ExecutablePath => exePath;

        /// <summary>Folder holding the *.traineddata files, or null when unavailable.</summary>
        public string TessDataPath => tessDataDir;

        public IReadOnlyList<OcrLanguage> AvailableLanguages
        {
            get
            {
                if (availableLanguages == null)
                {
                    availableLanguages = BuildAvailableLanguages().ToArray();
                }

                return availableLanguages;
            }
        }

        /// <summary>
        /// Optional diagnostic sink. Hosts pass their own logger here; nothing is written anywhere
        /// when it is left null, which keeps the engine free of host dependencies.
        /// </summary>
        public Action<string> Log { get; set; }

        public TesseractEngine() : this(null)
        {
        }

        public TesseractEngine(string executablePath)
        {
            exePath = ResolveExecutable(executablePath);
            tessDataDir = exePath == null ? null : ResolveTessDataDir(Path.GetDirectoryName(exePath));

            if (exePath == null)
            {
                unavailableReason = $"{TesseractExeName} was not found. Set its full path in the settings, or place it in a 'tesseract' folder next to the program.";
            }
            else if (tessDataDir == null)
            {
                unavailableReason = $"'{TessDataDirName}' folder was not found next to {TesseractExeName}. It must contain at least 'eng.traineddata'.";
            }
            else if (!HasAnyTrainedData())
            {
                unavailableReason = $"No '*.traineddata' language files found in '{tessDataDir}'.";
            }
        }

        public async Task<OcrResult> RecognizeAsync(Image image, OcrOptions options)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));

            options ??= new OcrOptions();

            if (!IsAvailable) throw new InvalidOperationException(unavailableReason);

            Stopwatch total = Stopwatch.StartNew();

            string languages = ResolveLanguages(options);
            float baseScale = Math.Max(options.ScaleFactor, 0.1f);

            PassResult first = await RunPassAsync(image, languages, baseScale, PrimaryPageSegmentation).ConfigureAwait(false);
            PassResult kept = first;
            bool usedRetry = false;

            if (options.AutoScale && first.LooksUnreliable && options.RetryScaleFactor > baseScale)
            {
                PassResult retry = await RunPassAsync(image, languages, options.RetryScaleFactor, RetryPageSegmentation).ConfigureAwait(false);

                // Sparse mode can return confident but trivial fragments such as a lone "о", which
                // would inflate the average. Enlarging also costs accuracy on text that was already
                // crisp, so the retry is only taken when it is both more confident and not emptier
                // than the first pass.
                if (retry.MeanConfidence > first.MeanConfidence
                    && retry.SignificantCharCount >= first.SignificantCharCount * 0.9)
                {
                    kept = retry;
                    usedRetry = true;
                }
            }

            total.Stop();

            string text = options.SingleLine
                ? string.Join(" ", kept.Lines)
                : string.Join(Environment.NewLine, kept.Lines);

            return new OcrResult(text, kept.Lines, kept.MeanConfidence, kept.MinConfidence, (int)total.ElapsedMilliseconds, usedRetry);
        }

        private async Task<PassResult> RunPassAsync(Image image, string languages, float scale, int pageSegmentation)
        {
            string tempFilePath = Path.Combine(Path.GetTempPath(), $"ocr_{Guid.NewGuid():N}.png");

            try
            {
                SaveScaled(image, tempFilePath, scale);

                string stdout = await RunProcessAsync(tempFilePath, languages, pageSegmentation).ConfigureAwait(false);

                return PassResult.Parse(stdout);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempFilePath))
                    {
                        File.Delete(tempFilePath);
                    }
                }
                catch (Exception e)
                {
                    Log?.Invoke($"Could not remove temporary file: {e.Message}");
                }
            }
        }

        private static void SaveScaled(Image image, string filePath, float scale)
        {
            if (Math.Abs(scale - 1f) < 0.01f)
            {
                image.Save(filePath, ImageFormat.Png);
                return;
            }

            int width = (int)Math.Round(image.Width * scale);
            int height = (int)Math.Round(image.Height * scale);

            // Bounding the scaled size keeps a large capture from being upscaled into a memory
            // spike, which the host would otherwise pay for on every retry.
            const int MaxSide = 8000;
            double shrink = MaxSide / (double)Math.Max(width, height);

            if (shrink < 1.0)
            {
                width = (int)(width * shrink);
                height = (int)(height * shrink);
            }

            using (Bitmap scaled = new Bitmap(Math.Max(width, 1), Math.Max(height, 1), PixelFormat.Format24bppRgb))
            {
                scaled.SetResolution(image.HorizontalResolution, image.VerticalResolution);

                using (Graphics graphics = Graphics.FromImage(scaled))
                {
                    // Tesseract is trained on screen text, so smoothing is used to avoid the hard
                    // edges a nearest-neighbour scale would produce.
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.CompositingQuality = CompositingQuality.HighQuality;
                    graphics.Clear(Color.White);
                    graphics.DrawImage(image, new Rectangle(0, 0, scaled.Width, scaled.Height));
                }

                scaled.Save(filePath, ImageFormat.Png);
            }
        }

        private async Task<string> RunProcessAsync(string inputFilePath, string languages, int pageSegmentation)
        {
            StringBuilder arguments = new StringBuilder();
            arguments.Append('"').Append(inputFilePath).Append('"');
            arguments.Append(" stdout");
            arguments.Append(" -l ").Append(languages);
            arguments.Append(" --psm ").Append(pageSegmentation);
            arguments.Append(" --tessdata-dir \"").Append(tessDataDir).Append('"');
            arguments.Append(" tsv");

            Log?.Invoke($"Tesseract run: \"{exePath}\" {arguments}");

            ProcessStartInfo psi = new ProcessStartInfo()
            {
                FileName = exePath,
                Arguments = arguments.ToString(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using (Process process = new Process())
            {
                process.StartInfo = psi;
                process.Start();

                // Both streams must be drained while the process runs, otherwise a full pipe buffer
                // would block the child process.
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync().ConfigureAwait(false);

                string stdout = await stdoutTask.ConfigureAwait(false);
                string stderr = await stderrTask.ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    // Tesseract writes diagnostics such as "Estimating resolution as 70" to
                    // stderr even on success, so only the exit code decides failure.
                    throw new InvalidOperationException($"Tesseract failed with exit code {process.ExitCode}. {stderr.Trim()}");
                }

                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    Log?.Invoke($"Tesseract stderr: {stderr.Trim()}");
                }

                return stdout;
            }
        }

        private string ResolveLanguages(OcrOptions options)
        {
            string languages = string.IsNullOrWhiteSpace(options.Languages) ? PreferredLanguage : options.Languages;

            // Keep only the part the user installed, so a default of "rus+eng" still works when
            // only one of the two language files is present.
            List<string> available = GetInstalledLanguageCodes();
            List<string> usable = languages
                .Split('+', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim().ToLowerInvariant())
                .Where(x => available.Contains(x))
                .ToList();

            if (usable.Count > 0)
            {
                return string.Join("+", usable);
            }

            throw new InvalidOperationException(
                $"None of the requested Tesseract languages are installed in '{tessDataDir}'. Requested: {languages}. Installed: {string.Join(", ", available)}.");
        }

        private List<string> GetInstalledLanguageCodes()
        {
            List<string> codes = new List<string>();

            foreach (string filePath in Directory.GetFiles(tessDataDir, "*.traineddata"))
            {
                string name = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();

                // tessdata_best files carry a quality suffix that Tesseract does not expect.
                int suffix = name.LastIndexOf('_');
                if (suffix > 0 && IsQualitySuffix(name.Substring(suffix + 1)))
                {
                    name = name.Substring(0, suffix);
                }

                // "osd" holds script and orientation detection data. It is not a language and
                // passing it to -l produces noise.
                if (name == "osd") continue;

                if (!codes.Contains(name))
                {
                    codes.Add(name);
                }
            }

            return codes;
        }

        private static bool IsQualitySuffix(string value)
        {
            return value == "best" || value == "fast" || value == "int" || value == "float";
        }

        private IEnumerable<OcrLanguage> BuildAvailableLanguages()
        {
            if (!IsAvailable) yield break;

            List<string> installed = GetInstalledLanguageCodes();

            if (installed.Contains("rus") && installed.Contains("eng"))
            {
                yield return new OcrLanguage(PreferredLanguageDisplayName, PreferredLanguage);
            }

            foreach (string code in installed.OrderBy(x => x, StringComparer.Ordinal))
            {
                yield return new OcrLanguage(DescribeLanguage(code), code);
            }
        }

        private static string DescribeLanguage(string code)
        {
            try
            {
                CultureInfo culture = CultureInfo.GetCultureInfo(code);
                return $"{culture.NativeName} ({code})";
            }
            catch (CultureNotFoundException)
            {
                return code;
            }
        }

        private bool HasAnyTrainedData()
        {
            try
            {
                return Directory.GetFiles(tessDataDir, "*.traineddata").Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string ResolveExecutable(string configuredPath)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                string configured = configuredPath.Trim();

                if (Directory.Exists(configured))
                {
                    configured = Path.Combine(configured, TesseractExeName);
                }

                if (File.Exists(configured))
                {
                    return Path.GetFullPath(configured);
                }
            }

            // When the engine is loaded from the ShareX folder (a project reference), that folder
            // is where the bundled tesseract lives, which can differ from the host's base directory.
            string engineDir = null;

            try
            {
                string location = typeof(TesseractEngine).Assembly.Location;
                if (!string.IsNullOrEmpty(location)) engineDir = Path.GetDirectoryName(location);
            }
            catch
            {
            }

            if (engineDir != null)
            {
                string besideEngine = Path.Combine(engineDir, "tesseract", TesseractExeName);
                if (File.Exists(besideEngine)) return besideEngine;
            }

            string exeDir = AppContext.BaseDirectory;

            // Shared install written by the setup, so no configuration is needed to find it.
            string shared = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "OCRoshka", "tesseract", TesseractExeName);

            if (File.Exists(shared)) return Path.GetFullPath(shared);

            // Folder shipped next to the running program.
            foreach (string folder in EnumerateCandidateFolders(exeDir))
            {
                string candidate = Path.Combine(folder, TesseractExeName);
                if (File.Exists(candidate)) return candidate;
            }

            // Development layout: the engine sits in tools\tesseract.
            DirectoryInfo dir = new DirectoryInfo(exeDir);

            for (int i = 0; i < 8 && dir != null; i++)
            {
                string candidate = Path.Combine(dir.FullName, "tools", "tesseract", TesseractExeName);
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }

            string path = Environment.GetEnvironmentVariable("PATH");

            if (!string.IsNullOrEmpty(path))
            {
                foreach (string entry in path.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(entry)) continue;

                    try
                    {
                        string candidate = Path.Combine(entry.Trim(), TesseractExeName);
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch (ArgumentException)
                    {
                        // Ignore malformed PATH entries.
                    }
                }
            }

            return null;
        }

        private static IEnumerable<string> EnumerateCandidateFolders(string exeDir)
        {
            yield return exeDir;
            yield return Path.Combine(exeDir, "tesseract");

            DirectoryInfo dir = new DirectoryInfo(exeDir);

            for (int i = 0; i < 8 && dir != null; i++)
            {
                yield return Path.Combine(dir.FullName, "tesseract");
                yield return Path.Combine(dir.FullName, "tools", "tesseract");
                dir = dir.Parent;
            }
        }

        private static string ResolveTessDataDir(string exeFolder)
        {
            if (string.IsNullOrEmpty(exeFolder)) return null;

            string parent = Path.GetDirectoryName(exeFolder);

            string[] candidates =
            {
                Path.Combine(exeFolder, TessDataDirName),
                Path.Combine(parent ?? string.Empty, TessDataDirName)
            };

            foreach (string candidate in candidates)
            {
                if (Directory.Exists(candidate)) return candidate;
            }

            return null;
        }

        /// <summary>
        /// One Tesseract pass: the reconstructed lines plus the confidence statistics used to
        /// decide whether another pass is worth doing.
        /// </summary>
        private sealed class PassResult
        {
            public List<string> Lines { get; private set; } = new List<string>();

            public double MeanConfidence { get; private set; }

            public double MinConfidence { get; private set; } = 100;

            /// <summary>
            /// Number of letters and digits recognized. Guards against accepting a result that only
            /// looks confident because it consists of a few short fragments.
            /// </summary>
            public int SignificantCharCount { get; private set; }

            public bool LooksUnreliable => MeanConfidence < RetryMeanConfidenceThreshold;

            /// <summary>
            /// Reads the TSV stream. Only level 5 (word) rows are used, because their text is in
            /// its own cell while higher levels repeat nested text and would duplicate it.
            /// </summary>
            public static PassResult Parse(string tsv)
            {
                PassResult result = new PassResult();

                if (string.IsNullOrEmpty(tsv)) return result;

                SortedDictionary<(int Block, int Paragraph, int Line), SortedDictionary<int, string>> lines =
                    new SortedDictionary<(int Block, int Paragraph, int Line), SortedDictionary<int, string>>();

                List<double> confidences = new List<double>();

                foreach (string row in tsv.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] columns = row.Split('\t');

                    if (columns.Length < 12) continue;
                    if (!int.TryParse(columns[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int level)) continue;
                    if (level != 5) continue;

                    string text = columns[11].Trim();

                    if (text.Length == 0) continue;

                    if (!int.TryParse(columns[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int block)) continue;
                    if (!int.TryParse(columns[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int paragraph)) continue;
                    if (!int.TryParse(columns[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int line)) continue;

                    int word = int.TryParse(columns[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) ? w : 0;

                    if (!lines.TryGetValue((block, paragraph, line), out SortedDictionary<int, string> words))
                    {
                        words = new SortedDictionary<int, string>();
                        lines[(block, paragraph, line)] = words;
                    }

                    words[word] = text;

                    if (double.TryParse(columns[10], NumberStyles.Float, CultureInfo.InvariantCulture, out double confidence))
                    {
                        confidences.Add(confidence);
                    }
                }

                foreach (var line in lines.Values)
                {
                    string text = string.Join(" ", line.Values).Trim();

                    if (text.Length > 0)
                    {
                        result.Lines.Add(text);
                    }
                }

                if (confidences.Count > 0)
                {
                    result.MeanConfidence = confidences.Average();
                    result.MinConfidence = confidences.Min();
                }

                result.SignificantCharCount = result.Lines.Sum(line => line.Count(char.IsLetterOrDigit));

                return result;
            }
        }
    }
}
