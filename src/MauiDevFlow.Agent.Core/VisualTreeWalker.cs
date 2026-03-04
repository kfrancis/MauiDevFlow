using Microsoft.Maui;
using Microsoft.Maui.Controls;
using MauiDevFlow.Agent.Core.Css;
using System.Collections.Concurrent;

namespace MauiDevFlow.Agent.Core;

/// <summary>
/// Walks the MAUI visual tree and produces ElementInfo representations.
/// Uses IVisualTreeElement.GetVisualChildren() for tree traversal.
/// Maintains a session-scoped element ID dictionary for stable references.
/// Also maps non-visual elements (ToolbarItems, MenuItems, navigation back button) for interaction.
/// </summary>
public class VisualTreeWalker
{
    private readonly ConcurrentDictionary<string, WeakReference<IVisualTreeElement>> _elementMap = new();
    private readonly ConcurrentDictionary<IVisualTreeElement, string> _reverseMap = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<string, WeakReference<object>> _objectMap = new();

    /// <summary>
    /// Marker object representing the navigation back button in Shell or NavigationPage.
    /// </summary>
    public class BackButtonMarker
    {
        public INavigation Navigation { get; init; } = null!;
        public string Title { get; init; } = "Back";
    }

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
    /// Reverse lookup: gets the ID for a previously-mapped visual tree element.
    /// </summary>
    public string? GetIdForElement(IVisualTreeElement element)
    {
        return _reverseMap.TryGetValue(element, out var id) ? id : null;
    }

