namespace VortexArena.Server;

// Property membership and registry order from Xonotic 863cd3e8456e18f577f7fe5fef857ff55ae62cc0.
// The stock cfgs supply values. These lists describe which cvars the QuakeC wr_config/tr_config methods emit.
internal static class BalanceDumpCatalog
{
    internal sealed record Section(int RegistryId, string Name, string CvarPrefix, string Properties, bool Mutator = false);

    internal static readonly Section[] Weapons =
    {
        new(1, "Blaster", "g_balance_blaster_", """
            primary_animtime primary_damage primary_delay primary_edgedamage primary_force primary_force_zscale
            primary_lifetime primary_radius primary_refire primary_shotangle primary_speed primary_spread
            switchdelay_drop switchdelay_raise weaponreplace weaponstart weaponstartoverride weaponthrowable
            """),
        new(2, "Shotgun", "g_balance_shotgun_", """
            pickup_ammo primary_ammo primary_animtime primary_bullets primary_damage
            primary_damagefalloff_forcehalflife primary_damagefalloff_halflife primary_damagefalloff_maxdist
            primary_damagefalloff_mindist primary_force primary_refire primary_solidpenetration primary_spread
            primary_spread_pattern primary_spread_pattern_bias primary_spread_pattern_scale reload_ammo
            reload_time secondary secondary_alt_animtime secondary_alt_refire secondary_animtime
            secondary_damage secondary_force secondary_melee_blockedbyfiring secondary_melee_delay
            secondary_melee_multihit secondary_melee_no_doubleslap secondary_melee_nonplayerdamage
            secondary_melee_range secondary_melee_swing_side secondary_melee_swing_up secondary_melee_time
            secondary_melee_traces secondary_refire switchdelay_drop switchdelay_raise weaponreplace
            weaponstart weaponstartoverride weaponthrowable
            """),
        new(3, "MachineGun", "g_balance_machinegun_", """
            burst burst_ammo burst_animtime burst_refire burst_refire2 burst_spread damagefalloff_forcehalflife
            damagefalloff_halflife damagefalloff_maxdist damagefalloff_mindist first first_ammo first_damage
            first_force first_refire first_spread mode pickup_ammo reload_ammo reload_time solidpenetration
            spread_add spread_cold_damagemultiplier spread_crouchmod spread_decay spread_heat_damagemultiplier
            spread_max spread_min sustained_ammo sustained_damage sustained_force sustained_refire
            sustained_spread switchdelay_drop switchdelay_raise weaponreplace weaponstart weaponstartoverride
            weaponthrowable
            """),
        new(4, "Mortar", "g_balance_mortar_", """
            bouncefactor bouncestop pickup_ammo primary_ammo primary_animtime primary_damage
            primary_damageforcescale primary_edgedamage primary_force primary_health primary_lifetime
            primary_lifetime_stick primary_radius primary_refire primary_remote_minbouncecnt primary_speed
            primary_speed_up primary_speed_z primary_spread primary_type reload_ammo reload_time secondary_ammo
            secondary_animtime secondary_damage secondary_damageforcescale secondary_edgedamage secondary_force
            secondary_health secondary_lifetime secondary_lifetime_bounce secondary_lifetime_stick
            secondary_radius secondary_refire secondary_remote_detonateprimary secondary_speed
            secondary_speed_up secondary_speed_z secondary_spread secondary_type switchdelay_drop
            switchdelay_raise weaponreplace weaponstart weaponstartoverride weaponthrowable
            """),
        new(5, "Mine Layer", "g_balance_minelayer_", """
            ammo animtime damage damageforcescale detonatedelay edgedamage force health lifetime
            lifetime_countdown limit pickup_ammo protection proximity_radius proximity_time_core
            proximity_time_edge radius refire reload_ammo reload_time remote_damage remote_edgedamage
            remote_force remote_radius speed switchdelay_drop switchdelay_raise weaponreplace weaponstart
            weaponstartoverride weaponthrowable
            """, Mutator: true),
        new(6, "Electro", "g_balance_electro_", """
            combo_comboradius combo_comboradius_thruwall combo_damage combo_duration combo_edgedamage
            combo_force combo_radius combo_safeammocheck combo_speed pickup_ammo primary_ammo primary_animtime
            primary_comboradius primary_damage primary_edgedamage primary_force primary_lifetime
            primary_midaircombo_enemy primary_midaircombo_explode primary_midaircombo_interval
            primary_midaircombo_own primary_midaircombo_radius primary_midaircombo_speed
            primary_midaircombo_teammate primary_radius primary_refire primary_speed primary_spread reload_ammo
            reload_time secondary_ammo secondary_animtime secondary_bouncefactor secondary_bouncestop
            secondary_count secondary_damage secondary_damagedbycontents secondary_damageforcescale
            secondary_edgedamage secondary_force secondary_health secondary_lifetime secondary_limit
            secondary_radius secondary_refire secondary_refire2 secondary_speed secondary_speed_up
            secondary_speed_z secondary_spread secondary_stick secondary_stick_lifetime secondary_touchexplode
            switchdelay_drop switchdelay_raise weaponreplace weaponstart weaponstartoverride weaponthrowable
            """),
        new(7, "Crylink", "g_balance_crylink_", """
            pickup_ammo primary_ammo primary_animtime primary_bouncedamagefactor primary_bounces primary_damage
            primary_edgedamage primary_force primary_joindelay primary_joinexplode primary_joinexplode_damage
            primary_joinexplode_edgedamage primary_joinexplode_force primary_joinexplode_radius
            primary_joinspread primary_linkexplode primary_middle_fadetime primary_middle_lifetime
            primary_other_fadetime primary_other_lifetime primary_radius primary_refire primary_shots
            primary_speed primary_spread reload_ammo reload_time secondary secondary_ammo secondary_animtime
            secondary_bouncedamagefactor secondary_bounces secondary_damage secondary_edgedamage
            secondary_force secondary_joindelay secondary_joinexplode secondary_joinexplode_damage
            secondary_joinexplode_edgedamage secondary_joinexplode_force secondary_joinexplode_radius
            secondary_joinspread secondary_linkexplode secondary_middle_fadetime secondary_middle_lifetime
            secondary_other_fadetime secondary_other_lifetime secondary_radius secondary_refire secondary_shots
            secondary_speed secondary_spread secondary_spreadtype switchdelay_drop switchdelay_raise
            weaponreplace weaponstart weaponstartoverride weaponthrowable
            """),
        new(8, "Vortex", "g_balance_vortex_", """
            charge charge_always charge_animlimit charge_limit charge_maxspeed charge_mindmg charge_minspeed
            charge_rate charge_rot_pause charge_rot_rate charge_rot_unequipped_limit charge_rot_unequipped_rate
            charge_shot_multiplier charge_start charge_velocity_rate pickup_ammo primary_ammo primary_animtime
            primary_armorpierce primary_damage primary_damagefalloff_forcehalflife
            primary_damagefalloff_halflife primary_damagefalloff_maxdist primary_damagefalloff_mindist
            primary_force primary_refire reload_ammo reload_time secondary secondary_ammo secondary_animtime
            secondary_armorpierce secondary_chargepool secondary_chargepool_pause_regen
            secondary_chargepool_regen secondary_damage secondary_damagefalloff_forcehalflife
            secondary_damagefalloff_halflife secondary_damagefalloff_maxdist secondary_damagefalloff_mindist
            secondary_force secondary_refire switchdelay_drop switchdelay_raise weaponreplace weaponstart
            weaponstartoverride weaponthrowable
            """),
        new(9, "Hagar", "g_balance_hagar_", """
            pickup_ammo primary_ammo primary_damage primary_damageforcescale primary_edgedamage primary_force
            primary_health primary_lifetime primary_radius primary_refire primary_speed primary_spread
            reload_ammo reload_time secondary secondary_ammo secondary_damage secondary_damageforcescale
            secondary_edgedamage secondary_force secondary_health secondary_lifetime_min
            secondary_lifetime_rand secondary_load secondary_load_abort secondary_load_animtime
            secondary_load_hold secondary_load_linkexplode secondary_load_max secondary_load_releasedeath
            secondary_load_speed secondary_load_spread secondary_load_spread_bias secondary_radius
            secondary_refire secondary_speed secondary_spread switchdelay_drop switchdelay_raise weaponreplace
            weaponstart weaponstartoverride weaponthrowable
            """),
        new(10, "Devastator", "g_balance_devastator_", """
            ammo animtime damage damageforcescale detonatedelay edgedamage force force_xyscale guidedelay
            guidegoal guiderate guideratedelay guidestop health lifetime pickup_ammo radius refire reload_ammo
            reload_time remote_damage remote_edgedamage remote_force remote_jump remote_jump_damage
            remote_jump_force remote_jump_radius remote_jump_velocity_z_add remote_jump_velocity_z_max
            remote_jump_velocity_z_min remote_radius speed speedaccel speedstart switchdelay_drop
            switchdelay_raise weaponreplace weaponstart weaponstartoverride weaponthrowable
            """),
        new(11, "Port-O-Launch", "g_balance_porto_", """
            primary_animtime primary_lifetime primary_refire primary_speed secondary secondary_animtime
            secondary_lifetime secondary_refire secondary_speed switchdelay_drop switchdelay_raise
            weaponreplace weaponstart weaponstartoverride weaponthrowable
            """),
        new(12, "Vaporizer", "g_balance_vaporizer_", """
            pickup_ammo primary_ammo primary_animtime primary_damage primary_damagefalloff_forcehalflife
            primary_damagefalloff_halflife primary_damagefalloff_maxdist primary_damagefalloff_mindist
            primary_force primary_refire reload_ammo reload_time switchdelay_drop switchdelay_raise
            weaponreplace weaponstart weaponstartoverride weaponthrowable
            """),
        new(13, "Grappling Hook", "g_balance_hook_", """
            pickup_ammo primary_ammo primary_animtime primary_hooked_ammo primary_hooked_time_free
            primary_hooked_time_max primary_refire secondary_animtime secondary_damage
            secondary_damageforcescale secondary_duration secondary_edgedamage secondary_force
            secondary_gravity secondary_health secondary_lifetime secondary_power secondary_radius
            secondary_refire secondary_speed switchdelay_drop switchdelay_raise weaponreplace weaponstart
            weaponstartoverride weaponthrowable
            """),
        new(14, "Heavy Laser Assault Cannon", "g_balance_hlac_", """
            pickup_ammo primary_ammo primary_animtime primary_damage primary_edgedamage primary_force
            primary_lifetime primary_radius primary_refire primary_speed primary_spread_add
            primary_spread_crouchmod primary_spread_max primary_spread_min reload_ammo reload_time secondary
            secondary_ammo secondary_animtime secondary_damage secondary_edgedamage secondary_force
            secondary_lifetime secondary_radius secondary_refire secondary_shots secondary_speed
            secondary_spread secondary_spread_crouchmod switchdelay_drop switchdelay_raise weaponreplace
            weaponstart weaponstartoverride weaponthrowable
            """, Mutator: true),
        new(15, "@!#%'n Tuba", "g_balance_tuba_", """
            animtime attenuation damage edgedamage force radius refire switchdelay_drop switchdelay_raise
            weaponreplace weaponstart weaponstartoverride weaponthrowable
            """),
        new(16, "Rifle", "g_balance_rifle_", """
            bursttime pickup_ammo primary_ammo primary_animtime primary_bullethail primary_burstcost
            primary_damage primary_damagefalloff_forcehalflife primary_damagefalloff_halflife
            primary_damagefalloff_maxdist primary_damagefalloff_mindist primary_force
            primary_headshot_multiplier primary_refire primary_shots primary_solidpenetration primary_spread
            primary_tracer reload_ammo reload_time secondary secondary_ammo secondary_animtime
            secondary_bullethail secondary_burstcost secondary_damage secondary_damagefalloff_forcehalflife
            secondary_damagefalloff_halflife secondary_damagefalloff_maxdist secondary_damagefalloff_mindist
            secondary_force secondary_headshot_multiplier secondary_refire secondary_reload secondary_shots
            secondary_solidpenetration secondary_spread secondary_tracer switchdelay_drop switchdelay_raise
            weaponreplace weaponstart weaponstartoverride weaponthrowable
            """, Mutator: true),
        new(17, "Fireball", "g_balance_fireball_", """
            primary_animtime primary_bfgdamage primary_bfgforce primary_bfgradius primary_damage
            primary_damageforcescale primary_edgedamage primary_force primary_health primary_laserburntime
            primary_laserdamage primary_laseredgedamage primary_laserradius primary_lifetime primary_radius
            primary_refire primary_refire2 primary_speed primary_spread secondary_animtime secondary_damage
            secondary_damageforcescale secondary_damagetime secondary_laserburntime secondary_laserdamage
            secondary_laseredgedamage secondary_laserradius secondary_lifetime secondary_refire secondary_speed
            secondary_speed_up secondary_speed_z secondary_spread switchdelay_drop switchdelay_raise
            weaponreplace weaponstart weaponstartoverride weaponthrowable
            """),
        new(18, "T.A.G. Seeker", "g_balance_seeker_", """
            flac_ammo flac_animtime flac_damage flac_edgedamage flac_force flac_lifetime flac_lifetime_rand
            flac_radius flac_refire flac_speed flac_speed_up flac_speed_z flac_spread missile_accel
            missile_ammo missile_animtime missile_count missile_damage missile_damageforcescale missile_decel
            missile_delay missile_edgedamage missile_force missile_health missile_lifetime missile_proxy
            missile_proxy_delay missile_proxy_maxrange missile_radius missile_refire missile_smart
            missile_smart_mindist missile_smart_trace_max missile_smart_trace_min missile_speed
            missile_speed_max missile_speed_up missile_speed_z missile_spread missile_turnrate pickup_ammo
            reload_ammo reload_time switchdelay_drop switchdelay_raise tag_ammo tag_animtime
            tag_damageforcescale tag_health tag_lifetime tag_refire tag_speed tag_spread tag_tracker_lifetime
            type weaponreplace weaponstart weaponstartoverride weaponthrowable
            """, Mutator: true),
        new(19, "Arc", "g_balance_arc_", """
            beam_ammo beam_animtime beam_botaimlifetime beam_botaimspeed beam_damage beam_degreespersegment
            beam_distancepersegment beam_falloff_halflifedist beam_falloff_maxdist beam_falloff_mindist
            beam_force beam_healing_amax beam_healing_aps beam_healing_hmax beam_healing_hps beam_heat
            beam_maxangle beam_nonplayerdamage beam_range beam_refire beam_returnspeed beam_tightness bolt
            bolt_ammo bolt_bounce_count bolt_bounce_explode bolt_bounce_lifetime bolt_count bolt_damage
            bolt_damageforcescale bolt_edgedamage bolt_force bolt_health bolt_lifetime bolt_radius bolt_refire
            bolt_refire2 bolt_speed bolt_spread burst_ammo burst_damage burst_healing_aps burst_healing_hps
            burst_heat cooldown cooldown_release overheat_max overheat_min pickup_ammo switchdelay_drop
            switchdelay_raise weaponreplace weaponstart weaponstartoverride weaponthrowable
            """, Mutator: true),
        new(25, "Ball Stealer", "g_balance_ballstealer_", "", Mutator: true),
        new(26, "Overkill Heavy MachineGun", "g_balance_okhmg_", """
            primary_ammo primary_damage primary_force primary_refire primary_solidpenetration
            primary_spread_add primary_spread_max primary_spread_min reload_ammo reload_time
            secondary_refire_type switchdelay_drop switchdelay_raise weaponreplace weaponstart
            weaponstartoverride weaponthrowable
            """, Mutator: true),
        new(27, "Overkill MachineGun", "g_balance_okmachinegun_", """
            damagefalloff_forcehalflife damagefalloff_halflife damagefalloff_maxdist damagefalloff_mindist
            primary_ammo primary_damage primary_force primary_refire primary_solidpenetration
            primary_spread_add primary_spread_max primary_spread_min reload_ammo reload_time
            secondary_refire_type switchdelay_drop switchdelay_raise weaponreplace weaponstart
            weaponstartoverride weaponthrowable
            """, Mutator: true),
        new(28, "Overkill Nex", "g_balance_oknex_", """
            charge charge_animlimit charge_limit charge_maxspeed charge_mindmg charge_minspeed charge_rate
            charge_rot_pause charge_rot_rate charge_shot_multiplier charge_start charge_velocity_rate
            primary_ammo primary_animtime primary_damage primary_damagefalloff_forcehalflife
            primary_damagefalloff_halflife primary_damagefalloff_maxdist primary_damagefalloff_mindist
            primary_force primary_refire reload_ammo reload_time secondary secondary_chargepool
            secondary_chargepool_pause_regen secondary_chargepool_regen secondary_damagefalloff_forcehalflife
            secondary_damagefalloff_halflife secondary_damagefalloff_maxdist secondary_damagefalloff_mindist
            secondary_refire_type switchdelay_drop switchdelay_raise weaponreplace weaponstart
            weaponstartoverride weaponthrowable
            """, Mutator: true),
        new(29, "Overkill Rocket Propelled Chainsaw", "g_balance_okrpc_", """
            primary_ammo primary_animtime primary_damage primary_damage2 primary_damageforcescale
            primary_edgedamage primary_force primary_health primary_lifetime primary_radius primary_refire
            primary_speed primary_speedaccel reload_ammo reload_time secondary_refire_type switchdelay_drop
            switchdelay_raise weaponreplace weaponstart weaponstartoverride weaponthrowable
            """, Mutator: true),
        new(30, "Overkill Shotgun", "g_balance_okshotgun_", """
            primary_ammo primary_animtime primary_bot_range primary_bullets primary_damage
            primary_damagefalloff_forcehalflife primary_damagefalloff_halflife primary_damagefalloff_maxdist
            primary_damagefalloff_mindist primary_force primary_refire primary_solidpenetration primary_spread
            primary_spread_pattern primary_spread_pattern_bias primary_spread_pattern_scale reload_ammo
            reload_time secondary_refire_type switchdelay_drop switchdelay_raise weaponreplace weaponstart
            weaponstartoverride weaponthrowable
            """, Mutator: true),
    };

