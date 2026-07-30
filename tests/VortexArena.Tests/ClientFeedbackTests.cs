using System.Collections.Generic;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Gameplay.Damage;
using VortexArena.Common.Services;
using VortexArena.Net;
using VortexArena.Server;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// [T41] Client-feedback drivers — the unit coverage for the four pieces of <c>view.qc</c>/<c>announcer.qc</c>
/// ported in T41:
/// <list type="bullet">
///   <item><b>Countdown number schedule</b> (<see cref="AnnouncerController.PickCountdownNumber"/> /
///         <see cref="AnnouncerController.CountdownRounded"/>) — 3/2/1/prepare picks the right NUM_* names.</item>
///   <item><b>Time-remaining hysteresis</b> (<see cref="AnnouncerController.Tick"/>) — the QC
///         <c>ANNOUNCER_CHECKMINUTE</c> latch fires the 5-/1-minute announcement once per crossing, re-arms on
///         the way back up, and respects <c>cl_announcer_maptime</c> + intermission.</item>
///   <item><b>DamageSystem hit-feedback accumulators</b> — the QC damage.qc:611-661 "count the damage" block
///         (enemy damage banks the pre-split amount; chat/team hits bank a typehit; fire/dead/self bank
///         nothing) and the QC world.qc EndFrame flush (typehit &gt; kill &gt; hit priority, ceil'd total).</item>
///   <item><b>Objective-stat networking lifecycle</b> — the <see cref="EntityField.Feedback"/> delta round-trips
///         HitDamageDealtTotal + HitTime/TypeHitTime/KillTime + the objective rings, off the wire when unchanged.</item>
///   <item><b>HitSoundLogic</b> — the client hit/typehit/kill state machine (view.qc UpdateDamage/HitSound):
///         accumulate-never-drop antispam, stat-time-compared typehit/kill, the arc hack, pitch curve.</item>
/// </list>
/// </summary>
public class ClientFeedbackTests
{
    // ===========================================================================================
    //  Countdown number schedule (Announcer_PickNumber / Announcer_Countdown rounding)
    // ===========================================================================================

    [Theory]
    [InlineData(3, "NUM_GAMESTART_3")]
    [InlineData(2, "NUM_GAMESTART_2")]
    [InlineData(1, "NUM_GAMESTART_1")]
    public void Countdown_Picks_The_GameStart_Number_For_3_2_1(int second, string expected)
        => Assert.Equal(expected, AnnouncerController.PickCountdownNumber(
            AnnouncerController.CountdownKind.GameStart, second));

    [Fact]
    public void Countdown_Terminal_Zero_Is_Not_A_Number_Tick()
    {
        // 0 is the BEGIN tick (handled separately), not a NUM_* announcement.
        Assert.Null(AnnouncerController.PickCountdownNumber(AnnouncerController.CountdownKind.GameStart, 0));
        Assert.Null(AnnouncerController.PickCountdownNumber(AnnouncerController.CountdownKind.RoundStart, 0));
    }

    [Fact]
    public void Countdown_Out_Of_Range_Seconds_Have_No_Announcement()
    {
        Assert.Null(AnnouncerController.PickCountdownNumber(AnnouncerController.CountdownKind.GameStart, -1));
        Assert.Null(AnnouncerController.PickCountdownNumber(AnnouncerController.CountdownKind.GameStart, 11));
    }

    [Fact]
    public void Countdown_RoundStart_Uses_The_RoundStart_Family()
        => Assert.Equal("NUM_ROUNDSTART_3",
            AnnouncerController.PickCountdownNumber(AnnouncerController.CountdownKind.RoundStart, 3));

    [Theory]
    [InlineData(3.0f, 3)]   // exact
    [InlineData(2.4f, 2)]   // floor(0.5 + 2.4) = floor(2.9) = 2
    [InlineData(2.6f, 3)]   // floor(0.5 + 2.6) = floor(3.1) = 3  (QC rounds to nearest)
    [InlineData(0.2f, 0)]   // floor(0.5 + 0.2) = 0 (terminal/BEGIN)
    public void Countdown_Rounded_Mirrors_QC_floor_half_plus(float secondsLeft, int rounded)
        => Assert.Equal(rounded, AnnouncerController.CountdownRounded(secondsLeft));

    // ===========================================================================================
    //  Time-remaining hysteresis (Announcer_Time / ANNOUNCER_CHECKMINUTE)
    // ===========================================================================================

