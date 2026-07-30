// Port of Base/data/xonotic-data.pk3dir/qcsrc/client/shownames.qc
// (Draw_ShowNames / Draw_ShowNames_All) — the floating player name + health/armor tags.
using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Framework;          // MoveFilter, Entity
using VortexArena.Common.Gameplay;          // Teams
using VortexArena.Common.Services;           // Api, ITraceService, TraceResult
using VortexArena.Game.Hud;                  // HudPanel.HudFont, HudText
using VortexArena.Game.Net;                  // ClientNet
using VortexArena.Net;                       // NetEntityState / NetEntityKind / NetEntityFlags
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>
/// The floating player name + health/armor tags drawn above each visible player — the C# port of QuakeC's
/// <c>Draw_ShowNames</c> / <c>Draw_ShowNames_All</c> (<c>client/shownames.qc</c>). In QC a per-player
/// <c>shownames_tag</c> entity tracked each client's networked <c>entcs</c> slice (origin / health / armor /
/// team / name) and, every frame, projected its origin through the view (<c>project_3d_to_2d</c>) to draw a
/// fading name tag with an optional teammate health/armor status bar, filtered by team, line-of-sight, screen
/// presence, distance and (for enemies) a crosshair-distance / anti-overlap gate.
///
/// <para>This is the in-world overlay twin of <see cref="WaypointSpriteLayer"/>: a plain <see cref="Control"/>
/// that never eats input, projecting each player's world position through the host's first-person
/// <see cref="Camera3D"/>. The per-player networked slice arrives through <see cref="ClientNet"/> the same way
/// the radar reads it — <see cref="ClientNet.RemoteIds"/> → <see cref="ClientNet.SampleRemote"/> (interpolated
/// pose) + <see cref="ClientNet.TryGetRemoteState"/> (kind / team-colormap / health / armor / dead flag) — and
/// the display NAME comes from the scoreboard name slice (the port's faithful <c>entcs_GetName</c> stand-in;
/// see <see cref="NameResolver"/>). The local player's own tag is handled exactly like QC (only in chase /
/// spectate, gated by <c>hud_shownames_self</c>).</para>
///
/// <para>Per-tag fade state (the QC <c>shownames_tag</c> <c>.alpha</c>/<c>.fadedelay</c>/<c>.pointtime</c>
/// fields) is kept here in <see cref="TagState"/>, keyed by the player's net id, so the alpha ramps mirror the
/// QC speed/delay constants exactly. Anti-overlap (the closer of two overlapping tags wins) is the same
/// box-overlap test over the per-tag screen boxes computed this frame.</para>
/// </summary>
public partial class ShowNamesLayer : Control
{
    // QC client/shownames.qc:38-39
    private const float SHOWNAMES_FADESPEED = 4f;
    private const float SHOWNAMES_FADEDELAY = 0f;

    // QC ALPHA_MIN_VISIBLE (common/constants.qh) — a tag dimmer than this isn't drawn.
    private const float AlphaMinVisible = 0.003f;

    // QC max_shot_distance (the engine's absolute trace cap; Xonotic uses 32768). Used as the hard cull distance
    // and the min() ceiling on hud_shownames_maxdistance, exactly like Draw_ShowNames.
    private const float MaxShotDistance = 32768f;

    /// <summary>The host's first-person camera (QC the view), for projecting world positions to screen
    /// (QC <c>project_3d_to_2d</c>) and as the LOS trace / distance origin (<c>view_origin</c>).</summary>
    public Camera3D? Camera { get; set; }

    /// <summary>The net source. Supplies the remote player set + each one's interpolated pose and networked
    /// health/armor/team/dead slice (QC the <c>entcs</c> receiver entities).</summary>
    public ClientNet? Net { get; set; }

    /// <summary>The local client's team (QC the local <c>entcs</c> team) — drives the <c>sameteam</c> branch
    /// (teammate tags fade in unconditionally + may show the status bar; enemies obey the enemy gates). A listen
    /// server feeds the local server Player's team; a pure client derives it from the local scoreboard row.
    /// ⚠ In FFA this is NON-ZERO (the pants-color-derived team the port carries on every player — the #27 trap),
    /// so it must NEVER be used as a teamplay test on its own: <see cref="Teamplay"/> gates every compare.</summary>
    public int LocalTeam { get; set; }

    /// <summary>Whether the ACTIVE GAMETYPE is team-based (QC <c>teamplay</c>, networked via the ScoreInfo block
    /// → <c>GameScores.Teamplay</c>). THE gate for every team compare in this layer — playtest #55's root cause
    /// was deriving this from <c>LocalTeam != None</c>: in FFA every player carries a pants-color-derived team,
    /// so players sharing profile colors classified as TEAMMATES (LOS trace skipped, tags always visible,
    /// through-wall + status bar). Exactly the #27 friendly-fire trap — same rule: gate at the call site on the
    /// real teamplay flag, never inside a team-field compare.</summary>
    public bool Teamplay { get; set; }

    /// <summary>Whether the local player is in third-person (QC <c>autocvar_chase_active</c> / event-chase). The
    /// own-name tag only draws in chase, exactly like <c>Draw_ShowNames</c>'s self branch.</summary>
    public bool ChaseActive { get; set; }

    /// <summary>The local client's net id (QC <c>player_localentnum</c>). Used to pick out the local player's own
    /// tag (drawn only in chase, gated by <c>hud_shownames_self</c>) and to apply the QC enemy/self model-alpha
    /// factor (the <c>this.sv_entnum == player_localentnum</c> arm of the entcs_GetAlpha branch).</summary>
    public int LocalNetId { get; set; }

    /// <summary>The net id of the player the local client is currently VIEWING (QC <c>current_player + 1</c>): the
    /// followed spectatee when spectating, else the local player. When this id is a remote in
    /// <see cref="ClientNet.RemoteIds"/> (the spectatee case), its tag takes the QC self/spectatee branch — drawn
    /// only with <c>hud_shownames_self</c> on, OR inside the 1-second spectatee-switch grace window.</summary>
    public int CurrentViewedNetId { get; set; }