    /// <summary>
    /// Walks the visual tree starting from the application's windows.
    /// When windowIndex is null, walks all windows. Otherwise walks only the specified window.
    /// </summary>
    public List<ElementInfo> WalkTree(Application app, int maxDepth = 0, int? windowIndex = null)
    {
        var results = new List<ElementInfo>();
        if (app is not IVisualTreeElement appElement)
            return results;

        if (windowIndex != null)
        {
            if (windowIndex.Value < 0 || windowIndex.Value >= app.Windows.Count)
                return results;
            var window = app.Windows[windowIndex.Value];
            if (window is IVisualTreeElement windowElement)
            {
                var info = WalkElement(windowElement, null, 1, maxDepth);
                if (info != null)
                    results.Add(info);
            }
            return results;
        }

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

        // ShellContent-specific: ensure content page is included even if
        // GetVisualChildren() doesn't expose it (common on GTK/Linux after navigation).
        if (element is ShellContent sc && sc.Content is IVisualTreeElement scPage
            && !children.Contains(scPage))
        {
            var pageInfo = WalkElement(scPage, id, currentDepth + 1, maxDepth);
            if (pageInfo != null)
                info.Children.Add(pageInfo);
        }

        // Add ToolbarItems as synthetic children of Pages
        if (element is Page page)
        {
            // NavBarTitle — inject page title as synthetic element
            AddNavBarTitle(page, id, info);

            foreach (var toolbarItem in page.ToolbarItems)
            {
                var tiInfo = CreateToolbarItemInfo(toolbarItem, id);
                info.Children.Add(tiInfo);
            }

            // Add synthetic back button when there's a navigation stack
            var backInfo = CreateBackButtonInfo(page, id);
            if (backInfo != null)
                info.Children.Add(backInfo);

            // SearchHandler (Shell only)
            AddSearchHandler(page, id, info);
        }

        // Shell-level synthetics: flyout button, flyout items, tab bar
        if (element is Shell shell)
        {
            AddShellSynthetics(shell, id, info);
        }

        // NavigationPage-level synthetics
        if (element is NavigationPage navPage2)
        {
            AddNavigationPageSynthetics(navPage2, id, info);
        }

        // FlyoutPage-level synthetics
        if (element is FlyoutPage flyoutPage)
        {
            AddFlyoutPageSynthetics(flyoutPage, id, info);
        }

        // TabbedPage-level synthetics
        if (element is TabbedPage tabbedPage)
        {
            AddTabbedPageSynthetics(tabbedPage, id, info);
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

        var children = element.GetVisualChildren();
        foreach (var child in children)
            QueryRecursive(child, type, automationId, text, id, results);

        // ShellContent-specific: include content page if not already traversed
        if (element is ShellContent sc && sc.Content is IVisualTreeElement scPage
            && !children.Contains(scPage))
        {
            QueryRecursive(scPage, type, automationId, text, id, results);
        }

        // Also query ToolbarItems and back button on Pages
        if (element is Page page)
        {
            foreach (var toolbarItem in page.ToolbarItems)
            {
                var tiInfo = CreateToolbarItemInfo(toolbarItem, id);
                MatchAndAdd(tiInfo, type, automationId, text, results);
            }

            var backInfo = CreateBackButtonInfo(page, id);
            if (backInfo != null)
                MatchAndAdd(backInfo, type, automationId, text, results);
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

    // ── Synthetic element injection helpers ──

    private void AddNavBarTitle(Page page, string parentId, ElementInfo parentInfo)
    {
        // Skip if nav bar is hidden
        try
        {
            if (page.Parent is Shell || FindAncestor<Shell>(page) != null)
            {
                if (!Shell.GetNavBarIsVisible(page)) return;
            }
            else if (page.Parent is NavigationPage)
            {
                if (!NavigationPage.GetHasNavigationBar(page)) return;
            }
        }
        catch { }

        var title = page.Title;
        if (string.IsNullOrEmpty(title)) return;

        var marker = new NavBarTitleMarker { Title = title };
        var id = GetOrCreateObjectId(marker, $"NavBarTitle_{parentId}");
        parentInfo.Children ??= new List<ElementInfo>();
        parentInfo.Children.Add(new ElementInfo
        {
            Id = id,
            ParentId = parentId,
            Type = "NavBarTitle",
            FullType = "MauiDevFlow.Agent.Core.NavBarTitle",
            Text = title,
            IsVisible = true,
            IsEnabled = false, // not interactive
        });
    }

    private void AddSearchHandler(Page page, string parentId, ElementInfo parentInfo)
    {
        try
        {
            var handler = Shell.GetSearchHandler(page);
            if (handler == null) return;

            var marker = new SearchHandlerMarker { Handler = handler };
            var id = GetOrCreateObjectId(marker, $"SearchHandler_{parentId}");
            parentInfo.Children ??= new List<ElementInfo>();
            parentInfo.Children.Add(new ElementInfo
            {
                Id = id,
                ParentId = parentId,
                Type = "SearchHandler",
                FullType = "MauiDevFlow.Agent.Core.SearchHandler",
                Text = handler.Placeholder ?? "Search",
                IsVisible = handler.SearchBoxVisibility != SearchBoxVisibility.Hidden,
                IsEnabled = handler.IsSearchEnabled,
            });
        }
        catch { } // Shell.GetSearchHandler may throw if not in Shell context
    }

    private void AddShellSynthetics(Shell shell, string parentId, ElementInfo parentInfo)
    {
        parentInfo.Children ??= new List<ElementInfo>();

        // Flyout button
        if (shell.FlyoutBehavior != FlyoutBehavior.Disabled)
        {
            var flyoutMarker = new FlyoutButtonMarker { Shell = shell };
            var flyoutId = GetOrCreateObjectId(flyoutMarker, "FlyoutButton");
            parentInfo.Children.Insert(0, new ElementInfo
            {
                Id = flyoutId,
                ParentId = parentId,
                Type = "FlyoutButton",
                FullType = "MauiDevFlow.Agent.Core.FlyoutButton",
                AutomationId = "FlyoutButton",
                Text = "☰",
                IsVisible = shell.FlyoutBehavior == FlyoutBehavior.Flyout,
                IsEnabled = true,
            });
        }

        // Flyout items
        try
        {
            foreach (var item in shell.Items)
            {
                if (item is BaseShellItem bsi && bsi.FlyoutItemIsVisible)
                {
                    var isSelected = shell.CurrentItem == item;
                    var fiMarker = new ShellFlyoutItemMarker { Item = item, Shell = shell };
                    var fiId = GetOrCreateObjectId(fiMarker, $"FlyoutItem_{bsi.Route ?? bsi.Title}");
                    parentInfo.Children.Add(new ElementInfo
                    {
                        Id = fiId,
                        ParentId = parentId,
                        Type = "FlyoutItem",
                        FullType = "MauiDevFlow.Agent.Core.FlyoutItem",
                        AutomationId = bsi.AutomationId,
                        Text = bsi.Title,
                        IsVisible = bsi.IsVisible,
                        IsEnabled = bsi.IsEnabled,
                        IsFocused = isSelected,
                    });
                }
            }
        }
        catch { }

        // Tab bar items for current ShellItem
        try
        {
            var currentPage = shell.CurrentPage;
            if (currentPage != null && Shell.GetTabBarIsVisible(currentPage))
            {
                var currentItem = shell.CurrentItem;
                if (currentItem?.Items != null && currentItem.Items.Count > 1)
                {
                    foreach (var section in currentItem.Items)
                    {
                        var isSelected = currentItem.CurrentItem == section;
                        var tabMarker = new ShellTabMarker { Section = section, Shell = shell };
                        var tabId = GetOrCreateObjectId(tabMarker, $"Tab_{section.Route ?? section.Title}");
                        parentInfo.Children.Add(new ElementInfo
                        {
                            Id = tabId,
                            ParentId = parentId,
                            Type = "Tab",
                            FullType = "MauiDevFlow.Agent.Core.Tab",
                            AutomationId = section.AutomationId,
                            Text = section.Title,
                            IsVisible = section.IsVisible,
                            IsEnabled = section.IsEnabled,
                            IsFocused = isSelected,
                        });
                    }
                }
            }
        }
        catch { }

        // Flyout header/footer
        try
        {
            if (shell.FlyoutHeader is View headerView && headerView is IVisualTreeElement headerVte)
            {
                var headerInfo = WalkElement(headerVte, parentId, 1, 3);
                if (headerInfo != null)
                {
                    headerInfo.Type = "FlyoutHeader";
                    parentInfo.Children.Add(headerInfo);
                }
            }
            if (shell.FlyoutFooter is View footerView && footerView is IVisualTreeElement footerVte)
            {
                var footerInfo = WalkElement(footerVte, parentId, 1, 3);
                if (footerInfo != null)
                {
                    footerInfo.Type = "FlyoutFooter";
                    parentInfo.Children.Add(footerInfo);
                }
            }
        }
        catch { }
    }

    private void AddNavigationPageSynthetics(NavigationPage navPage, string parentId, ElementInfo parentInfo)
    {
        // NavigationPage title from current page (rendered in native nav bar)
        try
        {
            var currentPage = navPage.CurrentPage;
            if (currentPage != null && !string.IsNullOrEmpty(currentPage.Title)
                && NavigationPage.GetHasNavigationBar(currentPage))
            {
                var marker = new NavBarTitleMarker { Title = currentPage.Title };
                var id = GetOrCreateObjectId(marker, $"NavBarTitle_{parentId}");
                parentInfo.Children ??= new List<ElementInfo>();
                parentInfo.Children.Insert(0, new ElementInfo
                {
                    Id = id,
                    ParentId = parentId,
                    Type = "NavBarTitle",
                    FullType = "MauiDevFlow.Agent.Core.NavBarTitle",
                    Text = currentPage.Title,
                    IsVisible = true,
                    IsEnabled = false,
                });
            }
        }
        catch { }
    }

    private void AddFlyoutPageSynthetics(FlyoutPage flyoutPage, string parentId, ElementInfo parentInfo)
    {
        var marker = new FlyoutToggleMarker { FlyoutPage = flyoutPage };
        var id = GetOrCreateObjectId(marker, "FlyoutToggle");
        parentInfo.Children ??= new List<ElementInfo>();
        parentInfo.Children.Insert(0, new ElementInfo
        {
            Id = id,
            ParentId = parentId,
            Type = "FlyoutToggle",
            FullType = "MauiDevFlow.Agent.Core.FlyoutToggle",
            AutomationId = "FlyoutToggle",
            Text = "☰",
            IsVisible = true,
            IsEnabled = true,
        });
    }

    private void AddTabbedPageSynthetics(TabbedPage tabbedPage, string parentId, ElementInfo parentInfo)
    {
        parentInfo.Children ??= new List<ElementInfo>();
        try
        {
            foreach (var child in tabbedPage.Children)
            {
                if (child is Page tabPage)
                {
                    var isSelected = tabbedPage.CurrentPage == tabPage;
                    var marker = new TabbedPageTabMarker { Page = tabPage, TabbedPage = tabbedPage };
                    var tabId = GetOrCreateObjectId(marker, $"Tab_{tabPage.AutomationId ?? tabPage.Title}");
                    parentInfo.Children.Add(new ElementInfo
                    {
                        Id = tabId,
                        ParentId = parentId,
                        Type = "Tab",
                        FullType = "MauiDevFlow.Agent.Core.Tab",
                        AutomationId = tabPage.AutomationId,
                        Text = tabPage.Title,
                        IsVisible = true,
                        IsEnabled = true,
                        IsFocused = isSelected,
                    });
                }
            }
        }
        catch { }
    }

    // ── Marker types for synthetic elements ──

    public class NavBarTitleMarker { public string Title { get; init; } = ""; }
    public class SearchHandlerMarker { public SearchHandler Handler { get; init; } = null!; }
    public class FlyoutButtonMarker { public Shell Shell { get; init; } = null!; }
    public class ShellFlyoutItemMarker { public ShellItem Item { get; init; } = null!; public Shell Shell { get; init; } = null!; }
    public class ShellTabMarker { public ShellSection Section { get; init; } = null!; public Shell Shell { get; init; } = null!; }
    public class FlyoutToggleMarker { public FlyoutPage FlyoutPage { get; init; } = null!; }
    public class TabbedPageTabMarker { public Page Page { get; init; } = null!; public TabbedPage TabbedPage { get; init; } = null!; }

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

    private ElementInfo? CreateBackButtonInfo(Page page, string parentId)
    {
        // Check NavigationPage stack — only add to the top page
        INavigation? nav = null;
        string title = "Back";

        var navPage = FindAncestor<NavigationPage>(page);
        if (navPage != null && navPage.Navigation.NavigationStack.Count > 1
            && navPage.CurrentPage == page)
        {
            nav = navPage.Navigation;
            var prev = nav.NavigationStack[nav.NavigationStack.Count - 2];
            if (prev != null && !string.IsNullOrEmpty(prev.Title))
                title = prev.Title;
        }

        // Check Shell's ShellSection navigation stack — only for the current top page
        if (nav == null)
        {
            var shell = FindAncestor<Shell>(page);
            if (shell == null)
            {
                try { if (page.Window?.Page is Shell windowShell) shell = windowShell; }
                catch { }
            }
            if (shell?.CurrentItem?.CurrentItem is ShellSection section)
            {
                var navStack = section.Navigation?.NavigationStack;
                if (navStack != null && navStack.Count > 1 && navStack[^1] == page)
                {
                    nav = section.Navigation;
                    var prev = navStack[navStack.Count - 2];
                    if (prev != null && !string.IsNullOrEmpty(prev.Title))
                        title = prev.Title;
                }
            }
        }

        if (nav == null)
            return null;

        var marker = new BackButtonMarker { Navigation = nav, Title = title };
        var id = GetOrCreateObjectId(marker, "BackButton");
        return new ElementInfo
        {
            Id = id,
            ParentId = parentId,
            Type = "BackButton",
            FullType = "MauiDevFlow.Agent.Core.BackButton",
            AutomationId = "BackButton",
            Text = $"← {title}",
            IsVisible = true,
            IsEnabled = true,
        };
    }

    private static T? FindAncestor<T>(Element? element) where T : Element
    {
        while (element != null)
        {
            if (element is T match)
                return match;
            element = element.Parent;
        }
        return null;
    }

    private ElementInfo CreateElementInfo(IVisualTreeElement element, string id, string? parentId)
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
            info.Opacity = double.IsFinite(ve.Opacity) ? ve.Opacity : 1;
            info.Bounds = new BoundsInfo
            {
                X = double.IsFinite(ve.Frame.X) ? ve.Frame.X : 0,
                Y = double.IsFinite(ve.Frame.Y) ? ve.Frame.Y : 0,
                Width = double.IsFinite(ve.Frame.Width) ? ve.Frame.Width : 0,
                Height = double.IsFinite(ve.Frame.Height) ? ve.Frame.Height : 0
            };

            // Populate style classes
            if (ve is NavigableElement ne && ne.StyleClass is { Count: > 0 } sc)
                info.StyleClass = sc.ToList();

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

        // Extract value/state from stateful controls
        info.Value = element switch
        {
            Switch sw => sw.IsToggled.ToString(),
            CheckBox cb => cb.IsChecked.ToString(),
            Slider sl => sl.Value.ToString("F2"),
            Stepper st => st.Value.ToString("F2"),
            ProgressBar pb => pb.Progress.ToString("F2"),
            Picker pk => pk.SelectedItem?.ToString(),
            DatePicker dp => dp.Date.ToString(),
            TimePicker tp => tp.Time.ToString(),
            RadioButton rb => rb.IsChecked.ToString(),
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

    protected virtual void PopulateNativeInfo(ElementInfo info, VisualElement ve)
    {
        try
        {
            var platformView = ve.Handler?.PlatformView;
            if (platformView == null) return;

            info.NativeType = platformView.GetType().FullName;
        }
        catch
        {
            // Native info is best-effort; don't fail the tree walk
        }
    }

    /// <summary>
    /// Queries elements matching a CSS selector string.
    /// Walks the full tree, then runs the CSS selector engine against it.
    /// </summary>
    public List<ElementInfo> QueryCss(Application app, string selector)
    {
        var tree = WalkTree(app);
        return CssSelectorEngine.Query(tree, selector);
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
