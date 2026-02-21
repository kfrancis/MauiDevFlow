using System.Diagnostics;
using System.Runtime.InteropServices;
#if WINDOWS_BUILD
using Interop.UIAutomationClient;
using MauiDevFlow.Driver.Windows;
#endif

namespace MauiDevFlow.Driver;

/// <summary>
/// Driver for Windows MAUI apps (WinUI3).
/// Direct localhost connection, no special setup needed.
/// Uses Windows UI Automation (UIA) via COM interop to detect and dismiss native dialogs.
///
/// Detection strategy:
///   1. Find all top-level windows for the target process.
///   2. Walk each window's subtree looking for dialog-like patterns:
///      buttons + text elements clustered together.
///   3. WinUI3 MAUI dialogs (DisplayAlert) render as modal overlays inside the main window,
///      so we scan all descendants, not just separate dialog windows.
///
/// Button label matching:
///   - Uses UIA Name property on Button control type elements.
///   - Case-insensitive comparison.
/// </summary>
public class WindowsAppDriver : AppDriverBase
{
    public override string Platform => "Windows";

    public int? ProcessId { get; set; }
    public string? AppName { get; set; }

    // ──────────────────────────────────────────────
    // Key simulation via SendInput P/Invoke
    // ──────────────────────────────────────────────

#if WINDOWS_BUILD
    public override Task BackAsync() => PressKeyAsync("ESCAPE");

    public override Task PressKeyAsync(string key)
    {
        EnsureWindows();
        var vk = MapKeyToVirtualKey(key);
        SendKeyPress(vk);
        return Task.CompletedTask;
    }
#else
    public override Task BackAsync() => throw new PlatformNotSupportedException("Windows operations require Windows.");
    public override Task PressKeyAsync(string key) => throw new PlatformNotSupportedException("Windows operations require Windows.");
#endif

    // ──────────────────────────────────────────────
    // Screen Recording via ffmpeg
    // ──────────────────────────────────────────────

