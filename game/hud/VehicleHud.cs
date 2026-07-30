using System.Collections.Generic;
using Godot;

namespace VortexArena.Game.Hud;

/// <summary>
/// The in-vehicle HUD overlay — the port of <c>Vehicles_drawHUD</c> / <c>Vehicles_drawCrosshair</c> /
/// <c>AuxiliaryXhair_Draw2D</c> and the <c>TE_CSQC_VEHICLESETUP</c> dispatch
/// (Base/.../qcsrc/common/vehicles/cl_vehicles.qc). When the local player is driving a vehicle the engine
/// hands CSQC the per-vehicle stat set (<c>VEHICLESTAT_*</c>: health / shield / energy / ammo1 / reload1 /
/// ammo2 / reload2, each a 0..100 percent) and a HUD id selecting which vehicle's art to show; this panel
/// mirrors those onto the bottom-center vehicle frame — the silhouette tinted by health, the weapon overlays
/// tinted by ammo, the health/shield/ammo bars, and the health/shield icons that blink + alarm when low.
///
/// It also draws the <b>auxiliary lock-on crosshairs</b> (<c>AuxiliaryXhair</c>): small images projected from
/// 3D lock positions (the Racer rocket lock, the Bumblebee gunner targets), tinted by lock strength and faded
/// out shortly after their last update — fed by <see cref="SetAuxiliaryXhair"/>.
///
/// <see cref="ConfigureForVehicle"/> is the <c>TE_CSQC_VEHICLESETUP</c> entry: it selects the vehicle art set
/// (and shows the panel); <see cref="Exit"/> is the <c>hud_id == HUD_NORMAL</c> case that hides it. The actual
/// first-person viewport override (<c>SVC_SETVIEWPORT</c>/<c>SETVIEWANGLES</c>) is a camera concern the host
/// reads from <see cref="InVehicle"/>; the stat mirroring + crosshairs live here.
/// </summary>
public partial class VehicleHud : HudPanel
{
    /// <summary>True while the local player is in a vehicle (drives both this overlay and the host's camera).</summary>
    public bool InVehicle { get; private set; }

    /// <summary>The HUD skin whose vehicle art is preferred (default-skin is the fallback).</summary>
    public string HudSkin { get; set; } = "luma";

    // --- the mirrored VEHICLESTAT_* values, each in [0,1] (the QC 0.01 * STAT(...)) ---
    public float Health { get; set; } = 1f;
    public float Shield { get; set; }
    public float Energy { get; set; }
    public float Ammo1 { get; set; }
    public float Reload1 { get; set; }
    public float Ammo2 { get; set; }
    public float Reload2 { get; set; }

    /// <summary>Tint for the two ammo bars (QC colorAmmo1/2).</summary>
    public Color Ammo1Color { get; set; } = new(0.8f, 0.8f, 0.3f);
    public Color Ammo2Color { get; set; } = new(0.3f, 0.6f, 0.9f);

    // =====================================================================================
    //  Centered main reticle (Vehicles_drawCrosshair) — the per-vehicle / per-mode crosshair
    // =====================================================================================

    /// <summary>
    /// The centered crosshair art for the active vehicle/fire-mode (QC <c>Vehicles_drawCrosshair</c>: the
    /// per-mode reticle each <c>vr_crosshair</c> selects — e.g. the raptor's <c>vCROSS_BURST</c> bomb /
    /// <c>vCROSS_RAIN</c> flare reticle). Empty = no centered reticle (on-foot / not fed). Drawn at
    /// screen-center, tinted by health when <c>cl_vehicles_crosshair_colorize</c> is set.
    /// </summary>
    public string MainReticle { get; set; } = "";

    // =====================================================================================
    //  Raptor bomb dropmark (raptor vr_crosshair tracetoss predictor)
    // =====================================================================================

