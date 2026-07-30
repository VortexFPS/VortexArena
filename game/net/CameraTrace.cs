using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Godot;
using VortexArena.Common.Diagnostics;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Net;

/// <summary>
/// In-engine camera-trace capture (apparatus tier A2): drive the live <see cref="NetGame"/> with a SCRIPTED
/// per-tick input sequence from a fixed spawn and record the RENDERED camera origin + the predicted player origin
/// per frame to a JSON file, for offline drift/departure analysis and for diffing against the Base-engine golden
/// capture (tier A3). This is the "same on VortexArena, with controlled start locations" the task asked for.
///
/// <para>Activated by <c>--camera-trace &lt;scenario.json&gt; &lt;out.json&gt;</c> (parsed in <c>Main</c>, which sets
/// the boot map from the scenario). Determinism: run with <c>--fixed-fps 72</c> and <c>cl_movement_perframe 0</c>
/// (fixed 1/72 s tics, one input drained per frame) so frame i ↔ scripted input i. Headless renders blank but the
/// camera transform + predicted accessors don't need the GPU, so <c>--headless</c> works. Static (one capture per
/// process) by design — the boot wiring is a single global, like the SDF baker / screenshot hook.</para>
/// </summary>
public static class CameraTrace
{
    public static bool Active { get; private set; }
    public static string Map { get; private set; } = "";
    public static string Gametype { get; private set; } = "dm";
    /// <summary>Bot count from the scenario (default 0) — set &gt;0 to capture the bot-join transition.</summary>
    public static int Bots { get; private set; }
    private static string _outPath = "";

    // One scripted input frame (normalized ±1 move axes + Quake view angles (deg) + button bits).
    public readonly record struct InputSpec(float Forward, float Side, float Up, NVec3 ViewAngles, int Buttons);
    private static InputSpec[] _inputs = Array.Empty<InputSpec>();
    private static int _cursor;

    // Optional cvar overrides from the scenario (e.g. cl_movement_smoothing_faithful 0 to capture the port path),
    // applied once to the live store on the first recorded frame (Api.Services is up by then).
    private static readonly Dictionary<string, string> _cvars = new();
    private static bool _cvarsApplied;

    // Recorded per-frame samples (Quake space, directly comparable to the Base golden trace).
    private readonly record struct Frame(
        int Tick, float Time, float Dt,
        NVec3 PhysicsOrigin, NVec3 ViewOrigin, NVec3 Velocity, bool OnGround, float ViewOfsZ, float ReconcileError);
    private static readonly List<Frame> _frames = new();
    private static bool _finished;

    /// <summary>True once every scripted input has been consumed (the capture should finish next record).</summary>
    public static bool Done => _cursor >= _inputs.Length;

