using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace OCRoshka.Engine
{
    /// <summary>
    /// Entry point that the ShareX integration calls.
    ///
    /// The patched ShareX replaces its own OCRHelper.OCR body with a call to this method and
    /// nothing else, so the signature here is load bearing: if it changes, the patcher must
    /// refuse to apply rather than produce a ShareX that fails at run time.
    /// </summary>
    public static class ShareXHook
    {
        public const string SettingsFileName = "settings.json";

        private static TesseractEngine engine;
        private static TesseractEngineSettings settings;

        public static bool IsAvailable => GetEngine().IsAvailable;

        public static TesseractEngineSettings Settings
        {
            get
            {
                if (settings == null)
                {
                    settings = TesseractEngineSettings.Load(SettingsPath);
                }

                return settings;
            }
        }

        public static string SettingsPath => Path.Combine(SettingsDirectory, SettingsFileName);

        public static string SettingsDirectory
        {
            get
            {
                // Roaming so the settings follow the user, not the machine. ShareX is often run by
                // more than one account on the same PC.
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OCRoshka");

                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>
        /// Called by ShareX in place of its own OCR. The language and scale that ShareX passes are
        /// deliberately ignored: the settings file is the single source of truth, so the same
        /// behaviour applies whether the text came from the standalone tool or from ShareX.
        /// </summary>
        public static Task<string> OCR(Bitmap bmp, string languageTag, float scaleFactor, bool singleLine)
        {
            return RecognizeAsync(bmp);
        }

        /// <summary>
        /// Entry point used by the patcher. Only the image is passed, which is what makes the
        /// patch survive a ShareX update that adds, removes or reorders the other parameters: the
        /// patched call site pushes one argument and nothing else has to match.
        /// </summary>
        public static Task<string> OCR(Bitmap bmp)
        {
            return RecognizeAsync(bmp);
        }

        /// <summary>
        /// Same as <see cref="OCR(Bitmap)"/> for a ShareX version that returns the text directly
        /// instead of a task.
        /// </summary>
        public static string OCRText(Bitmap bmp)
        {
            return RecognizeAsync(bmp).GetAwaiter().GetResult();
        }

        public static async Task<string> RecognizeAsync(Image image)
        {
            TesseractEngine current = GetEngine();

            if (!current.IsAvailable)
            {
                // Falling back to empty text would silently lose the capture. ShareX shows the
                // exception in its own error dialog, which is more honest than a blank result.
                throw new InvalidOperationException(current.UnavailableReason);
            }

            OcrOptions current_settings = Settings.BuildOptions();
            current_settings.SingleLine = Settings.SingleLine;

            OcrResult result = await current.RecognizeAsync(image, current_settings).ConfigureAwait(false);

            if (result.UsedRetry)
            {
                Diagnostics.Write($"retry used, confidence {result.MeanConfidence:F1}");
            }

            return RepairPaths(result.Text);
        }

        /// <summary>
        /// Tidy file paths that Tesseract split or retyped. The cleaner confirms every repair
        /// against the file system and leaves anything it cannot prove, so this never turns a
        /// readable path into a wrong one. Failures here are deliberately swallowed: a path that
        /// survives OCR unmended is still far more useful than no capture at all.
        /// </summary>
        public static IReadOnlyList<OcrLanguage> AvailableLanguages => GetEngine().AvailableLanguages;

        public static async Task<string> RecognizeAsync(Image image, bool singleLine)
        {
            TesseractEngine current = GetEngine();

            if (!current.IsAvailable) throw new InvalidOperationException(current.UnavailableReason);

            OcrOptions options = Settings.BuildOptions();
            options.SingleLine = singleLine;

            OcrResult result = await current.RecognizeAsync(image, options).ConfigureAwait(false);

            return RepairPaths(result.Text);
        }

        private static string RepairPaths(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf(':') < 0) return text;

            try
            {
                PathCleaner cleaner = new PathCleaner();
                string cleaned = cleaner.Clean(text);

                if (cleaner.PathsFixed > 0)
                {
                    Diagnostics.Write($"paths repaired: {cleaner.PathsFixed}");

                    foreach (string change in cleaner.Changes)
                    {
                        Diagnostics.Write("  " + change);
                    }
                }

                return cleaned;
            }
            catch (Exception e)
            {
                Diagnostics.Write($"path repair failed: {e.Message}");
                return text;
            }
        }

        private static TesseractEngine GetEngine()
        {
            if (engine == null)
            {
                engine = new TesseractEngine(Settings.ExecutablePath);
            }

            return engine;
        }
    }

    /// <summary>
    /// Settings shared by the standalone tool and the ShareX integration. Kept in one file so both
    /// behave identically and there is a single place to change behaviour.
    /// </summary>
    public sealed class TesseractEngineSettings
    {
        public string ExecutablePath { get; set; } = string.Empty;
        public string Languages { get; set; } = TesseractEngine.PreferredLanguage;
        public float ScaleFactor { get; set; } = 1f;
        public bool AutoScale { get; set; } = true;
        public float RetryScaleFactor { get; set; } = 2f;
        public bool SingleLine { get; set; }

        public OcrOptions BuildOptions()
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

        public static TesseractEngineSettings Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    TesseractEngineSettings loaded = JsonSerializer.Deserialize<TesseractEngineSettings>(File.ReadAllText(path));
                    if (loaded != null) return loaded;
                }
            }
            catch (Exception e)
            {
                Diagnostics.Write($"settings read failed ({e.Message}), using defaults");
            }

            TesseractEngineSettings defaults = new TesseractEngineSettings();
            defaults.Save(path);
            return defaults;
        }

        public void Save(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e)
            {
                Diagnostics.Write($"settings write failed: {e.Message}");
            }
        }
    }

    internal static class Diagnostics
    {
        private static readonly object gate = new object();

        public static void Write(string message)
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "OCRoshka");
                Directory.CreateDirectory(dir);
                lock (gate)
                {
                    File.AppendAllText(Path.Combine(dir, "ocroshka.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Diagnostics must never break recognition.
            }
        }
    }
}