    /// <summary>Whether the bomb dropmark is shown this frame (raptor bomb mode, not spectating).</summary>
    public bool DropmarkActive { get; set; }
    /// <summary>The dropmark's predicted bomb-impact point (Godot space); projected to a 2D marker.</summary>
    public Vector3 DropmarkWorld { get; set; }
    /// <summary>True while the bombs are reloaded (reload2 == 1): the live green predictor. False = the last
    /// predicted impact, drawn red + larger while it lingers (QC <c>dropmark.cnt &gt; time</c>).</summary>
    public bool DropmarkLive { get; set; }
    /// <summary>The dropmark image (QC <c>vCROSS_DROP</c>).</summary>
    public string DropmarkImage { get; set; } = "gfx/vehicles/crosshair_drop";

    // =====================================================================================
    //  Bumblebee "No right/left gunner!" prompts (QC bumblebee vr_hud, bumblebee.qc:977-987)
    // =====================================================================================

    /// <summary>QC bumblebee vr_hud: the pilot sees a blinking "No right gunner!" prompt while gun1's seat is
    /// empty (the QC test is <c>!AuxiliaryXhair[1].draw2d</c> — no right-gunner aux crosshair this frame).</summary>
    public bool ShowNoRightGunner { get; set; }
    /// <summary>QC bumblebee vr_hud: the blinking "No left gunner!" prompt while gun2's seat is empty
    /// (QC <c>!AuxiliaryXhair[2].draw2d</c>).</summary>
    public bool ShowNoLeftGunner { get; set; }

    // Art base names selected by ConfigureForVehicle (the vehicle silhouette + weapon overlays).
    private string _vehiclePic = "vehicle_racer";
    private string? _weapon1Pic = "vehicle_racer_weapon1";
    private string? _weapon2Pic;

    private double _time;

    public override bool IsDynamic => InVehicle || _aux.Count > 0 || MainReticle.Length > 0 || DropmarkActive;

    // =====================================================================================
    //  Low-health / low-shield alarm (QC vehicle_alarm + Vehicles_drawHUD, cl_vehicles.qc:3-9 / 228-267)
    // =====================================================================================

    /// <summary>VFS-backed sample → <see cref="AudioStream"/> loader (host-set to <c>AssetLoader.LoadSound</c>),
    /// used for the low-health/shield alarm cues (SND_VEH_ALARM / SND_VEH_ALARM_SHIELD).</summary>
    public System.Func<string, AudioStream?>? AudioLoader { get; set; }

    // QC alarm1time/alarm2time: the per-channel re-arm gates (health = +2s, shield = +1s). 0 = idle/stopped.
    private double _alarm1Time;
    private double _alarm2Time;
    private AudioStreamPlayer? _alarmHealth;
    private AudioStreamPlayer? _alarmShield;

    // =====================================================================================
    //  TE_CSQC_VEHICLESETUP dispatch
    // =====================================================================================

    /// <summary>
    /// Select the vehicle art set and show the HUD (the <c>TE_CSQC_VEHICLESETUP</c> non-zero hud_id case).
    /// Mirrors <c>info.vr_setup</c> picking <c>vehicle_&lt;name&gt;</c> + its weapon overlays.
    /// </summary>
    public void ConfigureForVehicle(VehicleHudKind kind)
    {
        (_vehiclePic, _weapon1Pic, _weapon2Pic) = kind switch
        {
            VehicleHudKind.Racer => ("vehicle_racer", "vehicle_racer_weapon1", "vehicle_racer_weapon2"),
            VehicleHudKind.Raptor => ("vehicle_raptor", "vehicle_raptor_weapon1", "vehicle_raptor_weapon2"),
            VehicleHudKind.Spiderbot => ("vehicle_spider", "vehicle_spider_weapon1", "vehicle_spider_weapon2"),
            VehicleHudKind.Bumblebee => ("vehicle_bumble", "vehicle_bumble_weapon1", "vehicle_bumble_weapon2"),
            VehicleHudKind.BumblebeeGun => ("vehicle_gunner", "vehicle_gunner_weapon1", null),
            _ => ("vehicle_racer", "vehicle_racer_weapon1", null),
        };
        InVehicle = true;
        Visible = true;
        QueueRedraw();
    }

