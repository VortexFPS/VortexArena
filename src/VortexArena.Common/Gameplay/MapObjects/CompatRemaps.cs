// Port of the ADD side of the Q3/QL/CPMA/Q1/Q2/WoP/Q3DF compatibility entity remaps:
//   qcsrc/server/compat/quake3.qh  (SPAWNFUNC_Q3 / _Q3WEAPON / _Q3AMMO macros + q3compat bits)
//   qcsrc/server/compat/quake3.qc  (weapon/item remaps 51-116 + the Q3DF target_* entities 119-295)
//   qcsrc/server/compat/quake.qc   (Q1 remaps 14-20)
//   qcsrc/server/compat/quake2.qc  (Q2 remaps 9-12)
//   qcsrc/server/compat/wop.qc     (World of Padman remaps 18-42)
//   qcsrc/common/resources/sv_resources.qc  GetAmmoConsumption (231-243)
//
// The REMOVAL filter (DoesQ3ARemoveThisEntity) is already ported in
// XonoticGodot.Engine/Collision/MapEntityFilter.cs; this file is ONLY the ADD side that was missing —
// the weapon/ammo/item classname spawnfuncs and the four Q3DF target_* entities. The weapon + item
// classname aliases are installed by ItemSpawnFuncs.Register() (which calls into AmmoConsumption here for
// the ammo .count scaling); the Q3 ammo classnames + the target_* spawnfuncs are installed by Register()
// below, called from MapObjectsRegistry.RegisterAll().
//
// PARITY NOTES / DELIBERATE DEVIATIONS:
//  * No live `q3compat` flag in this port layer. QC's SG<->MG arena swap is gated on
//    `q3compat == Q3COMPAT_ARENA` (a per-map .arena-file flag set during entity parse). The port never
//    sets it, so we keep the NON-arena mapping (shotgun->SHOTGUN, machinegun->MACHINEGUN) — the common
//    case — and record the arena swap as a known gap. Likewise q3compat & Q3COMPAT_DEFI (Q3DF spawnflag
//    interpretation in target_print) defaults to the Q3 reading.
//  * `autocvar_sv_mapformat_is_quake3` (nailgun->CRYLINK vs ELECTRO) and `g_mod_balance == "XDF"`
//    (plasmagun/bfg variants) are read live from the cvar store with the QC default (Q3 format, non-XDF).
//  * The centerprint TEXT half of target_score / target_fragsFilter / target_print is rendered client-side
//    (CSQC); headless we faithfully play the audible half (play2 SND(TALK)), matching the convention in
//    MapMover.UseTargets. (See those spawnfuncs.)
//  * target_init's buff-drop (spawnflag 8) drops the four powerup status effects; QC additionally drops
//    held BuffsMutator buffs with a notification — that buff-drop notification path is not reachable from
//    this layer and is recorded as a gap (the powerup reset, the load-bearing part, is faithful).

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Framework
{
    public partial class Entity
    {
        /// <summary>QC <c>.int fragsfilter_cnt</c> (quake3.qh:11) — the Q3DF target_score accumulator a player
        /// builds up; target_fragsFilter gates on it.</summary>
        public int FragsFilterCnt;
    }
}

namespace XonoticGodot.Common.Gameplay
{
    /// <summary>
    /// The Q3/QL/CPMA/Q1/Q2/WoP/Q3DF compatibility remaps — the ADD side of server/compat/*.qc. Exposes the
    /// weapon/item/ammo remap tables (consumed by <see cref="ItemSpawnFuncs"/>) and the Q3DF target_* spawnfuncs
    /// (installed by <see cref="MapObjectsRegistry"/> via <see cref="Register"/>).
    /// </summary>
    public static class CompatRemaps
    {
        // =====================================================================================
        //  CTS gate seam (QC `if(!g_cts) { delete(this); return; }` in target_score/target_fragsFilter)
        // =====================================================================================

        /// <summary>
        /// Host seam reporting whether the active gametype is CTS (QC <c>g_cts</c>). target_score and
        /// target_fragsFilter self-delete outside CTS. The orchestrator wires this to the live gametype
        /// (GameWorld.GameType is Cts); unwired it defaults to <c>false</c>, so a bare unit test / non-CTS
        /// context deletes those entities exactly like QC's <c>!g_cts</c> branch.
        /// </summary>
        public static System.Func<bool>? IsCtsActive;

