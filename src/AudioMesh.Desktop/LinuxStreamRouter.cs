using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AudioMesh.Core;

namespace AudioMesh.Desktop;

/// <summary>Routes active Linux playback streams using the PulseAudio-compatible interface of PipeWire/PulseAudio.</summary>
public sealed class LinuxStreamRouter
{
    public async Task<IReadOnlyList<PlaybackStream>> GetStreamsAsync(CancellationToken token = default)
    {
        var output = await PactlAsync(["--format=json", "list", "sink-inputs"], token);
        using var document = JsonDocument.Parse(output);
        var result = new List<PlaybackStream>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("index", out var index)) continue;
            var id = index.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var props = item.TryGetProperty("properties", out var propertyElement) ? propertyElement : default;
            string Prop(string name) => props.ValueKind == JsonValueKind.Object &&
                props.TryGetProperty(name, out var prop) ? prop.ToString() : "";
            var name = Prop("application.name");
            if (string.IsNullOrWhiteSpace(name)) name = Prop("media.name");
            if (string.IsNullOrWhiteSpace(name)) name = "Unknown application";
            var sink = item.TryGetProperty("sink", out var sinkElement) ? sinkElement.ToString() : "";
            result.Add(new PlaybackStream(id, name, Prop("application.process.binary"), sink));
        }
        return result;
    }

    public async Task SetStreamVolumeAsync(string streamId, int percentage, CancellationToken token = default)
    {
        if (percentage is < 0 or > 150)
            throw new ArgumentOutOfRangeException(nameof(percentage), "Volume must be between 0 and 150 percent.");
        if (!(await GetStreamsAsync(token)).Any(s => s.Id == streamId))
            throw new InvalidOperationException("Playback stream has ended.");
        await PactlAsync(["set-sink-input-volume", streamId,
            percentage.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%"], token);
    }

    public async Task SetStreamMuteAsync(string streamId, bool muted, CancellationToken token = default)
    {
        if (!(await GetStreamsAsync(token)).Any(s => s.Id == streamId))
            throw new InvalidOperationException("Playback stream has ended.");
        await PactlAsync(["set-sink-input-mute", streamId, muted ? "1" : "0"], token);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetSinkIndicesAsync(CancellationToken token = default)
    {
        var output = await PactlAsync(["--format=json", "list", "sinks"], token);
        using var document = JsonDocument.Parse(output);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("name", out var name) &&
                item.TryGetProperty("index", out var index) &&
                name.ValueKind == JsonValueKind.String)
                result[name.GetString()!] = index.ToString();
        }
        return result;
    }

    public async Task MoveAsync(string streamId, string sinkId, CancellationToken token = default)
    {
        // Verify both against the audio server: never allow arbitrary CLI arguments from user input.
        var streams = await GetStreamsAsync(token);
        if (!streams.Any(s => s.Id == streamId))
            throw new InvalidOperationException("The application audio stream no longer exists. Refresh and try again.");
        var sinks = await PactlAsync(["--format=json", "list", "sinks"], token);
        using var doc = JsonDocument.Parse(sinks);
        if (!doc.RootElement.EnumerateArray().Any(s =>
            s.TryGetProperty("name", out var name) && name.GetString() == sinkId))
            throw new InvalidOperationException("The selected output sink is unavailable.");
        await PactlAsync(["move-sink-input", streamId, sinkId], token);
    }

    private static async Task<string> PactlAsync(IReadOnlyList<string> args, CancellationToken token)
    {
        var start = new ProcessStartInfo("pactl") { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start pactl.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException(error.Trim());
            return output;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException("The audio server did not respond within 10 seconds.");
        }
    }
}

public sealed record PlaybackStream(string Id, string AppName, string Executable, string SinkIndex)
{
    public string Display => $"{AppName}{(string.IsNullOrWhiteSpace(Executable) ? "" : $" ({Executable})")} • stream #{Id}";
}
