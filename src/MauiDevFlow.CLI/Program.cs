using System.CommandLine;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace MauiDevFlow.CLI;

/// <summary>
/// CDP-oriented CLI for automating MAUI Blazor WebViews.
/// Commands mirror CDP domain/method patterns for familiarity.
/// </summary>
class Program
{
    private static readonly string DefaultEndpoint = "ws://localhost:9222/devtools/browser";
    
    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("MauiDevFlow CLI - automate MAUI apps via Agent API and Blazor WebViews via CDP");
        
        // ===== CDP commands (Blazor WebView) =====
        var endpointOption = new Option<string>(
            ["--endpoint", "-e"],
            () => DefaultEndpoint,
            "CDP WebSocket endpoint");
        
        var cdpCommand = new Command("cdp", "Blazor WebView automation via Chrome DevTools Protocol")
        {
            endpointOption
        };
        
        // Browser domain commands
        var browserCommand = new Command("Browser", "Browser domain commands");
        
        var getVersionCmd = new Command("getVersion", "Get browser version info");
        getVersionCmd.SetHandler(async (endpoint) => await BrowserGetVersionAsync(endpoint), endpointOption);
        browserCommand.Add(getVersionCmd);
        
        cdpCommand.Add(browserCommand);
        
        // Runtime domain commands  
        var runtimeCommand = new Command("Runtime", "Runtime domain commands");
        
        var evaluateArg = new Argument<string>("expression", "JavaScript expression");
        var evaluateCmd = new Command("evaluate", "Evaluate JavaScript expression") { evaluateArg };
        evaluateCmd.SetHandler(async (endpoint, expr) => await RuntimeEvaluateAsync(endpoint, expr), endpointOption, evaluateArg);
        runtimeCommand.Add(evaluateCmd);
        
        cdpCommand.Add(runtimeCommand);
        
        // DOM domain commands
        var domCommand = new Command("DOM", "DOM domain commands");
        
        var getDocumentCmd = new Command("getDocument", "Get document root node");
        getDocumentCmd.SetHandler(async (endpoint) => await DomGetDocumentAsync(endpoint), endpointOption);
        domCommand.Add(getDocumentCmd);
        
        var querySelectorArg = new Argument<string>("selector", "CSS selector");
        var querySelectorCmd = new Command("querySelector", "Find element by CSS selector") { querySelectorArg };
        querySelectorCmd.SetHandler(async (endpoint, selector) => await DomQuerySelectorAsync(endpoint, selector), endpointOption, querySelectorArg);
        domCommand.Add(querySelectorCmd);
        
        var querySelectorAllArg = new Argument<string>("selector", "CSS selector");
        var querySelectorAllCmd = new Command("querySelectorAll", "Find all elements by CSS selector") { querySelectorAllArg };
        querySelectorAllCmd.SetHandler(async (endpoint, selector) => await DomQuerySelectorAllAsync(endpoint, selector), endpointOption, querySelectorAllArg);
        domCommand.Add(querySelectorAllCmd);
        
        var getOuterHtmlArg = new Argument<string>("selector", "CSS selector");
        var getOuterHtmlCmd = new Command("getOuterHTML", "Get element HTML") { getOuterHtmlArg };
        getOuterHtmlCmd.SetHandler(async (endpoint, selector) => await DomGetOuterHtmlAsync(endpoint, selector), endpointOption, getOuterHtmlArg);
        domCommand.Add(getOuterHtmlCmd);
        
        cdpCommand.Add(domCommand);
        
        // Page domain commands
        var pageCommand = new Command("Page", "Page domain commands");
        
        var navigateArg = new Argument<string>("url", "URL to navigate to");
        var navigateCmd = new Command("navigate", "Navigate to URL") { navigateArg };
        navigateCmd.SetHandler(async (endpoint, url) => await PageNavigateAsync(endpoint, url), endpointOption, navigateArg);
        pageCommand.Add(navigateCmd);
        
        var reloadCmd = new Command("reload", "Reload page");
        reloadCmd.SetHandler(async (endpoint) => await PageReloadAsync(endpoint), endpointOption);
        pageCommand.Add(reloadCmd);
        
        var captureScreenshotCmd = new Command("captureScreenshot", "Capture page screenshot (base64)");
        captureScreenshotCmd.SetHandler(async (endpoint) => await PageCaptureScreenshotAsync(endpoint), endpointOption);
        pageCommand.Add(captureScreenshotCmd);
        
