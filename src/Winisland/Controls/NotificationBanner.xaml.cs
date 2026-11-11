using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Winisland.Models;

namespace Winisland.Controls;

public partial class NotificationBanner : UserControl
{
    public event EventHandler? DismissRequested;
    public event EventHandler? OpenRequested;
    public NotificationBanner() => InitializeComponent();
    public void Present(NotchNotification notification)
    {
        SourceLabel.Text = notification.AppName;
        TitleLabel.Text = notification.Title;
        BodyLabel.Text = notification.Body;
        BodyLabel.Visibility = string.IsNullOrWhiteSpace(notification.Body) ? Visibility.Collapsed : Visibility.Visible;
        AppIcon.Source = notification.Icon;
        FallbackIcon.Visibility = notification.Icon is null ? Visibility.Visible : Visibility.Collapsed;
        OpenButton.IsEnabled = notification.Activate is not null;
        AutomationProperties.SetName(this, $"{notification.AppName}. {notification.Title}. {notification.Body}");
        AutomationProperties.SetName(OpenButton, $"Open notification from {notification.AppName}");
    }
    private void OnDismiss(object sender, RoutedEventArgs args) { args.Handled = true; DismissRequested?.Invoke(this, EventArgs.Empty); }
    private void OnOpen(object sender, RoutedEventArgs args) { args.Handled = true; OpenRequested?.Invoke(this, EventArgs.Empty); }
}
