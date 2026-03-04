using Microsoft.Maui.Controls;
using MauiDevFlow.Agent.Core;
#if IOS || MACCATALYST
using UIKit;
#endif
#if MACOS
using AppKit;
#endif

namespace MauiDevFlow.Agent;

/// <summary>
/// Platform-specific visual tree walker that provides native view info
/// for Android, iOS, Mac Catalyst, Windows, and macOS AppKit.
/// </summary>
public class PlatformVisualTreeWalker : VisualTreeWalker
{
    protected override void PopulateNativeInfo(ElementInfo info, VisualElement ve)
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
#elif MACOS
            if (platformView is NSView nsView)
            {
                var props = new Dictionary<string, string?>();
                if (!string.IsNullOrEmpty(nsView.AccessibilityIdentifier))
                    props["accessibilityIdentifier"] = nsView.AccessibilityIdentifier;
                if (!string.IsNullOrEmpty(nsView.AccessibilityLabel))
                    props["accessibilityLabel"] = nsView.AccessibilityLabel;
                if (nsView is NSControl nsControl)
                {
                    props["isNSControl"] = "true";
                    props["isEnabled"] = nsControl.Enabled.ToString();
                }
                if (nsView is NSButton nsButton)
                    props["buttonTitle"] = nsButton.Title;
                if (nsView is NSTextField nsTextField)
                {
                    props["stringValue"] = nsTextField.StringValue;
                    props["isEditable"] = nsTextField.Editable.ToString();
                }
                props["isHidden"] = nsView.Hidden.ToString();
                props["alphaValue"] = nsView.AlphaValue.ToString("F2");
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

    protected override BoundsInfo? ResolveSyntheticBounds(object marker)
    {
        try
        {
#if IOS || MACCATALYST
            return ResolveBoundsApple(marker);
#elif ANDROID
            return ResolveBoundsAndroid(marker);
#elif WINDOWS
            return ResolveBoundsWindows(marker);
#else
            return null;
#endif
        }
        catch { return null; }
    }

    protected override void PopulateSyntheticNativeInfo(ElementInfo info, object marker)
    {
        try
        {
#if IOS || MACCATALYST
            PopulateNativeInfoApple(info, marker);
#elif ANDROID
            PopulateNativeInfoAndroid(info, marker);
#elif WINDOWS
            PopulateNativeInfoWindows(info, marker);
#endif
        }
        catch { }
    }

#if IOS || MACCATALYST
    private BoundsInfo? ResolveBoundsApple(object marker)
    {
        Shell? shell = marker switch
        {
            FlyoutButtonMarker m => m.Shell,
            ShellFlyoutItemMarker m => m.Shell,
            ShellTabMarker m => m.Shell,
            NavBarTitleMarker => Shell.Current,
            SearchHandlerMarker => Shell.Current,
            _ => null
        };

        if (shell?.Handler?.PlatformView is not UIView shellView)
            return null;

        // Find UINavigationBar for nav bar elements
        if (marker is NavBarTitleMarker or FlyoutButtonMarker or SearchHandlerMarker)
        {
            var navBar = FindSubview<UINavigationBar>(shellView);
            if (navBar != null)
            {
                var frame = navBar.ConvertRectToView(navBar.Bounds, shellView);
                if (marker is FlyoutButtonMarker)
                {
                    // Flyout button is in the left area of the nav bar
                    return new BoundsInfo
                    {
                        X = frame.X,
                        Y = frame.Y,
                        Width = 44,
                        Height = frame.Height
                    };
                }
                return new BoundsInfo
                {
                    X = frame.X,
                    Y = frame.Y,
                    Width = frame.Width,
                    Height = frame.Height
                };
            }
        }

        // Find UITabBar for tab elements
        if (marker is ShellTabMarker)
        {
            var tabBar = FindSubview<UITabBar>(shellView);
            if (tabBar != null)
            {
                var frame = tabBar.ConvertRectToView(tabBar.Bounds, shellView);
                return new BoundsInfo
                {
                    X = frame.X,
                    Y = frame.Y,
                    Width = frame.Width,
                    Height = frame.Height
                };
            }
        }

        return null;
    }

    private static T? FindSubview<T>(UIView root) where T : UIView
    {
        if (root is T match) return match;
        foreach (var sub in root.Subviews)
        {
            var found = FindSubview<T>(sub);
            if (found != null) return found;
        }
        return null;
    }

    private void PopulateNativeInfoApple(ElementInfo info, object marker)
    {
        Shell? shell = marker switch
        {
            FlyoutButtonMarker m => m.Shell,
            ShellFlyoutItemMarker m => m.Shell,
            ShellTabMarker m => m.Shell,
            NavBarTitleMarker => Shell.Current,
            _ => null
        };

        if (shell?.Handler?.PlatformView is UIView shellView)
        {
            if (marker is NavBarTitleMarker or FlyoutButtonMarker)
            {
                var navBar = FindSubview<UINavigationBar>(shellView);
                if (navBar != null) info.NativeType = navBar.GetType().FullName;
            }
            else if (marker is ShellTabMarker)
            {
                var tabBar = FindSubview<UITabBar>(shellView);
                if (tabBar != null) info.NativeType = tabBar.GetType().FullName;
            }
        }
    }
#endif

#if ANDROID
    private BoundsInfo? ResolveBoundsAndroid(object marker)
    {
        Shell? shell = marker switch
        {
            FlyoutButtonMarker m => m.Shell,
            ShellFlyoutItemMarker m => m.Shell,
            ShellTabMarker m => m.Shell,
            NavBarTitleMarker => Shell.Current,
            _ => null
        };

        if (shell?.Handler?.PlatformView is not Android.Views.View shellView)
            return null;

        var density = shellView.Context?.Resources?.DisplayMetrics?.Density ?? 1f;

        if (marker is NavBarTitleMarker or FlyoutButtonMarker)
        {
            var toolbar = FindAndroidView<AndroidX.AppCompat.Widget.Toolbar>(shellView);
            if (toolbar != null)
            {
                var location = new int[2];
                toolbar.GetLocationOnScreen(location);
                var shellLocation = new int[2];
                shellView.GetLocationOnScreen(shellLocation);

                return new BoundsInfo
                {
                    X = (location[0] - shellLocation[0]) / density,
                    Y = (location[1] - shellLocation[1]) / density,
                    Width = toolbar.Width / density,
                    Height = toolbar.Height / density
                };
            }
        }

        if (marker is ShellTabMarker)
        {
            var bottomNav = FindAndroidView<Google.Android.Material.BottomNavigation.BottomNavigationView>(shellView);
            if (bottomNav != null)
            {
                var location = new int[2];
                bottomNav.GetLocationOnScreen(location);
                var shellLocation = new int[2];
                shellView.GetLocationOnScreen(shellLocation);

                return new BoundsInfo
                {
                    X = (location[0] - shellLocation[0]) / density,
                    Y = (location[1] - shellLocation[1]) / density,
                    Width = bottomNav.Width / density,
                    Height = bottomNav.Height / density
                };
            }
        }

        return null;
    }

    private static T? FindAndroidView<T>(Android.Views.View root) where T : Android.Views.View
    {
        if (root is T match) return match;
        if (root is Android.Views.ViewGroup vg)
        {
            for (int i = 0; i < vg.ChildCount; i++)
            {
                var child = vg.GetChildAt(i);
                if (child != null)
                {
                    var found = FindAndroidView<T>(child);
                    if (found != null) return found;
                }
            }
        }
        return null;
    }

    private void PopulateNativeInfoAndroid(ElementInfo info, object marker)
    {
        Shell? shell = marker switch
        {
            FlyoutButtonMarker m => m.Shell,
            ShellFlyoutItemMarker m => m.Shell,
            ShellTabMarker m => m.Shell,
            NavBarTitleMarker => Shell.Current,
            _ => null
        };

        if (shell?.Handler?.PlatformView is Android.Views.View shellView)
        {
            if (marker is NavBarTitleMarker or FlyoutButtonMarker)
            {
                var toolbar = FindAndroidView<AndroidX.AppCompat.Widget.Toolbar>(shellView);
                if (toolbar != null) info.NativeType = toolbar.GetType().FullName ?? toolbar.Class?.Name;
            }
            else if (marker is ShellTabMarker)
            {
                var bottomNav = FindAndroidView<Google.Android.Material.BottomNavigation.BottomNavigationView>(shellView);
                if (bottomNav != null) info.NativeType = bottomNav.GetType().FullName ?? bottomNav.Class?.Name;
            }
        }
    }
#endif

#if WINDOWS
    private BoundsInfo? ResolveBoundsWindows(object marker)
    {
        // Windows NavigationView doesn't expose easily queryable sub-parts
        // for nav bar / tab regions. Return null for now — can be enhanced later.
        return null;
    }

    private void PopulateNativeInfoWindows(ElementInfo info, object marker)
    {
        Shell? shell = marker switch
        {
            FlyoutButtonMarker m => m.Shell,
            ShellFlyoutItemMarker m => m.Shell,
            ShellTabMarker m => m.Shell,
            NavBarTitleMarker => Shell.Current,
            _ => null
        };

        if (shell?.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement fe)
        {
            info.NativeType = fe.GetType().FullName;
        }
    }
#endif
}