    /// <summary>Build a controller whose context is driven by mutable locals + a list-recording announce sink.</summary>
    private static AnnouncerController MakeTimeAnnouncer(out List<int> fired,
        System.Func<float> timeLeftSeconds, System.Func<bool>? intermission = null, int mapTimeMode = 3)
    {
        var rec = new List<int>();
        fired = rec;
        // Drive Announcer_Time purely off a "time left" supplier: pin game_starttime at 0, time at 0, and feed
        // the remaining time through the (warmup-off) TIMELIMIT path. timeLeft = TIMELIMIT*60 + start - now, so
        // with start=now=0 the TimeLimitMinutes supplier IS timeLeft/60.
        return new AnnouncerController
        {
            Now = () => 0f,
            GameStartTime = () => 0f,
            WarmupStage = () => false,
            WarmupTimeLimitSeconds = () => 0f,
            TimeLimitMinutes = () => timeLeftSeconds() / 60f,
            Intermission = intermission ?? (() => false),
            AnnouncerMapTime = () => mapTimeMode,
            AnnounceRemainingMin = rec.Add,
        };
    }

    [Fact]
    public void TimeRemaining_Fires_Five_Minute_Once_As_It_Crosses_Below_300s()
    {
        float timeLeft = 0f;
        var ann = MakeTimeAnnouncer(out List<int> fired, () => timeLeft);

        // First tick at 320s: above 300, no announcement, latch armed clear after the warmup-stage prime tick.
        timeLeft = 320f; ann.Tick(); // primes warmup_stage_prev (returns), then
        timeLeft = 320f; ann.Tick();
        Assert.Empty(fired);

        // Cross into the (299, 300) arming window -> fire "5" exactly once.
        timeLeft = 299.5f; ann.Tick();
        Assert.Equal(new[] { 5 }, fired);

        // Many more frames still inside / below the window -> no repeat (latch holds).
        timeLeft = 299.0f; ann.Tick();
        timeLeft = 250.0f; ann.Tick();
        Assert.Equal(new[] { 5 }, fired);
    }

    [Fact]
    public void TimeRemaining_Five_Minute_ReArms_When_Time_Climbs_Back_Above_300s()
    {
        float timeLeft = 320f;
        var ann = MakeTimeAnnouncer(out List<int> fired, () => timeLeft);
        ann.Tick(); ann.Tick(); // prime

        timeLeft = 299.5f; ann.Tick();          // fire 5
        timeLeft = 305f; ann.Tick();            // climb back above 300 -> latch clears (no announce on the climb)
        Assert.Equal(new[] { 5 }, fired);
        timeLeft = 299.5f; ann.Tick();          // cross again -> fires 5 a SECOND time
        Assert.Equal(new[] { 5, 5 }, fired);
    }

    [Fact]
    public void TimeRemaining_Fires_One_Minute_When_It_Crosses_Below_60s()
    {
        float timeLeft = 320f;
        var ann = MakeTimeAnnouncer(out List<int> fired, () => timeLeft);
        ann.Tick(); ann.Tick(); // prime

        timeLeft = 299.5f; ann.Tick();          // 5 fires
        timeLeft = 59.5f; ann.Tick();           // crosses below 60 -> 1 fires (5 already latched)
        Assert.Equal(new[] { 5, 1 }, fired);
    }

    [Fact]
    public void TimeRemaining_Mode2_Suppresses_The_One_Minute_Announcement()
    {
        // cl_announcer_maptime == 2 -> 5-minute only.
        float timeLeft = 320f;
        var ann = MakeTimeAnnouncer(out List<int> fired, () => timeLeft, mapTimeMode: 2);
        ann.Tick(); ann.Tick();

        timeLeft = 299.5f; ann.Tick(); // 5 fires
        timeLeft = 59.5f; ann.Tick();  // 1 would fire under mode 1/3, but mode 2 only does 5
        Assert.Equal(new[] { 5 }, fired);
    }

    [Fact]
    public void TimeRemaining_Mode1_Suppresses_The_Five_Minute_Announcement()
    {
        // cl_announcer_maptime == 1 -> 1-minute only.
        float timeLeft = 320f;
        var ann = MakeTimeAnnouncer(out List<int> fired, () => timeLeft, mapTimeMode: 1);
        ann.Tick(); ann.Tick();

        timeLeft = 299.5f; ann.Tick(); // mode 1 does NOT announce 5
        timeLeft = 59.5f; ann.Tick();  // 1 fires
        Assert.Equal(new[] { 1 }, fired);
    }

