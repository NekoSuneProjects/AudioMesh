using AudioMesh.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace AudioMesh.Desktop;

/// <summary>Read-only visualization of monitoring connections managed by AudioMesh.</summary>
public static class RoutingGraphRenderer
{
    public static void Render(Canvas canvas, IReadOnlyList<AudioDevice> devices,
        IEnumerable<(string Source, string Target)> edges, Action<AudioDevice>? onDeviceClicked = null)
    {
        canvas.Children.Clear();
        var virtualSources = devices
            .Where(d => d.Kind == AudioDeviceKind.Playback &&
                (d.IsVirtual || d.Id.StartsWith("audiomesh_", StringComparison.Ordinal)))
            .Take(12).ToArray();
        var outputs = devices.Where(d => d.Kind == AudioDeviceKind.Playback &&
                !d.IsVirtual && !d.Id.StartsWith("audiomesh_", StringComparison.Ordinal))
            .Take(12).ToArray();
        var rows = Math.Max(virtualSources.Length, outputs.Length);
        canvas.Height = Math.Max(120, 50 + 54 * rows);

        AddText(canvas, "VIRTUAL SOURCES", 18, 12, Brushes.LightSeaGreen);
        AddText(canvas, "PHYSICAL OUTPUTS", 310, 12, Brushes.LightSteelBlue);
        if (rows == 0)
        {
            AddText(canvas, "No suitable playback endpoints detected. Refresh devices.", 18, 58, Brushes.LightGray);
            return;
        }

        var sources = virtualSources.Select((device, index) => (device, index))
            .ToDictionary(item => item.device.Id, item => 42d + item.index * 54);
        var destinations = outputs.Select((device, index) => (device, index))
            .ToDictionary(item => item.device.Id, item => 42d + item.index * 54);

        foreach (var (source, target) in edges)
        {
            if (!sources.TryGetValue(source, out var sourceY) ||
                !destinations.TryGetValue(target, out var targetY)) continue;
            var connection = new Line
            {
                StartPoint = new Point(195, sourceY + 18),
                EndPoint = new Point(310, targetY + 18),
                Stroke = Brushes.MediumTurquoise,
                StrokeThickness = 2
            };
            canvas.Children.Add(connection);
        }

        foreach (var item in virtualSources.Select((d, i) => (d, i)))
            AddNode(canvas, item.d, 18, 42 + item.i * 54, Brushes.DarkSlateGray, Brushes.MediumTurquoise, onDeviceClicked);
        foreach (var item in outputs.Select((d, i) => (d, i)))
            AddNode(canvas, item.d, 310, 42 + item.i * 54, Brushes.DarkSlateBlue, Brushes.LightSteelBlue, onDeviceClicked);
    }

    private static void AddText(Canvas canvas, string text, double x, double y, IBrush color)
    {
        var label = new TextBlock { Text = text, Foreground = color, FontSize = 12 };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        canvas.Children.Add(label);
    }

    private static void AddNode(Canvas canvas, AudioDevice device, double x, double y,
        IBrush fill, IBrush border, Action<AudioDevice>? onClick)
    {
        var node = new Button
        {
            Width = 177, Height = 38, Padding = new Thickness(4),
            Background = fill, BorderBrush = border, BorderThickness = new Thickness(1),
            Content = new TextBlock
            {
                Text = device.Name, Foreground = Brushes.White, FontSize = 12,
                MaxWidth = 165, TextTrimming = TextTrimming.CharacterEllipsis
            }
        };
        if (onClick is not null) node.Click += (_, _) => onClick(device);
        Canvas.SetLeft(node, x);
        Canvas.SetTop(node, y);
        canvas.Children.Add(node);
    }
}