        private static bool Cts => IsCtsActive?.Invoke() ?? false;

        // =====================================================================================
        //  Global q3compat flag (QC server/compat/quake3.qh `int q3compat`, set in world.qc:964-965)
        // =====================================================================================

        /// <summary>
        /// Host seam reporting whether the current map is a Q3/Q3DF import (QC the global <c>q3compat</c> int, set
        /// during worldspawn from <c>_MapInfo_FindArenaFile(mapname, ".arena"/".defi")</c> — i.e. the existence of a
        /// sibling <c>.arena</c> (Q3COMPAT_ARENA) or <c>.defi</c> (Q3COMPAT_DEFI) file in the map pack). The
        /// orchestrator wires this from the map-config file probe at boot, BEFORE the spawnfuncs run; unwired it
        /// defaults to <c>false</c> (a stock Xonotic map), so every q3compat branch stays off in a bare unit test —
        /// matching QC's <c>q3compat == 0</c> default.
        /// </summary>
        public static System.Func<bool>? Q3CompatProvider;

        /// <summary>QC <c>Q3COMPAT_COMMON</c> (truthy <c>q3compat</c>): the active map is a Q3/Q3DF import.</summary>
        public static bool IsQ3Compat => Q3CompatProvider?.Invoke() ?? false;

        // =====================================================================================
        //  GetAmmoConsumption (common/resources/sv_resources.qc:231) — ammo-per-shot, for .count scaling
        // =====================================================================================

        /// <summary>
        /// QC <c>GetAmmoConsumption(wpn)</c>: the ammo a weapon spends per primary shot, used to convert a Q3
        /// ammo box's shot-count into a Xonotic resource amount. Mirrors the QC switch exactly — the four
        /// special weapons read their non-PRI cvar (ARC beam_ammo / DEVASTATOR ammo / MACHINEGUN sustained_ammo
        /// / MINE_LAYER ammo); everything else reads WEP_CVAR_PRI(wpn, ammo). Returns 0 for an ammo-less weapon
        /// (RES_NONE), which makes the Q3 ammo spawnfunc seed nothing (FIREBALL/grapplinghook). Reads the live
        /// g_balance_* cvars with the stock fallbacks (the same values the weapon Configure()s use). Returns a
        /// float (QC's <c>int</c> is a float typedef and the cvar value is not rounded inside this fn — only the
        /// final <c>rint(count * consumption)</c> in ApplyAmmoRemap rounds).
        /// </summary>
        public static float GetAmmoConsumption(Weapon wpn)
        {
            if (wpn.AmmoType == ResourceType.None)
                return 0f;
            // QC: switch(wpn) { the four irregular ones; default WEP_CVAR_PRI(wpn, ammo) }
            return wpn.NetName switch
            {
                "arc"        => Bal("g_balance_arc_beam_ammo", 6f),
                "devastator" => Bal("g_balance_devastator_ammo", 4f),
                "machinegun" => Bal("g_balance_machinegun_sustained_ammo", 1f),
                "minelayer"  => Bal("g_balance_minelayer_ammo", 4f),
                "seeker"     => Bal("g_balance_seeker_tag_ammo", 5f),
                _            => Bal($"g_balance_{wpn.NetName}_primary_ammo", PrimaryAmmoFallback(wpn.NetName)),
            };
        }

        // Stock g_balance_<wep>_primary_ammo defaults for the weapons reachable through a Q3 ammo remap
        // (matches each weapon's Configure() fallback). Anything else returns 1 (a safe non-zero default).
        private static float PrimaryAmmoFallback(string netName) => netName switch
        {
            "vortex"  => 6f,
            "shotgun" => 1f,
            "mortar"  => 2f,
            "crylink" => 3f,
            "hagar"   => 1f,
            "hlac"    => 1f,
            "electro" => 4f,
            _         => 1f,
        };

        // QC autocvar read with an "unset => fallback" rule (mirrors Weapon.Bal, which is protected so we can't
        // reuse it). The empty-string case is "unset" and kept distinct from a genuine "0".
        private static float Bal(string cvar, float fallback)
        {
            if (Api.Services is null) return fallback;
            string s = Api.Cvars.GetString(cvar);
            return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(cvar);
        }

