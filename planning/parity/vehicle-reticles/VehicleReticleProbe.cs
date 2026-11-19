using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using VortexArena.Formats.Vfs;
using VortexArena.Game.Client;
using VortexArena.Game.Hud;
using VortexArena.Game.Loaders;
using VortexArena.Game.Menu;
using VortexArena.Game.Net;
using VortexArena.Net;
using Kind = VortexArena.Game.Hud.VehicleHud.VehicleHudKind;

public partial class VehicleReticleProbe : Node3D
{
    private const string Art = "gfx/vehicles/crosshair_";
    private const string SizeCvar = "cl_vehicles_crosshair_size";
    private static readonly Vector2I MainPoint = new(400, 300);
    private static readonly Vector2I DropPoint = new(400, 445);
    private static readonly Vector2I[] AuxPoints = { new(120, 110), new(285, 110), new(515, 110), new(680, 110) };
    private VehicleHud _hud = null!;
    private Camera3D _camera = null!;
    private Image _blank = null!;

    private static void Check(bool condition, string phase)
    {
        if (!condition)
            throw new InvalidOperationException(phase);
        GD.Print($"PASS {phase}");
    }

    private Vector3 At(Vector2I pixel) => _camera.ProjectPosition(pixel, 10f);

    private async Task<Image> Frame(string? filename = null)
    {
        _hud._Process(0);
        _hud.QueueRedraw();
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image image = GetViewport().GetTexture().GetImage();
        if (filename is not null && System.Environment.GetEnvironmentVariable("VEHICLE_CAPTURE_DIR") is { } dir)
        {
            if (image.SavePng(System.IO.Path.Combine(dir, filename + ".png")) != Error.Ok)
                throw new InvalidOperationException("Could not save " + filename);
        }
        return image;
    }

    private (int Count, int Width, Color Peak) Read(Image image, Vector2I center, int radius = 44)
    {
        int count = 0, minX = int.MaxValue, maxX = int.MinValue;
        Color peak = new(0, 0, 0, 1);
        for (int y = center.Y - radius; y < center.Y + radius; y++)
        for (int x = center.X - radius; x < center.X + radius; x++)
        {
            Color p = image.GetPixel(x, y);
            Color b = _blank.GetPixel(x, y);
            if (MathF.Abs(p.R - b.R) + MathF.Abs(p.G - b.G) + MathF.Abs(p.B - b.B) < 0.025f)
                continue;
            count++;
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            peak.R = MathF.Max(peak.R, p.R - b.R);
            peak.G = MathF.Max(peak.G, p.G - b.G);
            peak.B = MathF.Max(peak.B, p.B - b.B);
        }
        return (count, count == 0 ? 0 : maxX - minX + 1, peak);
    }

