// Port of qcsrc/common/mapobjects/target/music.qc (target_music / trigger_music).
//
// The Xonotic music system has three priority layers (from QC TargetMusic_Advance):
//   1. cdtrack (default) — the map's background music from its .mapinfo cdtrack line
//   2. target_music (targeted) — a point entity triggered on/off that overrides the default
//   3. trigger_music (brush volume) — highest priority, plays when the player is inside the brush
//
// On the SERVER side, these entities simply record their track/volume/fade parameters and respond to
// triggers (target_music) or touch events (trigger_music). The actual music PLAYBACK is client-side
// (MusicPlayer.cs in game/client/) — the server communicates music state to the client via the
// MusicState structure exposed on GameWorld, which the net layer / listen-server bridge reads.
//
// For the listen-server path (the current primary play path), target_music and trigger_music state is
// read directly by the client MusicPlayer each frame (no networking needed — shared process).

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// <c>target_music</c> and <c>trigger_music</c> — map-entity music overrides. Registered by
/// <see cref="MapObjectsRegistry"/>.
/// </summary>
public static class TargetMusic
{
    // ========================================================================
    //  target_music — point entity, triggered on/off
    // ========================================================================

    /// <summary><c>spawnfunc(target_music)</c> — a triggered music override (or the map default when untargetable).</summary>
    public static void TargetMusicSetup(Entity this_)
    {
        this_.ClassName = "target_music";

        // Default volume to 1 if unset.
        if (this_.Volume == 0f)
            this_.Volume = 1f;

        // QC spawnfunc(target_music) installs both this.use AND this.reset unconditionally; the round handler
        // fires this.reset(this) on every map reset. target_music_reset re-asserts the untargeted DEFAULT
        // (QC target_music_sendto(MSG_ALL,true)) — and here also clears any lifetime-0 default-slot claim a
        // triggered track made (MusicActivationTime), so the round restart resumes the map's own default music.
        this_.Reset = TargetMusicReset;

        // If no targetname, this is the DEFAULT music for the map (same role as cdtrack).
        // If it HAS a targetname, it's a triggered override.
        if (!string.IsNullOrEmpty(this_.TargetName))
        {
            this_.Use = TargetMusicUse;
            this_.Active = MapMover.ActiveNot; // starts inactive; triggered to activate
            MapMover.IndexRegister(this_);
        }
        else
        {
            // Default music: always active. This overrides cdtrack if present.
            this_.Active = MapMover.ActiveActive;
            MapMover.IndexRegister(this_);
        }
    }

    /// <summary>
    /// Port of <c>target_music_reset</c> (music.qc:41-47): the round-restart hook fired by the round handler on
    /// every entity. QC re-sends the untargeted default track to all clients; a TRIGGERED override (targetname
    /// set) does nothing there. In the listen-server model the MusicPlayer reads live state, so the only state
    /// to undo on reset is a triggered track's activation stamp — clearing it drops both its lifetime window and
    /// any lifetime-0 default-slot claim, so the map's own untargeted default resumes (matching QC's behaviour
    /// where the per-activator override is not re-sent on reset and the default reclaims the slot).
    /// </summary>
    private static void TargetMusicReset(Entity self)
    {
        if (!string.IsNullOrEmpty(self.TargetName))
        {
            self.Active = MapMover.ActiveNot;       // a triggered override goes silent again until re-used
            self.MusicActivationTime = -1f;         // clear the lifetime window / default-slot claim
        }
        else
        {
            self.Active = MapMover.ActiveActive;    // the untargeted default re-asserts (QC sendto MSG_ALL,true)
        }
    }

