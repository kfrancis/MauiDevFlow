# AGENTS.md

Instructions for AI agents working on the MauiDevFlow codebase.

## Project Overview

MauiDevFlow is a toolkit for AI-assisted .NET MAUI app development. It provides:
- **In-app Agent** (`MauiDevFlow.Agent`) — HTTP API running inside the MAUI app for visual tree inspection, element interaction, screenshots, and logging
- **Blazor CDP Bridge** (`MauiDevFlow.Blazor`) — Chrome DevTools Protocol support via Chobitsu for Blazor Hybrid WebView debugging
- **CLI Tool** (`MauiDevFlow.CLI`) — Terminal commands for both native MAUI and Blazor automation
- **Driver Library** (`MauiDevFlow.Driver`) — Platform-aware orchestration (Mac Catalyst, Android, iOS)
- **AI Skill** (`.claude/skills/maui-ai-debugging/`) — Skill files teaching AI agents the full build→deploy→inspect→fix workflow

## Architecture

```
CLI (dotnet global tool) ──HTTP──▶ Agent (runs inside MAUI app, single port)
                                     ├── /api/tree, /api/screenshot, /api/logs, etc.
                                     └── /api/cdp ──EvalJS──▶ Chobitsu (in BlazorWebView)
```

- **Single port** (default 9223, configurable via `.mauidevflow` file) serves both native MAUI commands and CDP
- **No WebSocket** — CDP uses HTTP POST request/response via Chobitsu's synchronous JS eval
- **Blazor→Agent wiring** uses reflection to avoid a direct package dependency between the two NuGet packages

## Building & Testing

```bash
# Restore, build, and test (use ci.slnf to avoid building the sample app)
dotnet restore ci.slnf
dotnet build ci.slnf
dotnet test ci.slnf

# Build the sample app for Mac Catalyst
dotnet build src/SampleMauiApp -f net10.0-maccatalyst

# Run the locally-built CLI
dotnet run --project src/MauiDevFlow.CLI -- <args>
```

The solution filter `ci.slnf` excludes `SampleMauiApp` (requires MAUI workloads). Use the full `MauiDevFlow.sln` when working on the sample app.

## Key Conventions

- **Always verify changes with the sample app** — build, deploy (Mac Catalyst is fastest), and test end-to-end
- **Do NOT auto-commit or push** unless explicitly asked
- **`#if DEBUG` only** — Agent and Blazor packages are debug-only tools, never ship in release builds
- **Reflection for cross-package wiring** — `BlazorDevFlowExtensions.WireAgentCdp()` connects Blazor→Agent via reflection to avoid NuGet dependency
- **Embedded JS resources** — Scripts in `Resources/Scripts/*.js` are embedded at build time and loaded via `ScriptResources.Load()`
- **Port configuration** — `.mauidevflow` JSON file in project dir, read by both MSBuild targets and CLI

## NuGet Packaging

Three packages are published on release:
- `Redth.MauiDevFlow.Agent` — In-app agent (MAUI library)
- `Redth.MauiDevFlow.Blazor` — Blazor CDP bridge (MAUI Razor library)
- `Redth.MauiDevFlow.CLI` — Global dotnet tool

**Versioning:** `Directory.Build.props` has the shared version for Agent/Blazor. CLI has its own version in its `.csproj`. The publish workflow uses the Git tag as `PackageVersion`, overriding both.

**Release process:** Create a GitHub release with tag `vX.Y.Z` → triggers `publish.yml` → builds, tests, packs, and pushes to NuGet.org.

## Skill System

The `.claude/skills/maui-ai-debugging/` directory contains AI skill files:
- `SKILL.md` — Main skill document with full command reference and workflows
- `references/setup.md` — Detailed setup guide
- `references/*.md` — Platform-specific guides

The CLI command `maui-devflow update-skill` downloads the latest skill files from GitHub. When updating skill docs, keep `SKILL.md` as the authoritative command reference.

## Logging Architecture

- **Native logs**: `ILogger` → `FileLogProvider` → rotating JSONL files → `/api/logs` endpoint
- **WebView logs**: JS `console.*` → intercepted by `console-intercept.js` → buffered in `window.__webviewLogs` → drained every 2s by native timer → written to same JSONL files with `source: "webview"`
- Log entries have a `source` field (`"native"` or `"webview"`) for filtering via `?source=` query param