    [Fact]
    public void TimeRemaining_Is_Silent_During_Intermission()
    {
        float timeLeft = 299.5f;
        var ann = MakeTimeAnnouncer(out List<int> fired, () => timeLeft, intermission: () => true);
        ann.Tick(); ann.Tick(); ann.Tick();
        Assert.Empty(fired);
    }

    // ===========================================================================================
    //  DamageSystem hit-feedback accumulators (QC damage.qc:611-661 "count the damage") + the
    //  EndFrame flush (QC world.qc:2507-2528 — typehit > kill > hit priority, ceil'd total)
    // ===========================================================================================

    [Collection("GlobalState")]
    public sealed class DamageStat : System.IDisposable
    {
        private readonly IEngineServices _prevServices;
        private readonly IDamageSystem _prevDamage;
        private readonly DictCvars _cvars = new();
        private readonly MutableClock _clock = new();

        public DamageStat()
        {
            Sounds.RegisterAll();
            _prevServices = Api.Services;
            _prevDamage = Combat.System;
            Api.Services = new MinimalServices(_cvars, _clock);
            Combat.System = new DamageSystem();
            _clock.Time = 100f;
        }

        public void Dispose()
        {
            Combat.System = _prevDamage;
            Api.Services = _prevServices;
        }

        /// <summary>The non-spectating EndFrame flush: stamp a player from their OWN accumulators, then clear.
        /// QC keeps these as two separate FOREACH_CLIENT passes (world.qc:2507-2527) precisely so a spectator
        /// can read their spectatee's accumulators before anyone clears them — see the spectator test below.</summary>
        private static void FlushSelf(Player p, float time)
        {
            DamageSystem.StampHitSoundStats(p, p, time);
            DamageSystem.ClearHitSoundAccumulators(p);
        }

        private static Player NewPlayer(float health = 100f, float armor = 0f, int team = 0)
        {
            // QC players are DAMAGE_AIM (SpawnSystem sets DamageMode.Aim) — the hit-feedback count block
            // gates on it, exactly like Base's `targ.takedamage == DAMAGE_AIM`.
            var p = new Player { Flags = EntFlags.Client, TakeDamage = DamageMode.Aim, Team = team };
            p.Mins = new Vector3(-16, -16, -24);
            p.Maxs = new Vector3(16, 16, 45);
            p.SetResource(ResourceType.Health, health);
            p.SetResource(ResourceType.Armor, armor);
            p.MaxHealth = 100f;
            p.DamageForceScale = 1f;
            return p;
        }

        [Fact]
        public void Enemy_Hit_Banks_The_Damage_Amount_Then_Flush_Advances_The_Total()
        {
            var attacker = NewPlayer(team: 5);
            var victim = NewPlayer(health: 100f, armor: 0f, team: 14); // FFA: different entities = enemies

            // 30 damage banks 30 in the PER-FRAME accumulator; the cumulative total moves only at the flush.
            float removed = Combat.Damage(victim, null, attacker, 30f, DeathTypes.Generic, victim.Origin, Vector3.Zero);
            Assert.Equal(30f, removed, 3);
            Assert.Equal(30f, attacker.HitSoundDamageDealt, 3);
            Assert.Equal(0f, attacker.HitsoundDamageDealtTotal, 3);

            // a second same-frame hit accumulates.
            Combat.Damage(victim, null, attacker, 20f, DeathTypes.Generic, victim.Origin, Vector3.Zero);
            Assert.Equal(50f, attacker.HitSoundDamageDealt, 3);

            // EndFrame: HIT_TIME stamps, the total advances by ceil(50), the accumulator clears.
            FlushSelf(attacker, 100f);
            Assert.Equal(100f, attacker.HitTime, 3);
            Assert.Equal(50f, attacker.HitsoundDamageDealtTotal, 3);
            Assert.Equal(0f, attacker.HitSoundDamageDealt, 3);
        }

        [Fact]
        public void Armor_Split_Does_Not_Change_The_Banked_Amount()
        {
            var attacker = NewPlayer(team: 5);
            // armor 100, blockpercent 0.7: a 40 hit saves 28 (armor) + takes 12 (health) — the bank is the
            // pre-split AMOUNT (40), matching QC's `hitsound_damage_dealt += damage`.
            var victim = NewPlayer(health: 100f, armor: 100f, team: 14);
            Combat.Damage(victim, null, attacker, 40f, DeathTypes.Generic, victim.Origin, Vector3.Zero);
            Assert.Equal(40f, attacker.HitSoundDamageDealt, 2);
        }

