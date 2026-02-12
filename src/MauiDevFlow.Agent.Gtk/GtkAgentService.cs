using Microsoft.Maui;
using Microsoft.Maui.Controls;
using MauiDevFlow.Agent.Core;

namespace MauiDevFlow.Agent.Gtk;

/// <summary>
/// GTK-specific agent service with native tap and screenshot support for Linux/GTK.
/// </summary>
public class GtkAgentService : DevFlowAgentService
{
    public GtkAgentService(AgentOptions? options = null) : base(options) { }

    protected override bool TryNativeTap(VisualElement ve)
    {
        try
        {
            var platformView = ve.Handler?.PlatformView;
            if (platformView == null) return false;

            if (platformView is global::Gtk.Button button)
            {
                button.Activate();
                return true;
            }

            if (platformView is global::Gtk.Widget widget)
            {
                widget.Activate();
                return true;
            }
        }
        catch { }
        return false;
    }

    protected override async Task<byte[]?> CaptureScreenshotAsync(VisualElement rootElement)
    {
        // Try the standard MAUI API first
        try
        {
            var result = await VisualDiagnostics.CaptureAsPngAsync(rootElement);
            if (result != null) return result;
        }
        catch { }

        // GTK4-specific fallback: capture via Gtk.WidgetPaintable
        try
        {
            var window = Application.Current?.Windows.FirstOrDefault();
            if (window?.Handler?.PlatformView is global::Gtk.Window gtkWindow)
            {
                return CaptureGtkWindow(gtkWindow);
            }
        }
        catch { }

        return null;
    }

    private static byte[]? CaptureGtkWindow(global::Gtk.Window window)
    {
        try
        {
            var paintable = global::Gtk.WidgetPaintable.New(window);
            var width = paintable.GetIntrinsicWidth();
            var height = paintable.GetIntrinsicHeight();

            if (width <= 0 || height <= 0) return null;

            var snapshot = global::Gtk.Snapshot.New();
            paintable.Snapshot(snapshot, width, height);
            var node = snapshot.ToNode();
            if (node == null) return null;

            var renderer = window.GetNative()?.GetRenderer();
            if (renderer == null) return null;

            var texture = renderer.RenderTexture(node, null);
            if (texture == null) return null;

            // Save to a temporary file and read back as bytes
            var tmpPath = System.IO.Path.GetTempFileName() + ".png";
            try
            {
                texture.SaveToPng(tmpPath);
                return System.IO.File.ReadAllBytes(tmpPath);
            }
            finally
            {
                try { System.IO.File.Delete(tmpPath); } catch { }
            }
        }
        catch
        {
            return null;
        }
    }

    protected override void TryNativeResize(IWindow window, int width, int height)
    {
        if (window.Handler?.PlatformView is global::Gtk.Window gtkWindow)
        {
            gtkWindow.SetDefaultSize(width, height);
        }
        else
        {
            base.TryNativeResize(window, width, height);
        }
    }
}
