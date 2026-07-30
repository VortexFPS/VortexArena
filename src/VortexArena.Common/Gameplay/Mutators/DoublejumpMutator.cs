// Port of common/mutators/mutator/doublejump/doublejump.qc

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Doublejump mutator — port of common/mutators/mutator/doublejump/doublejump.qc. With
/// <c>sv_doublejump</c> set, a player may jump again while standing on (or within a hair of) a walkable
/// surface — NOT a free midair re-jump. On the PlayerJump hook it traceboxes 0.01u below the player's feet;
/// if that hits a surface whose normal is steep-enough-to-stand-on (<c>trace_plane_normal_z &gt; 0.7</c>) it
/// grants the extra jump (sets the out <c>doublejump</c> flag) AND clips the into-plane velocity component so
/// the re-jump isn't fighting a downward slide. Enabled by the <c>sv_doublejump</c> cvar
/// (xonotic-server.cfg default <b>0</b>; the Q2/Q2a/Fruit/Warsow/XDF physics presets set it 1).
///
/// Ported faithfully: the tracebox down 0.01u with the player's bbox, the
/// <c>trace_fraction &lt; 1 &amp;&amp; trace_plane_normal.z &gt; 0.7</c> gate, the
/// <c>M_ARGV(2, bool) = true</c> grant (here <see cref="MutatorHooks.PlayerJumpArgs.Multijump"/>), and the
/// velocity clip <c>f = velocity·normal; if (f &lt; 0) velocity -= f * normal</c> (only the INTO-surface,
/// negative-dot component is removed — a rising player is untouched). The matching
/// <c>PlayerPhysics.cs</c> change makes the air-jump grant start <c>false</c> (so <c>sv_doublejump</c> no
/// longer unconditionally pre-grants it), leaving this mutator the sole governor — and the default-0 path
/// byte-identical to before (the golden movement-parity traces are unaffected).
/// </summary>
[Mutator]
public sealed class DoublejumpMutator : MutatorBase
{
    public DoublejumpMutator() => NetName = "doublejump";

    // QC SVQC: REGISTER_MUTATOR(doublejump, autocvar_sv_doublejump);
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("sv_doublejump") != 0f;

    private HookHandler<MutatorHooks.PlayerJumpArgs>? _onJump;

    public override void Hook()
    {
        _onJump ??= OnPlayerJump;
        MutatorHooks.PlayerJump.Add(_onJump);
    }

    public override void Unhook()
    {
        if (_onJump is not null) MutatorHooks.PlayerJump.Remove(_onJump);
    }

    // MUTATOR_HOOKFUNCTION(doublejump, PlayerJump)
    private bool OnPlayerJump(ref MutatorHooks.PlayerJumpArgs args)
    {
        if (Api.Services is null) return false;
        Entity player = args.Player;

        // QC: if (PHYS_DOUBLEJUMP(player)) { ... } — PHYS_DOUBLEJUMP(player) == STAT(DOUBLEJUMP, player), the
        // per-player doublejump stat = Physics_ClientOption(this, "doublejump", autocvar_sv_doublejump). The
        // physics call site supplies it as args.DoubleJump (from MovementParameters.DoubleJump). When it differs
        // from sv_doublejump (g_physics_clientselect on, client picked a preset that flips doublejump) this
        // honors the per-player value. When not supplied (null — e.g. unit tests) fall back to the raw cvar,
        // which equals the stat in stock play. (SVQC registration is still gated on sv_doublejump via IsEnabled.)
        bool perPlayerDoublejump = args.DoubleJump ?? (Api.Cvars.GetFloat("sv_doublejump") != 0f);
        if (!perPlayerDoublejump) return false;

        // tracebox(player.origin + '0 0 0.01', player.mins, player.maxs, player.origin - '0 0 0.01', MOVE_NORMAL, player)
        Vector3 up = player.Origin + new Vector3(0f, 0f, 0.01f);
        Vector3 down = player.Origin - new Vector3(0f, 0f, 0.01f);
        TraceResult tr = Api.Trace.Trace(up, player.Mins, player.Maxs, down, MoveFilter.Normal, player);

        if (tr.Fraction < 1f && tr.PlaneNormal.Z > 0.7f)
        {
            // M_ARGV(2, bool) = true; — grant the air-jump.
            args.Multijump = true;

            // we MUST clip velocity here! (QC) — remove only the into-plane (negative dot) component.
            float f = QMath.Dot(player.Velocity, tr.PlaneNormal);
            if (f < 0f)
                player.Velocity -= f * tr.PlaneNormal;
        }
        return false;
    }
}