    /// <summary>The vehicle HUD layouts (QC HUD_* ids), selected by TE_CSQC_VEHICLESETUP.</summary>
    public enum VehicleHudKind { Racer, Raptor, Spiderbot, Bumblebee, BumblebeeGun }

    /// <summary>Exit the vehicle HUD (the <c>hud_id == HUD_NORMAL</c> case): hide it + clear aux crosshairs.</summary>
    public void Exit()
    {
        InVehicle = false;
        Visible = false;
        _aux.Clear();
        MainReticle = "";
        DropmarkActive = false;
        ShowNoRightGunner = false;
        ShowNoLeftGunner = false;
        StopAlarms(); // QC: the alarm channels stop when the vehicle HUD is dismissed
        QueueRedraw();
    }

    // =====================================================================================
    //  Auxiliary lock-on crosshairs (AuxiliaryXhair)
    // =====================================================================================

    private sealed class AuxXhair
    {
        public Vector3 World;     // 3D lock position (Godot space)
        public Color Color = Colors.White;
        public string Image = "";  // art base name (e.g. "gfx/vehicles/axh-target")
        public double LastUpdate;  // for the fade-out (QC axh_fadetime)
    }

    private readonly Dictionary<int, AuxXhair> _aux = new();

    /// <summary>How long after its last update an aux crosshair fades out (QC <c>axh_fadetime</c>, ~0.1s).</summary>
    public float AuxFadeTime { get; set; } = 0.1f;

    /// <summary>Crosshair size scale (QC <c>cl_vehicles_crosshair_size</c>).</summary>
    public float AuxSize { get; set; } = 24f;

    /// <summary>
    /// Set/refresh an auxiliary lock-on crosshair (QC <c>UpdateAuxiliaryXhair</c> / the ENT_CLIENT_AUXILIARYXHAIR
    /// update): a 2D marker projected from a 3D world position (Godot space), tinted by lock strength. Keyed by
    /// slot id; re-set each frame the lock holds, then auto-fades when updates stop.
    /// </summary>
    public void SetAuxiliaryXhair(int slot, Vector3 worldGodot, Color color, string image = "gfx/vehicles/axh-ring")
    {
        if (!_aux.TryGetValue(slot, out AuxXhair? x))
        {
            x = new AuxXhair();
            _aux[slot] = x;
        }
        x.World = worldGodot;
        x.Color = color;
        x.Image = image;
        x.LastUpdate = _time;
    }

    /// <summary>
    /// Set/refresh a lock-on aux crosshair colored by lock strength (QC racer <c>UpdateAuxiliaryXhair</c>):
    /// the marker shifts red→yellow while the lock builds (0..1) and snaps to green once locked (≥1). The
    /// host feeds the 3D target position + the server's lock-progress value.
    /// </summary>
    public void SetAuxiliaryXhairLock(int slot, Vector3 worldGodot, float lockStrength,
        string image = "gfx/vehicles/axh-target")
        => SetAuxiliaryXhair(slot, worldGodot, AuxLockColor(lockStrength), image);

    /// <summary>Lock-strength → color (QC racer lock coloring): red building → yellow → green locked.</summary>
    public static Color AuxLockColor(float lockStrength)
    {
        float s = Mathf.Clamp(lockStrength, 0f, 1f);
        if (s >= 1f) return new Color(0.3f, 1f, 0.3f);              // locked = green
        return new Color(1f, Mathf.Lerp(0.2f, 1f, s), 0.2f);       // building = red → yellow
    }

    /// <summary>Clear an aux crosshair slot (the lock released).</summary>
    public void ClearAuxiliaryXhair(int slot) => _aux.Remove(slot);

    // =====================================================================================
    //  Draw
    // =====================================================================================

