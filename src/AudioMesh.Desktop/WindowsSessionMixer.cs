using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace AudioMesh.Desktop;

/// <summary>Windows CoreAudio session mixer. Does not change a session's output device.</summary>
public sealed class WindowsSessionMixer
{
    public Task<IReadOnlyList<WindowsSession>> ListAsync() => Task.Run<IReadOnlyList<WindowsSession>>(() =>
    {
        using var enumerator = new MMDeviceEnumerator();
        var list = new List<WindowsSession>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                using var manager = device.AudioSessionManager;
                for (var i = 0; i < manager.Sessions.Count; i++)
                {
                    using var session = manager.Sessions[i];
                    try
                    {
                        var id = session.GetSessionInstanceIdentifier;
                        var pid = session.GetProcessID;
                        string name = session.DisplayName;
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            try { name = Process.GetProcessById((int)pid).ProcessName; }
                            catch { name = pid == 0 ? "System sounds" : "Process " + pid; }
                        }
                        list.Add(new WindowsSession(device.ID, id, name, device.FriendlyName,
                            session.SimpleAudioVolume.Volume, session.SimpleAudioVolume.Mute));
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
                    {
                        // The audio session may have disappeared between enumeration and inspection.
                    }
                }
            }
        }
        return list;
    });

    public Task SetAsync(string endpointId, string sessionId, float? volume, bool? muted) => Task.Run(() =>
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(endpointId);
        using var manager = device.AudioSessionManager;
        for (var i = 0; i < manager.Sessions.Count; i++)
        {
            using var session = manager.Sessions[i];
            if (session.GetSessionInstanceIdentifier != sessionId) continue;
            if (volume.HasValue) session.SimpleAudioVolume.Volume = Math.Clamp(volume.Value, 0, 1);
            if (muted.HasValue) session.SimpleAudioVolume.Mute = muted.Value;
            return;
        }
        throw new InvalidOperationException("The audio session has closed. Refresh the session list.");
    });
}

public sealed record WindowsSession(string EndpointId, string SessionId, string Name,
    string OutputName, float Volume, bool Muted)
{
    public string Label => $"{Name} → {OutputName} ({Volume:P0}{(Muted ? ", muted" : "")})";
}
