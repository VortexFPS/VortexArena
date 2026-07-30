using System;
using System.Collections.Generic;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for the <see cref="Log"/> facade (the C# successor to Xonotic's <c>lib/log.qh</c>): the per-level
/// <c>developer</c> gating (LOG_TRACE&gt;0, LOG_DEBUG&gt;1, LOG_INFO message-always/header-gated, the
/// always-on WARN/SEVERE/FATAL/HELP), the <c>[::prog::LEVEL]</c> header + source location, FATAL's throw,
/// SEVERE's backtrace, the Quake colour-code stripper, and the live read of the <c>developer</c> cvar.
///
/// <see cref="Log"/> is global static state, so each test resets it (ctor + <see cref="Dispose"/>) and drives
/// the verbosity through <see cref="Log.DeveloperLevel"/> directly — except the one integration test that
/// proves the default provider reads the real <c>developer</c> cvar.
/// </summary>
[Collection("GlobalState")]
public class LogTests : IDisposable
{
    public LogTests() => Log.ResetForTests();
    public void Dispose() => Log.ResetForTests();

    /// <summary>Point the sink at a list and pin the developer level; returns the captured (level, line) sink buffer.</summary>
    private static List<(LogLevel Level, string Line)> Capture(int developer)
    {
        var lines = new List<(LogLevel, string)>();
        Log.Program = "test";
        Log.DeveloperLevel = () => developer;
        Log.Sink = (lvl, line) => lines.Add((lvl, line));
        return lines;
    }

    // =========================================================================================== INFO

    [Fact]
    public void Info_AtDeveloper0_EmitsBareMessageNoHeader()
    {
        var log = Capture(0);
        Log.Info("hello world");
        var (level, line) = Assert.Single(log);
        Assert.Equal(LogLevel.Info, level);
        Assert.Equal("hello world", line);                 // QC: message always prints, header suppressed at dev 0
    }

    [Fact]
    public void Info_AtDeveloper1_AddsHeaderWithoutSourceLocation()
    {
        var log = Capture(1);
        Log.Info("hello world");
        string line = Assert.Single(log).Line;
        Assert.Equal("[::test::INFO] hello world", line);   // header yes, source location only at dev>1
    }

    [Fact]
    public void Info_AtDeveloper2_AddsHeaderWithSourceLocation()
    {
        var log = Capture(2);
        Log.Info("hello world");
        string line = Assert.Single(log).Line;
        Assert.StartsWith("[::test::INFO] ", line);
        Assert.Contains("LogTests.cs:", line);             // __FILE__:__LINE__ equivalent
        Assert.Contains(nameof(Info_AtDeveloper2_AddsHeaderWithSourceLocation), line); // __FUNC__
        Assert.EndsWith("\nhello world", line);            // QC sourceloc ends in \n → message on next line
    }

    // ========================================================================================== TRACE

    [Fact]
    public void Trace_IsSuppressedBelowDeveloper1()
    {
        var log = Capture(0);
        Log.Trace("trace me");
        Assert.Empty(log);                                 // QC dprint: silent at developer 0
    }

    [Fact]
    public void Trace_EmitsAtDeveloper1_WithHeaderAndSourceLocation()
    {
        var log = Capture(1);
        Log.Trace("trace me");
        var (level, line) = Assert.Single(log);
        Assert.Equal(LogLevel.Trace, level);
        Assert.StartsWith("[::test::TRACE] ", line);
        Assert.Contains("LogTests.cs:", line);
        Assert.EndsWith("\ntrace me", line);
    }

    // ========================================================================================== DEBUG

    [Fact]
    public void Debug_IsSuppressedBelowDeveloper2()
    {
        var log = Capture(1);                              // dev 1 is enough for TRACE but not DEBUG
        Log.Debug("debug me");
        Assert.Empty(log);
    }

    [Fact]
    public void Debug_EmitsAtDeveloper2()
    {
        var log = Capture(2);
        Log.Debug("debug me");
        var (level, line) = Assert.Single(log);
        Assert.Equal(LogLevel.Debug, level);
        Assert.StartsWith("[::test::DEBUG] ", line);
        Assert.EndsWith("\ndebug me", line);
    }

    // =================================================================================== WARN / HELP

    [Fact]
    public void Warn_AlwaysEmits_HeaderGainsSourceLocationWithDeveloper()
    {
        var off = Capture(0);
        Log.Warn("careful");
        var (level, line) = Assert.Single(off);
        Assert.Equal(LogLevel.Warn, level);
        Assert.Equal("[::test::WARNING] careful", line);   // header always; no source location at dev 0

        var on = Capture(1);
        Log.Warn("careful");
        string devLine = Assert.Single(on).Line;
        Assert.StartsWith("[::test::WARNING] ", devLine);
        Assert.Contains("LogTests.cs:", devLine);          // source location added at dev>0
    }

    [Fact]
    public void Help_AlwaysEmits_NeverHasHeader()
    {
        var log = Capture(2);                              // even at max verbosity, HELP carries no header
        Log.Help("usage: foo <bar>");
        var (level, line) = Assert.Single(log);
        Assert.Equal(LogLevel.Help, level);
        Assert.Equal("usage: foo <bar>", line);
    }

    // ================================================================================= FATAL / SEVERE

    [Fact]
    public void Fatal_LogsThenThrows()
    {
        var log = Capture(0);
        var ex = Assert.Throws<LogFatalException>(() => Log.Fatal("the sky is falling"));
        Assert.Equal("the sky is falling", ex.Message);

        var (level, line) = Assert.Single(log);            // emitted to the sink before the throw
        Assert.Equal(LogLevel.Fatal, level);
        Assert.Equal("[::test::FATAL] the sky is falling", line);
    }

    [Fact]
    public void Severe_LogsBacktrace_DoesNotThrow()
    {
        var log = Capture(0);
        Log.Severe("something is wrong");                  // must NOT throw
        var (level, line) = Assert.Single(log);
        Assert.Equal(LogLevel.Severe, level);
        Assert.StartsWith("[::test::SEVERE] ", line);
        Assert.Contains("--- CUT HERE ---", line);         // QC backtrace() brackets
        Assert.Contains("something is wrong", line);
        Assert.Contains("--- CUT UNTIL HERE ---", line);
        Assert.Contains(nameof(Severe_LogsBacktrace_DoesNotThrow), line); // the managed stack trace
    }

    // =================================================================================== query helpers

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, true, false)]
    [InlineData(2, true, true)]
    public void WillTrace_WillDebug_TrackDeveloperLevel(int developer, bool willTrace, bool willDebug)
    {
        Capture(developer);
        Assert.Equal(developer, Log.Developer);
        Assert.Equal(willTrace, Log.WillTrace);
        Assert.Equal(willDebug, Log.WillDebug);
        Assert.Equal(willTrace, Log.IsEnabled(LogLevel.Trace));
        Assert.Equal(willDebug, Log.IsEnabled(LogLevel.Debug));
        Assert.True(Log.IsEnabled(LogLevel.Info));         // always-on levels are always enabled
        Assert.True(Log.IsEnabled(LogLevel.Warn));
        Assert.True(Log.IsEnabled(LogLevel.Fatal));
    }

    // ==================================================================================== StripColors

    [Fact]
    public void StripColors_RemovesDigitAndHexCodes_KeepsEscapedAndUnknown()
    {
        Assert.Equal("red hex ^caret white",
            Log.StripColors("^1red ^xF00hex ^^caret ^7white")); // ^d, ^xRGB dropped; ^^ → ^
        Assert.Equal("plain text", Log.StripColors("plain text"));   // fast path, no caret
        Assert.Equal("trailing^", Log.StripColors("trailing^"));     // dangling caret kept verbatim
        Assert.Equal("^z keep", Log.StripColors("^z keep"));         // unknown ^code left as-is
        Assert.Equal("", Log.StripColors(""));
    }

    [Fact]
    public void DefaultConsoleSink_StripsColorsFromMessage()
    {
        // The Godot/console sinks must not leak ^codes; the default console sink strips, and the live message
        // (which may carry colour codes from a player name etc.) is cleaned before display.
        string captured = "";
        Log.Program = "test";
        Log.DeveloperLevel = () => 0;
        Log.Sink = (_, line) => captured = Log.StripColors(line);
        Log.Info("^1player^7 joined");
        Assert.Equal("player joined", captured);
    }

    // ========================================================================================= ToBBCode

    [Fact]
    public void ToBBCode_EscapesBrackets_InfoKeepsDefaultColor()
    {
        // Info has no level tint, and our [::prog::LEVEL] header's '[' must be escaped so PrintRich doesn't
        // read it as a BBCode tag.
        Assert.Equal("[lb]::test::INFO] hello", Log.ToBBCode(LogLevel.Info, "[::test::INFO] hello"));
    }

    [Fact]
    public void ToBBCode_TintsByLevel()
    {
        Assert.Equal("[color=#ffcc33]careful[/color]", Log.ToBBCode(LogLevel.Warn, "careful"));
        Assert.Equal("[color=#cc88ff]t[/color]", Log.ToBBCode(LogLevel.Trace, "t"));
    }

    [Fact]
    public void ToBBCode_TranslatesQuakeColorCodes()
    {
        // ^1 opens red, ^7 resets (closes) — within a default-colour (Info) line.
        Assert.Equal("[color=#ff0000]red[/color]white", Log.ToBBCode(LogLevel.Info, "^1red^7white"));
        // ^xRGB → #RRGGBB, nested inside the Trace tint.
        Assert.Equal("[color=#cc88ff][color=#FF0000]hi[/color][/color]", Log.ToBBCode(LogLevel.Trace, "^xF00hi"));
        // ^^ → literal caret; an unrecognised ^z is preserved verbatim.
        Assert.Equal("a^b ^z", Log.ToBBCode(LogLevel.Info, "a^^b ^z"));
    }

    // ================================================================================== ring buffer

    [Fact]
    public void Buffer_CapturesAllEntries_RegardlessOfDeveloperGate()
    {
        // The in-game console reads from the buffer, which captures EVERY Log.* call regardless of `developer`
        // — so a Trace emitted at dev 0 still appears when the user opens `set developer 1`.
        Capture(0);
        Log.Info("info1");
        Log.Trace("trace0");                                 // would be suppressed by the live sink at dev 0
        Log.Debug("debug0");                                 // also suppressed by the live sink
        Log.Warn("warn1");

        var snap = Log.BufferSnapshot();
        Assert.Equal(4, snap.Count);
        Assert.Equal(LogLevel.Info, snap[0].Level);
        Assert.Equal("info1", snap[0].Message);
        Assert.Equal(LogLevel.Trace, snap[1].Level);
        Assert.Equal("trace0", snap[1].Message);
        Assert.Equal(LogLevel.Debug, snap[2].Level);
        Assert.Equal(LogLevel.Warn, snap[3].Level);
    }

    [Fact]
    public void Render_AtDeveloperLevel_GivesNullForHiddenEntries()
    {
        // Render reproduces the live-sink formatting for a buffered entry at the GIVEN dev level, returning
        // null when the entry would be suppressed at that level (Trace<dev 1, Debug<dev 2).
        Capture(0);
        Log.Trace("hidden trace");
        Log.Info("visible info");

        var snap = Log.BufferSnapshot();
        Assert.Null(Log.Render(snap[0], 0));                  // Trace hidden at dev 0
        Assert.NotNull(Log.Render(snap[0], 1));               // visible at dev 1
        Assert.Equal("visible info", Log.Render(snap[1], 0)); // Info bare at dev 0
        Assert.StartsWith("[::test::INFO] ", Log.Render(snap[1], 1)!); // header at dev 1
    }

    [Fact]
    public void EntryRecorded_FiresForEveryCall_EvenWhenSinkSuppresses()
    {
        // The buffer subscription gets the entry even for messages the live sink filters out (the console scrollback
        // needs them so it can reveal them retroactively when developer goes up).
        Capture(0);
        var recorded = new List<LogEntry>();
        Log.EntryRecorded += recorded.Add;

        Log.Trace("rec");
        Log.Info("rec2");

        Assert.Equal(2, recorded.Count);
        Assert.Equal(LogLevel.Trace, recorded[0].Level);
        Assert.Equal("rec", recorded[0].Message);
        Assert.Equal(LogLevel.Info, recorded[1].Level);
    }

    [Fact]
    public void BufferClear_EmptiesBuffer_DoesNotResetSubscribers()
    {
        Capture(0);
        Log.Info("one");
        Assert.Single(Log.BufferSnapshot());

        Log.BufferClear();
        Assert.Empty(Log.BufferSnapshot());

        Log.Info("two");                                      // subsequent calls still record
        Assert.Single(Log.BufferSnapshot());
    }

    // =================================================================== integration: the developer cvar

    [Fact]
    public void DeveloperLevel_DefaultProvider_ReadsDeveloperCvar()
    {
        Api.Services = new EngineServices(new CollisionWorld());
        Cvars.RegisterDefaults();
        Assert.Equal("0", Api.Cvars.GetString("developer")); // registered default is off

        // Leave Log.DeveloperLevel at the default provider (reads the cvar) — only swap the sink.
        var lines = new List<(LogLevel, string)>();
        Log.Program = "server";
        Log.Sink = (lvl, line) => lines.Add((lvl, line));

        Log.Trace("quiet");                                  // developer 0 → suppressed
        Assert.Empty(lines);

        Cvars.Set("developer", "1");                         // `set developer 1` (the QC console toggle)
        Assert.True(Log.WillTrace);
        Log.Trace("now visible");
        var (level, msg) = Assert.Single(lines);
        Assert.Equal(LogLevel.Trace, level);
        Assert.StartsWith("[::server::TRACE] ", msg);
        Assert.EndsWith("\nnow visible", msg);
    }
}