        private static string CvarString(string name)
            => Api.Services is null ? "" : Api.Cvars.GetString(name);

        // =====================================================================================
        //  Weapon remaps (quake3.qc:55-100, quake.qc:14-15, wop.qc:18-26) — classname -> Weapon
        // =====================================================================================

        /// <summary>
        /// QC the SPAWNFUNC_Q3WEAPON / SPAWNFUNC_WEAPON weapon classname -> Weapon table. Some entries are
        /// resolved live (nailgun, plasmagun, bfg branch on cvars; shotgun/machinegun would branch on the arena
        /// flag the port never sets — see file header). Returns null for an unmapped classname.
        /// </summary>
        public static Weapon? WeaponForClassname(string className) => className switch
        {
            // Q3 / QL / CPMA / Team Arena
            "weapon_shotgun"        => Weapons.ByName("shotgun"),     // q3compat ARENA would swap to MACHINEGUN
            "weapon_machinegun"     => Weapons.ByName("machinegun"),  // q3compat ARENA would swap to SHOTGUN
            "weapon_grenadelauncher"=> Weapons.ByName("mortar"),
            "weapon_prox_launcher"  => Weapons.ByName("minelayer"),
            "weapon_chaingun"       => Weapons.ByName("hagar"),
            "weapon_hmg"            => Weapons.ByName("hagar"),
            "weapon_nailgun"        => MapformatIsQuake3() ? Weapons.ByName("crylink") : Weapons.ByName("electro"),
            "weapon_lightning"      => Weapons.ByName("electro"),
            "weapon_plasmagun"      => IsXdf() ? Weapons.ByName("hagar") : Weapons.ByName("hlac"),
            "weapon_railgun"        => Weapons.ByName("vortex"),
            "weapon_bfg"            => IsXdf() ? Weapons.ByName("crylink") : Weapons.ByName("fireball"),
            "weapon_grapplinghook"  => Weapons.ByName("hook"),
            "weapon_rocketlauncher" => Weapons.ByName("devastator"),
            "weapon_gauntlet"       => Weapons.ByName("tuba"),
            // Q1 (quake.qc)
            "weapon_supernailgun"   => Weapons.ByName("hagar"),
            "weapon_supershotgun"   => Weapons.ByName("machinegun"),
            // World of Padman (wop.qc)
            "weapon_punchy"         => Weapons.ByName("arc"),
            "weapon_nipper"         => Weapons.ByName("machinegun"),
            "weapon_pumper"         => Weapons.ByName("shotgun"),
            "weapon_boaster"        => Weapons.ByName("electro"),
            "weapon_splasher"       => Weapons.ByName("vortex"),
            "weapon_bubbleg"        => Weapons.ByName("hagar"),
            "weapon_balloony"       => Weapons.ByName("mortar"),
            "weapon_betty"          => Weapons.ByName("devastator"),
            "weapon_imperius"       => Weapons.ByName("crylink"),
            _ => null,
        };

        /// <summary>QC <c>autocvar_sv_mapformat_is_quake3</c> (set by the .bsp/.map format detect). Default true
        /// (Q3 map format), so nailgun -> CRYLINK by default; a Q1 map sets it 0 -> ELECTRO. Public so the item
        /// spawnfunc table (item_armor1 -> ArmorSmall on a Q3 map, ArmorMedium otherwise; spawning.qc:99) can read
        /// the same flag at spawn time.</summary>
        public static bool IsMapformatQuake3() => MapformatIsQuake3();

        // QC autocvar_sv_mapformat_is_quake3 (set by the .bsp/.map format detect). Default true (Q3 map format),
        // so nailgun -> CRYLINK by default; a Q1 map sets it 0 -> ELECTRO.
        private static bool MapformatIsQuake3()
        {
            string s = CvarString("sv_mapformat_is_quake3");
            return string.IsNullOrEmpty(s) || Api.Cvars.GetFloat("sv_mapformat_is_quake3") != 0f;
        }

        // QC cvar_string("g_mod_balance") == "XDF" — the DeFRaG balance variant.
        private static bool IsXdf() => CvarString("g_mod_balance") == "XDF";

