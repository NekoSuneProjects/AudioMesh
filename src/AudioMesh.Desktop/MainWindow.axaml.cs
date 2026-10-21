using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AudioMesh.Core;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace AudioMesh.Desktop;

public partial class MainWindow : Window
{
    private readonly IAudioBackend _backend = BackendFactory.Create();
    private readonly ProfileStore _store = new();
    private readonly LinuxMonitorRouter? _monitorRouter = OperatingSystem.IsLinux() ? new LinuxMonitorRouter() : null;
    private readonly LinuxStreamRouter? _streamRouter = OperatingSystem.IsLinux() ? new LinuxStreamRouter() : null;
    private AudioProfile _profile = AudioProfile.Default();
    private readonly ObservableCollection<BusRow> _rows = [];
    private readonly Stack<string> _createdSinkModules = new();
    private List<AudioDevice> _devices = [];
    private bool _loading;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly SemaphoreSlim _routeLock = new(1, 1);
    private DispatcherTimer? _routeTimer;
    private readonly Dictionary<string, string> _lastRouted = new();
    private readonly HashSet<string> _manualOverrides = [];

    public MainWindow()
    {
        InitializeComponent();
        BusList.ItemsSource = _rows;
        LinuxRoutingPanel.IsVisible = _streamRouter is not null;
        LinuxMonitorPanel.IsVisible = _monitorRouter is not null;
        CreateSinkButton.IsEnabled = _backend.CanCreateVirtualSink;
        RemoveSinkButton.IsEnabled = false;
        Opened += async (_, _) => await LoadAsync();
        Closed += (_, _) => _routeTimer?.Stop();
    }

    private async Task LoadAsync()
    {
        try
        {
            _loading = true;
            _profile = await _store.LoadAsync();
            await RefreshAsync();
            RenderRows();
            if (_streamRouter is not null)
            {
                await RefreshStreamsAsync();
                RenderRules();
                _routeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _routeTimer.Tick += async (_, _) => await ApplyRoutingRulesAsync();
                _routeTimer.Start();
            }
            SetStatus("Ready. Changes are saved automatically.");
        }
        catch (Exception ex) { SetStatus("Audio initialization failed: " + ex.Message); }
        finally { _loading = false; }
    }

    private void RenderRows()
    {
        foreach (var row in _rows) row.PropertyChanged -= RowChanged;
        _rows.Clear();
        foreach (var bus in _profile.Buses)
        {
            var row = new BusRow(bus, _devices);
            row.PropertyChanged += RowChanged;
            _rows.Add(row);
        }
    }