        [Fact]
        public void Overkill_Banks_The_Full_Amount_Not_The_Health_Removed()
        {
            var attacker = NewPlayer(team: 5);
            var victim = NewPlayer(health: 5f, armor: 0f, team: 14);
            // An 80-damage killing blow: QC counts the whole 80 (pre-application), not the 5 hp removed.
            // (The kill priority normally eats this at the flush once Obituary banks killsound.)
            Combat.Damage(victim, null, attacker, 80f, DeathTypes.Generic, victim.Origin, Vector3.Zero);
            Assert.Equal(80f, attacker.HitSoundDamageDealt, 2);
        }

        [Fact]
        public void Self_Damage_Does_Not_Accrue()
        {
            var p = NewPlayer(health: 100f, armor: 0f);
            Combat.Damage(p, null, p, 30f, DeathTypes.Generic, p.Origin, Vector3.Zero);
            Assert.Equal(0f, p.HitSoundDamageDealt, 3);
            Assert.Equal(0, p.TypeHitSoundCount);
        }

        [Fact]
        public void World_Damage_Does_Not_Accrue_Any_Player_Stat()
        {
            var victim = NewPlayer(health: 100f);
            Combat.Damage(victim, null, null, 25f, DeathTypes.Generic, victim.Origin, Vector3.Zero);
            Assert.Equal(0f, victim.HitSoundDamageDealt, 3);
        }

        [Fact]
        public void Chatting_Victim_Banks_A_TypeHit_Instead_Of_The_Beep()
        {
            var attacker = NewPlayer(team: 5);
            var victim = NewPlayer(health: 100f, team: 14);
            victim.ButtonChat = true; // QC PHYS_INPUT_BUTTON_CHAT(victim)
            Combat.Damage(victim, null, attacker, 30f, DeathTypes.Generic, victim.Origin, Vector3.Zero);
            Assert.Equal(0f, attacker.HitSoundDamageDealt, 3);
            Assert.Equal(1, attacker.TypeHitSoundCount);
        }

        [Fact]
        public void Team_Hit_Banks_A_TypeHit()
        {
            VortexArena.Common.Gameplay.Scoring.GameScores.Teamplay = true;
            try
            {
                var attacker = NewPlayer(team: 5);
                var victim = NewPlayer(health: 100f, team: 5); // same team in teamplay
                Combat.Damage(victim, null, attacker, 30f, DeathTypes.Generic, victim.Origin, Vector3.Zero);
                Assert.Equal(0f, attacker.HitSoundDamageDealt, 3);
                Assert.Equal(1, attacker.TypeHitSoundCount);
            }
            finally { VortexArena.Common.Gameplay.Scoring.GameScores.Teamplay = false; }
        }

        [Fact]
        public void Fire_Deathtype_Banks_Nothing()
        {
            var attacker = NewPlayer(team: 5);
            var victim = NewPlayer(health: 100f, team: 14);
            // QC: deathtype == DEATH_FIRE skips both accumulators (burn feedback is once-per-ignite,
            // handled by Fire_ApplyDamage's save/restore).
            Combat.Damage(victim, null, attacker, 10f, DeathTypes.Fire, victim.Origin, Vector3.Zero);
            Assert.Equal(0f, attacker.HitSoundDamageDealt, 3);
            Assert.Equal(0, attacker.TypeHitSoundCount);
        }

        [Fact]
        public void Dead_Victim_Banks_Nothing()
        {
            var attacker = NewPlayer(team: 5);
            var victim = NewPlayer(health: 100f, team: 14);
            victim.DeadState = DeadFlag.Dead; // QC !IS_DEAD(targ) gate — corpse hits give no feedback
            Combat.Damage(victim, null, attacker, 30f, DeathTypes.Generic, victim.Origin, Vector3.Zero);
            Assert.Equal(0f, attacker.HitSoundDamageDealt, 3);
        }

        [Fact]
        public void Flush_Priority_TypeHit_Beats_Kill_Beats_Hit()
        {
            // QC world.qc:2509-2517 is an else-if chain: exactly ONE stat advances per frame.
            var p = NewPlayer();
            p.TypeHitSoundCount = 1;
            p.KillSoundCount = 1;
            p.HitSoundDamageDealt = 50f;
            FlushSelf(p, 10f);
            Assert.Equal(10f, p.TypeHitTime, 3);
            Assert.Equal(0f, p.KillTime, 3);
            Assert.Equal(0f, p.HitTime, 3);
            Assert.Equal(0f, p.HitsoundDamageDealtTotal, 3); // the killing frame's damage is eaten
            // all accumulators cleared
            Assert.Equal(0, p.TypeHitSoundCount);
            Assert.Equal(0, p.KillSoundCount);
            Assert.Equal(0f, p.HitSoundDamageDealt, 3);

            // kill beats hit (the killing blow plays ONLY misc/kill)
            p.KillSoundCount = 1;
            p.HitSoundDamageDealt = 80f;
            FlushSelf(p, 11f);
            Assert.Equal(11f, p.KillTime, 3);
            Assert.Equal(0f, p.HitTime, 3);
            Assert.Equal(0f, p.HitsoundDamageDealtTotal, 3);
        }

