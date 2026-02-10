#if WINDOWS_BUILD
using System.Text;
using Interop.UIAutomationClient;
#endif

namespace MauiDevFlow.Driver.Windows;

/// <summary>
/// Windows UI Automation helpers using the Interop.UIAutomationClient TLB-generated types.
/// Provides dialog detection, button invocation, and accessibility tree dumping
/// analogous to the Mac AXElement wrapper.
/// </summary>
internal static class UIAutomationInterop
{
#if WINDOWS_BUILD
    private const int UIA_InvokePatternId = 10000;
    private const int UIA_WindowControlTypeId = 50032;
    private const int UIA_ButtonControlTypeId = 50000;
    private const int UIA_TextControlTypeId = 50020;

    private static CUIAutomationClass? _automation;

    private static CUIAutomationClass GetAutomation()
    {
        _automation ??= new CUIAutomationClass();
        return _automation;
    }

    // ──────────────────────────────────────────────
    // Property helpers
    // ──────────────────────────────────────────────

    public static string? GetName(IUIAutomationElement element)
    {
        try { return element.CurrentName; } catch { return null; }
    }

    public static int GetControlType(IUIAutomationElement element)
    {
        try { return element.CurrentControlType; } catch { return 0; }
    }

    public static string? GetLocalizedControlType(IUIAutomationElement element)
    {
        try { return element.CurrentLocalizedControlType; } catch { return null; }
    }

    public static string? GetAutomationId(IUIAutomationElement element)
    {
        try { return element.CurrentAutomationId; } catch { return null; }
    }

    public static bool InvokeElement(IUIAutomationElement element)
    {
        try
        {
            var pattern = (IUIAutomationInvokePattern)element.GetCurrentPattern(UIA_InvokePatternId);
            pattern.Invoke();
            return true;
        }
        catch { return false; }
    }

    // ──────────────────────────────────────────────
    // Search helpers
    // ──────────────────────────────────────────────

    public static List<IUIAutomationElement> FindWindowsByProcessId(int processId)
    {
        var uia = GetAutomation();
        var root = uia.GetRootElement();
        var condition = uia.CreatePropertyCondition(30002, processId); // UIA_ProcessIdPropertyId
        var results = new List<IUIAutomationElement>();

        try
        {
            var array = root.FindAll(TreeScope.TreeScope_Children, condition);
            if (array != null)
                for (int i = 0; i < array.Length; i++)
                    results.Add(array.GetElement(i));
        }
        catch { }

        return results;
    }

    public static List<(IUIAutomationElement element, string name)> FindButtons(IUIAutomationElement root)
    {
        var uia = GetAutomation();
        var condition = uia.CreatePropertyCondition(30003, UIA_ButtonControlTypeId); // UIA_ControlTypePropertyId
        var results = new List<(IUIAutomationElement, string)>();

        try
        {
            var array = root.FindAll(TreeScope.TreeScope_Descendants, condition);
            if (array != null)
                for (int i = 0; i < array.Length; i++)
                {
                    var el = array.GetElement(i);
                    var name = GetName(el) ?? "";
                    if (name.Length > 0)
                        results.Add((el, name));
                }
        }
        catch { }

        return results;
    }

    public static List<string> FindTexts(IUIAutomationElement root)
    {
        var uia = GetAutomation();
        var condition = uia.CreatePropertyCondition(30003, UIA_TextControlTypeId);
        var texts = new List<string>();

        try
        {
            var array = root.FindAll(TreeScope.TreeScope_Descendants, condition);
            if (array != null)
                for (int i = 0; i < array.Length; i++)
                {
                    var name = GetName(array.GetElement(i)) ?? "";
                    if (name.Length > 0)
                        texts.Add(name);
                }
        }
        catch { }

        return texts;
    }

    public static List<IUIAutomationElement> FindChildWindows(IUIAutomationElement parent)
    {
        var uia = GetAutomation();
        var condition = uia.CreatePropertyCondition(30003, UIA_WindowControlTypeId);
        var results = new List<IUIAutomationElement>();

        try
        {
            var array = parent.FindAll(TreeScope.TreeScope_Descendants, condition);
            if (array != null)
                for (int i = 0; i < array.Length; i++)
                    results.Add(array.GetElement(i));
        }
        catch { }

        return results;
    }

    // ──────────────────────────────────────────────
    // Tree dump
    // ──────────────────────────────────────────────

    public static string DumpTree(IUIAutomationElement element, int maxDepth = 8)
    {
        var sb = new StringBuilder();
        var walker = GetAutomation().RawViewWalker;
        DumpTreeRecursive(walker, element, sb, 0, maxDepth);
        return sb.ToString();
    }

    private static void DumpTreeRecursive(IUIAutomationTreeWalker walker, IUIAutomationElement element, StringBuilder sb, int depth, int maxDepth)
    {
        if (depth >= maxDepth) return;

        try
        {
            var indent = new string(' ', depth * 2);
            var controlType = GetLocalizedControlType(element) ?? "?";
            var name = GetName(element) ?? "";
            var automationId = GetAutomationId(element) ?? "";

            sb.Append(indent).Append(controlType);
            if (name.Length > 0) sb.Append($" \"{name}\"");
            if (automationId.Length > 0) sb.Append($" [{automationId}]");
            sb.AppendLine();

            var child = walker.GetFirstChildElement(element);
            while (child != null)
            {
                DumpTreeRecursive(walker, child, sb, depth + 1, maxDepth);
                child = walker.GetNextSiblingElement(child);
            }
        }
        catch { }
    }
#endif
}
