using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Winisland.Controls;

public partial class DynamicIslandMenu : UserControl
{
    private bool _refreshing;
    public event EventHandler<string>? ActivityRequested;
    public event EventHandler? BackRequested;
    public event EventHandler? SetupRequested;
    public event EventHandler? PinRequested;
    public event EventHandler? MuteRequested;
    public event EventHandler<double>? VolumeRequested;

    public DynamicIslandMenu() => InitializeComponent();

    public bool IsAdjustingVolume => IslandVolumeSlider.IsAdjusting;
    public void CancelInteraction() => IslandVolumeSlider.CancelAdjustment();
    public void FocusActivity(string activity) =>
        (Activities.Children.OfType<Button>().FirstOrDefault(button => Equals(button.Tag, activity)) ?? MusicButton).Focus();

    public void SetActivity(string activity, bool updatesAvailable, bool pinned)
    {
        foreach (var button in Activities.Children.OfType<Button>())
        {
            var selected = Equals(button.Tag, activity);
            button.Background = selected ? (Brush)FindResource("ControlFill") : Brushes.Transparent;
            AutomationProperties.SetItemStatus(button, selected ? "Current activity" : "");
        }
        UpdatesButton.IsEnabled = updatesAvailable;
        PinButton.ToolTip = pinned ? "Unpin island" : "Keep island open";
        PinGlyph.Foreground = pinned ? Brushes.White : (Brush)FindResource("Muted");
        AutomationProperties.SetName(PinButton, pinned ? "Unpin island" : "Keep island open");
        AutomationProperties.SetItemStatus(PinButton, pinned ? "Pinned" : "Not pinned");
    }

    public void SetVolume(bool available, double volume, bool muted, double step, string hint)
    {
        _refreshing = true;
        try
        {
            if (!IslandVolumeSlider.IsAdjusting || !available) IslandVolumeSlider.Value = volume;
            IslandVolumeSlider.IsEnabled = MuteButton.IsEnabled = available;
            IslandVolumeSlider.WheelChange = step;
            IslandVolumeSlider.SmallChange = Math.Max(1, step);
            IslandVolumeSlider.ToolTip = hint;
            MuteButton.ToolTip = available ? muted ? "Unmute player" : "Mute player" : hint;
            AutomationProperties.SetName(MuteButton, muted ? "Unmute player" : "Mute player");
            VolumeLabel.Text = available ? $"{volume:0}%" : "—";
            VolumeGlyph.Kind = muted ? NotchIconKind.MutedSpeaker : NotchIconKind.Speaker;
            VolumeGlyph.SoundLevel = volume;
        }
        finally { _refreshing = false; }
    }

    private void OnActivityClick(object sender, RoutedEventArgs args) => ActivityRequested?.Invoke(this, (string)((Button)sender).Tag);
    private void OnBackClick(object sender, RoutedEventArgs args) => BackRequested?.Invoke(this, EventArgs.Empty);
    private void OnSetupClick(object sender, RoutedEventArgs args) => SetupRequested?.Invoke(this, EventArgs.Empty);
    private void OnPinClick(object sender, RoutedEventArgs args) => PinRequested?.Invoke(this, EventArgs.Empty);
    private void OnMuteClick(object sender, RoutedEventArgs args) => MuteRequested?.Invoke(this, EventArgs.Empty);
    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> args)
    {
        if (!_refreshing && ReferenceEquals(args.OriginalSource, IslandVolumeSlider)) VolumeRequested?.Invoke(this, args.NewValue);
    }

    private void OnActivityKeyDown(object sender, KeyEventArgs args)
    {
        var buttons = Activities.Children.OfType<Button>().ToArray();
        var index = Array.FindIndex(buttons, button => button.IsKeyboardFocused);
        if (index < 0) return;
        var delta = args.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -4, Key.Down => 4, _ => 0 };
        if (delta == 0) return;
        for (var count = 0; count < buttons.Length; count++)
        {
            index = (index + delta + buttons.Length) % buttons.Length;
            if (buttons[index].IsEnabled) { buttons[index].Focus(); break; }
        }
        args.Handled = true;
    }
}
