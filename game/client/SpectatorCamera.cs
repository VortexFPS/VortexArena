using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Services;
using VortexArena.Engine.Console;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>
/// [T65] The demo-replay spectator camera (planning/specs/demo-replay-and-spectator.md §8): a free-roam view
/// that replaces the first-person predicted eye while a demo plays back. Purely client-side view selection over
/// the networked entity stream — no server involvement, so every viewer chooses independently.
///
/// <list type="bullet">
///   <item><b>FreeFly</b> — WASD (the user's movement binds via <see cref="BindTable"/>) + mouse look; Shift
///         speeds up, the +jump/+crouch binds fly up/down.</item>
///   <item><b>Follow</b> — <c>F</c> cycles through the recorded players (provider-fed from the client's decoded
///         entity stream); the camera orbits the target at a fixed chase distance, mouse-look picks the angle;
///         pressing <c>F</c> past the last target returns to FreeFly.</item>
/// </list>
///
/// The camera position is critically-damped toward its goal (<see cref="SmoothingRate"/>) so switching targets
/// or snapping onto a respawned player glides instead of cutting; a demo SEEK teleports the followed entity,
/// and the same damping absorbs that jump.
/// </summary>
public sealed partial class SpectatorCamera : Camera3D
{
    /// <summary>One followable recorded player (net id + its interpolated Quake-space origin this frame).</summary>
    public readonly struct FollowTarget
    {
        public readonly int NetId;
        public readonly NVec3 OriginQuake;

        public FollowTarget(int netId, NVec3 originQuake)
        {
            NetId = netId;
            OriginQuake = originQuake;
        }
    }

    /// <summary>The follow-target feed, wired by the host (NetGame) to the client's decoded entity stream.
    /// Re-queried every frame so departed/respawned players resolve live. Null/empty = FreeFly only.</summary>
    public Func<List<FollowTarget>>? TargetProvider { get; set; }

    /// <summary>Base free-fly speed in Quake units/second (the observer free-flight ballpark: sv_maxspeed 360
    /// with the spectator ladder above it; Shift holds the fast rung).</summary>
    public float MoveSpeed { get; set; } = 500f;

    /// <summary>Speed multiplier while the Shift key is held (the spectator speed ladder's fast rung).</summary>
    public float FastMultiplier { get; set; } = 3f;

    /// <summary>Per-second exponential smoothing rate for the camera position (higher = tighter).</summary>
    public float SmoothingRate { get; set; } = 12f;

    private const float ChaseDistance = 160f;  // Quake units behind the followed player
    private const float ChaseHeight = 24f;     // lift above the target's eye when orbiting
    private const float EyeHeight = 35f;       // Xonotic PL_VIEW_OFS z — aim at the head, not the feet

    private float _yawDeg;    // Godot yaw (about +Y); mouse right → negative
    private float _pitchDeg;  // Godot pitch (about +X); clamped to ±89
    private int _followNetId; // 0 = FreeFly
    private Vector3 _goalPos;
    private bool _seeded;

    public override void _Ready()
    {
        _goalPos = GlobalPosition;
        Vector3 e = RotationDegrees;
        _pitchDeg = e.X;
        _yawDeg = e.Y;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Current)
            return;

        // Mouse look while the cursor is captured (the Shell owns Escape/menu around this, same as play).
        // Sensitivity mirrors NetGame.LookSensitivity: the live `sensitivity` cvar × the 0.025 deg/px base feel,
        // m_pitch sign = invert-Y — so the replay camera aims exactly like the game.
        if (@event is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            float s = Api.Services is not null ? Api.Cvars.GetFloat("sensitivity") : 0f;
            float sens = s > 0f ? s * 0.025f : 0.15f;
            float pitchSign = Api.Services is not null && Api.Cvars.GetFloat("m_pitch") < 0f ? -1f : 1f;
            _yawDeg -= motion.Relative.X * sens;
            _pitchDeg -= motion.Relative.Y * sens * pitchSign;
            _pitchDeg = Mathf.Clamp(_pitchDeg, -89f, 89f);
        }

        // F: cycle the follow target (free → player 1 → player 2 → … → free).
        if (@event is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.F }
            && !VortexArena.Game.Console.ConsoleState.IsOpen)
        {
            CycleFollowTarget();
            GetViewport().SetInputAsHandled();
        }
    }

    private void CycleFollowTarget()
    {
        List<FollowTarget>? targets = TargetProvider?.Invoke();
        if (targets is null || targets.Count == 0)
        {
            _followNetId = 0;
            return;
        }
        targets.Sort(static (a, b) => a.NetId.CompareTo(b.NetId));

        if (_followNetId == 0)
        {
            _followNetId = targets[0].NetId;
            return;
        }
        for (int i = 0; i < targets.Count; i++)
        {
            if (targets[i].NetId != _followNetId)
                continue;
            _followNetId = i + 1 < targets.Count ? targets[i + 1].NetId : 0; // past the last → back to FreeFly
            return;
        }
        _followNetId = targets[0].NetId; // previous target left — restart at the first
    }

    public override void _Process(double delta)
    {
        if (!Current)
            return;
        float dt = (float)delta;

        RotationDegrees = new Vector3(_pitchDeg, _yawDeg, 0f);
        Basis basis = GlobalTransform.Basis;

        bool inputActive = Input.MouseMode == Input.MouseModeEnum.Captured
                           && !VortexArena.Game.Console.ConsoleState.IsOpen && !GetTree().Paused;

        if (_followNetId != 0 && TryResolveFollowTarget(out NVec3 targetQuake))
        {
            // Chase: sit ChaseDistance behind the view direction from the target's head, so mouse-look orbits.
            Vector3 eye = Coords.ToGodot(targetQuake + new NVec3(0f, 0f, EyeHeight));
            _goalPos = eye + basis.Z * ChaseDistance + Vector3.Up * ChaseHeight;
        }
        else
        {
            if (_followNetId != 0)
                _followNetId = 0; // followed player left the recording — fall back to free flight

            if (inputActive)
            {
                // FreeFly along the view basis, from the user's own movement binds (BindTable.Forward/Side are
                // the +forward/+moveleft… held states; Up is +jump/+crouch — spectators fly with them, T44).
                float fwd = Math.Clamp(BindTable.Forward, -1f, 1f);
                float side = Math.Clamp(BindTable.Side, -1f, 1f);
                float up = Math.Clamp(BindTable.Up, -1f, 1f);
                if (BindTable.JumpHeld)
                    up = 1f;

                Vector3 wish = -basis.Z * fwd + basis.X * side + Vector3.Up * up;
                if (wish.LengthSquared() > 1f)
                    wish = wish.Normalized();

                float speed = MoveSpeed * (Input.IsPhysicalKeyPressed(Key.Shift) ? FastMultiplier : 1f);
                _goalPos += wish * speed * dt;
            }
        }

        if (!_seeded)
        {
            GlobalPosition = _goalPos;
            _seeded = true;
            return;
        }

        // Critically-damped glide toward the goal (frame-rate independent) — the "no jarring cuts" contract.
        float blend = 1f - Mathf.Exp(-SmoothingRate * dt);
        GlobalPosition = GlobalPosition.Lerp(_goalPos, blend);
    }

    private bool TryResolveFollowTarget(out NVec3 originQuake)
    {
        originQuake = default;
        List<FollowTarget>? targets = TargetProvider?.Invoke();
        if (targets is null)
            return false;
        for (int i = 0; i < targets.Count; i++)
        {
            if (targets[i].NetId != _followNetId)
                continue;
            originQuake = targets[i].OriginQuake;
            return true;
        }
        return false;
    }
}
