// Port of qcsrc/client/csqcmodel_hooks.qh (EF_*/MF_* bit constants) + the engine EF_* values from
// Base/darkplaces/dpdefs/{progsdefs.qc,dpextensions.qc}. The low EF_* bits are ENGINE constants (NOT the
// BIT()-defined CSQC-only high bits in csqcmodel_hooks.qh) — they are pinned here against the darkplaces defs
// so a wrong bit can't silently mis-fire lights/trails. CSQCMODEL_EF_RESPAWNGHOST aliases EF_SELECTABLE
// (common/csqcmodel_settings.qh:110). The MF_* set + the MF→trail mapping mirror CSQCModel_Effects_Apply
// (csqcmodel_hooks.qc:611-632).
//
// This is a SEPARATE client-side constant set, intentionally numerically identical to
// XonoticGodot.Common.Framework.EffectFlags for the bits they share (FullBright=512, Additive=32, NoDraw=16,
// NoShadow=4096, Stardust=2048) — kept apart so the server-side mutator file stays untouched (T35/T40 own it).
// NOTE: the server file's EffectFlags.FullBright=8 / NoShadow=8192 are WRONG vs the engine; the canonical
// engine values are used here (FullBright=512, NoShadow=4096). See CsqcModelHooksTests for the assertions.

namespace XonoticGodot.Engine.Simulation;

/// <summary>
/// The DarkPlaces engine <c>EF_*</c> entity-effect bits and the CSQC <c>MF_*</c> model-flag bits, plus the
/// <see cref="ModelFlagToTrail"/> / <see cref="BrightFieldTrail"/> trail mapping used by the ported
/// <c>CSQCModel_Effects_Apply</c>. Values verified against <c>darkplaces/dpdefs</c>.
/// </summary>
public static class CsqcModelEffectFlags
{
    // --- engine EF_* (darkplaces/dpdefs/progsdefs.qc + dpextensions.qc) ---
    public const int EF_BRIGHTFIELD       = 1;        // progsdefs.qc:382
    public const int EF_MUZZLEFLASH       = 2;        // progsdefs.qc:383 (ignored by Effects_Apply)
    public const int EF_BRIGHTLIGHT       = 4;        // progsdefs.qc:384
    public const int EF_DIMLIGHT          = 8;        // progsdefs.qc:385
    public const int EF_NODRAW            = 16;       // dpextensions.qc:149
    public const int EF_ADDITIVE          = 32;       // dpextensions.qc:93
    public const int EF_BLUE              = 64;       // dpextensions.qc:101
    public const int EF_RED               = 128;      // dpextensions.qc:179
    public const int EF_NOGUNBOB          = 256;      // dpextensions.qc:157 (ignored)
    public const int EF_FULLBRIGHT        = 512;      // dpextensions.qc:133
    public const int EF_FLAME             = 1024;     // dpextensions.qc:125
    public const int EF_STARDUST          = 2048;     // dpextensions.qc:197
    public const int EF_NOSHADOW          = 4096;     // dpextensions.qc:171
    public const int EF_NODEPTHTEST       = 8192;     // dpextensions.qc:141
    public const int EF_SELECTABLE        = 16384;    // dpextensions.qc:2394
    public const int EF_DOUBLESIDED       = 32768;    // dpextensions.qc:109 (= csqcmodel_hooks.qh BIT(15))
    public const int EF_NOSELFSHADOW      = 65536;    // csqcmodel_hooks.qh:27 BIT(16)
    public const int EF_DYNAMICMODELLIGHT = 131072;   // dpextensions.qc:117 (= csqcmodel_hooks.qh BIT(17))
    public const int EF_SHOCK             = 262144;   // common/constants.qh:103 BIT(18) (Xonotic-specific; arc/shock light+particles)
    public const int EF_RESTARTANIM_BIT   = 1048576;  // dpextensions.qc:187 (= csqcmodel_hooks.qh BIT(20))
    public const int EF_TELEPORT_BIT      = 2097152;  // dpextensions.qc:205 (= csqcmodel_hooks.qh BIT(21))
    public const int EF_LOWPRECISION      = 4194304;  // dpextensions.qc:274 (ignored)

    /// <summary>CSQCMODEL_EF_RESPAWNGHOST = EF_SELECTABLE (common/csqcmodel_settings.qh:110) — the bit the
    /// server sets on a respawn ghost; Effects_Apply masks it off the effect set and turns it into RF_ADDITIVE.</summary>
    public const int CSQCMODEL_EF_RESPAWNGHOST = EF_SELECTABLE;