    /// <summary>QC <c>spectatee_status</c> (0 = playing, &gt;0 = following a player). Together with
    /// <see cref="SpectateeStatusChangedTime"/> it drives the 1-second grace window in which a freshly-followed
    /// player's own tag stays visible even with <c>hud_shownames_self</c> off (<c>Draw_ShowNames</c>:48).</summary>
    public int SpectateeStatus { get; set; }

    /// <summary>QC <c>spectatee_status_changed_time</c>: the client time at which <see cref="SpectateeStatus"/> last
    /// changed (drives the 1s grace window).</summary>
    public float SpectateeStatusChangedTime { get; set; }

    /// <summary>Whether the local client is NOT a live in-game player — observing (free-fly) or following a
    /// spectatee (QC <c>spectatee_status != 0</c> ⇔ <c>!(IS_PLAYER(to) || INGAME(to))</c>). The server gives such a
    /// recipient EVERY player's private entcs slice (<c>_entcs_send</c>: the
    /// <c>!(IS_PLAYER(to) || INGAME(to))</c> arm), so on the client this forces <c>m_entcs_private</c> true for all
    /// players — free spectating sees everyone's team-membership + health/armor.</summary>
    public bool LocalIsSpectating { get; set; }

    /// <summary>Resolve a player's display name from its net id — the port's faithful <c>entcs_GetName</c>
    /// stand-in (the scoreboard name slice; the port has no separate entcs name stream). Returns "" when unknown;
    /// the tag then shows nothing for the name but still draws the status bar. Host-wired by the net layer.</summary>
    public Func<int, string>? NameResolver { get; set; }

    /// <summary>QC <c>autocvar_hud_panel_healtharmor_maxhealth</c> — the status-bar full-scale health. Read from
    /// the menu store live; fallback 200 (the shipped _hud_common.cfg:89 default) so the teammate bar scales like
    /// the QC HUD's (Draw_ShowNames divides healthvalue by this exact cvar).</summary>
    private static float MaxHealth => CvarF("hud_panel_healtharmor_maxhealth", 200f);

    /// <summary>QC <c>autocvar_hud_panel_healtharmor_maxarmor</c> — the status-bar full-scale armor (fallback 200,
    /// _hud_common.cfg:90).</summary>
    private static float MaxArmor => CvarF("hud_panel_healtharmor_maxarmor", 200f);

    /// <summary>Per-player tag fade/overlap state (QC the <c>shownames_tag</c> entity's mutable fields).</summary>
    private sealed class TagState
    {
        public float Alpha;          // QC .alpha — the fade ramp (0..1)
        public float FadeDelay;      // QC .fadedelay — absolute time the enemy fade-in may begin
        public float PointTime;      // QC .pointtime — last time the crosshair pointed at this player
        public bool HadFadeDelay;    // QC `if (!this.fadedelay)` seed guard (a real 0 is a valid value)
        // The screen-space anti-overlap box for THIS frame (QC .box_org / .box_ofs), and whether it's valid.
        public Vector2 BoxOrg;
        public Vector2 BoxOfs;
        public bool BoxValid;
        public NVec3 Origin;         // this frame's world origin (for the distance compare in anti-overlap)
        public int Touched;          // generation stamp so stale ids can be pruned

        // Draw params resolved in pass 1 and consumed in pass 2 (so the geometry is computed once).
        public Vector2 ScreenAnchor; // QC `o` (the projected origin + offset), screen px
        public float DrawAlpha;      // QC `a` after the distance fade
        public float Resize;         // QC resize factor
        public bool DrawStatus;      // teammate health/armor bar this frame
        public bool DrawDecolor;     // strip the name's own colors (decolorize rule)
        public int Health;           // entcs HEALTH slice (for the status bar)
        public int Armor;            // entcs ARMOR slice (for the status bar)
        public int TeamId;           // entcs team (s.Colormap & 0xFF) — drives the QC playername() team tint
    }

    private readonly Dictionary<int, TagState> _tags = new();
    private int _generation;

    // Scratch reused each frame to avoid per-frame allocs in the draw loop.
    private readonly List<int> _drawIds = new();

    public override void _Ready() => MouseFilter = MouseFilterEnum.Ignore; // QC hud_cursormode off

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        // QC Draw_ShowNames_All: `if (!autocvar_hud_shownames) return;`
        if (Camera is null || Net is null || !CvarBool("hud_shownames"))
            return;

        Vector2 vp = GetViewportRect().Size;
        float frametime = (float)GetProcessDeltaTime();
        float now = NowSec();
        NVec3 viewOrigin = Coords.ToQuake(Camera.GlobalPosition);

        float offset = CvarF("hud_shownames_offset", 52f);
        // [#55] THE authoritative teamplay flag (GameScores.Teamplay via the feed) — NOT `LocalTeam != None`:
        // FFA players carry a pants-derived non-zero team, so the old derivation turned DM into "everyone with
        // my profile colors is a teammate" (no LOS trace, tags always on). The #27 rule: gate on real teamplay.
        bool teamplay = Teamplay;

        // ---- pass 1: compute each visible player's fade alpha + screen box (QC Draw_ShowNames_All loop) ----
        _drawIds.Clear();
        _generation++;

        // Snapshot the remote-id set (RemoteIds is a live view over the net dict; a snapshot arriving mid-draw
        // would otherwise throw out of this every-frame path). Same defensive copy the radar takes.
        int[] ids = System.Linq.Enumerable.ToArray(Net.RemoteIds);

        foreach (int netId in ids)
        {
            if (!Net.TryGetRemoteState(netId, out NetEntityState s))
                continue;
            // QC: shownames only tracks PLAYERS (the entcs slice). Skip projectiles/items/gibs/nameplates.
            if (s.Kind != NetEntityKind.Player)
                continue;
            if (!Net.SampleRemote(netId, Net.LatestServerTime, out NVec3 origin, out _))
                continue;
            if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(origin.Z))
                continue;

