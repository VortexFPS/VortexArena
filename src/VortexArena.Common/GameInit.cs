using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;

namespace VortexArena.Common;

/// <summary>
/// The composition root: wires the gameplay systems onto their ambient facades and builds the registries.
/// The host (client/server) calls <see cref="Boot"/> once at startup with the engine-services
/// implementation. Keeping this in <c>VortexArena.Common</c> means the headless server boots without Godot.
/// </summary>
public static class GameInit
{
    /// <summary>
    /// Install the gameplay systems (movement, damage, …) onto their facades. Filled in as each system
    /// lands; safe to call multiple times.
    /// </summary>
    public static void InstallGameplaySystems()
    {
        VortexArena.Common.Physics.Movement.System = new VortexArena.Common.Physics.PlayerPhysics();
        VortexArena.Common.Gameplay.Damage.Combat.System = new VortexArena.Common.Gameplay.Damage.DamageSystem();
        VortexArena.Common.Gameplay.MapObjectsRegistry.RegisterAll();  // BSP entity spawnfuncs (func_door, trigger_*, …)
        VortexArena.Common.Gameplay.Effects.RegisterAll();             // named particle effects
        VortexArena.Common.Gameplay.Notifications.RegisterAll();       // kill-feed / announcer / centerprint
        VortexArena.Common.Gameplay.Sounds.RegisterAll();              // sound catalog
        VortexArena.Common.Gameplay.Minigames.RegisterAll();           // in-game minigames
        VortexArena.Common.Gameplay.StatusEffectsCatalog.RegisterAll(); // frozen/burning/buffs
        VortexArena.Common.Gameplay.Scoring.GameScores.RegisterAll();   // SP_* networked score columns
        // (more systems — gametype activation, etc. — wired as they land)
    }

    /// <summary>Full boot: install the engine facade, build the catalogs, install gameplay systems.</summary>
    public static void Boot(IEngineServices services)
    {
        Api.Services = services;
        GameRegistries.Bootstrap();   // source-generated registration tables (ADR-0003), then ConfigureAll
        InstallGameplaySystems();     // MapObjectsRegistry.RegisterAll etc. — must stay AFTER Bootstrap
    }
}