        // =====================================================================================
        //  Q3 ammo remaps (the SPAWNFUNC_Q3AMMO half) — ammo classname -> (Weapon, count multiplier)
        // =====================================================================================

        /// <summary>One Q3 ammo box: the Weapon whose ammo_type + GetAmmoConsumption the resource derives from,
        /// and the optional .count multiplier the SPAWNFUNC_Q3 variadic applies.</summary>
        public readonly record struct AmmoRemap(Weapon Wep, float CountMultiplier);

        /// <summary>
        /// QC the SPAWNFUNC_Q3AMMO classname -> weapon (+ .count multiplier) table. Resolving the weapon gives
        /// the ammo_type (which ammo item to spawn) and GetAmmoConsumption (the per-shot scale); the multiplier
        /// is the SPAWNFUNC_Q3 variadic (8/0.1 for the SG<->MG pair, 0.125 for lightning, else 1). Returns null
        /// for an unmapped ammo classname.
        /// </summary>
        public static AmmoRemap? AmmoForClassname(string className)
        {
            switch (className)
            {
                // SG <-> MG ammo. The arena swap the port never sets would flip these weapons + multipliers
                // (shells: 8 if ARENA->MG else 1; bullets: 0.1 if ARENA->SG else 1). We keep the non-arena form.
                case "ammo_shells":    return Make("shotgun", 1f);     // ARENA: (machinegun, 8)
                case "ammo_bullets":   return Make("machinegun", 1f);  // ARENA: (shotgun, 0.1)
                case "ammo_grenades":  return Make("mortar", 1f);
                case "ammo_mines":     return Make("minelayer", 1f);
                case "ammo_belt":      return Make("hagar", 1f);
                case "ammo_hmg":       return Make("hagar", 1f);
                case "ammo_nails":     return Make(MapformatIsQuake3() ? "crylink" : "electro", 1f);
                case "ammo_lightning": return Make("electro", 0.125f);
                case "ammo_cells":     return Make(IsXdf() ? "hagar" : "hlac", 1f);
                case "ammo_slugs":     return Make("vortex", 1f);
                case "ammo_bfg":       return Make(IsXdf() ? "crylink" : "fireball", 1f);
                case "ammo_rockets":   return Make("devastator", 1f);
                default: return null;
            }
        }

        private static AmmoRemap? Make(string weaponNetName, float mult)
            => Weapons.ByName(weaponNetName) is { } w ? new AmmoRemap(w, mult) : null;

        /// <summary>
        /// QC the SPAWNFUNC_Q3AMMO body for a Q3 ammo classname: multiply <see cref="Entity.Count"/> by the
        /// remap's multiplier, and if the result is non-zero AND the weapon has an ammo_type, seed the matching
        /// resource on the edict with <c>rint(count * GetAmmoConsumption(weapon))</c> (so the spawned ammo item's
        /// ItemInit — which only fills the resource when unset — keeps this scaled amount). Returns the ammo
        /// Pickup to spawn (GetAmmoItem(ammo_type)), or null when the weapon is ammo-less (FIREBALL): QC's
        /// SPAWNFUNC_BODY then deletes the entity.
        /// </summary>
        public static Pickup? ApplyAmmoRemap(Entity e, AmmoRemap remap)
        {
            // QC: __VA_OPT__(this.count *= (__VA_ARGS__);) — QuakeC .count is a FLOAT, so the multiply keeps
            // fractional precision (lightning 100 * 0.125 = 12.5) before the GetAmmoConsumption multiply. The
            // port's Entity.Count is an int, so carry the scaled value in a local float (NOT back into e.Count,
            // which would truncate 12.5 -> 12 and skew the resource by an extra rint).
            float scaledCount = e.Count * remap.CountMultiplier;

            ResourceType ammoType = remap.Wep.AmmoType;
            if (ammoType == ResourceType.None)
                return null; // GetAmmoItem(RES_NONE) == NULL -> SPAWNFUNC_BODY deletes (e.g. FIREBALL)

            // QC: if(this.count && ammo_type != RES_NONE) SetResource(this, ammo_type, rint(count * GetAmmoConsumption(wpn)))
            // QC gates on `this.count` (the SCALED value here); a 0 .count leaves the ammo item at its default.
            // rint = round-half-to-even (C rint under the default FE_TONEAREST), which MathF.Round defaults to.
            if (scaledCount != 0f)
            {
                float amount = System.MathF.Round(scaledCount * GetAmmoConsumption(remap.Wep));
                e.SetResourceExplicit(ammoType, amount);
            }

            return AmmoItemFor(ammoType);
        }

