// Port of qcsrc/common/mapobjects/func/stardust.qc (func_stardust) — SVQC half.
//
// func_stardust is the simplest of the long-tail entities: a (usually inline-brush) prop that wears the
// EF_STARDUST effect bit so the client renders the sparkle particle field over it. The server-side spawnfunc
// just precaches/sets the model, stamps EF_STARDUST, and parks a 0.25s think.
//
// In QC the think calls CSQCMODEL_AUTOUPDATE (re-net the csqcmodel entity each quarter second so a moving
// stardust prop stays in sync). The port's csqcmodel networking is the shared-entity facade (the same seam
// every server-side fx uses), so the think is a no-op heartbeat here — it only needs to keep rescheduling so
// a future csqcmodel autoupdate consumer can hang off it. The VISUAL (the EF_STARDUST particle field) is the
// client's effect renderer reading Entity.Effects; the bit being set is the whole server contract.

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary><c>func_stardust</c> — a prop wearing EF_STARDUST (sparkle particles). Registered by <see cref="MapObjectsRegistry"/>.</summary>
public static class Stardust
{
    /// <summary>QC stardust think interval (stardust.qc:5) — re-update the csqcmodel every quarter second.</summary>
    private const float ThinkInterval = 0.25f;

    /// <summary><c>spawnfunc(func_stardust)</c> (stardust.qc:8-18).</summary>
    public static void StardustSetup(Entity this_)
    {
        this_.ClassName = "func_stardust";

        // QC: if(this.model != "") { precache_model(this.model); _setmodel(this, this.model); }
        if (!string.IsNullOrEmpty(this_.Model) && Api.Services is not null)
            Api.Entities.SetModel(this_, this_.Model);

        this_.Effects = EffectFlags.Stardust; // QC: this.effects = EF_STARDUST

        // QC: CSQCMODEL_AUTOINIT(this) — net-link the csqcmodel. In the port the shared-entity facade IS the
        // csqcmodel channel (every server-side fx reads off it), so there is no per-entity Net_LinkEntity to do.

        // QC: setthink(this, func_stardust_think); this.nextthink = time + 0.25;
        this_.Think = StardustThink;
        this_.NextThink = MapMover.Now() + ThinkInterval;

        MapMover.IndexRegister(this_);
    }

    /// <summary>Port of <c>func_stardust_think</c> (stardust.qc:3-7): reschedule the csqcmodel autoupdate heartbeat.</summary>
    private static void StardustThink(Entity this_)
    {
        // QC: this.nextthink = time + 0.25; CSQCMODEL_AUTOUPDATE(this);
        this_.NextThink = MapMover.Now() + ThinkInterval;
    }
}
