using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AudioMesh.Core;
using NAudio.CoreAudioApi;

namespace AudioMesh.Desktop;

public static class BackendFactory
{
    public static IAudioBackend Create() =>
        OperatingSystem.IsWindows() ? new WindowsBackend() :
        OperatingSystem.IsLinux() ? new LinuxBackend() :
        throw new PlatformNotSupportedException("AudioMesh currently supports Windows and Linux.");
}

public sealed class WindowsBackend : IAudioBackend
{
    public bool CanCreateVirtualSink => false;

    public Task<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken token = default)
    {
        return Task.Run<IReadOnlyList<AudioDevice>>(() =>
        {
            using var enumerator = new MMDeviceEnumerator();
            var result = new List<AudioDevice>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active))
            {
                using (device)
                {
                    var name = device.FriendlyName;
                    bool virt = name.Contains("virtual", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("cable", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("sonar", StringComparison.OrdinalIgnoreCase);
                    result.Add(new AudioDevice(device.ID, name,
                        device.DataFlow == DataFlow.Capture ? AudioDeviceKind.Recording : AudioDeviceKind.Playback, virt));
                }
            }
            return result;
        }, token);
    }

    public Task SetEndpointVolumeAsync(string deviceId, float volume, bool muted, CancellationToken token = default)
    {
        return Task.Run(() =>
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(deviceId);
            device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0, 1);
            device.AudioEndpointVolume.Mute = muted;
        }, token);
    }

    public Task<string> CreateVirtualSinkAsync(string name, CancellationToken token = default) =>
        throw new NotSupportedException("Creating Windows audio endpoints requires a virtual device driver. Existing virtual cables are supported.");

    public Task RemoveVirtualSinkAsync(string moduleId, CancellationToken token = default) =>
        throw new NotSupportedException("AudioMesh does not remove Windows system audio devices.");
}

public sealed class LinuxBackend : IAudioBackend
{
    public bool CanCreateVirtualSink => true;
    private static readonly Regex SafeName = new("^[A-Za-z][A-Za-z0-9_]{0,47}$", RegexOptions.Compiled);

    public async Task<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken token = default)
    {
        var devices = new List<AudioDevice>();
        foreach (var (type,kind) in new[] { ("sinks", AudioDeviceKind.Playback), ("sources", AudioDeviceKind.Recording) })
        {
            var output = await RunAsync(["list", "short", type], token);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split('\t');
                if (parts.Length < 2) continue;
                var id = parts[1];
                var virt = id.Contains("null", StringComparison.OrdinalIgnoreCase) ||
                           id.Contains("monitor", StringComparison.OrdinalIgnoreCase) ||
                           id.Contains("virtual", StringComparison.OrdinalIgnoreCase) ||
                           id.StartsWith("audiomesh_", StringComparison.OrdinalIgnoreCase);
                devices.Add(new AudioDevice(id, id, kind, virt));
            }
        }
        return devices;
    }

    public async Task SetEndpointVolumeAsync(string deviceId, float volume, bool muted, CancellationToken token = default)
    {
        // Device IDs originate from pactl enumeration, never user text.
        var kind = (await GetDevicesAsync(token)).FirstOrDefault(d => d.Id == deviceId)?.Kind
                   ?? throw new InvalidOperationException("Selected device is no longer available.");
        var target = kind == AudioDeviceKind.Playback ? "sink" : "source";
        await RunAsync(["set-" + target + "-volume", deviceId,
            (Math.Clamp(volume, 0f, 1f) * 100).ToString("0", CultureInfo.InvariantCulture) + "%"], token);
        await RunAsync(["set-" + target + "-mute", deviceId, muted ? "1" : "0"], token);
    }

    public async Task<string> CreateVirtualSinkAsync(string name, CancellationToken token = default)
    {
        if (!SafeName.IsMatch(name))
            throw new ArgumentException("Use 1–48 letters, digits or underscores, starting with a letter.");
        // Preserve module ID so only sinks created by this instance can be unloaded.
        var result = (await RunAsync(["load-module", "module-null-sink",
            "sink_name=audiomesh_" + name, "sink_properties=device.description=AudioMesh_" + name], token)).Trim();
        if (!int.TryParse(result, out _))
            throw new InvalidOperationException("Audio server did not return a valid module identifier: " + result);
        return result;
    }

    public async Task RemoveVirtualSinkAsync(string moduleId, CancellationToken token = default)
    {
        if (!int.TryParse(moduleId, out _))
            throw new ArgumentException("Invalid module ID.");
        await RunAsync(["unload-module", moduleId], token);
    }

    /// <summary>Remove one explicitly selected, profile-managed AudioMesh null sink by name.</summary>
    public async Task<bool> RemoveNamedVirtualSinkAsync(string name, CancellationToken token = default)
    {
        if (!SafeName.IsMatch(name)) throw new ArgumentException("Invalid AudioMesh virtual cable name.");
        var json = await RunAsync(["--format=json", "list", "modules"], token);
        using var doc = JsonDocument.Parse(json);
        var targetArgument = "sink_name=audiomesh_" + name;
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("name", out var module) ||
                module.GetString() != "module-null-sink" ||
                !entry.TryGetProperty("argument", out var arg) ||
                !entry.TryGetProperty("index", out var id)) continue;
            var parts = (arg.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!parts.Contains(targetArgument, StringComparer.Ordinal)) continue;
            await RunAsync(["unload-module", id.ToString()], token);
            return true;
        }
        return false;
    }

    private static async Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken token)
    {
        var psi = new ProcessStartInfo("pactl")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start pactl.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException("pactl: " + error.Trim());
            return output;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Timed out communicating with the Linux audio server.");
        }
    }
}
