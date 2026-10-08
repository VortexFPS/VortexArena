// Port of Base/darkplaces/console.c Con_DrawNotify as far as the chat area goes (the lines a server
// prints with a chat mark, which the engine and not the program draws). The 2D drawing builtins
// themselves - pictures, text, fonts, clip areas - are LegacyCanvas.cs, shared with the menu program.
using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Presentation;
using VortexArena.QuakeC;

namespace VortexArena.Game.Legacy;

public sealed partial class GodotLegacyPresentation
{
    /// <summary>The program's 2D output: picture cache, font slots, and this frame's draw list.</summary>
    public LegacyCanvas Canvas { get; }
    /// <summary>The 2D picture cache, as far as existence and size go.</summary>
    public LegacyPictureCatalog Pictures => Canvas.Pictures;
    /// <summary>dp_fonts: the font slots, filled by the session's loadfont commands and the program's loadfont builtin.</summary>
    public LegacyFontSlots Fonts => Canvas.Fonts;
    /// <summary>This frame's recorded 2D drawing.</summary>
    public LegacyDrawList DrawList => Canvas.DrawList;

    internal Texture2D? LoadPictureTexture(string name) => Canvas.LoadPictureTexture(name);

    /// <summary>The loadfont console command (LegacyCanvas.LoadFontCommand).</summary>
    public void LoadFontCommand(IReadOnlyList<string> argv) => Canvas.LoadFontCommand(argv);

    /// <summary>
    /// console.c Con_DrawNotify, the chat area: the engine, not the program, draws the lines that were printed
    /// with a chat mark (a first byte of 1, 2 or 3 - Con_MaskPrint), and Xonotic prints its join, kill and chat
    /// lines that way. Xonotic's chat panel only tells the engine where: con_chatrect_x / _y (fractions of the
    /// virtual screen), con_chatwidth, con_chat (how many lines), con_chatsize and con_chattime. The newest
    /// line is at the bottom of the rectangle ("Con_DrawNotifyRect(..., align_y 1.0)"). Lines are not wrapped:
    /// one that is wider than the area is cut by the font, not continued.
    /// </summary>
    public void DrawChatArea(IReadOnlyList<(double Time, string Text)> lines, double now)
    {
        int count = (int)_cvars.GetFloat("con_chat");
        if (count <= 0 || lines.Count == 0) return;
        count = Math.Min(count, 64);
        float size = _cvars.GetFloat("con_chatsize");
        if (!(size > 0) || size > 256) size = 8;
        float life = _cvars.GetFloat("con_chattime");
        if (!(life > 0)) return;
        float x, top;
        if (_cvars.GetFloat("con_chatrect") != 0)
        {
            x = _cvars.GetFloat("con_chatrect_x") * View.ConWidth;
            top = _cvars.GetFloat("con_chatrect_y") * View.ConHeight;
        }
        else
        {
            // con_chatpos < 0: "-chatpos-1 empty lines" below the chat and its input line, from the bottom.
            int position = (int)_cvars.GetFloat("con_chatpos");
            x = 0;
            top = position < 0 ? View.ConHeight - (-position - 1 + count + 1) * size : 0;
        }
        if (!float.IsFinite(x) || !float.IsFinite(top)) return;
        int font = Fonts.Find("chat");
        int shown = 0;
        float bottom = top + count * size;
        for (int i = lines.Count - 1; i >= 0 && shown < count; i--)
        {
            (double time, string text) = lines[i];
            if (now - time > life) break;
            shown++;
            DrawList.Text(new LegacyText
            {
                Position = new QcVector(x, bottom - shown * size, 0), Scale = new QcVector(size, size, 0), Color = new QcVector(1, 1, 1), Alpha = 1,
                Text = text, Font = font, FontScale = new QcVector(1, 1, 0),
            });
        }
    }

    /// <summary>
    /// cl_screen.c SCR_DrawPause: the engine's own "PAUSE" picture (gfx/pause), centred, while the server has
    /// the game paused - which a listen server does whenever its only player has a menu or the console open.
    /// </summary>
    public void DrawPause()
    {
        if (_cvars.Has("scr_showpause") && _cvars.GetFloat("scr_showpause") == 0) return;
        QcVector size = Canvas.ImageSize("gfx/pause");
        if (!(size.X > 0) || !(size.Y > 0)) return;
        DrawList.ResetClip();
        DrawList.Picture(new LegacyPicture
        {
            Name = "gfx/pause", Position = new QcVector((View.ConWidth - size.X) / 2, (View.ConHeight - size.Y) / 2, 0), Size = size,
            Color = new QcVector(1, 1, 1), Alpha = 1, SourceSize = new QcVector(1, 1, 0),
        });
    }