        cdpCommand.Add(pageCommand);
        
        // Input domain commands
        var inputCommand = new Command("Input", "Input domain commands");
        
        var clickSelectorArg = new Argument<string>("selector", "CSS selector of element to click");
        var dispatchClickCmd = new Command("dispatchClickEvent", "Click element by selector") { clickSelectorArg };
        dispatchClickCmd.SetHandler(async (endpoint, selector) => await InputDispatchClickAsync(endpoint, selector), endpointOption, clickSelectorArg);
        inputCommand.Add(dispatchClickCmd);
        
        var insertTextArg = new Argument<string>("text", "Text to insert");
        var insertTextCmd = new Command("insertText", "Insert text at cursor") { insertTextArg };
        insertTextCmd.SetHandler(async (endpoint, text) => await InputInsertTextAsync(endpoint, text), endpointOption, insertTextArg);
        inputCommand.Add(insertTextCmd);
        
        var fillSelectorArg = new Argument<string>("selector", "CSS selector");
        var fillTextArg = new Argument<string>("text", "Text to fill");
        var fillCmd = new Command("fill", "Fill form field with text") { fillSelectorArg, fillTextArg };
        fillCmd.SetHandler(async (endpoint, selector, text) => await InputFillAsync(endpoint, selector, text), endpointOption, fillSelectorArg, fillTextArg);
        inputCommand.Add(fillCmd);
        
        cdpCommand.Add(inputCommand);
        
        // Convenience commands
        var statusCmd = new Command("status", "Check CDP connection status");
        statusCmd.SetHandler(async (endpoint) => await StatusAsync(endpoint), endpointOption);
        cdpCommand.Add(statusCmd);
        
        var snapshotCmd = new Command("snapshot", "Get simplified DOM snapshot with element refs");
        snapshotCmd.SetHandler(async (endpoint) => await SnapshotAsync(endpoint), endpointOption);
        cdpCommand.Add(snapshotCmd);
        
        rootCommand.Add(cdpCommand);
        
        // ===== MAUI Native commands =====
        var agentPortOption = new Option<int>(
            ["--agent-port", "-ap"],
            () => 9223,
            "Agent HTTP port");
        var agentHostOption = new Option<string>(
            ["--agent-host", "-ah"],
            () => "localhost",
            "Agent HTTP host");
        var platformOption = new Option<string>(
            ["--platform", "-p"],
            () => "maccatalyst",
            "Target platform (maccatalyst, android, ios)");

        var mauiCommand = new Command("MAUI", "Native MAUI app automation commands")
        {
            agentPortOption,
            agentHostOption,
            platformOption
        };

        // MAUI status
        var mauiStatusCmd = new Command("status", "Check agent connection");
        mauiStatusCmd.SetHandler(async (host, port) => await MauiStatusAsync(host, port), agentHostOption, agentPortOption);
        mauiCommand.Add(mauiStatusCmd);

        // MAUI tree
        var treeDepthOption = new Option<int>("--depth", () => 0, "Max tree depth (0=unlimited)");
        var mauiTreeCmd = new Command("tree", "Dump visual tree") { treeDepthOption };
        mauiTreeCmd.SetHandler(async (host, port, depth) => await MauiTreeAsync(host, port, depth), agentHostOption, agentPortOption, treeDepthOption);
        mauiCommand.Add(mauiTreeCmd);

        // MAUI query
        var queryTypeOption = new Option<string?>("--type", "Filter by element type");
        var queryAutoIdOption = new Option<string?>("--automationId", "Filter by AutomationId");
        var queryTextOption = new Option<string?>("--text", "Filter by text content");
        var mauiQueryCmd = new Command("query", "Find elements") { queryTypeOption, queryAutoIdOption, queryTextOption };
        mauiQueryCmd.SetHandler(async (host, port, type, autoId, text) =>
            await MauiQueryAsync(host, port, type, autoId, text),
            agentHostOption, agentPortOption, queryTypeOption, queryAutoIdOption, queryTextOption);
        mauiCommand.Add(mauiQueryCmd);

        // MAUI tap
        var tapIdArg = new Argument<string>("elementId", "Element ID to tap");
        var mauiTapCmd = new Command("tap", "Tap element") { tapIdArg };
        mauiTapCmd.SetHandler(async (host, port, id) => await MauiTapAsync(host, port, id), agentHostOption, agentPortOption, tapIdArg);
        mauiCommand.Add(mauiTapCmd);

