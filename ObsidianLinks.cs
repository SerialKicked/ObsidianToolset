using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ObsidianToolset
{
    /// <summary>
    /// Shared helpers for parsing and resolving Obsidian [[WikiLinks]] consistently across tools.
    /// Two link target forms are supported, both recognised by Obsidian:
    ///   [[Page Title]]                -> resolved by filename anywhere in the vault
    ///   [[Folder/Page_Title|Caption]] -> resolved by vault-relative path
    /// A link may carry an optional #heading anchor and an optional |Caption display text,
    /// neither of which is part of the target.
    /// </summary>
    internal static class ObsidianLinks
    {
        /// <summary>Matches the inside of a single [[...]] link, capturing the raw target (before # or |).</summary>
        // [[ target ( #heading )? ( |caption )? ]]  — target stops at #, |, or ]
        private static readonly Regex LinkRegex =
            new(@"\[\[(?<target>[^\[\]\|#]+)(?<anchor>#[^\[\]\|]*)?(?<caption>\|[^\[\]]*)?\]\]",
                RegexOptions.Compiled);

        /// <summary>True if the target string is a path form (contains a folder separator) rather than a bare title.</summary>
        public static bool IsPathForm(string target) =>
            target.Contains('/') || target.Contains('\\');

        /// <summary>
        /// Normalise a raw link target (the part before any # or |) to a comparable key:
        /// path form -> vault-relative path with forward slashes, no extension;
        /// title form -> bare title. Case is preserved; callers compare case-insensitively.
        /// </summary>
        public static string NormaliseTarget(string target)
        {
            var t = target.Trim().Replace('\\', '/');
            if (t.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                t = t[..^3];
            return t.TrimEnd('/');
        }

        /// <summary>The comparable key for an actual note file, for BOTH link forms.</summary>
        /// <returns>(pathKey, titleKey) — pathKey is vault-relative w/o extension, titleKey is the filename w/o extension.</returns>
        public static (string pathKey, string titleKey) NoteKeys(string vaultRoot, string noteFullPath)
        {
            var rel = Path.GetRelativePath(vaultRoot, noteFullPath).Replace('\\', '/');
            if (rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                rel = rel[..^3];
            var title = Path.GetFileNameWithoutExtension(noteFullPath);
            return (rel, title);
        }

        /// <summary>
        /// Resolve a raw link target (with or without #/| parts already stripped) to a note file on disk.
        /// Path-form targets resolve by vault-relative path; title-form by filename. Falls back to a
        /// filename match if a path target is not found on disk. Returns null if nothing matches.
        /// </summary>
        public static string? ResolveTarget(string vaultRoot, string rawTarget)
        {
            var target = NormaliseTarget(rawTarget);
            if (target.Length == 0) return null;

            if (IsPathForm(target))
            {
                var candidate = Path.Combine(vaultRoot, target.Replace('/', Path.DirectorySeparatorChar) + ".md");
                if (File.Exists(candidate))
                    return candidate;
                // Fall back to matching just the final title component.
                target = target[(target.LastIndexOf('/') + 1)..];
            }

            // First pass: exact filename match (cheap, no file reads).
            foreach (var file in Directory.EnumerateFiles(vaultRoot, "*.md", SearchOption.AllDirectories))
            {
                if (Path.GetFileNameWithoutExtension(file).Equals(target, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
            // Second pass: match a frontmatter alias.
            foreach (var file in Directory.EnumerateFiles(vaultRoot, "*.md", SearchOption.AllDirectories))
            {
                if (ObsidianMetadata.GetAliases(File.ReadAllText(file))
                        .Any(a => a.Equals(target, StringComparison.OrdinalIgnoreCase)))
                    return file;
            }
            return null;
        }

        /// <summary>True if a raw link target refers to the given note file (either link form, incl. aliases).</summary>
        public static bool TargetMatchesNote(string vaultRoot, string rawTarget, string noteFullPath)
        {
            var target = NormaliseTarget(rawTarget);
            if (target.Length == 0) return false;
            var (pathKey, titleKey) = NoteKeys(vaultRoot, noteFullPath);
            if (IsPathForm(target))
                return target.Equals(pathKey, StringComparison.OrdinalIgnoreCase);
            if (target.Equals(titleKey, StringComparison.OrdinalIgnoreCase))
                return true;
            // Bare-title links may also point at a frontmatter alias.
            return File.Exists(noteFullPath)
                && ObsidianMetadata.GetAliases(File.ReadAllText(noteFullPath))
                    .Any(a => a.Equals(target, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Rewrite every [[link]] in <paramref name="text"/> that points at <paramref name="oldNoteFullPath"/>
        /// so it points at <paramref name="newNoteFullPath"/> instead, preserving each link's form
        /// (bare title stays a title, path form stays a path) plus any #anchor and |caption.
        /// Alias-based links (e.g. [[SomeAlias]]) are left untouched since the alias still resolves.
        /// </summary>
        /// <returns>The rewritten text and how many links were changed.</returns>
        public static (string text, int count) RewriteLinksForRename(
            string vaultRoot, string text, string oldNoteFullPath, string newNoteFullPath)
        {
            var (oldPathKey, oldTitleKey) = NoteKeys(vaultRoot, oldNoteFullPath);
            var (newPathKey, newTitle) = NoteKeys(vaultRoot, newNoteFullPath);

            int count = 0;
            var result = LinkRegex.Replace(text, m =>
            {
                var target = NormaliseTarget(m.Groups["target"].Value);
                bool isPath = IsPathForm(target);

                // Only rewrite links whose target IS the old title/path. Alias links resolve on their
                // own and are intentionally preserved.
                bool matches = isPath
                    ? target.Equals(oldPathKey, StringComparison.OrdinalIgnoreCase)
                    : target.Equals(oldTitleKey, StringComparison.OrdinalIgnoreCase);
                if (!matches)
                    return m.Value;

                count++;
                var replacementTarget = isPath ? newPathKey : newTitle;
                return $"[[{replacementTarget}{m.Groups["anchor"].Value}{m.Groups["caption"].Value}]]";
            });
            return (result, count);
        }
    }
}
