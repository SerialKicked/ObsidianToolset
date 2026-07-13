# AGENTS.md

## What this is
A **plugin DLL**, not a standalone app. It's an Obsidian-vault toolset for [Lethe AI Sharp](https://github.com/SerialKicked/Lethe-AI-Sharp), loaded at runtime by the LetheChat host. Targets `net10.0`, nullable + implicit usings enabled.

## Build / dependencies
- Requires sibling projects checked out next to this repo: `..\LetheAISharp\` (project reference) and `..\Plugin.targets` (imported). Building standalone without them will fail.
- Build with `dotnet build` (or Visual Studio). There is no test suite, linter, or CI in this repo.
- **Post-build side effect**: `Plugin.targets` copies the output DLL into `..\LetheChat-Dev\bin\$(Configuration)\net10.0-windows10.0.17763.0\plugins\`. That path is hardcoded and must exist / be built for the plugin to actually load into the host.

## How the plugin wires in (host contracts from LetheAISharp)
- `ObsidianLethePlugin : IPluginEntry` — `Register()` is the entry point the host calls on DLL load. It reads/creates `ObsidianToolset.json` next to the DLL and stores it in the static `Settings`.
- Toolsets implement `IToolList` (`ObsidianReadTools`, `ObsidianWriteTools` in `ObsidianTools.cs`). Tools are registered in `LoadTools()` via `Tool.GetOrCreateTool(this, nameof(Method), "description")`.
- When adding a tool: it is a **public async `Task<string>` method** on the toolset, AND it must be registered in `LoadTools()`. Registering without the method (or vice versa) does nothing useful. Method **XML doc `<param>` comments feed the LLM tool schema** — keep them accurate, not just decorative.

## Runtime config (gotcha)
- Settings live in `ObsidianToolset.json` placed **beside the built DLL** (plugin dir), NOT in this repo. It is auto-created empty on first run; `VaultPath` must be set manually or all tools fail with "not found".
- All tool paths are **vault-relative**; `""` means vault root. Read tools skip dot-folders (`.obsidian`, `.trash`).

## Conventions
- Every tool method starts with `await Task.Delay(5)` before sync file IO (keeps the async signature; don't remove).
- Follow existing terse `string` return contract: return human-readable status/errors (e.g. `"Note not found."`), never throw for expected misses.