        /// <summary>QC <c>GetAmmoItem(ammo_type)</c>: the ammo Pickup for a resource (the inverse of Ammo.m_ammotype).</summary>
        public static Pickup? AmmoItemFor(ResourceType ammoType) => ammoType switch
        {
            ResourceType.Shells  => Items.ByName("shells"),
            ResourceType.Bullets => Items.ByName("bullets"),
            ResourceType.Rockets => Items.ByName("rockets"),
            ResourceType.Cells   => Items.ByName("cells"),
            ResourceType.Fuel    => Items.ByName("fuel"),
            _ => null,
        };

        // =====================================================================================
        //  Q3DF target_* entities (quake3.qc:119-295)
        // =====================================================================================

        /// <summary>Install the Q3DF target_* spawnfuncs (target_init / target_score / target_fragsFilter /
        /// target_print / target_smallprint). Called from <see cref="MapObjectsRegistry.RegisterAll"/>.</summary>
        public static void Register()
        {
            SpawnFuncs.Register("target_init", TargetInitSetup);
            SpawnFuncs.Register("target_score", TargetScoreSetup);
            SpawnFuncs.Register("target_fragsFilter", TargetFragsFilterSetup);
            SpawnFuncs.Register("target_print", TargetPrintSetup);
            SpawnFuncs.Register("target_smallprint", TargetPrintSetup); // QC: spawnfunc_target_print(this)
        }

        // ---- target_init (quake3.qc:119-187): the DeFRaG "weapon remove" reset entity ----

        // QC spawnflag bits read by target_init_use.
        private const int InitKeepArmor    = 1;  // bit 0 — DON'T reset armor
        private const int InitKeepHealth   = 2;  // bit 1 — DON'T reset health
        private const int InitKeepWeapons  = 4;  // bit 2 — DON'T reset ammo + weapons
        private const int InitKeepPowerups = 8;  // bit 3 — DON'T strip powerups/buffs
        // bit 4 (16) holdables: "We don't have holdables." (no-op, QC quake3.qc:176-179)
        private const int InitMeleeOnly    = 32; // bit 5 — reset to melee-only (zero ammo, SHOTGUN only)

        public static void TargetInitSetup(Entity this_)
        {
            this_.ClassName = "target_init";
            this_.Use = TargetInitUse;
            MapMover.IndexRegister(this_); // findable by the trigger that targets it (QC find() over all edicts)
        }