            bool isSelf = netId == LocalNetId;
            // QC self branch (Draw_ShowNames:42 `if (this.sv_entnum == current_player + 1)` — "self or spectatee"):
            // the tag of the player you are VIEWING. When following a spectatee, current_player is that followed
            // player, who IS a remote in ClientNet.RemoteIds — so this branch genuinely runs for the spectatee tag.
            // (The truly-local predicted player is never in RemoteIds, so the chase own-tag still can't draw here;
            // that case is a deferral, but the spectatee arm below is live.)
            bool isViewed = netId == CurrentViewedNetId;
            if (isViewed)
            {
                if (!ChaseActive)
                    continue;
                // QC:47-51 — suppress unless hud_shownames_self, OR we're inside the 1s spectatee-switch grace
                // window (spectatee_status > 0 && time <= spectatee_status_changed_time + 1).
                bool graceWindow = SpectateeStatus > 0 && now <= SpectateeStatusChangedTime + 1f;
                if (!CvarBool("hud_shownames_self") && !graceWindow)
                    continue;
            }

            // QC entcs.m_entcs_private → it.sameteam (Draw_ShowNames_All:237-248). m_entcs_private is set on the
            // client iff the server sent this player's PRIVATE entcs slice (ent_cs.qc ReadEntcs:359 — the ENTNUM
            // sentinel bit), and the server (`_entcs_send`) sends it iff `radar_showenemies || SAME_TEAM(to,player)
            // || !(IS_PLAYER(to) || INGAME(to))`. Reproduce that exact gate here so the authority matches QC (the
            // old (Colormap&0xFF)==LocalTeam compare ignored radar_showenemies and the observer arm): a teammate by
            // colormap, OR radar_showenemies is set, OR the local client is observing/spectating (not a live
            // in-game player → gets everyone's private slice). Self never takes the sameteam arm in QC (the self
            // branch is handled above; the private slice for self is the !sameteam||self entcs_GetAlpha case).
            bool sameTeamColor = teamplay && LocalTeam != Teams.None && (s.Colormap & 0xFF) == LocalTeam;
            bool radarShowEnemies = CvarBool("radar_showenemies");
            bool entcsPrivate = !isSelf && (sameTeamColor || radarShowEnemies || LocalIsSpectating);
            // QC `it.sameteam = entcs.m_entcs_private` — same flag drives the team/enemy gate AND the status bar.
            bool sameteam = entcsPrivate;
            bool dead = (s.Flags & NetEntityFlags.Dead) != 0;

            // QC: `if (!this.sameteam && !autocvar_hud_shownames_enemies) return;`. The viewed player (self in chase
            // / the followed spectatee) already passed the self-branch above — in QC it carries the private entcs
            // slice (so sameteam) and is never enemy-gated here; exempt it the same way.
            if (!sameteam && !isViewed && !CvarBool("hud_shownames_enemies"))
                continue;

            TagState tag = GetTag(netId);
            tag.Touched = _generation;
            tag.Origin = origin;

            // ---- line of sight (QC traceline(view_origin, this.origin, MOVE_NOMONSTERS, this)) ----
            float crossDist = CvarF("hud_shownames_crosshairdistance", 0f);
            bool hit;
            if (crossDist == 0f && sameteam)
            {
                hit = true; // QC: teammates skip the LOS trace when no crosshair-distance gate is set
            }
            else
            {
                hit = TraceVisible(viewOrigin, origin);
            }

            // ---- project the tag origin (QC project_3d_to_2d(this.origin + eZ * offset)) ----
            NVec3 tagWorld = origin + new NVec3(0f, 0f, offset);
            bool onScreen = Project(tagWorld, out Vector2 o, out bool behind);
            // QC OFF_SCREEN(o): behind, or outside the viewport rect.
            bool offScreen = behind || !onScreen
                             || o.X < 0f || o.Y < 0f || o.X > vp.X || o.Y > vp.Y;

            // ---- crosshair-distance gate + overlap pre-state (QC the autocvar_hud_shownames_crosshairdistance block) ----
            int overlap = -1;
            if (crossDist != 0f)
            {
                float w = o.X - vp.X / 2f;
                float h = o.Y - vp.Y / 2f;
                if (crossDist * crossDist > w * w + h * h)
                    tag.PointTime = now;
                if (tag.PointTime + CvarF("hud_shownames_crosshairdistance_time", 5f) <= now)
                    overlap = 1;
                else if (!CvarBool("hud_shownames_crosshairdistance_antioverlap"))
                    overlap = 0;
            }

            // ---- anti-overlap (QC: a closer overlapping tag fades this one out) ----
            // QC reads each tag's PREVIOUS-frame box (this.box_org/it.box_org set the last time it was drawn) — so
            // we test the current tag's last-frame box (tag.BoxValid here is from the prior frame; it's recomputed
            // at the end of this iteration). Already-processed others in _drawIds carry this frame's box, which only
            // tightens the test by a frame; the fade converges within a frame either way (a visual refinement).
            if (overlap == -1 && CvarBool("hud_shownames_antioverlap") && tag.BoxValid)
            {
                foreach (int otherId in _drawIds)
                {
                    if (otherId == netId) continue;
                    TagState ot = _tags[otherId];
                    if (!ot.BoxValid) continue;
                    if (BoxesOverlap(tag.BoxOrg - tag.BoxOfs, tag.BoxOrg + tag.BoxOfs,
                                     ot.BoxOrg - ot.BoxOfs, ot.BoxOrg + ot.BoxOfs)
                        && Vlen2(ot.Origin - viewOrigin) < Vlen2(origin - viewOrigin))
                    {
                        overlap = 1;
                        break;
                    }
                }
            }

