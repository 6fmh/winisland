using System;
using System.Windows.Media;

namespace Winisland.Models;

/// <summary>A transient notification. Notification content is never persisted.</summary>
public sealed record NotchNotification
{
    public NotchNotification() { }
    public NotchNotification(string id, string appName, string title, string body, DateTimeOffset timestamp,
        ImageSource? icon = null, Action? activate = null)
    {
        Id = id; AppName = appName; Title = title; Body = body; Timestamp = timestamp; Icon = icon; Activate = activate;
    }
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string AppName { get; init; } = "Notification";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public ImageSource? Icon { get; init; }
    public Action? Activate { get; init; }
}
