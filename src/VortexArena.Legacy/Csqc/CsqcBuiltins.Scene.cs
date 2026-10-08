// Port of Base/darkplaces/clvm_cmds.c VM_CL_R_ClearScene, VM_CL_R_AddEntities, VM_CL_R_AddEntity,
// VM_CL_R_SetView, VM_CL_R_RenderScene, VM_CL_R_AddDynamicLight, VM_CL_R_PolygonBegin/Vertex/End,
// VM_CL_unproject, VM_CL_project, VM_CL_V_CalcRefdef, VM_CL_lightstyle, VM_CL_getlight; and csprogs.c
// CSQC_AddRenderEdict (the reading of the entity's fields). The 2D drawing builtins of the same table
// are in LegacyDrawBuiltins.cs, shared with the menu program's table.
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Csqc;

public sealed partial class CsqcBuiltins
{
    private const int MaxPolygonVertices = 65536;
    private const int PmfOnGround = 8;

    // csprogs.h VF_*: the properties the host answers itself (the rest belong to the presentation).
    private const int VfClViewAngles = 33, VfClViewAnglesX = 34, VfClViewAnglesY = 35, VfClViewAnglesZ = 36;

    private readonly List<LegacyPolygonVertex> _polygon = new();
    private string _polygonTexture = "";
    private int _polygonFlags;
    private bool _polygonIs2D, _polygonOpen;
    // "polygonbegin without draw2d arg has to guess": 2D once anything 2D was drawn this frame.
    private bool _polygonGuess2D;

    private void RegisterScene()
    {
        Forward(35, "lightstyle", LightStyle);
        Forward(92, "getlight", GetLight);
        Forward(300, "clearscene", ClearScene);
        Forward(301, "addentities", AddEntities);
        Forward(302, "addentity", AddEntity);
        Forward(303, "setproperty", SetView);
        Forward(304, "renderscene", RenderScene);
        Forward(305, "adddynamiclight", AddDynamicLight);
        Forward(306, "R_BeginPolygon", PolygonBegin);
        Forward(307, "R_PolygonVertex", PolygonVertex);
        Forward(308, "R_EndPolygon", PolygonEnd);
        Forward(309, "getproperty", SetView);
        Forward(310, "cs_unproject", Unproject);
        Forward(311, "cs_project", Project);
        Forward(640, "V_CalcRefdef", CalcRefdef);
    }

    // The 2D drawing builtins are the same C functions in the client and the menu table; the shared
    // implementation is LegacyDrawBuiltins, registered here under the client table's numbers.
    private void RegisterDraw()
    {
        LegacyDrawBuiltins draw = new(_vm, _presentation.Draw, _host.Services, _g.DrawFont, _g.DrawFontScale)
        {
            Drew2D = () => _polygonGuess2D = true,
        };
        Forward(315, "drawline", draw.DrawLine);
        Here(316, "iscachedpic", draw.IsCachedPic);
        Forward(317, "precache_pic", draw.PrecachePic);
        Forward(318, "draw_getimagesize", draw.GetImageSize);
        Forward(319, "freepic", draw.FreePic);
        Forward(320, "drawcharacter", draw.DrawCharacter);
        Forward(321, "drawstring", draw.DrawString);
        Forward(322, "drawpic", draw.DrawPic);
        Forward(323, "drawfill", draw.DrawFill);
        Forward(324, "drawsetcliparea", draw.DrawSetClipArea);
        Forward(325, "drawresetcliparea", draw.DrawResetClipArea);
        Forward(326, "drawcolorcodedstring", draw.DrawColorCodedString);
        Forward(327, "stringwidth", draw.StringWidth);
        Forward(328, "drawsubpic", draw.DrawSubPic);
        Forward(329, "drawrotpic", draw.DrawRotPic);
        Forward(356, "findfont", draw.FindFont);
        Forward(357, "loadfont", draw.LoadFont);
    }

    // ---- scene -------------------------------------------------------------------------------------

    // #300 void() clearscene
    private void ClearScene(QcVm vm)
    {
        Parms(0, "VM_CL_R_ClearScene");
        _polygonGuess2D = false;
        _presentation.Scene.ClearScene();
    }

