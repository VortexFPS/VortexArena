using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Multijump mutator — port of common/mutators/mutator/multijump/multijump.qc. Lets a player jump
/// again in midair, up to a configured number of extra jumps. Enabled by the <c>g_multijump</c> cvar
/// (which is also the max extra-jump count; -1 = unlimited).
///
/// Ported: the ground reset of the jump counter (PlayerPhysics), the midair re-jump grant with the
/// press/release "ready" gate and the count/speed limits (PlayerJump), and the dodging-style horizontal
/// redirect (<c>g_multijump_dodging</c>) that aims the kept speed at the movement input on a re-jump.
///
/// Per-client opt-in/out/cap (QC <c>cl_multijump</c>, a REPLICATE'd per-client field): a player can set
/// <c>cl_multijump 0</c> to opt out or <c>2+</c> to disable it for themselves; <c>-1</c> (the default) defers to
/// the server's <c>g_multijump_client</c> default. The per-client value is sourced from
/// <see cref="ClientMultijumpProvider"/> (the host wires the replicated <c>cvar_cl_multijump</c> field, like
/// <c>Inventory.PriorityProvider</c>); when unwired it returns <c>-1</c> so the server default applies and the
/// default-server behavior is identical to Base.
/// </summary>
[Mutator]
public sealed class MultijumpMutator : MutatorBase
{
    /// <summary>QC STAT(MULTIJUMP) — max number of extra jumps (-1 = unlimited).</summary>
    public int MaxJumps = 1;

    /// <summary>QC STAT(MULTIJUMP_SPEED) — min upward velocity (z) required to allow a re-jump.</summary>
    public float MinSpeed = -999999f;

    /// <summary>QC STAT(MULTIJUMP_ADD) — when 0, the re-jump sets z velocity to jump speed instead of adding.</summary>
    public bool Add;

    /// <summary>QC STAT(MULTIJUMP_MAXSPEED) — if &gt;0, re-jump is disallowed above this horizontal speed.</summary>
    public float MaxSpeed;

    /// <summary>QC STAT(MULTIJUMP_DODGING) — redirect horizontal velocity toward the movement input on a re-jump.</summary>
    public bool Dodging;

    /// <summary>
    /// QC STAT(MULTIJUMP_CLIENT) / <c>autocvar_g_multijump_client</c> (BOOL, default 1) — the server-side per-client
    /// default substituted when a client's <c>cl_multijump</c> is <c>-1</c> ("server decides").
    /// </summary>
    public int ClientDefault = 1;

    /// <summary>
    /// PER-CLIENT opt source (QC <c>CS_CVAR(player).cvar_cl_multijump</c>, a REPLICATE'd field): the host wires the
    /// player's replicated <c>cl_multijump</c> value here (same pattern as <c>Inventory.PriorityProvider</c>). Null /
    /// unwired returns <c>-1</c> ("server decides") so the server <see cref="ClientDefault"/> applies and the
    /// default-server result matches Base exactly.
    /// </summary>
    public static System.Func<Entity, int>? ClientMultijumpProvider;

    public MultijumpMutator() => NetName = "multijump";

    // QC SVQC: REGISTER_MUTATOR(multijump, autocvar_g_multijump);
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_multijump") != 0f;

    private HookHandler<MutatorHooks.PlayerPhysicsArgs>? _onPhysics;
    private HookHandler<MutatorHooks.PlayerJumpArgs>? _onJump;

    public override void Hook()
    {
        _onPhysics ??= OnPlayerPhysics;
        _onJump ??= OnPlayerJump;

        MutatorHooks.PlayerPhysics.Add(_onPhysics);
        MutatorHooks.PlayerJump.Add(_onJump);

        if (Api.Services is not null)
        {
            float mj = Api.Cvars.GetFloat("g_multijump");
            if (mj != 0f) MaxJumps = (int)mj;
            MinSpeed = Api.Cvars.GetFloat("g_multijump_speed");
            Add = Api.Cvars.GetFloat("g_multijump_add") != 0f;
            MaxSpeed = Api.Cvars.GetFloat("g_multijump_maxspeed");
            Dodging = Api.Cvars.GetFloat("g_multijump_dodging") != 0f;
            // QC STAT(MULTIJUMP_CLIENT) = autocvar_g_multijump_client (bool, default 1).
            ClientDefault = (int)Api.Cvars.GetFloat("g_multijump_client");
        }
    }

