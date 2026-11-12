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
    public double ContentHeight { get; private set; } = 44;
    public NotificationBanner() => InitializeComponent();
    public void Present(NotchNotification notification)
    {
        SourceLabel.Text = notification.AppName;
        TitleLabel.Text = string.IsNullOrWhiteSpace(notification.Title) ? "Notification" : notification.Title;
        BodyLabel.Text = notification.Body;
        BodyLabel.Visibility = string.IsNullOrWhiteSpace(notification.Body) ? Visibility.Collapsed : Visibility.Visible;
        SourceLabel.ToolTip = notification.AppName;
        TitleLabel.ToolTip = notification.Title;
        BodyLabel.ToolTip = notification.Body;
        AppIcon.Source = notification.Icon;
        FallbackIcon.Visibility = notification.Icon is null ? Visibility.Visible : Visibility.Collapsed;
        OpenButton.IsEnabled = notification.Activate is not null;
        OpenButton.Cursor = notification.Activate is null ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.Hand;
        AutomationProperties.SetName(this, $"{notification.AppName}. {notification.Title}. {notification.Body}");
        AutomationProperties.SetName(OpenButton, $"Open notification from {notification.AppName}");
    }
    internal void MeasureContent(double width)
    {
        // Measure the row itself so housing margins and the previous height
        // cannot add empty space to short messages or constrain longer ones.
        MessageContent.Width = Math.Max(1, width - 44);
        // Text changes invalidate the leaf immediately; refresh its parents
        // before WPF can return a cached height from the previous message.
        TextContent.InvalidateMeasure();
        MessageContent.InvalidateMeasure();
        MessageContent.Measure(new Size(MessageContent.Width, double.PositiveInfinity));
        ContentHeight = Math.Ceiling(Math.Max(44, MessageContent.DesiredSize.Height));
        Height = ContentHeight;
    }
    private void OnDismiss(object sender, RoutedEventArgs args) { args.Handled = true; DismissRequested?.Invoke(this, EventArgs.Empty); }
    private void OnOpen(object sender, RoutedEventArgs args) { args.Handled = true; OpenRequested?.Invoke(this, EventArgs.Empty); }
}