    // CSQC_AddRenderEdict: gather what the renderer needs from the entity's fields.
    private bool AddRenderEdict(int edict, int renderNumber)
    {
        string? model = _host.ModelNameOf(edict);
        if (model is null) return false;
        QcVm vm = _vm;
        LegacyRenderEntity entity = new()
        {
            Edict = renderNumber,
            Model = model,
            Origin = vm.FieldVector(edict, _f.Origin),
            Angles = vm.FieldVector(edict, _f.Angles),
            // "for RF_USEAXIS entities, Predraw sets v_forward/v_right/v_up globals that are read by CSQC_AddRenderEdict"
            AxisForward = _host.GetVector(_g.VForward),
            AxisRight = _host.GetVector(_g.VRight),
            AxisUp = _host.GetVector(_g.VUp),
            Skin = Int(vm.FieldFloat(edict, _f.Skin)),
            RenderFlags = Int(vm.FieldFloat(edict, _f.RenderFlags)),
            Effects = Int(vm.FieldFloat(edict, _f.Effects)),
            ColorMap = Int(vm.FieldFloat(edict, _f.ColorMap)),
            Alpha = vm.FieldFloat(edict, _f.Alpha),
            Scale = vm.FieldFloat(edict, _f.Scale),
            ColorMod = vm.FieldVector(edict, _f.ColorMod),
            GlowMod = vm.FieldVector(edict, _f.GlowMod),
            Frame = vm.FieldFloat(edict, _f.Frame),
            Frame2 = vm.FieldFloat(edict, _f.Frame2),
            Frame3 = vm.FieldFloat(edict, _f.Frame3),
            Frame4 = vm.FieldFloat(edict, _f.Frame4),
            LerpFrac = vm.FieldFloat(edict, _f.LerpFrac),
            LerpFrac3 = vm.FieldFloat(edict, _f.LerpFrac3),
            LerpFrac4 = vm.FieldFloat(edict, _f.LerpFrac4),
            Frame1Time = vm.FieldFloat(edict, _f.Frame1Time),
            Frame2Time = vm.FieldFloat(edict, _f.Frame2Time),
            Frame3Time = vm.FieldFloat(edict, _f.Frame3Time),
            Frame4Time = vm.FieldFloat(edict, _f.Frame4Time),
            SkeletonIndex = Int(vm.FieldFloat(edict, _f.SkeletonIndex)),
            TagEntity = vm.FieldInt(edict, _f.TagEntity),
            TagIndex = Int(vm.FieldFloat(edict, _f.TagIndex)),
            ShaderTime = vm.FieldFloat(edict, _f.ShaderTime),
            ModelLightAmbient = vm.FieldVector(edict, _f.ModelLightAmbient),
            ModelLightDiffuse = vm.FieldVector(edict, _f.ModelLightDiffuse),
            ModelLightDir = vm.FieldVector(edict, _f.ModelLightDir),
        };
        return _presentation.Scene.AddEntity(entity);
    }

    // #301 void(float mask) addentities: the engine's entities, then every entity of the program -
    // each gets its think and its predraw run here, and is drawn if its drawmask matches.
    private void AddEntities(QcVm vm)
    {
        Parms(1, "VM_CL_R_AddEntities");
        int drawMask = ArgInt(0);
        _presentation.Scene.AddEngineEntities(drawMask);
        _host.SetFloat(_g.Time, (float)_state.Time);
        if (!vm.HasWatchedFields)
        {
            // The count is re-read every pass: a think function may spawn, and the new entity is visited too.
            for (int i = 1; i < vm.NumEdicts; i++) AddEntitiesVisit(vm, i, drawMask);
            return;
        }
        // The same pass over only the entities that can have something to do here: the VM keeps a bit
        // per entity that is clear only while its think, predraw and drawmask are all zero (CsqcHost
        // asks for that with WatchFields), and for such an entity every step below is a no-op. Xonotic's
        // client holds some 3,300 entities with ten kilobytes of fields each, about a tenth of which
        // draw or think; reading three fields of every one was a quarter of a millisecond of cache
        // misses a frame. The index is read as it stands at each step, so an entity a think function
        // spawns or arms further on is visited in its turn, exactly as the full pass visits it.
        if (_host.VerifyEntityIndex) VerifyEntityIndex(vm);
        for (int i = vm.NextWatched(1); i > 0; i = vm.NextWatched(i + 1)) AddEntitiesVisit(vm, i, drawMask);
        if (_host.VerifyEntityIndex) VerifyEntityIndex(vm);
    }

    private void AddEntitiesVisit(QcVm vm, int i, int drawMask)
    {
        if (vm.IsFree(i)) return;
        _host.Think(i);
        if (vm.IsFree(i)) return;
        _host.Predraw(i);
        if (vm.IsFree(i)) return;
        if ((Int(vm.FieldFloat(i, _f.DrawMask)) & drawMask) == 0)
        {
            // Nothing left for this pass to act on (the common case is an entity whose fields were
            // written once with zeroes, or one that has stopped drawing): out of the index until the
            // program writes one of the three again. The VM checks that all three are zero itself.
            if (vm.PeekField(i, _f.DrawMask) == 0) vm.ClearWatched(i);
            return;
        }
        AddRenderEdict(i, i);
    }

