using Microsoft.Maui;
using Microsoft.Maui.Controls;
using System.Collections.Concurrent;

namespace MauiDevFlow.Agent;

/// <summary>
/// Walks the MAUI visual tree and produces ElementInfo representations.
/// Uses IVisualTreeElement.GetVisualChildren() for tree traversal.
/// Maintains a session-scoped element ID dictionary for stable references.
/// Also maps non-visual elements (ToolbarItems, MenuItems) for interaction.
/// </summary>
public class VisualTreeWalker
{
    private readonly ConcurrentDictionary<string, WeakReference<IVisualTreeElement>> _elementMap = new();
    private readonly ConcurrentDictionary<IVisualTreeElement, string> _reverseMap = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<string, WeakReference<object>> _objectMap = new();

    /// <summary>
    /// Looks up a previously-mapped element by its ID.
    /// Returns IVisualTreeElement, ToolbarItem, or other mapped objects.
    /// </summary>
    public object? GetElementById(string id)
    {
        if (_elementMap.TryGetValue(id, out var weakRef) && weakRef.TryGetTarget(out var element))
            return element;

        if (_objectMap.TryGetValue(id, out var objRef) && objRef.TryGetTarget(out var obj))
            return obj;

        _elementMap.TryRemove(id, out _);
        _objectMap.TryRemove(id, out _);
        return null;
    }

    /// <summary>
    /// Looks up an element by ID, re-walking the tree if not found.
    /// </summary>
    public object? GetElementById(string id, Application? app)
    {
        var el = GetElementById(id);
        if (el != null || app == null) return el;

        // Re-walk tree to refresh the element map
        WalkTree(app);
        return GetElementById(id);
    }

    /// <summary>
    /// Returns diagnostic info about the element map state.
    /// </summary>
    public string GetDiagnostics()
    {
        return $"Map has {_elementMap.Count + _objectMap.Count} entries. Keys: [{string.Join(", ", _elementMap.Keys.Concat(_objectMap.Keys).Take(20))}]";
    }

    /// <summary>
    /// Walks the visual tree starting from the application's windows.
    /// </summary>
    public List<ElementInfo> WalkTree(Application app, int maxDepth = 0)
    {
        var results = new List<ElementInfo>();
        if (app is not IVisualTreeElement appElement)
            return results;

        foreach (var child in appElement.GetVisualChildren())
        {
            var info = WalkElement(child, null, 1, maxDepth);
            if (info != null)
                results.Add(info);
        }

        return results;
    }

    /// <summary>
    /// Walks from a specific element.
    /// </summary>
    public ElementInfo? WalkElement(IVisualTreeElement element, string? parentId, int currentDepth, int maxDepth)
    {
        var id = GetOrCreateId(element);
        var info = CreateElementInfo(element, id, parentId);

        if (maxDepth > 0 && currentDepth >= maxDepth)
            return info;

        info.Children ??= new List<ElementInfo>();

        var children = element.GetVisualChildren();
        foreach (var child in children)
        {
            var childInfo = WalkElement(child, id, currentDepth + 1, maxDepth);
            if (childInfo != null)
                info.Children.Add(childInfo);
        }

        // Add ToolbarItems as synthetic children of Pages
        if (element is Page page && page.ToolbarItems.Count > 0)
        {
            foreach (var toolbarItem in page.ToolbarItems)
            {
                var tiInfo = CreateToolbarItemInfo(toolbarItem, id);
                info.Children.Add(tiInfo);
            }
        }

        if (info.Children.Count == 0)
            info.Children = null;

        return info;
    }

    /// <summary>
    /// Queries elements matching the given criteria.
    /// </summary>
    public List<ElementInfo> Query(Application app, string? type = null, string? automationId = null, string? text = null)
    {
        var results = new List<ElementInfo>();
        if (app is not IVisualTreeElement appElement)
            return results;

        QueryRecursive(appElement, type, automationId, text, null, results);
        return results;
    }

    private void QueryRecursive(IVisualTreeElement element, string? type, string? automationId, string? text, string? parentId, List<ElementInfo> results)
    {
        var id = GetOrCreateId(element);
        var info = CreateElementInfo(element, id, parentId);

        MatchAndAdd(info, type, automationId, text, results);

        foreach (var child in element.GetVisualChildren())
            QueryRecursive(child, type, automationId, text, id, results);

        // Also query ToolbarItems on Pages
        if (element is Page page)
        {
            foreach (var toolbarItem in page.ToolbarItems)
            {
                var tiInfo = CreateToolbarItemInfo(toolbarItem, id);
                MatchAndAdd(tiInfo, type, automationId, text, results);
            }
        }
    }