    /// <summary>Developer aid: one frame's 2D draw list and view, as text (see LegacyGame.CaptureForReview).</summary>
    public void DumpFrame(Action<string> log)
    {
        System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
        log(string.Create(inv, $"dump: con {View.ConWidth}x{View.ConHeight} layer size {_drawLayer.Size} visible {_drawLayer.IsVisibleInTree()} segments {_drawLayer.SegmentsInUse} commands {DrawList.Count} view org {View.Origin.X:0.#} {View.Origin.Y:0.#} {View.Origin.Z:0.#} ang {View.Angles.X:0.#} {View.Angles.Y:0.#} {View.Angles.Z:0.#} fovy {View.VerticalFovDegrees:0.#}"));
        foreach (string line in _debugEntities) log("dump: " + line);
        foreach (string line in _debugSounds) log("dump: " + line);
        _debugSounds.Clear();
        int playing = 0, loops = 0, statics = 0;
        foreach (Voice voice in _voices)
            if (voice.InUse) { playing++; if (voice.Loop) loops++; if (voice.Static) statics++; }
        log($"dump: voices in use {playing} (looping {loops}, static {statics}) of {_voices.Count}");
        log("dump: effects since last dump: " + string.Join(", ", System.Linq.Enumerable.Select(_debugEffects, kv => kv.Key + " x" + kv.Value)));
        _debugEffects.Clear();
        if (_host is { } host)
        {
            QcVm vm = host.Vm;
            float G(string n) => vm.FindGlobal(n) is { } d ? vm.GlobalFloat(d.Offset) : float.NaN;
            log(string.Create(inv, $"dump: cvar r_drawviewmodel '{_cvars.GetString("r_drawviewmodel")}' chase_active '{_cvars.GetString("chase_active")}' autocvar_r_drawviewmodel {G("autocvar_r_drawviewmodel")} maxclients {G("maxclients")} player_localentnum {G("player_localentnum")} intermission {G("intermission")} autocvar_chase_active {G("autocvar_chase_active")}"));
            int aw = vm.FindField("activeweapon")?.Offset ?? -1, sw = vm.FindField("switchweapon")?.Offset ?? -1;
            for (int e = 1; e < vm.NumEdicts; e++)
            {
                if (vm.IsFree(e)) continue;
                string cls = vm.GetString(vm.FieldInt(e, host.Fields.ClassName));
                if (cls is not ("viewmodel" or "weaponchild")) continue;
                log(string.Create(inv, $"dump: edict {e} {cls} model \"{vm.GetString(vm.FieldInt(e, host.Fields.Model))}\" modelindex {vm.FieldFloat(e, host.Fields.ModelIndex)} name {host.ModelNameOf(e)} drawmask {vm.FieldFloat(e, host.Fields.DrawMask)} rf {vm.FieldFloat(e, host.Fields.RenderFlags)} alpha {vm.FieldFloat(e, host.Fields.Alpha)} activeweapon {(aw >= 0 ? vm.FieldInt(e, aw) : -1)} switchweapon {(sw >= 0 ? vm.FieldInt(e, sw) : -1)} origin {vm.FieldVector(e, host.Fields.Origin)}"));
            }
        }
        int n = 0;
        foreach (LegacyDrawCommand c in DrawList.Commands)
        {
            if (n++ >= 400) break;
            string extra = c.Kind switch
            {
                LegacyDrawKind.Picture => $"\"{c.Text}\" tex {(LoadPictureTexture(c.Text ?? "") is { } t ? t.GetSize().ToString() : "MISSING")} src {c.SourceX:0.##},{c.SourceY:0.##} {c.SourceWidth:0.##}x{c.SourceHeight:0.##}{(c.Rotated ? $" rot {c.Angle:0.#}" : "")}",
                LegacyDrawKind.Text => $"font {c.Font} \"{(c.Text is { Length: > 60 } s ? s[..60] : c.Text)}\"",
                LegacyDrawKind.Polygon => $"poly {c.VertexCount} \"{c.Text}\"",
                _ => "",
            };
            log(string.Create(inv, $"dump: {n,3} {c.Kind,-8} f{c.Flags} @{c.X:0.#},{c.Y:0.#} {c.Width:0.#}x{c.Height:0.#} rgba {c.Color.R:0.##} {c.Color.G:0.##} {c.Color.B:0.##} {c.Color.A:0.##} {extra}"));
        }
    }
}
