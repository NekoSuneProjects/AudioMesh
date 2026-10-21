using AudioMesh.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Interactivity;

namespace AudioMesh.Desktop;

/// <summary>Sonar-inspired, original mixer. Linux supports live app moves; Windows app moves require the OS UI.</summary>
public sealed class ChannelMixerWindow : Window
{
    private readonly IAudioBackend _backend = BackendFactory.Create();
    private readonly LinuxStreamRouter? _linux = OperatingSystem.IsLinux() ? new LinuxStreamRouter() : null;
    private readonly WindowsSessionMixer? _windows = OperatingSystem.IsWindows() ? new WindowsSessionMixer() : null;
    private readonly ProfileStore _store = new();
    private AudioProfile _profile = AudioProfile.Default();
    private readonly StackPanel _cards = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
    private readonly StackPanel _unassigned = new() { Spacing = 6 };
    private readonly TextBlock _status = new() { Foreground = Brushes.LightSteelBlue, TextWrapping = TextWrapping.Wrap };
    private List<AudioDevice> _devices = [];
    private IReadOnlyList<PlaybackStream> _streams = [];
    private IReadOnlyDictionary<string,string> _sinkIndices = new Dictionary<string,string>();
    private IReadOnlyList<WindowsSession> _sessions = [];
    private readonly Dictionary<string, string> _ephemeralRoutes = new();

    public ChannelMixerWindow()
    {
        Title = "AudioMesh • Channel Mixer";
        Width = 1320; Height = 820; MinWidth = 880; MinHeight = 570;
        Background = Brush("#161D25");
        var header = new StackPanel { Spacing = 3 };
        header.Children.Add(Text("AUDIOMESH   /   MIXER", 29, "#63E1C6", true));
        header.Children.Add(Text("Game · Chat · Media · Aux · Microphone | Drag active app chips to a channel", 13, "#9FB0C4"));

        var refresh = new Button { Content = "Refresh running apps", Padding = new Thickness(14, 8) };
        refresh.Click += async (_, _) => await RefreshAsync();
        var nav = new DockPanel { LastChildFill = true, Margin = new Thickness(0,0,0,14) };
        DockPanel.SetDock(refresh, Dock.Right);
        nav.Children.Add(refresh);
        nav.Children.Add(header);

        var sourcePanel = new Border { Background = Brush("#202B36"), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12), Margin = new Thickness(0,0,0,12) };
        var sourceLayout = new StackPanel { Spacing = 7 };
        sourceLayout.Children.Add(Text("UNASSIGNED / RUNNING APPLICATIONS", 14, "#AFC1D8", true));
        sourceLayout.Children.Add(Text("Drag an app chip from here or another channel to Game, Chat, Media or Aux.", 12, "#879AAD"));
        sourceLayout.Children.Add(_unassigned);
        sourcePanel.Child = sourceLayout;

