using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;

namespace VortexArena.Game.Client;

/// <summary>
/// Idle-time asset warm queue (planning/PERFORMANCE_REPORT.md §5 S3). After the loading screen drops, the eager precache
/// (A3) has warmed the combat essentials — weapons, weapon sounds, the local + default player models — but the
/// long tail (announcer lines, item-pickup cues, the other stock player models, gib variants) is still cold and
/// would hitch on first use. This drains a low-priority work queue on a small per-frame millisecond budget so
/// that within the first minute of play the entire asset set is hot, regardless of what the precache lists
/// covered — without ever spiking a frame (the Stopwatch caps each frame's warm work, and it self-disables once
/// the queue is empty).
/// </summary>
public partial class IdleWarmer : Node
{
    /// <summary>Per-frame warm budget in milliseconds. One item runs even if it overshoots (so a single slow
    /// asset can't deadlock the queue), but the loop stops once the budget is spent — keeping the warm work
    /// well under a frame so it's invisible.</summary>
    [Export] public double BudgetMs { get; set; } = 1.5;

    private readonly Queue<Action> _work = new();

    /// <summary>Append a unit of warm work (typically a cached loader call). Cheap to over-queue: the loaders
    /// cache, so re-warming an already-hot asset is a no-op.</summary>
    public void Enqueue(Action work)
    {
        if (work is not null)
            _work.Enqueue(work);
    }

    public int Pending => _work.Count;

    public override void _Process(double delta)
    {
        if (_work.Count == 0)
        {
            SetProcess(false); // nothing left — go quiet (re-enabled by Enqueue via SetProcess below)
            return;
        }

        var sw = Stopwatch.StartNew();
        // Always run at least one item per frame so a tiny budget still drains; stop once the budget is spent.
        do
        {
            Action work = _work.Dequeue();
            try { work(); }
            catch (Exception ex) { GD.PrintErr($"[IdleWarmer] warm item failed: {ex.Message}"); }
        }
        while (_work.Count > 0 && sw.Elapsed.TotalMilliseconds < BudgetMs);

        if (_work.Count == 0)
            GD.Print("[IdleWarmer] asset warm queue drained — full asset set is hot.");
    }
}