            // ---- the fade ramp (QC the big if/else alpha chain) ----
            if (!tag.HadFadeDelay) { tag.FadeDelay = now + SHOWNAMES_FADEDELAY; tag.HadFadeDelay = true; }
            if (dead) // dead player, fade out slowly
            {
                tag.Alpha = MathF.Max(0f, tag.Alpha - SHOWNAMES_FADESPEED * 0.25f * frametime);
            }
            else if (!sameteam && !hit) // view blocked, fade out
            {
                tag.Alpha = MathF.Max(0f, tag.Alpha - SHOWNAMES_FADESPEED * frametime);
                tag.HadFadeDelay = false; // reset fade-in delay, enemy has left the view (QC fadedelay = 0)
            }
            else if (offScreen) // out of view, fade out
            {
                tag.Alpha = MathF.Max(0f, tag.Alpha - SHOWNAMES_FADESPEED * frametime);
            }
            else if (overlap > 0) // tag overlap detected, fade toward the min alpha
            {
                float minalpha = CvarF("hud_shownames_antioverlap_minalpha", 0.4f);
                tag.Alpha = tag.Alpha >= minalpha
                    ? MathF.Max(minalpha, tag.Alpha - SHOWNAMES_FADESPEED * frametime)
                    : MathF.Min(minalpha, tag.Alpha + SHOWNAMES_FADESPEED * frametime);
            }
            else if (sameteam) // fade in for teammates
            {
                tag.Alpha = MathF.Min(1f, tag.Alpha + SHOWNAMES_FADESPEED * frametime);
            }
            else if (now > tag.FadeDelay || tag.Alpha > 0f) // fade in for enemies
            {
                tag.Alpha = MathF.Min(1f, tag.Alpha + SHOWNAMES_FADESPEED * frametime);
            }

            // QC:131-138 — a = autocvar_hud_shownames_alpha * this.alpha, then for enemies OR the local player the
            // tag alpha is scaled by entcs_GetAlpha(): the remote player's CSQC model render alpha, so a fading model
            // (spawn shield / invisibility / Cloaked / respawn fade) fades its tag with it. The port's per-entity
            // render alpha is NetEntityState.Alpha (0 = the default "opaque", networked as 1..254 = alpha/255).
            float a = CvarF("hud_shownames_alpha", 0.7f) * tag.Alpha;
            if (!sameteam || netId == LocalNetId)
            {
                // QC entcs_GetAlpha: model alpha; `if (f == 0) f = 1; if (f < 0) f = 0;`. NetEntityState.Alpha 0
                // means opaque (never networked) → f = 1; otherwise f = Alpha/255 (clamped 0..1).
                float f = s.Alpha == 0 ? 1f : s.Alpha / 255f;
                if (f < 0f) f = 0f;
                if (f > 1f) f = 1f;
                a *= f;
            }
            if (a < AlphaMinVisible)
            {
                tag.BoxValid = false;
                continue;
            }

            // ---- distance fade + cull (QC the maxdistance/mindistance block) ----
            float dist = Vlen(origin - viewOrigin);
            float maxDistCvar = CvarF("hud_shownames_maxdistance", 5000f);
            float minDist = CvarF("hud_shownames_mindistance", 1000f);
            if (maxDistCvar != 0f)
            {
                float maxDist = MathF.Min(maxDistCvar, MaxShotDistance);
                if (dist >= maxDist) { tag.BoxValid = false; continue; }
                if (dist >= minDist)
                {
                    float f = maxDistCvar - minDist;
                    if (f > 0f)
                        a *= (f - MathF.Max(0f, dist - minDist)) / f;
                }
            }
            else if (dist >= MaxShotDistance)
            {
                tag.BoxValid = false;
                continue;
            }
            if (a <= 0f || behind || !onScreen)
            {
                tag.BoxValid = false;
                continue;
            }

            // ---- resize (QC autocvar_hud_shownames_resize) ----
            float resize = 1f;
            if (CvarBool("hud_shownames_resize") && dist >= minDist)
            {
                float f = maxDistCvar - minDist;
                if (f > 0f)
                    resize = 0.5f + 0.5f * (f - MathF.Max(0f, dist - minDist)) / f;
            }

            // ---- the tag geometry (QC mySize / myPos) ----
            float fontsize = CvarF("hud_shownames_fontsize", 12f);
            float aspect = CvarF("hud_shownames_aspect", 8f);
            Vector2 mySize = new(aspect * fontsize, fontsize);
            Vector2 myPos = new(o.X - 0.5f * mySize.X, o.Y - mySize.Y);
            mySize.X *= resize;
            mySize.Y *= resize;
            // QC: myPos.x += 0.5 * (mySize.x / resize - mySize.x); myPos.y += (mySize.y / resize - mySize.y);
            myPos.X += 0.5f * (mySize.X / resize - mySize.X);
            myPos.Y += (mySize.Y / resize - mySize.Y);

            tag.BoxOrg = myPos + mySize / 2f;
            tag.BoxOfs = mySize / 2f;

            // ---- teammate status bar box adjustment (QC autocvar_hud_shownames_status branch) ----
            bool drawStatus = CvarBool("hud_shownames_status") && sameteam && !dead;
            float statusBarHeight = CvarF("hud_shownames_statusbar_height", 4f);
            if (drawStatus)
            {
                Vector2 sz = new(0.5f * mySize.X, resize * statusBarHeight);
                tag.BoxOfs.X = MathF.Max(mySize.X / 2f, sz.X);
                tag.BoxOfs.Y += sz.Y / 2f;
                tag.BoxOrg.Y = myPos.Y + (mySize.Y + sz.Y) / 2f;
            }
            tag.BoxValid = true;

