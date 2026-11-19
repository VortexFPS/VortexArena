"""Add a paired waypoint fixture and trace points to disposable source copies."""
from pathlib import Path
import os
import re
import struct
import sys
import zipfile

kind, source, output, repo = sys.argv[1:]
source, output, repo = map(Path, (source, output, repo))


followup = os.environ.get("RADAR_VARIANT") == "followup"
links = os.environ.get("RADAR_VARIANT") == "links"


def edit(path, edits):
    text = path.read_text()
    for old, new in edits:
        assert text.count(old) == 1, (path, old)
        text = text.replace(old, new, 1)
    path.write_text(text)


if kind == "assets":
    with zipfile.ZipFile(repo / "data/maps/runningmanctf.pk3") as stock:
        info = stock.read("maps/runningmanctf.mapinfo")
        ents = stock.read("maps/runningmanctf.bsp")
    offset, length = struct.unpack_from("<ii", ents, 8)
    with zipfile.ZipFile(output / "fixture.pk3", "w", zipfile.ZIP_DEFLATED) as pack:
        pack.writestr("maps/runningmanctf.mapinfo",
                      info if followup else info + b"\nsize -2400 -1000 -576 1200 1800 1664\n")
        if followup:
            # Enlarge model metadata while leaving the collision geometry intact. The two texture
            # halves have exact greys so the screen blend can be checked against the same backdrop.
            bsp = bytearray(ents)
            model_offset, _ = struct.unpack_from("<ii", bsp, 8 + 7 * 8)
            x0, y0, z0, x1, y1, z1 = struct.unpack_from("<6f", bsp, model_offset)
            struct.pack_into("<6f", bsp, model_offset, x0 - 64, y0 - 64, z0, x1 + 64, y1 + 64, z1)
            pack.writestr("maps/runningmanctf.bsp", bsp)
            header = struct.pack("<BBBHHBHHHHBB", 0, 0, 2, 0, 0, 0, 0, 0, 32, 32, 24, 0)
            row = bytes(16 * 3) + bytes([128]) * (16 * 3)
            pack.writestr("gfx/runningmanctf_radar.tga", header + row * 32)
        entities = ents[offset:offset + length].rstrip(b"\0")
        if links:
            entities = re.sub(rb'("classname" "onslaught_link")', lambda m: b'"targetname" "radar_audit_links"\n' + m[0], entities)
        pack.writestr("maps/runningmanctf.ent", entities + b'''
{
"classname" "misc_teleporter_dest"
"targetname" "radar_audit_camera"
"origin" "-80 -208 59"
"angle" "37"
}
{
"classname" "misc_teleporter_dest"
"targetname" "radar_audit_camera_native"
"origin" "-80 -208 68"
"angle" "37"
}
''')
elif kind == "stock":
    if links:
        edit(source / "client.qc", [
            ("void HUD_Radar(float should_draw)\n{",
             "float radar_audit_links_previous = -1;\nvoid HUD_Radar(float should_draw)\n{"),
            (" draw_teamradar_background(hud_panel_radar_foreground_alpha);",
             " float radar_audit_links_count = 0;\n draw_teamradar_background(hud_panel_radar_foreground_alpha);"),
            ("draw_teamradar_link(it.origin, it.velocity, it.team);",
             "++radar_audit_links_count; draw_teamradar_link(it.origin, it.velocity, it.team);"),
            (" float mutator_returnvalue = __Mutator_Send_TeamRadar_Draw(0 );", r''' if (hud_panel_radar_maximized && radar_audit_links_count != radar_audit_links_previous)
 {
   print(sprintf("LINK_HUD %d\n", radar_audit_links_count));
   radar_audit_links_previous = radar_audit_links_count;
 }
 float mutator_returnvalue = __Mutator_Send_TeamRadar_Draw(0 );'''),
        ])

    if followup:
        edit(source / "client.qc", [
            ("  mi_center = (mi_min + mi_max) * 0.5;", r'''  print(strcat(
     sprintf("RADAR_RAW %.3f %.3f %.3f %.3f\n", world.mins.x, world.mins.y, world.maxs.x, world.maxs.y),
     sprintf("RADAR_BOUNDS %.3f %.3f %.3f %.3f\n", mi_min.x, mi_min.y, mi_max.x, mi_max.y)));
  mi_center = (mi_min + mi_max) * 0.5;'''),
        ])

    # The compiled input is the pinned QuakeC after preprocessing. Leave the engine and installed code alone.
    edit(source / "server.qc", [
        (" void StartFrame()\n", " void radar_audit_frame();\n void StartFrame()\n"),
        ("   _StartFrame();\n", "   _StartFrame();\n  radar_audit_frame();\n"),
        ('  if ( ((player).classname == STR_PLAYER) )\n  {\n   entity source_point',
         '  print(sprintf("RADAR_CLICK %.6f %.6f %.6f\\n", pos.x, pos.y, pos.z));\n'
         '  if ( ((player).classname == STR_PLAYER) )\n  {\n   entity source_point'),
    ])
    with (source / "server.qc").open("a") as f:
        f.write(r'''
.entity radar_audit_marker;
entity radar_audit_owner_a, radar_audit_owner_b;
float radar_audit_next, radar_audit_count;
void radar_audit_frame()
{
    if (!radar_audit_owner_a)
    {
        if (!find((0, world), classname, "player")) return;
        radar_audit_owner_a = __spawn("radar_audit_owner", "radar fixture", true);
        radar_audit_owner_b = __spawn("radar_audit_owner", "radar fixture", true);
        WaypointSprite_SpawnFixed(WP_Here, '-300 100 64', radar_audit_owner_a, radar_audit_marker, RADARICON_HERE);
        WaypointSprite_SpawnFixed(WP_Here, '500 900 200', radar_audit_owner_b, radar_audit_marker, RADARICON_HERE);
        radar_audit_next = time + 12;
    }
    if (radar_audit_count >= 36 || time < radar_audit_next) return;
    WaypointSprite_Ping(radar_audit_owner_a.radar_audit_marker);
    ++radar_audit_count;
    radar_audit_next = time + 0.34;
    print(sprintf("RADAR_SEND %d %.6f\n", radar_audit_count, time));
}
''')
    edit(source / "client.qc", [
        ("            this.teamradar_time_index = (this.teamradar_time_index + 1) % MAX_TEAMRADAR_TIMES;",
         '            this.teamradar_time_index = (this.teamradar_time_index + 1) % MAX_TEAMRADAR_TIMES;\n'
         '            if (this.origin.x == -300 && this.origin.y == 100) print(sprintf("RADAR_RECV %d %.6f\\n", this.teamradar_time_index, time));'),
        ("void draw_teamradar_icon(vector coord, entity icon, entity pingdata, vector rgb, float a)\n{",
         "float radar_audit_seen, radar_audit_slot, radar_audit_active;\n"
         "void draw_teamradar_icon(vector coord, entity icon, entity pingdata, vector rgb, float a)\n{\n"
         " float radar_audit_probe = coord.x == -300 && coord.y == 100;\n float radar_audit_live = 0;"),
        ("   vector v = '2 2 0' * teamradar_size * dt;",
         "   ++radar_audit_live;\n   vector v = '2 2 0' * teamradar_size * dt;"),
        ('\n}\n\nvoid draw_teamradar_link(vector start, vector end, float colors)\n{', r'''
 if (radar_audit_probe && (pingdata.teamradar_time_index != radar_audit_slot || radar_audit_live != radar_audit_active))
 {
   if (pingdata.teamradar_time_index != radar_audit_slot) ++radar_audit_seen;
   radar_audit_slot = pingdata.teamradar_time_index;
   radar_audit_active = radar_audit_live;
   print(sprintf("RADAR_DRAW %d %d %d %.6f %.6f %.6f\n",
       radar_audit_seen, radar_audit_slot, radar_audit_live, coord.x, coord.y, teamradar_size));
 }
}

void draw_teamradar_link(vector start, vector end, float colors)
{'''),
        ("void HUD_Radar(float should_draw)\n{",
         "string radar_audit_last;\nvoid HUD_Radar(float should_draw)\n{"),
        (" draw_teamradar_background(hud_panel_radar_foreground_alpha);", r'''
 if (hud_panel_radar_maximized)
 {
   vector m = teamradar_texcoord_to_2dcoord(teamradar_3dcoord_to_texcoord('-300 100 64'));
   vector n = teamradar_texcoord_to_2dcoord(teamradar_3dcoord_to_texcoord('500 900 200'));
   string line = strcat(
       sprintf("RADAR_GEOM %d %.2f %.2f %.2f | %.2f %.2f %.2f", hud_panel_radar_mouse, teamradar_angle, zoom_factor,
           teamradar_size, view_origin.x, view_origin.y, view_origin.z),
       sprintf(" | %.2f %.2f %.2f | %.3f %.3f %.3f %.3f\n", mi_min.x, mi_max.x, mi_max.y, m.x, m.y, n.x, n.y));
   if (line != radar_audit_last)
   {
     if (radar_audit_last) strunzone(radar_audit_last);
     radar_audit_last = strzone(line);
     print(line);
   }
 }
 draw_teamradar_background(hud_panel_radar_foreground_alpha);'''),
    ])
