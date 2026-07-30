using System.Collections.Generic;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for <see cref="DeferredCommands"/> — the sim-clock command queue (DP <c>Cbuf_Execute_Deferred</c> /
/// <c>Cmd_Defer_f</c>) that backs the <c>defer</c> command and the passed-restart vote. Pure logic; no world.
/// </summary>
public class DeferredCommandsTests
{
    [Fact]
    public void Defer_DoesNotFireBeforeDelayElapses()
    {
        var q = new DeferredCommands();
        var fired = new List<string>();
        q.Defer(1f, "restart", now: 0f);

        q.Pump(0.5f, fired.Add); // half a second later — not yet
        Assert.Empty(fired);
        Assert.True(q.HasPending);
    }

    [Fact]
    public void Defer_FiresOnceWhenDelayElapses_AndIsRemoved()
    {
        var q = new DeferredCommands();
        var fired = new List<string>();
        q.Defer(1f, "restart", now: 0f);

        q.Pump(1f, fired.Add);           // exactly at the fire time → fires
        Assert.Equal(new[] { "restart" }, fired);
        Assert.False(q.HasPending);

        q.Pump(2f, fired.Add);           // already removed → no second fire
        Assert.Single(fired);
    }

    [Fact]
    public void Defer_FiresInInsertionOrder()
    {
        var q = new DeferredCommands();
        var fired = new List<string>();
        // Both due at the same pump; DP walks the list in insertion order.
        q.Defer(1f, "first", now: 0f);
        q.Defer(1f, "second", now: 0f);

        q.Pump(1f, fired.Add);
        Assert.Equal(new[] { "first", "second" }, fired);
    }

    [Fact]
    public void Defer_NonPositiveDelay_FiresOnNextPump()
    {
        var q = new DeferredCommands();
        var fired = new List<string>();
        q.Defer(0f, "now", now: 5f);     // delay 0 → fireTime == now
        q.Pump(5f, fired.Add);
        Assert.Equal(new[] { "now" }, fired);
    }

    [Fact]
    public void Clear_EmptiesTheQueue()
    {
        var q = new DeferredCommands();
        var fired = new List<string>();
        q.Defer(1f, "a", now: 0f);
        q.Defer(1f, "b", now: 0f);
        q.Clear();
        Assert.False(q.HasPending);
        q.Pump(100f, fired.Add);
        Assert.Empty(fired);
    }

    [Fact]
    public void EmptyCommand_IsNotQueued()
    {
        var q = new DeferredCommands();
        q.Defer(1f, "", now: 0f);        // DP requires strlen(argv(2)) > 0
        Assert.False(q.HasPending);
    }

    [Fact]
    public void Describe_NoPending_PrintsTheStandardLine()
    {
        var q = new DeferredCommands();
        IReadOnlyList<string> lines = q.Describe(0f);
        Assert.Equal(new[] { "No commands are pending." }, lines);
    }

    [Fact]
    public void Describe_ShowsRemainingTimeAndCommand()
    {
        var q = new DeferredCommands();
        q.Defer(2f, "restart", now: 0f);
        IReadOnlyList<string> lines = q.Describe(0.5f); // 1.5 s remaining
        Assert.Single(lines);
        Assert.Contains("restart", lines[0]);
        Assert.Contains("1.50", lines[0]); // "-> In      1.50: restart"
        Assert.StartsWith("-> In", lines[0]);
    }

    [Fact]
    public void Pump_OnlyFiresDueEntries_LeavesTheRest()
    {
        var q = new DeferredCommands();
        var fired = new List<string>();
        q.Defer(1f, "soon", now: 0f);
        q.Defer(5f, "later", now: 0f);

        q.Pump(1f, fired.Add);
        Assert.Equal(new[] { "soon" }, fired);
        Assert.True(q.HasPending);       // "later" still queued
        Assert.Equal(1, q.Count);
    }

    [Fact]
    public void Pump_CommandThatReDefers_DoesNotReFireThisTick()
    {
        // A command that itself enqueues another defer must not be fired again in the same pump pass (DP moves
        // the node to the free list before AddText). Guards against an infinite same-tick loop.
        var q = new DeferredCommands();
        int count = 0;
        q.Defer(0f, "loop", now: 0f);
        q.Pump(0f, cmd =>
        {
            count++;
            if (count < 3) q.Defer(0f, "loop", now: 0f); // re-arm (would fire next tick, not this one)
        });
        Assert.Equal(1, count);          // fired exactly once this tick
        Assert.True(q.HasPending);       // the re-armed one waits for the next pump
    }
}