    private async void RowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading || sender is not BusRow row) return;
        try
        {
            if (e.PropertyName is nameof(BusRow.VolumePercent) or nameof(BusRow.Muted) or nameof(BusRow.SelectedDevice))
            {
                row.Commit();
                await SaveAsync();
                if (row.Bus.DeviceId is { } deviceId)
                    await _backend.SetEndpointVolumeAsync(deviceId, row.Bus.Volume, row.Bus.Muted);
            }
        }
        catch (Exception ex) { SetStatus("Could not apply device setting: " + ex.Message); }
    }

    private async Task SaveAsync()
    {
        await _saveLock.WaitAsync();
        try { await _store.SaveAsync(_profile); }
        finally { _saveLock.Release(); }
    }

    private async Task RefreshAsync()
    {
        _devices = (await _backend.GetDevicesAsync()).ToList();
        RenderRows();
        if (_streamRouter is not null)
            SinkSelection.ItemsSource = _devices.Where(d => d.Kind == AudioDeviceKind.Playback)
                .Select(d => new DeviceOption(d.Id, d.Name)).ToList();
        if (_monitorRouter is not null)
        {
            MonitorSourceSelection.ItemsSource = _devices
                .Where(d => d.Kind == AudioDeviceKind.Playback && d.Id.StartsWith("audiomesh_", StringComparison.Ordinal))
                .Select(d => new DeviceOption(d.Id, d.Name)).ToList();
            MonitorTargetSelection.ItemsSource = _devices
                .Where(d => d.Kind == AudioDeviceKind.Playback && !d.Id.StartsWith("audiomesh_", StringComparison.Ordinal))
                .Select(d => new DeviceOption(d.Id, d.Name)).ToList();
        }
        SetStatus($"Found {_devices.Count} audio endpoints. Select an endpoint for each bus.");
    }

    private async void RefreshClicked(object? sender, RoutedEventArgs e)
    {
        try { await RefreshAsync(); }
        catch (Exception ex) { SetStatus("Refresh failed: " + ex.Message); }
    }

    private async void AddBusClicked(object? sender, RoutedEventArgs e)
    {
        var name = BusName.Text?.Trim();
        if (string.IsNullOrEmpty(name)) { SetStatus("Enter a channel name."); return; }
        if (name.Length > 64) { SetStatus("Channel name must be 64 characters or fewer."); return; }
        _profile.Buses.Add(new AudioBus { Name = name });
        BusName.Text = "";
        RenderRows();
        await SaveAsync();
        SetStatus($"Added channel '{name}'.");
    }

    private async void RemoveBusClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id) return;
        _profile.Buses.RemoveAll(b => b.Id == id);
        RenderRows();
        await SaveAsync();
    }

    private async void CreateSinkClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var name = SinkName.Text?.Trim() ?? "";
            var id = await _backend.CreateVirtualSinkAsync(name);
            _createdSinkModules.Push(id);
            RemoveSinkButton.IsEnabled = true;
            await RefreshAsync();
            SetStatus($"Created Linux virtual sink audiomesh_{name} (module {id}). Select it from a bus.");
        }
        catch (Exception ex) { SetStatus("Could not create cable: " + ex.Message); }
    }

    private async void RemoveSinkClicked(object? sender, RoutedEventArgs e)
    {
        if (!_createdSinkModules.TryPeek(out var id)) return;
        try
        {
            await _backend.RemoveVirtualSinkAsync(id);
            _createdSinkModules.Pop();
            RemoveSinkButton.IsEnabled = _createdSinkModules.Count > 0;
            await RefreshAsync();
            SetStatus("Removed last virtual sink created by AudioMesh.");
        }
        catch (Exception ex) { SetStatus("Could not remove cable: " + ex.Message); }
    }

    private async Task RefreshStreamsAsync()
    {
        if (_streamRouter is null) return;
        var streams = await _streamRouter.GetStreamsAsync();
        StreamSelection.ItemsSource = streams.Select(s => new StreamOption(s.Id, s.Display)).ToList();
        if (streams.Count == 0) SetStatus("No active Linux playback streams. Start playing audio and refresh.");
    }

    private async void RefreshStreamsClicked(object? sender, RoutedEventArgs e)
    {
        try { await RefreshStreamsAsync(); }
        catch (Exception ex) { SetStatus("Cannot list playback streams: " + ex.Message); }
    }

    private async void RouteStreamClicked(object? sender, RoutedEventArgs e)
    {
        if (_streamRouter is null) return;
        if (StreamSelection.SelectedItem is not StreamOption stream ||
            SinkSelection.SelectedItem is not DeviceOption { Id: { } sink })
        {
            SetStatus("Select an active application stream and an output device.");
            return;
        }
        try
        {
            await _streamRouter.MoveAsync(stream.Id, sink);
            _manualOverrides.Add(stream.Id);
            _lastRouted.Remove(stream.Id);
            SetStatus($"Moved {stream.Label} to {SinkSelection.SelectedItem}.");
            await RefreshStreamsAsync();
        }
        catch (Exception ex) { SetStatus("Routing failed: " + ex.Message); }
    }

    private async void ConnectMonitorClicked(object? sender, RoutedEventArgs e)
    {
        if (_monitorRouter is null) return;
        if (MonitorSourceSelection.SelectedItem is not DeviceOption { Id: { } source } ||
            MonitorTargetSelection.SelectedItem is not DeviceOption { Id: { } target })
        {
            SetStatus("Choose a virtual cable and a monitoring output.");
            return;
        }
        try
        {
            await _monitorRouter.ConnectAsync(source, target);
            SetStatus($"Monitoring {source} through {target}. Audio is now forwarded to that output.");
        }
        catch (Exception ex) { SetStatus("Monitor connection failed: " + ex.Message); }
    }

    private async void DisconnectMonitorClicked(object? sender, RoutedEventArgs e)
    {
        if (_monitorRouter is null) return;
        if (MonitorSourceSelection.SelectedItem is not DeviceOption { Id: { } source })
        {
            SetStatus("Select the virtual cable to disconnect.");
            return;
        }
        try
        {
            await _monitorRouter.DisconnectAsync(source);
            SetStatus($"Disconnected monitoring for {source}.");
        }
        catch (Exception ex) { SetStatus("Could not disconnect monitor: " + ex.Message); }
    }

    private void RenderRules()
    {
        RoutingRuleSelection.ItemsSource = _profile.AppRoutingRules
            .Select(r => new RuleOption(r.Application, r.SinkId)).ToList();
    }

    private async void SaveRoutingRuleClicked(object? sender, RoutedEventArgs e)
    {
        if (_streamRouter is null || StreamSelection.SelectedItem is not StreamOption selected ||
            SinkSelection.SelectedItem is not DeviceOption { Id: { } sink }) 
        {
            SetStatus("Choose a running application and an output before saving a rule.");
            return;
        }
        try
        {
            var stream = (await _streamRouter.GetStreamsAsync()).FirstOrDefault(x => x.Id == selected.Id);
            if (stream is null) { SetStatus("The selected application stream has ended."); return; }
            // Prefer executable names, which remain stable when the application's display name changes.
            var key = string.IsNullOrWhiteSpace(stream.Executable) ? stream.AppName : stream.Executable;
            if (string.IsNullOrWhiteSpace(key) || key == "Unknown application")
            { SetStatus("This stream has no identifiable application name."); return; }
            _profile.AppRoutingRules ??= [];
            _profile.AppRoutingRules.RemoveAll(x => x.Application == key);
            _profile.AppRoutingRules.Add(new AppRoutingRule { Application = key, SinkId = sink });
            _lastRouted.Clear();
            _manualOverrides.Clear();
            await SaveAsync();
            RenderRules();
            await ApplyRoutingRulesAsync();
            SetStatus($"Saved auto-routing: {key} → {sink}.");
        }
        catch (Exception ex) { SetStatus("Could not save routing rule: " + ex.Message); }
    }

    private async void DeleteRoutingRuleClicked(object? sender, RoutedEventArgs e)
    {
        if (RoutingRuleSelection.SelectedItem is not RuleOption selected) return;
        _profile.AppRoutingRules.RemoveAll(x => x.Application == selected.Application && x.SinkId == selected.Sink);
        _lastRouted.Clear();
        await SaveAsync();
        RenderRules();
        SetStatus("Deleted application routing rule.");
    }

    private async Task ApplyRoutingRulesAsync()
    {
        if (_streamRouter is null || !_routeLock.Wait(0)) return;
        try
        {
            var rules = _profile.AppRoutingRules ?? [];
            if (rules.Count == 0) return;
            var streams = await _streamRouter.GetStreamsAsync();
            var activeIds = streams.Select(x => x.Id).ToHashSet();
            foreach (var stale in _lastRouted.Keys.Where(id => !activeIds.Contains(id)).ToArray())
                _lastRouted.Remove(stale);
            _manualOverrides.RemoveWhere(id => !activeIds.Contains(id));
            var sinkIndices = await _streamRouter.GetSinkIndicesAsync();
            var targets = _devices.Where(d => d.Kind == AudioDeviceKind.Playback)
                .Select(d => d.Id).ToHashSet();
            foreach (var stream in streams)
            {
                var rule = rules.FirstOrDefault(x => x.Enabled &&
                    (x.Application == stream.Executable || x.Application == stream.AppName));
                if (rule is null || !targets.Contains(rule.SinkId) ||
                    _manualOverrides.Contains(stream.Id)) continue;
                // Detect external moves too: never assume the last requested destination is still active.
                if (sinkIndices.TryGetValue(rule.SinkId, out var sinkIndex) && sinkIndex == stream.SinkIndex)
                {
                    _lastRouted[stream.Id] = rule.SinkId;
                    continue;
                }
                if (_lastRouted.TryGetValue(stream.Id, out var previous) && previous == rule.SinkId)
                {
                    // An external mixer intentionally moved the stream. Yield rather than fight it.
                    _manualOverrides.Add(stream.Id);
                    continue;
                }
                try
                {
                    await _streamRouter.MoveAsync(stream.Id, rule.SinkId);
                    _lastRouted[stream.Id] = rule.SinkId;
                }
                catch (Exception ex) { SetStatus($"Routing {stream.AppName} failed: {ex.Message}"); }
            }
        }
        catch (Exception ex) { SetStatus("Automatic routing failed: " + ex.Message); }
        finally { _routeLock.Release(); }
    }

    private void SetStatus(string text) => StatusText.Text = text;
}

