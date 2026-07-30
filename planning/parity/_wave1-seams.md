# Wave 1 seam reference (for Wave 2)

Wave 1 implemented these shared seams on branch `claude/parity-port-waves` (build green, 2755 tests pass).
Wave-2 agents: **call these APIs** rather than inventing your own, and check the TODOs for your file.

## round-drive

_Brought the Common RoundHandler to full QC round_handler_Think parity (game_starttime hold, intermission idle, 1 Hz countdown, end-delay wait, first-round-no-reset) with new public GameStartTime/IntermissionRunning/OnCountdownTick/OnRoundCounted/OnRoundReset plus settable RoundEndTime/RoundsPlayed and a Reset(float) overload — backward-compatible (Onslaught/Survival/CA/FreezeTag self-drive paths verified, incl. the OnslaughtCombatTests 2-tick→InProgress assertion). In GameWorld, CA and FreezeTag now drive the LIVE round handler off their real CheckTeams/CheckWinner predicates and their warmup/round-timelimit/end-delay cvars (CA's round-timelimit stalemate via PreventStalemate now fires), with a per-frame roster feed and a round-timing mirror into each gametype's own handler; the double-award CheckRound calls were removed. Also seeded default_player_alpha/default_weapon_alpha at worldspawn via MutatorHooks.FireSetDefaultAlpha (exposed as GameWorld.DefaultPlayerAlpha/DefaultWeaponAlpha) and added the previously-dead procedural Keepaway/TeamKeepaway ball spawn (SpawnBall via BallEntity.RandomMapLocation post-map-spawn); the PlayerSpawn/PlayerDies/PlayerPreThink/SvStartFrame mutator .Call sites were confirmed already live. MatchController.cs needed no change._

Files: `src/VortexArena.Common/Gameplay/GameTypes/RoundHandler.cs`, `src/VortexArena.Server/GameWorld.cs`

**APIs you can call:**
- `VortexArena.Common.Gameplay.RoundHandler.GameStartTime { get; set; } (float) — QC game_starttime pre-game hold`
- `VortexArena.Common.Gameplay.RoundHandler.IntermissionRunning { get; set; } (bool) — QC intermission_running idle`
- `VortexArena.Common.Gameplay.RoundHandler.OnCountdownTick { get; set; } (Action<int>) — per-second round-start countdown edge`
- `VortexArena.Common.Gameplay.RoundHandler.OnRoundCounted { get; set; } (Action) — QC cnt==0 ROUNDS_PL per-player bump`
- `VortexArena.Common.Gameplay.RoundHandler.OnRoundReset { get; set; } (Action) — QC reset_map(false) next-round respawn`
- `VortexArena.Common.Gameplay.RoundHandler.RoundEndTime { get; set; } (float, now settable) — round timelimit expiry (mirrorable)`
- `VortexArena.Common.Gameplay.RoundHandler.RoundsPlayed { get; set; } (int, now settable)`
- `VortexArena.Common.Gameplay.RoundHandler.Reset(float nextStart) — QC round_handler_Reset(next_think) overload`
- `VortexArena.Common.Gameplay.RoundHandler.CountdownSecondsLeft { get; } (int)`
- `GameWorld.EnableRounds(Func<bool>? canStart, Func<bool>? canEnd, Action? onRoundStart, float? endDelay, float? countdown, float? roundTimeLimit) — now takes optional per-gametype timing (QC round_handler_Init)`
- `GameWorld.DefaultPlayerAlpha { get; } (float) — QC default_player_alpha seeded at worldspawn`
- `GameWorld.DefaultWeaponAlpha { get; } (float) — QC default_weapon_alpha seeded at worldspawn`

## spawn-items

_Wired the dead SetWeaponArena seam: SpawnSystem.ComputeStartItems (the port's readplayerstartcvars equivalent) now fires MutatorHooks.SetWeaponArena.Call, expands the resulting arena string into start_weapons via a new public ExpandWeaponArena(arena, out active) helper (most/all/devall/none/list, with the *_available variants using QC's no-map-weapons fallback), and — when an arena is active — replaces the weapon set and adds IT_UNLIMITED_AMMO|IT_UNLIMITED_SUPERWEAPONS, matching Base world.qc:2009-2110. Also fixed a latent bug where StartLoadout.ItemFlags (set by Mayhem/CA/hook handlers) was never translated to player.Items: both ApplyStartLoadout and ApplyWarmupLoadout now OR in the flag bits via a new StartItemFlagBits map, and warmup mirrors the active arena per Base (warmup_start_weapons = start_weapons). SetStartItems was already called and stays in place; the seam is now fully live so Mayhem/TeamMayhem (already subscribed) spawn with the full arsenal + unlimited ammo immediately, and Wave-2 CA/LMS can subscribe their hooks to do the same._

Files: `src/VortexArena.Common/Gameplay/Player/SpawnSystem.cs`

**APIs you can call:**
- `public static WepSet SpawnSystem.ExpandWeaponArena(string arena, out bool active) — resolves a g_weaponarena keyword ("all"/"1"/"devall"/"most"/"none"/"*_available") or a space-separated weapon-name list into the concrete WepSet of arena weapons; `active` is false for ""/"0"/"off" (no arena). Wave-2 gametypes can call it directly if they need the set.`
- `SpawnSystem.ComputeStartItems() signature unchanged (StartLoadout return) but now ALSO fires MutatorHooks.SetWeaponArena.Call and expands the arena into the loadout — the new live SetWeaponArena .Call site.`

## damage-channel

_The damage-channel seam was already substantially implemented in DamageSystem.cs and I verified each leg against Base QC (server/damage.qc Damage(), freezetag/keepaway Damage_Calculate): (1) the full string deathtype/hittype rides end-to-end via the deathTag override + DeathTypes "|hittype" suffix tokens; (2) per-entity Entity.DamageForceScale is consulted in ApplyKnockback and the Keepaway/TKA possession matrix + FreezeTag frozen-immunity + Midair scaling all flow through the live MutatorHooks.DamageCalculate hook (QC has no separate per-entity damage scalar — the hook IS the channel); (3) both damage-path hooks (Damage_Calculate, PlayerDamaged + PlayerDamage_SplitHealthArmor) are dispatched live from this file. I documented the seam contract in-file and added one new public helper, DamageSystem.SplashDeathType, so the (separately-owned) splash callers have a single stable entry point to set HITTYPE_SPLASH — closing the discoverability half of the splash gap. The behavioral one-line call-site changes live in files I don't own (see todos)._

Files: `src/VortexArena.Common/Gameplay/Damage/DamageSystem.cs`

**APIs you can call:**
- `public static string DamageSystem.SplashDeathType(string deathType) — canonically OR's HITTYPE_SPLASH onto an indirect blast victim's deathtype (no-op for special/non-weapon deaths, idempotent); the single seam the splash/RadiusDamage indirect path calls so the splash bit is set in one place`

## weaponfire-fx

_Wired the shared weapon-fire FX/audio seam entirely inside WeaponFiring.cs (the Common fire path), calling the existing-but-dead emission APIs: a new optional tracerEffect param on FireBullet now emits bullet-tracer trailparticles via EffectEmitter.EmitTrail (open-air >16u + through-player >4u legs, matching QC fireBullet_trace_callback), and three new shared hooks — BulletImpactFx (impact puff + the previously-zero-caller SoundSystem.PlayRic ricochet), EjectCasing (g_casings>=2 gate with QC's view-frame eject velocity), and MeleeWoosh (SHOTGUN_WOOSH effect + swing sound). All existing FireBullet/FireRailgunBullet callers are unaffected (signatures stayed back-compat). WeaponFireDriver.cs needed no change — it dispatches WrThink and the weapons call these helpers from their fire functions._

Files: `src/VortexArena.Common/Gameplay/Weapons/WeaponFiring.cs`

**APIs you can call:**
- `WeaponFiring.CasingType (enum Bullet/Shell)`
- `WeaponFiring.FireBullet tracerEffect param`
- `WeaponFiring.BulletImpactFx(Entity,Vector3,Vector3,string,bool)`
- `WeaponFiring.EjectCasing(Entity,Vector3,CasingType)`
- `WeaponFiring.MeleeWoosh(Entity,Vector3,Vector3,string?,string?)`

## mutator-hooks

_The mutator-hooks seam was almost entirely about reviving dead .Call dispatch sites whose actual call points live in files owned by OTHER Wave-1 agents (SpawnSystem, NetEntity/world-init, Vehicles). Within my three owned files I added stable Fire* dispatch entry points on MutatorHooks for the five chains that had subscribers but no caller (SetWeaponArena, ForbidRandomStartWeapons, SetDefaultAlpha, VehicleInit, VehicleTouch), mirroring the existing FireStartFrame pattern, so those owners can fire them with a fixed signature and Wave-2 mutator handlers become reachable. All edits are purely additive — no existing args struct, hook chain, or signature changed; MutatorActivation's subscription loop (Apply at GameWorld.cs:511, DeactivateAll at Registries.cs:148) was already correct and needed no change. The actual call-site insertions are recorded as todos for the owning files._

Files: `src/VortexArena.Common/Gameplay/Mutators/MutatorHooks.cs`

**APIs you can call:**
- `public static string MutatorHooks.FireSetWeaponArena(string arena)`
- `public static bool MutatorHooks.FireForbidRandomStartWeapons(Entity player)`
- `public static (float playerAlpha, float weaponAlpha) MutatorHooks.FireSetDefaultAlpha(float basePlayerAlpha = 1f, float baseWeaponAlpha = 1f)`
- `public static bool MutatorHooks.FireVehicleInit(Entity vehicle)`
- `public static bool MutatorHooks.FireVehicleTouch(Entity vehicle, Entity toucher)`

## input-impulse

_Wired two currently-unrouted impulses in WeaponImpulses.Handle (the impulse->handler router). (1) Added impulse 21 (`use`, all.qh:130 -> QC PlayerUseKey/impulse.qc:409) routing to the existing VehicleBoarding.UseKey (the port's PlayerUseKey entry) via a new private UseHandle — this is the single entry that, in Base, drives CTF flag throw/pass/request-pass, Keepaway/Team-Keepaway/Nexball/KeyHunt voluntary ball/key drop, and objective/door +use, all through the trailing PlayerUseKey mutator hook. Routed unconditionally (not gated on dead) to match QC, which lets PlayerUseKey's own guards decide. (2) Fixed ReloadHandle (impulse 20) so a non-Reloadable weapon whose QC wr_reload is repurposed (Tuba) dispatches to the previously-callerless Tuba.Reload instrument-cycle (Tuba->Accordion->Klein Bottle) instead of the no-op base WrReload. No new public APIs; all wiring uses existing methods. The flag/ball drop will only become live once the cross-file PlayerUseKey mutator-hook tail is added (see todos)._

Files: `src/VortexArena.Common/Gameplay/Weapons/WeaponImpulses.cs`

## ball-entity

_Created the new shared ball-entity framework BallEntity.cs (the W1-ball-frame seam): a host-side procedural ball spawner exposing the static API BallEntity.SpawnForGametype(BallKind, origin, BallConfig) plus the shared lifecycle helpers Relocate (QC MoveToRandomMapLocation + SelectSpawnPoint fallback), GlideHome (Nexball ResetBall), RespawnThink idle re-arm, AttachToCarrier/DropFromCarrier carry+drop, DropOffsetVelocity (crandom scatter), CarryOrbit (BALL_XYSPEED/DIST orbit anim), and IsRecaptureLocked (0.5s self-recapture lockout). BallKind+BallConfig carry the QC defaults per mode (keepawayball ±24/EF_DIMLIGHT/forcescale 2/relocate; nexball ±16/glide-home), so Wave-2 Keepaway/TeamKeepaway/Nexball agents configure touch/think and the round-drive seam (GameWorld) calls SpawnForGametype to finally make a ball exist on the live path. The framework is Godot-free, deterministic (Prandom), and built on the existing GametypeEntities/Api facade and partial-Entity fields; only this one new file was added (no other-file edits)._

Files: `C:/Users/Bryan/Projects/Xonotic/VortexArena/src/VortexArena.Common/Gameplay/GameTypes/BallEntity.cs`

**APIs you can call:**
- `enum BallKind { KeepawayBall, NexballBasketball, NexballFootball }`
- `sealed class BallConfig { string ClassName; string Model; Vector3 Mins; Vector3 Maxs; int Effects; int TrailColor; float DamageForceScale; bool TakesDamage; bool Pushable; float RespawnTime; bool RelocateOnRespawn; EntityTouch? Touch; EntityThink? Think; BallConfig WithKindDefaults(BallKind kind); }`
- `static class BallEntity`
- `const int BallEntity.EfDimLight = 8`
- `const float BallEntity.SelfRecaptureLockout = 0.5f`
- `const float BallEntity.BallOrbitSpeed = 100f`
- `const float BallEntity.BallOrbitDist = 24f`
- `static readonly Vector3 BallEntity.RespawnVelocity = (0,0,200)`
- `static readonly string[] BallEntity.SpawnPointClassNames`
- `static Entity? BallEntity.SpawnForGametype(BallKind kind, Vector3 origin, BallConfig? config = null)`
- `static void BallEntity.Relocate(Entity ball, float respawnTime)`
- `static void BallEntity.GlideHome(Entity ball, float idleTime)`
- `static void BallEntity.RespawnThink(Entity ball)`
- `static void BallEntity.AttachToCarrier(Entity ball, Entity carrier, Vector3 carryOffset = default)`
- `static Entity? BallEntity.DropFromCarrier(Entity ball, float respawnTime, bool takesDamage = true)`
- `static Vector3 BallEntity.DropOffsetVelocity()`
- `static Vector3 BallEntity.CarryOrbit(Vector3 carrierOrigin, float time, int cnt = 1, int chainCount = 1)`
- `static bool BallEntity.IsRecaptureLocked(Entity ball, Entity toucher)`
- `static Vector3 BallEntity.RandomMapLocation(Vector3 fallbackOrigin)`

## entity-frame

_Fixed the headline gap shared by both framework shards: the bit-faithful TurretAI.Damage and VehicleCommon.DamageVehicle had no live caller, so live turret/vehicle damage fell through DamageSystem.EventDamage (DamageSystem.cs:294) to the PLAYER damage path — bypassing inactive-immunity, friendly-fire scaling, MOVE knockback, retaliation (turrets) and the per-weapon damagerate + shield-then-health split + death-eject/respawn (vehicles). I wired each via the established GtEventDamage seam (same pattern monsters and Onslaught objectives already use): added a GtEventDamage-shaped EventDamage shim to TurretAI and VehicleCommon and installed it in the two shared chokepoints (TurretSpawn.Init, VehicleCommon.SpawnVehicle), which covers all 13 turrets and 4 vehicles since every unit routes through them. The turret shim runs the gate, subtracts the gated damage from RES_HEALTH, and fires Combat.Death on a lethal hit (which the existing OnAnyDeath subscription turns into the death blast + respawn); the vehicle shim just reorders args into the self-contained DamageVehicle. Public signatures of existing methods (TurretAI.Damage, DamageVehicle) are unchanged so existing unit tests still hold; only additive new APIs were introduced for Wave-2 units to depend on._

Files: `C:/Users/Bryan/Projects/Xonotic/VortexArena/src/VortexArena.Common/Gameplay/Turrets/TurretAI.cs`, `C:/Users/Bryan/Projects/Xonotic/VortexArena/src/VortexArena.Common/Gameplay/Turrets/TurretSpawn.cs`, `C:/Users/Bryan/Projects/Xonotic/VortexArena/src/VortexArena.Common/Gameplay/Vehicles/VehicleCommon.cs`

**APIs you can call:**
- `public static void TurretAI.EventDamage(Entity turret, Entity? inflictor, Entity? attacker, string deathType, float damage, Vector3 hitLoc, Vector3 force) — the GtEventDamage-shaped turret event_damage shim (runs the TurretAI.Damage gate, applies gated damage to RES_HEALTH, fires Combat.Death on lethal). Wired in TurretSpawn.Init so it is now live for every turret.`
- `public static void VehicleCommon.EventDamage(Entity vehic, Entity? inflictor, Entity? attacker, string deathType, float damage, Vector3 hitLoc, Vector3 force) — the GtEventDamage-shaped vehicle event_damage shim (thin reorder-adapter delegating to VehicleCommon.DamageVehicle). Wired in VehicleCommon.SpawnVehicle so it is now live for every vehicle.`

## mapobject-seam

_Added the shared map-object host plumbing to MapObjectsCommon.cs (MapMover): a host-wired Centerprint seam (CenterprintHandler + Centerprint(actor,message)) for free-text .message networking; InstallEventDamage/ClearEventDamage/MarkDamageable wrappers over the existing Entity.GtEventDamage / DamageSystem.EventDamage mechanism for shootable brushes; ApplyDoorSounds/ApplyPlatSounds/ApplySecretSounds soundpack selectors reading Entity.Sounds (+ new Sound1/Sound2 legacy overrides); and SetPlatMoveType + CubicSpeedFuncIsSane porting platforms.qc's platmovetype parse with reverse-curve sanity reject (plus new Entity.Platmovetype field). MapObjectsRegistry.cs needed no change. Key TODOs: the new sounds/sound1/sound2/platmovetype map keys still need promotion in the field binders (MapObjectFieldsExtra.cs / GameWorld.cs — not in my owned set), and the host must wire CenterprintHandler to the real net centerprint channel; Wave-2 unit files supply the call sites._

Files: `src/VortexArena.Common/Gameplay/MapObjects/MapObjectsCommon.cs`

**APIs you can call:**
- `public static System.Action<Entity,string>? MapMover.CenterprintHandler  — host-wired sink for free-text map-object .message centerprints`
- `public static void MapMover.Centerprint(Entity? actor, string? message)  — QC centerprint(actor,this.message); no-ops unless actor is a real client + message non-empty + handler wired`
- `public delegate void MapMover.EventDamageHandler(Entity self, Entity? inflictor, Entity? attacker, string deathType, float damage, Vector3 hitLoc, Vector3 force)  — QC .event_damage shape`
- `public static void MapMover.InstallEventDamage(Entity e, EventDamageHandler handler)  — sets Entity.GtEventDamage (dispatched by DamageSystem.EventDamage for non-player edicts)`
- `public static void MapMover.ClearEventDamage(Entity e)`
- `public static void MapMover.MarkDamageable(Entity e, float health = 10000f)  — shootable-brush setup (Health/MaxHealthMover + TakeDamage=DamageMode.Aim)`
- `public static void MapMover.ApplyDoorSounds(Entity e, bool q3compat = false)  — door.qc sounds>0 -> medplat1/medplat2 on Noise2/Noise1`
- `public static void MapMover.ApplyPlatSounds(Entity e, bool q3compat = false)  — plat.qc sounds 1->plat1/plat2, 2->medplat, + legacy Sound1/Sound2 overrides`
- `public static void MapMover.ApplySecretSounds(Entity e)  — door_secret sounds>0 -> medplat pack`
- `public static bool MapMover.SetPlatMoveType(Entity e, string? s)  — QC set_platmovetype: parse 'start end [force]' into PlatMoveStart/PlatMoveEnd, false on insane reverse curve`
- `public static bool MapMover.CubicSpeedFuncIsSane(float startSpeedFactor, float endSpeedFactor)  — QC cubic_speedfunc_is_sane reverse-curve reject`
- `Entity partial fields added: public string Sound1, Sound2, Platmovetype (QC .sound1/.sound2/.platmovetype map keys)`

## alpha-render

_Implemented the client-side per-entity alpha render in PlayerModel.cs: a new ApplyAlpha(float) method maps the networked QC render-alpha (Entity.Alpha, default 1=opaque) to per-instance GeometryInstance3D.Transparency (1 - clamped alpha) on a once-flattened, swap-invalidated mesh cache, deliberately using per-INSTANCE transparency rather than editing the AssetSystem-shared surface materials (the documented RC3/RC4 lesson). It is wired into Pose() so every rendered frame reads e.Alpha and applies it (before the skeletal early-out, so static-prop/placeholder models fade too); it's idempotent and skips the per-mesh interop when the value and mesh set are unchanged. With the default Alpha=1 this is a no-op, so there is no regression until the net seam carries Cloaked's 0.25 / fades onto the client Entity.Alpha._

Files: `C:/Users/Bryan/Projects/Xonotic/VortexArena/game/client/PlayerModel.cs`

**APIs you can call:**
- `public void PlayerModel.ApplyAlpha(float alpha)`

## csqcmodel

_Extended the CSQC player-model appearance/effects pipeline so Wave-3 presentation can drive forced colors/effects per player, by adding additive, default-safe API on top of the existing pure helpers. New: CsqcModelAppearance.ForcedAppearance (extra EF_* bits + MF_* model flags + optional forced glowmod) with a ComposeForcedAppearance role-mapper, CsqcModelEffectFlags.RoleGlowFlags + a ForcedEffectFlags presentation-owned mask, the DefaultPlayerModel/Skin force-model fallback constants, an optional trailing `forced` param on CsqcModelEffects.Apply (OR's the forced effect/model-flag bits in; existing ClientWorld call still binds unchanged), and a public CsqcModelEffects.ApplyForcedGlowmod helper. Wiring the new param + glowmod helper into the live per-frame pass requires edits in ClientWorld.cs/NetGame.cs (not owned here) — recorded as todos._

Files: `src/VortexArena.Engine/Simulation/CsqcModelAppearance.cs`, `src/VortexArena.Engine/Simulation/CsqcModelEffectFlags.cs`, `game/client/CsqcModelEffects.cs`

**APIs you can call:**
- `CsqcModelAppearance.DefaultPlayerModel : const string = "models/player/erebus.iqm"`
- `CsqcModelAppearance.DefaultPlayerSkin : const int = 0`
- `CsqcModelAppearance.ForcedAppearance : readonly record struct { int ExtraEffects; int ModelFlags; (float r,float g,float b) ForcedGlowmod; static ForcedAppearance None; bool HasForcedGlowmod; }`
- `CsqcModelAppearance.ComposeForcedAppearance(bool strength, bool shield, bool jetpackActive, (float r,float g,float b) forcedGlowmod) : ForcedAppearance`
- `CsqcModelEffectFlags.RoleGlowFlags(bool strength, bool shield) : int`
- `CsqcModelEffectFlags.ForcedEffectFlags : const int (presentation-owned EF_* mask)`
- `CsqcModelEffects.Apply(EffectSystem? fx, Node3D root, Entity e, State st, int modelFlags, float frameTime, ISoundService? sound, bool isRespawnGhost, CsqcModelAppearance.ForcedAppearance forced = default) : string?  (added optional trailing param; existing call site unchanged)`
- `CsqcModelEffects.ApplyForcedGlowmod(IReadOnlyList<MeshInstance3D> meshes, CsqcModelAppearance.ForcedAppearance forced) : void`

## net-server

_Implemented the net-server seam across the four owned files. Projectile networking was already substantially wired (Classify→NetEntityKind.Projectile + ProjectileCatalogKey already network the model/trail per type, driven client-side by ProjectileCatalog), so the genuinely-dead piece — shoot-down — is now reachable via Projectiles.MakeShootable, which installs a GtEventDamage shim (a path DamageSystem.EventDamage already dispatches) that runs W_CheckProjectileDamage, subtracts hp, and fires the per-weapon ProjectileDamage callback at hp<=0, without touching the forbidden DamageSystem.cs. Added a per-entity Alpha network field (EntityField bit 17, byte-quantized, 0=opaque) to NetEntity + wired it into ServerNet's player and entity snapshots, and extended GametypeStatusBlock with CTF flag-status / Domination pps / Keepaway carrier feeds (Kind 5/6/7, Capture+Deserialize+Decoded), which ServerNet already sends each snapshot._

Files: `C:/Users/Bryan/Projects/Xonotic/VortexArena/src/VortexArena.Net/NetEntity.cs`, `C:/Users/Bryan/Projects/Xonotic/VortexArena/src/VortexArena.Common/Gameplay/Weapons/Projectiles.cs`, `C:/Users/Bryan/Projects/Xonotic/VortexArena/game/net/ServerNet.cs`, `C:/Users/Bryan/Projects/Xonotic/VortexArena/src/VortexArena.Net/GametypeStatusBlock.cs`

**APIs you can call:**
- `public static void Projectiles.MakeShootable(Entity e, float exception = -1f) — installs a GtEventDamage shim (already live-dispatched by DamageSystem.EventDamage) that runs the g_projectiles_damage gate, subtracts Entity.Health, and fires the projectile's existing ProjectileDamage callback at hp<=0; this is what finally makes shoot-down reachable. Weapons call it after setting TakeDamage=Yes + Health + ProjectileDamage.`
- `public static bool Projectiles.CheckProjectileDamage(Entity? inflictorOwner, Entity? projOwner, string deathType, float exception) — faithful W_CheckProjectileDamage g_projectiles_damage ladder (-2 never / -1 exception-only / 0 contents+exc / 1 self+contents+exc / 2 all).`
- `NetEntity.cs: EntityField.Alpha = 1<<17 + NetEntityState.Alpha (int, byte-quantized: 0=opaque/not-networked, 1..254 = alpha/255) wired through Diff/WriteDelta/ReadDelta.`
- `GametypeStatusBlock.Kind gains Ctf=5, Domination=6, Keepaway=7; Capture() now emits CTF OBJECTIVE_STATUS pack (per-recipient taken/lost/carrying + neutral/shielded), Domination per-team pps (total + red/blue/yellow/pink), and the Keepaway ball-carrier net id; Deserialize() + Decoded.{ObjectiveStatus, DominationPps[5], CarrierNetId} added; mode-range guard widened to Keepaway. ServerNet already calls Capture() each snapshot, so all three are SENT.`
- `ServerNet.QuantizeAlpha(float)->int + Alpha now set on player and non-player NetEntityState in BuildEntitySet.`

## net-client

_Wired the CLIENT side of the gametype mod-icon feed: extended NetGame.UpdateModIcons to dispatch the three Wave-1 Kinds net-server added to GametypeStatusBlock (Ctf -> ObjectiveStatus pack, Domination -> SetDominationPps with the [0]=total/[1..4]=team decode order, Keepaway -> KA_CARRYING resolved against the local net id), and built the matching ModIconsPanel framework (new Keepaway mode + KeepawayCarrying feed + a DrawKeepaway renderer faithful to HUD_Mod_Keepaway's blink/expand transition, with a swatch fallback). The Ctf/Domination render cases already existed in the panel, so only their dispatch + the Keepaway renderer were new._

Files: `C:/Users/Bryan/Projects/Xonotic/VortexArena/game/net/NetGame.cs`, `C:/Users/Bryan/Projects/Xonotic/VortexArena/game/hud/ModIconsPanel.cs`

**APIs you can call:**
- `ModIconsPanel.ModIconsMode.Keepaway (enum member)`
- `bool ModIconsPanel.KeepawayCarrying { get; set; }`

## TODOs assigned to Wave-2 owners

Each Wave-2 agent: scan this list for your file and do the wiring the seam left for you.

- src/VortexArena.Common/Gameplay/Player/SpawnSystem.cs (alpha-net/startitems owner): PutPlayerInServer hardcodes `p.Alpha = 1f` (line ~524). Read GameWorld.DefaultPlayerAlpha instead so the Cloaked/RunningGuns worldspawn alpha seed actually reaches spawned players.
- Survival.cs / Invasion.cs / LastManStanding.cs / KeyHunt.cs / Onslaught.cs / Assault.cs (Wave-2 owners): these still call bare EnableRounds() with the GENERIC default predicates on the live handler (Survival/Onslaught additionally self-drive their OWN handler via their Tick()). Expose their real CanRoundStart/CanRoundEnd (and round-start side effects) publicly and wire them into GameWorld.EnableRounds(...) the same way CA/FreezeTag now are, then remove the redundant self-drive. KeyHunt/Onslaught/Assault have no predicate-wired live handler at all yet.
- FreezeTag.cs (Wave-2 owner): the live round-start thaw is currently done in GameWorld by iterating Clients.Players and calling ft.Unfreeze(p) (no public bulk-thaw exists). Consider exposing a public RoundStartThaw()/OnRoundStarted() that clears Frozen, and pass it as onRoundStart, so the thaw isn't host-roster-dependent.
- ClanArena.cs (Wave-2 owner): CheckWinner reads its OWN Handler.RoundEndTime; GameWorld now mirrors the live handler's RoundEndTime into ca.Handler each frame (1-frame lag). Consider adding a public RoundEndTimeSource (like Domination) so CheckWinner reads the live handler directly and the mirror can be dropped.
- ClanArena.cs (owned by another agent): subscribe MutatorHooks.SetStartItems (its OnSetStartItems exists as ApplyStartItems-equivalent setting 200/200 + 60/320/160/180/0) AND MutatorHooks.SetWeaponArena setting args.Arena = g_ca_weaponarena ("most") in Activate(); remove/unsubscribe in Deactivate. The seam now consumes both. ApplyStartItems can be deleted once SetStartItems is wired.
- LastManStanding.cs (owned by another agent): subscribe SetStartItems (g_lms_start_health/armor 200/200 + ammo 60/320/160/180/0, strip+set IT_UNLIMITED_AMMO per g_use_ammunition) and SetWeaponArena (g_lms_weaponarena = "most_available") in Activate.
- Optional: if a future map-weapon-availability set lands, refine ExpandWeaponArena's *_available cases to intersect with on-map weapons (weapons_start() | weaponsInMapAll) per Base world.qc:1944-1981; currently they use the full all/devall/most set (Base's documented no-map fallback) — note this divergence in the clanarena/lms/mayhem/tmayhem specs.
- Consider a small unit test (in a test file, not owned here) asserting ComputeStartItems expands a "most"/"most_available" arena into the NORMAL non-hidden weapon set + adds UNLIMITED_AMMO/SUPERWEAPONS flags, and that an "off" arena keeps the Blaster-only default loadout.
- WeaponSplash.cs (RadiusDamage, ~line 196-200, OWNED by the damage-pipeline/Wave-2 task, NOT damage-channel): for every victim that is NOT the directHit entity, OR HITTYPE_SPLASH onto the deathtype before calling the pipeline — i.e. for the string deathTag path call Combat.Damage(e, ..., DamageSystem.SplashDeathType(deathTag), ...), and for the int path have WeaponFiring.ApplyDamage resolve the tag then apply DamageSystem.SplashDeathType. This is the DMG-SPLASH gap (QC damage.qc:917-920); the SplashDeathType seam now exists to call. Direct-hit entity + special deaths keep the plain tag.
- WeaponFiring.cs (ApplyDamage, ~line 507-523, NOT owned by damage-channel): add an optional bool/flag (e.g. isSplash) or a string-deathtype overload so the splash caller can route an indirect hit through DamageSystem.SplashDeathType after the int->NetName tag is resolved — currently ApplyDamage(int) can only emit the plain weapon tag, so the splash bit cannot be set on the int path.
- Fire model (damage-pipeline unit, StatusEffects.cs + Fireball.cs:240 + NadeNapalmBoom.cs:190 + MonsterFramework.AddFireDamage — none owned by damage-channel): unify the ignition Strength convention (frametime-correct everywhere or store dps + tick by real dt), add the Fire_AddDamage overlap LEMMA merge and the Fire_ApplyDamage fire-transfer (g_balance_firetransfer_damage 0.8 / _time 0.9 to adjacent g_damagedbycontents entities), and carry fire_deathtype instead of the generic 'burning' tag. Out of this file's scope.
- No central Heal() dispatcher (damage-pipeline unit): port QC server/damage.qc:948 Heal(targ, inflictor, amount, limit) — game_stopped/frozen/dead gate + event_heal route + limit cap — as a Combat.Heal seam. Could live in DamageSystem.cs in a later pass but was out of this seam's brief (damage channel, not heal channel).
- EffectEmitter (game/client + src/.../Effects/EffectEmitter.cs — NOT owned by this seam): add a dedicated casing temp-entity emission path mirroring QC REGISTER_NET_TEMP(casings)/SpawnCasing (casingtype byte, compressed velocity, angles, the 0x40 first-person + 0x80 silent flags, PVS/cl_casings gating). EjectCasing currently routes casings through EmitByEffectInfoName("casing_bullet"/"casing_shell") carrying the eject velocity as a stopgap so the request reaches the sink; the client-side EffectSystem.SpawnCasing consumer must be wired to that request (game/client/EffectSystem.cs SpawnCasing has no live feed).
- Register the casing/melee assets the seam references but Common doesn't yet have: an EFFECT for casings (or keep the effectinfo-name fallback) and the SND_SHOTGUN_MELEE / SND_BRASS_RANDOM / SND_CASINGS_RANDOM sound groups (common/effects/qc/casings.qc) — sound registry files, owned by FX/sounds units, not this seam.
- Wave-2 weapon files (Machinegun/Rifle/Shotgun/OkMachinegun/OkHmg/OkShotgun .cs) should call the new WeaponFiring.BulletImpactFx (replacing their inline EffectEmitter.Emit impact), EjectCasing (per shot), and pass tracerEffect to FireBullet (Rifle: "RIFLE"/"RIFLE_WEAK"); Shotgun should call MeleeWoosh from its secondary melee. These are Wave-2 own-file edits, intentionally not done here.
- src/VortexArena.Common/Gameplay/Player/SpawnSystem.cs (W1-startitems owner): call MutatorHooks.FireSetWeaponArena(arena) where the spawn loadout resolves the weapon arena, and use its return as the effective arena (mutators rewrite to 'off'/'most'/'all'). This is the missing SetWeaponArena .Call site — unblocks Mayhem/TeamMayhem/ClanArena/LMS/instagib/overkill/melee/nix arena loadouts.
- src/VortexArena.Common/Gameplay/Player/SpawnSystem.cs (W1-startitems owner): before granting random start weapons, call MutatorHooks.FireForbidRandomStartWeapons(player) and skip the grant if it returns true (instagib/overkill/melee_only/nix return true). This is the missing ForbidRandomStartWeapons .Call site.
- Alpha-net seam owner (NetEntity.cs / world-init): at worldspawn call MutatorHooks.FireSetDefaultAlpha() and seed default_player_alpha/default_weapon_alpha + the per-entity Alpha channel from the returned tuple; spawn/death/weapon-exit should read those defaults instead of hardcoding Alpha=1f (SpawnSystem.cs:524, DamageSystem.cs:581). This revives the dead SetDefaultAlpha chain for Cloaked + RunningGuns.
- Vehicle-framework seam owner (Vehicles/): at the end of vehicle initialise call MutatorHooks.FireVehicleInit(vehicle) and abort init if true; in the vehicle touch handler call MutatorHooks.FireVehicleTouch(vehicle, toucher) and suppress the touch/enter if true. (VehicleEnter/VehicleExit already have live .Call sites in VehicleBoarding.cs.)
- MutatorHooks.cs (owned by W1-mutator-hooks): add a `public static readonly HookChain<PlayerUseKeyArgs> PlayerUseKey` hook (port of MUTATOR_CALLHOOK(PlayerUseKey)) carrying the acting player entity; this is the chain CTF/Keepaway/TeamKeepaway/Nexball/KeyHunt subscribe to for the voluntary throw/pass/request-pass and ball/key drop.
- VehicleBoarding.UseKey (VehicleBoarding.cs, owned by another agent): after the vehicle enter/exit half, fire MutatorHooks.PlayerUseKey.Call(player) at the tail, mirroring client.qc:2666 `MUTATOR_CALLHOOK(PlayerUseKey, this)`. Without this the impulse-21 route reaches UseKey but the flag/ball/objective drop handlers never run.
- DispatchImpulse (Commands.cs / GameWorld.cs, owned by W1-round-handler): add the round-not-started gate — block weapon_drop(17)/weapon_reload(20)/use(21) while round_handler IsActive && !IsRoundStarted (impulse.qc:383-395, CA/Freezetag pre-round warmup). RoundHandler.IsRoundStarted exists but is an instance member with no static accessor reachable from WeaponImpulses, so this gate belongs in the dispatcher that already holds the active RoundHandler. Currently only intermission/timeout are gated.
- Wave-2 CTF/Keepaway/TeamKeepaway/Nexball/KeyHunt files: subscribe their existing ThrowFlag/PassFlag/RequestPass and ball/key-drop handlers to the new MutatorHooks.PlayerUseKey chain (own-file work per Wave 2).
- GameWorld.cs (round-drive seam, owned by W1-round-handler agent): once the map is loaded and the match goes live, call BallEntity.SpawnForGametype for the procedural-ball modes. Keepaway/TeamKeepaway have NO ball map-entity (QC spawned it from ka_Handler/ka_SpawnBalls), so the existing map-entity WireObjectiveSpawns switch never fires for them — add a host-side call e.g. `ball.world` (Keepaway) -> `var e = BallEntity.SpawnForGametype(BallKind.KeepawayBall, homeOrigin, cfg); ka.AdoptBall(e);`. Nexball's ball DOES come from a map entity (nexball_basketball/football in WireObjectiveSpawns GameWorld.cs:1505); route that through BallEntity.SpawnForGametype(BallKind.NexballBasketball/Football, e.Origin, cfg) instead of the gametype's own SpawnBall so it gets the lifecycle think.
- Wave-2 Keepaway.cs: replace the dead Keepaway.SpawnBall body with BallEntity.SpawnForGametype(BallKind.KeepawayBall, origin, cfg) where cfg.Touch = its BallTouchEntity; honor BallEntity.IsRecaptureLocked in that touch; drive carried-ball orbit each Tick via BallEntity.CarryOrbit; on drop use BallEntity.DropFromCarrier; wire the missing use-key/disconnect/observe drop triggers (PlayerUseKey / MakePlayerObserver). Fix the two wrong fallback defaults (DefaultScoreBcKill/KillAc 0 -> 1).
- Wave-2 TeamKeepaway.cs: same as Keepaway — route SpawnBall through BallEntity.SpawnForGametype(BallKind.KeepawayBall,...), use DropFromCarrier / IsRecaptureLocked; fix DefaultScoreKillAc/BcKill 0 -> 1.
- Wave-2 Nexball.cs: route SpawnBall through BallEntity.SpawnForGametype(BallKind.NexballBasketball/Football, ...); use the BallConfig.Think hook for Nexball's 4-step ResetBall glide state machine (the framework's GlideHome is the simple baseline) and delay_start/delay_idle timers; add the BallStealer weapon, football kick, carry per-frame follow (separate weapon/PlayerPreThink seams).
- Bot FindBall (BotObjectiveRoles.cs:277) queries classname 'keepaway_ball' but BallEntity spawns 'keepawayball' (the BallConfig default for BallKind.KeepawayBall). Reconcile the bot's query string to 'keepawayball'.
- Entity has no Scale / glow_color / glow_trail / pushable / damagedbycontents fields (Entity.cs + partials). BallConfig.Pushable and the carried-ball scale (12/16) + glow trail are recorded in config/comments but not applied to the edict — add those Entity fields (owned by Framework) if the carried-ball shrink or pushable physics are needed for parity. RandomMapLocation uses a spawnpoint-sampling fallback, not QC's true MoveToRandomMapLocation world-bounds sampler (no headless world-bounds sampler exists yet).
- src/VortexArena.Server/GameWorld.cs (owned by another agent): set VehicleCommon.GameStopped (and the turret equivalent) from the match/round loop so vehicles park on warmup/match-end/intermission — currently it falls back to reading the g_game_stopped cvar mirror. (Out of my file scope; informational — the fallback works.)
- Framework constant drift noted in turret-framework.md (NOT fixed here, would require touching TurretAI.RunCombat which I may edit but is out of the damage-seam scope): g_turrets_targetscan_maxdelay is hardcoded 0.6f vs Base 1.0 — should read the cvar in RunCombat. Flagged for the Wave-2/cleanup pass.
- Vehicle VehicleInit / VehicleTouch mutator hooks remain defined-but-never-Called (MutatorHooks.cs is owned by another Wave-1 agent). Wiring their .Call sites needs the mutator-hooks seam owner; the vehicle framework has no place to dispatch them without those hooks live.
- Shared vehicles_heal (event_heal) helper + crush/impact/painframe and the vehicle/turret client presentation + TNSF networking remain NOT IMPLEMENTED per the specs — these are larger structural gaps beyond the event-damage routing seam and were intentionally left for dedicated follow-up (no single shared-base one-liner).
- Promote the map keys onto the new fields in the field binders (files I don't own): in src/VortexArena.Common/Gameplay/MapObjects/MapObjectFieldsExtra.cs (Apply) AND/OR src/VortexArena.Server/GameWorld.cs (ApplyDictFields, ~line 2266 near noise/noise1) add: fields 'sounds' -> e.Sounds (int), 'sound1' -> e.Sound1, 'sound2' -> e.Sound2, 'platmovetype' -> e.Platmovetype. Without this the soundpack helpers + SetPlatMoveType have no map input. (mapobject-func/-movers-platforms shards: 'sounds key not promoted by ApplyDictFields/MapObjectFieldsExtra'.)
- Wire MapMover.CenterprintHandler to the real networked centerprint channel from the host (GameWorld / the server net layer — files I don't own) so map-object .message text reaches clients. Until then Centerprint() no-ops (parity gap: 'centerprint TEXT dropped, rendered client-side in CSQC').
- Wave-2: call MapMover.SetPlatMoveType(this, this.Platmovetype) from the path_corner spawnfunc (MovingBrushes.PathCornerSetup), func_train setup (MovingBrushes.TrainSetup), and func_plat (Platforms.PlatSetup) — these are the QC set_platmovetype call sites (corner.qc:48, train.qc:266/323).
- Wave-2: call the soundpack helpers from their spawnfuncs — Doors.DoorSetup -> ApplyDoorSounds, Platforms.PlatSetup -> ApplyPlatSounds, TargetUtilities.DoorSecretSetup -> ApplySecretSounds (each currently hardcodes the default pack).
- Wave-2: replace ad-hoc message/audible-only handling in jumppads (Jumppads.cs), trigger_secret (Triggers.cs SUB_UseTargets path), and keylock 'Unlocked!' with MapMover.Centerprint(actor, message) calls.
- Consider (separate decision, NOT done here to avoid breaking matched parity rows): the mapobject-misc shard flags the SUB_CalcMove ease DEFAULT as wrong — port hardcodes PlatMoveStart/End = 1 (linear) but Base defaults 0/0 (smoothstep). Left as 1/1 so existing func/plat/movers 'match:true' rows stay valid; flipping the default + always running set_platmovetype would S-curve every long mover. Owner of MapObjectsCommon should revisit with Wave-2 mover authors.
- NetEntity/net-server seam (owned by another Wave-1 agent): must add the per-entity Alpha network field AND populate the CLIENT-side Entity.Alpha when applying the snapshot delta, or PlayerModel.ApplyAlpha will only ever see the default 1f. PlayerModel reads Common.Framework.Entity.Alpha (defined in DamageEntityState.cs:123, default 1f) each frame in Pose().
- Round-drive/world-init seam (GameWorld + CloakedMutator): seed default_player_alpha at worldspawn by firing MutatorHooks.SetDefaultAlpha and applying its result so PutClientInServer/death/vehicle-exit set Entity.Alpha to default_player_alpha (0.25 under Cloaked) instead of the current hardcoded SpawnSystem.cs:524 / DamageSystem.cs:581 = 1f. Out of scope for this seam (those files are owned by other agents).
- PowerupsMutator.cs:170 restores a hardcoded 1f on Invisibility lapse instead of default_player_alpha, so under Cloaked the compose-back is wrong (tracked as mutator-cloaked.compose.invisibility_powerup). Not this seam's file.
- game/client/ClientWorld.cs (owned by another agent): in DriveCsqcModelHooks, build a per-player CsqcModelAppearance.ForcedAppearance (from AppearanceContext / gametype role / powerup state) and pass it to CsqcModelEffects.Apply(...) instead of the hardcoded modelFlags:0 — and call CsqcModelEffects.ApplyForcedGlowmod(meshes, forced) right after ModelTint.ApplyAppearance, invalidating st.Effects.Tint.Valid when a forced glowmod is set (mirrors the frozen-tint colormod pattern). This is what makes the new seam live; Wave-3 gametype/mutator code supplies the ForcedAppearance.
- game/net/NetGame.cs (owned by another agent): if Wave-3 wants server-driven role glow/colormod per player, add a provider on ClientWorld (analogous to AppearanceProvider/ForcedModelResolver) that yields a per-entity ForcedAppearance, and wire it in NetGame so the effects pass can consume it. Not required for the local-derived (powerup/jetpack) path.
- src/VortexArena.Common/Framework/Entity.cs (owned by another agent / networking seam): to make MF_* trails, per-entity colormod/glowmod and scale truly networked (the cl-csqcmodel.networking.csqcmodel_contract umbrella gap), add ModelFlags/TrailEffect/Colormod/Glowmod/Scale fields. The new ForcedAppearance is the client-side stopgap until then.
- Wave-2 weapon files (Mortar.cs, Seeker.cs, Minelayer.cs, Devastator.cs, Hagar.cs, Electro.cs, Crylink.cs, Arc.cs) must CALL Projectiles.MakeShootable(projectile, exception) after spawning each shootable projectile (pass exception=1 for the combo-able ones — electro orb, hagar, ML mine — so they pass under stock g_projectiles_damage -2). The shim + ProjectileDamage callbacks exist but no weapon installs the GtEventDamage hook yet.
- Wave-2 net-client stage (NetGame.cs) must decode the new GametypeStatusBlock.Decoded fields (ObjectiveStatus for CTF, DominationPps[5], CarrierNetId) and route them to ModIconsPanel modes (Ctf/Domination/KA_CARRYING) — explicitly out of scope for this seam.
- Wave-2 client render (PlayerModel.cs / CsqcModel pipeline) must read NetEntityState.Alpha (0=opaque, else byte/255) to render transparent players/items for Cloaked / Running Guns / Invisibility.
- Wave-2 Ctf.cs must add stalemate detection to set the reserved CTF_STALEMATE bit (PackCtfStatus leaves it clear today; UpdateCaptureShields already populates Player.GtCaptureShielded which the seam reads for CTF_SHIELDED).
- Wave-2 Domination.cs may refine pps to the exact set_dom_state per-point .frags/.wait accumulation (the seam currently computes pps from the global g_domination_point_amt/_rate × owned-point count, which matches stock defaults but ignores per-point overrides).
- ClientEntityView.cs (game/net/ClientEntityView.cs, owned by another agent): DriveEntity does NOT copy NetEntityState.Alpha onto the proxy Entity, so the W1-alpha-net render-transparency channel is decoded on the wire but dropped before render. Add `e.Alpha = s.Alpha == 0 ? 1f : s.Alpha / 255f;` (0 = opaque) and have the render path consume it. Requires Entity (src/VortexArena.Common/Framework/Entity.cs) to gain an Alpha/RenderAlpha field first.
- Entity.cs (src/VortexArena.Common/Framework/Entity.cs): add a render-Alpha field (default 1.0 = opaque) so the decoded NetEntityState.Alpha can be carried from the proxy to PlayerModel/ClientWorld rendering. Part of the W1-alpha-net seam.
- NetEntityState proxy fill / ClientEntityView: surface the networked render alpha so PlayerModel.cs (W1-alpha-net owner) can apply transparency for Cloaked/Running Guns/Invisibility/death-fade.
- Ctf.cs / Domination.cs / Keepaway.cs (Wave 2): the server must actually emit GametypeStatusBlock.Capture for these gametypes (the Kind cases exist in the wire + are now dispatched client-side, but the producer state — Ctf.Flags, Domination control-point ownership, Keepaway ball carrier — is Wave-2 work).