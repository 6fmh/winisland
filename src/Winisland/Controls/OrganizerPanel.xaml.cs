using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Winisland.Services;

namespace Winisland.Controls;

public partial class OrganizerPanel : UserControl, IDisposable
{
    private sealed record DayView(Button Button, TextBlock Label, Ellipse Dot);
    private sealed record ClockView(string Id, TextBlock Time, TextBlock Date, ComboBox? Choice);
    private sealed record LapView(int Number, string SplitLabel, string TotalLabel);
    private readonly OrganizerStore _store;
    private readonly OrganizerStopwatch _stopwatch = new();
    private readonly DispatcherTimer _noteTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer _displayTimer = new(DispatcherPriority.Background);
    private readonly DispatcherTimer _reminderTimer = new(DispatcherPriority.Background);
    private readonly List<DayView> _days = new();
    private readonly List<ClockView> _clocks = new();
    private readonly DayOfWeek _firstWeekday = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
    private DateOnly _selectedDay = DateOnly.FromDateTime(DateTime.Today);
    private DateOnly _displayedMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private string _selectedTool = "notes";
    private Guid? _editingEventId, _editingReminderId;
    private int _editingAllDayDuration = 1440;
    private bool _initializing = true, _notesDirty, _disposed, _loaded, _clockDropdownOpen, _dialogOpen;
    private static readonly Brush Red = FrozenBrush(0xFF, 0x45, 0x3A);
    private static readonly Brush Muted = FrozenBrush(0x8A, 0x8A, 0x8E);
    private static readonly Brush Inactive = FrozenBrush(0x55, 0x55, 0x5A);
    private static readonly DependencyProperty TransitionEnabledProperty = DependencyProperty.Register(
        "TransitionEnabled", typeof(bool), typeof(OrganizerPanel),
        new FrameworkPropertyMetadata(true, (_, args) => { if (args.NewValue is false && _ is OrganizerPanel panel) panel.FinishTransitions(); }));

    public event EventHandler<string>? Notification;
    public event EventHandler? EditingChanged;
    public bool IsEditing => IsVisible && (CalendarView.IsVisible && EventEditor.IsVisible
        || RemindersView.IsVisible && _editingReminderId.HasValue || _clockDropdownOpen || _dialogOpen
        || IsKeyboardFocusWithin && Keyboard.FocusedElement is TextBox { IsVisible: true });
    internal OrganizerStore StoreForTest => _store;
    internal bool EventEditorVisibleForTest => EventEditor.Visibility == Visibility.Visible;
    internal bool DisplayTimerRunningForTest => _displayTimer.IsEnabled;
    internal int ClockCountForTest => _clocks.Count;

