using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;
using XonoticGodot.Net;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T61 — feedback / notification polish (the C# ports of common/deathtypes/all.{qh,inc},
/// common/mutators/mutator/status_effects/*, common/effects/qc/globalsound.{qh,qc}, and the
/// common/notifications/all.qc MSG_CHOICE replication + announcer queuetime plumbing).
///
/// These pin the four Godot-free subsystems that T61 added or hardened:
///  1. The deathtype registry + .message category (DeathTypes.IsMonster/IsTurret/IsVehicle) that replaces
///     the old substring obituary checks.
///  2. MSG_CHOICE per-client cvar replication (NotificationChoiceState / NotificationChoiceIndexer) +
///     verbose/terse selection via the existing NotificationSystem dispatch.
///  3. StatusEffects networking (ENT_CLIENT_STATUSEFFECTS bitmap Write/Read round-trip, the PERSISTENT
///     flag, and the apply/remove dirty-mark surfaced by DamageSystem.ApplyFrozen/ApplyBurning).
///  4. _GlobalSound VOICETYPE routing (PLAYERSOUND/TEAMRADIO/LASTATTACKER(_ONLY)/TAUNT/AUTOTAUNT + the
///     sv_taunt/sv_autotaunt/sv_gentle gates).
/// The announcer queue itself lives in the Godot-coupled HudNotifications (untestable headless); its
/// Common-side plumbing (Notification.Queuetime) is covered here.
/// GlobalState collection (process-global Api.Services + registries).
/// </summary>
[Collection("GlobalState")]
public class NotificationPolishTests : IDisposable
{
    private readonly IEngineServices _prevServices;
    private readonly RecordingSound _sound = new();
    private readonly DictCvars _cvars = new();
    private readonly MutableClock _clock = new();

    public NotificationPolishTests()
    {
        Notifications.RegisterAll();          // names + choice-index assignment
        DeathMessages.ResetChoiceArgCounts();
        DeathMessages.EnsureChoiceArgCounts();// backfill CHOICE/MULTI arg counts so a CHOICE Send validates
        StatusEffectsCatalog.RegisterAll();   // frozen/burning + the full effect set (for the bitmap ids)
        StatusEffectsCatalog.ResetNetworkState();

        _prevServices = Api.Services;
        Api.Services = new PolishServices(_sound, _cvars, _clock);
        _clock.Time = 100f;
    }

    public void Dispose()
    {
        StatusEffectsCatalog.ResetNetworkState();
        Api.Services = _prevServices;
    }

    // ============================================================================================
    //  (1) Deathtype registry + .message category (DEATH_ISMONSTER/ISTURRET/ISVEHICLE)
    // ============================================================================================

    [Fact]
    public void DeathTypes_Monster_Category_Detected()
    {
        Assert.True(DeathTypes.IsMonster(DeathTypes.MonsterSpider));
        Assert.True(DeathTypes.IsMonster(DeathTypes.MonsterGolemZap));
        Assert.Equal(DeathCategory.Monster, DeathTypes.CategoryOf(DeathTypes.MonsterWyvern));
        // a monster death is NOT classed as turret/vehicle.
        Assert.False(DeathTypes.IsTurret(DeathTypes.MonsterSpider));
        Assert.False(DeathTypes.IsVehicle(DeathTypes.MonsterSpider));
    }

    [Fact]
    public void DeathTypes_Turret_And_Vehicle_Match_The_Whole_Family_By_Prefix()
    {
        // the bare tags...
        Assert.True(DeathTypes.IsTurret(DeathTypes.Turret));
        Assert.True(DeathTypes.IsVehicle(DeathTypes.Vehicle));
        // ...and the per-turret / per-vehicle QC variants resolve via the name prefix (turret_*/vh_*).
        Assert.True(DeathTypes.IsTurret("turret_mlrs"));
        Assert.True(DeathTypes.IsTurret("turret_walk_rocket"));
        Assert.True(DeathTypes.IsVehicle("vh_waki_gun"));
        Assert.True(DeathTypes.IsVehicle("vh_raptor_bomb"));
        Assert.Equal(DeathCategory.Vehicle, DeathTypes.CategoryOf("vh_spiderbot_rocket"));
    }

    [Fact]
    public void DeathTypes_Plain_And_Weapon_Deaths_Have_No_Category()
    {
        Assert.Equal(DeathCategory.None, DeathTypes.CategoryOf(DeathTypes.Fall));
        Assert.Equal(DeathCategory.None, DeathTypes.CategoryOf(DeathTypes.FromWeapon("vortex")));
        Assert.False(DeathTypes.IsMonster(DeathTypes.Generic));
        Assert.False(DeathTypes.IsTurret(DeathTypes.FromWeapon("rocketlauncher")));
        // category survives a HITTYPE suffix (the registry keys on the base tag).
        Assert.True(DeathTypes.IsMonster(DeathTypes.WithHitType(DeathTypes.MonsterMage, DeathTypes.Splash)));
    }

    // ============================================================================================
    //  (2) MSG_CHOICE per-client replication + verbose/terse selection
    // ============================================================================================

    [Fact]
    public void Choice_Indices_Are_Unique_And_Within_The_Max()
    {
        int count = NotificationChoiceIndexer.AssignChoiceIndices();
        Assert.True(count > 0);

        var seen = new HashSet<int>();
        foreach (var n in Notifications.All)
        {
            if (n.Type != MsgType.Choice) continue;
            Assert.InRange(n.ChoiceIdx, 0, NotificationChoiceState.NotifChoiceMax - 1);
            // Each non-team choice owns its own index in this port (no MULTITEAM index sharing).
            Assert.DoesNotContain(n.ChoiceIdx, seen);
            seen.Add(n.ChoiceIdx);
        }
    }

    [Fact]
    public void ReplicateFromCvars_Reads_Terse_And_Verbose_Per_Choice()
    {
        // FRAG is a real registered CHOICE (terse DEATH_MURDER_FRAG vs verbose ..._VERBOSE).
        var frag = Notifications.ByName(MsgType.Choice, "FRAG");
        Assert.NotNull(frag);

        var state = new NotificationChoiceState();
        // the client requests the verbose phrasing for FRAG via its notification_FRAG cvar = 2.
        var cvars = new Dictionary<string, int> { ["notification_FRAG"] = NotificationChoiceState.OptionB };
        state.ReplicateFromCvars(name => cvars.TryGetValue(name, out int v) ? v : (int?)null,
            defaultValue: NotificationChoiceState.OptionA);

        Assert.Equal(NotificationChoiceState.OptionB, state.Get(frag!.ChoiceIdx));
        // an un-set choice takes the terse default (option A).
        var typefrag = Notifications.ByName(MsgType.Choice, "TYPEFRAG")!;
        Assert.Equal(NotificationChoiceState.OptionA, state.Get(typefrag.ChoiceIdx));
    }

    [Fact]
    public void Choice_Selection_Dispatches_OptionB_When_Client_Picked_Verbose()
    {
        var rec = new NotificationSystem.RecordingSink();
        var prevSink = NotificationSystem.Sink;
        bool prevWarmup = NotificationSystem.WarmupStage;
        try
        {
            NotificationSystem.Sink = rec;
            NotificationSystem.WarmupStage = true; // FRAG is challow=Warmup -> the choice is honoured
            NotificationSystem.ChoiceValues.Clear();

            var fragChoice = Notifications.ByName(MsgType.Choice, "FRAG")!;

            // build the per-client state (verbose FRAG) and project it onto the dispatcher's map.
            var state = new NotificationChoiceState();
            state.Set(fragChoice.ChoiceIdx, NotificationChoiceState.OptionB);
            state.ApplyTo(NotificationSystem.ChoiceValues);

            var target = new Player { NetName = "P", Flags = EntFlags.Client };
            // build args matching the CHOICE's declared (max-of-options) shape so Send validates.
            var args = new List<object>();
            for (int i = 0; i < fragChoice.StringCount; i++) args.Add("Killer");
            for (int i = 0; i < fragChoice.FloatCount; i++) args.Add(0f);
            NotificationSystem.Send(NotifBroadcast.One, target, MsgType.Choice, "FRAG", args.ToArray());

            // option B (verbose) was dispatched, NOT the terse option A.
            Assert.Contains(rec.Log, d => d.Notification.RegistryName == "CENTER_DEATH_MURDER_FRAG_VERBOSE");
            Assert.DoesNotContain(rec.Log, d => d.Notification.RegistryName == "CENTER_DEATH_MURDER_FRAG");
        }
        finally
        {
            NotificationSystem.Sink = prevSink;
            NotificationSystem.WarmupStage = prevWarmup;
            NotificationSystem.ChoiceValues.Clear();
        }
    }

    // ============================================================================================
    //  (3) StatusEffects networking — bitmap round-trip, PERSISTENT, apply/remove dirty-mark
    // ============================================================================================

    [Fact]
    public void StatusEffects_Bitmap_RoundTrips_Time_And_Flags()
    {
        var e = new Entity();
        var burning = StatusEffectsCatalog.Burning!;
        var frozen = StatusEffectsCatalog.Frozen!;

        StatusEffectsCatalog.Apply(e, burning, duration: 5f, strength: 2f);   // sets ACTIVE, ExpireTime=105
        StatusEffectsCatalog.Apply(e, frozen, duration: 0f);                  // permanent freeze

        byte[] wire = StatusEffectsCatalog.Write(e);
        var decoded = StatusEffectsCatalog.Read(wire);

        Assert.True(decoded.ContainsKey(burning.RegistryId));
        Assert.True(decoded.ContainsKey(frozen.RegistryId));
        // the burning timer (105) round-trips; both carry the ACTIVE flag.
        Assert.Equal(105f, decoded[burning.RegistryId].Time, 3);
        Assert.True(decoded[burning.RegistryId].Flags.HasFlag(StatusEffectFlags.Active));
        Assert.True(decoded[frozen.RegistryId].Flags.HasFlag(StatusEffectFlags.Active));
    }

    [Fact]
    public void StatusEffects_Persistent_Flag_Survives_The_Wire_And_Stops_Timeout()
    {
        var e = new Entity();
        var burning = StatusEffectsCatalog.Burning!;
        StatusEffectsCatalog.Apply(e, burning, duration: 5f, strength: 1f); // expires at t=105

        // Drive PERSISTENT through the QC m_tick BITSET path, NOT a manual flag: m_tick recomputes the
        // flag from m_persistent() every tick (sv_status_effects.qc:16), so the only Base-faithful way to
        // carry PERSISTENT is to satisfy Burning.m_persistent (burning.qc:9-12 — lava burn while in lava).
        _cvars.Set("g_balance_contents_playerdamage_lava_burn", "1");
        e.WaterLevel = 1;
        e.WaterType = (int)Contents.Lava;
        StatusEffectsCatalog.Tick(e, _clock.Time); // m_tick sets PERSISTENT from m_persistent()
        Assert.True(StatusEffectsCatalog.FlagsOf(e, burning).HasFlag(StatusEffectFlags.Persistent));

        // PERSISTENT survives the bitmap round-trip.
        var decoded = StatusEffectsCatalog.Read(StatusEffectsCatalog.Write(e));
        Assert.True(decoded[burning.RegistryId].Flags.HasFlag(StatusEffectFlags.Persistent));

        // a persistent effect is NOT removed on timeout (QC m_tick early-return).
        _clock.Time = 200f; // well past 105
        StatusEffectsCatalog.Tick(e, _clock.Time);
        Assert.True(StatusEffectsCatalog.Has(e, burning));

        // leaving the lava clears m_persistent -> the next tick recomputes PERSISTENT off and times it out.
        e.WaterLevel = 0;
        StatusEffectsCatalog.Tick(e, _clock.Time);
        Assert.False(StatusEffectsCatalog.Has(e, burning));
    }

    [Fact]
    public void ApplyFrozen_And_ApplyBurning_Mark_The_Entity_Dirty_And_Flush_Clears_It()
    {
        var e = new Entity();
        StatusEffectsCatalog.ResetNetworkState();
        Assert.False(StatusEffectsCatalog.IsDirty(e));

        Assert.True(DamageSystem.ApplyFrozen(e, duration: 0f));
        Assert.True(StatusEffectsCatalog.IsDirty(e)); // QC StatusEffects_update set SendFlags

        // a flush emits the snapshot and clears the dirty mark (QC the per-frame Net_LinkEntity send).
        byte[]? snap = StatusEffectsCatalog.Flush(e);
        Assert.NotNull(snap);
        Assert.False(StatusEffectsCatalog.IsDirty(e));

        // burning re-marks it dirty.
        Assert.True(DamageSystem.ApplyBurning(e, duration: 3f, strength: 1f));
        Assert.True(StatusEffectsCatalog.IsDirty(e));

        // an entity with no pending update flushes null.
        StatusEffectsCatalog.Flush(e);
        Assert.Null(StatusEffectsCatalog.Flush(e));
    }

    // --------------------------------------------------------------------------------------------
    //  (3b) ENT_CLIENT_STATUSEFFECTS over the entity-delta wire (A5 #3/#7): the remote channel that
    //       carries an entity's status-effect bitmap so the client drives the burning/frozen overlays.
    //       The isolated bitmap Write/Read is covered above; these pin the NetEntityState delta field
    //       and the full server→client snapshot round-trip (the seam ServerNet/ClientNet actually use).
    // --------------------------------------------------------------------------------------------

    [Fact]
    public void StatusEffects_Blob_RoundTrips_Through_The_Entity_Delta_Codec()
    {
        var e = new Entity();
        var burning = StatusEffectsCatalog.Burning!;
        StatusEffectsCatalog.Apply(e, burning, duration: 5f, strength: 2f); // ExpireTime = 105

        // A spawn delta (baseline = Empty) carrying the status-effect blob.
        var baseline = NetEntityState.Empty(7);
        var current = baseline;
        current.Kind = NetEntityKind.Player;
        current.StatusEffects = StatusEffectsCatalog.Write(e);

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, current);
        // The widened (bit 16) StatusEffects field is in the mask — proving the uint mask carries a >15 bit.
        Assert.True(mask.HasFlag(EntityField.StatusEffects));

        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, baseline);
        Assert.False(r.BadRead);
        // The blob survived the wire and decodes back to the burning effect with its timer.
        var decoded = StatusEffectsCatalog.Read(got.StatusEffects!);
        Assert.True(decoded.ContainsKey(burning.RegistryId));
        Assert.Equal(105f, decoded[burning.RegistryId].Time, 3);
        Assert.True(decoded[burning.RegistryId].Flags.HasFlag(StatusEffectFlags.Active));
    }

    [Fact]
    public void StatusEffects_Blob_Stays_Off_The_Wire_When_Unchanged()
    {
        var e = new Entity();
        StatusEffectsCatalog.Apply(e, StatusEffectsCatalog.Burning!, duration: 5f);
        byte[] blob = StatusEffectsCatalog.Write(e);

        // Same effect bitmap CONTENT both frames (a distinct array with identical bytes — not the same reference),
        // only the origin moved: the delta must NOT re-send the blob (it's compared by content, not reference).
        var baseline = new NetEntityState { EntNum = 7, Kind = NetEntityKind.Player, StatusEffects = blob };
        var moved = baseline;
        moved.StatusEffects = (byte[])blob.Clone();
        moved.Origin = new Vector3(8f, 0f, 0f);

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, moved);
        Assert.Equal(EntityField.Origin, mask);
        Assert.False(mask.HasFlag(EntityField.StatusEffects));
    }

    [Fact]
    public void Remote_Player_Marked_Burning_On_The_Server_Shows_Burning_Client_Side()
    {
        var burning = StatusEffectsCatalog.Burning!;
        const int netId = 7;

        // Server side: a remote player is set burning; BuildEntitySet would pack its bitmap into the snapshot.
        var p = new Entity();
        StatusEffectsCatalog.Apply(p, burning, duration: 5f, strength: 1f);

        var server = new ServerSnapshotHistory();
        var client = new ClientSnapshotHistory();

        var burningSnap = new NetEntityState
        {
            EntNum = netId, Kind = NetEntityKind.Player, StatusEffects = StatusEffectsCatalog.Write(p),
        };
        var set1 = new Dictionary<int, NetEntityState> { [netId] = burningSnap };

        // Encode on the server, decode on the client — the exact ServerNet/ClientNet snapshot seam.
        var w1 = new BitWriter();
        server.EncodeSnapshot(w1, set1, snapshotSeq: 1);
        var r1 = new BitReader(w1.WrittenSpan);
        IReadOnlyDictionary<int, NetEntityState>? dec1 = client.DecodeSnapshot(ref r1);

        Assert.NotNull(dec1);
        NetEntityState got = dec1![netId];
        var decodedEffects = StatusEffectsCatalog.Read(got.StatusEffects!);
        // The client sees the remote player as burning — the whole point of the channel.
        Assert.True(decodedEffects.ContainsKey(burning.RegistryId));

        // ...and when the server clears the effects, the client drops them too (the delta carries the clear as an
        // empty bitmap against the acked baseline). The client acks seq 1, then the server sends a cleared seq 2.
        server.Ack(1);
        var clearedSnap = burningSnap;
        clearedSnap.StatusEffects = null; // ServerNet's NormalizeStatusBlob collapses "no effects" to null
        var set2 = new Dictionary<int, NetEntityState> { [netId] = clearedSnap };

        var w2 = new BitWriter();
        server.EncodeSnapshot(w2, set2, snapshotSeq: 2);
        var r2 = new BitReader(w2.WrittenSpan);
        IReadOnlyDictionary<int, NetEntityState>? dec2 = client.DecodeSnapshot(ref r2);

        Assert.NotNull(dec2);
        NetEntityState gotCleared = dec2![netId];
        // The field was present in the delta (cleared) → an empty bitmap → no burning client-side.
        var afterClear = StatusEffectsCatalog.Read(gotCleared.StatusEffects ?? System.Array.Empty<byte>());
        Assert.False(afterClear.ContainsKey(burning.RegistryId));
    }

    // ============================================================================================
    //  (4) _GlobalSound VOICETYPE routing + sv_taunt/sv_autotaunt/sv_gentle gates
    // ============================================================================================

    [Fact]
    public void VoiceMessage_VoiceType_Lookup_Matches_The_QC_Table()
    {
        Assert.Equal(VoiceType.TeamRadio, VoiceMessages.VoiceTypeOf("attack"));
        Assert.Equal(VoiceType.Taunt, VoiceMessages.VoiceTypeOf("taunt"));
        Assert.Equal(VoiceType.LastAttacker, VoiceMessages.VoiceTypeOf("teamshoot"));
        // an unknown id routes like a taunt (QC default field == playersound_taunt).
        Assert.Equal(VoiceType.Taunt, VoiceMessages.VoiceTypeOf("not_a_real_message"));
    }

    [Fact]
    public void TeamRadio_Plays_Only_To_Same_Team_Recipients()
    {
        var speaker = new Player { NetName = "Red1", Flags = EntFlags.Client, Team = Teams.Red };
        var mate = new Player { NetName = "Red2", Flags = EntFlags.Client, Team = Teams.Red };
        var foe = new Player { NetName = "Blue1", Flags = EntFlags.Client, Team = Teams.Blue };

        _sound.Played.Clear();
        SoundSystem.GlobalSound(speaker, "attack", new List<Entity> { speaker, mate, foe });

        // QC: FOREACH_CLIENT(SAME_TEAM(it, this)) — the mate hears it, the foe does NOT.
        Assert.Contains(_sound.Played, s => ReferenceEquals(s.E, mate));
        Assert.DoesNotContain(_sound.Played, s => ReferenceEquals(s.E, foe));
    }

    [Fact]
    public void LastAttacker_Plays_To_The_Pusher_And_The_Speaker_Only_Plays_To_Pusher()
    {
        var speaker = new Player { NetName = "Hit", Flags = EntFlags.Client };
        var attacker = new Player { NetName = "Atk", Flags = EntFlags.Client };
        speaker.Pusher = attacker;

        _sound.Played.Clear();
        // teamshoot is VOICETYPE_LASTATTACKER -> heard by the attacker AND the speaker.
        SoundSystem.GlobalSound(speaker, "teamshoot");
        Assert.Contains(_sound.Played, s => ReferenceEquals(s.E, attacker));
        Assert.Contains(_sound.Played, s => ReferenceEquals(s.E, speaker));

        _sound.Played.Clear();
        // LASTATTACKER_ONLY -> the attacker hears it, the speaker does NOT.
        SoundSystem._GlobalSound(speaker, "teamshoot", VoiceType.LastAttackerOnly, recipients: null);
        Assert.Contains(_sound.Played, s => ReferenceEquals(s.E, attacker));
        Assert.DoesNotContain(_sound.Played, s => ReferenceEquals(s.E, speaker));
    }

    [Fact]
    public void Taunt_Is_Gated_By_sv_taunt_And_Suppressed_By_sv_gentle()
    {
        var speaker = new Player { NetName = "T", Flags = EntFlags.Client };
        var listener = new Player { NetName = "L", Flags = EntFlags.Client };
        var roster = new List<Entity> { speaker, listener };

        // sv_taunt off -> no taunt at all.
        _cvars.Set("sv_taunt", "0");
        _cvars.Set("sv_gentle", "0");
        _sound.Played.Clear();
        SoundSystem.GlobalSound(speaker, "taunt", roster);
        Assert.Empty(_sound.Played);

        // sv_taunt on, sv_gentle off -> the taunt broadcasts.
        _cvars.Set("sv_taunt", "1");
        _sound.Played.Clear();
        SoundSystem.GlobalSound(speaker, "taunt", roster);
        Assert.NotEmpty(_sound.Played);

        // sv_gentle on -> taunt suppressed even with sv_taunt on.
        _cvars.Set("sv_gentle", "1");
        _sound.Played.Clear();
        SoundSystem.GlobalSound(speaker, "taunt", roster);
        Assert.Empty(_sound.Played);
    }

    [Fact]
    public void AutoTaunt_Requires_sv_autotaunt()
    {
        var speaker = new Player { NetName = "T", Flags = EntFlags.Client };
        var roster = new List<Entity> { speaker };

        // sv_autotaunt off -> nothing, regardless of sv_taunt.
        _cvars.Set("sv_autotaunt", "0");
        _cvars.Set("sv_taunt", "1");
        _cvars.Set("sv_gentle", "0");
        _sound.Played.Clear();
        SoundSystem._GlobalSound(speaker, "taunt", VoiceType.AutoTaunt, roster);
        Assert.Empty(_sound.Played);

        // sv_autotaunt on (and sv_taunt on, sv_gentle off) -> it plays.
        _cvars.Set("sv_autotaunt", "1");
        _sound.Played.Clear();
        SoundSystem._GlobalSound(speaker, "taunt", VoiceType.AutoTaunt, roster);
        Assert.NotEmpty(_sound.Played);
    }

    // ============================================================================================
    //  Announcer queuetime plumbing (Common-side; the queue itself is Godot-coupled)
    // ============================================================================================

    [Fact]
    public void Announcer_Notifications_Carry_A_Queuetime_Field()
    {
        // The Annce builder threads queuetime onto the notification (QC nent_queuetime); the field exists and
        // is wired so HudNotifications can space the queue. A representative announcer round-trips a value.
        var n = Notifications.Annce("T61_TEST_ANNCE", "headshot", queuetime: -1f);
        Assert.Equal(MsgType.Annce, n.Type);
        Assert.Equal(-1f, n.Queuetime);
    }

    // ============================================================================================
    //  scaffolding
    // ============================================================================================

    private sealed class RecordingSound : ISoundService
    {
        public readonly record struct Entry(Entity E, SoundChannel Channel, string Sample, float Volume, float Attenuation);
        public readonly List<Entry> Played = new();
        public void Play(Entity e, SoundChannel channel, string sample, float volume = 1f, float attenuation = 1f, bool loop = false, float pitch = 1f)
            => Played.Add(new Entry(e, channel, sample, volume, attenuation));
        public void Stop(Entity e, SoundChannel channel) { }
    }

    private sealed class DictCvars : ICvarService
    {
        private readonly Dictionary<string, string> _v = new();
        public float GetFloat(string name) => _v.TryGetValue(name, out var s) && float.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0f;
        public string GetString(string name) => _v.TryGetValue(name, out var s) ? s : "";
        public void Set(string name, string value) => _v[name] = value;
        public void Register(string name, string defaultValue, CvarFlags flags = CvarFlags.None) { if (!_v.ContainsKey(name)) _v[name] = defaultValue; }
    }

    private sealed class PolishServices : IEngineServices
    {
        public PolishServices(ISoundService sound, ICvarService cvars, IGameClock clock)
        { Sound = sound; Cvars = cvars; Clock = clock; }
        public ITraceService Trace { get; } = new NullTrace();
        public IGameClock Clock { get; }
        public ICvarService Cvars { get; }
        public IEntityService Entities { get; } = new NullEntities();
        public ISoundService Sound { get; }
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
        private sealed class NullModels : IModelService
        {
            public bool TryGetTag(Entity e, string tagName, out Vector3 origin, out Vector3 forward, out Vector3 right, out Vector3 up)
            { origin = forward = right = up = Vector3.Zero; return false; }
            public void SetAttachment(Entity e, Entity parent, string tagName) { }
        }
    }
}