        // MAUI fill
        var fillIdArg = new Argument<string>("elementId", "Element ID");
        var fillTextArg2 = new Argument<string>("text", "Text to fill");
        var mauiFillCmd = new Command("fill", "Fill text into element") { fillIdArg, fillTextArg2 };
        mauiFillCmd.SetHandler(async (host, port, id, text) => await MauiFillAsync(host, port, id, text), agentHostOption, agentPortOption, fillIdArg, fillTextArg2);
        mauiCommand.Add(mauiFillCmd);

        // MAUI clear
        var clearIdArg = new Argument<string>("elementId", "Element ID to clear");
        var mauiClearCmd = new Command("clear", "Clear text from element") { clearIdArg };
        mauiClearCmd.SetHandler(async (host, port, id) => await MauiClearAsync(host, port, id), agentHostOption, agentPortOption, clearIdArg);
        mauiCommand.Add(mauiClearCmd);

        // MAUI screenshot
        var screenshotOutputOption = new Option<string?>("--output", "Output file path");
        var mauiScreenshotCmd = new Command("screenshot", "Take screenshot") { screenshotOutputOption };
        mauiScreenshotCmd.SetHandler(async (host, port, output) => await MauiScreenshotAsync(host, port, output), agentHostOption, agentPortOption, screenshotOutputOption);
        mauiCommand.Add(mauiScreenshotCmd);

        // MAUI property
        var propIdArg = new Argument<string>("elementId", "Element ID");
        var propNameArg = new Argument<string>("propertyName", "Property name");
        var mauiPropertyCmd = new Command("property", "Get element property") { propIdArg, propNameArg };
        mauiPropertyCmd.SetHandler(async (host, port, id, name) => await MauiPropertyAsync(host, port, id, name), agentHostOption, agentPortOption, propIdArg, propNameArg);
        mauiCommand.Add(mauiPropertyCmd);

        // MAUI element
        var elementIdArg = new Argument<string>("elementId", "Element ID");
        var mauiElementCmd = new Command("element", "Get element details") { elementIdArg };
        mauiElementCmd.SetHandler(async (host, port, id) => await MauiElementAsync(host, port, id), agentHostOption, agentPortOption, elementIdArg);
        mauiCommand.Add(mauiElementCmd);

        // MAUI navigate (Shell)
        var navRouteArg = new Argument<string>("route", "Shell route (e.g. //blazor)");
        var mauiNavigateCmd = new Command("navigate", "Navigate to Shell route") { navRouteArg };
        mauiNavigateCmd.SetHandler(async (host, port, route) => await MauiNavigateAsync(host, port, route), agentHostOption, agentPortOption, navRouteArg);
        mauiCommand.Add(mauiNavigateCmd);

        rootCommand.Add(mauiCommand);

        // ===== update-skill command =====
        var forceOption = new Option<bool>(
            ["--force", "-y"],
            "Skip confirmation prompt");
        var outputDirOption = new Option<string?>(
            ["--output", "-o"],
            "Output directory (defaults to current directory)");
        var branchOption = new Option<string>(
            ["--branch", "-b"],
            () => "main",
            "GitHub branch to download from");
        var updateSkillCmd = new Command("update-skill", "Download the latest maui-ai-debugging skill from GitHub")
        {
            forceOption, outputDirOption, branchOption
        };
        updateSkillCmd.SetHandler(async (force, output, branch) => await UpdateSkillAsync(force, output, branch), forceOption, outputDirOption, branchOption);
        rootCommand.Add(updateSkillCmd);
        
