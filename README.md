# MauiDevFlow

Unified tooling for automating and debugging .NET MAUI apps — both native MAUI and Blazor Hybrid.
Built to enable AI agents (and humans) to build, deploy, inspect, and debug MAUI apps entirely
from the terminal.

## What This Is (and Isn't)

MauiDevFlow is designed for **agentic development workflows** — giving AI coding agents full
autonomy over the MAUI dev loop: build, deploy, inspect, interact, diagnose, fix, and repeat.

It is **not** a UI testing framework, and it is **not** meant to ship in your app. The agent and
debug bridge are intended for `#if DEBUG` only. Think of it as giving your AI pair-programmer
eyes and hands inside the running app so it can close the feedback loop on its own — verify its
changes work, see what went wrong when they don't, and iterate without waiting for a human to
manually check the simulator.

## Features

- **Native MAUI Automation** — Visual tree inspection, element interaction (tap, fill, clear), screenshots via in-app Agent
- **Blazor WebView Debugging** — CDP bridge using Chobitsu for JavaScript evaluation, DOM manipulation, page navigation
- **CLI Tool** (`maui-devflow`) — Scriptable commands for both native and Blazor automation
- **Driver Library** — Platform-aware (Mac Catalyst, Android, iOS Simulator) orchestration
- **AI Skill** — Claude Code skill (`.claude/skills/maui-ai-debugging`) for AI-driven development workflows

## Quick Start

### 1. Add Agent to your MAUI App

```csharp
// MauiProgram.cs
using MauiDevFlow.Agent;
using MauiDevFlow.Blazor;

var builder = MauiApp.CreateBuilder();
// ... your existing setup ...

#if DEBUG
builder.Services.AddBlazorWebViewDeveloperTools();
builder.AddMauiDevFlowAgent(options => { options.Port = 9223; });
builder.AddMauiBlazorDevFlowTools(options => { options.Port = 9222; }); // Blazor Hybrid only
#endif
```

**Agent options:** `Port` (default 9223), `Enabled` (default true), `MaxTreeDepth` (0 = unlimited).

**Blazor options:** `Port` (default 9222), `Enabled` (default true), `EnableWebViewInspection` (default true), `EnableLogging` (default true in DEBUG).

Both methods extend `MauiAppBuilder`. Chobitsu.js is embedded in the `MauiDevFlow.Blazor` library — no wwwroot copy needed.

### 2. Install the CLI Tool

```bash
dotnet tool install --global Redth.MauiDevFlow.CLI
```

Companion tools for device/emulator management:

```bash
dotnet tool install --global androidsdk.tool    # android (SDK, AVD, device management)
dotnet tool install --global appledev.tools     # apple (simulators, provisioning, certificates)
```

### 3. Run Commands

```bash
# Check agent connection
maui-devflow MAUI status

# Dump visual tree
maui-devflow MAUI tree

# Find elements
maui-devflow MAUI query --type Button
maui-devflow MAUI query --text "Submit"

# Interact with elements
maui-devflow MAUI tap <elementId>
maui-devflow MAUI fill <elementId> "Hello World"
maui-devflow MAUI clear <elementId>

# Take screenshot
maui-devflow MAUI screenshot --output screen.png

# Get element property
maui-devflow MAUI property <elementId> IsVisible

# Get element details
maui-devflow MAUI element <elementId>

# Shell navigation (for Shell-based apps)
maui-devflow MAUI navigate "//native"
maui-devflow MAUI navigate "//blazor"
```

## Blazor WebView CDP Commands

For Blazor Hybrid apps with the CDP bridge configured:

```bash
# Check CDP connection
maui-devflow cdp status

# Page snapshot (useful for AI agents — accessible text representation of DOM)
maui-devflow cdp snapshot

# Evaluate JavaScript
maui-devflow cdp Runtime evaluate "document.title"

# DOM queries
maui-devflow cdp DOM querySelector "button.btn-primary"
maui-devflow cdp DOM querySelectorAll "a.nav-link"
maui-devflow cdp DOM getOuterHTML "div.container"

# Input actions
maui-devflow cdp Input dispatchClickEvent "button"
maui-devflow cdp Input fill "input#name" "John"
maui-devflow cdp Input insertText "typed text"

# Page navigation
maui-devflow cdp Page navigate "https://example.com"
maui-devflow cdp Page reload
maui-devflow cdp Page captureScreenshot
```

