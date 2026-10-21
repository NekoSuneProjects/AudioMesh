# NekoSune AudioMesh

A cross-platform desktop audio routing manager (work in progress) for Windows and Linux.

## First milestone

- Avalonia desktop mixer showing detected recording and playback devices
- Named, persisted audio buses (Game, Chat, Media, Aux, Mic and custom buses)
- Map buses to available playback endpoints and control their volume / mute (where supported)
- Discover existing Windows audio endpoints, including third-party virtual cables
- Windows: discover active application audio sessions and change per-session volume/mute (does not move application outputs)
- Create/remove Linux virtual sinks with PipeWire's PulseAudio compatibility layer or PulseAudio (`pactl`)
- Save Linux virtual-sink names to profile and recreate missing sinks when the application opens
- Re-scan devices, preserve user profiles, and show backend errors
- Linux: enumerate active playback streams and move them to any available sink (physical or virtual), via pactl
- Linux: fan out AudioMesh virtual-sink monitors to multiple physical or external sinks using managed loopback modules; disconnect destinations independently
- Linux: save per-application routing rules and reapply them to new audio streams every 3 seconds while the app is open
- Linux: adjust volume (0–150%) and mute of a selected active playback stream independently of other streams
- Linux: manual moves (including moves made by another mixer) override automatic routing for the remaining lifetime of that playback stream, avoiding routing fights; updating a rule clears those overrides

**This is a foundation, not yet a replacement for Sonar or MIXLINE.** The first milestone does not mix arbitrary audio streams, intercept applications, provide low-latency routing graphs, or install a Windows virtual driver. A bus is currently a saved assignment to a device, not a DSP mixing channel. Linux virtual sinks are real OS audio endpoints; Windows virtual endpoints must currently be installed through third-party software.

## Requirements

- .NET 8 SDK or newer compatible SDK
- Windows 10/11 or Linux desktop
- Linux: PipeWire with `pipewire-pulse`, or PulseAudio, plus `pactl`
- Windows: installed audio endpoints; optional VB-CABLE for additional virtual endpoints

## Run

```sh
dotnet restore src/AudioMesh.Desktop/AudioMesh.Desktop.csproj
dotnet run --project src/AudioMesh.Desktop
```

On Linux, `pactl info` should succeed in the active desktop audio session. Custom sinks are created using `pactl load-module module-null-sink` and unloaded through `pactl unload-module`. These are session-scoped and may disappear after logging out or restarting the audio server. AudioMesh restores saved virtual-sink names on startup when a running audio server is available. It does not run in the background before login or provide a system service.

## Architecture

- `AudioMesh.Core`: buses, user settings and audio backend contracts
- `AudioMesh.Desktop`: Avalonia UI, platform backends and JSON profile storage
- Linux backend: pactl query/control and module-null-sink virtual sink management
- Windows backend: NAudio CoreAudio MMDevice enumeration and endpoint volume/mute

## Known limitations

- Windows custom virtual driver not implemented.
- Linux routing rules are saved to the JSON profile and reapplied while AudioMesh runs, but do not work when the application is closed. Route matching uses executable name where available; applications without identifying metadata cannot be saved. Windows still needs app-specific routing implementation; use OS sound settings meanwhile.
- Linux sink removal only unloads modules created by the current AudioMesh process. It deliberately will not unload arbitrary user modules.
- Monitor loopbacks use `module-loopback` (80 ms target latency). AudioMesh attempts to unload managed monitoring modules on exit; crashes or abrupt shutdown may leave loopbacks running until the audio server is restarted.
- Monitor connections support multiple external/physical destinations per AudioMesh virtual sink; feedback protection prevents chaining into another AudioMesh virtual sink.
- External device changes require clicking Refresh.
- The remove-last-cable command only removes modules created by the current AudioMesh process; saved sinks that survive a previous app exit are reused but cannot yet be removed from the UI after relaunch.
- Endpoint volume affects the actual device, not a private bus, and may also affect other applications sharing that endpoint.
- Microphone input devices are detected but are not processed or remixed.

## Roadmap

1. Routed audio graph with per-bus gain, metering, independent stream/monitor outputs and feedback detection
2. Persistent per-application capture and assignment (WASAPI application loopback on supported Windows versions; PipeWire native graph on Linux)
3. DSP effects, EQ, microphone processing and presets
4. Windows virtual audio driver based on SysVAD, with code signing and installation testing
5. Automated Windows/Linux CI, packages and end-to-end audio integration tests

Contributions welcome. Do not install unsigned kernel drivers or expose the local control API to the public network.
