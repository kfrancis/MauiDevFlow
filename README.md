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
- **Unified Logging** — Native `ILogger` and WebView `console.log/warn/error` unified into a single log stream with source filtering
- **CLI Tool** (`maui-devflow`) — Scriptable commands for both native and Blazor automation
- **Driver Library** — Platform-aware (Mac Catalyst, Android, iOS Simulator) orchestration
- **AI Skill** — Claude Code skill (`.claude/skills/maui-ai-debugging`) for AI-driven development workflows

## Quick Start

### AI-Assisted Setup (Recommended)

The fastest way to get started is to let your AI agent set everything up using the included skill:

**1. Install the CLI tool and download the skill:**

```bash
dotnet tool install --global Redth.MauiDevFlow.CLI
maui-devflow update-skill
```

**2. Ask your AI agent to set up MauiDevFlow:**

> Use the maui-ai-debugging skill to integrate MauiDevFlow into my MAUI app.
> Add the NuGet packages, configure MauiProgram.cs, and set up everything
> needed for debugging. Then build, run, and verify it works.

The skill includes a complete setup guide with a step-by-step checklist, platform-specific
configuration (entitlements, port forwarding), and the full command reference. The agent
will handle NuGet packages, code changes, Blazor script tags, and verification.

### Manual Setup

<details>
<summary>Click to expand manual setup steps</summary>

#### 1. Add NuGet Packages

```xml
<PackageReference Include="Redth.MauiDevFlow.Agent" Version="*" />
<PackageReference Include="Redth.MauiDevFlow.Blazor" Version="*" />  <!-- Blazor Hybrid only -->
```

#### 2. Configure MauiProgram.cs

```csharp
// MauiProgram.cs
using MauiDevFlow.Agent;
using MauiDevFlow.Blazor;

var builder = MauiApp.CreateBuilder();
// ... your existing setup ...

#if DEBUG
builder.Services.AddBlazorWebViewDeveloperTools();
builder.AddMauiDevFlowAgent();
builder.AddMauiBlazorDevFlowTools(); // Blazor Hybrid only
#endif
```

**Agent options:** `Port` (default 9223), `Enabled` (default true), `MaxTreeDepth` (0 = unlimited). Port is also configurable via `.mauidevflow` or `-p:MauiDevFlowPort=XXXX`.

**Blazor options:** `Enabled` (default true), `EnableWebViewInspection` (default true), `EnableLogging` (default true in DEBUG). CDP commands are routed through the agent port — no separate Blazor port needed.

Both methods extend `MauiAppBuilder`.

#### 3. Port Configuration (optional)

Create a `.mauidevflow` in your project directory to use a custom port:

```json
{ "port": 9347 }
```

Both the build and CLI auto-detect this file — no flags needed. Useful when running multiple MAUI projects simultaneously.

#### 3. Blazor Hybrid: Add Script Tag

Blazor Hybrid apps also need a script tag in `wwwroot/index.html` (the `chobitsu.js` file is delivered automatically by the NuGet package as a static web asset):

```html
<script src="chobitsu.js"></script>  <!-- Add before </body> -->
```

#### 4. Install CLI Tools

```bash
dotnet tool install --global Redth.MauiDevFlow.CLI
dotnet tool install --global androidsdk.tool    # android (SDK, AVD, device management)
dotnet tool install --global appledev.tools     # apple (simulators, provisioning, certificates)
```

#### 5. Platform-Specific Setup

- **Mac Catalyst:** Add `com.apple.security.network.server` entitlement (see [setup guide](.claude/skills/maui-ai-debugging/references/setup.md#5-mac-catalyst-entitlements))
- **Android Emulator:** Run `adb reverse tcp:9223 tcp:9223`
- **iOS Simulator:** No extra setup needed

</details>

### Verify It Works

```bash
# Check agent connection
maui-devflow MAUI status

# Dump visual tree
maui-devflow MAUI tree

# Take screenshot
maui-devflow MAUI screenshot --output screen.png

# Fetch application logs
maui-devflow MAUI logs --limit 50

# Filter by source (native ILogger or Blazor WebView console)
maui-devflow MAUI logs --source webview
maui-devflow MAUI logs --source native

# Live edit native properties (no rebuild)
maui-devflow MAUI set-property HeaderLabel TextColor "Tomato"
maui-devflow MAUI set-property HeaderLabel FontSize "32"

# Blazor WebView (if applicable)
maui-devflow cdp status
maui-devflow cdp snapshot
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

# Live CSS/DOM editing (experiment without rebuilding)
maui-devflow cdp Runtime evaluate "document.querySelector('h1').style.color = 'tomato'"
maui-devflow cdp Runtime evaluate "document.querySelectorAll('.item').forEach(el => el.style.borderRadius = '20px')"
maui-devflow cdp Runtime evaluate "document.documentElement.style.setProperty('--bg-color', '#1a1a2e')"
maui-devflow cdp Runtime evaluate "document.head.insertAdjacentHTML('beforeend', '<style>.btn { background: hotpink !important; }</style>')"
```

> **Tip:** Live CSS/DOM edits are immediate and non-destructive — changes are lost on page reload,
> making them safe for experimentation before committing to code.

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
| `/api/property/{id}/{name}` | POST | Set property `{"value":"..."}` |
| `/api/logs?limit=N&skip=N&source=S` | GET | Application logs (source: `native`, `webview`, or omit for all) |
| `/api/cdp` | POST | Forward CDP command to Blazor WebView |

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
