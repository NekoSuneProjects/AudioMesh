# AudioMesh end-to-end audio setup and test guide

These steps are for the **experimental** desktop builds. GitHub Actions tests that code builds, not whether audio reaches your hardware with acceptable latency.

## Windows 10/11: existing virtual cables

1. Install a trusted virtual audio cable driver (such as VB-CABLE), following its signed-driver installer instructions, and reboot if requested.
2. Run AudioMesh and click **Refresh devices**. Confirm the cable appears as a virtual playback endpoint, and your headphones as a physical playback endpoint.
3. Under **Windows Virtual Cable Monitoring**, click **Open Windows per-app output settings**. With Discord (or Firefox) producing sound, choose the virtual cable's **playback** endpoint for the application's output.
4. Back in AudioMesh, choose that virtual playback cable as the monitor source and your headphones as the target. Click **Connect**.
5. Play audio at low volume. Check the application is audible only via the intended path. Test mute and per-application volume in the Windows Application Mixer.
6. In OBS, use the cable's corresponding capture/recording endpoint as an Audio Input Capture source where appropriate. Avoid capturing the same content twice (desktop audio + virtual cable).
7. Click **Disconnect selected** or **Disconnect all from source** before changing devices. Verify audio stops through the monitor destination.

**Limitations:** AudioMesh does not install Windows virtual drivers or automatically assign apps to playback devices yet. Loopback monitoring captures the *whole* virtual endpoint, not individual processes. Formats and latencies vary by audio device; some source/output combinations may not initialize. Do not route monitoring output back into the same cable.

## Linux: PipeWire/PulseAudio

1. Confirm `pactl info` succeeds in your logged-in graphical desktop session.
2. Run AudioMesh and create a virtual cable named `Chat`. It appears as `audiomesh_Chat` playback sink and an associated monitor source.
3. Start an app producing sound, click **Refresh application streams**, select the stream and route it to `audiomesh_Chat`.
4. Choose **Virtual Cable Monitoring**: source `audiomesh_Chat`, destination your headphones, then click **Connect monitor**.
5. Use **Save automatic rule** so the stream gets reassigned if the app creates a fresh audio stream. Test with the application closed and reopened.
6. Set the current stream volume and mute from its independent controls. OBS can capture `audiomesh_Chat.monitor` via the corresponding PulseAudio/PipeWire monitor source.
7. For more outputs, connect the same source to a second destination. Remove destinations individually; disconnect all outputs if finished.
8. Restart the audio service or relaunch AudioMesh and use **Refresh devices** to verify saved virtual cables can be restored.
9. Use **Remove selected saved cable** to remove the sink and profile entry.

**Limitations:** Rules are applied while AudioMesh runs, every 3 seconds, and matching is based on process executable/name. The virtual sink exists in the user audio session and may be recreated when AudioMesh launches. Per-stream volume applies to the current stream, whereas bus endpoint volume may affect every app using a sink.

## Bug-report checklist

Please collect the platform, OS version, audio server/driver, AudioMesh commit SHA, source and destination device names, exact reproduction steps, expected audio vs actual audio, and error shown in the AudioMesh status area. On Linux include `pactl info`, `pactl list short sinks`, and `pactl list short sink-inputs` after removing any private device labels. On Windows include only non-sensitive device names and the application settings; do not share personal audio recordings without consent.

**Audio safety:** Start volume low when enabling monitoring. Unexpected routing loops can create sudden loud feedback. Disconnect a monitor immediately if feedback or distortion occurs.
