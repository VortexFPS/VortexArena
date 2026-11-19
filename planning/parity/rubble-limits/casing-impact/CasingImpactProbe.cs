using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using VortexArena.Engine.Collision;
using VortexArena.Formats.Vfs;
using VortexArena.Game.Audio;
using VortexArena.Game.Loaders;
using VortexArena.Game.Menu;
using Vec = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

public sealed partial class CasingImpactProbe : Node3D
{
    private ShellCasings _casings = null!;
    private readonly string _output = System.Environment.GetEnvironmentVariable("CASING_OUTPUT")!;

    public override async void _Ready()
    {
        try
        {
            WorldTint.EnsureRegistered();
            using var vfs = new VirtualFileSystem();
            if (!vfs.MountContentRoot(ProjectSettings.GlobalizePath("res://data")))
                throw new InvalidOperationException("Asset mount failed");
            var assets = new AssetSystem(vfs);
            var cv = MenuState.Cvars;
            string mode = System.Environment.GetEnvironmentVariable("CASING_PARTICLES") ?? "0";
            cv.Set("cl_particles_modern", mode);
            cv.Set("r_darkplaces_colour", System.Environment.GetEnvironmentVariable("CASING_COLOUR") ?? "1");
            NativeColour.ApplyAtBoot(assets);
            var loader = new AssetLoader(vfs, assets);
            cv.Set("volume", "1");
            cv.Set("mastervolume", "1");
            cv.Set("snd_spatialization_occlusion", "0");
            cv.Set("snd_speed", "44100");
            DpNative.Configure(cv);
            var render = new ClientWorld { Assets = assets, AudioLoader = loader.LoadSound };
            AddChild(render);
            var floor = new CollisionWorld();
            floor.AddBrush(Brush.FromBox(new Vec(-100, -100, -20), new Vec(100, 100, 0), SuperContents.Solid));
            floor.BuildGrid();
            render.Effects.SetCollisionWorld(floor);
            render.Effects.ModelLoader = path => loader.LoadModel(path);
            _casings = render.Effects.Casings;
            _casings.SetProcess(false);
            var play = _casings.SoundHook!;
            bool shellMixed = false;
            bool brassMixed = false;
            _casings.SoundHook = (sample, origin) =>
            {
                long previous = DpAudio.Instance.Sound.LastStartTicks;
                play(sample, origin);
                bool mixed = DpAudio.Instance.Sound.LastStartTicks != previous;
                if (sample.StartsWith("weapons/casings", StringComparison.Ordinal)) shellMixed |= mixed;
                if (sample.StartsWith("weapons/brass", StringComparison.Ordinal)) brassMixed |= mixed;
                GD.Print($"IMPACT {sample} origin={origin} mixed={mixed}");
            };
            var camera = new Camera3D { Position = new Vector3(0, 35, 23), Projection = Camera3D.ProjectionType.Orthogonal, Size = 26 };
            AddChild(camera);
            camera.LookAt(new Vector3(0, 0, 0));
            camera.MakeCurrent();
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-55, -20, 0) });
            var plane = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(60, 60),
                Material = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.3f, 0.3f) } } };
            AddChild(plane);

            cv.Set("cl_casings_shell_time", "3");
            cv.Set("cl_casings_bronze_time", "4");
            await Shot("CASING_SHELL", -5);
            await Shot("CASING_BULLET", 5);
            for (int i = 0; i < 68; i++)
                await Step();
            Image opaque = await Capture("opaque");
            for (int i = 0; i < 42; i++)
                await Step();
            Image shellFade = await Capture("shell-fade");
            for (int i = 0; i < 80; i++)
                await Step();
            Image brassFade = await Capture("brass-fade");
            for (int i = 0; i < 45; i++)
                await Step();
            Image expired = await Capture("expired");
            CheckContrast(opaque, shellFade, expired, left: true);
            CheckContrast(opaque, brassFade, expired, left: false);

            Check(shellMixed && brassMixed, $"both impact families reached the mixer in particle mode {mode}");

            cv.Set("cl_casings_shell_time", "1");
            _casings.Spawn(new Vec(0, 0, 900), default, ShellCasings.CasingKind.Shell);
            cv.Set("cl_casings_shell_time", "30");
            _casings._Process(0.995);
            Check(VisibleCasings() == 1, "casing retains the lifetime set at spawn");
            _casings._Process(0.004);
            Check(VisibleCasings() == 0, "casing retires below the stock 0.003 alpha threshold");

            int sounds = 0;
            int sweeps = 0;
            _casings.SoundHook = (_, _) => sounds++;
            _casings.TraceHook = (from, to) =>
            {
                int sweep = ++sweeps;
                return sweep is 1 or 11
                    ? new ShellCasings.CasingTrace(0.99f, from + (to - from) * 0.99f,
                        new Vec(-MathF.Sign(to.X - from.X), 0, 0), false)
                    : new ShellCasings.CasingTrace(1, to, default, false);
            };
            _casings.Spawn(new Vec(0, 0, 24), new Vec(1600, 0, 0));
            _casings._Process(0.65);
            if (sweeps < 11 || sounds != 1)
                throw new InvalidOperationException($"catch-up reaches both contacts, one sound expected, got {sweeps} sweeps and {sounds} sounds");
            GD.Print($"PASS catch-up casing throttled within one draw ({sweeps} sweeps, {sounds} sound)");
            GetTree().Quit();

            async Task Shot(string effect, float x)
            {
                render.Effects.Spawn(effect, new Vec(x, 0, 24), new Vec(0, 0, -90));
                for (int i = 0; i < 20; i++)
                    await Step();
            }
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
    }

    private int VisibleCasings() => _casings.GetChildren().OfType<MultiMeshInstance3D>()
        .Sum(batch => batch.Multimesh.VisibleInstanceCount);

    private static void Check(bool pass, string label)
    {
        if (!pass) throw new InvalidOperationException(label);
        GD.Print($"PASS {label}");
    }

    private async Task Step()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        _casings._Process(1.0 / 60);
    }

    private async Task<Image> Capture(string name)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        foreach (var batch in _casings.GetChildren().OfType<MultiMeshInstance3D>())
        {
            var mesh = batch.Multimesh;
            string material = (batch.MaterialOverride ?? mesh.Mesh.SurfaceGetMaterial(0))?.GetClass() ?? "(none)";
            string pose = mesh.VisibleInstanceCount > 0 ? mesh.GetInstanceTransform(0).Origin.ToString() : "-";
            string alpha = mesh.UseColors && mesh.VisibleInstanceCount > 0 ? mesh.GetInstanceColor(0).A.ToString() : "-";
            GD.Print($"VIEW {name} {batch.Name} visible={mesh.VisibleInstanceCount} material={material} pose={pose} alpha={alpha}");
        }
        Image image = GetViewport().GetTexture().GetImage();
        image.SavePng($"{_output}/{name}.png");
        return image;
    }

    private static void CheckContrast(Image full, Image fade, Image empty, bool left)
    {
        double fullContrast = 0;
        double fadeContrast = 0;
        int width = empty.GetWidth();
        int height = empty.GetHeight();
        for (int y = 0; y < height; y++)
        for (int x = left ? 0 : width / 2; x < (left ? width / 2 : width); x++)
        {
            Color background = empty.GetPixel(x, y);
            fullContrast += Contrast(full.GetPixel(x, y), background);
            fadeContrast += Contrast(fade.GetPixel(x, y), background);
        }
        if (fadeContrast < 0.1 * fullContrast || fadeContrast > 0.85 * fullContrast || fullContrast < 1)
            throw new InvalidOperationException($"expected partial pixel coverage, full={fullContrast:0.0} fade={fadeContrast:0.0}");
        GD.Print($"PASS {(left ? "shell" : "brass")} rendered partial opacity, background delta full={fullContrast:0.0} fade={fadeContrast:0.0}");
    }

    private static float Contrast(Color a, Color b)
        => MathF.Abs(a.R - b.R) + MathF.Abs(a.G - b.G) + MathF.Abs(a.B - b.B);
}