    public override void _Process(double delta)
    {
        _time += delta;

        // Low-health / low-shield alarm (QC Vehicles_drawHUD low-health blocks, cl_vehicles.qc:228-267). Runs
        // every frame while piloting; the SND_VEH_ALARM (health, +2s re-arm) / SND_VEH_ALARM_SHIELD (shield,
        // +1s re-arm) cues are gated by cl_vehicles_alarm (default 0) inside the vehicle_alarm helper.
        if (InVehicle)
            UpdateAlarms();
        else
            StopAlarms();

        // Drop aux crosshairs that stopped updating (faded out).
        if (_aux.Count > 0)
        {
            var stale = new List<int>();
            foreach (var kv in _aux)
                if (_time - kv.Value.LastUpdate > AuxFadeTime * 4)
                    stale.Add(kv.Key);
            foreach (int k in stale) _aux.Remove(k);
        }
    }

    /// <summary>
    /// QC <c>Vehicles_drawHUD</c> health/shield alarm blocks (cl_vehicles.qc:228-267): when health/shield drop
    /// below 25% re-arm an alarm cue on a fixed interval (health +2s, shield +1s); once they recover, stop the
    /// looping cue (the QC <c>SND_Null</c> branch). The whole thing is gated by <c>cl_vehicles_alarm</c>
    /// (default 0) via <see cref="VehicleAlarm"/>.
    /// </summary>
    private void UpdateAlarms()
    {
        // Health alarm — QC: if (health < 0.25) { if (alarm1time < time) { alarm1time = time + 2; alarm(SND_VEH_ALARM); } }
        if (Health < 0.25f)
        {
            if (_alarm1Time < _time)
            {
                _alarm1Time = _time + 2.0;
                VehicleAlarm(ref _alarmHealth, "vehicles/alarm");
            }
        }
        else if (_alarm1Time != 0.0)
        {
            VehicleAlarmStop(_alarmHealth);
            _alarm1Time = 0.0;
        }

        // Shield alarm — QC: if (shield < 0.25) { if (alarm2time < time) { alarm2time = time + 1; alarm(SND_VEH_ALARM_SHIELD); } }
        if (Shield < 0.25f)
        {
            if (_alarm2Time < _time)
            {
                _alarm2Time = _time + 1.0;
                VehicleAlarm(ref _alarmShield, "vehicles/alarm_shield");
            }
        }
        else if (_alarm2Time != 0.0)
        {
            VehicleAlarmStop(_alarmShield);
            _alarm2Time = 0.0;
        }
    }

    /// <summary>Stop + reset both alarm channels (exit / on-foot). Mirrors the QC SND_Null stop on both gates.</summary>
    private void StopAlarms()
    {
        if (_alarm1Time != 0.0) { VehicleAlarmStop(_alarmHealth); _alarm1Time = 0.0; }
        if (_alarm2Time != 0.0) { VehicleAlarmStop(_alarmShield); _alarm2Time = 0.0; }
    }

    /// <summary>Port of QC <c>vehicle_alarm(e, ch, snd)</c> (cl_vehicles.qc:3-9): play the cue, but only when
    /// <c>cl_vehicles_alarm</c> is set (default 0 → silent, matching Base).</summary>
    private void VehicleAlarm(ref AudioStreamPlayer? player, string sample)
    {
        if (GlobalF("cl_vehicles_alarm", 0f) == 0f) // QC: if (!autocvar_cl_vehicles_alarm) return;
            return;

        player ??= MakeAlarmPlayer(sample);
        if (player is null)
            return;
        player.Play();
    }

    private static void VehicleAlarmStop(AudioStreamPlayer? player)
    {
        if (player is not null && GodotObject.IsInstanceValid(player) && player.Playing)
            player.Stop();
    }

    private AudioStreamPlayer? MakeAlarmPlayer(string sample)
    {
        AudioStream? stream = AudioLoader?.Invoke(sample);
        if (stream is null)
            return null;
        var p = new AudioStreamPlayer { Name = "VehAlarm_" + sample.Replace('/', '_'), Bus = "SFX", Stream = stream };
        AddChild(p);
        return p;
    }