        return await rootCommand.InvokeAsync(args);
    }
    
    // ===== Browser Domain =====
    
    private static async Task BrowserGetVersionAsync(string endpoint)
    {
        try
        {
            using var client = await CdpClient.ConnectAsync(endpoint);
            var result = await client.SendCommandAsync("Browser.getVersion");
            Console.WriteLine(FormatJson(result));
        }
        catch (Exception ex)
        {
            WriteError(ex.Message);
        }
    }
    
    // ===== Runtime Domain =====
    
    private static async Task RuntimeEvaluateAsync(string endpoint, string expression)
    {
        try
        {
            using var client = await CdpClient.ConnectAsync(endpoint);
            var result = await client.EvaluateAsync(expression);
            Console.WriteLine(result);
        }
        catch (Exception ex)
        {
            WriteError(ex.Message);
        }
    }
    
    // ===== DOM Domain =====
    
    private static async Task DomGetDocumentAsync(string endpoint)
    {
        try
        {
            using var client = await CdpClient.ConnectAsync(endpoint);
            var result = await client.SendCommandAsync("DOM.getDocument");
            Console.WriteLine(FormatJson(result));
        }
        catch (Exception ex)
        {
            WriteError(ex.Message);
        }
    }
    
    private static async Task DomQuerySelectorAsync(string endpoint, string selector)
    {
        try
        {
            using var client = await CdpClient.ConnectAsync(endpoint);
            var result = await client.EvaluateAsync($@"
                JSON.stringify((function() {{
                    const el = document.querySelector({JsonSerializer.Serialize(selector)});
                    if (!el) return null;
                    return {{
                        tagName: el.tagName.toLowerCase(),
                        id: el.id || null,
                        className: el.className || null,
                        textContent: el.textContent?.trim().substring(0, 100) || null
                    }};
                }})())
            ");
            Console.WriteLine(result);
        }
        catch (Exception ex)
        {
            WriteError(ex.Message);
        }
    }
    
    private static async Task DomQuerySelectorAllAsync(string endpoint, string selector)
    {
        try
        {
            using var client = await CdpClient.ConnectAsync(endpoint);
            var result = await client.EvaluateAsync($@"
                JSON.stringify((function() {{
                    const els = document.querySelectorAll({JsonSerializer.Serialize(selector)});
                    return Array.from(els).map((el, i) => ({{
                        index: i,
                        tagName: el.tagName.toLowerCase(),
                        id: el.id || null,
                        className: el.className || null,
                        textContent: el.textContent?.trim().substring(0, 50) || null
                    }}));
                }})(), null, 2)
            ");
            Console.WriteLine(result);
        }
        catch (Exception ex)
        {
            WriteError(ex.Message);
        }
    }
    
    private static async Task DomGetOuterHtmlAsync(string endpoint, string selector)
    {
        try
        {
            using var client = await CdpClient.ConnectAsync(endpoint);
            var result = await client.EvaluateAsync($@"document.querySelector({JsonSerializer.Serialize(selector)})?.outerHTML || null");
            Console.WriteLine(result);
        }
        catch (Exception ex)
        {
            WriteError(ex.Message);
        }
    }
    
    // ===== Page Domain =====
    
    private static async Task PageNavigateAsync(string endpoint, string url)
    {
        try
        {
            using var client = await CdpClient.ConnectAsync(endpoint);
            var result = await client.SendCommandAsync("Page.navigate", new { url });
            Console.WriteLine($"Navigated to: {url}");
        }
        catch (Exception ex)
        {
            WriteError(ex.Message);
        }
    }
    
    private static async Task PageReloadAsync(string endpoint)
    {
        try
        {
            using var client = await CdpClient.ConnectAsync(endpoint);
            await client.SendCommandAsync("Page.reload");
            Console.WriteLine("Page reloaded");
        }
        catch (Exception ex)
        {
            WriteError(ex.Message);
        }
    }
    
    private static async Task PageCaptureScreenshotAsync(string endpoint)
    {
        try
        {
            using var client = await CdpClient.ConnectAsync(endpoint);
            var result = await client.SendCommandAsync("Page.captureScreenshot");
            
            if (result.TryGetProperty("result", out var resultProp) && 
                resultProp.TryGetProperty("data", out var dataProp))
            {
                Console.WriteLine(dataProp.GetString());
            }
            else
            {
                Console.WriteLine(FormatJson(result));
            }
        }
        catch (Exception ex)
        {
            WriteError(ex.Message);
        }
    }
    
    // ===== Input Domain =====
    
    private static async Task InputDispatchClickAsync(string endpoint, string selector)
    {
        try
        {
            using var client = await CdpClient.ConnectAsync(endpoint);
            var result = await client.EvaluateAsync($@"
                (function() {{
                    const el = document.querySelector({JsonSerializer.Serialize(selector)});
                    if (!el) return 'Error: Element not found';
                    el.click();
                    return 'Clicked: ' + el.tagName.toLowerCase() + (el.id ? '#' + el.id : '');
                }})()
            ");
            Console.WriteLine(result);
        }
        catch (Exception ex)
        {
            WriteError(ex.Message);
        }
    }
    
    private static async Task InputInsertTextAsync(string endpoint, string text)
    {
        try
        {
            using var client = await CdpClient.ConnectAsync(endpoint);
            var result = await client.EvaluateAsync($@"
                (function() {{
                    const el = document.activeElement;
                    if (!el) return 'Error: No element focused';
                    if (el.tagName !== 'INPUT' && el.tagName !== 'TEXTAREA' && !el.isContentEditable) {{
                        return 'Error: Focused element is not editable';
                    }}
                    
                    const text = {JsonSerializer.Serialize(text)};
                    if (el.isContentEditable) {{
                        el.textContent += text;
                    }} else {{
                        el.value += text;
                    }}
                    el.dispatchEvent(new Event('input', {{ bubbles: true }}));
                    return 'Inserted: ' + text.length + ' characters';
                }})()
            ");
            Console.WriteLine(result);
        }
        catch (Exception ex)
        {
            WriteError(ex.Message);
        }
    }
    
    private static async Task InputFillAsync(string endpoint, string selector, string text)
    {
        try
        {
            using var client = await CdpClient.ConnectAsync(endpoint);
            var result = await client.EvaluateAsync($@"
                (function() {{
                    const el = document.querySelector({JsonSerializer.Serialize(selector)});
                    if (!el) return 'Error: Element not found';
                    
                    const text = {JsonSerializer.Serialize(text)};
                    if (el.isContentEditable) {{
                        el.textContent = text;
                    }} else {{
                        el.value = text;
                        el.focus();
                    }}
                    el.dispatchEvent(new Event('input', {{ bubbles: true }}));
                    el.dispatchEvent(new Event('change', {{ bubbles: true }}));
                    return 'Filled: ' + el.tagName.toLowerCase() + (el.id ? '#' + el.id : '') + ' with ' + text.length + ' chars';
                }})()
            ");
            Console.WriteLine(result);
        }
        catch (Exception ex)
        {
            WriteError(ex.Message);
        }
    }
    
    // ===== Convenience Commands =====
    
    private static async Task StatusAsync(string endpoint)
    {
        try
        {
            using var client = await CdpClient.ConnectAsync(endpoint);
            var result = await client.SendCommandAsync("Browser.getVersion");
            
            if (result.TryGetProperty("result", out var versionResult))
            {
                var product = versionResult.TryGetProperty("product", out var p) ? p.GetString() : "unknown";
                Console.WriteLine($"Connected: {product}");
            }
            else
            {
                Console.WriteLine("Connected");
            }
        }
        catch (Exception ex)
        {
            WriteError($"Not connected: {ex.Message}");
        }
    }
    
    private static async Task SnapshotAsync(string endpoint)
    {
        try
        {
            using var client = await CdpClient.ConnectAsync(endpoint);
            
            var result = await client.EvaluateAsync(@"
                (function() {
                    function walk(node, depth) {
                        if (depth > 8) return '';
                        let result = '';
                        const indent = '  '.repeat(depth);
                        
                        if (node.nodeType === 1) {
                            const tag = node.tagName.toLowerCase();
                            const text = node.childNodes.length === 1 && node.childNodes[0].nodeType === 3 
                                ? node.textContent?.trim().substring(0, 80) : null;
                            const isClickable = node.onclick || tag === 'button' || tag === 'a' || 
                                               node.getAttribute('role') === 'button' ||
                                               (tag === 'input' && node.type === 'submit');
                            const isInput = tag === 'input' || tag === 'textarea' || tag === 'select';
                            
                            result += indent + '<' + tag;
                            if (node.id) result += ' id=""' + node.id + '""';
                            if (node.className && typeof node.className === 'string') result += ' class=""' + node.className.split(' ').slice(0,2).join(' ') + '""';
                            if (isClickable) result += ' [clickable]';
                            if (isInput) result += ' [input]';
                            if (tag === 'a' && node.href) result += ' href=""' + node.getAttribute('href') + '""';
                            if (tag === 'input') result += ' type=""' + (node.type || 'text') + '""';
                            result += '>';
                            if (text) result += ' ' + text;
                            result += '\n';
                            
                            for (const child of node.children) {
                                result += walk(child, depth + 1);
                            }
                        }
                        return result;
                    }
                    
                    return 'Title: ' + document.title + '\nURL: ' + location.href + '\n\n' + walk(document.body, 0);
                })()
            ");
            
            Console.WriteLine(result);
        }
        catch (Exception ex)
        {
            WriteError(ex.Message);
        }
    }
    
    private static void WriteError(string message)
    {
        Console.Error.WriteLine($"Error: {message}");
        Environment.Exit(1);
    }
    
    private static string FormatJson(JsonElement element)
    {
        return JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = true });
    }

    // ===== Update Skill Command =====

    private const string SkillRepo = "Redth/MauiDevFlow";
    private const string SkillBasePath = ".claude/skills/maui-ai-debugging";

    private static async Task UpdateSkillAsync(bool force, string? outputDir, string branch)
    {
        var root = outputDir ?? Directory.GetCurrentDirectory();
        var destBase = Path.Combine(root, SkillBasePath);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MauiDevFlow-CLI", "1.0"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        // Discover files via GitHub Trees API (recursive)
        Console.WriteLine("Fetching skill file list from GitHub...");
        List<string> files;
        try
        {
            files = await GetSkillFilesFromGitHubAsync(http, branch);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to fetch file list: {ex.Message}");
            return;
        }

        if (files.Count == 0)
        {
            Console.Error.WriteLine("No skill files found in the repository.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("maui-devflow update-skill");
        Console.WriteLine($"  Source: https://github.com/{SkillRepo}/tree/{branch}/{SkillBasePath}");
        Console.WriteLine($"  Destination: {destBase}");
        Console.WriteLine();
        Console.WriteLine("Files to download:");
        foreach (var file in files)
        {
            var destPath = Path.Combine(destBase, file);
            var exists = File.Exists(destPath);
            Console.WriteLine($"  {SkillBasePath}/{file}{(exists ? " (overwrite)" : " (new)")}");
        }
        Console.WriteLine();

        if (!force)
        {
            Console.Write("Existing files will be overwritten. Continue? [y/N] ");
            var response = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (response is not ("y" or "yes"))
            {
                Console.WriteLine("Cancelled.");
                return;
            }
        }

        var success = 0;
        foreach (var file in files)
        {
            var url = $"https://raw.githubusercontent.com/{SkillRepo}/{branch}/{SkillBasePath}/{file}";
            var destPath = Path.Combine(destBase, file);

            try
            {
                var content = await http.GetStringAsync(url);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                await File.WriteAllTextAsync(destPath, content);
                Console.WriteLine($"  ✓ {file}");
                success++;
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"  ✗ {file}: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(success == files.Count
            ? $"Done. {success} files updated."
            : $"Done. {success}/{files.Count} files updated.");
    }

    private static async Task<List<string>> GetSkillFilesFromGitHubAsync(HttpClient http, string branch)
    {
        var files = new List<string>();
        await ListGitHubDirectoryAsync(http, SkillBasePath, "", files, branch);
        return files;
    }

    private static async Task ListGitHubDirectoryAsync(HttpClient http, string basePath, string relativePath, List<string> files, string branch)
    {
        var apiPath = string.IsNullOrEmpty(relativePath) ? basePath : $"{basePath}/{relativePath}";
        var url = $"https://api.github.com/repos/{SkillRepo}/contents/{apiPath}?ref={branch}";
        var json = await http.GetStringAsync(url);
        var items = JsonSerializer.Deserialize<JsonElement>(json);

        foreach (var item in items.EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()!;
            var type = item.GetProperty("type").GetString()!;
            var itemRelative = string.IsNullOrEmpty(relativePath) ? name : $"{relativePath}/{name}";

            if (type == "file")
                files.Add(itemRelative);
            else if (type == "dir")
                await ListGitHubDirectoryAsync(http, basePath, itemRelative, files, branch);
        }
    }

    // ===== MAUI Agent Commands =====

    private static async Task MauiStatusAsync(string host, int port)
    {
        try
        {
            using var client = new MauiDevFlow.Driver.AgentClient(host, port);
            var status = await client.GetStatusAsync();
            if (status == null)
            {
                WriteError($"Cannot connect to agent at {host}:{port}");
                return;
            }
            Console.WriteLine($"Agent: {status.Agent} v{status.Version}");
            Console.WriteLine($"Platform: {status.Platform}");
            Console.WriteLine($"Device: {status.DeviceType} ({status.Idiom})");
            Console.WriteLine($"App: {status.AppName}");
        }
        catch (Exception ex) { WriteError(ex.Message); }
    }

    private static async Task MauiTreeAsync(string host, int port, int depth)
    {
        try
        {
            using var client = new MauiDevFlow.Driver.AgentClient(host, port);
            var tree = await client.GetTreeAsync(depth);
            PrintTree(tree, 0);
        }
        catch (Exception ex) { WriteError(ex.Message); }
    }

    private static async Task MauiQueryAsync(string host, int port, string? type, string? autoId, string? text)
    {
        try
        {
            using var client = new MauiDevFlow.Driver.AgentClient(host, port);
            var results = await client.QueryAsync(type, autoId, text);
            if (results.Count == 0)
            {
                Console.WriteLine("No elements found");
                return;
            }
            Console.WriteLine($"Found {results.Count} element(s):");
            foreach (var el in results)
            {
                Console.WriteLine($"  [{el.Id}] {el.Type}" +
                    (el.AutomationId != null ? $" automationId=\"{el.AutomationId}\"" : "") +
                    (el.Text != null ? $" text=\"{el.Text}\"" : "") +
                    (el.IsVisible ? "" : " [hidden]") +
                    (el.IsEnabled ? "" : " [disabled]"));
            }
        }
        catch (Exception ex) { WriteError(ex.Message); }
    }

    private static async Task MauiTapAsync(string host, int port, string elementId)
    {
        try
        {
            using var client = new MauiDevFlow.Driver.AgentClient(host, port);
            var success = await client.TapAsync(elementId);
            Console.WriteLine(success ? $"Tapped: {elementId}" : $"Failed to tap: {elementId}");
        }
        catch (Exception ex) { WriteError(ex.Message); }
    }

    private static async Task MauiFillAsync(string host, int port, string elementId, string text)
    {
        try
        {
            using var client = new MauiDevFlow.Driver.AgentClient(host, port);
            var success = await client.FillAsync(elementId, text);
            Console.WriteLine(success ? $"Filled: {elementId}" : $"Failed to fill: {elementId}");
        }
        catch (Exception ex) { WriteError(ex.Message); }
    }

    private static async Task MauiClearAsync(string host, int port, string elementId)
    {
        try
        {
            using var client = new MauiDevFlow.Driver.AgentClient(host, port);
            var success = await client.ClearAsync(elementId);
            Console.WriteLine(success ? $"Cleared: {elementId}" : $"Failed to clear: {elementId}");
        }
        catch (Exception ex) { WriteError(ex.Message); }
    }

    private static async Task MauiScreenshotAsync(string host, int port, string? output)
    {
        try
        {
            using var client = new MauiDevFlow.Driver.AgentClient(host, port);
            var data = await client.ScreenshotAsync();
            if (data == null)
            {
                WriteError("Failed to capture screenshot");
                return;
            }
            var filename = output ?? $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            await File.WriteAllBytesAsync(filename, data);
            Console.WriteLine($"Screenshot saved: {Path.GetFullPath(filename)} ({data.Length} bytes)");
        }
        catch (Exception ex) { WriteError(ex.Message); }
    }

    private static async Task MauiPropertyAsync(string host, int port, string elementId, string propertyName)
    {
        try
        {
            using var client = new MauiDevFlow.Driver.AgentClient(host, port);
            var value = await client.GetPropertyAsync(elementId, propertyName);
            Console.WriteLine(value != null ? $"{propertyName}: {value}" : $"Property '{propertyName}' not found");
        }
        catch (Exception ex) { WriteError(ex.Message); }
    }

    private static async Task MauiElementAsync(string host, int port, string elementId)
    {
        try
        {
            using var client = new MauiDevFlow.Driver.AgentClient(host, port);
            var el = await client.GetElementAsync(elementId);
            if (el == null)
            {
                WriteError($"Element '{elementId}' not found");
                return;
            }
            Console.WriteLine(JsonSerializer.Serialize(el, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { WriteError(ex.Message); }
    }

    private static async Task MauiNavigateAsync(string host, int port, string route)
    {
        try
        {
            using var client = new MauiDevFlow.Driver.AgentClient(host, port);
            var success = await client.NavigateAsync(route);
            Console.WriteLine(success ? $"Navigated to: {route}" : $"Failed to navigate to: {route}");
        }
        catch (Exception ex) { WriteError(ex.Message); }
    }

    private static void PrintTree(List<MauiDevFlow.Driver.ElementInfo> elements, int indent)
    {
        foreach (var el in elements)
        {
            var prefix = new string(' ', indent * 2);
            var info = $"{prefix}[{el.Id}] {el.Type}";
            if (el.AutomationId != null) info += $" automationId=\"{el.AutomationId}\"";
            if (el.Text != null) info += $" text=\"{el.Text}\"";
            if (!el.IsVisible) info += " [hidden]";
            if (!el.IsEnabled) info += " [disabled]";
            if (el.Bounds != null) info += $" ({el.Bounds.X:F0},{el.Bounds.Y:F0} {el.Bounds.Width:F0}x{el.Bounds.Height:F0})";
            Console.WriteLine(info);
            if (el.Children != null)
                PrintTree(el.Children, indent + 1);
        }
    }
}

/// <summary>
/// CDP WebSocket client for communicating with the debug bridge.
/// </summary>
class CdpClient : IDisposable
{
    private readonly ClientWebSocket _ws;
    private readonly string _endpoint;
    private int _messageId = 1;
    private string? _sessionId;
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    
    private CdpClient(string endpoint)
    {
        _endpoint = endpoint;
        _ws = new ClientWebSocket();
    }
    
    public static async Task<CdpClient> ConnectAsync(string endpoint)
    {
        var client = new CdpClient(endpoint);
        await client.ConnectInternalAsync();
        return client;
    }
    
    private async Task ConnectInternalAsync()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _ws.ConnectAsync(new Uri(_endpoint), cts.Token);
        
        // Start message pump
        _ = Task.Run(MessagePumpAsync);
        
        // Initialize session
        await SendCommandAsync("Browser.getVersion");
        await SendCommandAsync("Target.setAutoAttach", new { 
            autoAttach = true, 
            waitForDebuggerOnStart = false, 
            flatten = true 
        });
        
        // Wait for attachedToTarget event (with timeout)
        var waitStart = DateTime.UtcNow;
        while (_sessionId == null && DateTime.UtcNow - waitStart < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(50);
        }
    }
    
    private async Task MessagePumpAsync()
    {
        var buffer = new byte[1024 * 1024]; // 1MB buffer
        
        try
        {
            while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(buffer, _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                
                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                
                try
                {
                    var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    
                    // Handle response
                    if (root.TryGetProperty("id", out var idProp))
                    {
                        var id = idProp.GetInt32();
                        if (_pending.TryGetValue(id, out var tcs))
                        {
                            _pending.Remove(id);
                            tcs.SetResult(root.Clone());
                        }
                    }
                    
                    // Handle events
                    if (root.TryGetProperty("method", out var methodProp))
                    {
                        var method = methodProp.GetString();
                        if (method == "Target.attachedToTarget" && root.TryGetProperty("params", out var parms))
                        {
                            if (parms.TryGetProperty("sessionId", out var sid))
                            {
                                _sessionId = sid.GetString();
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore parse errors
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch
        {
            // Connection closed
        }
    }
    
    public async Task<string> EvaluateAsync(string expression)
    {
        var result = await SendCommandAsync("Runtime.evaluate", new { 
            expression,
            returnByValue = true
        }, _sessionId);
        
        if (result.TryGetProperty("result", out var evalResult))
        {
            if (evalResult.TryGetProperty("result", out var resultProp))
            {
                if (resultProp.TryGetProperty("value", out var value))
                {
                    if (value.ValueKind == JsonValueKind.String)
                        return value.GetString() ?? "null";
                    if (value.ValueKind == JsonValueKind.Object || value.ValueKind == JsonValueKind.Array)
                        return JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
                    return value.ToString();
                }
            }
            
            if (evalResult.TryGetProperty("exceptionDetails", out var exception))
            {
                var text = exception.TryGetProperty("text", out var t) ? t.GetString() : "Unknown error";
                return $"Error: {text}";
            }
        }
        
        return "undefined";
    }
    
    public async Task<JsonElement> SendCommandAsync(string method, object? parameters = null, string? sessionId = null)
    {
        var id = _messageId++;
        var tcs = new TaskCompletionSource<JsonElement>();
        _pending[id] = tcs;
        
        var message = new Dictionary<string, object>
        {
            ["id"] = id,
            ["method"] = method
        };
        
        if (parameters != null)
            message["params"] = parameters;
        if (sessionId != null)
            message["sessionId"] = sessionId;
        
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        
        // Wait with timeout
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
        var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
        
        if (completedTask == timeoutTask)
        {
            _pending.Remove(id);
            throw new TimeoutException($"CDP command {method} timed out");
        }
        
        return await tcs.Task;
    }
    
    public void Dispose()
    {
        _cts.Cancel();
        _ws.Dispose();
    }
}
