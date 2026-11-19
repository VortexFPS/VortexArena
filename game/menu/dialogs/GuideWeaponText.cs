using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using VortexArena.Common.Gameplay;
using VortexArena.Game.Console;

namespace VortexArena.Game.Menu;

internal static partial class GuideCatalogText
{
    // Weapon describe() callbacks use the shipped gettext paragraph keys.
    private static string WeaponProse(Weapon weapon) => weapon.NetName switch
    {
        "arc" => Text(
            Paragraph("The %s fires a continuous beam of electricity, steadily dealing damage to any enemies that cross its path.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Localization.Tr("The secondary fire rapidly shoots electric bolts forward, exploding on impact and dealing some splash damage."),
            Paragraph("It consumes %s ammo, steadily churning through your supply to maintain the stream.",
                ItemName("cells", "Cells")),
            Paragraph("The %s is quite a versatile weapon, however it is more effective at close to medium ranges, since the stream is not instantaneous.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color))),
        "blaster" => Text(
            Paragraph("The %s is one of the two main default weapons, firing a relatively weak but high speed laser, dealing some splash damage but importantly a lot of knockback.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Localization.Tr("It has no secondary fire, instead switching to the previously selected weapon."),
            Localization.Tr("It doesn't require ammo, meaning it is a great choice if you are running low on ammo and need to preserve some."),
            Paragraph("The %s is always available so ends up being used a lot when players spawn in, but it's difficult to master when used over a medium to long range. " +
                           "Unlike other weapons, the %s can hit other projectiles directly.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color), GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Paragraph("One of the most common uses of the %s is \"laser jumping,\" where you can gain height by aiming down, jumping, then firing to boost yourself up. " +
                           "Because it does a lot of knockback, another common usage is alternating between a high damage weapon and the %s to throw the enemy's aim off.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color), GuidePages.Colored(weapon.DisplayName, weapon.Color))),
        "crylink" => Text(
            Paragraph("The %s fires bursts of laser-like projectiles, spreading out as they travel away and deflecting off walls. " +
                           "If the primary fire is held, when it's released the projectiles will converge before spreading out again, " +
                           "which can be utilized to deal more damage to an enemy by making the projectiles converge on them.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Localization.Tr("Projectiles deal damage on collision but also when they bounce, so with good positioning they can sometimes deal double damage."),
            Localization.Tr("The secondary fire shoots the projectiles together in a tight group, so it is often the better option in situations where the enemy is an easy target to hit."),
            Paragraph("It consumes %s ammo for each projectile, which is shared by several weapons.",
                ItemName("cells", "Cells")),
            Paragraph("Close to medium range is the ideal time to use the %s, although the secondary fire can be useful for long range combat sometimes.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Paragraph("The %s deals knockback in a unique way, pulling the player from their center to the point of impact. " +
                           "This makes it one of the best weapons to slow someone down if you are chasing them, particularly with the secondary fire. " +
                           "Another common use of the %s is \"crylink running,\" where you partially angle down and use the secondary fire to pull yourself forwards, in order to gain speed.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color), GuidePages.Colored(weapon.DisplayName, weapon.Color))),
        "devastator" => Text(
            Paragraph("The %s launches a remote controlled rocket, dealing significant damage when it explodes on impact. " +
                           "If the primary fire is held, the rocket can be guided by the user's aim, allowing steering it towards enemies.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Localization.Tr("The secondary fire can be used to immediately detonate rockets, allowing dealing damage to enemies even if the rocket barely missed colliding with them."),
            Paragraph("It consumes a bunch of %s ammo for each rocket, which can end up being depleted quickly, so often players alternate with another weapon like %s.",
                ItemName("rockets", "Rockets"), WeaponName("vortex", "Vortex")),
            Paragraph("Due to its high damage output, the %s is one of the most commonly used weapons. " +
                           "It can be used in almost any scenario, working best in medium range combat. " +
                           "In close range combat, the large splash radius often results in rockets damaging both you and your enemy.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Paragraph("Due to the ability to remotely detonate rockets, a common usage is \"rocket flying,\" where you fire a rocket and immediately detonate it to boost yourself while mid-air, " +
                           "much more effective with the %s mutator enabled.",
                White("Rocket Flying"))),
        "electro" => Text(
            Paragraph("The %s shoots electric balls forwards, dealing some splash damage when they burst on impact.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Paragraph("The secondary fire launches orbs that are influenced by gravity, " +
                           "so they can be laid around the map at high traffic locations (like at %s flag bases) to damage enemies that walk by. " +
                           "The orbs burst after some time, and can be forced to burst in a \"combo\" if a primary fire ball bursts near them.",
                ModeName("ctf")),
            Paragraph("It consumes some %s ammo for each ball / orb.",
                ItemName("cells", "Cells")),
            Paragraph("The %s is one of the best spam weapons to use in crowded areas, since combos can deal tons of damage, if the enemy is close enough. " +
                           "Since the primary fire doesn't travel particularly fast, the %s is not useful in many other situations.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color), GuidePages.Colored(weapon.DisplayName, weapon.Color))),
        "fireball" => Text(
            Paragraph("The %s supercharges then fires a massive fireball in a straight line, dealing heaps of splash damage over a large radius on impact.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Localization.Tr("The secondary fire launches flaming balls that set nearby players alight."),
            Localization.Tr("It is a superweapon, so isn't often found in game."),
            Localization.Tr("It doesn't require ammo, but it is destroyed after some time."),
            Paragraph("Since the %s takes a moment to charge and the fireball travels slowly, using it effectively may be difficult, " +
                           "but if done properly it can deal a ton of damage.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color))),
        "hagar" => Text(
            Paragraph("The %s rapidly fires small propelled rockets forwards, dealing some splash damage on impact.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Localization.Tr("When the secondary fire is held, multiple rockets are loaded up, and they're shot at the same time when released. " +
                           "These rockets can't be held forever, so it will fire itself after some time (after a warning beep) if the secondary fire isn't released."),
            Paragraph("It consumes %s ammo for each rocket.",
                ItemName("rockets", "Rockets")),
            Paragraph("The %s works best over close to medium ranges, since it's hard to land hits at a long distance. " +
                           "A common usage is fully loading the secondary fire before turning a corner, so you can surprise any enemies around the corner with a bunch of rockets to the face.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color))),
        "hlac" => Text(
            Paragraph("The %s (or HLAC for short) fires lasers in quick succession. " +
                           "The projectiles it fires are similar to those of the %s.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color), WeaponName("blaster", "Blaster")),
            Localization.Tr("The secondary fire shoots a burst of multiple randomly-scattered lasers at once."),
            Paragraph("Unlike the %s, it consumes %s ammo for each laser shot, meaning that it cannot be used infinitely.",
                WeaponName("blaster", "Blaster"), ItemName("cells", "Cells")),
            Paragraph("The %s works best in close ranges, but the primary fire is also useful in medium ranges.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Paragraph("A unique aspect of the %s is that the longer the primary fire is held, the more that the lasers will start to spread out. " +
                           "This means releasing primary fire every now and then is important to restore accuracy. " +
                           "Also, the %s has less spread when used while crouching.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color), GuidePages.Colored(weapon.DisplayName, weapon.Color))),
        "hook" => Text(
            Paragraph("The %s is a unique weapon, firing a chain forwards which pulls you in once it latches onto something.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Localization.Tr("The secondary fire usually drops a gravity bomb that affects enemies, also releasing light and smoke sort of like a flashbang."),
            Paragraph("It usually requires %s ammo to work, consuming it both when initially firing the hook, and after a couple seconds as it reels you in.",
                ItemName("fuel", "Fuel")),
            Paragraph("The %s allows reaching previously unreachable places on maps and zooming around the map at high speeds, " +
                           "making both surprise ambushes and miraculous escapes possible.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Paragraph("It isn't available very often on maps, unless the %s mutator is active, in which all players have it on their offhand, used with %s.",
                White("Hook"), GuidePages.Key("hook"))),
        "machinegun" => Text(
            Paragraph("The %s quickly fires bullets with a small degree of spread.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Localization.Tr("The secondary fire fires a quick burst of bullets faster than the primary fire and with no spread, but there's a short delay until it can be used again."),
            Paragraph("It consumes %s ammo for each bullet shot, until the whole magazine is emptied.",
                ItemName("bullets", "Bullets")),
            Paragraph("The %s can be used in a lot of situations, and it works particularly well at long ranges since the bullets pierce the sky instantaneously. " +
                           "Since the secondary fire has no spread, it's the better option when firing over a long range.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color))),
        "minelayer" => Text(
            Paragraph("The %s places mines on the ground when fired, which detonate and damage enemies if stepped on. " +
                           "Only a couple mines can be placed at any time, and after some time they will self-detonate.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Localization.Tr("Uniquely, the mines explode sooner the closer an enemy comes to them. " +
                           "The secondary fire can be used to detonate all mines placed by the primary fire."),
            Paragraph("It consumes %s ammo for each mine laid.",
                ItemName("rockets", "Rockets")),
            Paragraph("The mines are not launched very far before they hit the ground, so the %s isn't very effective at medium to long ranges. " +
                           "It is often used to protect important areas of the map such as the flag in %s, control points in %s, or checkpoints in %s.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color), ModeName("ctf"), ModeName("ons"), ModeName("as"))),
        "mortar" => Text(
            Paragraph("The %s launches a grenade that explodes immediately on impact, dealing a medium amount of splash damage.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Localization.Tr("The secondary fire shoots a similar grenade that explodes shortly after bouncing."),
            Paragraph("It consumes %s ammo for every grenade launched.",
                ItemName("rockets", "Rockets")),
            Paragraph("The %s works best at close to medium ranges, but it's quite tricky to hit an enemy if they're airborne. " +
                           "Since the secondary fire grenade bounces before exploding, it can be bounced against walls to damage enemies lurking around a corner.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color))),
        "porto" => Text(
            Paragraph("The %s is a unique gun that creates one-way portals between flat surfaces on the map, that immediately teleport players and projectiles.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Localization.Tr("The secondary fire is used to shoot the out-portal, while the primary fire shoots the in-portal."),
            Localization.Tr("It doesn't require ammo, but it is destroyed after some time."),
            Localization.Tr("The portals will close either after the player who shot them dies or after some time period."),
            Paragraph("The %s isn't often placed on maps, but if used well it can make for some interesting gameplay.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color))),
        "rifle" => Text(
            Paragraph("The %s fires bullets that traverse the map instantaneously and deal a significant chunk of damage on impact.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Localization.Tr("The secondary fire shoots a few less powerful bullets at once with a bit of scatter."),
            Paragraph("It consumes %s ammo for each bullet shot.",
                ItemName("bullets", "Bullets")),
            Paragraph("Unlike the %s, the secondary fire doesn't zoom, so %s needs to be used manually with the %s. " +
                           "Also, it needs to be reloaded after its magazine is emptied.",
                WeaponName("vortex", "Vortex"), GuidePages.Key("zoom"), GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Paragraph("Similar to the %s, the %s can be used at any range, but it stands out at long ranges.",
                WeaponName("vortex", "Vortex"), GuidePages.Colored(weapon.DisplayName, weapon.Color))),
        "seeker" => Text(
            Paragraph("The %s is a unique weapon, firing a \"tag\" which then launches a few homing missiles if it collides with a player. " +
                           "The homing isn't perfect, so sometimes the missiles can hit an object or a corner.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Localization.Tr("The secondary fire launches a rapid barrage of scattered explosives that travel only a short distance."),
            Paragraph("It consumes %s ammo, even when the tag doesn't land.",
                ItemName("rockets", "Rockets")),
            Paragraph("The %s primary fire deals quite a lot of damage when a tag lands, although it requires skill to aim effectively. " +
                           "The secondary fire is only useful in close range combat, and sometimes the explosives can damage yourself too.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color))),
        "shotgun" => Text(
            Paragraph("The %s is one of the two main default weapons, firing a single shotgun round which spreads into multiple pellets upon exiting the barrel.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Paragraph("The secondary fire swings the %s, slapping players close enough with the head of the weapon. " +
                           "Since the slap takes a moment to land, timing this well is difficult.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Paragraph("The primary fire consumes %s ammo, although if you spawn in with the %s you will already have some.",
                ItemName("shells", "Shells"), GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Paragraph("The %s damage drops off quickly as the range increases, so it is only useful for close combat or sometimes medium range combat.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color))),
        "tuba" => Text(
            Paragraph("The %s is a unique weapon that makes the ears of nearby enemies bleed by playing awful sounds, also slightly knocking them back.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Localization.Tr("The secondary fire works the same way, playing a higher pitch."),
            Localization.Tr("The only ammo it needs to operate is the breath from your lungs."),
            Paragraph("Since your enemies need to be close by to hear your rubbish music, the %s is only effective at very close ranges.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Paragraph("The pitch the %s plays depends on the movement keys pressed.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color))),
        "vaporizer" => Text(
            Paragraph("The %s is a unique weapon, firing a deadly beam of energy dealing a huge amount of damage. " +
                           "In %s, the beam has the ability to instantly kill enemies with a single shot, unless they have an %s.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color), White("InstaGib"), ItemName("extralife", "Extra life")),
            Paragraph("The secondary fire shoots a laser identical to that fired by the %s, with strong knockback.",
                WeaponName("blaster", "Blaster")),
            Paragraph("It is a superweapon, so isn't often found in game, except in %s where all players spawn with it.",
                White("InstaGib")),
            Paragraph("It consumes some %s ammo with each shot.",
                ItemName("cells", "Cells"))),
        "vortex" => Text(
            Paragraph("The %s fires harmful beams of energy that traverse the map instantaneously and deal a significant chunk of damage on impact.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Localization.Tr("The secondary fire zooms in when held, allowing for ease of aiming."),
            Paragraph("It consumes %s ammo for each shot.",
                ItemName("cells", "Cells")),
            Paragraph("Unlike the %s, the %s doesn't need to be reloaded manually, although you have to wait a couple seconds between shots. " +
                           "Uniquely, it can be fired slightly before it finishes completely reloading, albeit with the tradeoff of dealing slightly less damage.",
                WeaponName("rifle", "Rifle"), GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Paragraph("Similar to the %s, the %s can be used at any range, but it stands out at long ranges.",
                WeaponName("rifle", "Rifle"), GuidePages.Colored(weapon.DisplayName, weapon.Color))),
        "okhmg" => Text(
            Paragraph("The %s is a superweapon that rapidly fires harmful bullets with a small degree of spread.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Paragraph("Like with all %s weapons, the secondary fire shoots a laser which doesn't damage or push enemies, but can be used to push yourself around.",
                White("Overkill")),
            Localization.Tr("It is a superweapon found on some maps, meaning that it breaks down after some time."),
            Paragraph("The primary fire consumes %s ammo, although you spawn with an infinite amount of it in %s. " +
                           "It has a limited magazine size, so needs reloading after several shots.",
                ItemName("bullets", "Bullets"), White("Overkill")),
            Paragraph("The %s can be used in a lot of situations, and it works particularly well at long ranges since the bullets pierce the sky instantaneously.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Paragraph("Since its bullets deal a lot more damage than the %s bullets, it is often heavily contested when it spawns in.",
                WeaponName("okmachinegun", "Overkill MachineGun"))),
        "okmachinegun" => Text(
            Paragraph("The %s quickly fires bullets with a small degree of spread.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Paragraph("Like with all %s weapons, the secondary fire shoots a laser which doesn't damage or push enemies, but can be used to push yourself around.",
                White("Overkill")),
            Paragraph("The primary fire consumes %s ammo, although you spawn with an infinite amount of it in %s. " +
                           "It has a limited magazine size, so needs reloading after several shots.",
                ItemName("bullets", "Bullets"), White("Overkill")),
            Paragraph("The %s can be used in a lot of situations, and it works particularly well at long ranges since the bullets pierce the sky instantaneously.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color))),
        "oknex" => Text(
            Paragraph("The %s fires harmful beams of energy that traverse the map instantaneously and deal a significant chunk of damage on impact.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Paragraph("Like with all %s weapons, the secondary fire shoots a laser which doesn't damage or push enemies, but can be used to push yourself around.",
                White("Overkill")),
            Paragraph("The primary fire consumes %s ammo, although you spawn with an infinite amount of it in %s. " +
                           "It has a limited magazine size, so needs reloading after several shots.",
                ItemName("cells", "Cells"), White("Overkill")),
            Paragraph("Since it is the only %s weapon with no spread, the %s stands out at long ranges.",
                White("Overkill"), GuidePages.Colored(weapon.DisplayName, weapon.Color))),
        "okrpc" => Text(
            Paragraph("As the name suggests, the %s is a superweapon that fires a rocket propelled chainsaw which explodes on impact, dealing a lot of splash damage to any players close by.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Paragraph("Like with all %s weapons, the secondary fire shoots a laser which doesn't damage or push enemies, but can be used to push yourself around.",
                White("Overkill")),
            Localization.Tr("It is a superweapon found on some maps, meaning that it breaks down after some time."),
            Paragraph("The primary fire consumes %s ammo, although you spawn with an infinite amount of it in %s.",
                ItemName("rockets", "Rockets"), White("Overkill")),
            Paragraph("Since it is the only %s weapon which deals splash damage, the %s is a good choice of weapon for attacking groups of enemies.",
                White("Overkill"), GuidePages.Colored(weapon.DisplayName, weapon.Color))),
        "okshotgun" => Text(
            Paragraph("The %s fires a single shotgun round which spreads into multiple pellets upon exiting the barrel, dealing a deadly blow if up close.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color)),
            Paragraph("Like with all %s weapons, the secondary fire shoots a laser which doesn't damage or push enemies, but can be used to push yourself around.",
                White("Overkill")),
            Paragraph("The primary fire consumes %s ammo, although you spawn with an infinite amount of it in %s. " +
                           "It has a limited magazine size, so needs reloading after several shots.",
                ItemName("shells", "Shells"), White("Overkill")),
            Paragraph("The %s damage drops off quickly as the range increases, so it is only useful for close combat or sometimes medium range combat.",
                GuidePages.Colored(weapon.DisplayName, weapon.Color))),
        _ => weapon.GuideDescription is { Length: > 0 } text ? TranslateParagraphs(text) : "",
    };

    internal static string DescribeWeapon(Weapon weapon)
    {
        string prose = WeaponProse(weapon);
        if (WeaponOrder.ToXonoticRegistryId(weapon.NetName) == 0 || prose.Length == 0)
            return prose;

        string bindings = WeaponBindings(weapon);
        string damage = new WeaponDefaults(weapon.NetName).Describe();
        return damage.Length == 0 ? Text(prose, bindings) : Text(prose, bindings, damage);
    }

    private static string WeaponBindings(Weapon weapon)
    {
        bool showJoystick = MenuState.Cvars.GetFloat("joy_active") != 0;
        var keys = QcKeyBinder.KeysFor($"weapon_group_{weapon.Impulse}").Concat(QcKeyBinder.KeysFor("weapon_" + weapon.NetName))
            .Select(BindInput.EngineKeyName)
            .Where(key => showJoystick || !key.StartsWith("JOY", StringComparison.OrdinalIgnoreCase))
            .Select(key => "^3" + LocalizedKeyName(key) + "^7")
            .ToArray();
        return keys.Length == 0
            ? Localization.Tr("This weapon has not been bound directly.")
            : Paragraph("You have bound this weapon to: %s.", string.Join(", ", keys));
    }

    private static string LocalizedKeyName(string key)
    {
        var numbered = Regex.Match(key, @"^(F|KP_|MOUSE|JOY|AUX|MIDINOTE)([0-9]+)$");
        if (numbered.Success)
        {
            string format = Localization.Tr(numbered.Groups[1].Value + "%d");
            return format.Replace("%d", numbered.Groups[2].Value, StringComparison.Ordinal);
        }

        int separator = key.IndexOf('_');
        if (separator >= 0 && key[..separator] is "KP" or "X360" or "JOY")
            return Paragraph(key[..(separator + 1)] + "%s", Localization.Tr(key[(separator + 1)..]));
        return Localization.Tr(key);
    }

    private readonly record struct WeaponDefaults(string Name)
    {
        private readonly record struct Attack(string Damage, double Delay, string Shots = "")
        {
            internal double DamagePerSecond => Delay > 0
                ? Number(Damage) / Delay * (Shots.Length == 0 ? 1 : Number(Shots))
                : 1337;
        }

        internal string Describe()
        {
            string primary = Localization.Tr("primary");
            string secondary = Localization.Tr("secondary");
            string rows = Name switch
            {
                "arc" => Row("The %s does %s damage per second", primary, ColorDamage(Default("beam_damage")))
                    + Single(Mode("bolt"), secondary),
                "blaster" or "vaporizer" or "vortex" or "okhmg" or "okmachinegun" or "oknex" => Single(Mode("primary")),
                "crylink" => Multi(Mode("primary", "primary_shots"), primary) + Multi(Mode("secondary", "secondary_shots"), secondary),
                "devastator" or "minelayer" or "tuba" => Single(Mode("")),
                "electro" => Electro(primary, secondary),
                "fireball" or "mortar" => Single(Mode("primary"), primary) + Single(Mode("secondary"), secondary),
                "hagar" => Single(Mode("primary"), primary) + Multi(Mode("secondary", "secondary_load_max", "secondary_load_speed",
                    damageFallback: Default("primary_damage")), secondary, variable: true),
                "hlac" or "rifle" => Single(Mode("primary"), primary) + Multi(Mode("secondary", "secondary_shots",
                    damageFallback: Default("primary_damage")), secondary),
                "hook" => Single(Mode("secondary"), secondary),
                "machinegun" => Single(Mode("sustained"), primary) + Multi(Mode("burst", "burst", "burst_refire2",
                    damageFallback: Default("sustained_damage")), secondary),
                "okrpc" => Single(Mode("primary")) + DirectHit(Mode("primary") with { Damage = Default("primary_damage2") }),
                "okshotgun" => Multi(Mode("primary", "primary_bullets")),
                "seeker" => Multi(Mode("missile", "missile_count", "tag_refire"), primary) + Single(Mode("flac"), secondary),
                "shotgun" => Multi(Mode("primary", "primary_bullets"), primary) + Single(Mode("secondary"), secondary),
                _ => "",
            };
            return rows.Length == 0 ? "" : Localization.Tr("Default damage stats:") + rows;
        }

        private string Default(string suffix) => MenuState.Cvars.GetDefault("g_balance_" + Name + "_" + suffix);

        private Attack Mode(string prefix, string shotKey = "", string refireLastKey = "", string damageFallback = "")
        {
            string stem = prefix.Length == 0 ? "" : prefix + "_";
            string damage = Default(stem + "damage");
            string shots = shotKey.Length == 0 ? "" : Default(shotKey);
            double delay = Number(Default(stem + "refire"));
            if (refireLastKey.Length != 0)
                delay = (Number(shots) - 1) * delay + Number(Default(refireLastKey));
            return new Attack(damage.Length == 0 ? damageFallback : damage, delay, shots);
        }

        private string Electro(string primaryName, string secondaryName)
        {
            Attack primary = Mode("primary");
            Attack secondary = Mode("secondary", "secondary_count");
            double shots = Number(secondary.Shots);
            double betweenShots = Number(Default("secondary_refire2"));
            double comboDelay = Math.Max(Math.Max(primary.Delay, secondary.Delay), shots * betweenShots + secondary.Delay);
            string comboDamage = Default("combo_damage");
            double comboDps = comboDelay > 0
                ? (Number(primary.Damage) + Number(secondary.Damage) * shots + Number(comboDamage)) / comboDelay
                : 1337;
            secondary = secondary with { Delay = (shots - 1) * betweenShots + secondary.Delay };
            return Single(primary, primaryName) + Multi(secondary, secondaryName, variable: true)
                + Row("The combo adds an extra %s damage", ColorDamage(comboDamage)) + Dps("(total DPS: %.2f)", comboDps);
        }

        private static string Single(Attack attack, string? mode = null)
        {
            string damage = ColorDamage(attack.Damage);
            string row = mode is null ? Row("It does %s damage", damage) : Row("The %s does %s damage", mode, damage);
            return row + Dps("(DPS: %.2f)", attack.DamagePerSecond);
        }

        private static string Multi(Attack attack, string? mode = null, bool variable = false)
        {
            string damage = ColorDamage(attack.Damage);
            string row = mode is null
                ? Row("It shoots %s shots doing %s damage each", attack.Shots, damage)
                : Row(variable ? "The %s shoots up to %s shots doing %s damage each" : "The %s shoots %s shots doing %s damage each",
                    mode, attack.Shots, damage);
            return row + Dps("(DPS: %.2f)", attack.DamagePerSecond);
        }

        private static string DirectHit(Attack attack) =>
            Row("A direct hit does %s damage", ColorDamage(attack.Damage)) + Dps("(DPS: %.2f)", attack.DamagePerSecond);

        private static double Number(string value) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number : 0;
        private static string ColorDamage(string value) => "^9" + value + "^7";
        private static string Row(string source, params string[] args) => "\n* " + Paragraph(source, args);
        private static string Dps(string source, double value) =>
            " " + Localization.Tr(source).Replace("%.2f", value.ToString("F2", CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}
