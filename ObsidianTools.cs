using HNSW.Net;
using LetheAISharp.Agent.Tools;
using OpenAI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ObsidianToolset
{
    internal class ObsidianReadTools : IToolList
    {
        public string Id => "ObsidianRead";
        public string Description => "A set of tools for reading and searching notes in an Obsidian vault. These tools allow the bot to explore the structure of the vault, read note contents, and discover connections between notes.";
        public string SystemPromptInstruction => "You have access to [Obsidian] tools allowing you to read, search, and explore your vault's contents and documents. Be proactive, if the user references something as a `#tag` or mentions activities not mentioned in your prompt, check the vault first.";

        private List<Tool> toolList = [];

        private string _vaultRoot => ObsidianLethePlugin.Settings.VaultPath;

        public IReadOnlyList<Tool> GetToolList() => toolList;

        public void LoadTools(bool clearExisting = false)
        {
            toolList.Clear();
            if (clearExisting)
            {
                Tool.ClearRegisteredTools();
            }
            toolList.Add(Tool.GetOrCreateTool(this, nameof(VaultGetTree), "[Obsidian] Returns a recursive outline of folders and notes so you can get oriented in the vault. Use empty string for the whole vault. Prefer this over walking folders one level at a time."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(VaultListTags), "[Obsidian] Lists all tags used across the vault with a per-tag note count. Tags come from frontmatter 'tags' and inline #tags."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(VaultListFolders), "[Obsidian] Lists subfolders at a given vault-relative path. Use empty string for root."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(VaultListNotes), "[Obsidian] Lists all notes (.md files) in a vault folder. Use empty string for root."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(VaultReadFrontmatter), "[Obsidian] Reads a note's YAML frontmatter properties (tags, summary, and other metadata keys). Provide vault-relative path to the .md file."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(VaultReadNote), "[Obsidian] Reads the full content of a note. Provide vault-relative path to the .md file."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(VaultReadSection), "[Obsidian] Reads only the content under a specific heading in a note. Provide vault-relative path to the .md file and the heading text."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(VaultListSections), "[Obsidian] Lists all headings (sections) in a note, with their heading level. Use this before VaultReadSection to discover available section names. Provide vault-relative path to the .md file."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(VaultListBacklinks), "[Obsidian] Use this when you encounter a [[NoteTitle]] link inside a note and want to see which other notes reference the same topic. Essential for exploring connected ideas across the vault. Provide the title (filename without .md)."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(VaultSearchByTag), "[Obsidian] Finds all notes carrying a given tag (frontmatter or inline #tag). Provide the tag with or without a leading '#'."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(VaultSearchByTitle), "[Obsidian] Searches notes by title (filename). Provide a case-insensitive substring to match against note names."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(VaultSearchByContent), "[Obsidian] Searches notes by content, returning file paths and a snippet of matching context. Provide text to search for inside notes."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(VaultFollowLink), "[Obsidian] Use this to retrieve the note attached to a [[WikiLink]]. Provide the link without brackets."));
        }

        public void UnloadTools()
        {
            foreach (var tool in toolList)
            {
                Tool.TryUnregisterTool(tool);
            }
            toolList.Clear();
        }

        public bool RequiresConfirmation(string functionName)
        {
            return false;
        }

        /// <summary>
        /// Read a note as LF-normalised text. Returns the vault-relative path (forward slashes) on success,
        /// or a ready-to-return error message.
        /// </summary>
        private (string? text, string rel, string? error) ReadNoteLf(string notePath)
        {
            var full = VaultIO.ResolveNote(_vaultRoot, notePath);
            if (full is null)
                return (null, string.Empty, VaultIO.NoteNotFound(_vaultRoot, notePath));
            var text = VaultIO.ToLf(VaultIO.ReadText(full).text);
            return (text, VaultIO.ToRel(_vaultRoot, full), null);
        }

        /// <summary>List subfolders at a given vault-relative path.</summary>
        /// <param name="folderPath">Vault-relative folder path, or empty for root.</param>
        public async Task<string> VaultListFolders(
            [FunctionParameter("Vault-relative folder path to list subfolders of. Use an empty string for the vault root.")] string folderPath = "")
        {
            await Task.Delay(5).ConfigureAwait(false);
            var full = VaultIO.ResolveFolder(_vaultRoot, folderPath);
            if (full is null) return $"Folder not found: '{VaultIO.CleanArg(folderPath)}'.";
            var dirs = Directory.GetDirectories(full)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))  // skip .obsidian, .trash, etc.
                .Select(d => VaultIO.ToRel(_vaultRoot, d))
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return dirs.Length == 0 ? "No subfolders." : string.Join("\n", dirs);
        }

        /// <summary>List all notes (.md files) in a vault folder.</summary>
        /// <param name="folderPath">Vault-relative folder path, or empty for root.</param>
        public async Task<string> VaultListNotes(
            [FunctionParameter("Vault-relative folder path to list notes (.md files) in. Use an empty string for the vault root.")] string folderPath = "")
        {
            await Task.Delay(5).ConfigureAwait(false);
            var full = VaultIO.ResolveFolder(_vaultRoot, folderPath);
            if (full is null) return $"Folder not found: '{VaultIO.CleanArg(folderPath)}'.";
            var files = Directory.GetFiles(full, "*.md", SearchOption.TopDirectoryOnly)
                .Select(f => VaultIO.ToRel(_vaultRoot, f))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return files.Length == 0 ? $"No notes found in '{VaultIO.CleanArg(folderPath)}'." : string.Join("\n", files);
        }

        /// <summary>Read the full content of a note.</summary>
        /// <param name="notePath">Vault-relative path to the .md file.</param>
        public async Task<string> VaultReadNote(
            [FunctionParameter("Vault-relative path to the .md file to read, e.g. 'Folder/My Note.md'.")] string notePath)
        {
            await Task.Delay(5).ConfigureAwait(false);
            var (text, _, error) = ReadNoteLf(notePath);
            return error ?? text!;
        }

        /// <summary>Read only the content under a specific heading in a note.</summary>
        /// <param name="notePath">Vault-relative path to the .md file.</param>
        /// <param name="heading">The heading text to look for (without # symbols).</param>
        public async Task<string> VaultReadSection(
            [FunctionParameter("Vault-relative path to the .md file.")] string notePath,
            [FunctionParameter("The heading text of the section to read, without the leading # symbols. Use VaultListSections first to discover exact heading names.")] string heading)
        {
            await Task.Delay(5).ConfigureAwait(false);
            var (text, rel, error) = ReadNoteLf(notePath);
            if (error is not null)
                return error;

            var lines = MarkdownSections.SplitLines(text!, out _);
            var sec = MarkdownSections.Find(lines, heading);
            if (sec is null)
                return $"Section '{VaultIO.CleanArg(heading)}' not found in '{rel}'.\n{MarkdownSections.DescribeHeadings(lines)}";

            var s = sec.Value;
            var body = string.Join("\n", lines.Skip(s.Heading.Line + 1).Take(s.EndLine - s.Heading.Line - 1)).TrimEnd();
            return body.Length == 0 ? $"Section '{s.Heading.Text}' in '{rel}' is empty." : body;
        }

        /// <summary>List all headings (sections) in a note.</summary>
        /// <param name="notePath">Vault-relative path to the .md file.</param>
        public async Task<string> VaultListSections(
            [FunctionParameter("Vault-relative path to the .md file whose headings should be listed.")] string notePath)
        {
            await Task.Delay(5).ConfigureAwait(false);
            var (text, rel, error) = ReadNoteLf(notePath);
            if (error is not null)
                return error;

            var headings = MarkdownSections.ParseHeadings(MarkdownSections.SplitLines(text!, out _))
                .Select(h => $"{new string('#', h.Level)} {h.Text}")
                .ToArray();

            return headings.Length == 0 ? $"No headings found in '{rel}'." : string.Join("\n", headings);
        }

        /// <summary>Search notes by title (filename).</summary>
        /// <param name="query">Case-insensitive substring to match against note names.</param>
        public async Task<string> VaultSearchByTitle(
            [FunctionParameter("Case-insensitive substring to match against note titles (filenames without .md).")] string query)
        {
            await Task.Delay(5).ConfigureAwait(false);
            query = VaultIO.CleanArg(query);
            if (query.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                query = query[..^3];
            var matches = VaultIO.EnumerateNotes(_vaultRoot)
                .Where(f => Path.GetFileNameWithoutExtension(f).Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(f => VaultIO.ToRel(_vaultRoot, f)).ToArray();
            return matches.Length == 0 ? $"No matching notes for {query}." : string.Join("\n", matches);
        }

        /// <summary>Search notes by content, returning file paths and a snippet of matching context.</summary>
        /// <param name="query">Text to search for inside notes.</param>
        public async Task<string> VaultSearchByContent(
            [FunctionParameter("Case-insensitive text to search for inside note contents.")] string query)
        {
            await Task.Delay(5).ConfigureAwait(false);
            query = VaultIO.ToLf(VaultIO.CleanArg(query));
            if (query.Length == 0) return "Text to search for is required.";
            var results = new StringBuilder();
            foreach (var file in VaultIO.EnumerateNotes(_vaultRoot))
            {
                var text = VaultIO.ToLf(File.ReadAllText(file));
                var idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                var start = Math.Max(0, idx - 60);
                var snippet = text.Substring(start, Math.Min(120, text.Length - start)).Replace('\n', ' ');
                results.AppendLine($"{VaultIO.ToRel(_vaultRoot, file)}: ...{snippet}...");
            }
            return results.Length == 0 ? $"No matches for {query}." : results.ToString();
        }

        /// <summary>Find all notes that link to a given note via [[WikiLinks]].</summary>
        /// <param name="noteTitle">The title (filename without .md) or vault-relative path of the note to find backlinks for.</param>
        public async Task<string> VaultListBacklinks(
            [FunctionParameter("The note to find backlinks for: either its title (filename without .md) or its vault-relative path.")] string noteTitle)
        {
            await Task.Delay(5).ConfigureAwait(false);
            // Resolve the target first so both a bare title and a Folder/Path form point at one file.
            var targetFile = ObsidianLinks.ResolveTarget(_vaultRoot, noteTitle);
            if (targetFile is null)
                return $"Note '{VaultIO.CleanArg(noteTitle)}' not found.";

            var linkFinder = new Regex(@"\[\[(?<target>[^\[\]\|#]+)(?:#[^\[\]\|]*)?(?:\|[^\[\]]*)?\]\]");
            var results = new List<string>();
            foreach (var f in VaultIO.EnumerateNotes(_vaultRoot))
            {
                if (string.Equals(Path.GetFullPath(f), Path.GetFullPath(targetFile), StringComparison.OrdinalIgnoreCase))
                    continue; // don't count a note linking to itself
                var text = File.ReadAllText(f);
                bool hit = linkFinder.Matches(text)
                    .Any(m => ObsidianLinks.TargetMatchesNote(_vaultRoot, m.Groups["target"].Value, targetFile));
                if (hit)
                    results.Add(VaultIO.ToRel(_vaultRoot, f));
            }
            return results.Count == 0 ? $"No backlinks found for {VaultIO.CleanArg(noteTitle)}." : string.Join("\n", results);
        }

        /// <summary>Go to a [[WikiLink]] and read the linked note. Accepts bare titles or Folder/Path forms, with optional #heading or |caption.</summary>
        /// <param name="WikiLink">The link target (without the surrounding [[ ]]).</param>
        public async Task<string> VaultFollowLink(
            [FunctionParameter("The wiki-link target without the surrounding brackets. Accepts a bare title, a 'Folder/Path' form, or an alias, and tolerates trailing #heading or |caption.")] string WikiLink)
        {
            await Task.Delay(5).ConfigureAwait(false);
            var link = VaultIO.CleanArg(WikiLink);
            // Tolerate a model passing the brackets anyway, and strip any #heading / |caption.
            if (link.StartsWith("[[")) link = link[2..];
            if (link.EndsWith("]]")) link = link[..^2];
            var cut = link.IndexOfAny(['|', '#']);
            if (cut >= 0) link = link[..cut];

            var match = ObsidianLinks.ResolveTarget(_vaultRoot, link);
            if (match is null)
            {
                return $"Linked note '{VaultIO.CleanArg(WikiLink)}' not found.";
            }
            return VaultIO.ToLf(VaultIO.ReadText(match).text);
        }

        /// <summary>Get a recursive outline of the vault's folder and note structure.</summary>
        /// <param name="folderPath">Vault-relative folder to start from, or empty for the whole vault.</param>
        /// <param name="maxDepth">Maximum folder depth to descend (default 5). Use a small number for a high-level overview.</param>
        public async Task<string> VaultGetTree(
            [FunctionParameter("Vault-relative folder to start the outline from. Use an empty string for the whole vault.")] string folderPath = "",
            [FunctionParameter("Maximum folder depth to descend. Defaults to 5; use a small number for a high-level overview.")] int maxDepth = 5)
        {
            await Task.Delay(5).ConfigureAwait(false);
            var root = VaultIO.ResolveFolder(_vaultRoot, folderPath);
            if (root is null) return $"Folder not found: '{VaultIO.CleanArg(folderPath)}'.";
            var sb = new StringBuilder();
            BuildTree(root, 0, maxDepth, sb);
            return sb.Length == 0 ? "Vault is empty." : sb.ToString().TrimEnd();
        }

        private void BuildTree(string dir, int depth, int maxDepth, StringBuilder sb)
        {
            var indent = new string(' ', depth * 2);
            foreach (var sub in Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.')) continue; // skip .obsidian, .trash, etc.
                sb.Append(indent).Append(name).Append("/\n");
                if (depth + 1 < maxDepth)
                    BuildTree(sub, depth + 1, maxDepth, sb);
                else
                    sb.Append(indent).Append("  ...\n");
            }
            foreach (var file in Directory.GetFiles(dir, "*.md", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                sb.Append(indent).Append(Path.GetFileName(file)).Append('\n');
        }

        /// <summary>Read a note's YAML frontmatter properties (tags, aliases, and any other keys).</summary>
        /// <param name="notePath">Vault-relative path to the .md file.</param>
        public async Task<string> VaultReadFrontmatter(
            [FunctionParameter("Vault-relative path to the .md file whose YAML frontmatter should be read.")] string notePath)
        {
            await Task.Delay(5).ConfigureAwait(false);
            var (text, rel, error) = ReadNoteLf(notePath);
            if (error is not null)
                return error;
            var (fm, _) = ObsidianMetadata.SplitFrontmatter(text!);
            if (string.IsNullOrWhiteSpace(fm))
                return $"No frontmatter in '{rel}'.";
            return fm.Trim();
        }

        /// <summary>List all tags used across the vault, with how many notes use each.</summary>
        public async Task<string> VaultListTags()
        {
            await Task.Delay(5).ConfigureAwait(false);
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in VaultIO.EnumerateNotes(_vaultRoot))
            {
                foreach (var tag in ObsidianMetadata.GetTags(File.ReadAllText(file)))
                    counts[tag] = counts.TryGetValue(tag, out var c) ? c + 1 : 1;
            }
            if (counts.Count == 0) return "No tags found in the vault.";
            return string.Join("\n", counts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => $"#{kv.Key} ({kv.Value})"));
        }

        /// <summary>Find all notes carrying a given tag (frontmatter tags or inline #tags).</summary>
        /// <param name="tag">The tag to search for, with or without a leading '#'.</param>
        public async Task<string> VaultSearchByTag(
            [FunctionParameter("The tag to search for, with or without a leading '#'. Matches both frontmatter tags and inline #tags.")] string tag)
        {
            await Task.Delay(5).ConfigureAwait(false);
            var needle = VaultIO.CleanArg(tag).TrimStart('#').Trim();
            if (needle.Length == 0) return "A tag is required.";
            var results = new List<string>();
            foreach (var file in VaultIO.EnumerateNotes(_vaultRoot))
            {
                if (ObsidianMetadata.GetTags(File.ReadAllText(file))
                        .Any(t => t.Equals(needle, StringComparison.OrdinalIgnoreCase)))
                    results.Add(VaultIO.ToRel(_vaultRoot, file));
            }
            return results.Count == 0 ? $"No notes found with tag '#{needle}'." : string.Join("\n", results);
        }
    }

    internal class ObsidianWriteTools : IToolList
    {
        public string Id => "ObsidianWrite";
        public string Description => "A set of [Obsidian] tools for writing and editing notes in your Obsidian vault.";
        public string SystemPromptInstruction => string.Empty;

        private List<Tool> toolList = [];

        private string _vaultRoot => ObsidianLethePlugin.Settings.VaultPath;

        public IReadOnlyList<Tool> GetToolList() => toolList;

        public void LoadTools(bool clearExisting = false)
        {
            toolList.Clear();
            if (clearExisting)
            {
                Tool.ClearRegisteredTools();
            }
            toolList.Add(Tool.GetOrCreateTool(this, nameof(VaultAppendToNote), "[Obsidian] Appends text to the end of an existing note. Provide vault-relative path to the .md file and the content to append."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(VaultWriteNote), "[Obsidian] Creates a note or overwrites an existing note with the full content provided. Creates parent folders if needed. Provide vault-relative path to the .md file and the full content. WARNING: this replaces the entire document."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(VaultReplaceString), "[Obsidian] Replaces occurrences of a literal string with another string inside a note. Provide vault-relative path to the .md file, the text to find, and the replacement text. Use VaultReadNote or VaultReadSection first to know the exact text. Differences in line endings and leading/trailing whitespace are tolerated; text may span several lines."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(VaultReplaceSection), "[Obsidian] Replaces the content under a specific heading in a note, keeping the heading line. Use VaultListSections first to discover available section names. Provide vault-relative path to the .md file, the heading text (without # symbols), and the new content for that section."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(VaultDeleteNote), "[Obsidian] Permanently deletes a note from the vault. Provide vault-relative path to the .md file."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(VaultRenameNote), "[Obsidian] Renames or moves a note and automatically updates [[WikiLinks]] in all other notes to point to the new title. Provide the current vault-relative path and the new vault-relative path (both .md)."));
        }

        public void UnloadTools()
        {
            foreach (var tool in toolList)
            {
                Tool.TryUnregisterTool(tool);
            }
            toolList.Clear();
        }

        public bool RequiresConfirmation(string functionName)
        {
            return false;
        }

        private const string InvalidPathHint = "Use a vault-relative path such as 'Folder/My Note.md'.";

        /// <summary>Append text to the end of an existing note.</summary>
        /// <param name="notePath">Vault-relative path to the .md file.</param>
        /// <param name="content">Content to append.</param>
        public async Task<string> VaultAppendToNote(
            [FunctionParameter("Vault-relative path to the existing .md file to append to.")] string notePath,
            [FunctionParameter("The text to append to the end of the note.")] string content)
        {
            await Task.Delay(5).ConfigureAwait(false);
            var full = VaultIO.ResolveNote(_vaultRoot, notePath);
            if (full is null)
                return VaultIO.NoteNotFound(_vaultRoot, notePath);

            var existing = VaultIO.ReadText(full).text;
            var eol = VaultIO.DetectEol(existing);
            var addition = VaultIO.ApplyEol(VaultIO.ToLf(content), eol);
            // Separate from the existing text with one line break (nothing if the note is empty).
            File.AppendAllText(full, (existing.Length == 0 ? string.Empty : eol) + addition);
            return $"Content appended successfully to '{VaultIO.ToRel(_vaultRoot, full)}'.";
        }

        /// <summary>Create a note or overwrite an existing note with the full content provided.</summary>
        /// <param name="notePath">Vault-relative path to the .md file.</param>
        /// <param name="content">Full content to write to the note. This replaces the entire document.</param>
        public async Task<string> VaultWriteNote(
            [FunctionParameter("Vault-relative path to the .md file. Parent folders are created if missing.")] string notePath,
            [FunctionParameter("The full content of the note. WARNING: this replaces the entire document if it already exists.")] string content)
        {
            await Task.Delay(5).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(notePath))
                return "Note path is required.";
            var full = VaultIO.ResolveNewNote(_vaultRoot, notePath);
            if (full is null)
                return $"Invalid note path '{VaultIO.CleanArg(notePath)}'. {InvalidPathHint}";

            var existed = File.Exists(full);
            string eol = Environment.NewLine;
            bool bom = false;
            if (existed)
            {
                var (old, oldBom) = VaultIO.ReadText(full);
                eol = VaultIO.DetectEol(old);
                bom = oldBom;
            }

            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            VaultIO.WriteText(full, VaultIO.ApplyEol(VaultIO.ToLf(content), eol), bom);
            var rel = VaultIO.ToRel(_vaultRoot, full);
            return existed ? $"Note '{rel}' overwritten successfully." : $"Note '{rel}' created successfully.";
        }

        /// <summary>Replace occurrences of a literal string with another string inside a note.</summary>
        /// <param name="notePath">Vault-relative path to the .md file.</param>
        /// <param name="find">The literal text to find.</param>
        /// <param name="replace">The text to replace it with.</param>
        public async Task<string> VaultReplaceString(
            [FunctionParameter("Vault-relative path to the .md file to edit.")] string notePath,
            [FunctionParameter("The literal text to find (not a regular expression); may span several lines. Every occurrence is replaced. Leading/trailing whitespace and line-ending differences are tolerated.")] string find,
            [FunctionParameter("The text to substitute in place of each occurrence of 'find'. May be empty to delete the found text.")] string replace)
        {
            await Task.Delay(5).ConfigureAwait(false);
            var full = VaultIO.ResolveNote(_vaultRoot, notePath);
            if (full is null)
                return VaultIO.NoteNotFound(_vaultRoot, notePath);
            var rel = VaultIO.ToRel(_vaultRoot, full);

            var findLf = VaultIO.ToLf(find);
            if (findLf.Trim().Length == 0)
                return "The text to find must not be empty.";
            var replaceLf = VaultIO.ToLf(replace);

            var (raw, bom) = VaultIO.ReadText(full);
            var eol = VaultIO.DetectEol(raw);
            var doc = VaultIO.ToLf(raw); // all matching happens on LF text, the file's own line endings are restored on write

            var hit = TryReplace(doc, findLf, replaceLf);
            if (hit is null)
                return $"Text to replace not found in '{rel}'.{FirstLineHint(doc, findLf)}";

            var (result, count, how) = hit.Value;
            if (result == doc)
                return $"No change: the replacement is identical to the existing text in '{rel}'.";

            VaultIO.WriteText(full, VaultIO.ApplyEol(result, eol), bom);
            var note = how is null ? string.Empty : $" (matched {how})";
            return $"Replaced {count} occurrence(s) in '{rel}'{note}.";
        }

        // Progressively more forgiving matching. Each tier is only tried when the stricter one found nothing.
        private static (string result, int count, string? how)? TryReplace(string doc, string find, string replace)
        {
            var hit = TryReplaceTiers(doc, find, replace);
            if (hit is not null)
                return hit;

            // Last resort: the model sent escape sequences as literal characters ("\n" as backslash + n).
            if (find.Contains("\\n") || find.Contains("\\t"))
            {
                hit = TryReplaceTiers(doc, Unescape(find), Unescape(replace));
                if (hit is not null)
                    return (hit.Value.result, hit.Value.count, "after interpreting \\n / \\t escape sequences");
            }
            return null;
        }

        private static (string result, int count, string? how)? TryReplaceTiers(string doc, string find, string replace)
        {
            // 1. Exact.
            var count = CountOrdinal(doc, find);
            if (count > 0)
                return (doc.Replace(find, replace), count, null);

            var ft = find.Trim();
            if (ft.Length == 0)
                return null;
            var rt = replace.Trim();

            // 2. Ignoring leading/trailing whitespace (and blank lines) around the search text.
            if (ft != find)
            {
                count = CountOrdinal(doc, ft);
                if (count > 0)
                    return (doc.Replace(ft, rt), count, "ignoring leading/trailing whitespace");
            }

            // 3. Ignoring differences in indentation, trailing spaces and runs of spaces/tabs.
            var rx = new Regex(BuildFlexiblePattern(ft), RegexOptions.CultureInvariant);
            count = rx.Matches(doc).Count;
            if (count > 0)
                return (rx.Replace(doc, _ => rt), count, "ignoring whitespace differences");

            return null;
        }

        private static readonly Regex HorizontalWs = new(@"[\p{Zs}\t]+", RegexOptions.Compiled);

        private static string BuildFlexiblePattern(string trimmedFind)
        {
            var parts = trimmedFind.Split('\n').Select(line =>
                string.Join(@"[\p{Zs}\t]+", HorizontalWs.Split(line.Trim()).Select(Regex.Escape)));
            return string.Join(@"[\p{Zs}\t]*\n[\p{Zs}\t]*", parts);
        }

        private static string Unescape(string s) =>
            s.Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\t", "\t");

        private static int CountOrdinal(string text, string value)
        {
            int count = 0, idx = 0;
            while ((idx = text.IndexOf(value, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += value.Length;
            }
            return count;
        }

        /// <summary>Help the model recover: tell it if at least the first line of its text exists in the note.</summary>
        private static string FirstLineHint(string doc, string find)
        {
            var first = find.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            if (first is null) return string.Empty;
            var idx = doc.IndexOf(first, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return " Re-read the note with VaultReadNote or VaultReadSection and copy the exact text.";
            var line = doc.AsSpan(0, idx).Count('\n') + 1;
            return $" The first line of your text does appear near line {line}, but the whole text does not match. Re-read that part of the note and copy it exactly, or use a shorter piece of text.";
        }

        /// <summary>Replace the content under a specific heading in a note, keeping the heading line.</summary>
        /// <param name="notePath">Vault-relative path to the .md file.</param>
        /// <param name="heading">The heading text to look for (without # symbols).</param>
        /// <param name="content">The new content to place under the heading.</param>
        public async Task<string> VaultReplaceSection(
            [FunctionParameter("Vault-relative path to the .md file to edit.")] string notePath,
            [FunctionParameter("The heading text of the section to replace, without the leading # symbols. Use VaultListSections first to find exact heading names.")] string heading,
            [FunctionParameter("The new content to place under the heading. The heading line itself is kept (do not repeat it); the old body (including any subsections) is replaced.")] string content)
        {
            await Task.Delay(5).ConfigureAwait(false);
            var full = VaultIO.ResolveNote(_vaultRoot, notePath);
            if (full is null)
                return VaultIO.NoteNotFound(_vaultRoot, notePath);
            var rel = VaultIO.ToRel(_vaultRoot, full);

            var (raw, bom) = VaultIO.ReadText(full);
            var eol = VaultIO.DetectEol(raw);
            var lines = MarkdownSections.SplitLines(VaultIO.ToLf(raw), out var trailingNewline);

            var found = MarkdownSections.Find(lines, heading);
            if (found is null)
                return $"Section '{VaultIO.CleanArg(heading)}' not found in '{rel}'.\n{MarkdownSections.DescribeHeadings(lines)}";
            var sec = found.Value;

            // New body: LF lines without leading/trailing blank lines.
            var body = VaultIO.ToLf(content).Split('\n').ToList();
            TrimBlankEdges(body);
            // The model often repeats the heading line itself; the heading is kept already, so drop the duplicate.
            if (body.Count > 0
                && MarkdownSections.TryParseHeadingLine(body[0], out var lvl, out var txt)
                && lvl == sec.Heading.Level
                && string.Equals(MarkdownSections.Norm(txt), MarkdownSections.Norm(sec.Heading.Text), StringComparison.OrdinalIgnoreCase))
            {
                body.RemoveAt(0);
                TrimBlankEdges(body);
            }

            int s = sec.Heading.Line, e = sec.EndLine;
            bool blankAfterHeading = body.Count > 0 && s + 1 < e && lines[s + 1].Trim().Length == 0; // keep the note's own style

            var result = new List<string>(lines.Take(s + 1));
            if (blankAfterHeading)
                result.Add(string.Empty);
            result.AddRange(body);
            if (e < lines.Count)
                result.Add(string.Empty); // blank line between the new body and the next heading
            result.AddRange(lines.Skip(e));

            var text = string.Join("\n", result) + (trailingNewline ? "\n" : string.Empty);
            VaultIO.WriteText(full, VaultIO.ApplyEol(text, eol), bom);

            var msg = $"Section '{sec.Heading.Text}' replaced in '{rel}'.";
            if (sec.MatchCount > 1)
                msg += $" Note: {sec.MatchCount} sections have this heading; only the first was replaced.";
            return msg;
        }

        private static void TrimBlankEdges(List<string> lines)
        {
            while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
            while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        }

        /// <summary>Delete a note from the vault.</summary>
        /// <param name="notePath">Vault-relative path to the .md file.</param>
        public async Task<string> VaultDeleteNote(
            [FunctionParameter("Vault-relative path to the .md file to permanently delete.")] string notePath)
        {
            await Task.Delay(5).ConfigureAwait(false);
            var full = VaultIO.ResolveNote(_vaultRoot, notePath);
            if (full is null)
                return VaultIO.NoteNotFound(_vaultRoot, notePath);
            File.Delete(full);
            return $"Note '{VaultIO.ToRel(_vaultRoot, full)}' deleted.";
        }

        /// <summary>Rename (or move) a note and update [[WikiLinks]] in all other notes to point to the new title.</summary>
        /// <param name="notePath">Vault-relative path to the existing .md file.</param>
        /// <param name="newNotePath">New vault-relative path for the .md file. Folders are created if needed.</param>
        public async Task<string> VaultRenameNote(
            [FunctionParameter("Vault-relative path to the existing .md file to rename or move.")] string notePath,
            [FunctionParameter("The new vault-relative path for the .md file. Parent folders are created if needed; wiki-links in other notes are updated automatically.")] string newNotePath)
        {
            await Task.Delay(5).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(newNotePath))
                return "New note path is required.";

            var src = VaultIO.ResolveNote(_vaultRoot, notePath);
            if (src is null)
                return VaultIO.NoteNotFound(_vaultRoot, notePath);

            var dst = VaultIO.ResolveNewNote(_vaultRoot, newNotePath);
            if (dst is null)
                return $"Invalid new note path '{VaultIO.CleanArg(newNotePath)}'. {InvalidPathHint}";
            var dstRel = VaultIO.ToRel(_vaultRoot, dst);

            // A case-only rename ("note.md" -> "Note.md") points at the same file on Windows; that is allowed.
            bool sameFile = string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase);
            if (File.Exists(dst) && !sameFile)
                return $"A note already exists at '{dstRel}'.";

            var dstDir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dstDir))
                Directory.CreateDirectory(dstDir);
            File.Move(src, dst);

            // Rewrite links across the vault, preserving each link's form (title vs path) and any #anchor/|caption.
            int updatedFiles = 0;
            foreach (var file in VaultIO.EnumerateNotes(_vaultRoot))
            {
                if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase))
                    continue; // skip the note we just moved
                var (text, bom) = VaultIO.ReadText(file);
                var (rewritten, count) = ObsidianLinks.RewriteLinksForRename(_vaultRoot, text, src, dst);
                if (count == 0)
                    continue;
                VaultIO.WriteText(file, rewritten, bom);
                updatedFiles++;
            }

            return $"Note moved to '{dstRel}'. Updated wiki-links in {updatedFiles} note(s).";
        }

    }

}