            // Stash the resolved draw params for pass 2 (geometry computed once; no re-derive/divergence).
            int decolorize = (int)CvarF("hud_shownames_decolorize", 1f);
            tag.ScreenAnchor = o;
            tag.DrawAlpha = a;
            tag.Resize = resize;
            tag.DrawStatus = drawStatus;
            tag.DrawDecolor = (decolorize == 1 && teamplay) || decolorize == 2;
            // QC Draw_ShowNames_All:237-248 — healthvalue / RES_ARMOR are copied from the entcs ONLY under
            // m_entcs_private; an enemy slice zeroes them. The server already withholds enemy health/armor
            // (ServerNet.RelevantEntitiesFor zeroes them for enemy recipients), so this is the matching client-side
            // gate: read the slice only when private, mirroring QC's enemy zeroing even if a future stream leaks it.
            tag.Health = entcsPrivate ? s.Health : 0;
            tag.Armor = entcsPrivate ? s.Armor : 0;
            // QC entcs_GetTeam: the team id (clientColors & 15 == Colormap & 0xFF) drives the QC playername()
            // team-tint applied to decolorized teammate names (Team_ColorCode(teamid) prepended after strdecolorize).
            tag.TeamId = s.Colormap & 0xFF;

            _drawIds.Add(netId);
        }

        // ---- synthetic self-tag (QC Draw_ShowNames:42 self/spectatee branch for the local predicted player) ----
        // The local predicted player is never in ClientNet.RemoteIds (HandleSnapshot skips LocalNetId), so the
        // foreach above can't reach the own tag. When chase_active is on and hud_shownames_self is set (or the
        // 1s spectatee-switch grace window is active), synthesise the local player's tag from the predicted
        // origin and live Health/Armor stats, mirroring QC's self entry with `sameteam = true` (the server
        // always gives the local player its own private entcs slice).
        TryDrawSelfTag(ids, now, frametime, offset, vp, teamplay, viewOrigin);

        // ---- pass 2: draw (QC the per-tag drawing after the alpha math) — reads the pass-1 draw params ----
        Font font = HudPanel.HudFont ?? ThemeDB.FallbackFont;
        float fontsizeCv = CvarF("hud_shownames_fontsize", 12f);
        float aspectCv = CvarF("hud_shownames_aspect", 8f);
        float statusBarHeightCv = CvarF("hud_shownames_statusbar_height", 4f);
        bool highlight = CvarBool("hud_shownames_statusbar_highlight");

        foreach (int netId in _drawIds)
        {
            TagState tag = _tags[netId];
            float a = tag.DrawAlpha;
            if (a <= 0f) continue;

            float resize = tag.Resize;
            Vector2 o = tag.ScreenAnchor;
            Vector2 mySize = new(aspectCv * fontsizeCv * resize, fontsizeCv * resize);
            Vector2 myPos = new(o.X - 0.5f * mySize.X, o.Y - mySize.Y);

            // ---- teammate health/armor status bar (QC HUD_Panel_DrawProgressBar) ----
            if (tag.DrawStatus)
            {
                Vector2 barPos = new(myPos.X, myPos.Y + fontsizeCv * resize);
                Vector2 barSize = new(0.5f * mySize.X, resize * statusBarHeightCv);

                // QC statusbar_highlight: a faint grey backing behind the right half.
                if (highlight)
                    DrawRect(new Rect2(new Vector2(barPos.X + 0.25f * mySize.X, barPos.Y), barSize),
                        new Color(0.7f, 0.7f, 0.7f, a / 2f));

                if (tag.Health > 0)
                    // health bar: left half, baralign 1 (fills from the right), red.
                    DrawStatusBar(new Rect2(barPos, barSize), tag.Health / MaxHealth,
                        new Color(1f, 0f, 0f, a), rightAlign: true);
                if (tag.Armor > 0)
                    // armor bar: right half, baralign 0 (fills from the left), green.
                    DrawStatusBar(new Rect2(new Vector2(barPos.X + 0.5f * mySize.X, barPos.Y), barSize),
                        tag.Armor / MaxArmor, new Color(0f, 1f, 0f, a), rightAlign: false);
            }

            // ---- the name (QC drawcolorcodedstring with the decolorize rules) ----
            string name = NameResolver?.Invoke(netId) ?? "";
            if (string.IsNullOrEmpty(name))
                continue;
            // QC namewidth = mySize.x: the (resized) tag box width; textShortenToWidth clamps the name to it.
            DrawName(font, o, name, fontsizeCv * resize, a, tag.DrawDecolor, mySize.X, tag.TeamId);
        }

        PruneStaleTags();
    }

    // =====================================================================================================
    //  Drawing helpers
    // =====================================================================================================

    /// <summary>Draw the centered, optionally-decolorized color-coded name (QC <c>drawcolorcodedstring</c>). The name
    /// is centered on the projected anchor X like QC (<c>myPos.x = o.x - width/2</c>) and clamped to the tag box
    /// width <paramref name="maxWidth"/> via the QC <c>textShortenToWidth</c> rule (truncate + append "…" when it
    /// overflows). QC's <c>drawcolorcodedstring</c> has no drop-shadow, so neither does this.
    /// When <paramref name="decolor"/> is true the name's own <c>^</c>-codes are stripped and the team color CODE
    /// (<c>Team_ColorCode(teamid)</c>) is prepended, exactly as QC's <c>playername(s, team, true)</c> does
    /// (<c>strcat(Team_ColorCode(teamid), strdecolorize(name))</c>); the rebuilt string flows through the same
    /// color-coded path, so the tint is the engine palette colour of that code. <paramref name="teamId"/> = team.</summary>
    private void DrawName(Font font, Vector2 o, string name, float fontPx, float alpha, bool decolor, float maxWidth, int teamId)
    {
        int size = Mathf.Max(6, Mathf.RoundToInt(fontPx));
        if (decolor)
        {
            // QC playername(s, team, true) (util.qc:2178): strcat(Team_ColorCode(teamid), strdecolorize(thename)).
            // It strips the name's own ^codes then PREPENDS the team's color CODE (^1/^4/^3/^6 per teams.qh:26-29 /
            // Team_ColorCode), and the result is rendered by drawcolorcodedstring — so the tint is the ENGINE
            // palette colour of that code, not the Team_ColorRGB minimap table. Reproduce exactly by rebuilding the
            // string and routing it through the normal color-coded path (which uses the same engine palette).
            name = TeamColorCode(teamId) + HudText.Strip(name);
            // decolor falls through to the color-coded layout below (a single team-coloured run).
        }

        // Color-coded: lay out the runs left-to-right, centered on o.X. The color codes carry no width (QC
        // stringwidth_colors), so the width clamp counts only the printable glyphs — done on the joined text and
        // re-applied to the runs so the same characters are dropped from the end.
        var runs = HudText.Parse(name, new Color(1f, 1f, 1f, alpha));
        runs = ShortenRunsToWidth(font, runs, size, maxWidth);
        float total = 0f;
        foreach (HudText.Run run in runs)
            total += font.GetStringSize(run.Text, HorizontalAlignment.Left, -1f, size).X;
        float x = o.X - total * 0.5f;
        float y = o.Y - size;
        foreach (HudText.Run run in runs)
        {
            var rc = new Color(run.Color.R, run.Color.G, run.Color.B, alpha);
            DrawString(font, new Vector2(x, y), run.Text, HorizontalAlignment.Left, -1f, size, rc);
            x += font.GetStringSize(run.Text, HorizontalAlignment.Left, -1f, size).X;
        }
    }

    /// <summary>QC <c>textShortenToWidth</c> with <c>stringwidth_colors</c> (color codes are zero-width) applied to a
    /// parsed run list: only the printable glyphs count toward the width, so the clamp is computed on the joined
    /// visible text and the same number of trailing characters is dropped from the runs (the "…" lands in the last
    /// surviving run's color). Returns the input unchanged when it already fits.</summary>
    private static List<HudText.Run> ShortenRunsToWidth(Font font, List<HudText.Run> runs, int size, float maxWidth)
    {
        if (maxWidth <= 0f || runs.Count == 0)
            return runs;
        float total = 0f;
        foreach (HudText.Run r in runs)
            total += font.GetStringSize(r.Text, HorizontalAlignment.Left, -1f, size).X;
        if (total <= maxWidth)
            return runs;

        const string ellipsis = "...";
        float budget = maxWidth - font.GetStringSize(ellipsis, HorizontalAlignment.Left, -1f, size).X;
        var result = new List<HudText.Run>(runs.Count);
        float used = 0f;
        Color lastColor = runs[runs.Count - 1].Color;
        foreach (HudText.Run run in runs)
        {
            // Take as many leading characters of this run as the remaining budget allows.
            int take = run.Text.Length;
            while (take > 0)
            {
                float w = font.GetStringSize(run.Text.Substring(0, take), HorizontalAlignment.Left, -1f, size).X;
                if (used + w <= budget) break;
                take--;
            }
            if (take > 0)
            {
                result.Add(new HudText.Run(run.Text.Substring(0, take), run.Color));
                used += font.GetStringSize(run.Text.Substring(0, take), HorizontalAlignment.Left, -1f, size).X;
                lastColor = run.Color;
            }
            if (take < run.Text.Length)
                break; // budget exhausted mid-run; stop and append the ellipsis
        }
        result.Add(new HudText.Run(ellipsis, lastColor));
        return result;
    }

    /// <summary>Draw one status sub-bar (QC <c>HUD_Panel_DrawProgressBar</c> with the <c>"nametag_statusbar"</c> skin
    /// image, horizontal). <paramref name="rightAlign"/> mirrors baralign 1 (health, fills from the right edge) vs 0
    /// (armor, fills from the left). The backing (a dark rect drawn BEFORE calling this, matching QC's explicit
    /// <c>drawfill(… '0.7 0.7 0.7' …)</c> highlight) is not drawn here — it is drawn by the caller.
    /// Uses the <c>nametag_statusbar</c> texture from the active HUD skin (gfx/hud/&lt;skin&gt;/ with default
    /// fall-through), falling back to a flat colored rect when the art is missing — the same priority QC's
    /// <c>HUD_Panel_DrawProgressBar</c> has (precache_pic fall-through to "gfx/hud/default/progressbar").</summary>
    private void DrawStatusBar(Rect2 area, float fraction, Color fill, bool rightAlign)
    {
        if (fraction <= 0f || fill.A <= 0f) return;
        if (fraction > 1f) fraction = 1f;
        float w = area.Size.X * fraction;
        Vector2 pos = rightAlign
            ? new Vector2(area.Position.X + (area.Size.X - w), area.Position.Y)
            : area.Position;
        var fillRect = new Rect2(pos, new Vector2(w, area.Size.Y));
        // QC HUD_Panel_DrawProgressBar("nametag_statusbar"): try the skin image first, then the generic
        // progressbar skin, then a flat rect — matching QC's precache_pic fall-through order.
        Texture2D? tex = TextureCache.GetFirst(
            $"gfx/hud/{HudSkin.SkinName}/nametag_statusbar",
            "gfx/hud/default/nametag_statusbar",
            $"gfx/hud/{HudSkin.SkinName}/progressbar",
            "gfx/hud/default/progressbar");
        if (tex is not null)
            DrawTextureRect(tex, fillRect, false, fill);
        else
            DrawRect(fillRect, fill);
    }

    // =====================================================================================================
    //  Team-color + self-tag helpers
    // =====================================================================================================

    /// <summary>QC <c>Team_ColorCode</c> (common/teams.qh:63-74) — the per-team color CODE prepended by
    /// <c>playername(s, team, true)</c> to a decolorized name (then rendered by <c>drawcolorcodedstring</c>, so the
    /// visible tint is the engine palette colour of this code). The codes are <c>COL_TEAM_*</c> (teams.qh:26-29):
    /// red <c>^1</c>, blue <c>^4</c>, yellow <c>^3</c>, pink <c>^6</c>; fallback <c>^7</c> (white) for no team / FFA.
    /// Team ids are the colormap-derived NUM_TEAM values (Teams.Red=4 / Blue=13 / Yellow=12 / Pink=9).</summary>
    private static string TeamColorCode(int teamId) => teamId switch
    {
        Teams.Red    => "^1", // COL_TEAM_1
        Teams.Blue   => "^4", // COL_TEAM_2
        Teams.Yellow => "^3", // COL_TEAM_3
        Teams.Pink   => "^6", // COL_TEAM_4
        _            => "^7", // no team / FFA
    };

    /// <summary>Synthesise the local predicted player's own tag when <c>chase_active</c> is on and
    /// <c>hud_shownames_self</c> is set (or the 1s spectatee-switch grace window is active). The local player
    /// is never in <see cref="ClientNet.RemoteIds"/> (HandleSnapshot skips <see cref="LocalNetId"/>), so this
    /// pass injects it using <see cref="ClientNet.PredictedOrigin"/> and live <c>Health</c>/<c>Armor</c>, matching
    /// QC's <c>Draw_ShowNames</c>:42 self entry with <c>sameteam = true</c> (the server always gives the local
    /// player its own private entcs slice). Modifies <see cref="_drawIds"/> + <see cref="_tags"/> in place.</summary>
    private void TryDrawSelfTag(int[] remoteIds, float now, float frametime, float offset,
                                Vector2 vp, bool teamplay, NVec3 viewOrigin)
    {
        if (!ChaseActive || LocalNetId == 0 || Net is null)
            return;
        // QC self branch fires only for the VIEWED player (`this.sv_entnum == current_player + 1`). The local
        // player is the viewed one only when NOT following a remote spectatee (CurrentViewedNetId == LocalNetId);
        // while spectating someone else, the spectatee's tag is the one handled by the main loop, and the local
        // observer's own slot must NOT get a synthesised tag (QC never draws it).
        if (CurrentViewedNetId != LocalNetId)
            return;
        // Skip if the local net id is already in RemoteIds (shouldn't happen, but guard it).
        foreach (int rid in remoteIds) if (rid == LocalNetId) return;

        bool selfGrace = SpectateeStatus > 0 && now <= SpectateeStatusChangedTime + 1f;
        if (!CvarBool("hud_shownames_self") && !selfGrace)
            return;

        NVec3 selfOrigin = Net.PredictedOrigin;
        if (!float.IsFinite(selfOrigin.X) || !float.IsFinite(selfOrigin.Y) || !float.IsFinite(selfOrigin.Z))
            return;

        // QC Draw_ShowNames_All: `dead = entcs_IsDead(i) || entcs_IsSpectating(i)`. Prefer the networked Dead flag
        // from the captured local entcs slice (csqcmodel_isdead), falling back to the owner Health<=0 stat; OR in the
        // observer case (free-fly: SpectateeStatus == LocalNetId) which entcs_IsSpectating would also mark dead.
        bool selfDead = (Net.LocalState is { } ls && (ls.Flags & NetEntityFlags.Dead) != 0)
                        || Net.Health <= 0
                        || (SpectateeStatus != 0 && SpectateeStatus == LocalNetId);
        TagState selfTag = GetTag(LocalNetId);
        selfTag.Touched = _generation;
        selfTag.Origin = selfOrigin;

        // Projection
        NVec3 selfTagWorld = selfOrigin + new NVec3(0f, 0f, offset);
        bool selfOnScreen = Project(selfTagWorld, out Vector2 selfO, out bool selfBehind);
        bool selfOffScreen = selfBehind || !selfOnScreen
                             || selfO.X < 0f || selfO.Y < 0f || selfO.X > vp.X || selfO.Y > vp.Y;

        // Fade ramp — self uses the sameteam branch (always private slice) with dead slow-fade and offscreen fade.
        if (!selfTag.HadFadeDelay) { selfTag.FadeDelay = now + SHOWNAMES_FADEDELAY; selfTag.HadFadeDelay = true; }
        if (selfDead)
            selfTag.Alpha = MathF.Max(0f, selfTag.Alpha - SHOWNAMES_FADESPEED * 0.25f * frametime);
        else if (selfOffScreen)
            selfTag.Alpha = MathF.Max(0f, selfTag.Alpha - SHOWNAMES_FADESPEED * frametime);
        else
            selfTag.Alpha = MathF.Min(1f, selfTag.Alpha + SHOWNAMES_FADESPEED * frametime);

        // QC `a = alpha * this.alpha`; for the self case entcs_GetAlpha is also applied (the
        // `this.sv_entnum == player_localentnum` arm), but the local player's model alpha is 1 (no networked
        // fade for the predicted self), so it is a no-op here.
        float selfA = CvarF("hud_shownames_alpha", 0.7f) * selfTag.Alpha;
        if (selfA < AlphaMinVisible) { selfTag.BoxValid = false; return; }

        float selfDist = Vlen(selfOrigin - viewOrigin);
        float selfMaxDistCv = CvarF("hud_shownames_maxdistance", 5000f);
        float selfMinDist = CvarF("hud_shownames_mindistance", 1000f);
        if (selfMaxDistCv != 0f)
        {
            float selfMaxDist = MathF.Min(selfMaxDistCv, MaxShotDistance);
            if (selfDist >= selfMaxDist) { selfTag.BoxValid = false; return; }
            if (selfDist >= selfMinDist)
            {
                float selfF = selfMaxDistCv - selfMinDist;
                if (selfF > 0f) selfA *= (selfF - MathF.Max(0f, selfDist - selfMinDist)) / selfF;
            }
        }
        else if (selfDist >= MaxShotDistance) { selfTag.BoxValid = false; return; }
        if (selfA <= 0f || selfBehind || !selfOnScreen) { selfTag.BoxValid = false; return; }

        float selfResize = 1f;
        if (CvarBool("hud_shownames_resize") && selfDist >= selfMinDist)
        {
            float selfF = selfMaxDistCv - selfMinDist;
            if (selfF > 0f) selfResize = 0.5f + 0.5f * (selfF - MathF.Max(0f, selfDist - selfMinDist)) / selfF;
        }

        float selfFontsize = CvarF("hud_shownames_fontsize", 12f);
        float selfAspect = CvarF("hud_shownames_aspect", 8f);
        Vector2 selfMySize = new(selfAspect * selfFontsize, selfFontsize);
        Vector2 selfMyPos = new(selfO.X - 0.5f * selfMySize.X, selfO.Y - selfMySize.Y);
        selfMySize.X *= selfResize; selfMySize.Y *= selfResize;
        selfMyPos.X += 0.5f * (selfMySize.X / selfResize - selfMySize.X);
        selfMyPos.Y += (selfMySize.Y / selfResize - selfMySize.Y);
        selfTag.BoxOrg = selfMyPos + selfMySize / 2f;
        selfTag.BoxOfs = selfMySize / 2f;

        bool selfDrawStatus = CvarBool("hud_shownames_status") && !selfDead;
        float selfBarH = CvarF("hud_shownames_statusbar_height", 4f);
        if (selfDrawStatus)
        {
            Vector2 sz = new(0.5f * selfMySize.X, selfResize * selfBarH);
            selfTag.BoxOfs.X = MathF.Max(selfMySize.X / 2f, sz.X);
            selfTag.BoxOfs.Y += sz.Y / 2f;
            selfTag.BoxOrg.Y = selfMyPos.Y + (selfMySize.Y + sz.Y) / 2f;
        }
        selfTag.BoxValid = true;

        int selfDecolorize = (int)CvarF("hud_shownames_decolorize", 1f);
        selfTag.ScreenAnchor = selfO;
        selfTag.DrawAlpha = selfA;
        selfTag.Resize = selfResize;
        selfTag.DrawStatus = selfDrawStatus;
        selfTag.DrawDecolor = (selfDecolorize == 1 && teamplay) || selfDecolorize == 2;
        selfTag.Health = Net.Health;
        selfTag.Armor = Net.Armor;
        selfTag.TeamId = LocalTeam;
        _drawIds.Add(LocalNetId);
    }

    // =====================================================================================================
    //  Math / projection helpers (QC project_3d_to_2d, traceline, vlen2, boxesoverlap)
    // =====================================================================================================

    /// <summary>Project a Quake-space world point to screen (QC project_3d_to_2d). Returns false (and sets
    /// <paramref name="behind"/>) when the point is behind the camera; <paramref name="screen"/> is then the
    /// center, so off-screen culling treats it as not visible.</summary>
    private bool Project(NVec3 worldQuake, out Vector2 screen, out bool behind)
    {
        Camera3D cam = Camera!;
        Vector3 g = Coords.ToGodot(worldQuake);
        behind = cam.IsPositionBehind(g);
        if (behind)
        {
            screen = GetViewportRect().Size * 0.5f;
            return false;
        }
        screen = cam.UnprojectPosition(g);
        return float.IsFinite(screen.X) && float.IsFinite(screen.Y);
    }

    /// <summary>QC traceline(view_origin, target, MOVE_NOMONSTERS) line-of-sight: true when world geometry does
    /// not block the segment. On a listen server <see cref="Api.Trace"/> is the shared collision world; if no
    /// trace service is available (a pure client before its facade is up) we fall back to "visible" so tags don't
    /// vanish — matching the radar's degrade-to-shown behaviour.</summary>
    private static bool TraceVisible(NVec3 from, NVec3 to)
    {
        if (Api.Services is null)
            return true;
        ITraceService trace = Api.Trace;
        TraceResult tr = trace.Trace(from, NVec3.Zero, NVec3.Zero, to, MoveFilter.NoMonsters, null);
        // QC shownames.qc:62-63 — `hit = !(trace_fraction < 1 && <the blocker isn't the target player>)`: ANY
        // shortfall blocks. The target-player escape hatch Base needs does not apply here: MOVE_NOMONSTERS skips
        // every non-SOLID_BSP entity (TraceService.ClipToEntities), so a player can never stop this trace and an
        // unobstructed sweep returns Fraction exactly 1 (SweepState seeds 1f and only a real hit lowers it).
        //
        // This used to allow a 0.99 slop, which is not a constant epsilon but 1% OF THE TRACE LENGTH: an enemy
        // standing within 1% of eye-distance behind cover kept their tag lit — ~20 u of see-through wall at 2000 u
        // range, growing with distance. That is the "names visible through walls" leak; Base has no such tolerance.
        return tr.Fraction >= 1f;
    }

    private static float Vlen(NVec3 v) => v.Length();
    private static float Vlen2(NVec3 v) => v.LengthSquared();

    /// <summary>QC boxesoverlap(min1,max1,min2,max2) in 2D screen space.</summary>
    private static bool BoxesOverlap(Vector2 min1, Vector2 max1, Vector2 min2, Vector2 max2)
        => min1.X <= max2.X && min2.X <= max1.X && min1.Y <= max2.Y && min2.Y <= max1.Y;

    private TagState GetTag(int netId)
    {
        if (!_tags.TryGetValue(netId, out TagState? t))
        {
            t = new TagState();
            _tags[netId] = t;
        }
        return t;
    }

    private void PruneStaleTags()
    {
        // Drop tags whose player wasn't seen this frame AND whose fade fully decayed (so a tag fading out after a
        // player leaves the set still completes its fade — matching QC keeping the entity around for the ramp).
        if (_tags.Count == 0) return;
        List<int>? remove = null;
        foreach (KeyValuePair<int, TagState> kv in _tags)
            if (kv.Value.Touched != _generation && kv.Value.Alpha <= 0f)
                (remove ??= new List<int>()).Add(kv.Key);
        if (remove is not null)
            foreach (int id in remove)
                _tags.Remove(id);
    }

    // ---- cvar accessors (the shared menu/console store, like HudPanel) ----

    private static float CvarF(string name, float fallback)
    {
        string s = VortexArena.Game.Menu.MenuState.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? fallback : VortexArena.Game.Menu.MenuState.Cvars.GetFloat(name);
    }

    private static bool CvarBool(string name) => CvarF(name, 0f) != 0f;

    private static float NowSec()
        => Api.Services is not null ? Api.Clock.Time : Time.GetTicksMsec() / 1000f;
}
