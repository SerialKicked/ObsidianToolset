using System.Text;

namespace ObsidianToolset
{
    /// <summary>
    /// Shared path + text helpers so every tool treats paths and line endings the same way.
    ///
    /// Paths: tools accept vault-relative paths with either slash style, optional surrounding whitespace/quotes,
    /// and an optional missing '.md' extension. Tools always RETURN vault-relative paths with forward slashes.
    /// Nothing outside the vault root (or inside a dot-folder like .obsidian/.trash) can be addressed.
    ///
    /// Text: notes on disk are usually CRLF on Windows. Everything the LLM sees is normalised to LF, and anything
    /// the LLM sends is normalised to LF before being converted back to the file's own line ending on write.
    /// </summary>
    internal static class VaultIO
    {
        // ------------------------------------------------------------------ argument cleanup

        /// <summary>Trim whitespace and one layer of wrapping quotes/backticks that models like to add.</summary>
        public static string CleanArg(string? s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var v = s.Trim();
            if (v.Length >= 2 && (v[0] == '"' || v[0] == '\'' || v[0] == '`') && v[^1] == v[0])
                v = v[1..^1].Trim();
            return v;
        }

        /// <summary>Clean a vault-relative path: forward slashes, no leading './' or '/', no trailing '/'.</summary>
        public static string NormalizeRel(string? p)
        {
            var v = CleanArg(p).Replace('\\', '/');
            while (v.StartsWith("./")) v = v[2..].TrimStart();
            return v.Trim('/').Trim();
        }

        // ------------------------------------------------------------------ path resolution

        private static string? ResolveFull(string root, string? rel)
        {
            if (string.IsNullOrWhiteSpace(root))
                return null;
            try
            {
                var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                var cleaned = CleanArg(rel);
                // Accept an absolute path as long as it is inside the vault.
                var full = Path.IsPathFullyQualified(cleaned)
                    ? Path.GetFullPath(cleaned)
                    : Path.GetFullPath(Path.Combine(rootFull, NormalizeRel(cleaned)));
                full = Path.TrimEndingDirectorySeparator(full);

                if (full.Equals(rootFull, StringComparison.OrdinalIgnoreCase))
                    return full;
                if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return null; // escapes the vault ('..', other drive...)
                return full;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>True if any segment of the path (relative to the vault) starts with '.', e.g. .obsidian or .trash.</summary>
        public static bool IsHidden(string root, string full)
        {
            var rel = Path.GetRelativePath(root, full);
            return rel.Split('\\', '/').Any(seg => seg.Length > 0 && seg != "." && seg != ".." && seg[0] == '.');
        }

        /// <summary>Resolve an existing folder (empty = vault root). Null if missing, outside the vault, or hidden.</summary>
        public static string? ResolveFolder(string root, string? rel)
        {
            var full = ResolveFull(root, rel);
            if (full is null || !Directory.Exists(full) || IsHidden(root, full))
                return null;
            return full;
        }

        /// <summary>Resolve an existing note. Tolerates a missing '.md' extension. Null if not found.</summary>
        public static string? ResolveNote(string root, string? rel)
        {
            if (string.IsNullOrWhiteSpace(CleanArg(rel)))
                return null;
            var full = ResolveFull(root, rel);
            if (full is null || IsHidden(root, full))
                return null;
            if (File.Exists(full))
                return full;
            if (!full.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && File.Exists(full + ".md"))
                return full + ".md";
            return null;
        }

        /// <summary>Resolve the path for a note that may not exist yet. Always ends in '.md'. Null if invalid.</summary>
        public static string? ResolveNewNote(string root, string? rel)
        {
            if (string.IsNullOrWhiteSpace(CleanArg(rel)))
                return null;
            var full = ResolveFull(root, rel);
            if (full is null)
                return null;
            if (!full.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                full += ".md";
            if (IsHidden(root, full) || Path.GetFileNameWithoutExtension(full).Trim().Length == 0)
                return null;
            return full;
        }

        /// <summary>Vault-relative path with forward slashes: the single format every tool returns.</summary>
        public static string ToRel(string root, string full) =>
            Path.GetRelativePath(root, full).Replace('\\', '/');

        /// <summary>All notes in the vault, skipping dot-folders (.obsidian, .trash, ...). Stable order.</summary>
        public static List<string> EnumerateNotes(string root)
        {
            var result = new List<string>();
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                Walk(root, result);
            return result;
        }

        private static void Walk(string dir, List<string> result)
        {
            try
            {
                var files = Directory.GetFiles(dir, "*.md", SearchOption.TopDirectoryOnly);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                result.AddRange(files);

                var subs = Directory.GetDirectories(dir);
                Array.Sort(subs, StringComparer.OrdinalIgnoreCase);
                foreach (var sub in subs)
                {
                    if (Path.GetFileName(sub).StartsWith('.')) continue;
                    Walk(sub, result);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        /// <summary>Consistent "not found" message, suggesting same-named notes in other folders.</summary>
        public static string NoteNotFound(string root, string? rel)
        {
            var shown = CleanArg(rel);
            var name = Path.GetFileName(NormalizeRel(rel));
            if (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                name = name[..^3];
            if (name.Length > 0)
            {
                var similar = EnumerateNotes(root)
                    .Where(f => Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase))
                    .Take(5)
                    .Select(f => ToRel(root, f))
                    .ToList();
                if (similar.Count > 0)
                    return $"Note not found: '{shown}'. Did you mean: {string.Join(", ", similar)}?";
            }
            return $"Note not found: '{shown}'. Paths are vault-relative, e.g. 'Folder/My Note.md'. Use VaultSearchByTitle to locate a note.";
        }

        // ------------------------------------------------------------------ text + line endings

        /// <summary>Read a note. Also reports whether it starts with a UTF-8 BOM so writes can preserve it.</summary>
        public static (string text, bool bom) ReadText(string full)
        {
            bool bom = false;
            using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var head = new byte[3];
                bom = fs.Read(head, 0, 3) == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
            }
            return (File.ReadAllText(full), bom);
        }

        public static void WriteText(string full, string text, bool bom) =>
            File.WriteAllText(full, text, new UTF8Encoding(bom));

        /// <summary>The dominant line ending of a text. Defaults to the platform's for text with no newlines.</summary>
        public static string DetectEol(string text)
        {
            int crlf = 0, lf = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                if (i > 0 && text[i - 1] == '\r') crlf++; else lf++;
            }
            if (crlf == 0 && lf == 0) return Environment.NewLine;
            return crlf >= lf ? "\r\n" : "\n";
        }

        /// <summary>Normalise any line ending (CRLF, lone CR) to LF.</summary>
        public static string ToLf(string? text) =>
            string.IsNullOrEmpty(text) ? string.Empty : text.Replace("\r\n", "\n").Replace('\r', '\n');

        /// <summary>Convert LF-only text to the given line ending.</summary>
        public static string ApplyEol(string lfText, string eol) =>
            eol == "\n" ? lfText : lfText.Replace("\n", eol);
    }
}
