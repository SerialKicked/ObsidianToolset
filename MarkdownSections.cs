using System.Text.RegularExpressions;

namespace ObsidianToolset
{
    internal readonly record struct Heading(int Line, int Level, string Text);

    /// <summary>A heading plus the (exclusive) line index where its section ends, and how many headings matched the query.</summary>
    internal readonly record struct Section(Heading Heading, int EndLine, int MatchCount);

    /// <summary>
    /// Heading/section parsing shared by the read and write tools, so both agree on what a section is.
    /// Works on LF-normalised lines. Ignores '#' lines inside YAML frontmatter and fenced code blocks.
    /// </summary>
    internal static class MarkdownSections
    {
        private static readonly Regex HeadingRegex =
            new(@"^ {0,3}(?<h>#{1,6})[ \t]+(?<t>.*?)(?:[ \t]+#+)?[ \t]*$", RegexOptions.Compiled);

        private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

        public static string Norm(string s) => WhitespaceRun.Replace(s.Trim(), " ");

        private static bool Eq(string a, string b) =>
            string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);

        public static bool TryParseHeadingLine(string line, out int level, out string text)
        {
            var m = HeadingRegex.Match(line);
            level = m.Success ? m.Groups["h"].Length : 0;
            text = m.Success ? m.Groups["t"].Value.Trim() : string.Empty;
            return m.Success;
        }

        public static List<Heading> ParseHeadings(IReadOnlyList<string> lines)
        {
            var result = new List<Heading>();
            int i = 0;

            // Skip YAML frontmatter (only if it is properly closed).
            if (lines.Count > 0 && lines[0].TrimEnd() == "---")
            {
                for (int j = 1; j < lines.Count; j++)
                {
                    if (lines[j].TrimEnd() is "---" or "...")
                    {
                        i = j + 1;
                        break;
                    }
                }
            }

            char fence = '\0';
            int fenceLen = 0;
            for (; i < lines.Count; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart(' ');
                if (fence != '\0')
                {
                    if (IsFenceClose(trimmed, fence, fenceLen))
                        fence = '\0';
                    continue;
                }
                if (TryOpenFence(trimmed, out fence, out fenceLen))
                    continue;
                if (TryParseHeadingLine(line, out var level, out var text))
                    result.Add(new Heading(i, level, text));
            }
            return result;
        }

        private static bool TryOpenFence(string t, out char ch, out int len)
        {
            ch = '\0';
            len = 0;
            if (t.Length < 3 || (t[0] != '`' && t[0] != '~')) return false;
            int n = 0;
            while (n < t.Length && t[n] == t[0]) n++;
            if (n < 3) return false;
            if (t[0] == '`' && t.IndexOf('`', n) >= 0) return false; // inline code like ```x```, not a fence
            ch = t[0];
            len = n;
            return true;
        }

        private static bool IsFenceClose(string t, char ch, int len)
        {
            int n = 0;
            while (n < t.Length && t[n] == ch) n++;
            return n >= len && t[n..].Trim().Length == 0;
        }

        /// <summary>
        /// Find a section by heading text. Forgiving about: surrounding whitespace/quotes, extra inner whitespace,
        /// case, and the model including the '#' marks ("## My Heading"). First match wins.
        /// </summary>
        public static Section? Find(IReadOnlyList<string> lines, string? headingArg)
        {
            var arg = VaultIO.CleanArg(headingArg ?? string.Empty);
            var nl = arg.IndexOf('\n');
            if (nl >= 0) arg = arg[..nl];
            arg = Norm(arg);
            if (arg.Length == 0) return null;

            var headings = ParseHeadings(lines);

            // Pass 1: the whole argument is the heading text (handles headings that really start with '#').
            var matches = headings.Where(h => Eq(h.Text, arg)).ToList();

            // Pass 2: argument carries the '#' marks, e.g. "## Goals".
            if (matches.Count == 0)
            {
                var m = Regex.Match(arg, @"^(#{1,6})\s*(.+)$");
                if (m.Success)
                {
                    var level = m.Groups[1].Length;
                    var text = m.Groups[2].Value;
                    matches = headings.Where(h => h.Level == level && Eq(h.Text, text)).ToList();
                    if (matches.Count == 0)
                        matches = headings.Where(h => Eq(h.Text, text)).ToList();
                }
            }

            if (matches.Count == 0) return null;

            var first = matches[0];
            int end = lines.Count;
            foreach (var h in headings)
            {
                // A section ends at the next heading of the same or higher level (subsections stay inside).
                if (h.Line > first.Line && h.Level <= first.Level)
                {
                    end = h.Line;
                    break;
                }
            }
            return new Section(first, end, matches.Count);
        }

        /// <summary>Human-readable heading list for "not found" messages.</summary>
        public static string DescribeHeadings(IReadOnlyList<string> lines)
        {
            var headings = ParseHeadings(lines);
            if (headings.Count == 0) return "The note has no headings.";
            var shown = headings.Take(40).Select(h => $"{new string('#', h.Level)} {h.Text}");
            var more = headings.Count > 40 ? $"\n... and {headings.Count - 40} more" : string.Empty;
            return "Available headings:\n" + string.Join("\n", shown) + more;
        }

        /// <summary>Split LF-normalised text into lines, dropping the empty element a trailing newline creates.</summary>
        public static List<string> SplitLines(string lfText, out bool trailingNewline)
        {
            trailingNewline = lfText.EndsWith('\n');
            var lines = lfText.Split('\n').ToList();
            if (trailingNewline)
                lines.RemoveAt(lines.Count - 1);
            return lines;
        }
    }
}
