namespace AudioMesh.Core;

public enum AudioDeviceKind { Playback, Recording }
public sealed record AudioDevice(string Id, string Name, AudioDeviceKind Kind, bool IsVirtual);
public sealed class AudioBus
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Bus";
    public string? DeviceId { get; set; }
    public float Volume { get; set; } = 1f;
    public bool Muted { get; set; }
}
public sealed class AppRoutingRule
{
    public string Application { get; set; } = "";
    public string SinkId { get; set; } = "";
    public bool Enabled { get; set; } = true;
}
public sealed class AudioProfile
{
    public List<AudioBus> Buses { get; set; } = [];
    public List<AppRoutingRule> AppRoutingRules { get; set; } = [];
    public static AudioProfile Default() => new()
    {
        Buses = new[] { "Game", "Chat", "Media", "Aux", "Mic" }.Select(n => new AudioBus { Name = n }).ToList()
    };
}
public interface IAudioBackend
{
    Task<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken token = default);
    Task SetEndpointVolumeAsync(string deviceId, float volume, bool muted, CancellationToken token = default);
    bool CanCreateVirtualSink { get; }
    Task<string> CreateVirtualSinkAsync(string name, CancellationToken token = default);
    Task RemoveVirtualSinkAsync(string moduleId, CancellationToken token = default);
}
