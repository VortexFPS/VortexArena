using Godot;
using VortexArena.Game.Menu;
using VortexArena.Game.Text;
using VortexArena.Server;

namespace VortexArena.Game.Client;

/// <summary>Draws the listen host's local diagnostics like stock screen-projected debug text.</summary>
public sealed partial class LocalDebugLabelLayer : Control
{
    private const float CellSize = 11f;
    public LocalDebugLabels Labels { get; } = new();
    public Camera3D Camera { get; init; } = null!;

    private bool _hadLabels;
    private double _now;

    public void Refresh(double now)
    {
        _now = now;
        Labels.Expire(now);
        bool hasLabels = Labels.Labels.Count != 0;
        if (hasLabels || _hadLabels)
            QueueRedraw();
        _hadLabels = hasLabels;
    }

    public override void _Draw()
    {
        foreach (LocalDebugLabels.Label label in Labels.Labels)
        {
            if (!CrosshairTrace.ProjectToScreen(Camera, label.Position, out Vector2 position))
                continue;

            Color tint = new(1f, 1f, 1f, label.Alpha(_now));
            foreach (string line in label.Text.Split('\n'))
            {
                float x = position.X;
                bool first = true;
                foreach ((string run, Color color) in MenuColorCodes.Segments(line, tint))
                {
                    x += DpText.Draw(this, DpText.Hud, new Vector2(x, position.Y), run, CellSize, color, snapX: first);
                    first = false;
                }
                position.Y += 1.25f * CellSize;
            }
        }
    }
}