    private static void MatchAndAdd(ElementInfo info, string? type, string? automationId, string? text, List<ElementInfo> results)
    {
        bool matches = true;

        if (type != null && !info.Type.Equals(type, StringComparison.OrdinalIgnoreCase)
            && !info.FullType.Equals(type, StringComparison.OrdinalIgnoreCase))
            matches = false;

        if (automationId != null && !string.Equals(info.AutomationId, automationId, StringComparison.OrdinalIgnoreCase))
            matches = false;

        if (text != null && (info.Text == null || !info.Text.Contains(text, StringComparison.OrdinalIgnoreCase)))
            matches = false;

        if (matches && (type != null || automationId != null || text != null))
            results.Add(info);
    }

    private string GetOrCreateId(IVisualTreeElement element)
    {
        if (_reverseMap.TryGetValue(element, out var existingId))
            return existingId;

        // Prefer AutomationId if available
        string id;
        if (element is VisualElement ve && !string.IsNullOrEmpty(ve.AutomationId))
        {
            id = ve.AutomationId;
            // Ensure uniqueness by appending suffix if needed
            if (_elementMap.ContainsKey(id) || _objectMap.ContainsKey(id))
            {
                var suffix = 1;
                while (_elementMap.ContainsKey($"{id}_{suffix}") || _objectMap.ContainsKey($"{id}_{suffix}"))
                    suffix++;
                id = $"{id}_{suffix}";
            }
        }
        else
        {
            id = Guid.NewGuid().ToString("N")[..12];
        }

        _elementMap[id] = new WeakReference<IVisualTreeElement>(element);
        _reverseMap[element] = id;
        return id;
    }

    private string GetOrCreateObjectId(object element, string? automationId)
    {
        // Check if we already have an ID for this object
        foreach (var kvp in _objectMap)
        {
            if (kvp.Value.TryGetTarget(out var existing) && ReferenceEquals(existing, element))
                return kvp.Key;
        }

        string id;
        if (!string.IsNullOrEmpty(automationId))
        {
            id = automationId;
            if (_elementMap.ContainsKey(id) || _objectMap.ContainsKey(id))
            {
                var suffix = 1;
                while (_elementMap.ContainsKey($"{id}_{suffix}") || _objectMap.ContainsKey($"{id}_{suffix}"))
                    suffix++;
                id = $"{id}_{suffix}";
            }
        }
        else
        {
            id = Guid.NewGuid().ToString("N")[..12];
        }

        _objectMap[id] = new WeakReference<object>(element);
        return id;
    }

    private ElementInfo CreateToolbarItemInfo(ToolbarItem item, string parentId)
    {
        var id = GetOrCreateObjectId(item, item.AutomationId);
        return new ElementInfo
        {
            Id = id,
            ParentId = parentId,
            Type = "ToolbarItem",
            FullType = item.GetType().FullName ?? "Microsoft.Maui.Controls.ToolbarItem",
            AutomationId = item.AutomationId,
            Text = item.Text,
            IsVisible = true,
            IsEnabled = item.IsEnabled,
        };
    }

    private static ElementInfo CreateElementInfo(IVisualTreeElement element, string id, string? parentId)
    {
        var info = new ElementInfo
        {
            Id = id,
            ParentId = parentId,
            Type = element.GetType().Name,
            FullType = element.GetType().FullName ?? element.GetType().Name,
        };

        if (element is VisualElement ve)
        {
            info.AutomationId = ve.AutomationId;
            info.IsVisible = ve.IsVisible;
            info.IsEnabled = ve.IsEnabled;
            info.IsFocused = ve.IsFocused;
            info.Opacity = ve.Opacity;
            info.Bounds = new BoundsInfo
            {
                X = ve.Frame.X,
                Y = ve.Frame.Y,
                Width = ve.Frame.Width,
                Height = ve.Frame.Height
            };

            // Populate native view info from handler
            PopulateNativeInfo(info, ve);
        }

        // Extract text from common controls (including Shell elements)
        info.Text = element switch
        {
            Label l => l.Text,
            Button b => b.Text,
            Entry e => e.Text,
            Editor ed => ed.Text,
            SearchBar sb => sb.Text,
            Span s => s.Text,
            BaseShellItem si => si.Title,
            _ => null
        };

        // Populate gesture recognizer metadata
        if (element is View view && view.GestureRecognizers.Count > 0)
        {
            var gestures = new List<string>();
            foreach (var gr in view.GestureRecognizers)
            {
                var name = gr switch
                {
                    TapGestureRecognizer => "tap",
                    SwipeGestureRecognizer => "swipe",
                    PanGestureRecognizer => "pan",
                    PinchGestureRecognizer => "pinch",
                    PointerGestureRecognizer => "pointer",
                    DragGestureRecognizer => "drag",
                    DropGestureRecognizer => "drop",
                    _ => gr.GetType().Name.Replace("GestureRecognizer", "").ToLowerInvariant()
                };
                if (!gestures.Contains(name))
                    gestures.Add(name);
            }
            if (gestures.Count > 0)
                info.Gestures = gestures;
        }

        return info;
    }

