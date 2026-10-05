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
        public string SystemPromptInstruction => "Your `Obsidian` vault contains all your notes and research documents. Use the provided tools to read, search, and explore the vault's contents.";

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
            toolList.Add(Tool.GetOrCreateTool(this, nameof(GetVaultTree), "[Obsidian] Returns a recursive outline of folders and notes so you can get oriented in the vault. Use empty string for the whole vault. Prefer this over walking folders one level at a time."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(ListAllTags), "[Obsidian] Lists all tags used across the vault with a per-tag note count. Tags come from frontmatter 'tags' and inline #tags."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(ListFolders), "[Obsidian] Lists subfolders at a given vault-relative path. Use empty string for root."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(ListNotes), "[Obsidian] Lists all notes (.md files) in a vault folder. Use empty string for root."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(ReadFrontmatter), "[Obsidian] Reads a note's YAML frontmatter properties (tags, aliases, and other metadata keys). Provide vault-relative path to the .md file."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(ReadNoteFull), "[Obsidian] Reads the full content of a note. Provide vault-relative path to the .md file."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(ReadNoteSection), "[Obsidian] Reads only the content under a specific heading in a note. Provide vault-relative path to the .md file and the heading text."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(ListSectionsInNote), "[Obsidian] Lists all headings (sections) in a note, with their heading level. Use this before ReadNoteSection to discover available section names. Provide vault-relative path to the .md file."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(ListNoteBacklinks), "[Obsidian] Use this when you encounter a [[NoteTitle]] link inside a note and want to see which other notes reference the same topic. Essential for exploring connected ideas across the vault. Provide the title (filename without .md)."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(SearchNotesByTag), "[Obsidian] Finds all notes carrying a given tag (frontmatter or inline #tag). Provide the tag with or without a leading '#'."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(SearchNotesByTitle), "[Obsidian] Searches notes by title (filename). Provide a case-insensitive substring to match against note names."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(SearchNotesByContent), "[Obsidian] Searches notes by content, returning file paths and a snippet of matching context. Provide text to search for inside notes."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(FollowLink), "[Obsidian] Use this to read a [[WikiLink]] when you want to retrieve the content of the linked note. Provide the link (without brackets)."));
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

        /// <summary>List subfolders at a given vault-relative path.</summary>
        /// <param name="folderPath">Vault-relative folder path, or empty for root.</param>
        public async Task<string> ListFolders(
            [FunctionParameter("Vault-relative folder path to list subfolders of. Use an empty string for the vault root.")] string folderPath = "")
        {
            await Task.Delay(5).ConfigureAwait(false);
            var full = Path.Combine(_vaultRoot, folderPath);
            if (!Directory.Exists(full)) return "Folder not found.";
            var dirs = Directory.GetDirectories(full)
                .Select(d => Path.GetRelativePath(_vaultRoot, d))
                .Where(d => !d.StartsWith('.'))  // skip .obsidian, .trash, etc.
                .ToArray();
            return dirs.Length == 0 ? "No subfolders." : string.Join("\n", dirs);
        }

        /// <summary>List all notes (.md files) in a vault folder.</summary>
        /// <param name="folderPath">Vault-relative folder path, or empty for root.</param>
        public async Task<string> ListNotes(
            [FunctionParameter("Vault-relative folder path to list notes (.md files) in. Use an empty string for the vault root.")] string folderPath = "")
        {
            await Task.Delay(5).ConfigureAwait(false);
            var full = Path.Combine(_vaultRoot, folderPath);
            if (!Directory.Exists(full)) return "Folder not found.";
            var files = Directory.GetFiles(full, "*.md", SearchOption.TopDirectoryOnly)
                .Select(f => Path.GetRelativePath(_vaultRoot, f))
                .ToArray();
            return files.Length == 0 ? $"No notes found in {folderPath}" : string.Join("\n", files);
        }

        /// <summary>Read the full content of a note.</summary>
        /// <param name="notePath">Vault-relative path to the .md file.</param>
        public async Task<string> ReadNoteFull(
            [FunctionParameter("Vault-relative path to the .md file to read, e.g. 'Folder/My Note.md'.")] string notePath)
        {
            await Task.Delay(5).ConfigureAwait(false);
            var full = Path.Combine(_vaultRoot, notePath);
            if (!File.Exists(full)) return $"Note not found {notePath}.";
            return File.ReadAllText(full);
        }

        /// <summary>Read only the content under a specific heading in a note.</summary>
        /// <param name="notePath">Vault-relative path to the .md file.</param>
        /// <param name="heading">The heading text to look for (without # symbols).</param>
        public async Task<string> ReadNoteSection(
            [FunctionParameter("Vault-relative path to the .md file.")] string notePath,
            [FunctionParameter("The heading text of the section to read, without the leading # symbols. Use ListNoteSections first to discover exact heading names.")] string heading)
        {
            var content = await ReadNoteFull(notePath);
            if (content.StartsWith("Note not found")) 
                return content;

            var lines = content.Split('\n');
            var sb = new StringBuilder();
            bool inSection = false;
            int sectionLevel = 0;

            foreach (var line in lines)
            {
                if (!inSection)
                {
                    var start = Regex.Match(line, $@"^(#+)\s+{Regex.Escape(heading)}\s*$", RegexOptions.IgnoreCase);
                    if (start.Success)
                    {
                        inSection = true;
                        sectionLevel = start.Groups[1].Value.Length;
                    }
                    continue;
                }
                // Stop only at a heading of the same or higher level (subsections stay in).
                var next = Regex.Match(line, @"^(#+)\s+");
                if (next.Success && next.Groups[1].Value.Length <= sectionLevel)
                    break;
                sb.AppendLine(line);
            }

            return !inSection ? $"Section '{heading}' not found in '{notePath}'." : sb.ToString();
        }

        /// <summary>List all headings (sections) in a note.</summary>
        /// <param name="notePath">Vault-relative path to the .md file.</param>
        public async Task<string> ListSectionsInNote(
            [FunctionParameter("Vault-relative path to the .md file whose headings should be listed.")] string notePath)
        {
            var content = await ReadNoteFull(notePath);
            if (content.StartsWith("Note not found"))
                return content;

            var headings = content.Split('\n')
                .Where(l => Regex.IsMatch(l, @"^#+\s+"))
                .Select(l =>
                {
                    var m = Regex.Match(l, @"^(#+)\s+(.*?)\s*$");
                    return $"{m.Groups[1].Value} {m.Groups[2].Value}";
                })
                .ToArray();

            return headings.Length == 0 ? $"No headings found in '{notePath}'." : string.Join("\n", headings);
        }

        /// <summary>Search notes by title (filename).</summary>
        /// <param name="query">Case-insensitive substring to match against note names.</param>
        public async Task<string> SearchNotesByTitle(
            [FunctionParameter("Case-insensitive substring to match against note titles (filenames without .md).")] string query)
        {
            await Task.Delay(5).ConfigureAwait(false);
            var matches = Directory
                .GetFiles(_vaultRoot, "*.md", SearchOption.AllDirectories)
                .Where(f => Path.GetFileNameWithoutExtension(f).Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetRelativePath(_vaultRoot, f)).ToArray();
            return matches.Length == 0 ? $"No matching notes for {query}." : string.Join("\n", matches);
        }

        /// <summary>Search notes by content, returning file paths and a snippet of matching context.</summary>
        /// <param name="query">Text to search for inside notes.</param>
        public async Task<string> SearchNotesByContent(
            [FunctionParameter("Case-insensitive text to search for inside note contents.")] string query)
        {
            await Task.Delay(5).ConfigureAwait(false);
            var results = new StringBuilder();
            foreach (var file in Directory.GetFiles(_vaultRoot, "*.md", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                var idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                var start = Math.Max(0, idx - 60);
                var snippet = text.Substring(start, Math.Min(120, text.Length - start)).Replace('\n', ' ');
                results.AppendLine($"{Path.GetRelativePath(_vaultRoot, file)}: ...{snippet}...");
            }
            return results.Length == 0 ? $"No matches for {query}." : results.ToString();
        }

        /// <summary>Find all notes that link to a given note via [[WikiLinks]].</summary>
        /// <param name="noteTitle">The title (filename without .md) or vault-relative path of the note to find backlinks for.</param>
        public async Task<string> ListNoteBacklinks(
            [FunctionParameter("The note to find backlinks for: either its title (filename without .md) or its vault-relative path.")] string noteTitle)
        {
            await Task.Delay(5).ConfigureAwait(false);
            // Resolve the target first so both a bare title and a Folder/Path form point at one file.
            var targetFile = ObsidianLinks.ResolveTarget(_vaultRoot, noteTitle);
            if (targetFile is null)
                return $"Note '{noteTitle}' not found.";

            var linkFinder = new Regex(@"\[\[(?<target>[^\[\]\|#]+)(?:#[^\[\]\|]*)?(?:\|[^\[\]]*)?\]\]");
            var results = new List<string>();
            foreach (var f in Directory.GetFiles(_vaultRoot, "*.md", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFullPath(f), Path.GetFullPath(targetFile), StringComparison.OrdinalIgnoreCase))
                    continue; // don't count a note linking to itself
                var text = File.ReadAllText(f);
                bool hit = linkFinder.Matches(text)
                    .Any(m => ObsidianLinks.TargetMatchesNote(_vaultRoot, m.Groups["target"].Value, targetFile));
                if (hit)
                    results.Add(Path.GetRelativePath(_vaultRoot, f));
            }
            return results.Count == 0 ? $"No backlinks found for {noteTitle}." : string.Join("\n", results);
        }

        /// <summary>Go to a [[WikiLink]] and read the linked note. Accepts bare titles or Folder/Path forms, with optional #heading or |caption.</summary>
        /// <param name="WikiLink">The link target (without the surrounding [[ ]]).</param>
        public async Task<string> FollowLink(
            [FunctionParameter("The wiki-link target without the surrounding brackets. Accepts a bare title, a 'Folder/Path' form, or an alias, and tolerates trailing #heading or |caption.")] string WikiLink)
        {
            await Task.Delay(5).ConfigureAwait(false);
            var match = ObsidianLinks.ResolveTarget(_vaultRoot, WikiLink);
            if (match is null)
            {
                return $"Linked note '{WikiLink}' not found.";
            }
            return await ReadNoteFull(Path.GetRelativePath(_vaultRoot, match));
        }

        /// <summary>Get a recursive outline of the vault's folder and note structure.</summary>
        /// <param name="folderPath">Vault-relative folder to start from, or empty for the whole vault.</param>
        /// <param name="maxDepth">Maximum folder depth to descend (default 5). Use a small number for a high-level overview.</param>
        public async Task<string> GetVaultTree(
            [FunctionParameter("Vault-relative folder to start the outline from. Use an empty string for the whole vault.")] string folderPath = "",
            [FunctionParameter("Maximum folder depth to descend. Defaults to 5; use a small number for a high-level overview.")] int maxDepth = 5)
        {
            await Task.Delay(5).ConfigureAwait(false);
            var root = Path.Combine(_vaultRoot, folderPath);
            if (!Directory.Exists(root)) return "Folder not found.";
            var sb = new StringBuilder();
            BuildTree(root, 0, maxDepth, sb);
            return sb.Length == 0 ? "Vault is empty." : sb.ToString().TrimEnd();
        }

        private void BuildTree(string dir, int depth, int maxDepth, StringBuilder sb)
        {
            var indent = new string(' ', depth * 2);
            foreach (var sub in Directory.GetDirectories(dir).OrderBy(d => d))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.')) continue; // skip .obsidian, .trash, etc.
                sb.AppendLine($"{indent}{name}/");
                if (depth + 1 < maxDepth)
                    BuildTree(sub, depth + 1, maxDepth, sb);
                else
                    sb.AppendLine($"{indent}  ...");
            }
            foreach (var file in Directory.GetFiles(dir, "*.md", SearchOption.TopDirectoryOnly).OrderBy(f => f))
                sb.AppendLine($"{indent}{Path.GetFileName(file)}");
        }

        /// <summary>Read a note's YAML frontmatter properties (tags, aliases, and any other keys).</summary>
        /// <param name="notePath">Vault-relative path to the .md file.</param>
        public async Task<string> ReadFrontmatter(
            [FunctionParameter("Vault-relative path to the .md file whose YAML frontmatter should be read.")] string notePath)
        {
            var content = await ReadNoteFull(notePath);
            if (content.StartsWith("Note not found"))
                return content;
            var (fm, _) = ObsidianMetadata.SplitFrontmatter(content);
            if (string.IsNullOrWhiteSpace(fm))
                return $"No frontmatter in '{notePath}'.";
            return fm.Trim();
        }

        /// <summary>List all tags used across the vault, with how many notes use each.</summary>
        public async Task<string> ListAllTags()
        {
            await Task.Delay(5).ConfigureAwait(false);
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(_vaultRoot, "*.md", SearchOption.AllDirectories))
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
        public async Task<string> SearchNotesByTag(
            [FunctionParameter("The tag to search for, with or without a leading '#'. Matches both frontmatter tags and inline #tags.")] string tag)
        {
            await Task.Delay(5).ConfigureAwait(false);
            var needle = tag.TrimStart('#').Trim();
            if (needle.Length == 0) return "A tag is required.";
            var results = new List<string>();
            foreach (var file in Directory.GetFiles(_vaultRoot, "*.md", SearchOption.AllDirectories))
            {
                if (ObsidianMetadata.GetTags(File.ReadAllText(file))
                        .Any(t => t.Equals(needle, StringComparison.OrdinalIgnoreCase)))
                    results.Add(Path.GetRelativePath(_vaultRoot, file));
            }
            return results.Count == 0 ? $"No notes found with tag '#{needle}'." : string.Join("\n", results);
        }
    }

    internal class ObsidianWriteTools : IToolList
    {
        public string Id => "ObsidianWrite";
        public string Description => "A set of tools for writing and editing notes in an Obsidian vault.";
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
            toolList.Add(Tool.GetOrCreateTool(this, nameof(AppendToNote), "Obsidian: Appends text to the end of an existing note. Provide vault-relative path to the .md file and the content to append."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(WriteNote), "Obsidian: Creates a note or overwrites an existing note with the full content provided. Creates parent folders if needed. Provide vault-relative path to the .md file and the full content. WARNING: this replaces the entire document."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(ReplaceString), "Obsidian: Replaces occurrences of a literal string with another string inside a note. Provide vault-relative path to the .md file, the text to find, and the replacement text. Use ListNoteSections/ReadNoteSection or ReadNoteFull first to know the exact text."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(ReplaceSection), "Obsidian: Replaces the content under a specific heading in a note, keeping the heading line. Use ListNoteSections first to discover available section names. Provide vault-relative path to the .md file, the heading text (without # symbols), and the new content for that section."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(DeleteNote), "Obsidian: Permanently deletes a note from the vault. Provide vault-relative path to the .md file."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(RenameNote), "Obsidian: Renames or moves a note and automatically updates [[WikiLinks]] in all other notes to point to the new title. Provide the current vault-relative path and the new vault-relative path (both .md)."));
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

        /// <summary>Append text to the end of an existing note.</summary>
        /// <param name="notePath">Vault-relative path to the .md file.</param>
        /// <param name="content">Content to append.</param>
        public async Task<string> AppendToNote(
            [FunctionParameter("Vault-relative path to the existing .md file to append to.")] string notePath,
            [FunctionParameter("The text to append to the end of the note.")] string content)
        {
            await Task.Delay(5).ConfigureAwait(false);
            var full = Path.Combine(_vaultRoot, notePath);
            if (!File.Exists(full))
                return "Note not found.";
            File.AppendAllText(full, "\n" + content);
            return "Content appended successfully.";
        }

        /// <summary>Create a note or overwrite an existing note with the full content provided.</summary>
        /// <param name="notePath">Vault-relative path to the .md file.</param>
        /// <param name="content">Full content to write to the note. This replaces the entire document.</param>
        public async Task<string> WriteNote(
            [FunctionParameter("Vault-relative path to the .md file. Parent folders are created if missing.")] string notePath,
            [FunctionParameter("The full content of the note. WARNING: this replaces the entire document if it already exists.")] string content)
        {
            await Task.Delay(5).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(notePath))
                return "Note path is required.";
            var full = Path.Combine(_vaultRoot, notePath);
            var existed = File.Exists(full);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(full, content);
            return existed ? "Note overwritten successfully." : "Note created successfully.";
        }

        /// <summary>Replace occurrences of a literal string with another string inside a note.</summary>
        /// <param name="notePath">Vault-relative path to the .md file.</param>
        /// <param name="find">The literal text to find.</param>
        /// <param name="replace">The text to replace it with.</param>
        public async Task<string> ReplaceString(
            [FunctionParameter("Vault-relative path to the .md file to edit.")] string notePath,
            [FunctionParameter("The exact literal text to find (not a regular expression). Every occurrence is replaced.")] string find,
            [FunctionParameter("The text to substitute in place of each occurrence of 'find'.")] string replace)
        {
            await Task.Delay(5).ConfigureAwait(false);
            var full = Path.Combine(_vaultRoot, notePath);
            if (!File.Exists(full))
                return "Note not found.";
            if (string.IsNullOrEmpty(find))
                return "The text to find must not be empty.";
            var content = File.ReadAllText(full);
            var count = Regex.Matches(content, Regex.Escape(find)).Count;
            if (count == 0)
                return $"Text to replace not found in '{notePath}'.";
            content = content.Replace(find, replace);
            File.WriteAllText(full, content);
            return $"Replaced {count} occurrence(s) in '{notePath}'.";
        }

        /// <summary>Replace the content under a specific heading in a note, keeping the heading line.</summary>
        /// <param name="notePath">Vault-relative path to the .md file.</param>
        /// <param name="heading">The heading text to look for (without # symbols).</param>
        /// <param name="content">The new content to place under the heading.</param>
        public async Task<string> ReplaceSection(
            [FunctionParameter("Vault-relative path to the .md file to edit.")] string notePath,
            [FunctionParameter("The heading text of the section to replace, without the leading # symbols. Use ListNoteSections first to find exact heading names.")] string heading,
            [FunctionParameter("The new content to place under the heading. The heading line itself is kept; the old body (including any subsections) is replaced.")] string content)
        {
            await Task.Delay(5).ConfigureAwait(false);
            var full = Path.Combine(_vaultRoot, notePath);
            if (!File.Exists(full))
                return "Note not found.";

            var lines = File.ReadAllText(full).Split('\n');
            var sb = new StringBuilder();
            bool found = false;
            bool inSection = false;
            int sectionLevel = 0;

            foreach (var line in lines)
            {
                if (!inSection)
                {
                    var start = Regex.Match(line, $@"^(#+)\s+{Regex.Escape(heading)}\s*$", RegexOptions.IgnoreCase);
                    if (!found && start.Success)
                    {
                        found = true;
                        inSection = true;
                        sectionLevel = start.Groups[1].Value.Length;
                        sb.AppendLine(line);                          // keep the heading
                        sb.AppendLine(content.TrimEnd('\n', '\r'));   // new body
                        continue;
                    }
                    sb.AppendLine(line);
                    continue;
                }
                // In the old section: skip body until a heading of same-or-higher level (subsections dropped too).
                var next = Regex.Match(line, @"^(#+)\s+");
                if (next.Success && next.Groups[1].Value.Length <= sectionLevel)
                {
                    inSection = false;
                    sb.AppendLine(line);
                }
                // else: drop the old body line
            }

            if (!found)
                return $"Section '{heading}' not found in '{notePath}'.";

            // Split('\n') then AppendLine adds a trailing newline; trim one to avoid growth.
            File.WriteAllText(full, sb.ToString().TrimEnd('\r', '\n') + "\n");
            return $"Section '{heading}' replaced in '{notePath}'.";
        }

        /// <summary>Delete a note from the vault.</summary>
        /// <param name="notePath">Vault-relative path to the .md file.</param>
        public async Task<string> DeleteNote(
            [FunctionParameter("Vault-relative path to the .md file to permanently delete.")] string notePath)
        {
            await Task.Delay(5).ConfigureAwait(false);
            var full = Path.Combine(_vaultRoot, notePath);
            if (!File.Exists(full))
                return "Note not found.";
            File.Delete(full);
            return $"Note '{notePath}' deleted.";
        }

        /// <summary>Rename (or move) a note and update [[WikiLinks]] in all other notes to point to the new title.</summary>
        /// <param name="notePath">Vault-relative path to the existing .md file.</param>
        /// <param name="newNotePath">New vault-relative path for the .md file. Folders are created if needed.</param>
        public async Task<string> RenameNote(
            [FunctionParameter("Vault-relative path to the existing .md file to rename or move.")] string notePath,
            [FunctionParameter("The new vault-relative path for the .md file. Parent folders are created if needed; wiki-links in other notes are updated automatically.")] string newNotePath)
        {
            await Task.Delay(5).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(newNotePath))
                return "New note path is required.";

            var src = Path.Combine(_vaultRoot, notePath);
            if (!File.Exists(src))
                return "Note not found.";

            var dst = Path.Combine(_vaultRoot, newNotePath);
            if (File.Exists(dst))
                return $"A note already exists at '{newNotePath}'.";

            var dstDir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dstDir))
                Directory.CreateDirectory(dstDir);
            File.Move(src, dst);

            // Rewrite links across the vault, preserving each link's form (title vs path) and any #anchor/|caption.
            int updatedFiles = 0;
            foreach (var file in Directory.GetFiles(_vaultRoot, "*.md", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase))
                    continue; // skip the note we just moved
                var text = File.ReadAllText(file);
                var (rewritten, count) = ObsidianLinks.RewriteLinksForRename(_vaultRoot, text, src, dst);
                if (count == 0)
                    continue;
                File.WriteAllText(file, rewritten);
                updatedFiles++;
            }

            return $"Note moved to '{newNotePath}'. Updated wiki-links in {updatedFiles} note(s).";
        }

    }

}