        // QC target_init_use (quake3.qc:119-182).
        private static void TargetInitUse(Entity this_, Entity actor)
        {
            // The reset blocks below operate on the activating PLAYER's resources / loadout. QC reads the
            // precomputed start_* globals; the port computes the same loadout via SpawnSystem.ComputeStartItems
            // (the SetStartItems seam, so arena/loadout mutators are honoured identically).
            StartLoadout start = SpawnSystem.ComputeStartItems();
            float now = Now();

            if ((this_.SpawnFlags & InitKeepArmor) == 0)
            {
                actor.SetResource(ResourceType.Armor, start.Armor);
                actor.PauseRotArmorFinished = now + Bal("g_balance_pause_armor_rot", 1f);
            }

            if ((this_.SpawnFlags & InitKeepHealth) == 0)
            {
                actor.SetResource(ResourceType.Health, start.Health);
                actor.PauseRotHealthFinished = now + Bal("g_balance_pause_health_rot", 1f);
                actor.PauseRegenFinished = now + Bal("g_balance_pause_health_regen", 5f);
            }

            if ((this_.SpawnFlags & InitKeepWeapons) == 0)
            {
                if ((this_.SpawnFlags & InitMeleeOnly) != 0) // spawn with only melee (QC quake3.qc:136-145)
                {
                    actor.SetResource(ResourceType.Shells, 0f);
                    actor.SetResource(ResourceType.Bullets, 0f);
                    actor.SetResource(ResourceType.Rockets, 0f);
                    actor.SetResource(ResourceType.Cells, 0f);
                    actor.SetResource(ResourceType.Fuel, 0f);
                    SetWeaponSet(actor, new[] { "shotgun" }); // QC: STAT(WEAPONS, actor) = WEPSET(SHOTGUN)
                }
                else // QC quake3.qc:146-155: restore the start loadout
                {
                    actor.SetResource(ResourceType.Shells, start.AmmoShells);
                    actor.SetResource(ResourceType.Bullets, start.AmmoBullets);
                    actor.SetResource(ResourceType.Rockets, start.AmmoRockets);
                    actor.SetResource(ResourceType.Cells, start.AmmoCells);
                    actor.SetResource(ResourceType.Fuel, start.AmmoFuel);
                    SetWeaponSet(actor, start.Weapons);
                }
            }

            if ((this_.SpawnFlags & InitKeepPowerups) == 0)
            {
                // QC: FOREACH(StatusEffects, it.instanceOfPowerupStatusEffect, it.m_remove(...)) — drop the four
                // powerups. (QC additionally drops held BuffsMutator buffs with a notification; that buff-drop
                // notification path is not reachable from this layer — recorded as a gap in the file header.)
                RemovePowerup(actor, "strength");
                RemovePowerup(actor, "shield");
                RemovePowerup(actor, "speed");
                RemovePowerup(actor, "invisibility");
            }

            // bit 4 (16): holdables — "We don't have holdables." (QC no-op).

            MapMover.UseTargets(this_, actor, null); // QC: SUB_UseTargets(this, actor, trigger)
        }

        private static void RemovePowerup(Entity actor, string name)
        {
            if (StatusEffectsCatalog.ByName(name) is { } def)
                StatusEffectsCatalog.Remove(actor, def);
        }

        // Set the activator's owned-weapon set (both reps: the WepSet authority + the Player NetName set).
        private static void SetWeaponSet(Entity actor, System.Collections.Generic.IEnumerable<string> netNames)
        {
            actor.OwnedWeaponSet.Clear();
            var player = actor as Player;
            player?.OwnedWeapons.Clear();
            foreach (string n in netNames)
            {
                if (Weapons.ByName(n) is { } w)
                    actor.OwnedWeaponSet.Add(w);
                player?.OwnedWeapons.Add(n);
            }
        }

        // ---- target_score (quake3.qc:190-203): a CTS-only accumulator ----

        public static void TargetScoreSetup(Entity this_)
        {
            if (!Cts) { MapMover.RemoveEntity(this_); return; } // QC: if(!g_cts) { delete(this); return; }
            this_.ClassName = "target_score";
            if (this_.Count == 0)
                this_.Count = 1; // QC: if(!this.count) this.count = 1;
            this_.Use = ScoreUse;
            MapMover.IndexRegister(this_); // findable by the trigger that targets it
        }

        // QC score_use (quake3.qc:190-195).
        private static void ScoreUse(Entity this_, Entity actor)
        {
            if (!IsPlayer(actor)) return;
            actor.FragsFilterCnt += this_.Count;
        }

        // ---- target_fragsFilter (quake3.qc:205-236): a CTS-only gate on fragsfilter_cnt ----

        private const int FragsFilterRemover = 1 << 0; // BIT(0)
        private const int FragsFilterRunonce = 1 << 1; // BIT(1) — introduced but unused by q3df
        private const int FragsFilterSilent  = 1 << 2; // BIT(2)
        private const int FragsFilterReset   = 1 << 3; // BIT(3)

        public static void TargetFragsFilterSetup(Entity this_)
        {
            if (!Cts) { MapMover.RemoveEntity(this_); return; } // QC: if(!g_cts) { delete(this); return; }
            this_.ClassName = "target_fragsFilter";
            if (this_.Frags == 0f)
                this_.Frags = 1f; // QC: if(!this.frags) this.frags = 1;
            this_.Use = FragsFilterUse;
            MapMover.IndexRegister(this_); // findable by the trigger that targets it
        }