        [Fact]
        public void Flush_Ceils_The_Damage_Total()
        {
            // QC EndFrame: STAT(HITSOUND_DAMAGE_DEALT_TOTAL) += ceil(hitsound_damage_dealt).
            var p = NewPlayer();
            p.HitSoundDamageDealt = 0.3f;
            FlushSelf(p, 5f);
            Assert.Equal(1f, p.HitsoundDamageDealtTotal, 3);
            Assert.Equal(5f, p.HitTime, 3);

            p.HitSoundDamageDealt = 12.2f;
            FlushSelf(p, 6f);
            Assert.Equal(14f, p.HitsoundDamageDealtTotal, 3); // 1 + ceil(12.2)
            Assert.Equal(6f, p.HitTime, 3);
        }

        [Fact]
        public void Spectator_Inherits_The_Spectatee_Feedback_Without_Stealing_It()
        {
            // QC world.qc:2508 resolves the accumulator source as `IS_SPEC(it) ? it.enemy : it`, so a
            // follow-spectator's own feedback stats mirror whoever they are watching — watching someone frag
            // sounds exactly like fragging. The stamp/clear split is what makes this safe: if clearing were
            // fused into the stamp, whichever of the two was iterated first would consume the banked damage
            // and the other would fall silent.
            var player = NewPlayer();
            var spectator = NewPlayer();

            player.HitSoundDamageDealt = 40f;
            player.KillSoundCount = 1;

            // Pass 1 — every viewer stamps from its source; the spectator reads the player's accumulators.
            DamageSystem.StampHitSoundStats(player, player, 20f);
            DamageSystem.StampHitSoundStats(spectator, player, 20f);
            // Pass 2 — only now does anything clear.
            DamageSystem.ClearHitSoundAccumulators(player);
            DamageSystem.ClearHitSoundAccumulators(spectator);

            // Both got the kill stamp: the spectator did NOT eat the player's event.
            Assert.Equal(20f, player.KillTime, 3);
            Assert.Equal(20f, spectator.KillTime, 3);
            Assert.Equal(0f, player.HitSoundDamageDealt, 3);

            // A spectator watching an IDLE player gets nothing (no phantom sounds).
            var idle = NewPlayer();
            var watcher = NewPlayer();
            DamageSystem.StampHitSoundStats(watcher, idle, 30f);
            Assert.Equal(0f, watcher.KillTime, 3);
            Assert.Equal(0f, watcher.HitTime, 3);
            Assert.Equal(0f, watcher.TypeHitTime, 3);
        }
    }

    // ===========================================================================================
    //  Objective-stat networking lifecycle (EntityField.Feedback delta)
    // ===========================================================================================