> **Note:** On iOS/Mac Catalyst, `Page.reload`, `Page.navigate`, and `Input.insertText` are
> intercepted and handled via native WKWebView APIs. This is necessary because these operations
> destroy/recreate the JavaScript context — the Chobitsu debug bridge is automatically
> re-injected after reload/navigation completes.

## Agent API

The Agent runs inside the MAUI app and exposes an HTTP/JSON REST API on port 9223.

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/status` | GET | Agent version, platform, app info |
| `/api/tree?depth=N` | GET | Visual tree as JSON |
| `/api/element/{id}` | GET | Single element by ID |
| `/api/query?type=&text=&automationId=` | GET | Find matching elements |
| `/api/action/tap` | POST | Tap element `{"elementId":"..."}` |
| `/api/action/fill` | POST | Fill text `{"elementId":"...","text":"..."}` |
| `/api/action/clear` | POST | Clear text `{"elementId":"..."}` |
| `/api/action/focus` | POST | Focus element `{"elementId":"..."}` |
| `/api/screenshot` | GET | PNG screenshot |
| `/api/property/{id}/{name}` | GET | Get property value |

## Project Structure

```
MauiDevFlow.sln
.claude/skills/
└── maui-ai-debugging/          # AI skill for Claude Code
src/
├── MauiDevFlow.Agent/          # In-app agent (NuGet library for MAUI apps)
├── MauiDevFlow.Blazor/         # Blazor WebView CDP bridge (Chobitsu)
├── MauiDevFlow.CLI/            # CLI tool (maui-devflow)
├── MauiDevFlow.Driver/         # Driver library (connects to Agent, platform tools)
└── SampleMauiApp/              # Sample todo app (native + Blazor Hybrid via Shell)
tests/
├── MauiDevFlow.Console/        # Console test app for CDP
└── MauiDevFlow.Tests/          # Unit and integration tests
docs/
└── setup-guides/               # Platform-specific setup (Android, Apple, Windows)
```

### Sample App

The `SampleMauiApp` is a shared-state todo list with both a native MAUI page and a Blazor
Hybrid page, connected via Shell navigation (`//native` and `//blazor` routes). It demonstrates:

- **Shared `TodoService`** — both pages read/write the same data, changes reflect immediately
- **Shell navigation** — switch between tabs via `maui-devflow MAUI navigate "//native"` etc.
- **Dark mode support** — both native (AppThemeBinding) and Blazor (`@media prefers-color-scheme`) adapt to system theme
- **Description field** — todo items support title + optional description on both pages

## Platform Support

| Platform | Agent | CDP/Blazor | Network Setup |
|----------|-------|------------|---------------|
| Mac Catalyst | ✅ | ✅ | Direct localhost |
| iOS Simulator | ✅ | ✅ | Shares host network |
| Android Emulator | ✅ | 🔄 | `adb reverse tcp:9223 tcp:9223` |
| Windows | 🔄 | 🔄 | Direct localhost |

### Companion Tools

| Tool | Install | Purpose |
|------|---------|---------|
| `maui-devflow` | `dotnet tool install -g Redth.MauiDevFlow.CLI` | App automation & Blazor CDP |
| `android` | `dotnet tool install -g androidsdk.tool` | Android SDK, AVD, device management |
| `apple` | `dotnet tool install -g appledev.tools` | iOS simulators, provisioning, certificates |
| `adb` | Android SDK platform-tools | Device communication, port forwarding, logs |
| `xcrun simctl` | Xcode command-line tools | iOS simulator lifecycle |

## AI Agent Integration

This project includes a Claude Code skill (`.claude/skills/maui-ai-debugging`) that teaches AI
agents the complete build → deploy → inspect → fix feedback loop. The skill covers:

- Installing and configuring all required tools
- Building and deploying to iOS simulators, Android emulators, and Mac Catalyst
- Using `maui-devflow` to inspect visual trees, interact with elements, and take screenshots
- Using CDP to inspect and manipulate Blazor WebView content
- Managing simulators/emulators with `apple`, `android`, `xcrun simctl`, and `adb`

### Installing the Skill

The CLI can download the latest skill files directly from GitHub into your project:

```bash
# Interactive — shows files and asks for confirmation
maui-devflow update-skill

# Skip confirmation
maui-devflow update-skill -y

# Download to a specific directory
maui-devflow update-skill -o /path/to/my-project

# Use a different branch
maui-devflow update-skill -b dev
```

This downloads the skill files into `.claude/skills/maui-ai-debugging/` relative to the output
directory (or current directory if `--output` is not specified). Existing files are overwritten.
The file list is discovered dynamically from the repository, so new reference docs are picked up
automatically.

## License

MIT