    protected override void DrawPanel()
    {
        DrawAuxiliaryXhairs();
        DrawBombDropmark();   // raptor vr_crosshair: the predicted bomb-impact marker (under the centered reticle)
        DrawMainReticle();    // Vehicles_drawCrosshair: the centered per-mode reticle

        if (!InVehicle)
            return;

        float w = Size2.X, h = Size2.Y;
        float blink = 0.55f + Mathf.Sin((float)_time * 7f) * 0.45f;

        // Frame (gfx/hud/<skin>/vehicle_frame), or a translucent panel fallback.
        Texture2D? frame = Skin("vehicle_frame");
        if (frame is not null)
            DrawTextureRect(frame, new Rect2(Vector2.Zero, Size2), tile: false, Colors.White);
        else
            DrawBackground();

        // Vehicle silhouette, tinted by health (red flash when critical) — QC drawpic_skin(vehicle, health tint).
        var modelRect = new Rect2(new Vector2(w / 3f, 0f), new Vector2(w / 3f, h));
        Color healthTint = Health < 0.25f
            ? new Color(1f, blink, blink)
            : new Color(1f, 1f, 1f) * Health + new Color(1f, 0f, 0f) * (1f - Health);
        DrawSkinPic(_vehiclePic, modelRect, healthTint);

        // Weapon overlays, tinted by ammo.
        if (_weapon1Pic is not null)
            DrawSkinPic(_weapon1Pic, modelRect, AmmoTint(Ammo1));
        if (_weapon2Pic is not null)
            DrawSkinPic(_weapon2Pic, modelRect, AmmoTint(Ammo2));
        // Shield overlay (fades in with shield strength).
        DrawSkinPic("vehicle_shield", modelRect, new Color(1f, 1f, 1f, Shield));

        // Bars: health (NW), shield (SW), ammo1 (NE), ammo2 (SE). QC clips the bar pic to the fraction.
        float barW = w / 3f, barH = h * 0.5f;
        float leftX = w * (32f / 768f), rightX = w * (480f / 768f);
        DrawClippedBar("vehicle_bar_northwest", new Rect2(leftX, 0f, barW, barH), Health, fromRight: true,
            new Color(0.8f, 0.2f, 0.2f));
        DrawClippedBar("vehicle_bar_southwest", new Rect2(leftX, barH, barW, barH), Shield, fromRight: true,
            new Color(0.3f, 0.6f, 0.9f));
        DrawClippedBar("vehicle_bar_northeast", new Rect2(rightX, 0f, barW, barH), Ammo1 > 0f ? Ammo1 : Reload1,
            fromRight: false, Ammo1Color);
        DrawClippedBar("vehicle_bar_southeast", new Rect2(rightX, barH, barW, barH), Ammo2 > 0f ? Ammo2 : Reload2,
            fromRight: false, Ammo2Color);

        // Icons: health + shield, blinking when low (the QC alarm pulse).
        var iconSize = new Vector2(w * (80f / 768f), h * (80f / 256f));
        DrawSkinPic("vehicle_icon_health", new Rect2(new Vector2(w * (64f / 768f), h * (48f / 256f)), iconSize),
            new Color(1f, 1f, 1f, Health < 0.25f ? blink : 1f));
        DrawSkinPic("vehicle_icon_shield", new Rect2(new Vector2(w * (64f / 768f), h * 0.5f), iconSize),
            new Color(1f, 1f, 1f, Shield < 0.25f ? blink : 1f));
        DrawSkinPic("vehicle_icon_ammo1", new Rect2(new Vector2(w * (624f / 768f), h * (48f / 256f)), iconSize),
            new Color(1f, 1f, 1f, Ammo1 > 0f ? 1f : 0.2f));
        DrawSkinPic("vehicle_icon_ammo2", new Rect2(new Vector2(w * (624f / 768f), h * 0.5f), iconSize),
            new Color(1f, 1f, 1f, Ammo2 > 0f ? 1f : 0.2f));

        // QC bumblebee vr_hud (bumblebee.qc:971-987): the pilot's blinking "No right/left gunner!" prompts while a
        // side-gun seat is empty. blinkValue = 0.55 + sin(time*7)*0.45 (same pulse as the low-health alarm), text
        // pure white at hud_fg_alpha * blink, positioned x = 520/768 across, y = 96/256 (right) / 160/256 (left).
        if (ShowNoRightGunner || ShowNoLeftGunner)
        {
            float promptX = w * (520f / 768f);
            var promptCol = new Color(1f, 1f, 1f, blink);
            if (ShowNoRightGunner)
                DrawText(new Vector2(promptX, h * (96f / 256f) - FontSize), "No right gunner!", promptCol);
            if (ShowNoLeftGunner)
                DrawText(new Vector2(promptX, h * (160f / 256f)), "No left gunner!", promptCol);
        }
    }

