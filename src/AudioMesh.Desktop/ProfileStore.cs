using System.Text.Json;
using AudioMesh.Core;

namespace AudioMesh.Desktop;

public sealed class ProfileStore
{
    private readonly string _file = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioMesh", "profile.json");

    public async Task<AudioProfile> LoadAsync()
    {
        if (!File.Exists(_file)) return AudioProfile.Default();
        var profile = JsonSerializer.Deserialize<AudioProfile>(await File.ReadAllTextAsync(_file));
        return profile ?? AudioProfile.Default();
    }

    public async Task SaveAsync(AudioProfile profile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var tmp = _file + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _file, true);
    }
}