    /// <summary>
    /// Port of <c>target_music_use</c> (music.qc:59-72): each trigger RE-SENDS the track to the activator —
    /// it never toggles off. The override then lives for <see cref="Entity.MusicLifetime"/> seconds
    /// (client-side: <c>e.lifetime = time + tim</c> in Net_TargetMusic, evaluated by TargetMusic_Advance);
    /// with lifetime 0 the track instead REPLACES the map-default slot permanently. The activation time is
    /// stamped here and the client MusicPlayer evaluates the window against it.
    /// (The previous port behavior — toggling Active off on the second use — had no QC counterpart.)
    /// </summary>
    private static void TargetMusicUse(Entity self, Entity activator)
    {
        // QC target_music_use: `if(!actor) return;` then only sends to a REAL client actor (+ its spectators).
        // The port's MusicPlayer is global to the single listen-server local player, so we don't (and can't)
        // route per-activator, but we keep QC's gates: a null/non-client trigger source must NOT activate the
        // override (e.g. a logic relay with no carried activator) — only a real player triggering it does.
        if (activator is null)
            return;
        if ((activator.Flags & EntFlags.Client) == 0)
            return;

        self.Active = MapMover.ActiveActive;
        self.MusicActivationTime = MapMover.Now();
    }

    // ========================================================================
    //  trigger_music — brush volume, plays when player is inside
    // ========================================================================

    /// <summary><c>spawnfunc(trigger_music)</c> — a brush volume that plays music when the player is inside.</summary>
    public static void TriggerMusicSetup(Entity this_)
    {
        this_.ClassName = "trigger_music";

        // Default volume to 1 if unset.
        if (this_.Volume == 0f)
            this_.Volume = 1f;

        // Initialize as a trigger volume (brush model for the overlap test).
        MapMover.InitTrigger(this_);

        // Touch handler: the MusicPlayer reads the touch state each frame to determine if the player is
        // inside any trigger_music volume. We set the Touch delegate so the touch system fires it.
        this_.Touch = TriggerMusicTouch;

        // QC spawnfunc(trigger_music) installs use + reset UNCONDITIONALLY (use = generic_netlinked_legacy_use,
        // reset = trigger_music_reset) and then CALLS this.reset(this) to apply the initial active state from the
        // START_DISABLED spawnflag. Mirror that: the use handler toggles active for relay_activate/deactivate
        // (works even on an untargeted volume, matching QC), and reset re-applies START_DISABLED on round restart.
        this_.Use = TriggerMusicActivateUse;
        this_.Reset = TriggerMusicReset;
        TriggerMusicReset(this_);   // QC: this.reset(this) — set the initial active state

        // Register so relay_activate / relay_deactivate can find it by targetname.
        MapMover.IndexRegister(this_);
    }

    /// <summary>
    /// Port of <c>trigger_music_reset</c> (music.qc:135-145): set active from the START_DISABLED spawnflag
    /// (BIT(0)=1). Called at spawn (QC's <c>this.reset(this)</c>) and on every round restart by the round handler,
    /// so a volume toggled off mid-round by a relay returns to its mapped default state.
    /// </summary>
    private static void TriggerMusicReset(Entity self)
    {
        // START_DISABLED (spawnflag 1) starts the volume inactive; otherwise active.
        self.Active = (self.SpawnFlags & 1) != 0 ? MapMover.ActiveNot : MapMover.ActiveActive;
    }

    /// <summary>
    /// Touch handler for trigger_music. Called by the touch system when a player overlaps this brush volume.
    /// We mark the entity with a timestamp so the client MusicPlayer can detect "player is inside this volume
    /// THIS frame" — the standard DP pattern where a touch fires every physics frame the overlap holds.
    /// </summary>
    private static void TriggerMusicTouch(Entity self, Entity toucher)
    {
        if (self.Active != MapMover.ActiveActive)
            return;

        // Only react to players (not projectiles/items).
        if ((toucher.Flags & EntFlags.Client) != 0)
        {
            // Record the touch time on the trigger entity. The MusicPlayer scans all trigger_music entities
            // each frame and considers one "active" if its LastTouchTime is within the current frame window.
            if (Api.Services is not null)
                self.PushLTime = Api.Clock.Time;
        }
    }

    /// <summary>Use handler for relay_activate/deactivate toggling on trigger_music.</summary>
    private static void TriggerMusicActivateUse(Entity self, Entity activator)
    {
        // Toggle active state (relay_activate / relay_deactivate / relay_activatetoggle).
        if (self.Active == MapMover.ActiveActive)
            self.Active = MapMover.ActiveNot;
        else
            self.Active = MapMover.ActiveActive;
    }
}
