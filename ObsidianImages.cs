using System.Text.RegularExpressions;

namespace ObsidianToolset
{
    /// <summary>
    /// Finds the images embedded in a note and resolves them to something the engine can attach to a tool result:
    /// an absolute local file path (for images inside the vault) or an http(s) URL (for remote images).
    /// Supported syntaxes:
    ///   ![[image.png]] / ![[Folder/image.png|300]]  -> Obsidian embed, by file name anywhere in the vault or by vault path
    ///   ![alt](path/to/image.png)                   -> markdown image, relative to the note, then to the vault root
    ///   ![alt](https://example.com/image.png)       -> remote image, forwarded untouched
    /// Images inside fenced code blocks are ignored.
    /// </summary>
    internal static class ObsidianImages
    {
        /// <summary>Extensions treated as images for local files.</summary>
        private static readonly HashSet<string> ImageExtensions =
            new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

        // ![[target#anchor|size]]
        private static readonly Regex WikiEmbed =
            new(@"!\[\[(?<target>[^\[\]\|#]+)(?:#[^\[\]\|]*)?(?:\|[^\[\]]*)?\]\]", RegexOptions.Compiled);

        // ![alt](target "title")  or  ![alt](<target with spaces> "title")
        private static readonly Regex MarkdownImage =
            new(@"!\[[^\]]*\]\(\s*(?:<(?<target>[^>]+)>|(?<target>[^)\s]+))(?:\s+(?:""[^""]*""|'[^']*'))?\s*\)", RegexOptions.Compiled);

        private static readonly Regex FencedCode =
            new(@"^(?<fence>```|~~~).*?^\k<fence>[ \t]*$", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.Singleline);

        /// <summary>
        /// Resolve the images linked in a note, in document order, without duplicates.
        /// </summary>
        /// <param name="vaultRoot">Vault root folder.</param>
        /// <param name="noteFullPath">Full path of the note the text comes from (used to resolve relative links).</param>
        /// <param name="text">The note's text.</param>
        /// <param name="max">Maximum number of images to return.</param>
        /// <returns>Local absolute paths and/or http(s) URLs.</returns>
        public static List<string> Resolve(string vaultRoot, string noteFullPath, string text, int max)
        {
            var result = new List<string>();
            if (max <= 0 || string.IsNullOrEmpty(text))
                return result;

            // Blank out code blocks (keeping offsets) so example syntax inside them is not picked up.
            var scan = FencedCode.Replace(text, m => new string(' ', m.Length));

            var refs = new List<(int index, string target)>();
            foreach (Match m in WikiEmbed.Matches(scan))
                refs.Add((m.Index, m.Groups["target"].Value));
            foreach (Match m in MarkdownImage.Matches(scan))
                refs.Add((m.Index, m.Groups["target"].Value));
            refs.Sort((a, b) => a.index.CompareTo(b.index));

            var noteDir = Path.GetDirectoryName(noteFullPath);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, target) in refs)
            {
                var resolved = ResolveTarget(vaultRoot, noteDir, target);
                if (resolved is null || !seen.Add(resolved))
                    continue;
                result.Add(resolved);
                if (result.Count >= max)
                    break;
            }
            return result;
        }

        private static string? ResolveTarget(string vaultRoot, string? noteDir, string rawTarget)
        {
            var target = rawTarget.Trim();
            if (target.Length == 0)
                return null;

            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return target;

            if (target.Contains("://") || target.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return null; // other schemes (file://, data: URIs...) are not forwarded

            // Markdown links are URL-encoded ("My%20Image.png"); wiki embeds are not, but decoding is harmless.
            try { target = Uri.UnescapeDataString(target); }
            catch (UriFormatException) { }

            // Strip a #fragment (wiki embeds already exclude it; markdown links may carry one).
            var hash = target.IndexOf('#');
            if (hash >= 0)
                target = target[..hash];
            target = target.Trim().Replace('\\', '/');

            if (!ImageExtensions.Contains(Path.GetExtension(target)))
                return null;

            // Relative to the note first (markdown convention), then relative to the vault root, then by bare file name.
            if (noteDir is not null && !Path.IsPathFullyQualified(target))
            {
                var nearby = VaultIO.ResolveFile(vaultRoot, Path.Combine(noteDir, target));
                if (nearby is not null)
                    return nearby;
            }
            var rooted = VaultIO.ResolveFile(vaultRoot, target);
            if (rooted is not null)
                return rooted;

            return VaultIO.FindFileByName(vaultRoot, Path.GetFileName(target), noteDir);
        }
    }
}