    private const string CommonTurretProperties = """
        aim_firetolerance_dist aim_maxpitch aim_maxrot aim_speed ammo_max ammo_recharge health respawntime
        shot_dmg shot_force shot_radius shot_refire shot_speed shot_spread shot_volly shot_volly_refire
        target_range target_range_min target_range_optimal target_select_anglebias target_select_missilebias
        target_select_playerbias target_select_rangebias target_select_samebias track_accel_pitch
        track_accel_rot track_blendrate track_type
        """;

    internal static readonly Section[] Turrets =
    {
        new(1, "eWheel Turret", "g_turrets_unit_ewheel_", CommonTurretProperties + " " + """
            speed_fast speed_slow speed_slower speed_stop turnrate
            """),
        new(2, "FLAC Cannon", "g_turrets_unit_flac_", CommonTurretProperties),
        new(3, "Fusion Reactor", "g_turrets_unit_fusreac_", CommonTurretProperties),
        new(4, "Hellion Missile Turret", "g_turrets_unit_hellion_", CommonTurretProperties + " " + """
            shot_speed_gain shot_speed_max
            """),
        new(5, "Hunter-Killer Turret", "g_turrets_unit_hk_", CommonTurretProperties + " " + """
            shot_speed_accel shot_speed_accel2 shot_speed_decel shot_speed_max shot_speed_turnrate
            """),
        new(6, "Machinegun Turret", "g_turrets_unit_machinegun_", CommonTurretProperties),
        new(7, "MLRS Turret", "g_turrets_unit_mlrs_", CommonTurretProperties),
        new(8, "Phaser Cannon", "g_turrets_unit_phaser_", CommonTurretProperties),
        new(9, "Plasma Cannon", "g_turrets_unit_plasma_", CommonTurretProperties),
        new(10, "Dual Plasma Cannon", "g_turrets_unit_plasma_dual_", CommonTurretProperties),
        new(11, "Tesla Coil", "g_turrets_unit_tesla_", CommonTurretProperties),
        new(12, "Walker Turret", "g_turrets_unit_walker_", CommonTurretProperties + " " + """
            melee_damage melee_force melee_range rocket_damage rocket_force rocket_radius rocket_range
            rocket_range_min rocket_refire rocket_speed rocket_turnrate speed_jump speed_roam speed_run
            speed_stop speed_swim speed_walk turn turn_run turn_strafe turn_swim turn_walk
            """),
    };
}