    public OrganizerPanel() : this(new OrganizerStore()) { }
    internal OrganizerPanel(OrganizerStore store)
    {
        _store = store;
        InitializeComponent();
        SetResourceReference(TransitionEnabledProperty, InteractionMotion.PreferenceKey);
        for (var i = 0; i < 7; i++)
        {
            var weekday = ((int)_firstWeekday + i) % 7;
            var name = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames[weekday];
            WeekdayLabels.Children.Add(new TextBlock { Text = name.Length > 2 ? name[..2] : name,
                FontSize = 10, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        }
        for (var i = 0; i < 42; i++)
        {
            var label = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var dot = new Ellipse { Width = 3, Height = 3, Fill = Red, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2), Visibility = Visibility.Hidden };
            var content = new Grid { Width = 30, Height = 30 }; content.Children.Add(label); content.Children.Add(dot);
            var button = new Button { Style = (Style)FindResource("DayButton"), Content = content };
            button.Click += OnDayClicked;
            MonthDays.Children.Add(button); _days.Add(new DayView(button, label, dot));
        }
        NotesBox.Text = store.Note;
        _noteTimer.Tick += (_, _) => FlushEdits();
        _displayTimer.Tick += (_, _) => { if (_selectedTool == "clocks") UpdateClockTimes(); else UpdateStopwatch(); };
        _reminderTimer.Tick += OnReminderDue;
        _store.Changed += OnStoreChanged;
        SystemEvents.TimeChanged += OnSystemTimeChanged;
        Unloaded += (_, _) => { _displayTimer.Stop(); FlushEdits(); };
        _initializing = false;
        RefreshCalendar(); RefreshReminders(); BuildClockCards(); UpdateNoteStatus(); UpdateStopwatch();
        SelectTool("notes", false);
    }

    public void ShowCalendar()
    {
        FlushEdits();
        var changed = CalendarView.Visibility != Visibility.Visible;
        CalendarView.Visibility = Visibility.Visible; ToolsView.Visibility = Visibility.Collapsed;
        if (changed && IsVisible) AnimateEntrance(CalendarView);
        RefreshCalendar(); SynchronizeDisplayTimer(); EditingChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ShowTools()
    {
        var changed = ToolsView.Visibility != Visibility.Visible;
        CalendarView.Visibility = Visibility.Collapsed; ToolsView.Visibility = Visibility.Visible;
        if (changed && IsVisible) AnimateEntrance(ToolsView);
        SelectTool(_selectedTool, false); EditingChanged?.Invoke(this, EventArgs.Empty);
    }

    public void FlushEdits()
    {
        _noteTimer.Stop();
        if (!_notesDirty || _initializing || _disposed) return;
        try { _store.SetNote(NotesBox.Text); _notesDirty = false; NotesSaveLabel.Text = "Saved locally"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { NotesSaveLabel.Text = "Could not save · try again"; Log.Error("Save organizer note", ex); }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        SynchronizeDisplayTimer(); ScheduleReminder();
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_initializing || _disposed) return;
        if (!IsVisible) FlushEdits();
        SynchronizeDisplayTimer(); EditingChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnEditingFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_initializing) return;
        Dispatcher.BeginInvoke(() => { if (!_disposed) EditingChanged?.Invoke(this, EventArgs.Empty); }, DispatcherPriority.Input);
    }

    private void RefreshCalendar()
    {
        MonthLabel.Text = _displayedMonth.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        MonthLabel.ToolTip = MonthLabel.Text;
        SelectedDateLabel.Text = _selectedDay.ToString("dddd, d MMM", CultureInfo.CurrentCulture);
        SelectedDateLabel.ToolTip = _selectedDay.ToString("D", CultureInfo.CurrentCulture);
        PreviousMonthButton.IsEnabled = OrganizerCalendar.TryShiftMonth(_displayedMonth, -1, out _);
        NextMonthButton.IsEnabled = OrganizerCalendar.TryShiftMonth(_displayedMonth, 1, out _);
        var dates = OrganizerCalendar.GetDays(_displayedMonth, _firstWeekday);
        var today = DateOnly.FromDateTime(DateTime.Today);
        for (var i = 0; i < _days.Count; i++)
        {
            var view = _days[i]; var date = dates[i];
            view.Button.Tag = date; view.Button.IsEnabled = date.HasValue;
            view.Label.Text = date?.Day.ToString(CultureInfo.CurrentCulture) ?? "";
            var selected = date == _selectedDay;
            view.Button.Background = selected ? Red : date == today ? FrozenBrush(0x37, 0x1D, 0x1D) : Brushes.Transparent;
            view.Label.Foreground = selected ? Brushes.White : date == today ? Red : date?.Month == _displayedMonth.Month ? Brushes.White : Inactive;
            view.Dot.Fill = selected ? Brushes.White : Red;
            view.Dot.Visibility = date.HasValue && _store.EventsOn(date.Value).Count > 0 ? Visibility.Visible : Visibility.Hidden;
            if (date.HasValue)
            {
                var name = date.Value.ToString("D", CultureInfo.CurrentCulture);
                view.Button.ToolTip = name;
                System.Windows.Automation.AutomationProperties.SetName(view.Button, name);
            }
        }
        RefreshAgenda();
    }

    private void RefreshAgenda()
    {
        var entries = _store.EventsOn(_selectedDay);
        AgendaItems.ItemsSource = entries;
        AgendaEmpty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AgendaScroll.Visibility = entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnDayClicked(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not DateOnly date) return;
        SetSelectedDay(date);
    }

    private void SetSelectedDay(DateOnly day)
    {
        _selectedDay = day;
        _displayedMonth = new DateOnly(day.Year, day.Month, 1);
        RefreshCalendar();
    }

    private void OnPreviousMonth(object sender, RoutedEventArgs e) => ShiftMonth(-1);
    private void OnNextMonth(object sender, RoutedEventArgs e) => ShiftMonth(1);
    private void ShiftMonth(int offset)
    {
        if (!OrganizerCalendar.TryShiftMonth(_displayedMonth, offset, out var month)) return;
        _displayedMonth = month; RefreshCalendar(); AnimateEntrance(MonthDays);
    }
    private void OnToday(object sender, RoutedEventArgs e) { SetSelectedDay(DateOnly.FromDateTime(DateTime.Today)); AnimateEntrance(MonthDays); }
    internal void SelectDateForTest(DateOnly day) => SetSelectedDay(day);

    private void OnAddEvent(object sender, RoutedEventArgs e) => OpenEventEditor(null);
    internal void EditEventForTest(CalendarEntry entry) => OpenEventEditor(entry);
    private void OnEditEvent(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is CalendarEntry entry) OpenEventEditor(entry); }
    private void OpenEventEditor(CalendarEntry? entry)
    {
        _editingEventId = entry?.Id;
        _editingAllDayDuration = entry is { AllDay: true } ? entry.DurationMinutes : 1440;
        if (entry is not null) SetSelectedDay(DateOnly.FromDateTime(entry.Start.LocalDateTime));
        EventTitleBox.Text = entry?.Title ?? "";
        EventTimeBox.Text = entry?.Start.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture) ?? DateTime.Now.AddHours(1).ToString("HH:00", CultureInfo.InvariantCulture);
        EventDurationBox.Text = (entry?.DurationMinutes ?? 60).ToString(CultureInfo.InvariantCulture);
        EventNotesBox.Text = entry?.Description ?? "";
        AllDayToggle.IsChecked = entry?.AllDay ?? false;
        EventError.Text = "";
        AgendaView.Visibility = AgendaFooter.Visibility = Visibility.Collapsed;
        EventEditor.Visibility = Visibility.Visible;
        AnimateEntrance(EventEditor);
        EditingChanged?.Invoke(this, EventArgs.Empty);
        EventTitleBox.Focus(); EventTitleBox.SelectAll();
    }

    private void OnAllDayChanged(object sender, RoutedEventArgs e)
    {
        if (EventTimeBox is null || EventDurationBox is null) return;
        EventTimeBox.IsEnabled = EventDurationBox.IsEnabled = AllDayToggle.IsChecked != true;
    }

    private void OnSaveEvent(object sender, RoutedEventArgs e)
    {
        try
        {
            var allDay = AllDayToggle.IsChecked == true;
            if (!allDay && !TimeOnly.TryParseExact(EventTimeBox.Text.Trim(), new[] { "H:mm", "HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new ArgumentException("Use a 24-hour time such as 14:30.");
            var time = allDay ? TimeOnly.MinValue : TimeOnly.ParseExact(EventTimeBox.Text.Trim(), new[] { "H:mm", "HH:mm" }, CultureInfo.InvariantCulture);
            if (!allDay && !int.TryParse(EventDurationBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out _)) throw new ArgumentException("Enter a duration in minutes.");
            var duration = allDay ? _editingAllDayDuration : int.Parse(EventDurationBox.Text, CultureInfo.InvariantCulture);
            var start = OrganizerCalendar.LocalInstant(_selectedDay, time);
            if (_editingEventId is { } id) _store.UpdateEvent(id, EventTitleBox.Text, start, duration, allDay, EventNotesBox.Text);
            else _store.AddEvent(EventTitleBox.Text, start, duration, allDay, EventNotesBox.Text);
            CloseEventEditor();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or FormatException)
        { EventError.Text = ex is IOException or UnauthorizedAccessException ? "Could not save this event. Try again." : ex.Message; }
    }

    private void OnEventEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ReferenceEquals(Keyboard.FocusedElement, EventNotesBox) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        if (e.Key == Key.Enter) { OnSaveEvent(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape) { CloseEventEditor(); e.Handled = true; }
    }
    private void OnCancelEvent(object sender, RoutedEventArgs e) => CloseEventEditor();
    private void CloseEventEditor()
    {
        EventEditor.Visibility = Visibility.Collapsed;
        AgendaView.Visibility = AgendaFooter.Visibility = Visibility.Visible;
        _editingEventId = null; RefreshCalendar();
        AgendaAddButton.Focus(); EditingChanged?.Invoke(this, EventArgs.Empty);
    }
    private void OnRemoveEvent(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not CalendarEntry entry) return;
        TryStoreAction(() => _store.RemoveEvent(entry.Id));
    }

    private void OnImportCalendar(object sender, RoutedEventArgs e)
    {
        _dialogOpen = true; EditingChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var picker = new OpenFileDialog { Filter = "iCalendar files (*.ics)|*.ics", Title = "Import your calendar", Multiselect = false };
            if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
            var parsed = CalendarInterchange.ReadFile(picker.FileName);
            var count = _store.ImportEvents(parsed.Entries);
            Notification?.Invoke(this, $"Imported {count} calendar event{(count == 1 ? "" : "s")}." + (parsed.Skipped > 0 ? $" {parsed.Skipped} unsupported entr{(parsed.Skipped == 1 ? "y was" : "ies were")} skipped." : ""));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        { Notification?.Invoke(this, ex is ArgumentException ? ex.Message : "Could not read that calendar file."); }
        finally { _dialogOpen = false; EditingChanged?.Invoke(this, EventArgs.Empty); }
    }

    private void OnExportCalendar(object sender, RoutedEventArgs e)
    {
        _dialogOpen = true; EditingChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var picker = new SaveFileDialog { Filter = "iCalendar files (*.ics)|*.ics", DefaultExt = ".ics", AddExtension = true, FileName = "Winisland calendar.ics", Title = "Export your local calendar" };
            if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
            File.WriteAllText(picker.FileName, CalendarInterchange.Export(_store.Events), new System.Text.UTF8Encoding(false));
            Notification?.Invoke(this, "Your local calendar was exported.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Notification?.Invoke(this, "Could not export that calendar file."); }
        finally { _dialogOpen = false; EditingChanged?.Invoke(this, EventArgs.Empty); }
    }

    private void OnToolClick(object sender, RoutedEventArgs e) => SelectTool((string)((Button)sender).Tag);
    internal void SelectToolForTest(string tool) => SelectTool(tool, false);
    private void SelectTool(string tool, bool animate = true)
    {
        if (tool is not ("notes" or "reminders" or "clocks" or "stopwatch" or "shortcuts")) return;
        FlushEdits(); _selectedTool = tool;
        var panels = new Dictionary<string, FrameworkElement> { ["notes"] = NotesView, ["reminders"] = RemindersView,
            ["clocks"] = ClocksView, ["stopwatch"] = StopwatchView, ["shortcuts"] = ShortcutsView };
        foreach (var pair in panels) pair.Value.Visibility = pair.Key == tool ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { NotesTool, RemindersTool, ClocksTool, StopwatchTool, ShortcutsTool })
        {
            var active = (string)button.Tag == tool;
            button.Background = active ? FrozenBrush(0x32, 0x32, 0x35) : Brushes.Transparent;
            button.Foreground = active ? Brushes.White : Muted;
        }
        if (animate) AnimateEntrance(panels[tool]);
        if (tool == "clocks") UpdateClockTimes();
        if (tool == "stopwatch") UpdateStopwatch();
        SynchronizeDisplayTimer(); EditingChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnNotesChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing || _disposed) return;
        _notesDirty = true; UpdateNoteStatus();
        NotesSaveLabel.Text = "Saving…"; _noteTimer.Stop(); _noteTimer.Start();
    }
    private void UpdateNoteStatus() => NotesCountLabel.Text = $"{NotesBox.Text.Length:N0} / {OrganizerStore.MaximumNoteLength:N0}";

    private void RefreshReminders()
    {
        var reminders = _store.Reminders.OrderBy(r => r.Completed).ThenBy(r => r.Due ?? DateTimeOffset.MaxValue).ToArray();
        ReminderItems.ItemsSource = reminders;
        RemindersEmpty.Visibility = reminders.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ReminderCountLabel.Text = $"{reminders.Count(r => !r.Completed)} remaining";
    }
    private void OnReminderToggle(object sender, RoutedEventArgs e)
    { if (((CheckBox)sender).Tag is ReminderEntry item) TryStoreAction(() => _store.ToggleReminder(item.Id)); }
    private void OnRemoveReminder(object sender, RoutedEventArgs e)
    { if (((Button)sender).Tag is ReminderEntry item) TryStoreAction(() => _store.RemoveReminder(item.Id)); }
    private void OnEditReminder(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not ReminderEntry item) return;
        _editingReminderId = item.Id; ReminderTitleBox.Text = item.Title;
        ReminderDueBox.Text = item.Due?.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "";
        ReminderSaveButton.Content = "Save"; ReminderCancelButton.Visibility = Visibility.Visible;
        EditingChanged?.Invoke(this, EventArgs.Empty); ReminderTitleBox.Focus(); ReminderTitleBox.SelectAll();
    }
    private void OnSaveReminder(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!OrganizerCalendar.TryParseReminderDue(ReminderDueBox.Text, DateTimeOffset.Now, out var due))
                throw new ArgumentException("Use 18:00, tomorrow 09:00, or a date such as 2027-03-12 14:30.");
            if (_editingReminderId is { } id) _store.UpdateReminder(id, ReminderTitleBox.Text, due);
            else _store.AddReminder(ReminderTitleBox.Text, due);
            CancelReminderEdit();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        { ReminderError.Text = ex is IOException or UnauthorizedAccessException ? "Could not save this reminder." : ex.Message; ReminderError.Foreground = Red; }
    }
    private void OnReminderKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OnSaveReminder(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape) { CancelReminderEdit(); e.Handled = true; }
    }
    private void OnCancelReminder(object sender, RoutedEventArgs e) => CancelReminderEdit();
    private void CancelReminderEdit()
    {
        _editingReminderId = null; ReminderTitleBox.Clear(); ReminderDueBox.Clear();
        ReminderSaveButton.Content = "Add"; ReminderCancelButton.Visibility = Visibility.Collapsed;
        ReminderError.Text = "Due time is optional · try tomorrow 09:00"; ReminderError.Foreground = Muted;
        EditingChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnStoreChanged(object? sender, OrganizerChangedEventArgs e)
    {
        if (e.Change.HasFlag(OrganizerChange.Events)) RefreshCalendar();
        if (e.Change.HasFlag(OrganizerChange.Reminders)) { RefreshReminders(); if (_loaded) ScheduleReminder(); }
        if (e.Change.HasFlag(OrganizerChange.Clocks))
        {
            for (var i = 0; i < _clocks.Count; i++)
            {
                _clocks[i] = _clocks[i] with { Id = _store.ClockZoneIds[i] };
                if (_clocks[i].Choice is { } choice) choice.SelectedValue = _store.ClockZoneIds[i];
            }
            UpdateClockTimes();
        }
    }

    private void ScheduleReminder()
    {
        _reminderTimer.Stop();
        if (_disposed || !_loaded || _store.NextReminderDue is not { } due) return;
        var delay = due - DateTimeOffset.Now;
        _reminderTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 250, TimeSpan.FromHours(1).TotalMilliseconds));
        _reminderTimer.Start();
    }
    private void OnReminderDue(object? sender, EventArgs e)
    {
        _reminderTimer.Stop();
        try
        {
            foreach (var item in _store.TakeDueReminders(DateTimeOffset.Now)) Notification?.Invoke(this, "Reminder: " + item.Title);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Log.Error("Acknowledge organizer reminder", ex); }
        ScheduleReminder();
    }

    private void OnSystemTimeChanged(object? sender, EventArgs e)
    {
        if (_disposed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            TimeZoneInfo.ClearCachedData(); UpdateClockTimes(); ScheduleReminder();
            if (CalendarView.IsVisible) RefreshCalendar();
        });
    }

    private void BuildClockCards()
    {
        _clocks.Clear(); ClockCards.Children.Clear();
        for (var i = 0; i < 4; i++)
        {
            var index = i; var id = _store.ClockZoneIds[i];
            ComboBox? cityChoice = null;
            var grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
            grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(17) });
            if (i == 0) grid.Children.Add(new TextBlock { Text = "Local time", FontSize = 11, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
            else
            {
                var choice = new ComboBox { ItemsSource = WorldClockChoice.All, DisplayMemberPath = "City", SelectedValuePath = "Id",
                    SelectedValue = id, FontSize = 11, Width = 145, Height = 22, HorizontalAlignment = HorizontalAlignment.Left,
                    Style = (Style)FindResource("ClockChoice") };
                cityChoice = choice;
                System.Windows.Automation.AutomationProperties.SetName(choice, $"World clock city {i}");
                choice.SelectionChanged += (_, _) => { if (choice.SelectedValue is string selected) TryStoreAction(() => _store.SetClockZone(index, selected)); };
                choice.DropDownOpened += (_, _) => { _clockDropdownOpen = true; EditingChanged?.Invoke(this, EventArgs.Empty); };
                choice.DropDownClosed += (_, _) => { _clockDropdownOpen = false; EditingChanged?.Invoke(this, EventArgs.Empty); };
                grid.Children.Add(choice);
            }
            var time = new TextBlock { FontSize = 32, FontFamily = (FontFamily)FindResource("DisplayFont"), FontWeight = FontWeights.Light,
                VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White };
            System.Windows.Documents.Typography.SetNumeralAlignment(time, FontNumeralAlignment.Tabular);
            var date = new TextBlock { FontSize = 10, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetRow(time, 1); Grid.SetRow(date, 2); grid.Children.Add(time); grid.Children.Add(date);
            ClockCards.Children.Add(new Border { Background = FrozenBrush(0x19, 0x19, 0x1B), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(3), Child = grid });
            _clocks.Add(new ClockView(id, time, date, cityChoice));
        }
        UpdateClockTimes();
    }

    private void UpdateClockTimes()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var clock in _clocks)
        {
            try
            {
                var local = TimeZoneInfo.ConvertTime(now, WorldClockChoice.Resolve(clock.Id));
                clock.Time.Text = local.ToString("HH:mm", CultureInfo.CurrentCulture);
                var offset = local.Offset;
                var hours = Math.Abs((int)offset.TotalMinutes) / 60; var minutes = Math.Abs(offset.Minutes);
                clock.Date.Text = local.ToString("ddd, d MMM", CultureInfo.CurrentCulture) + $" · UTC{(offset >= TimeSpan.Zero ? "+" : "−")}{hours}" + (minutes == 0 ? "" : $":{minutes:00}");
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            { clock.Time.Text = "—"; clock.Date.Text = "Time zone unavailable"; }
        }
    }

    private void OnStopwatchToggle(object sender, RoutedEventArgs e) { _stopwatch.Toggle(); UpdateStopwatch(); SynchronizeDisplayTimer(); }
    private void OnStopwatchLap(object sender, RoutedEventArgs e) { _stopwatch.AddLap(); RefreshLaps(); }
    private void OnStopwatchReset(object sender, RoutedEventArgs e) { _stopwatch.Reset(); UpdateStopwatch(); RefreshLaps(); SynchronizeDisplayTimer(); }
    private void UpdateStopwatch()
    {
        StopwatchLabel.Text = OrganizerStopwatch.Format(_stopwatch.Elapsed);
        StopwatchToggleButton.Content = _stopwatch.IsRunning ? "Pause" : _stopwatch.Elapsed > TimeSpan.Zero ? "Resume" : "Start";
        System.Windows.Automation.AutomationProperties.SetName(StopwatchToggleButton, $"{StopwatchToggleButton.Content} stopwatch");
        StopwatchLapButton.IsEnabled = _stopwatch.IsRunning;
        LapsEmpty.Visibility = _stopwatch.Laps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void RefreshLaps()
    {
        LapItems.ItemsSource = _stopwatch.Laps.Select(l => new LapView(l.Number, OrganizerStopwatch.Format(l.Split), OrganizerStopwatch.Format(l.Total))).ToArray();
        LapsEmpty.Visibility = _stopwatch.Laps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SynchronizeDisplayTimer()
    {
        _displayTimer.Stop();
        if (!IsVisible || ToolsView.Visibility != Visibility.Visible || _disposed) return;
        if (_selectedTool == "clocks") { _displayTimer.Interval = TimeSpan.FromSeconds(1); _displayTimer.Start(); }
        else if (_selectedTool == "stopwatch" && _stopwatch.IsRunning) { _displayTimer.Interval = TimeSpan.FromMilliseconds(100); _displayTimer.Start(); }
    }

    private void OnShortcut(object sender, RoutedEventArgs e)
    {
        var target = ((Button)sender).Tag as string;
        var command = target switch { "downloads" => "shell:Downloads", "documents" => "shell:Personal", "screenshot" => "ms-screenclip:",
            "sound" => "ms-settings:sound", "network" => "ms-settings:network-status", "tasks" => "taskmgr.exe", _ => "" };
        if (command.Length == 0) return;
        try { Process.Start(new ProcessStartInfo(command) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { Notification?.Invoke(this, "That Windows shortcut is unavailable."); }
    }

    private void TryStoreAction(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Notification?.Invoke(this, ex is ArgumentException ? ex.Message : "Could not save this change. Try again."); }
    }

    private void AnimateEntrance(FrameworkElement element)
    {
        var enabled = (bool)GetValue(TransitionEnabledProperty) && SystemParameters.ClientAreaAnimation;
        element.BeginAnimation(OpacityProperty, null);
        if (element.RenderTransform is not TranslateTransform transform) { transform = new TranslateTransform(); element.RenderTransform = transform; }
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        if (!enabled) { element.Opacity = 1; transform.Y = 0; return; }
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(140)) { FillBehavior = FillBehavior.Stop });
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(3, 0, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }

    private void FinishTransitions()
    {
        if (_initializing) return;
        foreach (var element in new FrameworkElement[] { CalendarView, ToolsView, MonthDays, EventEditor, NotesView, RemindersView, ClocksView, StopwatchView, ShortcutsView })
        {
            element.BeginAnimation(OpacityProperty, null); element.Opacity = 1;
            if (element.RenderTransform is TranslateTransform transform) { transform.BeginAnimation(TranslateTransform.YProperty, null); transform.Y = 0; }
        }
    }

    private static Brush FrozenBrush(byte r, byte g, byte b) { var brush = new SolidColorBrush(Color.FromRgb(r, g, b)); brush.Freeze(); return brush; }

    public void Dispose()
    {
        if (_disposed) return;
        FlushEdits(); _disposed = true;
        _noteTimer.Stop(); _displayTimer.Stop(); _reminderTimer.Stop();
        _store.Changed -= OnStoreChanged;
        SystemEvents.TimeChanged -= OnSystemTimeChanged;
    }
}