    /// <summary>
    /// Parse the scenario and arm the capture. Returns false (and leaves <see cref="Active"/> off) if the file is
    /// missing/invalid. Scenario JSON: <c>{ "map":"&lt;vpath&gt;", "gametype":"dm", "frames":600,
    /// "input":{"f":0,"s":0,"u":0,"yaw":0,"pitch":0,"buttons":0}, "inputs":[ {..}, .. ] }</c> — <c>inputs</c> (an
    /// explicit per-frame list) wins; otherwise the single <c>input</c> (default all-zero = stand still) repeats
    /// for <c>frames</c> frames.
    /// </summary>
    public static bool Configure(string scenarioPath, string outPath)
    {
        try
        {
            string json = System.IO.File.ReadAllText(scenarioPath);
            using var doc = JsonDocument.Parse(json);
            JsonElement r = doc.RootElement;

            Map = r.TryGetProperty("map", out var m) ? m.GetString() ?? "" : "";
            Gametype = r.TryGetProperty("gametype", out var g) ? g.GetString() ?? "dm" : "dm";
            Bots = r.TryGetProperty("bots", out var b) ? b.GetInt32() : 0;
            _outPath = outPath;

            var list = new List<InputSpec>();
            if (r.TryGetProperty("inputs", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in arr.EnumerateArray())
                    list.Add(ParseSpec(e));
            }
            else
            {
                int frames = r.TryGetProperty("frames", out var fr) ? fr.GetInt32() : 600;
                InputSpec one = r.TryGetProperty("input", out var inp) ? ParseSpec(inp) : default;
                for (int i = 0; i < frames; i++) list.Add(one);
            }
            _cvars.Clear();
            if (r.TryGetProperty("cvars", out var cv) && cv.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty p in cv.EnumerateObject())
                    _cvars[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString();

            _inputs = list.ToArray();
            _cursor = 0;
            _cvarsApplied = false;
            _frames.Clear();
            _finished = false;
            Active = _inputs.Length > 0;
            Log.Info($"[camera-trace] armed: map='{Map}' gametype='{Gametype}' frames={_inputs.Length} -> {outPath}");
            return Active;
        }
        catch (Exception ex)
        {
            Log.Severe($"[camera-trace] failed to load scenario '{scenarioPath}': {ex.Message}");
            Active = false;
            return false;
        }
    }

    private static InputSpec ParseSpec(JsonElement e)
    {
        float F(string k) => e.TryGetProperty(k, out var v) ? (float)v.GetDouble() : 0f;
        int I(string k) => e.TryGetProperty(k, out var v) ? v.GetInt32() : 0;
        // ViewAngles: Quake (pitch=X, yaw=Y, roll=Z); scenario gives yaw/pitch/roll.
        return new InputSpec(F("f"), F("s"), F("u"), new NVec3(F("pitch"), F("yaw"), F("roll")), I("buttons"));
    }

    /// <summary>Consume the next scripted input (called from <see cref="NetGame.SampleInput"/> when armed + spawned).
    /// Returns false once the script is exhausted (the caller then falls back to real/idle input).</summary>
    public static bool TryNextInput(out InputSpec spec)
    {
        if (_cursor < _inputs.Length) { spec = _inputs[_cursor++]; return true; }
        spec = default;
        return false;
    }

    /// <summary>Record one rendered frame (Quake space). <paramref name="viewOriginGodot"/> is the camera world
    /// position; the rest come from the predicted local state. The tick is the running frame index.</summary>
    public static void RecordFrame(float time, float dt,
        NVec3 physicsOrigin, Godot.Vector3 viewOriginGodot, NVec3 velocity, bool onGround, float viewOfsZ,
        float reconcileError = 0f)
    {
        if (!Active || _finished) return;
        if (!_cvarsApplied)
        {
            _cvarsApplied = true;
            foreach (var kv in _cvars)
            {
                VortexArena.Common.Services.Api.Cvars.Set(kv.Key, kv.Value);
                Log.Info($"[camera-trace] cvar {kv.Key} = {kv.Value}");
            }
        }
        _frames.Add(new Frame(_frames.Count, time, dt, physicsOrigin,
            VortexArena.Game.Coords.ToQuake(viewOriginGodot), velocity, onGround, viewOfsZ, reconcileError));
    }

    /// <summary>Write the trace to the out path and quit the process. Idempotent.</summary>
    public static void Finish(SceneTree tree)
    {
        if (_finished) return;
        _finished = true;
        try
        {
            var sb = new StringBuilder(_frames.Count * 96 + 64);
            sb.Append("{\n  \"map\": ").Append(JsonEncode(Map)).Append(",\n  \"frames\": [\n");
            for (int i = 0; i < _frames.Count; i++)
            {
                Frame f = _frames[i];
                sb.Append("    {\"tick\":").Append(f.Tick)
                  .Append(",\"time\":").Append(Num(f.Time))
                  .Append(",\"dt\":").Append(Num(f.Dt))
                  .Append(",\"physicsOrigin\":").Append(Vec(f.PhysicsOrigin))
                  .Append(",\"viewOrigin\":").Append(Vec(f.ViewOrigin))
                  .Append(",\"velocity\":").Append(Vec(f.Velocity))
                  .Append(",\"onground\":").Append(f.OnGround ? 1 : 0)
                  .Append(",\"viewOfsZ\":").Append(Num(f.ViewOfsZ))
                  .Append(",\"reconcileError\":").Append(Num(f.ReconcileError))
                  .Append('}');
                sb.Append(i + 1 < _frames.Count ? ",\n" : "\n");
            }
            sb.Append("  ]\n}\n");
            System.IO.File.WriteAllText(_outPath, sb.ToString());
            Log.Info($"[camera-trace] wrote {_frames.Count} frames -> {_outPath}");
        }
        catch (Exception ex)
        {
            Log.Severe($"[camera-trace] failed to write '{_outPath}': {ex.Message}");
        }
        tree.Quit();
    }

    private static string JsonEncode(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    private static string Num(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    private static string Vec(NVec3 v) => "[" + Num(v.X) + "," + Num(v.Y) + "," + Num(v.Z) + "]";
}