    public override void Unhook()
    {
        if (_onPhysics is not null) MutatorHooks.PlayerPhysics.Remove(_onPhysics);
        if (_onJump is not null) MutatorHooks.PlayerJump.Remove(_onJump);
    }

    // MUTATOR_HOOKFUNCTION(multijump, PlayerPhysics) — reset the counter when grounded.
    private bool OnPlayerPhysics(ref MutatorHooks.PlayerPhysicsArgs args)
    {
        Entity player = args.Player;
        if (MaxJumps == 0) return false;
        if (player.OnGround)
            player.MultijumpCount = 0;
        return false;
    }

    // MUTATOR_HOOKFUNCTION(multijump, PlayerJump)
    private bool OnPlayerJump(ref MutatorHooks.PlayerJumpArgs args)
    {
        Entity player = args.Player;
        if (MaxJumps == 0) return false;

        // QC: int client_multijump = PHYS_MULTIJUMP_CLIENT(player); if(-1) client_multijump = clientdefault;
        // -1 means "server decides" (use the g_multijump_client server default).
        int clientMultijump = ClientMultijumpProvider?.Invoke(player) ?? -1;
        if (clientMultijump == -1)
            clientMultijump = ClientDefault;
        if (clientMultijump > 1)
            return false; // QC: nope — this client has opted out / capped it off

        bool jumpHeld = (player.Flags & EntFlags.JumpReleased) == 0; // QC IS_JUMP_HELD == !JUMP_RELEASED bit
        // QC: if jump button is *not* held this frame, we're airborne, AND the client value is truthy → "ready".
        if (!jumpHeld && !player.OnGround && clientMultijump != 0)
            player.MultijumpReady = true;
        else
            player.MultijumpReady = false;

        bool underCount = MaxJumps == -1 || player.MultijumpCount < MaxJumps;
        bool fastEnough = player.Velocity.Z > MinSpeed;
        // QC: !PHYS_MULTIJUMP_MAXSPEED || vdist(player.velocity, <=, maxspeed) — vdist is the FULL 3D speed.
        bool horizOk = MaxSpeed == 0f || player.Velocity.Length() <= MaxSpeed;

        if (!args.Multijump && player.MultijumpReady && underCount && fastEnough && horizOk)
        {
            float jumpVel = JumpVelocity();
            if (!Add)
            {
                // QC: make z velocity == jumpvelocity (only if currently rising slower than that).
                if (player.Velocity.Z < jumpVel)
                {
                    args.Multijump = true;
                    var v = player.Velocity; v.Z = 0f; player.Velocity = v; // cleared then re-added by jump code
                }
            }
            else
            {
                args.Multijump = true;
            }

            if (args.Multijump)
            {
                // QC MULTIJUMP_DODGING: redirect the existing horizontal speed toward the movement wish
                // (using yaw only), so a re-jump can change direction without changing speed.
                if (Dodging && (player.MovementForward != 0f || player.MovementRight != 0f))
                {
                    float curSpeed = Horiz2D(player);
                    float yaw = (player.ViewAngles != Vector3.Zero ? player.ViewAngles : player.Angles).Y;
                    QMath.AngleVectors(new Vector3(0f, yaw, 0f), out Vector3 vfwd, out Vector3 vright, out _);
                    Vector3 wishVel = vfwd * player.MovementForward + vright * player.MovementRight;
                    Vector3 wishDir = QMath.Normalize(wishVel);
                    var v = player.Velocity;
                    v.X = wishDir.X * curSpeed; // keep z unchanged
                    v.Y = wishDir.Y * curSpeed;
                    player.Velocity = v;
                }
                if (MaxJumps > 0)
                    player.MultijumpCount++;
            }
            player.MultijumpReady = false; // require release+press again for the next jump
        }
        return false;
    }

    // MUTATOR_HOOKFUNCTION(multijump, BuildMutatorsString) — multijump.qc:121-124
    public override string BuildMutatorsString(string s) => s + ":multijump";

    // MUTATOR_HOOKFUNCTION(multijump, BuildMutatorsPrettyString) — multijump.qc:126-129
    public override string BuildMutatorsPrettyString(string s) => s + ", Multi jump";

    private static float Horiz2D(Entity e)
    {
        float x = e.Velocity.X, y = e.Velocity.Y;
        return MathF.Sqrt(x * x + y * y);
    }

    private static float JumpVelocity()
    {
        if (Api.Services is null) return 260f;
        float v = Api.Cvars.GetFloat("sv_jumpvelocity");
        return v != 0f ? v : 260f;
    }
}
