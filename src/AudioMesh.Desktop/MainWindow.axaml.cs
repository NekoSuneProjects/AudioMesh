using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AudioMesh.Core;
using Avalonia.Controls;
using Avalonia.Interactivity;

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

    public MainWindow()
    {
        InitializeComponent();
        BusList.ItemsSource = _rows;
        LinuxRoutingPanel.IsVisible = _streamRouter is not null;
        LinuxMonitorPanel.IsVisible = _monitorRouter is not null;
        CreateSinkButton.IsEnabled = _backend.CanCreateVirtualSink;
        RemoveSinkButton.IsEnabled = false;
        Opened += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _loading = true;
            _profile = await _store.LoadAsync();
            await RefreshAsync();
            RenderRows();
            if (_streamRouter is not null) await RefreshStreamsAsync();
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
