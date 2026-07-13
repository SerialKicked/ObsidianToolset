using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ObsidianToolset
{
    /// <summary>
    /// Lightweight parsing of Obsidian note metadata: YAML frontmatter (tags, aliases, arbitrary keys)
    /// and inline #tags. Deliberately hand-rolled to avoid a YAML dependency; handles the common
    /// shapes Obsidian itself produces, not the full YAML spec.
    /// </summary>
    internal static class ObsidianMetadata
    {
        private static readonly Regex InlineTagRegex =
            new(@"(?<![\w#])#([A-Za-z0-9_][A-Za-z0-9_/\-]*)", RegexOptions.Compiled);

        /// <summary>
        /// Split a note into its raw frontmatter block (without the --- fences) and the body.
        /// Returns (null, wholeText) when there is no frontmatter.
        /// </summary>
        public static (string? frontmatter, string body) SplitFrontmatter(string text)
        {
            // Frontmatter must start at the very top of the file: ---\n ... \n---
            var m = Regex.Match(text, @"\A---\r?\n(?<fm>.*?)\r?\n---\r?\n?", RegexOptions.Singleline);
            if (!m.Success)
                return (null, text);
            return (m.Groups["fm"].Value, text[m.Length..]);
        }

        /// <summary>Read a list-valued frontmatter key (e.g. tags, aliases) in any of Obsidian's common shapes.</summary>
        public static List<string> GetListProperty(string? frontmatter, string key)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(frontmatter))
                return result;

            var lines = frontmatter.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var m = Regex.Match(line, $@"^\s*{Regex.Escape(key)}\s*:\s*(?<val>.*)$", RegexOptions.IgnoreCase);
                if (!m.Success)
                    continue;

                var val = m.Groups["val"].Value.Trim();

                if (val.StartsWith('[') && val.EndsWith(']'))
                {
                    // Flow list: [a, b, "c d"]
                    foreach (var part in SplitFlow(val[1..^1]))
                        AddClean(result, part);
                }
                else if (val.Length > 0)
                {
                    // Scalar value on the same line.
                    AddClean(result, val);
                }
                else
                {
                    // Block list on following indented "- item" lines.
                    for (int j = i + 1; j < lines.Length; j++)
                    {
                        var item = Regex.Match(lines[j], @"^\s*-\s+(?<item>.+?)\s*$");
                        if (!item.Success)
                            break;
                        AddClean(result, item.Groups["item"].Value);
                    }
                }
                break; // key handled
            }
            return result;
        }

        /// <summary>Read a scalar frontmatter key. Returns null if absent.</summary>
        public static string? GetScalarProperty(string? frontmatter, string key)
        {
            if (string.IsNullOrEmpty(frontmatter))
                return null;
            var m = Regex.Match(frontmatter, $@"^\s*{Regex.Escape(key)}\s*:\s*(?<val>.+?)\s*$",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);
            return m.Success ? Trim(m.Groups["val"].Value) : null;
        }

        /// <summary>All aliases declared in a note's frontmatter ('aliases' or legacy 'alias').</summary>
        public static List<string> GetAliases(string text)
        {
            var (fm, _) = SplitFrontmatter(text);
            var aliases = GetListProperty(fm, "aliases");
            aliases.AddRange(GetListProperty(fm, "alias"));
            return aliases.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>All tags for a note: frontmatter 'tags' + inline #tags in the body. Leading '#' stripped, deduped.</summary>
        public static List<string> GetTags(string text)
        {
            var (fm, body) = SplitFrontmatter(text);
            var tags = new List<string>();

            foreach (var t in GetListProperty(fm, "tags"))
                tags.Add(t.TrimStart('#'));
            // Obsidian also allows a singular 'tag' key.
            foreach (var t in GetListProperty(fm, "tag"))
                tags.Add(t.TrimStart('#'));

            foreach (Match m in InlineTagRegex.Matches(body))
                tags.Add(m.Groups[1].Value);

            return tags
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IEnumerable<string> SplitFlow(string inner)
        {
            // Split on commas not inside quotes; good enough for Obsidian frontmatter.
            var current = "";
            char quote = '\0';
            foreach (var c in inner)
            {
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    else current += c;
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                }
                else if (c == ',')
                {
                    yield return current;
                    current = "";
                }
                else current += c;
            }
            if (current.Trim().Length > 0)
                yield return current;
        }

        private static void AddClean(List<string> list, string raw)
        {
            var v = Trim(raw);
            if (v.Length > 0)
                list.Add(v);
        }

        private static string Trim(string raw)
        {
            var v = raw.Trim();
            if (v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\'')))
                v = v[1..^1];
            return v.Trim();
        }
    }
}
