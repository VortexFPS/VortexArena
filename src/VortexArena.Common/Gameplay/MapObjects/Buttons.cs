// Port of qcsrc/common/mapobjects/func/button.qc.
//
// "When a button is touched, it moves some distance in the direction of its angle, triggers all of its
//  targets, waits some time, then returns to its original position where it can be triggered again." (QC)
//
// A func_button is a MOVETYPE_PUSH brush that presses from pos1 (spawn origin) to pos2
// (pos1 + movedir*(|movedir·size| - lip)). It fires on touch (player moving into it), on use (remote
// trigger), or on damage (if it has health / is shootable). After firing it fires its targets, waits
// `.wait`, and returns; wait < 0 means it never returns.
//
// Now ported in full: shootable buttons via the per-hit event_damage seam (button_damage —
// DONTACCUMULATEDMG single-hit, NOSPLASH splash-immunity, and health == -1 fire-on-any-attack all
// faithful), the button_setactive deactivate-while-pressed bookkeeping (wait_remaining/activation_time),
// and the alternate (pressed) texture frame. Genuinely client-only: CSQC networking. The round-restart
// button_reset re-arm is wired via `this_.Reset = ButtonReset` in ButtonSetup; GameWorld.ResetMapObjects
// fires every map entity's .Reset on a round/match restart (QC reset_map_global).

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary><c>func_button</c> — a press-to-fire BSP button. Registered as a spawnfunc (<see cref="ButtonSetup"/>).</summary>
public static class Buttons
{
    public const int DontAccumulateDmg = 1 << 7; // BUTTON_DONTACCUMULATEDMG

    /// <summary><c>spawnfunc(func_button)</c>.</summary>
    public static void ButtonSetup(Entity this_)
    {
        MapMover.SetMovedir(this_);

        if (!MapMover.InitMovingBrushTrigger(this_))
            return;
        this_.ClassName = "button";

        this_.Blocked = ButtonBlocked;
        this_.Use = ButtonUse;
        this_.Reset = ButtonReset; // QC button.qc: this.reset = button_reset (round-restart re-arm)

        // Shootable button (health set, incl. -1 = fire on ANY attack): the per-hit event_damage shim
        // (button_damage) decides what a hit does. Otherwise touch. QC: button.qc spawnfunc.
        if (this_.Health != 0f)
        {
            this_.MaxHealthMover = this_.Health;
            this_.TakeDamage = DamageMode.Yes;
            MapMover.InstallEventDamage(this_, ButtonDamage);
        }
        else
        {
            this_.Touch = ButtonTouch;
        }

        if (this_.Speed == 0f) this_.Speed = 40f;
        if (this_.Wait == 0f) this_.Wait = 1f;
        if (this_.Lip == 0f) this_.Lip = 4f;

        // QC also does `if (wait < 0 && q3compat) wait = 0.1` (q3 -1 = return immediately), but this port
        // layer has no live q3compat flag (CompatRemaps documents it as a port-wide gap), so it never fires.

        this_.Pos1 = this_.Origin;
        this_.Pos2 = this_.Pos1 + this_.MoveDir * (MathF.Abs(QMath.Dot(this_.MoveDir, this_.Size)) - this_.Lip);
        this_.Flags |= EntFlags.NoTarget;

        MapMover.IndexRegister(this_);
        ButtonReset(this_);
    }

    /// <summary>QC <c>button_reset</c>: place at pos1 (unpressed), STATE_BOTTOM, active, re-arm if shootable.</summary>
    public static void ButtonReset(Entity this_)
    {
        if (this_.MaxHealthMover != 0f)
            this_.Health = this_.MaxHealthMover;
        MapMover.SetOrigin(this_, this_.Pos1);
        this_.Frame = 0f;
        this_.MoverState = MapMover.StateBottom;
        this_.Velocity = Vector3.Zero;
        this_.WaitRemaining = -1f;
        this_.ActivationTime = -1f;
        this_.Active = MapMover.ActiveActive;
        this_.Think = null;
        this_.NextThink = 0f;
        if (this_.MaxHealthMover != 0f)
            this_.TakeDamage = DamageMode.Yes;
    }

    /// <summary>
    /// QC <c>button_setactive</c>: enable/disable the button, preserving its press timer across a
    /// deactivation so a relay_activate resumes the auto-return where it left off.
    /// </summary>
    public static void ButtonSetActive(Entity this_, int astate)
    {
        int oldState = this_.Active;
        if (astate == MapMover.ActiveToggle)
            this_.Active = this_.Active == MapMover.ActiveActive ? MapMover.ActiveNot : MapMover.ActiveActive;
        else
            this_.Active = astate;

        if (this_.Active == MapMover.ActiveActive && oldState == MapMover.ActiveNot)
        {
            // re-activated while it was pressed: resume the remaining wait.
            if (this_.WaitRemaining >= 0f)
            {
                this_.NextThink = this_.WaitRemaining + this_.LTime;
                this_.Think = ButtonReturn;
            }
        }
        else if (this_.Active == MapMover.ActiveNot && oldState == MapMover.ActiveActive)
        {
            // deactivated: bank how much of the press wait remains.
            if (this_.ActivationTime >= 0f)
                this_.WaitRemaining = this_.Wait - (MapMover.Now() - this_.ActivationTime);
        }
    }

