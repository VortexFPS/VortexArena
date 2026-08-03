using System.Linq;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Server;
using VortexArena.Server.Bot;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// The gates between "the bot has an enemy" and "a projectile leaves the muzzle". Each test here pins one
/// difference the 2026-08-03 parity audit found on that chain; see planning/bot-ai-parity-2026-08-03.md
/// section "Why bots don't fire as often as they could".
/// </summary>
[Collection("GlobalState")]
public class BotFireRateTests
{
    public BotFireRateTests()
    {
        Api.Services = new EngineServices(new CollisionWorld());
        GameRegistries.Reset();
        StatusEffectsCatalog.RegisterAll();
        GameRegistries.Bootstrap(); // discovers the [Weapon] registry
        Cvars.RegisterDefaults();
    }

    private static Player BotWith(params string[] weapons)
    {
        var p = new Player { IsBot = true, Health = 100f, DeadState = DeadFlag.No };
        p.Index = -1000;
        foreach (string w in weapons)
        {
            p.OwnedWeapons.Add(w);
            if (Weapons.ByName(w) is { } wep) p.OwnedWeaponSet.Add(wep);
        }
        return p;
    }

    // ---------------------------------------------------------------------------------------------
    // F4 — shot_accurate is a per-weapon literal, not an inference from hitscan-ness
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// QC wr_aim's shot_accurate literals: the hitscan Shotgun fires on a WIDE cone (false) while the
    /// projectile Blaster/Crylink/Electro/Hagar/HLAC/Mortar/Porto all fire on a tight one (true). Inferring
    /// this from TypeHitscan gets both families backwards.
    /// </summary>
    [Theory]
    // shot_accurate = false (aim.qc f = 1.6) — shotgun.qc:270,272; okshotgun.qc:7,9; minelayer.qc:385;
    // fireball.qc:350,359; seeker.qc:536,538
    [InlineData("shotgun", false)]
    [InlineData("okshotgun", false)]
    [InlineData("minelayer", false)]
    [InlineData("fireball", false)]
    [InlineData("seeker", false)]
    // shot_accurate = true (f = 1) — blaster.qc:98; crylink.qc:488,490; electro.qc:611; hagar.qc:394;
    // hlac.qc:158; mortar.qc:269,278; porto.qc:347; vortex.qc:182; machinegun.qc:272; rifle.qc:104
    [InlineData("blaster", true)]
    [InlineData("crylink", true)]
    [InlineData("electro", true)]
    [InlineData("hagar", true)]
    [InlineData("hlac", true)]
    [InlineData("mortar", true)]
    [InlineData("porto", true)]
    [InlineData("vortex", true)]
    [InlineData("machinegun", true)]
    [InlineData("rifle", true)]
    public void ShotAccurateMatchesTheWeaponsQcLiteral(string netName, bool expected)
    {
        Weapon? w = Weapons.ByName(netName);
        Assert.NotNull(w);
        Assert.Equal(expected, w!.BotAimAccurate());
    }

    /// <summary>
    /// The consequence: at the shipped skill 8 the Shotgun's fire cone must be the WIDER of the two, because
    /// f = 1.6 + bound(0, (10 - skill) * 0.3, 3) = 2.2 against the Blaster's 1 + 0.6 = 1.6.
    /// </summary>
    [Fact]
    public void ShotgunFireConeIsWiderThanBlasterAtStockSkill()
    {
        var aim = new BotAim(seed: 1) { ShotOrigin = Vector3.Zero };
        var target = new Vector3(500f, 0f, 0f);

        float shotgun = aim.MaxFireDeviation(target, 8f, Weapons.ByName("shotgun")!.BotAimAccurate() ?? true);
        float blaster = aim.MaxFireDeviation(target, 8f, Weapons.ByName("blaster")!.BotAimAccurate() ?? true);

        Assert.True(shotgun > blaster,
            $"shotgun cone {shotgun:0.00}deg must exceed blaster {blaster:0.00}deg (QC f = 2.2 vs 1.6)");
        Assert.Equal(2.2f / 1.6f, shotgun / blaster, 3);
    }

