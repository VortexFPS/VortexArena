using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common;

/// <summary>
/// The composition root: wires the gameplay systems onto their ambient facades and builds the registries.
/// The host (client/server) calls <see cref="Boot"/> once at startup with the engine-services
/// implementation. Keeping this in <c>XonoticGodot.Common</c> means the headless server boots without Godot.
/// </summary>
public static class GameInit
{
    /// <summary>
    /// Install the gameplay systems (movement, damage, …) onto their facades. Filled in as each system
    /// lands; safe to call multiple times.
    /// </summary>
    public static void InstallGameplaySystems()
    {
        XonoticGodot.Common.Physics.Movement.System = new XonoticGodot.Common.Physics.PlayerPhysics();
        XonoticGodot.Common.Gameplay.Damage.Combat.System = new XonoticGodot.Common.Gameplay.Damage.DamageSystem();
        XonoticGodot.Common.Gameplay.MapObjectsRegistry.RegisterAll();  // BSP entity spawnfuncs (func_door, trigger_*, …)
        XonoticGodot.Common.Gameplay.Effects.RegisterAll();             // named particle effects
        XonoticGodot.Common.Gameplay.Notifications.RegisterAll();       // kill-feed / announcer / centerprint
        XonoticGodot.Common.Gameplay.Sounds.RegisterAll();              // sound catalog
        XonoticGodot.Common.Gameplay.Minigames.RegisterAll();           // in-game minigames
        XonoticGodot.Common.Gameplay.StatusEffectsCatalog.RegisterAll(); // frozen/burning/buffs
        XonoticGodot.Common.Gameplay.Scoring.GameScores.RegisterAll();   // SP_* networked score columns
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