elif kind == "native":
    edit(source / "game/net/ServerNet.cs", [
        ("    private void SendWaypoints()\n    {", '''
    private VortexArena.Common.Gameplay.Waypoints.WaypointSprite? _radarAuditMarker;
    private float _radarAuditNext;
    private int _radarAuditCount;

    private void SendWaypoints()
    {
        if (System.Environment.GetEnvironmentVariable("VORTEX_RADAR_FIXTURE") == "1" &&
            System.Linq.Enumerable.Any(_peers.Values, st => st.Accepted))
        {
            if (_radarAuditMarker is null)
            {
                _radarAuditMarker = VortexArena.Common.Gameplay.Waypoints.WaypointSprites.SpawnFixed(
                    "here", new(-300, 100, 64), 0, new(0, 1, 0));
                VortexArena.Common.Gameplay.Waypoints.WaypointSprites.SpawnFixed(
                    "here", new(500, 900, 200), 0, new(0, 1, 0));
                _radarAuditNext = float.PositiveInfinity;
            }
            if (float.IsPositiveInfinity(_radarAuditNext) &&
                System.IO.File.Exists(System.Environment.GetEnvironmentVariable("VORTEX_RADAR_TRIGGER")))
                _radarAuditNext = _world.Time;
            if (_radarAuditCount < 36 && _world.Time >= _radarAuditNext)
            {
                VortexArena.Common.Gameplay.Waypoints.WaypointSprites.Ping(_radarAuditMarker);
                _radarAuditNext = _world.Time + 0.34f;
                GD.Print(FormattableString.Invariant($"RADAR_SEND {++_radarAuditCount} {_world.Time:F6}"));
            }
        }'''),
    ])
    edit(source / "game/net/NetGame.cs", [
        ("        await PrecacheWeaponModelsAsync();",
         "        if (_radarAuditCommand is null) await PrecacheWeaponModelsAsync();"),
        ("            await PrecacheCombatSoundsAndModelsAsync();",
         "            if (_radarAuditCommand is null) await PrecacheCombatSoundsAndModelsAsync();"),
        ("    public override void _Process(double delta)\n    {", '''
    private readonly string? _radarAuditCommand = System.Environment.GetEnvironmentVariable("VORTEX_RADAR_COMMAND");
    private bool _radarAuditPlaying;

    public override void _Process(double delta)
    {
        if (!_radarAuditPlaying && _radarAuditCommand is not null && _client is { IsObserving: false, Health: > 0 })
        {
            GD.Print("RADAR_PLAYING");
            _radarAuditPlaying = true;
        }
        if (_readyComplete && _radarAuditCommand is { } file && System.IO.File.Exists(file))
        {
            string command = System.IO.File.ReadAllText(file).Trim();
            System.IO.File.Delete(file);
            RunCommand?.Invoke(command);
            GD.Print($"RADAR_CONSOLE {command}");
        }'''),
    ])
    edit(source / "game/net/ClientNet.cs", [
        ("public void Record(uint sequence, float time)", "public void Record(uint sequence, float time, bool auditMarker)"),
        ("history.Record(sequence, packetTime);", "history.Record(sequence, packetTime, tmp[i].Origin.X == -300 && tmp[i].Origin.Y == 100);"),
        ("            _count = Math.Min(_count + 1, _times.Length);",
         '            _count = Math.Min(_count + 1, _times.Length);\n'
         '            if (auditMarker) GD.Print(FormattableString.Invariant($"RADAR_RECV {sequence} {_next} {_count} {time:F6}"));'),
    ])
    edit(source / "game/hud/RadarPanel.cs", [
        ("    private Control _pingLayer", '''
    private string? _radarAuditPreviousGeometry;
    private string? _radarAuditPreviousDraw;
    private Control _pingLayer'''),
        ("        // ---- Onslaught radar links", '''
        if (Maximized)
        {
            Vector2 a = Position + WorldToScreen(-300, 100);
            Vector2 b = Position + WorldToScreen(500, 900);
            string line = FormattableString.Invariant(
                $"RADAR_GEOM {(Clickable ? 1 : 0)} {yawRad:F2} {zoomFactor:F2} {worldToPixels * pictureSpan:F2} | {viewOrigin.X:F2} {viewOrigin.Y:F2} {viewOrigin.Z:F2} | {MapMinXY.X:F2} {MapMaxXY.X:F2} {MapMaxXY.Y:F2} | {a.X:F3} {a.Y:F3} {b.X:F3} {b.Y:F3}");
            if (line != _radarAuditPreviousGeometry)
            {
                GD.Print(line);
                _radarAuditPreviousGeometry = line;
            }
        }

        // ---- Onslaught radar links'''),
        ("                foreach (float stamp in net.RadarPingTimes(wp.Id))", '''
                int before = _pingDraws.Count;
                foreach (float stamp in net.RadarPingTimes(wp.Id))'''),
        ("            }\n        }\n\n        // ---- maximized clickable", '''
                if (wp.Origin.X == -300 && wp.Origin.Y == 100)
                {
                    var times = net.RadarPingTimes(wp.Id);
                    string line = FormattableString.Invariant(
                        $"RADAR_DRAW {times.Length} {(times.IsEmpty ? 0 : System.Linq.Enumerable.Max(times.ToArray())):F6} {_pingDraws.Count - before} {Position.X + op.X:F6} {Position.Y + op.Y:F6} {pulseSize:F6}");
                    if (line != _radarAuditPreviousDraw)
                    {
                        GD.Print(line);
                        _radarAuditPreviousDraw = line;
                    }
                }
            }
        }

        // ---- maximized clickable'''),
    ])
    edit(source / "src/VortexArena.Server/Commands.cs", [
        ("        string? msg = ons.HandleOnsSpawnCommand(p, pos);", '''
        System.Console.WriteLine(FormattableString.Invariant($"RADAR_CLICK {pos.X:F6} {pos.Y:F6} {pos.Z:F6}"));
        string? msg = ons.HandleOnsSpawnCommand(p, pos);'''),
    ])

    if followup:
        edit(source / "game/net/NetGame.cs", [
            ("        radar.MapMaxXY = new Vector2(maxs.X, maxs.Y);", r'''        radar.MapMaxXY = new Vector2(maxs.X, maxs.Y);
        var model = bsp.Models[0];
        GD.Print(FormattableString.Invariant($"RADAR_RAW {model.Mins.X:F3} {model.Mins.Y:F3} {model.Maxs.X:F3} {model.Maxs.Y:F3}"));
        GD.Print(FormattableString.Invariant($"RADAR_BOUNDS {mins.X:F3} {mins.Y:F3} {maxs.X:F3} {maxs.Y:F3}"));'''),
        ])
        edit(source / "game/net/ServerNet.cs", [
            ('if (System.Environment.GetEnvironmentVariable("VORTEX_RADAR_FIXTURE") == "1" &&',
             'if (System.Environment.GetEnvironmentVariable("RADAR_VARIANT") != "followup" &&'),
            ("    private void SendWaypoints()\n    {", r'''    private byte[]? _radarAuditHeld;
    private bool _radarAuditCleared, _radarAuditReplayed, _radarAuditRecovered;
    private int _radarAuditDrops;

    private void SendWaypoints()
    {
        string path = System.Environment.GetEnvironmentVariable("VORTEX_RADAR_TRIGGER")!;
        if (System.IO.File.Exists(path) && _radarAuditMarker is null)
            _radarAuditMarker = VortexArena.Common.Gameplay.Waypoints.WaypointSprites.SpawnFixed(
                "here", new(-300, 100, 64), 0, new(0, 1, 0));
        if (!_radarAuditCleared && System.IO.File.Exists(path + ".clear"))
        {
            VortexArena.Common.Gameplay.Waypoints.WaypointSprites.Kill(_radarAuditMarker);
            _radarAuditCleared = true;
        }
        if (!_radarAuditRecovered && System.IO.File.Exists(path + ".recover"))
        {
            _radarAuditMarker = VortexArena.Common.Gameplay.Waypoints.WaypointSprites.SpawnFixed(
                "here", new(500, 900, 200), 0, new(0, 1, 0));
            _radarAuditRecovered = true;
        }'''),
            ("            bool hasVisible = _wpVisible.Count != 0;", r'''            _wpVisible.RemoveAll(wp => !ReferenceEquals(wp, _radarAuditMarker));
            if (!_radarAuditReplayed && _radarAuditHeld is { } older && System.IO.File.Exists(path + ".replay"))
            {
                _radarAuditReplayed = true;
                GD.Print("RADAR_REPLAY");
                SendPacket(st.PeerId, older, reliable: false);
            }
            bool hasVisible = _wpVisible.Count != 0;'''),
            ("            _scratchWriter.WriteLong(unchecked((int)++st.WaypointSequence));\n            _scratchWriter.WriteFloat(now);",
             "            _scratchWriter.WriteLong(unchecked((int)++st.WaypointSequence));\n            _scratchWriter.WriteFloat(16f);"),
            ("            SendPacket(st.PeerId, _scratchWriter.WrittenSpan, reliable: !hasVisible);", r'''            if (hasVisible && !_radarAuditRecovered)
            {
                if (_radarAuditHeld is not null)
                {
                    if (++_radarAuditDrops == 3) GD.Print("RADAR_LOSS 3");
                    continue;
                }
                _radarAuditHeld = _scratchWriter.WrittenSpan.ToArray();
            }
            if (!hasVisible || !_radarAuditRecovered || _radarAuditDrops > 0)
            {
                GD.Print($"RADAR_PACKET {st.WaypointSequence} {_wpVisible.Count} {!hasVisible} 16");
                if (_radarAuditRecovered) _radarAuditDrops = 0;
            }
            SendPacket(st.PeerId, _scratchWriter.WrittenSpan, reliable: !hasVisible);'''),
        ])
        edit(source / "game/net/ClientNet.cs", [
            ("        if (_waypointSequence is uint previous && unchecked((int)(snapshotSequence - previous)) <= 0)\n            return;",
             r'''        if (_waypointSequence is uint previous && unchecked((int)(snapshotSequence - previous)) <= 0)
        {
            GD.Print($"RADAR_STALE {snapshotSequence} {previous} {_waypoints.Count}");
            return;
        }'''),
            ("        _waypoints.AddRange(tmp);", r'''        _waypoints.AddRange(tmp);
        if (tmp.Count == 0 || snapshotSequence == 1 || tmp.Count == 1 && tmp[0].Origin.X == 500 && !_radarAuditRecovered)
        {
            GD.Print($"RADAR_STATE {snapshotSequence} {tmp.Count} {packetTime:F0}");
            _radarAuditRecovered |= tmp.Count == 1 && tmp[0].Origin.X == 500;
        }'''),
            ("    private uint? _waypointSequence;", "    private uint? _waypointSequence;\n    private bool _radarAuditRecovered;"),
        ])

    if links:
        # The native world replaces map point placeholders with live pads. Expose the two
        # fixture pads to the ordinary killtarget command through their existing map names.
        edit(source / "src/VortexArena.Common/Gameplay/GameTypes/OnslaughtControlPoint.cs", [
            ("            e.GtPointId = controlPointId;",
             "            e.GtPointId = controlPointId;\n"
             "            if (name is \"redcp1\" or \"redcp2\")\n"
             "            {\n"
             "                e.TargetName = name;\n"
             "                MapMover.IndexRegister(e);\n"
             "            }"),
        ])
        edit(source / "game/net/NetTransport.cs", [
            ("            _pendingConnects.Add((int)id);",
             "            Peer.GetPeer((int)id)?.SetTimeout(32, 120000, 180000);\n"
             "            _pendingConnects.Add((int)id);"),
        ])
        edit(source / "game/net/ServerNet.cs", [
            ("    private void SendRadarLinks()\n    {", r'''    private byte[]? _auditOldLinks;
    private bool _auditReplayedLinks;

    private void SendRadarLinks()
    {
        string? directory = System.Environment.GetEnvironmentVariable("VORTEX_RADAR_LINK_DIR");
        if (directory is not null && !_auditReplayedLinks && _auditOldLinks is { } older &&
            System.IO.File.Exists(System.IO.Path.Combine(directory, "replay-link")))
        {
            _auditReplayedLinks = true;
            foreach (PeerState st in _peers.Values)
                if (st.Accepted && st.Player is not null)
                    SendPacket(st.PeerId, older, reliable: false);
            GD.Print($"LINK_REPLAY {System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(older.AsSpan(1))}");
        }'''),
            ("                SendPacket(st.PeerId, _scratchWriter.WrittenSpan, reliable: count == 0);", r'''            {
                if (count > 0)
                    _auditOldLinks ??= _scratchWriter.WrittenSpan.ToArray();
                if (count == 0 && directory is not null)
                {
                    string clear = System.IO.Path.Combine(directory, "link-clear");
                    System.IO.File.WriteAllText(clear + ".tmp", Convert.ToHexString(_scratchWriter.WrittenSpan));
                    System.IO.File.Move(clear + ".tmp", clear, overwrite: true);
                }
                SendPacket(st.PeerId, _scratchWriter.WrittenSpan, reliable: count == 0);
            }'''),
        ])
        edit(source / "game/net/ClientNet.cs", [
            ("        if (_radarLinkSequence is uint previous && unchecked((int)(sequence - previous)) <= 0)\n            return;", r'''        if (_radarLinkSequence is uint previous && unchecked((int)(sequence - previous)) <= 0)
        {
            if (_radarLinks.Count == 0) GD.Print($"LINK_STALE {sequence} {previous} {_radarLinks.Count}");
            return;
        }'''),
            ("        _radarLinkSequence = sequence;", r'''        _radarLinkSequence = sequence;
        if (tmp.Count == 0) GD.Print($"LINK_CLEAR {sequence}");'''),
        ])
        edit(source / "game/hud/RadarPanel.cs", [
            ("    private Control _pingLayer", "    private int _auditDrawnLinks = -1;\n    private Control _pingLayer"),
            ("            foreach (ClientNet.RadarLink link in net.RadarLinks)", r'''            if (Maximized && _auditDrawnLinks != net.RadarLinks.Count)
            {
                _auditDrawnLinks = net.RadarLinks.Count;
                GD.Print($"LINK_HUD {_auditDrawnLinks}");
            }
            foreach (ClientNet.RadarLink link in net.RadarLinks)'''),
        ])
