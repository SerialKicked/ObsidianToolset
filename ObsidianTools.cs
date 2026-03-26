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
            toolList.Add(Tool.GetOrCreateTool(this, nameof(ListNoteFolders), "Obsidian: Lists subfolders at a given vault-relative path. Use empty string for root."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(ListNotes), "Obsidian: Lists all notes (.md files) in a vault folder. Use empty string for root."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(ReadNoteFull), "Obsidian: Reads the full content of a note. Provide vault-relative path to the .md file."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(ListNoteSections), "Obsidian: Lists all headings (sections) in a note, with their heading level. Use this before ReadNoteSection to discover available section names. Provide vault-relative path to the .md file."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(ReadNoteSection), "Obsidian: Reads only the content under a specific heading in a note. Provide vault-relative path to the .md file and the heading text."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(SearchNotesByTitle), "Obsidian: Searches notes by title (filename). Provide a case-insensitive substring to match against note names."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(SearchNotesByContent), "Obsidian: Searches notes by content, returning file paths and a snippet of matching context. Provide text to search for inside notes."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(GetNoteBacklinks), "Obsidian: Use this when you encounter a [[NoteTitle]] link inside a note and want to see which other notes reference the same topic. Essential for exploring connected ideas across the vault. Provide the title (filename without .md)."));
            toolList.Add(Tool.GetOrCreateTool(this, nameof(DisplayLink), "Obsidian: Use this to read a [[WikiLink]] when you want to retrieve the content of the linked note. Provide the link (without brackets)."));
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
        public async Task<string> ListNoteFolders(string folderPath = "")
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
        public async Task<string> ListNotes(string folderPath = "")
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
        public async Task<string> ReadNoteFull(string notePath)
        {
            await Task.Delay(5).ConfigureAwait(false);
            var full = Path.Combine(_vaultRoot, notePath);
            if (!File.Exists(full)) return $"Note not found {notePath}.";
            return File.ReadAllText(full);
        }

        /// <summary>Read only the content under a specific heading in a note.</summary>
        /// <param name="notePath">Vault-relative path to the .md file.</param>
        /// <param name="heading">The heading text to look for (without # symbols).</param>
        public async Task<string> ReadNoteSection(string notePath, string heading)
        {
            var content = await ReadNoteFull(notePath);
            if (content.StartsWith("Note not found")) 
                return content;

            var lines = content.Split('\n');
            var sb = new StringBuilder();
            bool inSection = false;

            foreach (var line in lines)
            {
                if (Regex.IsMatch(line, $@"^#+\s+{Regex.Escape(heading)}\s*$", RegexOptions.IgnoreCase))
                {
                    inSection = true;
                    continue;
                }
                if (inSection && Regex.IsMatch(line, @"^#+\s+"))
                    break; // next heading — stop
                if (inSection)
                    sb.AppendLine(line);
            }

            return sb.Length == 0 ? $"Section '{heading}' not found in '{notePath}'." : sb.ToString();
        }

        /// <summary>List all headings (sections) in a note.</summary>
        /// <param name="notePath">Vault-relative path to the .md file.</param>
        public async Task<string> ListNoteSections(string notePath)
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
        public async Task<string> SearchNotesByTitle(string query)
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
        public async Task<string> SearchNotesByContent(string query)
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
        /// <param name="noteTitle">The title (filename without .md) to find backlinks for.</param>
        public async Task<string> GetNoteBacklinks(string noteTitle)
        {
            await Task.Delay(5).ConfigureAwait(false);
            var pattern = new Regex($@"\[\[{Regex.Escape(noteTitle)}([\|#\]])", RegexOptions.IgnoreCase);
            var results = Directory.GetFiles(_vaultRoot, "*.md", SearchOption.AllDirectories).Where(f => pattern.IsMatch(File.ReadAllText(f)))
                .Select(f => Path.GetRelativePath(_vaultRoot, f)).ToArray();
            return results.Length == 0 ? $"No backlinks found for {noteTitle}." : string.Join("\n", results);
        }

        /// <summary>Go to a [[WikiLinks]].</summary>
        /// <param name="noteTitle">The link.</param>
        public async Task<string> DisplayLink(string WikiLink)
        {
            await Task.Delay(5).ConfigureAwait(false);
            var matches = Directory
                .GetFiles(_vaultRoot, "*.md", SearchOption.AllDirectories)
                .Where(f => Path.GetFileNameWithoutExtension(f).Equals(WikiLink, StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetRelativePath(_vaultRoot, f)).ToArray();
            if (matches.Length == 0)
            {
                return $"Linked note '{WikiLink}' not found.";
            }
            return await ReadNoteFull(matches[0]);
        }
    }

    internal class ObsidianWriteTools : IToolList
    {
        public string Id => "ObsidianWrite";
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
        public async Task<string> AppendToNote(string notePath, string content)
        {
            await Task.Delay(5).ConfigureAwait(false);
            var full = Path.Combine(_vaultRoot, notePath);
            if (!File.Exists(full))
                return "Note not found.";
            File.AppendAllText(full, "\n" + content);
            return "Content appended successfully.";
        }

    }

}