    [Fact]
    public void Feedback_Stats_RoundTrip_Through_The_Delta_Codec()
    {
        var baseline = new NetEntityState { EntNum = 7, Kind = NetEntityKind.Player };
        var current = baseline;
        current.HitDamageDealtTotal = 137f;
        current.NadeTimer = 0.5f;
        current.CaptureProgress = 0.25f;
        current.ReviveProgress = 0.75f;
        // [hitsound] the v18 feedback times ride the same block.
        current.HitTime = 12.5f;
        current.TypeHitTime = 13.25f;
        current.KillTime = 14.125f;

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, current);
        Assert.Equal(EntityField.Feedback, mask); // only the feedback block on the wire

        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, baseline);
        Assert.Equal(137f, got.HitDamageDealtTotal, 3);
        Assert.Equal(0.5f, got.NadeTimer, 3);
        Assert.Equal(0.25f, got.CaptureProgress, 3);
        Assert.Equal(0.75f, got.ReviveProgress, 3);
        Assert.Equal(12.5f, got.HitTime, 3);
        Assert.Equal(13.25f, got.TypeHitTime, 3);
        Assert.Equal(14.125f, got.KillTime, 3);
        Assert.Equal(NetEntityKind.Player, got.Kind); // carried from baseline
    }

    [Fact]
    public void KillTime_Advance_Alone_Is_A_Tracked_Feedback_Change()
    {
        // The killing-blow frame advances ONLY KillTime (the flush's priority chain) — the wire must carry it.
        var baseline = new NetEntityState { EntNum = 7, HitTime = 5f, KillTime = 5f };
        var current = baseline;
        current.KillTime = 9f;

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, current);
        Assert.Equal(EntityField.Feedback, mask);

        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, baseline);
        Assert.Equal(9f, got.KillTime, 3);
        Assert.Equal(5f, got.HitTime, 3); // untouched, carried from baseline
    }

    [Fact]
    public void ClientColors_RoundTrip_Through_The_Delta_Codec()
    {
        // [r15 #43] the packed 16*shirt+pants clientcolors slice (EntityField.Colors): a bot/player picking
        // FFA profile colors must reach the client render entity; a colorless player (0) keeps the bit clear.
        var baseline = new NetEntityState { EntNum = 3, Kind = NetEntityKind.Player };
        var current = baseline;
        current.Colors = 0x6B; // shirt 6, pants 11

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, current);
        Assert.Equal(EntityField.Colors, mask); // only the colors byte on the wire

        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, baseline);
        Assert.Equal(0x6B, got.Colors);

        // unchanged colors stay off the wire
        var same = current;
        same.Origin = new Vector3(2, 0, 0);
        var w2 = new BitWriter();
        EntityField mask2 = EntityStateCodec.WriteDelta(w2, current, same);
        Assert.Equal(EntityField.Origin, mask2);
        Assert.False(mask2.HasFlag(EntityField.Colors));
    }

    [Fact]
    public void Feedback_Stats_Stay_Off_The_Wire_When_Unchanged()
    {
        // a player whose nade timer is mid-charge but identical between frames: the mask must NOT include
        // Feedback (idle objective stats cost nothing).
        var baseline = new NetEntityState { EntNum = 7, NadeTimer = 0.4f, HitDamageDealtTotal = 50f };
        var same = baseline;
        same.Origin = new Vector3(1, 0, 0); // only the origin moved

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, same);
        Assert.Equal(EntityField.Origin, mask);
        Assert.False(mask.HasFlag(EntityField.Feedback));
    }

    [Fact]
    public void NadeTimer_Clearing_To_Zero_Is_A_Tracked_Change()
    {
        // a thrown/expired nade clears the timer back to 0 — that's an objective-stat lifecycle edge the wire
        // must carry (the ring disappears on the client).
        var charged = new NetEntityState { EntNum = 7, NadeTimer = 0.9f };
        var cleared = charged;
        cleared.NadeTimer = 0f;

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, charged, cleared);
        Assert.Equal(EntityField.Feedback, mask);

        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, charged);
        Assert.Equal(0f, got.NadeTimer, 5);
    }

    // ===========================================================================================
    //  HitSoundLogic — the client hit/typehit/kill state machine (view.qc UpdateDamage + HitSound)
    // ===========================================================================================

    /// <summary>One Update step with the QC cvar defaults (antispam 0.05, pitch 0.75..1.5, nominal 25).</summary>
    private static HitSoundCues Step(HitSoundLogic l, float now, float hitTime = 0f, float total = 0f,
        float typehitTime = 0f, float killTime = 0f, int mode = 1, bool haveArc = false, int spectatee = 0)
        => l.Update(now, mode, HitSoundLogic.DefaultAntispamTime,
            HitSoundLogic.DefaultMaxPitch, HitSoundLogic.DefaultMinPitch, HitSoundLogic.DefaultNomDamage,
            haveArc, spectatee, hitTime, total, typehitTime, killTime);

    [Fact]
    public void HitSound_First_Sample_Seeds_Silently()
    {
        // Joining mid-match against nonzero totals / recent kill times must not fire spurious feedback.
        var l = new HitSoundLogic();
        HitSoundCues c = Step(l, now: 0f, hitTime: 89f, total: 500f, typehitTime: 91f, killTime: 90f);
        Assert.False(c.PlayHit); Assert.False(c.PlayTypeHit); Assert.False(c.PlayKill); Assert.False(c.NewDamage);

        // and an idle next frame stays silent.
        c = Step(l, now: 0.1f, hitTime: 89f, total: 500f, typehitTime: 91f, killTime: 90f);
        Assert.False(c.PlayHit); Assert.False(c.PlayTypeHit); Assert.False(c.PlayKill);
    }

    [Fact]
    public void HitSound_Beep_Fires_When_Damage_And_HitTime_Advance()
    {
        var l = new HitSoundLogic();
        Step(l, now: 0f); // seed
        HitSoundCues c = Step(l, now: 0.1f, hitTime: 1f, total: 30f);
        Assert.True(c.PlayHit);
        Assert.True(c.NewDamage);          // drives the crosshair hit flash
        Assert.Equal(1f, c.HitPitch, 3);   // mode 1 = fixed pitch
    }

    [Fact]
    public void HitSound_Damage_Inside_The_Antispam_Window_Accumulates_Never_Drops()
    {
        var l = new HitSoundLogic();
        Step(l, now: 0f, mode: 2); // seed
        HitSoundCues first = Step(l, now: 0.1f, hitTime: 1f, total: 30f, mode: 2);
        Assert.True(first.PlayHit);
        Assert.Equal(HitSoundLogic.ComputePitch(2, 30f, 1.5f, 0.75f, 25f), first.HitPitch, 4);

        // 10ms later: window closed → NO beep, but the 20 new damage is BANKED (the old port dropped it).
        HitSoundCues second = Step(l, now: 0.11f, hitTime: 2f, total: 50f, mode: 2);
        Assert.False(second.PlayHit);
        Assert.True(second.NewDamage);
        Assert.Equal(20f, l.UnaccountedDamage, 3);

        // window reopens → one beep whose pitch reflects the whole banked amount.
        HitSoundCues third = Step(l, now: 0.16f, hitTime: 2f, total: 50f, mode: 2);
        Assert.True(third.PlayHit);
        Assert.Equal(HitSoundLogic.ComputePitch(2, 20f, 1.5f, 0.75f, 25f), third.HitPitch, 4);
        Assert.Equal(0f, l.UnaccountedDamage, 3);
    }

    [Fact]
    public void HitSound_TypeHit_And_Kill_Play_Even_With_cl_hitsound_Off()
    {
        // QC gates only the BEEP on cl_hitsound; typehit/kill are unconditional.
        var l = new HitSoundLogic();
        Step(l, now: 0f, mode: 0); // seed
        HitSoundCues c = Step(l, now: 0.1f, killTime: 5f, mode: 0);
        Assert.True(c.PlayKill);
        Assert.False(c.PlayHit);

        c = Step(l, now: 0.2f, killTime: 5f, typehitTime: 6f, mode: 0);
        Assert.True(c.PlayTypeHit);
    }

    [Fact]
    public void HitSound_Kills_Inside_The_Antispam_Window_Collapse_To_One_Sound()
    {
        // The compare runs on the SERVER stat times (view.qc:973): a second kill 30ms after the first is
        // silent, and the skipped advance does NOT move the baseline — a third kill 80ms after the first
        // plays again (QC's exact latch behavior).
        var l = new HitSoundLogic();
        Step(l, now: 0f); // seed
        Assert.True(Step(l, now: 0.1f, killTime: 5.00f).PlayKill);
        Assert.False(Step(l, now: 0.2f, killTime: 5.03f).PlayKill);
        Assert.True(Step(l, now: 0.3f, killTime: 5.08f).PlayKill);
    }

    [Fact]
    public void HitSound_Spectatee_Switch_Drops_Accumulated_Damage()
    {
        var l = new HitSoundLogic();
        Step(l, now: 0f); // seed (spectatee 0)
        Step(l, now: 0.01f, hitTime: 1f, total: 30f);          // banked inside the window
        Assert.Equal(30f, l.UnaccountedDamage, 3);
        Step(l, now: 0.02f, hitTime: 1f, total: 30f, spectatee: 2); // switch spectatee
        Assert.Equal(0f, l.UnaccountedDamage, 3);
        Assert.False(Step(l, now: 0.1f, hitTime: 1f, total: 30f, spectatee: 2).PlayHit);
    }

    [Fact]
    public void HitSound_Arc_Hack_Bypasses_The_Window_Only_At_Pitch_Modes()
    {
        var l = new HitSoundLogic();
        Step(l, now: 0f, mode: 2); // seed
        // window still closed (10ms), but holding the Arc at mode >= 2 plays anyway (QC view.qc:928).
        Assert.True(Step(l, now: 0.01f, hitTime: 1f, total: 10f, mode: 2, haveArc: true).PlayHit);
        // mode 1 does NOT get the bypass.
        Assert.False(Step(l, now: 0.02f, hitTime: 2f, total: 20f, mode: 1, haveArc: true).PlayHit);
    }

    [Fact]
    public void HitSound_Backward_Stat_Jump_Reseeds_Silently()
    {
        // A server restart / tracked-player swap shrinks the stats: no sound, and the next real advance plays.
        var l = new HitSoundLogic();
        Step(l, now: 0f); // seed
        Assert.True(Step(l, now: 0.1f, killTime: 50f).PlayKill);
        Assert.False(Step(l, now: 0.2f, killTime: 3f).PlayKill);      // backward → reseed
        Assert.True(Step(l, now: 0.3f, killTime: 3.06f).PlayKill);    // fresh kill after the reseed
    }

    [Theory]
    [InlineData(1, 100f, 1.0f)]   // mode 1: fixed
    [InlineData(2, 25f, 1.0f)]    // mode 2 at the nominal damage: pitch 1 (curve crosses (c, 1))
    [InlineData(2, 0f, 1.5f)]     // mode 2 at zero damage: max pitch (curve crosses (0, a))
    [InlineData(3, 0f, 0.75f)]    // mode 3 mirrors: zero damage → min pitch
    [InlineData(3, 25f, 1.25f)]   // mode 3 at nominal: mirror of 1.0 in (a-b)/2+b = 1.125 → 1.25
    public void HitSound_ComputePitch_Matches_The_QC_Curve(int mode, float damage, float expected)
        => Assert.Equal(expected, HitSoundLogic.ComputePitch(mode, damage, 1.5f, 0.75f, 25f), 3);

    [Fact]
    public void HitSound_ComputePitch_Approaches_MinPitch_For_Huge_Damage()
    {
        float pitch = HitSoundLogic.ComputePitch(2, 100000f, 1.5f, 0.75f, 25f);
        Assert.InRange(pitch, 0.75f, 0.76f);
    }

    // ===========================================================================================
    //  scaffolding
    // ===========================================================================================

    private sealed class DictCvars : ICvarService
    {
        private readonly Dictionary<string, string> _v = new();
        public float GetFloat(string name) => _v.TryGetValue(name, out var s) && float.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0f;
        public string GetString(string name) => _v.TryGetValue(name, out var s) ? s : "";
        public void Set(string name, string value) => _v[name] = value;
        public void Register(string name, string defaultValue, CvarFlags flags = CvarFlags.None) { if (!_v.ContainsKey(name)) _v[name] = defaultValue; }
    }

    private sealed class MinimalServices : IEngineServices
    {
        public MinimalServices(ICvarService cvars, IGameClock clock) { Cvars = cvars; Clock = clock; }
        public ITraceService Trace { get; } = new NullTrace();
        public IGameClock Clock { get; }
        public ICvarService Cvars { get; }
        public IEntityService Entities { get; } = new NullEntities();
        public ISoundService Sound { get; } = new NullSound();
        public IModelService Models { get; } = new NullModels();

        private sealed class NullTrace : ITraceService
        {
            public TraceResult Trace(Vector3 start, Vector3 mins, Vector3 maxs, Vector3 end, MoveFilter filter, Entity? ignore) => TraceResult.Miss(end);
            public int PointContents(Vector3 point) => 0;
            public bool CheckPvs(Vector3 viewpoint, Vector3 target) => true;
        }
        private sealed class NullEntities : IEntityService
        {
            public Entity Spawn() => new();
            public void Remove(Entity e) { }
            public void SetOrigin(Entity e, Vector3 origin) => e.Origin = origin;
            public void SetSize(Entity e, Vector3 mins, Vector3 maxs) { e.Mins = mins; e.Maxs = maxs; }
            public void SetModel(Entity e, string model) { }
            public IEnumerable<Entity> FindByClass(string className) => System.Array.Empty<Entity>();
            public IEnumerable<Entity> FindInRadius(Vector3 origin, float radius) => System.Array.Empty<Entity>();
        }
        private sealed class NullSound : ISoundService
        {
            public void Play(Entity e, SoundChannel channel, string sample, float volume = 1f, float attenuation = 1f, bool loop = false, float pitch = 1f) { }
            public void Stop(Entity e, SoundChannel channel) { }
        }
        private sealed class NullModels : IModelService
        {
            public bool TryGetTag(Entity e, string tagName, out Vector3 origin, out Vector3 forward, out Vector3 right, out Vector3 up)
            { origin = forward = right = up = Vector3.Zero; return false; }
            public void SetAttachment(Entity e, Entity parent, string tagName) { }
        }
    }
}
