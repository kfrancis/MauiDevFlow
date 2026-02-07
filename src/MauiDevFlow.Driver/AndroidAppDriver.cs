using System.Diagnostics;

namespace MauiDevFlow.Driver;

/// <summary>
/// Driver for Android MAUI apps via emulator/device.
/// Handles adb reverse port forwarding and adb shell commands.
/// </summary>
public class AndroidAppDriver : AppDriverBase
{
    public override string Platform => "Android";

    protected override async Task SetupPlatformAsync(string host, int port)
    {
        // Set up adb reverse port forwarding so the device/emulator can reach localhost
        // Both MAUI native and CDP use the same port
        await RunAdbAsync($"reverse tcp:{port} tcp:{port}");
    }

    public override async Task BackAsync()
    {
        await RunAdbAsync("shell input keyevent KEYCODE_BACK");
    }

    public override async Task PressKeyAsync(string key)
    {
        var keycode = key.ToUpperInvariant() switch
        {
            "ENTER" or "RETURN" => "KEYCODE_ENTER",
            "BACK" => "KEYCODE_BACK",
            "HOME" => "KEYCODE_HOME",
            "TAB" => "KEYCODE_TAB",
            "ESCAPE" or "ESC" => "KEYCODE_ESCAPE",
            "DELETE" or "BACKSPACE" => "KEYCODE_DEL",
            _ => $"KEYCODE_{key.ToUpperInvariant()}"
        };

        await RunAdbAsync($"shell input keyevent {keycode}");
    }

    private static async Task RunAdbAsync(string arguments)
    {
        var psi = new ProcessStartInfo("adb", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) throw new InvalidOperationException("Failed to start adb");
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"adb {arguments} failed: {error}");
        }
    }
}
