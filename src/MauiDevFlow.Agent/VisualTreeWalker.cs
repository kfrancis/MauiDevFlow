using Microsoft.Maui.Controls;
using MauiDevFlow.Agent.Core;

namespace MauiDevFlow.Agent;

/// <summary>
/// Platform-specific visual tree walker that provides native view info
/// for Android, iOS, Mac Catalyst, and Windows.
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
}