        var channelsScroll = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = _cards };

        var root = new DockPanel { Margin = new Thickness(22), LastChildFill = true };
        DockPanel.SetDock(nav, Dock.Top);
        DockPanel.SetDock(sourcePanel, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(nav);
        root.Children.Add(sourcePanel);
        root.Children.Add(_status);
        root.Children.Add(channelsScroll);
        Content = root;
        Opened += async (_, _) =>
        {
            _profile = await _store.LoadAsync();
            await RefreshAsync();
        };
    }

    private async Task RefreshAsync()
    {
        try
        {
            _devices = (await _backend.GetDevicesAsync()).ToList();
            if (_linux is not null)
            {
                _streams = await _linux.GetStreamsAsync();
                _sinkIndices = await _linux.GetSinkIndicesAsync();
            }
            else if (_windows is not null) _sessions = await _windows.ListAsync();
            Render();
            _status.Text = _linux is not null
                ? "Linux: dropping an app moves its active playback stream. Reopen the app if its stream disappears."
                : "Windows: dragging apps is a visual assignment only until a native per-app output backend is implemented. Use Windows sound settings for actual app routing.";
        }
        catch (Exception ex) { _status.Text = "Refresh failed: " + ex.Message; }
    }

    private void Render()
    {
        _cards.Children.Clear();
        _unassigned.Children.Clear();
        var assigned = new HashSet<string>();
        var buses = _profile.Buses.Where(b => new[] { "Game", "Chat", "Media", "Aux", "Mic" }.Contains(b.Name))
            .OrderBy(b => Array.IndexOf(new[] { "Game", "Chat", "Media", "Aux", "Mic" }, b.Name)).ToList();
        foreach (var bus in buses)
        {
            var color = bus.Name switch { "Game" => "#20D9C4", "Chat" => "#33AAFF", "Media" => "#EF4CA7",
                "Aux" => "#A36BFF", _ => "#FFB132" };
            var card = BuildBusCard(bus, color, assigned);
            _cards.Children.Add(card);
        }

        if (_linux is not null)
        {
            foreach (var stream in _streams.Where(s => !assigned.Contains(s.Id)))
                _unassigned.Children.Add(AppChip(stream.Id, stream.AppName, "#607488", true));
        }
        else
        {
            foreach (var session in _sessions.Where(s => !assigned.Contains(s.SessionId)))
                _unassigned.Children.Add(AppChip(session.SessionId, session.Name, "#607488", false));
        }
        if (_unassigned.Children.Count == 0)
            _unassigned.Children.Add(Text("No unassigned active playback apps. Start audio in a program, then refresh.", 12, "#8F9EAE"));
    }

    private Border BuildBusCard(AudioBus bus, string color, HashSet<string> assigned)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Text(bus.Name.ToUpperInvariant(), 19, color, true));
        panel.Children.Add(Text(bus.Name == "Mic" ? "Recording input" : "Playback channel", 12, "#92A7B9"));

        var playbackDevices = _devices.Where(d => d.Kind ==
            (bus.Name == "Mic" ? AudioDeviceKind.Recording : AudioDeviceKind.Playback)).ToList();
        var selector = new ComboBox { Width = 180, ItemsSource = playbackDevices,
            SelectedItem = playbackDevices.FirstOrDefault(d => d.Id == bus.DeviceId),
            PlaceholderText = "Choose audio device" };
        selector.SelectionChanged += async (_, _) =>
        {
            if (selector.SelectedItem is not AudioDevice selected) return;
            bus.DeviceId = selected.Id;
            try { await _store.SaveAsync(_profile); }
            catch (Exception ex) { _status.Text = ex.Message; }
        };
        panel.Children.Add(selector);
        panel.Children.Add(Text("Endpoint volume", 12, "#9CACC1"));
        var volume = new Slider { Minimum = 0, Maximum = 100, Value = bus.Volume * 100, Width = 170 };
        volume.PropertyChanged += async (_, e) =>
        {
            if (e.Property != Slider.ValueProperty) return;
            bus.Volume = (float)(volume.Value / 100);
            try
            {
                if (bus.DeviceId is not null) await _backend.SetEndpointVolumeAsync(bus.DeviceId, bus.Volume, bus.Muted);
            }
            catch (Exception ex) { _status.Text = ex.Message; }
        };
        panel.Children.Add(volume);
        var muted = new CheckBox { Content = "Mute", IsChecked = bus.Muted };
        muted.IsCheckedChanged += async (_, _) =>
        {
            bus.Muted = muted.IsChecked == true;
            try
            {
                if (bus.DeviceId is not null) await _backend.SetEndpointVolumeAsync(bus.DeviceId, bus.Volume, bus.Muted);
                await _store.SaveAsync(_profile);
            }
            catch (Exception ex) { _status.Text = ex.Message; }
        };
        panel.Children.Add(muted);
        panel.Children.Add(Text("APPLICATIONS", 12, "#C9D8E8", true));

        var appArea = new StackPanel { Spacing = 6, MinHeight = 220 };
        if (_linux is not null && bus.Name != "Mic")
        {
            var sinkIndex = bus.DeviceId is null ? null : _sinkIndices.GetValueOrDefault(bus.DeviceId);
            foreach (var stream in _streams.Where(s =>
                (sinkIndex is not null && s.SinkIndex == sinkIndex) ||
                _ephemeralRoutes.GetValueOrDefault(s.Id) == bus.Name))
            {
                appArea.Children.Add(AppChip(stream.Id, stream.AppName, color, true));
                assigned.Add(stream.Id);
            }
        }
        else if (_windows is not null && bus.Name != "Mic")
        {
            foreach (var session in _sessions.Where(s => _ephemeralRoutes.GetValueOrDefault(s.SessionId) == bus.Name))
            {
                appArea.Children.Add(AppChip(session.SessionId, session.Name, color, false));
                assigned.Add(session.SessionId);
            }
        }
        if (appArea.Children.Count == 0) appArea.Children.Add(Text("Drop an application here", 12, "#748698"));
        panel.Children.Add(appArea);

        var card = new Border
        {
            Width = 205, Padding = new Thickness(12), CornerRadius = new CornerRadius(9),
            Background = Brush("#28323E"), BorderBrush = Brush(color), BorderThickness = new Thickness(0,3,0,0),
            Child = panel
        };
        if (bus.Name != "Mic")
        {
            DragDrop.SetAllowDrop(card, true);
            card.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            {
                e.DragEffects = e.Data.Contains("AudioMeshStream") ? DragDropEffects.Move : DragDropEffects.None;
            });
            card.AddHandler(DragDrop.DropEvent, async (_, e) =>
            {
                if (e.Data.Get("AudioMeshStream") is not string streamId) return;
                await MoveToBusAsync(streamId, bus);
            });
        }
        return card;
    }

    private Control AppChip(string id, string name, string color, bool draggable)
    {
        var chip = new Border { Padding = new Thickness(7,5), Background = Brush(color),
            CornerRadius = new CornerRadius(5), Margin = new Thickness(0,0,2,0),
            Child = Text(name.Length > 23 ? name[..23] + "…" : name, 12, "#101722", true) };
        if (draggable)
        {
            chip.PointerPressed += async (_, e) =>
            {
                if (!e.GetCurrentPoint(chip).Properties.IsLeftButtonPressed) return;
                var data = new DataObject();
                data.Set("AudioMeshStream", id);
                await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
            };
        }
        return chip;
    }

    private async Task MoveToBusAsync(string streamId, AudioBus bus)
    {
        try
        {
            if (_linux is null)
            {
                _status.Text = "Windows cannot yet move apps by drag and drop. Open Windows Settings → System → Sound → Volume mixer.";
                return;
            }
            if (string.IsNullOrEmpty(bus.DeviceId))
            {
                _status.Text = $"Choose a playback sink on the {bus.Name} channel first.";
                return;
            }
            await _linux.MoveAsync(streamId, bus.DeviceId);
            _ephemeralRoutes[streamId] = bus.Name;
            await RefreshAsync();
            _status.Text = $"Moved playback stream #{streamId} to {bus.Name} ({bus.DeviceId}).";
        }
        catch (Exception ex) { _status.Text = "Routing failed: " + ex.Message; }
    }

    private static TextBlock Text(string value, double size, string color, bool bold = false) =>
        new() { Text = value, FontSize = size, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
            Foreground = Brush(color), TextWrapping = TextWrapping.Wrap };

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
}
