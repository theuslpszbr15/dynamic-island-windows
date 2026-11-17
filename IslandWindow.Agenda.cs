using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DynamicIsland.Services;

namespace DynamicIsland;

// The real agenda: meetings from iCal links and Outlook, "Daily em 5 min" with Entrar, and the list on the agenda page.
public partial class IslandWindow
{
    List<CalendarEvent> _events = [];
    DateTime _eventsAt;
    bool _eventsBusy;
    readonly HashSet<string> _meetingAlerts = [];

    bool AgendaOn => _settings.CalendarFeeds.Count > 0 || _settings.OutlookCalendar;

    /// <summary>Every minute (ExtrasTick): reload every 10 minutes, then warn about what starts soon.</summary>
    async void AgendaTick()
    {
        if (!AgendaOn || SnapshotMode) return;
        if (DateTime.Now - _eventsAt > TimeSpan.FromMinutes(10) && !_eventsBusy)
        {
            _eventsBusy = true;
            try { _events = await CalendarService.LoadAsync(_settings.CalendarFeeds, _settings.OutlookCalendar); }
            catch { }
            finally { _eventsBusy = false; _eventsAt = DateTime.Now; }
            if (_expanded && _page == PageCalendar) UpdateEventsList();
        }
        CheckMeetings();
    }

    /// <summary>Loads again now (after the feeds change in the settings).</summary>
    public void ReloadAgenda()
    {
        _eventsAt = default;
        AgendaTick();
    }

    void CheckMeetings()
    {
        var now = DateTime.Now;
        foreach (var e in _events)
        {
            var minutes = (int)Math.Ceiling((e.Start - now).TotalMinutes);
            int bucket = minutes switch
            {
                <= 0 when now < e.Start.AddMinutes(5) => 0,
                <= 1 and > 0 => 1,
                <= 5 and > 1 => 5,
                <= 10 and > 5 => 10,
                _ => -1,
            };
            if (bucket < 0 || !_meetingAlerts.Add($"{e.Start:O}|{e.Title}|{bucket}")) continue;
            var join = e.JoinUrl;
            ShowInfo("\uE787", bucket == 0 ? $"Começou: {e.Title}" : $"{e.Title} em {minutes} min",
                join != null ? "Entrar" : e.Start.ToString("HH:mm", CultureInfo.CurrentCulture),
                glyphColor: AgentSession.Working, action: join != null ? () => OpenJoin(join) : null, seconds: 12);
            if (bucket <= 1) ((App)Application.Current).DesktopPet?.Visit(IslandAnchor());
            break; // one at a time
        }
        if (_meetingAlerts.Count > 200) _meetingAlerts.Clear();
    }

    static void OpenJoin(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    /// <summary>The next three meetings on the agenda page (the weather gets smaller when there are any).</summary>
    void UpdateEventsList()
    {
        EventsList.Children.Clear();
        var now = DateTime.Now;
        var next = _events.Where(e => e.End > now).Take(3).ToList();
        EventsList.Visibility = Vis(next.Count > 0);
        WeatherMore.Visibility = Vis(next.Count == 0);
        foreach (var e in next)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
            var when = new TextBlock
            {
                Text = e.Start.Date == DateTime.Today ? e.Start.ToString("HH:mm", CultureInfo.CurrentCulture) : "amanhã",
                FontSize = 11.5, FontWeight = FontWeights.SemiBold, Width = 46, VerticalAlignment = VerticalAlignment.Center,
                Foreground = e.Start <= now ? new SolidColorBrush(AgentSession.Done) : new SolidColorBrush(AgentSession.Working),
            };
            DockPanel.SetDock(when, Dock.Left);
            row.Children.Add(when);
            if (e.JoinUrl is { } url)
            {
                var join = new TextBlock
                {
                    Text = "Entrar", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand,
                    Foreground = new SolidColorBrush(AgentSession.Working), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                };
                join.MouseLeftButtonUp += (_, ev) => { ev.Handled = true; OpenJoin(url); };
                DockPanel.SetDock(join, Dock.Right);
                row.Children.Add(join);
            }
            var title = new TextBlock { Text = e.Title, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            row.Children.Add(title);
            EventsList.Children.Add(row);
        }
    }

    /// <summary>For snapshots: a fixed agenda.</summary>
    void SetEvents(List<CalendarEvent> events) => _events = events;
}