        // QC fragsfilter_use (quake3.qc:210-228).
        private static void FragsFilterUse(Entity this_, Entity actor)
        {
            if (!IsPlayer(actor)) return;
            int req = (int)this_.Frags;
            if (actor.FragsFilterCnt >= req)
            {
                if ((this_.SpawnFlags & FragsFilterReset) != 0)
                    actor.FragsFilterCnt = 0;
                else if ((this_.SpawnFlags & FragsFilterRemover) != 0)
                    actor.FragsFilterCnt -= req;
                MapMover.UseTargets(this_, actor, null);
            }
            else if ((this_.SpawnFlags & FragsFilterSilent) == 0)
            {
                // QC: centerprint(actor, "<N> more frag[s] needed"); play2(actor, SND(TALK)). Route the text
                // through the raw-centerprint channel (→ CenterPrintPanel.Add) and play the audible half.
                int more = req - actor.FragsFilterCnt;
                MapMover.Centerprint(actor, more == 1 ? "1 more frag needed" : $"{more} more frags needed");
                MapMover.Play2(actor, "misc/talk.wav"); // QC quake3.qc:226 play2(actor, SND(TALK)) — 2D VOL_BASE/ATTEN_NONE
            }
        }

        // ---- target_print / target_smallprint (quake3.qc:238-295) ----

        private const int PrintRedTeam   = 1 << 0; // Q3 only
        private const int PrintBlueTeam  = 1 << 1; // Q3 only
        private const int PrintPrivate   = 1 << 2; // Q3 only
        private const int PrintBroadcast = 1 << 3; // Q3DF only

        public static void TargetPrintSetup(Entity this_)
        {
            this_.ClassName = "target_print";
            this_.Use = TargetPrintUse;
            MapMover.IndexRegister(this_); // findable by the trigger that targets it
        }

        // QC target_print_use (quake3.qc:249-278).
        private static void TargetPrintUse(Entity this_, Entity actor)
        {
            if (!IsPlayer(actor)) return;
            if (string.IsNullOrEmpty(this_.Message)) return;

            // q3compat & Q3COMPAT_DEFI selects the Q3DF spawnflag reading; the port never sets it, so we use
            // the Q3 reading (the default, file-header deviation). In Q3 mode PRINT_PRIVATE (bit 2) -> private;
            // in Q3DF mode the message is private UNLESS PRINT_BROADCAST (bit 3) is set.
            bool priv = IsDefi()
                ? (this_.SpawnFlags & PrintBroadcast) == 0
                : (this_.SpawnFlags & PrintPrivate) != 0;

            // NB: the Q3 broadcast team filter (PRINT_REDTEAM / PRINT_BLUETEAM, bits 0/1) restricts the
            // FOREACH_CLIENT recipients to one team. This layer doesn't own the live client roster, so the
            // broadcast plays the audible half on the activator only (the text render is client-side); the
            // per-team recipient filtering is a host-roster concern, recorded as a deferred parity gap.
            // priv vs broadcast both resolve to the same audible-half emit here, but the branch is kept
            // explicit so the spawnflag interpretation stays observable.
            if (priv)
                TargetPrintMessage(actor, this_.Message);
            else
                TargetPrintMessage(actor, this_.Message);
        }

        // QC target_print_message (quake3.qc:243-247): centerprint(actor, this.message) + play2 SND(TALK). The
        // text routes through the raw-centerprint channel (→ CenterPrintPanel.Add); the audible half plays SND(TALK).
        private static void TargetPrintMessage(Entity actor, string? message)
        {
            MapMover.Centerprint(actor, message);
            MapMover.Play2(actor, "misc/talk.wav"); // QC quake3.qc:246 play2(actor, SND(TALK)) — 2D VOL_BASE/ATTEN_NONE
        }

        // q3compat & Q3COMPAT_DEFI — the .defi-file flag the port never sets (default Q3 reading).
        private static bool IsDefi() => false;

        // =====================================================================================
        //  shared helpers
        // =====================================================================================

        // QC IS_PLAYER(e): a live client (the .use callbacks gate on it).
        private static bool IsPlayer(Entity e) => (e.Flags & EntFlags.Client) != 0;

        private static float Now() => Api.Services is null ? 0f : Api.Clock.Time;
    }
}