    /// <summary>QC <c>button_use</c>: a remote trigger presses the button.</summary>
    public static void ButtonUse(Entity self, Entity actor)
    {
        if (self.Active != MapMover.ActiveActive)
            return;
        self.Enemy = actor;
        ButtonFire(self);
    }

    /// <summary>QC <c>button_touch</c>: a creature moving INTO the button (along movedir) presses it.</summary>
    public static void ButtonTouch(Entity self, Entity toucher)
    {
        if (self.Active != MapMover.ActiveActive)
            return;
        if (!MapMover.IsCreature(toucher))
            return;
        // must be moving into the button face (QC: toucher.velocity * movedir < 0 rejects)
        if (QMath.Dot(toucher.Velocity, self.MoveDir) < 0f)
            return;
        self.Enemy = toucher.Owner ?? toucher;
        ButtonFire(self);
    }

    /// <summary>QC <c>button_fire</c>: press to pos2 then run button_wait.</summary>
    public static void ButtonFire(Entity this_)
    {
        if (this_.MaxHealthMover != 0f)
        {
            this_.Health = this_.MaxHealthMover;
            this_.TakeDamage = DamageMode.No; // will be reset upon return
        }

        if (this_.MoverState == MapMover.StateUp || this_.MoverState == MapMover.StateTop)
            return;

        this_.ActivationTime = MapMover.Now();

        MapMover.Sound(this_, SoundChannel.Auto, this_.Noise);

        this_.MoverState = MapMover.StateUp;
        MapMover.CalcMove(this_, this_.Pos2, MapMover.SpeedType.Linear, this_.Speed, ButtonWait);
    }

    /// <summary>QC <c>button_wait</c>: pressed-in; fire targets, schedule the return after wait.</summary>
    private static void ButtonWait(Entity this_)
    {
        this_.MoverState = MapMover.StateTop;
        if (this_.Wait >= 0f)
        {
            this_.NextThink = this_.LTime + this_.Wait;
            this_.Think = ButtonReturn;
        }
        MapMover.UseTargets(this_, this_.Enemy, null);
        this_.Frame = 1f; // alternate (pressed) texture
    }

    /// <summary>QC <c>button_return</c>: move back to pos1, re-arm shootable buttons.</summary>
    private static void ButtonReturn(Entity this_)
    {
        if (this_.Active != MapMover.ActiveActive)
            return;
        this_.MoverState = MapMover.StateDown;
        MapMover.CalcMove(this_, this_.Pos1, MapMover.SpeedType.Linear, this_.Speed, ButtonDone);
        this_.Frame = 0f; // normal texture
        if (this_.MaxHealthMover != 0f)
            this_.TakeDamage = DamageMode.Yes; // can be shot again
        this_.WaitRemaining = -1f;
        this_.ActivationTime = -1f;
    }

    /// <summary>QC <c>button_done</c>: fully returned.</summary>
    private static void ButtonDone(Entity this_)
    {
        this_.MoverState = MapMover.StateBottom;
    }

    /// <summary>QC <c>button_blocked</c>: do nothing (just don't come all the way back out).</summary>
    public static void ButtonBlocked(Entity self, Entity blocker) { }

    // ================= shootable buttons via the per-hit event_damage seam =================

    /// <summary>
    /// QC <c>button_damage</c> (the <c>.event_damage</c> callback): faithful per-hit damage reaction for a
    /// shootable button. Installed via <see cref="MapMover.InstallEventDamage"/> at spawn and dispatched by
    /// the damage pipeline for every hit on the brush (not just a lethal one), so the QC single-hit /
    /// splash-immune / fire-on-any semantics are reproduced exactly — none of which the accumulate-then-die
    /// <see cref="Combat.Death"/> hook could express.
    /// </summary>
    private static void ButtonDamage(
        Entity self, Entity? inflictor, Entity? attacker, string deathType, float damage,
        Vector3 hitLoc, Vector3 force)
    {
        if (self.Active != MapMover.ActiveActive)
            return;

        // NOSPLASH: ignore non-special splash damage (QC button_damage early-out).
        if ((self.SpawnFlags & MapMover.SpawnNoSplash) != 0)
            if (!DeathTypes.IsSpecial(deathType) && DeathTypes.HasHitType(deathType, DeathTypes.Splash))
                return;

        if ((self.SpawnFlags & DontAccumulateDmg) != 0)
        {
            // DONTACCUMULATEDMG: fire only on a single hit whose damage meets/exceeds current health, and
            // never subtract health. A high-HP button needs one big hit; small hits never open it.
            // (health == -1: this branch fires on ANY hit, since any damage >= -1.)
            if (self.Health <= damage)
            {
                self.Enemy = attacker;
                ButtonFire(self);
            }
        }
        else
        {
            // Default: accumulate damage; fire once driven to/below zero. (health == -1 starts <= 0, so any
            // attack — even a damageless InstaGib laser — fires immediately. QC `health -1 = fire on ANY`.)
            self.Health -= damage;
            if (self.Health <= 0f)
            {
                self.Enemy = attacker;
                ButtonFire(self);
            }
        }
    }
}