public sealed class BusRow : INotifyPropertyChanged
{
    public AudioBus Bus { get; }
    public ObservableCollection<DeviceOption> AvailableDevices { get; } = [];
    private DeviceOption? _selectedDevice;
    private double _volumePercent;
    private bool _muted;
    public string Id => Bus.Id;
    public string Name => Bus.Name;
    public event PropertyChangedEventHandler? PropertyChanged;

    public BusRow(AudioBus bus, IEnumerable<AudioDevice> devices)
    {
        Bus = bus;
        AvailableDevices.Add(new DeviceOption(null, "— Not assigned —"));
        foreach (var device in devices)
            AvailableDevices.Add(new DeviceOption(device.Id,
                $"{(device.Kind == AudioDeviceKind.Playback ? "OUT" : "IN")} • {device.Name}" +
                (device.IsVirtual ? " (virtual)" : "")));
        _selectedDevice = AvailableDevices.FirstOrDefault(o => o.Id == bus.DeviceId) ?? AvailableDevices[0];
        _volumePercent = bus.Volume * 100;
        _muted = bus.Muted;
    }

    public DeviceOption? SelectedDevice
    {
        get => _selectedDevice;
        set { if (_selectedDevice == value) return; _selectedDevice = value; Notify(); }
    }

    public double VolumePercent
    {
        get => _volumePercent;
        set { if (Math.Abs(_volumePercent - value) < 0.001) return; _volumePercent = value; Notify(); Notify(nameof(VolumeLabel)); }
    }

    public string VolumeLabel => $"{_volumePercent:0}%";
    public bool Muted
    {
        get => _muted;
        set { if (_muted == value) return; _muted = value; Notify(); }
    }

    public void Commit()
    {
        Bus.DeviceId = SelectedDevice?.Id;
        Bus.Volume = (float)(_volumePercent / 100);
        Bus.Muted = _muted;
    }

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record DeviceOption(string? Id, string Label)
{
    public override string ToString() => Label;
}

public sealed record StreamOption(string Id, string Label)
{
    public override string ToString() => Label;
}

public sealed record RuleOption(string Application, string Sink)
{
    public override string ToString() => $"{Application} → {Sink}";
}
