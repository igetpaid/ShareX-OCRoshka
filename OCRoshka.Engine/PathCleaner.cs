using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OCRoshka.Engine
{
    /// <summary>
    /// Repairs file paths in recognised text.
    ///
    /// The temptation here is to strip spaces from anything that looks like a path, because OCR
    /// likes to break a path into words. That is wrong: "C:\OpenCode Projects\ShareX_ocr" is a real
    /// path and the space in it is part of the name. Deleting it produces a path that points
    /// nowhere and looks plausible while being wrong, which is worse than leaving the damage alone.
    ///
    /// So nothing is guessed. Every candidate repair is checked against the file system and a
    /// repair is applied only when it resolves to something that actually exists. A path that
    /// cannot be verified is left exactly as it was recognised, because at that point there is no
    /// evidence for any particular correction.
    /// </summary>
    public sealed class PathCleaner
    {
        /// <summary>
        /// Characters that can legitimately appear inside a Windows path. Space is here on
        /// purpose: it is the whole point of the exercise, and the file system is what decides
        /// whether any given space was real. Characters that are illegal in a Windows filename
        /// are deliberately excluded, because that makes a comma or an exclamation mark end the
        /// path instead of being mistaken for part of it.
        /// </summary>
        private static bool IsPathChar(char c)
        {
            if (char.IsLetterOrDigit(c)) return true;

            return c switch
            {
                '_' or '.' or '$' or '#' or '@' or '~' or '%' or '^' or '&' or
                '+' or '=' or '(' or ')' or '[' or ']' or '{' or '}' or '-' or
                '\\' or '/' or ':' or ' ' => true,
                _ => false
            };
        }

        private static bool IsDriveLetter(char c) => char.IsLetter(c);

        /// <summary>
        /// Characters that end the token when they sit at the very end. A trailing comma or full
        /// stop is far more likely to belong to the sentence than to the path, and a candidate that
        /// exists without it is the same path.
        /// </summary>
        private const string TrailingPunctuation = ".,;:!?)]}>\"'";

        public int PathsFixed { get; private set; }

        public List<string> Changes { get; } = new List<string>();

        public string Clean(string text)
        {
            PathsFixed = 0;
            Changes.Clear();

            if (string.IsNullOrEmpty(text)) return text;

            StringBuilder result = new StringBuilder(text.Length);
            int position = 0;

            foreach ((int start, int length) in FindTokens(text))
            {
                string token = text.Substring(start, length);
                string repaired = Repair(token);

                result.Append(text, position, start - position);

                if (repaired != token)
                {
                    result.Append(repaired);
                    PathsFixed++;
                    Changes.Add($"{token}  ->  {repaired}");
                }
                else
                {
                    result.Append(token);
                }

                position = start + length;
            }

            result.Append(text, position, text.Length - position);
            return result.ToString();
        }

        /// <summary>
        /// Finds runs that start with a drive letter. Anchoring on "X:" is what keeps ordinary
        /// prose out: no sentence fragment can begin with a single letter and a colon, whereas a
        /// heuristic based on dots and slashes happily eats half a paragraph.
        /// </summary>
        private static List<(int Start, int Length)> FindTokens(string text)
        {
            List<(int, int)> tokens = new List<(int, int)>();

            for (int i = 0; i + 1 < text.Length; i++)
            {
                if (!IsDriveLetter(text[i]) || text[i + 1] != ':') continue;

                // "http:" and friends are not paths. A real drive letter is never preceded by
                // another letter, so this one check keeps schemes and abbreviations out.
                if (i > 0 && char.IsLetter(text[i - 1])) continue;

                // Reject "http:" and similar by requiring a separator after the colon. A space may
                // sit between them, since "C: \Program Files" is a common way to misread a path.
                int afterColon = i + 2;

                while (afterColon < text.Length && text[afterColon] == ' ') afterColon++;

                if (afterColon >= text.Length || (text[afterColon] != '\\' && text[afterColon] != '/')) continue;

                int end = afterColon;

                while (end < text.Length && IsPathChar(text[end])) end++;

                // A path cannot end on a space.
                while (end > i + 3 && text[end - 1] == ' ') end--;

                if (end - i >= 4) tokens.Add((i, end - i));

                i = end - 1;
            }

            return tokens;
        }

        /// <summary>
        /// Tidies punctuation and redundant separators. Windows quietly tolerates "stage.", a
        /// forward slash and a doubled backslash, so a damaged path often still resolves and would
        /// otherwise look healthy. The tidied reading is therefore tried first and accepted only
        /// when it names something real.
        /// </summary>
        private static string Canonical(string token)
        {
            string trimmed = token;

            while (trimmed.Length > 3 && TrailingPunctuation.IndexOf(trimmed[trimmed.Length - 1]) >= 0)
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            }

            return CollapseSeparators(trimmed);
        }

        private static int Score(string path) => WholeExists(path) ? int.MaxValue : Depth(path);

        private string Repair(string token)
        {
            string canonical = Canonical(token);

            // Already real and already tidy. There is nothing to fix, and no reason to form a
            // hypothesis about something known good.
            if (canonical == token && WholeExists(token)) return token;

            // The tidied reading names something real, so the untidy text was OCR noise.
            if (canonical != token && WholeExists(canonical)) return canonical;

            // Resolves exactly as recognised. Tidy alternatives would only be a downgrade.
            if (WholeExists(token)) return token;

            int originalDepth = Depth(canonical);

            // Where does the path stop and the sentence resume? This is tried before rewriting any
            // character, because finding the end of the path is a far more certain thing to do
            // than guessing what the characters inside it should have been. Only a prefix that
            // resolves on disk is accepted, so a path that really does contain spaces is never
            // cut short by this.
            foreach (int index in SpaceBoundaries(canonical))
            {
                string head = Canonical(canonical.Substring(0, index));

                if (head.Length < 3) continue;

                if (Score(head) <= originalDepth) continue;

                // The tail is taken from the original text so that punctuation belonging to the
                // sentence survives. Only the part that is genuinely part of the path is tidied.
                string result = head + token.Substring(index);

                // A boundary that reproduces the token exactly is not a repair, it just describes
                // the same damaged path, so the search carries on to a more likely reading.
                if (result == token) continue;

                return result;
            }

            foreach (string candidate in Candidates(canonical))
            {
                if (Score(candidate) > originalDepth) return candidate;
            }

            foreach (string candidate in SpaceInsertions(canonical, originalDepth))
            {
                if (Score(candidate) > originalDepth) return candidate;
            }

            return token;
        }

        /// <summary>Positions of spaces that could mark the end of a path.</summary>
        private static IEnumerable<int> SpaceBoundaries(string token)
        {
            for (int i = token.Length - 1; i > 2; i--)
            {
                if (token[i] == ' ') yield return i;
            }
        }

        /// <summary>
        /// The opposite error to removing a space: OCR swallowing one and gluing a real folder name
        /// into a single word. Reinstating the space is a small, bounded search confined to the
        /// first segment that did not resolve, and every hit is confirmed the same way as any other
        /// repair, so a wrong guess cannot survive to reach the text.
        /// </summary>
        private static IEnumerable<string> SpaceInsertions(string token, int resolvedDepth)
        {
            int resolved = resolvedDepth;

            // Walk to the end of the first segment that has not been confirmed yet. One insertion
            // in one segment is enough: if more than one name was damaged the result will not
            // resolve, and a search that kept going would only be guessing further.
            int start = resolved;

            if (start >= token.Length) yield break;

            while (start < token.Length && token[start] != '\\' && token[start] != '/') start++;

            if (start >= token.Length) yield break;

            string confirmed = token.Substring(0, start);
            string rest = token.Substring(start);

            for (int i = 1; i < rest.Length; i++)
            {
                if (rest[i] == '\\' || rest[i] == '/') break;

                string candidate = confirmed + rest.Substring(0, i) + " " + rest.Substring(i);

                if (WholeExists(candidate) || Depth(candidate) > resolved)
                {
                    yield return candidate;
                }
            }
        }

        /// <summary>
        /// Repairs in order of how little they change, so a fix that only removes a stray space is
        /// always preferred over one that also rewrites letters. The first candidate that resolves
        /// wins, which means the least invasive explanation is the one that gets applied.
        /// </summary>
        private static IEnumerable<string> Candidates(string token)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            List<string> ordered = new List<string>();

            void Add(string value)
            {
                if (value.Length == 0) return;
                if (!seen.Add(value)) return;
                if (value == token) return;
                ordered.Add(value);
            }

            foreach (string trimmed in TrimVariants(token))
            {
                Add(trimmed);
            }

            foreach (string basePath in Structural(token))
            {
                Add(basePath);

                foreach (string mapped in Confusable(basePath))
                {
                    Add(mapped);
                }
            }

            foreach (string structural in Structural(token))
            {
                foreach (string mapped in Confusable(structural))
                {
                    Add(mapped);
                }
            }

            return ordered;
        }

        private static IEnumerable<string> TrimVariants(string token)
        {
            yield return token;

            string trimmed = token;

            while (trimmed.Length > 3 && TrailingPunctuation.IndexOf(trimmed[trimmed.Length - 1]) >= 0)
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
                yield return trimmed;
            }
        }

        /// <summary>
        /// Whitespace repairs, cheapest first: a space that merely sits against a separator is
        /// always wrong, whereas a space in the middle of a name may be perfectly real.
        /// </summary>
        private static IEnumerable<string> Structural(string token)
        {
            string trimmed = token;

            while (trimmed.Length > 3 && TrailingPunctuation.IndexOf(trimmed[trimmed.Length - 1]) >= 0)
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            }

            // "C : \ " and "dir \ sub" are never legal; "C:\Program Files" is.
            StringBuilder near = new StringBuilder(trimmed.Length);
            near.Append(trimmed[0]).Append(trimmed[1]);

            for (int i = 2; i < trimmed.Length; i++)
            {
                char c = trimmed[i];

                if (c == ' ')
                {
                    char previous = trimmed[i - 1];
                    char next = i + 1 < trimmed.Length ? trimmed[i + 1] : '\0';

                    if (previous == '\\' || previous == '/' || next == '\\' || next == '/' || next == ':')
                    {
                        continue;
                    }
                }

                near.Append(c);
            }

            string nearPath = CollapseSeparators(near.ToString());
            yield return nearPath;

            if (nearPath.IndexOf(' ') < 0) yield break;

            // Last resort, and only ever taken when the file system agrees.
            yield return CollapseSeparators(nearPath.Replace(" ", string.Empty));
        }

        private static string CollapseSeparators(string path)
        {
            StringBuilder builder = new StringBuilder(path.Length);

            // A doubled leading backslash is a network path, not a redundant separator.
            bool unc = path.Length > 1 && path[0] == '\\' && path[1] == '\\';

            if (unc) builder.Append("\\\\");

            for (int i = unc ? 2 : 0; i < path.Length; i++)
            {
                char c = path[i];

                if (c == '\\' || c == '/')
                {
                    if (builder.Length > 0 && (builder[builder.Length - 1] == '\\' || builder[builder.Length - 1] == '/')) continue;
                    builder.Append('\\');
                    continue;
                }

                builder.Append(c);
            }

            return builder.ToString();
        }

        /// <summary>
        /// Cyrillic and digit lookalikes, which is how OCR mangles a path into something no rule can
        /// read. Every one of these rewrites is speculative on its own, so they are only ever
        /// produced as candidates and then confirmed against the disk like any other guess.
        /// </summary>
        private static readonly Dictionary<char, char> Confusables = new Dictionary<char, char>
        {
            ['А'] = 'A', ['В'] = 'B', ['Е'] = 'E', ['К'] = 'K', ['М'] = 'M', ['Н'] = 'H',
            ['О'] = 'O', ['Р'] = 'P', ['С'] = 'C', ['Т'] = 'T', ['У'] = 'Y', ['Х'] = 'X',
            ['а'] = 'a', ['в'] = 'b', ['е'] = 'e', ['к'] = 'k', ['м'] = 'm', ['н'] = 'h',
            ['о'] = 'o', ['р'] = 'p', ['с'] = 'c', ['т'] = 't', ['у'] = 'y', ['х'] = 'x',
            ['і'] = 'i', ['ј'] = 'j', ['ѕ'] = 's', ['һ'] = 'h', ['ԁ'] = 'd', ['ԛ'] = 'q',
            ['г'] = 'r', ['п'] = 'n', ['З'] = '3', ['Ч'] = '4', ['б'] = '6', ['и'] = 'u',
            ['ѵ'] = 'v', ['Ѵ'] = 'V', ['А'] = 'A', ['О'] = 'O', ['Р'] = 'P', ['С'] = 'C',
            ['Т'] = 'T', ['Х'] = 'X', ['М'] = 'M', ['К'] = 'K', ['В'] = 'B', ['Е'] = 'E',
            ['Н'] = 'H', ['У'] = 'Y'
        };

        private static readonly Dictionary<char, char> DigitSwaps = new Dictionary<char, char>
        {
            ['5'] = 'S', ['8'] = 'B', ['0'] = 'O', ['1'] = 'l', ['2'] = 'Z', ['6'] = 'G', ['3'] = 'E'
        };

        private static IEnumerable<string> Confusable(string path)
        {
            yield return Swap(path, Confusables);

            string both = Swap(Swap(path, Confusables), DigitSwaps);

            if (both != path) yield return both;
        }

        private static string Swap(string path, Dictionary<char, char> map)
        {
            StringBuilder builder = new StringBuilder(path.Length);

            foreach (char c in path)
            {
                builder.Append(map.TryGetValue(c, out char replacement) ? replacement : c);
            }

            return builder.ToString();
        }

        /// <summary>
        /// How far the path could be confirmed on disk, measured in characters, counting back to the
        /// longest existing ancestor at a segment boundary. Zero means the file system offers no
        /// support for this reading at all, which is the answer that keeps the original text.
        /// </summary>
        private static int Depth(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length < 3) return 0;

            try
            {
                for (int cut = path.Length - 1; cut > 2; cut--)
                {
                    char c = path[cut];

                    if (c != '\\' && c != '/') continue;

                    string prefix = path.Substring(0, cut);

                    if (Exists(prefix) && Substantial(prefix)) return cut;
                }

                return 0;
            }
            catch
            {
                return 0;
            }
        }

        private static bool WholeExists(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length < 3) return false;

            try
            {
                return Exists(path) && Substantial(path);
            }
            catch
            {
                return false;
            }
        }

        private static bool Exists(string path)
        {
            try
            {
                return Directory.Exists(path) || File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// "C:\" exists on every machine with a C drive, so accepting it would let any guess look
        /// confirmed. A repair has to reach a real named directory to count as evidence.
        /// </summary>
        private static bool Substantial(string path)
        {
            int separators = 0;

            for (int i = 2; i < path.Length; i++)
            {
                if (path[i] == '\\' || path[i] == '/') separators++;
            }

            return separators >= 1 && path.Length >= 4;
        }
    }
}