    // The index's promise, checked (CsqcHostOptions.VerifyEntityIndex): an entity it leaves out has
    // nothing the pass would act on.
    private void VerifyEntityIndex(QcVm vm)
    {
        if (vm.VerifyMirrors() is { } difference) throw Fault("field mirror: " + difference);
        for (int i = 1; i < vm.NumEdicts; i++)
        {
            if (vm.IsFree(i) || vm.IsWatched(i)) continue;
            // PeekField: looking must not mark.
            if (vm.PeekField(i, _f.Think) != 0 || vm.PeekField(i, _f.Predraw) != 0 || vm.PeekField(i, _f.DrawMask) != 0)
                throw Fault($"entity index: entity {i} has think {vm.PeekField(i, _f.Think)}, predraw {vm.PeekField(i, _f.Predraw)}, drawmask bits {vm.PeekField(i, _f.DrawMask):X8} but is not marked");
        }
    }

    // #302 void(entity ent) addentity
    private void AddEntity(QcVm vm)
    {
        Parms(1, "VM_CL_R_AddEntity");
        AddRenderEdict(vm.ArgEdict(0), 0);
    }

    private static bool IsVectorProperty(int property) => property is 1 or 4 or 7 or 8 or 11 or 15 or 33 or 203;

    // #303 float(float property, ...) setproperty, and #309 getproperty: with one argument the
    // property is returned ("make this function be able to return previously set property if new
    // value is not given").
    private void SetView(QcVm vm)
    {
        Parms(1, 3, "VM_CL_R_SetView");
        int property = ArgInt(0);

        if (vm.ArgCount < 2)
        {
            QcVector value;
            switch (property)
            {
                // cl.viewangles is input state, not a property of the view.
                case VfClViewAngles: value = _state.ViewAngles; break;
                case VfClViewAnglesX: value = new QcVector(_state.ViewAngles.X, 0, 0); break;
                case VfClViewAnglesY: value = new QcVector(_state.ViewAngles.Y, 0, 0); break;
                case VfClViewAnglesZ: value = new QcVector(_state.ViewAngles.Z, 0, 0); break;
                default:
                    if (!_presentation.Scene.GetProperty(property, out value))
                    {
                        vm.ReturnFloat(0);
                        Warning($"VM_CL_R_GetView : unknown parm {property}\n");
                        return;
                    }
                    break;
            }
            if (IsVectorProperty(property)) vm.ReturnVector(value);
            else vm.ReturnFloat(value.X);
            return;
        }

        QcVector a = vm.ArgVector(1);
        QcVector b = vm.ArgCount >= 3 ? vm.ArgVector(2) : default;
        QcVector angles = _state.ViewAngles;
        switch (property)
        {
            case VfClViewAngles: _state.ViewAngles = a; break;
            case VfClViewAnglesX: angles.X = a.X; _state.ViewAngles = angles; break;
            case VfClViewAnglesY: angles.Y = a.X; _state.ViewAngles = angles; break;
            case VfClViewAnglesZ: angles.Z = a.X; _state.ViewAngles = angles; break;
            default:
                if (!_presentation.Scene.SetProperty(property, a, b))
                {
                    vm.ReturnFloat(0);
                    Warning($"VM_CL_R_SetView : unknown parm {property}\n");
                    return;
                }
                break;
        }
        vm.ReturnFloat(1);
    }

    // #304 void() renderscene
    private void RenderScene(QcVm vm)
    {
        Parms(0, "VM_CL_R_RenderScene");
        _presentation.Scene.RenderScene();
        _polygonGuess2D = false;
    }

    // #305 void(vector org, float radius, vector lightcolours[, float style, string cubemapname, float pflags]) adddynamiclight
    private void AddDynamicLight(QcVm vm)
    {
        Parms(3, 8, "VM_CL_R_AddDynamicLight");
        LegacyDynamicLight light = new()
        {
            Origin = vm.ArgVector(0),
            Radius = vm.ArgFloat(1),
            Color = vm.ArgVector(2),
            Style = -1,
            Flags = 2 | 128, // PFLAGS_CORONA | PFLAGS_FULLDYNAMIC
            Forward = _host.GetVector(_g.VForward),
            Right = _host.GetVector(_g.VRight),
            Up = _host.GetVector(_g.VUp),
        };
        if (vm.ArgCount >= 4)
        {
            light.Style = ArgInt(3);
            if (light.Style >= DpProtocol.MaxLightStyles) light.Style = -1;
        }
        if (vm.ArgCount >= 5) light.Cubemap = vm.ArgString(4);
        if (vm.ArgCount >= 6) light.Flags = ArgInt(5);
        _presentation.Scene.AddDynamicLight(light);
    }

