// Port of the porto_ticker mutator — common/weapons/weapon/porto.qc:96-100.

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The porto_ticker mutator — port of <c>REGISTER_MUTATOR(porto_ticker, true)</c> + its SV_StartFrame hook
/// (common/weapons/weapon/porto.qc:96-100):
/// <code>FOREACH_CLIENT(IS_PLAYER(it), it.porto_forbidden = max(0, it.porto_forbidden - 1));</code>
/// Each server frame this decrements every live player's <c>porto_forbidden</c> cooldown toward zero. The
/// cooldown is SET (to 2) when a Race/CTS player respawns at a checkpoint (QC race.qc:731, ported in
/// <c>Race.RetractPlayer</c>), so they can't immediately re-fire a porto across the respawn; the ticker counts
/// it back down over the next two frames. Without this the cooldown <see cref="WeaponSlotState.PortoForbidden"/>
/// the fire gate reads (Porto.WrThink) could never fall, so it was effectively dead.
///
/// QC stores <c>porto_forbidden</c> as a player field; this port hangs it on the per-weapon-slot scratch state
/// (where <c>Porto.WrThink</c> reads it), so the ticker decrements every populated weapon slot of each player —
/// the slot the porto fire gate consults.
///
/// SEAM: hooks <see cref="MutatorHooks.SvStartFrame"/> (the Common-side per-frame chain) like RandomGravity; the
/// server's per-frame loop pumps that chain (GameWorld.StartFrame / ServerHooks.FireStartFrame).
/// </summary>
[Mutator]
public sealed class PortoTickerMutator : MutatorBase
{
    public PortoTickerMutator() => NetName = "porto_ticker";

    // QC: REGISTER_MUTATOR(porto_ticker, true) — always on.
    public override bool IsEnabled => Api.Services is not null;

    private HookHandler<MutatorHooks.SvStartFrameArgs>? _onStartFrame;

    public override void Hook()
    {
        _onStartFrame ??= OnStartFrame;
        MutatorHooks.SvStartFrame.Add(_onStartFrame);
    }

    public override void Unhook()
    {
        if (_onStartFrame is not null) MutatorHooks.SvStartFrame.Remove(_onStartFrame);
    }

    // MUTATOR_HOOKFUNCTION(porto_ticker, SV_StartFrame):
    //   FOREACH_CLIENT(IS_PLAYER(it), it.porto_forbidden = max(0, it.porto_forbidden - 1));
    private bool OnStartFrame(ref MutatorHooks.SvStartFrameArgs args)
    {
        if (Api.Services is null) return false;
        foreach (Entity it in Api.Entities.FindByClass("player"))
        {
            if (it.IsFreed || (it.Flags & EntFlags.Client) == 0) continue;
            if (it is Player ply && ply.IsObserver) continue; // QC IS_PLAYER excludes observers
            it.ForEachWeaponSlot(s =>
            {
                if (s.PortoForbidden > 0) s.PortoForbidden -= 1; // QC max(0, porto_forbidden - 1)
            });
        }
        return false;
    }
}