    private static Color AmmoTint(float ammo) => new Color(1f, 1f, 1f) * ammo + new Color(1f, 0f, 0f) * (1f - ammo);

    /// <summary>Draw a vehicle bar pic revealing <paramref name="fraction"/> of it (QC drawsetcliparea),
    /// or a colored <see cref="HudPanel.DrawBar"/> when the art is missing.</summary>
    private void DrawClippedBar(string pic, Rect2 area, float fraction, bool fromRight, Color fallback)
    {
        fraction = Mathf.Clamp(fraction, 0f, 1f);
        Texture2D? tex = Skin(pic);
        if (tex is null)
        {
            DrawBar(area, fraction, fallback);
            return;
        }
        // Reveal a sub-region of the texture matching the fraction (NW/SW bars fill from the right).
        Vector2 ts = tex.GetSize();
        float revealW = ts.X * fraction;
        Rect2 src = fromRight
            ? new Rect2(ts.X - revealW, 0f, revealW, ts.Y)
            : new Rect2(0f, 0f, revealW, ts.Y);
        Rect2 dst = fromRight
            ? new Rect2(area.Position.X + area.Size.X * (1f - fraction), area.Position.Y, area.Size.X * fraction, area.Size.Y)
            : new Rect2(area.Position, new Vector2(area.Size.X * fraction, area.Size.Y));
        if (revealW > 0.5f)
            DrawTextureRectRegion(tex, dst, src, fallback);
    }

    /// <summary>
    /// Port of <c>Vehicles_drawCrosshair</c> (cl_vehicles.qc:293): draw the centered per-mode reticle at
    /// screen-center, scaled by <c>cl_vehicles_crosshair_size</c> and faded by <c>crosshair_alpha</c>, tinted
    /// by vehicle health when <c>cl_vehicles_crosshair_colorize</c> is set (else white). The reticle image is
    /// chosen by the per-vehicle <c>vr_crosshair</c> (e.g. raptor bomb = vCROSS_BURST, flare = vCROSS_RAIN).
    /// </summary>
    private void DrawMainReticle()
    {
        if (MainReticle.Length == 0) return;
        Texture2D? tex = TextureCache.Get(MainReticle);
        if (tex is null) return;

        float size = GlobalF("cl_vehicles_crosshair_size", 1f);
        if (size <= 0f) size = 1f;
        float alpha = GlobalF("crosshair_alpha", 0.8f);

        Vector2 ts = tex.GetSize() * size;
        // QC centers on vid_conwidth/conheight: screen center → panel-local.
        Vector2 center = ScreenCenter() - PanelRect.Position;
        // QC cl_vehicles_crosshair_colorize → crosshair_getcolor by VEHICLESTAT_HEALTH; else '1 1 1'.
        Color tint = GlobalF("cl_vehicles_crosshair_colorize", 1f) != 0f
            ? CrosshairHealthColor(Health)
            : Colors.White;
        tint.A *= alpha;
        DrawTextureRect(tex, new Rect2(center - ts * 0.5f, ts), tile: false, tint);
    }

