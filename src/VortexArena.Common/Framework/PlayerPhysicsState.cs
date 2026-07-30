// Port of qcsrc/ecs/systems/sv_physics.qc (sys_phys_spectator_control / sys_phys_fixspeed) +
// qcsrc/common/stats.qh STAT(SPECTATORSPEED).
//
// The per-player free-flight state QuakeC kept on the client edict's stat/field namespace, promoted to
// typed members on the partial Entity (ADR-0007, specs/entity-model.md). Entity is declared partial and
// this is a NEW file, so adding these here respects the task constraint (no existing Entity file modified).

namespace XonoticGodot.Common.Framework
{
    public partial class Entity
    {
        // --- spectator free-flight (ecs/systems/sv_physics.qc) ---

        /// <summary>
        /// QC STAT(SPECTATORSPEED, this) (common/stats.qh:360 REGISTER_STAT(SPECTATORSPEED, FLOAT)) — the
        /// per-spectator speed multiplier applied to the fly-branch top speed/accel. Seeded to
        /// <c>autocvar_sv_spectator_speed_multiplier</c> on first use and stepped by the impulse ladder in
        /// <c>sys_phys_spectator_control</c>. 0 = unseeded (matches QC's <c>!STAT(SPECTATORSPEED)</c> seed test).
        /// </summary>
        public float SpectatorSpeed;

        /// <summary>
        /// QC <c>this.lastclassname</c> as read by <c>sys_phys_spectator_control</c> (<c>this.lastclassname !=
        /// STR_PLAYER</c>, sv_physics.qc:76): true when the entity was a spectator on the PREVIOUS physics tick,
        /// which gates the speed-ladder STEP (the first tick after un-spawning only seeds/clears, it does not
        /// step). Written at the END of every Move, mirroring <c>sys_phys_postupdate</c>'s
        /// <c>this.lastclassname = this.classname;</c> (physics.qc:194). The port maps QC's classname-based
        /// !IS_PLAYER test to <c>Player.IsObserver</c> (ADR-0007), so this tracks "was an observer last tick".
        /// </summary>
        public bool WasSpectatorLastTick;
    }
}