    public override async void _Ready()
    {
        try
        {
            using var vfs = new VirtualFileSystem();
            Check(vfs.MountContentRoot(ProjectSettings.GlobalizePath("res://data")), "mount shipped HUD art");
            var assets = new AssetLoader(vfs);
            TextureCache.VfsResolver = assets.LoadTexture;
            foreach (string suffix in new[] { "burst", "rain", "guide", "heal", "drop", "lock", "hint" })
                Check(TextureCache.Get(Art + suffix) is not null, $"load {suffix} texture");

            _camera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Size = 16f };
            AddChild(_camera);
            _camera.MakeCurrent();
            var layer = new CanvasLayer();
            AddChild(layer);
            layer.AddChild(new ColorRect { Color = new Color(0.24f, 0.24f, 0.24f), Size = new Vector2(800, 600) });
            var crosshair = new CrosshairPanel { Visible = false };
            layer.AddChild(crosshair);
            _hud = new VehicleHud { CrosshairColors = crosshair };
            layer.AddChild(_hud);
            _hud.SetPhysicsProcess(false);
            _hud.SetProcess(false);
            _hud.Configure(new Rect2(250, 560, 300, 40));
            _hud.Exit();
            _blank = await Frame();

            var cvars = MenuState.Cvars;
            cvars.Set("cl_vehicles_crosshair_colorize", "0");
            cvars.Set("crosshair_alpha", "0.8");
            cvars.Set("cl_vehicles_alarm", "0");
            var remoteFeed = typeof(NetGame).GetMethod("FeedRemoteReticle", BindingFlags.NonPublic | BindingFlags.Static)!;
            foreach (var (kind, id, mode, art) in new[]
            {
                (Kind.Raptor, (byte)2, (byte)1, "burst"),
                (Kind.Raptor, (byte)2, (byte)2, "rain"),
                (Kind.Spiderbot, (byte)3, (byte)1, "burst"),
                (Kind.Spiderbot, (byte)3, (byte)2, "guide"),
                (Kind.Spiderbot, (byte)3, (byte)3, "rain"),
                (Kind.Bumblebee, (byte)4, (byte)0, "heal"),
            })
            {
                _hud.ConfigureForVehicle(kind);
                remoteFeed.Invoke(null, new object[] { _hud, new VehicleViewState { VehKind = id, W2Mode = mode } });
                Image artFrame = await Frame(mode == 2 && kind == Kind.Spiderbot ? "spiderbot-guide"
                    : kind == Kind.Bumblebee ? "bumblebee-heal" : null);
                Check(_hud.MainReticle == Art + art && Read(artFrame, MainPoint).Count > 10,
                    $"{kind} mode {mode} draws {art} at screen center");
            }

            object seat = new();
            _hud.ConfigureForVehicle(Kind.Raptor, seat);
            _hud.MainReticle = Art + "burst";
            _hud.SetAuxiliaryXhairLock(0, At(AuxPoints[0]), 0.25f);
            _hud.SetAuxiliaryXhairLock(1, At(AuxPoints[1]), 0.75f);
            _hud.SetAuxiliaryXhairLock(2, At(AuxPoints[2]), 1f);
            _hud.SetAuxiliaryXhair(3, At(AuxPoints[3]), new Color(1, 0, 1, 0.5f), Art + "burst");
            _hud.FeedBombDropmark(At(DropPoint), 12);
            Image standard = await Frame("default-all-paths");
            var main = Read(standard, MainPoint);
            var drop = Read(standard, DropPoint, 78);
            var aux = AuxPoints.Select(p => Read(standard, p)).ToArray();
            Check(main.Count > 10 && drop.Count > 10 && aux.All(region => region.Count > 10),
                "default 0.5 draws main, dropmark and auxiliary slots 0 through 3");
            Check(aux[0].Peak.B > 0.4f && aux[1].Peak.G > 0.4f && aux[2].Peak.R > 0.4f
                && aux[0].Peak.R < 0.01f && aux[1].Peak.B < 0.01f && aux[2].Peak.G < 0.01f,
                "Racer lock colors are blue, green, and red");
            Check(drop.Peak.G > 0.6f && drop.Peak.R < 0.01f, "green live dropmark blends additively over gray");
            _hud.SetAuxiliaryXhair(3, At(AuxPoints[3]), new Color(1, 0, 1), Art + "burst");
            float opaquePeak = Read(await Frame(), AuxPoints[3]).Peak.R;
            Check(opaquePeak > 0.1f && MathF.Abs(aux[3].Peak.R / opaquePeak - 0.5f) < 0.06f,
                "auxiliary marker alpha multiplies crosshair alpha");
            _hud.SetAuxiliaryXhair(3, At(AuxPoints[3]), new Color(1, 0, 1, 0.5f), Art + "burst");

            cvars.Set("crosshair_alpha", "0.35");
            Image dim = await Frame();
            Check(Read(dim, MainPoint).Peak.R < main.Peak.R * 0.6f
                && Read(dim, AuxPoints[2]).Peak.R < aux[2].Peak.R * 0.6f
                && Read(dim, DropPoint, 78).Peak.G < drop.Peak.G * 0.8f, "live crosshair alpha dims all three paths");
            cvars.Set("crosshair_alpha", "0");
            Image transparent = await Frame();
            Check(Read(transparent, MainPoint).Count == 0 && Read(transparent, DropPoint, 78).Count == 0
                && AuxPoints.All(p => Read(transparent, p).Count == 0), "zero alpha hides all three paths");
            cvars.Set("crosshair_alpha", "0.8");
            cvars.Set(SizeCvar, "0");
            Image zero = await Frame("zero-size");
            Check(Read(zero, MainPoint).Count == 0 && Read(zero, DropPoint, 78).Count == 0
                && AuxPoints.All(p => Read(zero, p).Count == 0), "explicit zero size hides all three paths");
            cvars.Set(SizeCvar, "1");
            Image large = await Frame("size-one");
            Check(Read(large, MainPoint, 55).Width > main.Width * 1.5f
                && Read(large, DropPoint, 78).Width > drop.Width * 1.5f
                && Read(large, AuxPoints[2]).Width > aux[2].Width * 1.5f, "nondefault size scales all three paths");
            cvars.Set(SizeCvar, "0.5");

            cvars.Set("cl_vehicles_crosshair_colorize", "1");
            cvars.Set("crosshair_color_special", "1");
            cvars.Set("crosshair_color", "0 1 0");
            var tinted = Read(await Frame("configured-main-color"), MainPoint);
            Check(tinted.Peak.G >= main.Peak.G - 0.02f && tinted.Peak.R < 0.01f && tinted.Peak.B < 0.01f,
                $"vehicle main uses configured crosshair color ({tinted.Peak})");
            cvars.Set("cl_vehicles_crosshair_colorize", "0");

            _hud._Process(0.07);
            _hud.SetAuxiliaryXhair(3, At(AuxPoints[3]), new Color(1, 0, 1, 0.5f), Art + "burst");
            _hud.SetAuxiliaryXhairLock(0, At(AuxPoints[0]), 0.5f);
            ClientRenderTime.Scale = 0;
            _hud._Process(2);
            Check(Read(await Frame(), AuxPoints[0]).Count == aux[0].Count, "paused client time does not age markers");
            ClientRenderTime.Scale = 0.5f;
            _hud._Process(0.04);
            var justBefore = Read(await Frame(), AuxPoints[0]);
            Check(justBefore.Peak.B == aux[0].Peak.B, "marker holds full alpha until expiry under slowmo");
            _hud._Process(0.04);
            ClientRenderTime.Scale = 1;
            Image expired = await Frame("aux-expired");
            Check(AuxPoints.Take(3).All(p => Read(expired, p).Count == 0)
                && Read(expired, AuxPoints[3]).Count == aux[3].Count, "only refreshed slot 3 survives the 0.1 simulation-second deadline");

            _hud.FeedBombDropmark(At(DropPoint), 16);
            _hud.FeedBombDropmark(null, 20.9);
            Image linger = await Frame("red-linger");
            var red = Read(linger, DropPoint, 78);
            Check(red.Peak.R > 0.6f && red.Peak.G < 0.01f
                && red.Width > drop.Width * 1.15f, "released bomb marker is red and grows by 25 percent");
            _hud.FeedBombDropmark(null, 20.9);
            Check(Read(await Frame(), DropPoint, 78).Count == red.Count, "unchanged simulation time holds the bomb marker");
            _hud.FeedBombDropmark(null, 21);
            Check(Read(await Frame(), DropPoint, 78).Count == 0, "bomb marker expires at five simulation seconds");

            _hud.FeedBombDropmark(At(DropPoint), 22);
            _hud.ScoreboardActive = true;
            Check(Read(await Frame(), MainPoint).Count == 0 && Read(await Frame(), DropPoint, 78).Count == 0,
                "scoreboard suppresses reticles");
            _hud.ScoreboardActive = false;

            _hud.ConfigureForVehicle(Kind.Raptor, seat);
            Check(Read(await Frame(), DropPoint, 78).Count > 10, "same seat refresh preserves reticle state");
            _hud.ConfigureForVehicle(Kind.Raptor, new object());
            _hud.FeedBombDropmark(null, 22.1);
            Image changed = await Frame();
            Check(Read(changed, MainPoint).Count == 0 && Read(changed, DropPoint, 78).Count == 0
                && AuxPoints.All(p => Read(changed, p).Count == 0), "switching seats clears stale markers even within one vehicle kind");
            _hud.SetAuxiliaryXhair(3, At(AuxPoints[3]), Colors.White);
            _hud.ConfigureForVehicle(Kind.BumblebeeGun);
            Check(Read(await Frame(), AuxPoints[3]).Count == 0, "changing vehicle kind clears the prior aux slot");
            _hud.SetAuxiliaryXhair(3, At(AuxPoints[3]), Colors.White);
            _hud.Exit();
            Image onFoot = await Frame("exit");
            Check(onFoot.GetData().SequenceEqual(_blank.GetData()), "exit restores the clean viewport");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
    }
}
