using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioMesh.Desktop;

/// <summary>
/// Monitors an existing Windows virtual playback endpoint with WASAPI loopback.
/// Requires an installed virtual audio device (e.g. VB-CABLE); does not install a driver.
/// </summary>
public sealed class WindowsMonitorRouter : IDisposable
{
    private readonly Dictionary<(string Source, string Target), Connection> _connections = new();

    public void Connect(string sourceId, string targetId)
    {
        if (sourceId == targetId)
            throw new InvalidOperationException("A device cannot monitor itself.");
        var key = (sourceId, targetId);
        if (_connections.ContainsKey(key)) return;

        using var enumerator = new MMDeviceEnumerator();
        using var source = enumerator.GetDevice(sourceId);
        using var target = enumerator.GetDevice(targetId);
        if (source.DataFlow != DataFlow.Render || target.DataFlow != DataFlow.Render)
            throw new InvalidOperationException("Choose Windows playback endpoints for source and destination.");
        if (!LooksVirtual(source.FriendlyName))
            throw new InvalidOperationException("Choose an installed virtual playback cable as the source.");
        if (LooksVirtual(target.FriendlyName))
            throw new InvalidOperationException("Choose a physical headphone or speaker endpoint as the destination to prevent routing loops.");

        var connection = new Connection(sourceId, targetId);
        try
        {
            connection.Start();
            _connections.Add(key, connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Disconnect(string sourceId, string targetId)
    {
        var key = (sourceId, targetId);
        if (!_connections.Remove(key, out var connection)) return;
        connection.Dispose();
    }

    public void DisconnectSource(string sourceId)
    {
        foreach (var key in _connections.Keys.Where(k => k.Source == sourceId).ToArray())
            Disconnect(key.Source, key.Target);
    }

    public IReadOnlyList<(string Source, string Target)> ActiveConnections => _connections.Keys.ToList();

    public void Dispose()
    {
        foreach (var connection in _connections.Values) connection.Dispose();
        _connections.Clear();
    }

    private static bool LooksVirtual(string name) =>
        name.Contains("virtual", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("cable", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("sonar", StringComparison.OrdinalIgnoreCase);

    private sealed class Connection : IDisposable
    {
        private readonly string _sourceId;
        private readonly string _targetId;
        private MMDevice? _source;
        private MMDevice? _target;
        private WasapiLoopbackCapture? _capture;
        private WasapiOut? _player;
        private BufferedWaveProvider? _buffer;
        private bool _disposed;

        public Connection(string sourceId, string targetId)
        {
            _sourceId = sourceId;
            _targetId = targetId;
        }

        public void Start()
        {
            using var enumerator = new MMDeviceEnumerator();
            _source = enumerator.GetDevice(_sourceId);
            _target = enumerator.GetDevice(_targetId);
            _capture = new WasapiLoopbackCapture(_source);
            _buffer = new BufferedWaveProvider(_capture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(500),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };
            _player = new WasapiOut(_target, AudioClientShareMode.Shared, true, 120);
            // Shared mode is necessary for mixing with other output applications.
            _player.Init(_buffer);
            _capture.DataAvailable += CaptureData;
            _player.Play();
            _capture.StartRecording();
        }

        private void CaptureData(object? sender, WaveInEventArgs e)
        {
            if (_disposed || e.BytesRecorded == 0) return;
            try { _buffer?.AddSamples(e.Buffer, 0, e.BytesRecorded); }
            catch (ObjectDisposedException) { /* capture stopped during shutdown */ }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_capture is not null)
            {
                _capture.DataAvailable -= CaptureData;
                try { _capture.StopRecording(); } catch { /* device may have been unplugged */ }
            }
            if (_player is not null)
            {
                try { _player.Stop(); } catch { /* device may have been unplugged */ }
                _player.Dispose();
            }
            _capture?.Dispose();
            _source?.Dispose();
            _target?.Dispose();
        }
    }
}
