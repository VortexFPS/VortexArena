using System;
using System.Collections.Concurrent;
using System.Threading;
using Godot;

namespace XonoticGodot.Game.Net;

/// <summary>
/// DS-2: the dedicated server's interactive stdin console — the C# successor to DP's dedicated-host terminal
/// reader (<c>Sys_ConsoleInput</c>). A headless host otherwise accepts NO runtime commands: an operator can't
/// type <c>status</c>, <c>kick</c>, or <c>set g_…</c> into a running server. This node runs a background thread
/// that blocks on <see cref="System.Console.In"/> and enqueues each line; <see cref="_Process"/> drains the
/// queue once per frame on the MAIN thread and hands each line to <see cref="CommandSink"/>, which the host
/// wires to the server command path (with the <c>RunOnSimThread</c> gate discipline under sv_threaded).
///
/// The reader thread is a background thread (dies with the process) and tolerates the three ways stdin can be
/// absent on a server box: a piped/redirected stream that hits EOF (the scripted <c>echo status | godot</c>
/// smoke), a closed handle (a systemd unit with no console → <c>ReadLine</c> returns null), or a throwing
/// handle (no stdin at all → caught). In every case the thread exits quietly and the host keeps running.
/// Gated to the headless/dedicated host (a windowed client has the in-game console instead) and off entirely
/// with <c>--no-console</c>.
/// </summary>
public sealed partial class ServerConsole : Node
{
    private readonly ConcurrentQueue<string> _lines = new();
    private Thread? _reader;
    private volatile bool _running;

    /// <summary>Where drained console lines go — the host wires this to its server-command executor. Invoked on
    /// the MAIN thread from <see cref="_Process"/>, so the sink itself owns any sim-thread hand-off.</summary>
    public Action<string>? CommandSink { get; set; }

    public override void _Ready()
    {
        _running = true;
        _reader = new Thread(ReadLoop)
        {
            Name = "XG-ServerConsole",
            IsBackground = true, // never block process exit — a blocked ReadLine can't be interrupted cleanly
        };
        _reader.Start();
        GD.Print("[ServerConsole] reading commands from stdin (type 'status', 'help', 'quit'; --no-console to disable).");
    }

    private void ReadLoop()
    {
        try
        {
            while (_running)
            {
                string? line;
                try
                {
                    line = System.Console.In.ReadLine();
                }
                catch (Exception ex)
                {
                    // No usable stdin handle (service with no console, closed stream). Stop reading — the host
                    // still runs; commands then arrive only via rcon (DS-6) / the net path.
                    GD.Print($"[ServerConsole] stdin unavailable ({ex.GetType().Name}); console input disabled.");
                    return;
                }

                if (line is null)
                {
                    // EOF: a redirected stream ran out (the `echo … | godot` smoke) or the terminal closed. Done.
                    return;
                }

                // Strip a leading UTF-8 BOM (U+FEFF): a redirected/piped stdin (a control script, or PowerShell's
                // native-command pipe) can prepend one to the first line, which would otherwise turn `status` into
                // an unknown command. Harmless for a real terminal (no BOM present).
                line = line.TrimStart('﻿').Trim();
                if (line.Length > 0)
                    _lines.Enqueue(line);
            }
        }
        catch { /* teardown race — the node left the tree; drop silently */ }
    }

    public override void _Process(double delta)
    {
        // Drain on the main thread and dispatch. The sink is responsible for the sim-thread gate (RunOnSimThread)
        // when the server is threaded — mirrors how the changelevel/bot sinks cross threads.
        while (_lines.TryDequeue(out string? line))
        {
            try { CommandSink?.Invoke(line); }
            catch (Exception ex) { GD.PrintErr($"[ServerConsole] command '{line}' failed: {ex.Message}"); }
        }
    }

    public override void _ExitTree()
    {
        _running = false;
        // The reader may be parked in a blocking ReadLine; it's a background thread, so we don't join — it dies
        // with the process. Setting _running false makes it exit after its current (or next) read returns.
    }
}