    // --- CSQC MF_* model flags (csqcmodel_hooks.qh:32-39 — BIT(0)..BIT(7)) ---
    public const int MF_ROCKET  = 1 << 0;  // leave a trail (also the jetpack loop)
    public const int MF_GRENADE = 1 << 1;  // leave a trail
    public const int MF_GIB     = 1 << 2;  // leave a trail
    public const int MF_ROTATE  = 1 << 3;  // rotate (bonus items)
    public const int MF_TRACER  = 1 << 4;  // green split trail
    public const int MF_ZOMGIB  = 1 << 5;  // small blood trail
    public const int MF_TRACER2 = 1 << 6;  // orange split trail
    public const int MF_TRACER3 = 1 << 7;  // purple trail

    /// <summary>
    /// The trail-effect name a model-flag set maps to, mirroring the MF_* cascade in
    /// <c>CSQCModel_Effects_Apply</c> (csqcmodel_hooks.qc:611-632). QC applies them in this fixed order and the
    /// LAST matching one wins (later assignments to <c>tref</c> overwrite earlier ones), so we test the same
    /// order. Returns <c>null</c> when no MF_* trail bit is set (the caller then keeps any EF_BRIGHTFIELD trail).
    /// </summary>
    public static string? ModelFlagToTrail(int modelFlags)
    {
        string? tref = null;
        if ((modelFlags & MF_ROCKET) != 0)  tref = "TR_ROCKET";
        if ((modelFlags & MF_GRENADE) != 0) tref = "TR_GRENADE";
        if ((modelFlags & MF_GIB) != 0)     tref = "TR_BLOOD";
        // MF_ROTATE has no trail (it sets RF_USEAXIS + makevectors), so it doesn't touch tref.
        if ((modelFlags & MF_TRACER) != 0)  tref = "TR_WIZSPIKE";
        if ((modelFlags & MF_ZOMGIB) != 0)  tref = "TR_SLIGHTBLOOD";
        if ((modelFlags & MF_TRACER2) != 0) tref = "TR_KNIGHTSPIKE";
        if ((modelFlags & MF_TRACER3) != 0) tref = "TR_VORESPIKE";
        return tref;
    }

    /// <summary>The trail EF_BRIGHTFIELD sets (csqcmodel_hooks.qc:554-555: <c>tref = EFFECT_TR_NEXUIZPLASMA</c>).</summary>
    public const string BrightFieldTrail = "TR_NEXUIZPLASMA";

    /// <summary>
    /// The role-glow EF_* bits a Wave-2/3 presentation driver sets on a player to tint its dynamic light by
    /// team/powerup role (csqcmodel_hooks.qc Effects_Apply consumes EF_BLUE → blue light, EF_RED → red light;
    /// the Powerups mutator sets EF_BLUE for Strength and EF_RED for Shield, strength.qc:31 / shield.qc:31). This
    /// returns the EF_* bit-set for a role so a caller can OR it into the per-player forced-effects channel
    /// (<see cref="ForcedEffectFlags"/>) without re-deriving the constant. <paramref name="strength"/> → EF_BLUE,
    /// <paramref name="shield"/> → EF_RED (both → both bits, matching a double powerup).
    /// </summary>
    public static int RoleGlowFlags(bool strength, bool shield)
    {
        int e = 0;
        if (strength) e |= EF_BLUE;
        if (shield) e |= EF_RED;
        return e;
    }

    /// <summary>
    /// The full set of EF_* bits a per-player presentation override is allowed to FORCE on (OR into) a model's
    /// effect field via the per-player forced-effects channel: the role-glow lights (EF_BLUE/EF_RED), the
    /// powerup/flame visuals (EF_FLAME/EF_STARDUST/EF_SHOCK), the additive/fullbright render-flags, and the
    /// no-shadow/no-depth-test flags. Used to MASK an incoming forced-effects value so a presentation driver can
    /// never accidentally force engine-meaningful bits (NODRAW/SELECTABLE/MUZZLEFLASH) that aren't its concern.
    /// </summary>
    public const int ForcedEffectFlags =
        EF_BLUE | EF_RED | EF_FLAME | EF_STARDUST | EF_SHOCK |
        EF_ADDITIVE | EF_FULLBRIGHT | EF_BRIGHTLIGHT | EF_DIMLIGHT | EF_NOSHADOW | EF_NODEPTHTEST;
}