    public override async Task StartRecordingAsync(string outputFile, int timeoutSeconds = 30)
    {
        EnsureNotRecording();
        EnsureFfmpeg();

        var fullPath = Path.GetFullPath(outputFile);
        if (!fullPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            fullPath = Path.ChangeExtension(fullPath, ".mp4");

        var input = "desktop";
        if (!string.IsNullOrEmpty(AppName))
            input = $"title={AppName}";

        var psi = new ProcessStartInfo("ffmpeg",
            $"-f gdigrab -framerate 30 -t {timeoutSeconds} -i {input} -y \"{fullPath}\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg");

        await Task.Delay(500);

        RecordingStateManager.Save(new RecordingState
        {
            RecordingPid = process.Id,
            OutputFile = fullPath,
            Platform = "windows",
            StartedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = timeoutSeconds
        });
    }

    public override async Task<string> StopRecordingAsync()
    {
        var state = RecordingStateManager.Load()
            ?? throw new InvalidOperationException("No active recording found.");

        if (state.Platform != "windows")
            throw new InvalidOperationException($"Active recording is on {state.Platform}, not Windows.");

        // Send 'q' to ffmpeg's stdin for graceful stop
        try
        {
            var proc = Process.GetProcessById(state.RecordingPid);
            if (!proc.HasExited)
            {
                proc.StandardInput.Write("q");
                proc.StandardInput.Flush();
                await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        catch
        {
            SendInterrupt(state.RecordingPid);
        }

        RecordingStateManager.Delete();
        return state.OutputFile;
    }

    private static void EnsureFfmpeg()
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
            if (proc?.ExitCode != 0)
                throw new Exception();
        }
        catch
        {
            throw new InvalidOperationException(
                "ffmpeg is required for screen recording on Windows but was not found on PATH. " +
                "Install it from https://ffmpeg.org/download.html or via 'winget install ffmpeg'.");
        }
    }

    // ──────────────────────────────────────────────
    // Dialog detection & dismissal via UIA
    // ──────────────────────────────────────────────

#if WINDOWS_BUILD
    public Task<AlertInfo?> DetectAlertAsync()
    {
        EnsureWindows();
        var pid = ResolveProcessId();
        return Task.FromResult(DetectDialog(pid));
    }

    public Task DismissAlertAsync(string? buttonLabel = null)
    {
        EnsureWindows();
        var pid = ResolveProcessId();
        var buttons = FindDialogButtonsCore(pid);
        if (buttons.Count == 0)
            throw new InvalidOperationException("No alert detected to dismiss.");

        var target = PickButton(buttons, buttonLabel);
        if (!UIAutomationInterop.InvokeElement(target.element))
            throw new InvalidOperationException("UIA Invoke action failed.");

        return Task.CompletedTask;
    }

    public Task<AlertInfo?> HandleAlertIfPresentAsync(string? buttonLabel = null)
    {
        EnsureWindows();
        var pid = ResolveProcessId();
        var buttons = FindDialogButtonsCore(pid);
        if (buttons.Count == 0)
            return Task.FromResult<AlertInfo?>(null);

        var alertButtons = buttons.Select(b => new AlertButton(b.name, 0, 0, 0, 0)).ToList();
        var texts = FindDialogTextsCore(pid);
        var info = new AlertInfo(texts.FirstOrDefault(), alertButtons);

        var target = PickButton(buttons, buttonLabel);
        UIAutomationInterop.InvokeElement(target.element);

        return Task.FromResult<AlertInfo?>(info);
    }

    public Task<string> GetAccessibilityTreeAsync()
    {
        EnsureWindows();
        var pid = ResolveProcessId();
        var windows = UIAutomationInterop.FindWindowsByProcessId(pid);
        var result = string.Empty;
        foreach (var window in windows)
            result += UIAutomationInterop.DumpTree(window);
        return Task.FromResult(result);
    }

    // ──────────────────────────────────────────────
    // Core detection logic
    // ──────────────────────────────────────────────

    private static AlertInfo? DetectDialog(int pid)
    {
        var buttons = FindDialogButtonsCore(pid);
        if (buttons.Count == 0)
            return null;

        var alertButtons = buttons.Select(b => new AlertButton(b.name, 0, 0, 0, 0)).ToList();
        var texts = FindDialogTextsCore(pid);
        return new AlertInfo(texts.FirstOrDefault(), alertButtons);
    }

    private static List<(IUIAutomationElement element, string name)> FindDialogButtonsCore(int pid)
    {
        var windows = UIAutomationInterop.FindWindowsByProcessId(pid);

        foreach (var window in windows)
        {
            var childWindows = UIAutomationInterop.FindChildWindows(window);
            foreach (var childWin in childWindows)
            {
                var buttons = UIAutomationInterop.FindButtons(childWin);
                if (buttons.Count > 0)
                {
                    var texts = UIAutomationInterop.FindTexts(childWin);
                    if (texts.Count > 0)
                        return buttons;
                }
            }
        }

        return new();
    }

    private static List<string> FindDialogTextsCore(int pid)
    {
        var windows = UIAutomationInterop.FindWindowsByProcessId(pid);

        foreach (var window in windows)
        {
            var childWindows = UIAutomationInterop.FindChildWindows(window);
            foreach (var childWin in childWindows)
            {
                var buttons = UIAutomationInterop.FindButtons(childWin);
                if (buttons.Count > 0)
                    return UIAutomationInterop.FindTexts(childWin);
            }
        }

        return new();
    }

    // ──────────────────────────────────────────────
    // Button matching
    // ──────────────────────────────────────────────

    private static (IUIAutomationElement element, string name) PickButton(
        List<(IUIAutomationElement element, string name)> buttons, string? buttonLabel)
    {
        if (buttons.Count == 0)
            throw new InvalidOperationException("No buttons found in dialog.");

        if (buttonLabel is not null)
        {
            var match = buttons.FirstOrDefault(b =>
                b.name.Equals(buttonLabel, StringComparison.OrdinalIgnoreCase));
            if (match.element is null)
            {
                var available = string.Join(", ", buttons.Select(b => b.name));
                throw new InvalidOperationException($"Button '{buttonLabel}' not found. Available: {available}");
            }
            return match;
        }

        return buttons[0];
    }
#else
    public Task<AlertInfo?> DetectAlertAsync() => throw new PlatformNotSupportedException("Windows operations require Windows.");
    public Task DismissAlertAsync(string? buttonLabel = null) => throw new PlatformNotSupportedException("Windows operations require Windows.");
    public Task<AlertInfo?> HandleAlertIfPresentAsync(string? buttonLabel = null) => throw new PlatformNotSupportedException("Windows operations require Windows.");
    public Task<string> GetAccessibilityTreeAsync() => throw new PlatformNotSupportedException("Windows operations require Windows.");
#endif

    // ──────────────────────────────────────────────
    // Process resolution
    // ──────────────────────────────────────────────

    private int ResolveProcessId()
    {
        if (ProcessId.HasValue)
            return ProcessId.Value;

        if (!string.IsNullOrEmpty(AppName))
        {
            var processes = Process.GetProcessesByName(AppName);
            if (processes.Length > 0)
            {
                ProcessId = processes[0].Id;
                return ProcessId.Value;
            }

            var all = Process.GetProcesses();
            var match = all.FirstOrDefault(p =>
            {
                try { return p.ProcessName.Contains(AppName, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            });
            if (match != null)
            {
                ProcessId = match.Id;
                return ProcessId.Value;
            }
        }

        throw new InvalidOperationException("ProcessId or AppName must be set for Windows operations.");
    }

    // ──────────────────────────────────────────────
    // Key simulation
    // ──────────────────────────────────────────────

#if WINDOWS_BUILD
    private static ushort MapKeyToVirtualKey(string key) => key.ToUpperInvariant() switch
    {
        "ENTER" or "RETURN" => 0x0D,
        "BACK" or "ESCAPE" or "ESC" => 0x1B,
        "TAB" => 0x09,
        "DELETE" or "BACKSPACE" => 0x08,
        "HOME" => 0x24,
        "END" => 0x23,
        "LEFT" => 0x25,
        "UP" => 0x26,
        "RIGHT" => 0x27,
        "DOWN" => 0x28,
        "SPACE" => 0x20,
        _ => (ushort)(key.Length == 1 ? char.ToUpper(key[0]) : 0)
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private static void SendKeyPress(ushort vk)
    {
        if (vk == 0) return;
        var inputs = new INPUT[]
        {
            new() { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk } } },
            new() { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } } }
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows dialog handling requires Windows.");
    }
#else
    private static void EnsureWindows() => throw new PlatformNotSupportedException("Windows dialog handling requires Windows.");
#endif
}
