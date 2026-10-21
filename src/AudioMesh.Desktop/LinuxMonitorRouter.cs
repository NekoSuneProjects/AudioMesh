using System.Diagnostics;
using System.Text.Json;

namespace AudioMesh.Desktop;

/// <summary>Manages loopbacks created by this instance only; will never unload unrelated modules.</summary>
public sealed class LinuxMonitorRouter
{
    private readonly Dictionary<string, Dictionary<string, string>> _moduleBySource = new(StringComparer.Ordinal);

    public async Task ConnectAsync(string virtualSink, string destinationSink, CancellationToken token = default)
    {
        var sinks = await GetSinkNamesAsync(token);
        if (!sinks.Contains(virtualSink) || !sinks.Contains(destinationSink))
            throw new InvalidOperationException("Both audio outputs must exist. Refresh the device list.");
        if (!virtualSink.StartsWith("audiomesh_", StringComparison.Ordinal))
            throw new InvalidOperationException("Monitoring is restricted to AudioMesh virtual sinks.");
        if (virtualSink == destinationSink)
            throw new InvalidOperationException("A sink cannot monitor itself.");
        if (destinationSink.EndsWith(".monitor", StringComparison.Ordinal))
            throw new InvalidOperationException("Select a playback sink, not a monitor source.");
        if (destinationSink.StartsWith("audiomesh_", StringComparison.Ordinal))
            throw new InvalidOperationException("Select physical output or an external sink to prevent virtual-sink feedback loops.");

        // Each distinct destination gets its own loopback. Duplicate connections are not loaded.
        if (_moduleBySource.TryGetValue(virtualSink, out var existing) && existing.ContainsKey(destinationSink)) return;
        var module = (await RunAsync(["load-module", "module-loopback",
            "source=" + virtualSink + ".monitor", "sink=" + destinationSink,
            "latency_msec=80"], token)).Trim();
        if (!uint.TryParse(module, out _))
            throw new InvalidOperationException("Audio server did not return a module identifier: " + module);
        if (!_moduleBySource.TryGetValue(virtualSink, out var destinations))
            _moduleBySource[virtualSink] = destinations = new Dictionary<string, string>(StringComparer.Ordinal);
        destinations[destinationSink] = module;
    }

    public async Task DisconnectDestinationAsync(string virtualSink, string destinationSink, CancellationToken token = default)
    {
        if (!_moduleBySource.TryGetValue(virtualSink, out var destinations) ||
            !destinations.TryGetValue(destinationSink, out var module)) return;
        await RunAsync(["unload-module", module], token);
        destinations.Remove(destinationSink);
        if (destinations.Count == 0) _moduleBySource.Remove(virtualSink);
    }

    public async Task DisconnectAsync(string virtualSink, CancellationToken token = default)
    {
        if (!_moduleBySource.TryGetValue(virtualSink, out var destinations)) return;
        var failed = new List<Exception>();
        foreach (var entry in destinations.ToArray())
        {
            try
            {
                await RunAsync(["unload-module", entry.Value], token);
                destinations.Remove(entry.Key);
            }
            catch (Exception ex) { failed.Add(ex); }
        }
        if (destinations.Count == 0) _moduleBySource.Remove(virtualSink);
        if (failed.Count != 0) throw new AggregateException("Could not disconnect every monitor.", failed);
    }

    public bool IsConnected(string virtualSink) =>
        _moduleBySource.TryGetValue(virtualSink, out var destinations) && destinations.Count > 0;

    public IReadOnlyList<string> GetDestinations(string virtualSink) =>
        _moduleBySource.TryGetValue(virtualSink, out var destinations) ?
            destinations.Keys.ToArray() : [];

    /// <summary>Unload only loopbacks created by this instance before the window closes.</summary>
    public async Task DisconnectAllAsync(CancellationToken token = default)
    {
        var failures = new List<Exception>();
        foreach (var source in _moduleBySource.Keys.ToArray())
        {
            try { await DisconnectAsync(source, token); }
            catch (Exception ex) { failures.Add(ex); }
        }
        if (failures.Count > 0)
            throw new AggregateException("Some monitor loopbacks could not be removed.", failures);
    }

    private static async Task<HashSet<string>> GetSinkNamesAsync(CancellationToken token)
    {
        var output = await RunAsync(["--format=json", "list", "sinks"], token);
        using var doc = JsonDocument.Parse(output);
        return doc.RootElement.EnumerateArray()
            .Where(x => x.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
            .Select(x => x.GetProperty("name").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken token)
    {
        var psi = new ProcessStartInfo("pactl")
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Cannot launch pactl.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var text = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException("pactl: " + error.Trim());
            return text;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException("Audio server timed out.");
        }
    }
}