    /// <summary>
    /// Port of the raptor <c>vr_crosshair</c> bomb dropmark (raptor.qc:792-832): project the predicted bomb-
    /// impact point to a 2D marker. While the bombs are reloaded (reload2 == 1, <see cref="DropmarkLive"/>) the
    /// marker is the live tracetoss prediction drawn GREEN at normal size; after a drop the marker lingers on
    /// the last impact, drawn RED at 1.25× size until it expires. Each is drawn twice (additive + normal) so it
    /// stays visible against a bright sky, exactly like QC.
    /// </summary>
    private void DrawBombDropmark()
    {
        if (!DropmarkActive) return;
        Camera3D? cam = GetViewport()?.GetCamera3D();
        if (cam is null) return;
        Vector3 world = DropmarkWorld; // already Godot space (converted at the NetGame feed boundary)
        if (cam.IsPositionBehind(world)) return;

        Texture2D? tex = TextureCache.Get(DropmarkImage);
        if (tex is null) return;

        float size = GlobalF("cl_vehicles_crosshair_size", 1f);
        if (size <= 0f) size = 1f;
        float alpha = GlobalF("crosshair_alpha", 0.8f);
        // QC: green '0 1 0' while live at base size; red '1 0 0' at 1.25× after release.
        Color c = DropmarkLive ? new Color(0f, 1f, 0f) : new Color(1f, 0f, 0f);
        float scale = DropmarkLive ? size : size * 1.25f;
        Vector2 ts = tex.GetSize() * scale;

        Vector2 screen = cam.UnprojectPosition(world);
        Vector2 local = screen - PanelRect.Position - ts * 0.5f;
        var dst = new Rect2(local, ts);
        // QC draws the dropmark twice: additive at 0.9*alpha then normal at 0.6*alpha for visibility.
        DrawTextureRect(tex, dst, tile: false, new Color(c.R, c.G, c.B, alpha * 0.9f));
        DrawTextureRect(tex, dst, tile: false, new Color(c.R, c.G, c.B, alpha * 0.6f));
    }

    /// <summary>QC <c>crosshair_getcolor</c> by health: green when healthy, fading to red when critical. Mirrors
    /// the simple health→color used for the colorized vehicle reticle (cl_vehicles_crosshair_colorize).</summary>
    private static Color CrosshairHealthColor(float health01)
    {
        float h = Mathf.Clamp(health01, 0f, 1f);
        return new Color(Mathf.Lerp(1f, 0.3f, h), Mathf.Lerp(0.2f, 1f, h), 0.2f);
    }

    /// <summary>The screen-space center (QC <c>vid_conwidth/conheight * 0.5</c>).</summary>
    private Vector2 ScreenCenter()
    {
        Rect2 vp = GetViewportRect();
        return vp.Size * 0.5f;
    }

    private void DrawAuxiliaryXhairs()
    {
        if (_aux.Count == 0) return;
        Camera3D? cam = GetViewport()?.GetCamera3D();
        if (cam is null) return;

        foreach (AuxXhair x in _aux.Values)
        {
            if (cam.IsPositionBehind(x.World)) continue;
            Vector2 screen = cam.UnprojectPosition(x.World);
            // The panel is laid out at a fixed rect; aux crosshairs are screen-space, so convert to panel-local.
            Vector2 local = screen - PanelRect.Position;
            float fade = 1f - Mathf.Clamp((float)(_time - x.LastUpdate) / Mathf.Max(0.001f, AuxFadeTime * 4), 0f, 1f);
            Color c = x.Color; c.A *= fade;

            Texture2D? tex = TextureCache.Get(x.Image);
            var half = new Vector2(AuxSize, AuxSize) * 0.5f;
            if (tex is not null)
                DrawTextureRect(tex, new Rect2(local - half, new Vector2(AuxSize, AuxSize)), tile: false, c);
            else
                DrawRect(new Rect2(local - half, new Vector2(AuxSize, AuxSize)), c, filled: false, width: 2f);
        }
    }

    // =====================================================================================
    //  Art resolution (skin-aware, VFS-backed via TextureCache)
    // =====================================================================================

    /// <summary>Resolve a vehicle HUD pic, preferred skin then default skin then a project override.</summary>
    private Texture2D? Skin(string baseName)
        => TextureCache.GetFirst($"gfx/hud/{HudSkin}/{baseName}", $"gfx/hud/default/{baseName}",
            $"res://art/hud/vehicles/{baseName}.png");

    private void DrawSkinPic(string baseName, Rect2 dst, Color tint)
    {
        Texture2D? tex = Skin(baseName);
        if (tex is not null)
            DrawTextureRect(tex, dst, tile: false, tint);
    }
}