    private static void PopulateNativeInfo(ElementInfo info, VisualElement ve)
    {
        try
        {
            var platformView = ve.Handler?.PlatformView;
            if (platformView == null) return;

            info.NativeType = platformView.GetType().FullName;

#if IOS || MACCATALYST
            if (platformView is UIKit.UIView uiView)
            {
                var props = new Dictionary<string, string?>();
                if (!string.IsNullOrEmpty(uiView.AccessibilityIdentifier))
                    props["accessibilityIdentifier"] = uiView.AccessibilityIdentifier;
                if (!string.IsNullOrEmpty(uiView.AccessibilityLabel))
                    props["accessibilityLabel"] = uiView.AccessibilityLabel;
                if (uiView is UIKit.UIControl uiControl)
                    props["isUIControl"] = "true";
                if (uiView is UIKit.UITextField textField)
                    props["isSecureTextEntry"] = textField.SecureTextEntry.ToString();
                if (props.Count > 0)
                    info.NativeProperties = props;
            }
#elif ANDROID
            if (platformView is Android.Views.View androidView)
            {
                var props = new Dictionary<string, string?>();
                if (!string.IsNullOrEmpty(androidView.ContentDescription))
                    props["contentDescription"] = androidView.ContentDescription;
                if (androidView is Android.Widget.EditText editText)
                    props["inputType"] = editText.InputType.ToString();
                if (androidView.Clickable)
                    props["clickable"] = "true";
                if (props.Count > 0)
                    info.NativeProperties = props;
            }
#elif WINDOWS
            if (platformView is Microsoft.UI.Xaml.FrameworkElement frameworkElement)
            {
                var props = new Dictionary<string, string?>();
                var automationId = Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(frameworkElement);
                if (!string.IsNullOrEmpty(automationId))
                    props["automationId"] = automationId;
                var automationName = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(frameworkElement);
                if (!string.IsNullOrEmpty(automationName))
                    props["automationName"] = automationName;
                var helpText = Microsoft.UI.Xaml.Automation.AutomationProperties.GetHelpText(frameworkElement);
                if (!string.IsNullOrEmpty(helpText))
                    props["helpText"] = helpText;
                if (!string.IsNullOrEmpty(frameworkElement.Name))
                    props["name"] = frameworkElement.Name;
                if (frameworkElement.Visibility != Microsoft.UI.Xaml.Visibility.Visible)
                    props["visibility"] = "collapsed";
                if (!frameworkElement.IsHitTestVisible)
                    props["isHitTestVisible"] = "false";
                if (frameworkElement is Microsoft.UI.Xaml.Controls.Control control)
                {
                    if (!control.IsEnabled)
                        props["isEnabled"] = "false";
                    if (!control.IsTabStop)
                        props["isTabStop"] = "false";
                }
                if (frameworkElement is Microsoft.UI.Xaml.Controls.TextBox textBox)
                {
                    if (textBox.IsReadOnly)
                        props["isReadOnly"] = "true";
                }
                if (frameworkElement is Microsoft.UI.Xaml.Controls.PasswordBox)
                    props["isPassword"] = "true";
                if (props.Count > 0)
                    info.NativeProperties = props;
            }
#endif
        }
        catch
        {
            // Native info is best-effort; don't fail the tree walk
        }
    }

    /// <summary>
    /// Clears the element ID mappings.
    /// </summary>
    public void Reset()
    {
        _elementMap.Clear();
        _reverseMap.Clear();
        _objectMap.Clear();
    }
}