    // #306 void(string texturename, float flag[, float is2d]) R_BeginPolygon
    private void PolygonBegin(QcVm vm)
    {
        Parms(2, 3, "VM_CL_R_PolygonBegin");
        string texture = vm.ArgString(0);
        _polygonTexture = texture.Length == 0 ? "$whiteimage" : texture;
        _polygonFlags = ArgInt(1);
        _polygonIs2D = vm.ArgCount >= 3 ? vm.ArgFloat(2) != 0 : _polygonGuess2D;
        _polygon.Clear();
        _polygonOpen = true;
    }

    // #307 void(vector org, vector texcoords, vector rgb, float alpha) R_PolygonVertex
    private void PolygonVertex(QcVm vm)
    {
        Parms(4, "VM_CL_R_PolygonVertex");
        if (!_polygonOpen)
        {
            Warning("VM_CL_R_PolygonVertex: VM_CL_R_PolygonBegin wasn't called\n");
            return;
        }
        // The C grows its vertex array without limit; a polygon this large is a runaway loop.
        if (_polygon.Count >= MaxPolygonVertices) throw Fault($"VM_CL_R_PolygonVertex: more than {MaxPolygonVertices} vertices in one polygon");
        _polygon.Add(new LegacyPolygonVertex { Position = vm.ArgVector(0), TexCoord = vm.ArgVector(1), Color = vm.ArgVector(2), Alpha = vm.ArgFloat(3) });
    }

    // #308 void() R_EndPolygon
    private void PolygonEnd(QcVm vm)
    {
        Parms(0, "VM_CL_R_PolygonEnd");
        if (!_polygonOpen)
        {
            Warning("VM_CL_R_PolygonEnd: VM_CL_R_PolygonBegin wasn't called\n");
            return;
        }
        _polygonOpen = false;
        _presentation.Scene.DrawPolygon(_polygonTexture, _polygonFlags, _polygonIs2D, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_polygon));
        _polygon.Clear();
    }

    // #310 vector(vector v) cs_unproject
    private void Unproject(QcVm vm)
    {
        Parms(1, "VM_CL_unproject");
        vm.ReturnVector(_presentation.Scene.Unproject(vm.ArgVector(0)));
    }

    // #311 vector(vector v) cs_project
    private void Project(QcVm vm)
    {
        Parms(1, "VM_CL_project");
        vm.ReturnVector(_presentation.Scene.Project(vm.ArgVector(0)));
    }

    // #640 void(entity e, float refdefflags) V_CalcRefdef
    private void CalcRefdef(QcVm vm)
    {
        Parms(2, "VM_CL_V_CalcRefdef");
        int e = vm.ArgEdict(0);
        int flags = ArgInt(1);
        LegacyRefdefInput input = new()
        {
            Edict = e,
            Origin = vm.FieldVector(e, _f.Origin),
            Angles = vm.FieldVector(e, _f.Angles),
            Velocity = vm.FieldVector(e, _f.Velocity),
            ViewHeight = _f.ViewOfs >= 0 ? vm.FieldVector(e, _f.ViewOfs).Z : 0,
            OnGround = (Int(vm.FieldFloat(e, _f.PmoveFlags)) & PmfOnGround) != 0,
            Teleported = (flags & 1) != 0,   // REFDEFFLAG_TELEPORTED
            Jumping = (flags & 2) != 0,      // REFDEFFLAG_JUMPING
            Dead = (flags & 4) != 0,         // REFDEFFLAG_DEAD
            Intermission = (flags & 8) != 0, // REFDEFFLAG_INTERMISSION
        };
        _state.Intermission = input.Intermission ? 1 : 0;
        _presentation.Scene.CalcRefdef(input);
    }

    // #35 void(float style, string value) lightstyle
    private void LightStyle(QcVm vm)
    {
        Parms(2, "VM_CL_lightstyle");
        int style = ArgInt(0);
        string map = vm.ArgString(1);
        if ((uint)style >= DpProtocol.MaxLightStyles)
        {
            Warning("VM_CL_lightstyle >= MAX_LIGHTSTYLES\n");
            return;
        }
        _presentation.Scene.SetLightStyle(style, map.Length > 63 ? map[..63] : map); // MAX_STYLESTRING
    }

    // #92 vector(vector org[, float lpflag]) getlight
    private void GetLight(QcVm vm)
    {
        Parms(1, 3, "VM_CL_getlight");
        int flags = vm.ArgCount >= 2 ? ArgInt(1) : 1; // LP_LIGHTMAP
        vm.ReturnVector(_presentation.Scene.GetLight(vm.ArgVector(0), flags, out QcVector ambient, out QcVector diffuse, out QcVector direction));
        _host.SetVector(_g.GetLightAmbient, ambient);
        _host.SetVector(_g.GetLightDiffuse, diffuse);
        _host.SetVector(_g.GetLightDir, direction);
    }
}