    // ---------------------------------------------------------------------------------------------
    // F1 — weapon selection checks ammo, not just ownership
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// QC havocbot_chooseweapon passes andammo = true to client_hasweapon, so a dry weapon is SKIPPED and the
    /// scan falls through to the next entry in the priority list. Vortex heads all three shipped bands and
    /// cells are the first ammo type a bot exhausts, so ownership-only selection pinned bots to an empty gun.
    /// </summary>
    [Fact]
    public void WeaponSelectionSkipsAnOwnedButDryWeapon()
    {
        Cvars.Set("bot_ai_custom_weapon_priority_mid", "vortex shotgun");
        var bot = BotWith("vortex", "shotgun");
        bot.SetResource(ResourceType.Cells, 0f);    // vortex is dry
        bot.SetResource(ResourceType.Shells, 30f);  // shotgun has ammo

        var brain = new BotBrain(bot, network: null, skill: 8f, seed: 1);
        brain.ChooseWeapon(enemy: null);

        Assert.Equal("shotgun", brain.ChosenWeapon?.NetName);
    }

    /// <summary>With ammo for the top-priority weapon, the bot still takes it — the gate must not over-reject.</summary>
    [Fact]
    public void WeaponSelectionTakesTheTopPriorityWeaponWhenItHasAmmo()
    {
        Cvars.Set("bot_ai_custom_weapon_priority_mid", "vortex shotgun");
        var bot = BotWith("vortex", "shotgun");
        bot.SetResource(ResourceType.Cells, 20f);
        bot.SetResource(ResourceType.Shells, 30f);

        var brain = new BotBrain(bot, network: null, skill: 8f, seed: 1);
        brain.ChooseWeapon(enemy: null);

        Assert.Equal("vortex", brain.ChosenWeapon?.NetName);
    }

    // ---------------------------------------------------------------------------------------------
    // F2 — the combo rule is a SKIP, not a hold
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// QC havocbot.qc:1573/1587/1600: `if ((m_weapon.m_id == w && combo) || checkreload) continue;` — when a
    /// combo is in play the priority scan skips the weapon just fired, so the bot switches to a second gun and
    /// shoots with THAT during the first's refire. The port used to return early and pin the bot to the weapon
    /// on cooldown, which is the opposite behaviour and a direct cause of the reported low fire rate.
    /// </summary>
    [Fact]
    public void ComboSkipsTheJustFiredWeaponInsteadOfPinningToIt()
    {
        // Set every band so the test does not depend on which one the enemy distance selects.
        foreach (string band in new[] { "far", "mid", "close" })
            Cvars.Set($"bot_ai_custom_weapon_priority_{band}", "mortar shotgun");
        Cvars.Set("bot_ai_weapon_combo", "1");
        var bot = BotWith("mortar", "shotgun");
        bot.SetResource(ResourceType.Rockets, 30f);
        bot.SetResource(ResourceType.Shells, 30f);

        var brain = new BotBrain(bot, network: null, skill: 8f, seed: 1);
        brain.ChooseWeapon(enemy: null);
        Assert.Equal("mortar", brain.ChosenWeapon?.NetName);

        // Simulate: the bot fired the mortar, and its refire runs well past the combo threshold.
        var enemy = new Player { Health = 100f, DeadState = DeadFlag.No, Origin = new Vector3(400f, 0f, 0f) };
        brain.LastFiredWeapon = brain.ChosenWeapon;
        bot.WeaponState(new WeaponSlot(0)).AttackFinished = Api.Clock.Time + 100f;

        brain.ChooseWeapon(enemy);

        Assert.Equal("shotgun", brain.ChosenWeapon?.NetName);
    }

    /// <summary>Without a preceding shot from the held weapon there is no combo, so the range pick stands.</summary>
    [Fact]
    public void NoComboWhenTheHeldWeaponWasNotTheOneJustFired()
    {
        // Set every band so the test does not depend on which one the enemy distance selects.
        foreach (string band in new[] { "far", "mid", "close" })
            Cvars.Set($"bot_ai_custom_weapon_priority_{band}", "mortar shotgun");
        Cvars.Set("bot_ai_weapon_combo", "1");
        var bot = BotWith("mortar", "shotgun");
        bot.SetResource(ResourceType.Rockets, 30f);
        bot.SetResource(ResourceType.Shells, 30f);

        var brain = new BotBrain(bot, network: null, skill: 8f, seed: 1);
        var enemy = new Player { Health = 100f, DeadState = DeadFlag.No, Origin = new Vector3(400f, 0f, 0f) };
        bot.WeaponState(new WeaponSlot(0)).AttackFinished = Api.Clock.Time + 100f;

        brain.ChooseWeapon(enemy);

        Assert.Equal("mortar", brain.ChosenWeapon?.NetName);
    }
}
