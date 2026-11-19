using System;
using System.Collections.Generic;
using System.Linq;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Gameplay.Nades;

namespace VortexArena.Game.Menu;

/// <summary>Native Guide descriptions for the gameplay catalogs, based on the corresponding MENUQC callbacks.</summary>
internal static partial class GuideCatalogText
{
    internal static string DescribeGametype(GameType mode) => mode.NetName switch
    {
        "as" => Text(
            Paragraph("%s is a team-based gametype involving an attacking and a defending team.",
                White(mode.DisplayName)),
            Localization.Tr("The attacking team has to destroy objects placed on the map, which opens up new doors or pathways to more objects, until the main object is reached and destroyed. " +
                           "The defending team has to defend the objects, winning the game if they successfully prevent the attacking team from destroying the main object within the time limit. " +
                           "If they fail then a second round is played where teams switch roles and the new attacking team wins only if they destroy the main object within the time the other team took to do it."),
            Localization.Tr("Objects have waypoints which show their health and position, and they're destroyed by shooting them with any weapon.")),
        "ca" => Text(
            Paragraph("%s is a common round-based gametype played with teams, where players spawn with all weapons available, have maximum health and armor, but only have one life per round. " +
                           "There are no pickups available on the map in %s, making the gameplay fast-paced and focused on combat.",
                White(mode.DisplayName), White(mode.DisplayName)),
            Localization.Tr("Rounds start with a ten second grace period where weapons cannot be fired, so it's a good opportunity to group up with your team."),
            Localization.Tr("The match ends once a team reaches a certain number of rounds won, or has a considerable lead over the other team."),
            Localization.Tr("Since players only have one life per round, it's important to group up with fellow teammates and pay attention to any callouts from them. " +
                           "Team coordination is key!")),
        "ctf" => Text(
            Paragraph("%s is a common team-based gametype where the objective is to steal an enemy team's flag and bring it to your own to capture it. " +
                           "A flag can only be captured if your own flag is also home in its base, so it's important to collect your flag after it's dropped.",
                White(mode.DisplayName)),
            Localization.Tr("The game is won once a team reaches a certain number of \"caps\" (captures), or has a considerable lead over the other team."),
            Localization.Tr("If a flag is dropped into the void or a death trap it will be immediately returned to its base, or otherwise sit idle for a while before automatically returning itself. " +
                           "If all flags are held, after some time period a \"stalemate\" will initiate and all flag carriers (\"fc\"s) will be exposed on the radar and with waypoints."),
            Paragraph("%s rewards crafty movement, since the more difficult it is for enemies to attack you the more likely you are to escape from their base with their flag. " +
                           "Importantly, it also rewards base defense since if your enemies can't take your flag, they can never capture. " +
                           "Lastly, good team communication is important, especially to let teammates know if you see the enemy flag carrier.",
                White(mode.DisplayName))),
        "cts" => Text(
            Paragraph("%s is a unique gametype, focusing on racing against other players on the map, rather than fighting them with weapons. " +
                           "Players run through the map from start to finish as fast as possible and try to beat the times set by others.",
                White(mode.DisplayName)),
            Paragraph("%s is similar to %s, except you don't run in laps, and instead run from the start line to the finish line then start over again.",
                White(mode.DisplayName), ModeName("rc")),
            Localization.Tr("Although this gametype is quite different from the rest, it is played quite frequently since it's a great way to master movement skills and there are heaps of community-made maps available. " +
                           "Also, it only requires one player to play.")),
        "dm" => Text(
            Paragraph("%s is a simple free-for-all fight to the death, where everyone plays against everyone else, and the winner is the one with the most points. " +
                           "When fragged, you respawn with only the starter weapons, so any you collected will have to be picked up again.",
                White(mode.DisplayName)),
            Localization.Tr("Scoring is quite simple, fragging an opponent adds one to your score, and fragging yourself subtracts one."),
            Paragraph("Maps often have powerups and items like %s and %s, which are usually highly contested since they sometimes have the power to make or break a game, so making good use of them is important.",
                ItemName("health_mega", "Mega health"), ItemName("armor_mega", "Mega armor"))),
        "dom" => Text(
            Paragraph("%s is a gametype where teams compete to dominate the map by capturing and keeping control points.",
                White(mode.DisplayName)),
            Localization.Tr("Control points are displayed on the radar to make finding them easy, and they are captured by simply walking through them. " +
                           "However, an enemy can just as easily steal a control point owned by your team by doing the same. " +
                           "Control points start neutral (owned by no teams)."),
            Localization.Tr("Scoring is a combination of frags and score automatically generated by any control points held by your team every few seconds.")),
        "duel" => Text(
            Paragraph("%s is a 1-vs-1 arena battle to decide the winner, essentially a 2-player version of %s, making it one of the most competitive gametypes.",
                White(mode.DisplayName), ModeName("dm")),
            Paragraph("Since players are on their own, important items like the %s and %s are heavily contested, " +
                           "and are often \"timed\" by observing the in-game timer when they are picked up to determine when they'll next spawn. " +
                           "The player with the best item control is often the winner, so this is a key aspect to master.",
                ItemName("health_mega", "Mega health"), ItemName("armor_mega", "Mega armor")),
            Localization.Tr("Once you frag your opponent, they will spawn with few weapons and limited health, so it is a good opportunity to search the map for them to pick up an easy frag and continue your streak."),
            Paragraph("%s is often played with %s rules.",
                White(mode.DisplayName), Localization.Tr("XPM"))),
        "freezetag" => Text(
            Paragraph("%s is a round-based teamplay gametype where fragged players are frozen and need to be unfrozen to re-enter the round. " +
                           "The round ends when all players from all opposing teams are frozen, scoring the remaining team a point.",
                White(mode.DisplayName)),
            Localization.Tr("Frozen players slowly unfreeze over time, but they unfreeze much faster when \"revived\" by a teammate standing in close proximity. " +
                           "Additionally, damaging frozen players on the other team helps unfreeze them, so it is best to try to avoid doing that."),
            Localization.Tr("Like in many other round-based gametypes, rounds start with a ten second grace period where weapons cannot be fired, meaning it's a good opportunity to grab as many pickups as you can."),
            Localization.Tr("Sneakily reviving a teammate will bring them back into the match to help outnumber your opponents, but it should be done with caution since often enemies pay close attention to frozen players to make sure they aren't being revived. " +
                           "For this reason it's important to have good team communication especially while frozen, so you can let your teammates know where the enemies are.")),
        "inv" => Text(
            Paragraph("%s is a unique gametype played in rounds where a herd of monsters spawn all around the map and players must try to frag as many as possible. " +
                           "Since players can't damage each other, they instead fight to steal monster frags from each other. " +
                           "The player who frags the most monsters overall wins the match.",
                White(mode.DisplayName)),
            Localization.Tr("A round ends when either all monsters are fragged, or after a time limit.")),
        "ka" => Text(
            Paragraph("%s is a free-for-all match where a ball spawns randomly on the map and the goal is to hold the ball for as long as possible.",
                White(mode.DisplayName)),
            Localization.Tr("Your score increases when you frag someone while you're holding a ball and when you frag the ball carrier. " +
                           "The first player to reach the score limit will win the match."),
            Localization.Tr("When the ball carrier is fragged, the ball will drop to the ground and anyone is able to pick it up. " +
                           "If nobody picks it up after some time it will get bored and teleport itself elsewhere."),
            Paragraph("%s rewards good fragging but also good movement, since the better you can evade attacks, the longer you can keep the ball.",
                White(mode.DisplayName))),
        "kh" => Text(
            Paragraph("%s is a round-based gametype played with a few teams, where each team starts with one key and all keys must be collected to win the round. " +
                           "The team member who is given the key at the start of the round is randomly selected after a ten second period, and the location of all keys will be visible on the radar after a few seconds.",
                White(mode.DisplayName)),
            Paragraph("Scoring in %s is quite complicated, involving a mix of gathering all keys, points for collecting a key, fragging an enemy key carrier, and fragging other enemies.",
                White(mode.DisplayName)),
            Paragraph("Like in %s the key can be dropped, so you can pass it to your teammates if you're running low on health.",
                ModeName("ctf"))),
        "lms" => Text(
            Paragraph("%s is a free-for-all gametype where all players start with a certain number of lives, and are eliminated from the match once they lose all lives. " +
                           "Players spawn in with all available weapons, maximum health and armor, and health and armor do not regenerate, similar to gametypes like %s. " +
                           "Similarly, item pickups don't spawn on the map.",
                White(mode.DisplayName), ModeName("ca")),
            Localization.Tr("Hiding is sometimes a viable strategy since if you can't be found you can't lose lives, but often you become a vulnerable target once discovered."),
            Paragraph("Players leading the game by at least 2 lives glow and periodically have waypoints over their heads visible to all players for a few seconds in order to prevent hiding too much. " +
                           "Players who aren't in the lead steal a small percentage of health when they attack leaders, like in the %s mutator.",
                White("Vampire"))),
        "mayhem" => Text(
            Paragraph("%s is a fast-paced free-for-all deathmatch where players spawn in with all available weapons and maximum health and armor. " +
                           "There are no pickups enabled on maps, but buffs are still available.",
                White(mode.DisplayName)),
            Localization.Tr("It shines in its unique handling of scoring, accounting for both frags but also damage dealt. " +
                           "This means that if you almost frag a player you will still be rewarded with points, but dealing the final blow is always better if possible. " +
                           "Points are deducted when you walk into hazards, but not when you do movement tricks like blaster jumping since self damage is disabled."),
            Paragraph("Since damage is always rewarded in %s, the more you shoot the better, turning this gametype into chaotic fun at times.",
                White(mode.DisplayName)),
            Paragraph("This gametype is often seen as a more newbie-friendly version of %s.",
                ModeName("dm"))),
        "nb" => Text(
            Paragraph("%s is a fun ball sport game where 2 teams compete to score the most goals.",
                White(mode.DisplayName)),
            Paragraph("%s is usually played as either soccer where players walk into the ball to kick it around, or basketball where players have a %s so they can pass the ball and intercept passes.",
                White(mode.DisplayName), WeaponName("ballstealer", "Ball Stealer")),
            Localization.Tr("Keep in mind players can score own goals, so don't embarrass yourself in front of your team!")),
        "ons" => Text(
            Paragraph("%s is a team-based gametype similar to %s where the final goal is to destroy the enemy team core generator. " +
                           "In order to destroy the generator, control points on the map (similar to those in %s) must first be captured.",
                White(mode.DisplayName), ModeName("as"), ModeName("dom")),
            Localization.Tr("Since your team can only capture a control point if it is connected to another control point currently owned by your team, you have to systematically progress across the map to reach the enemy generator."),
            Localization.Tr("A control point is captured by walking into it, triggering its automatic construction. " +
                           "Control points of the opposing team are destroyed by attacking the \"info bot\" that hovers above them, converting the control point to neutral once it is destroyed."),
            Localization.Tr("When in overtime, the enemy's generator will start to decay, at a faster rate the more control points your team has.")),
        "rc" => Text(
            Paragraph("%s is a gametype similar to %s where players run on a track to be the first to cross the finish line after a certain number of laps.",
                White(mode.DisplayName), ModeName("cts")),
            Paragraph("Before the real race starts there's a qualifying session that decides the starting grid based on the best lap time of each player. " +
                           "This stage is very similar to %s since weapons can't be used to disturb other players but can still be used to run faster.",
                ModeName("cts")),
            Localization.Tr("During the real race it's possible to shoot other players in order to slow them down.")),
        "surv" => Text(
            Paragraph("%s is a hunter-survivor gametype involving two teams where the goal of the hunters is to frag all survivors, and the goal of the survivors is to frag all hunters or survive until time runs out. " +
                           "The trick lies in the fact that survivors don't know who else is a survivor.",
                White(mode.DisplayName)),
            Localization.Tr("Since the goal of both teams is essentially to eliminate all members of the opposite team, it can be tricky for survivors to figure out their fellow teammates."),
            Localization.Tr("Teams are randomly selected as the round begins, with the hunter team being a bit smaller, " +
                           "and then the round starts with a brief warmup grace period in which items can be collected but weapons can't be fired."),
            Localization.Tr("Players are rewarded a point for each member of the opposing team they frag, only if they survive the round, and points are deducted for each teammate fragged. " +
                           "Extra points are also given to survivors who survive until the round timer expires.")),
        "tdm" => Text(
            Paragraph("%s is the team version of %s, played in teams rather than solo.",
                White(mode.DisplayName), ModeName("dm")),
            Localization.Tr("Scoring is quite simple, adding a point for every frag, and subtracting a point for fragging yourself or a teammate."),
            Localization.Tr("Careful team coordination and map control is often key to winning a match, and sometimes players will share weapons with their teammates.")),
        "tka" => Text(
            Paragraph("%s is the team version of %s, where teams compete to maintain superior control over the ball.",
                White(mode.DisplayName), ModeName("ka")),
            Paragraph("Scoring is similar to in %s, except that points are awarded for frags for all members of the team of the player holding the ball.",
                ModeName("ka"))),
        "tmayhem" => Text(
            Paragraph("%s is a constant action team-based gametype based on %s, using the same scoring system. " +
                           "It has a lot of similarities to %s, with one of the main differences being that %s is a round-based gametype, and of course the scoring system differences.",
                White(mode.DisplayName), ModeName("mayhem"), ModeName("ca"), ModeName("ca")),
            Localization.Tr("Since it doesn't have rounds, the chaos is nonstop. " +
                           "As a consequence of the chaotic nature, team communication is less important in this gametype than other team-based gametypes.")),
        _ => mode.MenuDescription is { Length: > 0 } text ? TranslateParagraphs(text) : "",
    };

    internal static string MutatorName(MutatorBase mutator, string fallback) => mutator.NetName switch
    {
        "bloodloss" => "Blood loss",
        "cloaked" => "Cloaked",
        "dodging" => "Dodging",
        "grappling_hook" => "Hook",
        "instagib" => "InstaGib",
        "nades" => "Nades",
        "new_toys" => "New Toys",
        "nix" => "NIX",
        "offhand_blaster" => "Offhand blaster",
        "pinata" => "Piñata",
        "rocketflying" => "Rocket Flying",
        "touchexplode" => "Touch explode",
        "vampire" => "Vampire",
        "walljump" => "Wall jumping",
        _ => fallback,
    };

    internal static string DescribeMutator(MutatorBase mutator) => mutator.NetName switch
    {
        "overkill" => "",
        "bloodloss" => Text(
            Paragraph("%s is a mutator in which players below a certain health threshold (for example 25) will suffer blood loss.",
                White(MutatorName(mutator, mutator.NetName))),
            Localization.Tr("Blood loss makes players stunned, severely impairs their movement, and rapidly takes away health points until they either die or gain enough health to go above the health threshold.")),
        "cloaked" => Text(
            Paragraph("The %s mutator makes all players nearly invisible, similar to the %s powerup. " +
                           "This adds an extra layer of stealth and strategy to gameplay.",
                White(MutatorName(mutator, mutator.NetName)), ItemName("invisibility", "Invisibility"))),
        "dodging" => Text(
            Paragraph("%s is a mutator that enables dodging maneuvers, making evading attacks in close combat easier. " +
                           "If enabled, when you're standing on the ground and double tap a movement key, you will leap in that direction. " +
                           "This also works while in contact with a wall, allowing quick speed gain if done repeatedly against a long wall.",
                White(MutatorName(mutator, mutator.NetName))),
            Paragraph("Some other mutators like %s enable this by default.",
                White("Overkill"))),
        "grappling_hook" => Text(
            Paragraph("The %s mutator gives all players a %s as their offhand weapon, used with %s. " +
                           "It has unlimited ammo, but the ordinary secondary fire can't be used.",
                White(MutatorName(mutator, mutator.NetName)), WeaponName("hook", "Hook"), GuidePages.Key("hook")),
            Paragraph("Since it's given as an offhand, players can use it to move around and shoot at their enemies at the same time, opening up more gameplay possibilities than the regular %s.",
                WeaponName("hook", "Hook")),
            Paragraph("Note that it is overridden by the %s mutator.",
                White("Offhand blaster"))),
        "instagib" => Text(
            Paragraph("%s is a mutator that removes all weapons and weapon pickups from the map, instead giving players the %s, " +
                           "which immediately frags players in a single shot, hence the name \"InstaGib.\"",
                White(MutatorName(mutator, mutator.NetName)), WeaponName("vaporizer", "Vaporizer")),
            Paragraph("Precise aim and ammo conservation are important, since you will suicide after 10 seconds if you run out of ammo. " +
                           "Ammo (%s) can be picked up on the map often, or collected from dropped %s guns.",
                ItemName("cells", "Cells"), WeaponName("vaporizer", "Vaporizer")),
            Paragraph("Maps often have an %s on them, which will save you from being fragged when picked up (total extra lives is displayed as armor). " +
                           "Other powerups often present on maps include %s and %s.",
                ItemName("extralife", "Extra life"), ItemName("speed", "Speed"), ItemName("invisibility", "Invisibility")),
            Paragraph("It is quite a common mutator present on many servers, mixing well with a lot of gametypes but particularly %s. " +
                           "Sometimes it's enabled alongside other mutators such as %s for added movement possibilities.",
                ModeName("dm"), White("Hook"))),
        "nades" => Text(
            Paragraph("The %s mutator gives all players offhand nades that can be primed then thrown by pressing %s twice. " +
                           "If the %s and %s mutators are disabled, they can also be thrown by holding then releasing %s.",
                White(MutatorName(mutator, mutator.NetName)), GuidePages.Key("drop weapon"), White("Offhand blaster"), White("Hook"), GuidePages.Key("hook")),
            Localization.Tr("There are multiple types of nades that can be selected, and many configuration options for this mutator, " +
                           "so they may act a little differently depending on the server you play on."),
            Paragraph("This mutator is enabled by default in %s.",
                White("Overkill"))),
        "new_toys" => Text(
            Paragraph("The %s mutator, enabled by default, allows the spawning of new gimmicky weapons, sometimes replacing a core weapon.",
                White(MutatorName(mutator, mutator.NetName))),
            Paragraph("Since these weapons can't spawn in %s and %s, the %s mutator can't be enabled concurrently.",
                White("InstaGib"), White("Overkill"), White(MutatorName(mutator, mutator.NetName))),
            Paragraph("The current %s weapons are:",
                White(MutatorName(mutator, mutator.NetName))) + string.Concat(WeaponsInRegistryOrder()
                    .Where(weapon => NewToysMutator.IsNewToy(weapon.NetName))
                    .Select(weapon => "\n* " + GuidePages.Colored(weapon.DisplayName, weapon.Color)))),
        "nix" => Text(
            Paragraph("%s is a mutator that gives all players the same randomly-selected weapon. After some time the weapon is reselected, and this repeats forever. " +
                           "Weapon and ammo pickups are also removed from the map.",
                White(MutatorName(mutator, mutator.NetName)))),
        "offhand_blaster" => Text(
            Paragraph("The %s mutator gives all players a %s as their offhand weapon, used with %s. " +
                           "It can be fired at any time, but the ordinary secondary fire can't be used.",
                White(MutatorName(mutator, mutator.NetName)), WeaponName("blaster", "Blaster"), GuidePages.Key("hook")),
            Paragraph("Since it's given as an offhand, you can fire it at the same time as the ordinary %s while laser jumping to achieve even more height.",
                WeaponName("blaster", "Blaster")),
            Paragraph("Note that it overrides the %s mutator, since they use the same bind.",
                White("Hook"))),
        "pinata" => Text(
            Paragraph("%s is a mutator that makes players drop all their weapons when they die. " +
                           "Without this mutator, players normally drop only their equipped weapon.",
                White(MutatorName(mutator, mutator.NetName)))),
        "rocketflying" => Text(
            Paragraph("The %s mutator removes the slight delay when immediately detonating a %s rocket after firing. " +
                           "This allows rockets to be detonated much closer to your hitbox, so they can give you a much larger speed boost, also allowing flying around the map.",
                White(MutatorName(mutator, mutator.NetName)), WeaponName("devastator", "Devastator"))),
        "touchexplode" => Text(
            Paragraph("The %s mutator causes an explosion when two players collide if it is enabled.",
                White(MutatorName(mutator, mutator.NetName))),
            Localization.Tr("This is a nice way to add some silly fun to a server, but it also does allow for the use of new tactics in some gametypes.")),
        "vampire" => Text(
            Paragraph("The %s mutator gives all players a permanent version of the %s buff. " +
                           "However, unlike the normal %s buff, when this mutator is enabled players' health can go way above the usual limit of 200.",
                White(MutatorName(mutator, mutator.NetName)), BuffName("buff_vampire", "Vampire"), BuffName("buff_vampire", "Vampire")),
            Paragraph("Additionally the amount of health players get is equal to the damage they deal, which isn't normally the case with the %s buff.",
                BuffName("buff_vampire", "Vampire"))),
        "walljump" => Text(
            Paragraph("%s is a mutator that enables jumping off walls for added mobility. " +
                           "To do a wall jump, tap %s when against a wall.",
                White(MutatorName(mutator, mutator.NetName)), GuidePages.Key("jump")),
            Localization.Tr("This mutator adds a bit of versatility, allowing for more dynamic and unpredictable movement.")),
        _ => mutator.GuideDescription is { Length: > 0 } text ? TranslateParagraphs(text) : "",
    };

    private static string Text(params string[] paragraphs) => string.Join("\n\n", paragraphs);

    private static string Paragraph(string source, params string[] names)
        => GuidePages.F(Localization.Tr(source), names);

    private static string White(string name) => GuidePages.Colored(name, 1f, 1f, 1f);

    private static string ModeName(string netName) => White(GameTypes.ByName(netName)?.DisplayName ?? netName);

    private static string ItemName(string netName, string fallback)
        => Items.ByName(netName) is { } item ? GuidePages.Colored(item.DisplayName, item.ItemDef.Color) : White(fallback);

    private static string WeaponName(string netName, string fallback)
        => Weapons.ByName(netName) is { } weapon ? GuidePages.Colored(weapon.DisplayName, weapon.Color) : White(fallback);

    private static string BuffName(StatusEffectDef effect, string displayName)
        => GuidePages.Colored(displayName, effect.Color.R, effect.Color.G, effect.Color.B);

    private static string BuffName(string netName, string fallback)
        => StatusEffectsCatalog.ByName(netName) is { } effect ? BuffName(effect, effect.DisplayName ?? fallback) : White(fallback);

    private static string NadeName(string netName, string fallback)
        => NadeRegistry.ByName(netName) is { } nade ? GuidePages.Colored(nade.Name, nade.Color) : White(fallback);

    /// <summary>Stock pickup amounts read the locked cvar defaults, as the MENUQC callbacks do.</summary>
    internal static string DescribeItem(Pickup item)
    {
        string name = GuidePages.Colored(item.DisplayName, item.ItemDef.Color);
        if (item is VaporizerCellsItem)
            return Paragraph("The %s is sometimes present on maps when %s is enabled, providing ammo for the %s.",
                name, White("InstaGib"), WeaponName("vaporizer", "Vaporizer"));

        string suffix = item is Bullets ? "nails" : item.NetName.Replace("_", "");
        string defaultCvar = "g_pickup_" + suffix;
        string amount = $"^9{MenuState.Cvars.GetDefault(defaultCvar)}^7";
        if (item is AmmoPickup ammo)
        {
            string users = item is Fuel
                ? Paragraph("The %s ammo type is used by the %s.", name, ItemName("jetpack", "Jetpack"))
                : Paragraph("The %s ammo type is used by the:", name) + string.Concat(WeaponsInRegistryOrder()
                    .Where(weapon => weapon.MenuRegistered && weapon.AmmoType == ammo.Resource)
                    .Select(weapon => "\n* " + GuidePages.Colored(weapon.DisplayName, weapon.Color)));
            return Text(users, Paragraph("By default, the pickup gives you %s ammo, to a maximum of %s.",
                amount, MenuState.Cvars.GetDefault(defaultCvar + "_max")));
        }

        string description = item switch
        {
            HealthSmall => "The %s restores a small amount of health when picked up, helping you recover from damage taken during combat.",
            HealthMedium => "The %s restores a medium amount of health when picked up, helping you recover from damage taken during combat.",
            HealthBig => "The %s restores a large amount of health when picked up, helping you recover from damage taken during combat.",
            HealthMega => "The %s restores a huge amount of health when picked up, helping you recover from damage taken during combat.",
            ArmorSmall => "The %s provides you a small amount of armor when picked up, protecting you from damage by absorbing incoming hits until it is depleted.",
            ArmorMedium => "The %s provides you a medium amount of armor when picked up, protecting you from damage by absorbing incoming hits until it is depleted.",
            ArmorBig => "The %s provides you a large amount of armor when picked up, protecting you from damage by absorbing incoming hits until it is depleted.",
            ArmorMega => "The %s provides you a huge amount of armor when picked up, protecting you from damage by absorbing incoming hits until it is depleted.",
            _ => "",
        };
        string effect = Paragraph(description, name);
        string defaults = Paragraph(item is HealthPickup
            ? "By default, the pickup gives you %s health."
            : "By default, the pickup gives you %s armor.", amount);

        return item is HealthMega or ArmorMega
            ? Text(effect, Paragraph("It tends to be one of the most highly contested items on a map, particularly in gametypes like %s.", ModeName("duel")), defaults)
            : Text(effect, defaults);
    }

    // The order follows the pinned Weapons registry without changing the native netcode IDs.
    internal static IEnumerable<Weapon> WeaponsInRegistryOrder() =>
        Weapons.All.OrderBy(weapon =>
        {
            int id = WeaponOrder.ToXonoticRegistryId(weapon.NetName);
            return id == 0 ? int.MaxValue : id;
        });

    internal static string DescribePowerup(Pickup item) => item.NetName switch
    {
        "fuel_regen" => Text(
            Paragraph("The %s powerup regenerates %s needed for the %s until the powerup expires, " +
                           "so you can continue flying around for longer.",
                GuidePages.Colored(item.DisplayName, item.ItemDef.Color), ItemName("fuel", "Fuel"), ItemName("jetpack", "Jetpack")),
            Localization.Tr("Since it is a powerup, it will drop if you die while holding it.")),
        "invisibility" => Text(
            Paragraph("The %s powerup increases your translucency while the powerup is active, making it more difficult for enemies to see you. " +
                           "This powerup is often present in %s.",
                GuidePages.Colored(item.DisplayName, item.ItemDef.Color), White("InstaGib")),
            Localization.Tr("Since it is a powerup, it will drop if you die while holding it.")),
        "jetpack" => Text(
            Paragraph("The %s powerup allows you to fly around the map for a short period, " +
                           "providing increased mobility, maneuverability, and the ability to reach higher ground.",
                GuidePages.Colored(item.DisplayName, item.ItemDef.Color)),
            Paragraph("It consumes %s ammo while operating, so make sure you don't run out when you're high up!",
                ItemName("fuel", "Fuel"))),
        "invincible" => Text(
            Paragraph("The %s powerup greatly decreases the damage you take until the powerup expires, having a compounding effect with armor. " +
                           "It also makes you more resistant to knockback.",
                GuidePages.Colored(item.DisplayName, item.ItemDef.Color)),
            Localization.Tr("Since it is a powerup, it will drop if you die while holding it.")),
        "speed" => Text(
            Paragraph("The %s powerup increases your movement speed, attack speed, and health regeneration speed while the powerup is active. " +
                           "However, it also makes you a bit more vulnerable to incoming attacks.",
                GuidePages.Colored(item.DisplayName, item.ItemDef.Color)),
            Localization.Tr("Since it is a powerup, it will drop if you die while holding it.")),
        "strength" => Text(
            Paragraph("The %s powerup greatly increases the damage you deal, until the powerup expires. " +
                           "It also increases the knockback that your attacks deal.",
                GuidePages.Colored(item.DisplayName, item.ItemDef.Color)),
            Localization.Tr("The damage and knockback you deal to yourself also increases but by a smaller amount."),
            Paragraph("The %s is one of the most sought after items on a map due to its ability to allow you to easily dominate a map, so it's often highly contested when it spawns in.",
                GuidePages.Colored(item.DisplayName, item.ItemDef.Color)),
            Localization.Tr("Since it is a powerup, it will drop if you die while holding it.")),
        "extralife" => Text(
            Paragraph("The %s powerup acts as armor in %s. " +
                           "If a player is shot while having one or more extra lives, they will survive the shot but suffer strong knockback.",
                GuidePages.Colored(item.DisplayName, item.ItemDef.Color), White("InstaGib"))),
        _ => "",
    };

    internal static string DescribeBuff(StatusEffectDef effect, string displayName) => effect.Name switch
    {
        "buff_ammo" => Text(
            Paragraph("The %s buff gives you infinite ammo until the buff expires, so you don't need to worry about running out of ammo.",
                BuffName(effect, displayName)),
            Paragraph("It also removes the need to reload any weapons that require reloading, like the %s and %s.",
                WeaponName("rifle", "Rifle"), WeaponName("okmachinegun", "Overkill MachineGun"))),
        "buff_bash" => Text(
            Paragraph("The %s buff increases the knockback force you deal, and makes you immune to knockback while the buff is active.",
                BuffName(effect, displayName)),
            Localization.Tr("It also slightly increases the knockback you deal to yourself.")),
        "buff_disability" => Text(
            Paragraph("The %s buff causes your enemies and monsters to slow down for a few seconds when you attack them, while the buff is active.",
                BuffName(effect, displayName)),
            Paragraph("This is particularly useful against speedy players, especially in %s.",
                ModeName("ctf"))),
        "buff_flight" => Text(
            Paragraph("While you have the %s buff, you can crouch while mid-air to switch your gravity, allowing flight.",
                BuffName(effect, displayName))),
        "buff_inferno" => Text(
            Paragraph("The %s buff sets any enemies or monsters you attack alight, dealing burn damage to them for several seconds until the buff expires.",
                BuffName(effect, displayName))),
        "buff_jump" => Text(
            Paragraph("The %s buff greatly increases your jump height, while the buff is active.",
                BuffName(effect, displayName)),
            Localization.Tr("It also makes you immune to fall damage.")),
        "buff_luck" => Text(
            Paragraph("While you have the %s buff, each attack has a chance of being a critical hit with greatly increased damage.",
                BuffName(effect, displayName))),
        "buff_magnet" => Text(
            Paragraph("The %s buff increases your item pickup range, collecting nearby items for you while the buff is active.",
                BuffName(effect, displayName))),
        "buff_medic" => Text(
            Paragraph("The %s buff increases health regeneration speed, increases the maximum health you can have before health starts to rot, and reduces health rot speed until the buff expires.",
                BuffName(effect, displayName)),
            Localization.Tr("It also gives you a chance to survive a fatal hit, with a small amount of health left over.")),
        "buff_resistance" => Text(
            Paragraph("The %s buff greatly reduces your damage taken while the buff is active.",
                BuffName(effect, displayName)),
            Localization.Tr("This also impacts the damage you deal to yourself.")),
        "buff_swapper" => Text(
            Paragraph("The %s buff allows you to press %s to switch places with a nearby enemy once.",
                BuffName(effect, displayName), GuidePages.Key("drop weapon")),
            Localization.Tr("A common usage of this buff is to jump over the map's void, then swap with an enemy, to cause them to fall into the void."),
            Localization.Tr("The range that the buff works over is limited, so sometimes it may do nothing if there are no enemies nearby.")),
        "buff_vampire" => Text(
            Paragraph("The %s buff converts some of the damage you deal to enemies and monsters into health for yourself, until the buff expires.",
                BuffName(effect, displayName)),
            Paragraph("In some sense, this is the opposite of the %s buff.",
                BuffName("buff_vengeance", "Vengeance"))),
        "buff_vengeance" => Text(
            Paragraph("The %s buff reciprocates a portion of the damage enemies deal to you onto them, until the buff expires.",
                BuffName(effect, displayName)),
            Paragraph("In some sense, this is the opposite of the %s buff.",
                BuffName("buff_vampire", "Vampire"))),
        _ => "",
    };

    internal static string DescribeNade(NadeDef nade) => nade.NetName switch
    {
        "normal" => Text(
            Paragraph("The (normal) %s explodes after a short delay, dealing damage to nearby players. " +
                           "If other nade types are destroyed before they detonate, they instead explode similar to this, without doing their special effects.",
                GuidePages.Colored(nade.Name, nade.Color)),
            Paragraph("Make sure you remember to throw it with %s, or else it will blow up in your hands!",
                GuidePages.Key("drop weapon"))),
        "napalm" => Text(
            Paragraph("The %s detonates after a short delay, spreading fiery napalm globs around in a fountain. " +
                           "The napalm fire balls burn for a while, and damage players who get too close.",
                GuidePages.Colored(nade.Name, nade.Color))),
        "ice" => Text(
            Paragraph("The %s detonates after a short delay, freezing any enemies who walk within the explosion radius for a few seconds after the explosion. " +
                           "While frozen, players can't move or shoot, leaving them vulnerable to damage, but if left too long they'll unfreeze and run away!",
                GuidePages.Colored(nade.Name, nade.Color))),
        "translocate" => Text(
            Paragraph("The %s detonates after a short delay, teleporting you to where it detonated.",
                GuidePages.Colored(nade.Name, nade.Color)),
            Localization.Tr("A common use of this nade is saving yourself from the void in space maps, however mastering this is very tricky."),
            Localization.Tr("If this grenade is destroyed by an attacker, it will deal some damage to them.")),
        "spawn" => Text(
            Paragraph("The %s detonates after a short delay, temporarily setting your spawnpoint to where it detonated. " +
                           "It is useful for cases where you want to go back to some point on the map after you die.",
                GuidePages.Colored(nade.Name, nade.Color))),
        "heal" => Text(
            Paragraph("The %s detonates after a short delay, temporarily creating a healing orb around the point where it detonated for several seconds. " +
                           "If your team members enter the orb they will recover health, and if enemies enter the sphere they will be harmed.",
                GuidePages.Colored(nade.Name, nade.Color))),
        "pokenade" => Text(
            Paragraph("The %s detonates after a short delay, spawning one of the five monster types.",
                GuidePages.Colored(nade.Name, nade.Color))),
        "entrap" => Text(
            Paragraph("The %s detonates after a short delay, temporarily creating an orb around the point where it detonated for several seconds. " +
                           "Players and projectiles that enter the sphere will be slowed down, including yourself.",
                GuidePages.Colored(nade.Name, nade.Color))),
        "veil" => Text(
            Paragraph("The %s detonates after a short delay, temporarily creating an orb around the point where it detonated for several seconds. " +
                           "Players inside the orb will be invisible to those outside it.",
                GuidePages.Colored(nade.Name, nade.Color)),
            Paragraph("This is sort of the opposite of the %s.",
                NadeName("darkness", "Darkness grenade"))),
        "ammo" => Text(
            Paragraph("The %s detonates after a short delay, temporarily creating an orb around the point where it detonated for several seconds. " +
                           "If your team members enter the orb they will recover ammo, and if enemies enter the sphere they will lose ammo.",
                GuidePages.Colored(nade.Name, nade.Color)),
            Paragraph("Similar to the %s buff, your weapon magazines remain full while you are inside the orb, so you don't need to reload.",
                BuffName("buff_ammo", "Ammo"))),
        "darkness" => Text(
            Paragraph("The %s detonates after a short delay, creating a dark field which temporarily blinds enemies who enter it.",
                GuidePages.Colored(nade.Name, nade.Color)),
            Paragraph("This is sort of the opposite of the %s.",
                NadeName("veil", "Veil grenade"))),
        _ => "",
    };

    internal static string DescribeMonster(Monster monster) => monster.NetName switch
    {
        "golem" => Text(
            Localization.Tr("Golems are large powerful brutes capable of taking and dealing a beating. Keeping your distance is advised."),
            Localization.Tr("The Golem's primary melee attack is a series of punches. " +
                           "On occasion the Golem may jump into the air, dealing massive damage in an area as it slams the ground."),
            Localization.Tr("To deal with distant foes, the Golem may throw a chunk of its electrified rocky exterior, zapping nearby targets on impact.")),
        "mage" => Text(
            Localization.Tr("Wielding nanotechnology as if it were sorcery, the Mage employs a range of unique abilities of its own creation in combat."),
            Localization.Tr("As a primary attack, the Mage throws a homing electric sphere towards the player. " +
                           "This sphere will track its target at high speed, exploding on impact or if it does not reach its target in time."),
            Localization.Tr("When threatened, the Mage may deploy an energy shield to protect itself from damage briefly. " +
                           "Enemies approaching too closely during this time may be pushed away with explosive force!"),
            Localization.Tr("Defensively the Mage is capable of healing itself and nearby allies, with some variants also providing armor and ammunition."),
            Localization.Tr("The Mage may sometimes appear to blink out of existence as it teleports behind its target for a sneak attack.")),
        "spider" => Text(
            Localization.Tr("The Spider is a large mechanically-enhanced arachnoid adept at hunting speedy enemies."),
            Localization.Tr("To slow down its target, the Spider launches a synthetic web-like substance from its cannons. " +
                           "Approaching its enwebbed prey, the Spider will inflict a series of high damage bites.")),
        "wyvern" => Text(
            Localization.Tr("The Wyvern is a flying reptilian monster that glides around hunting for fresh prey."),
            Localization.Tr("While fragile, the Wyvern is capable of launching deadly fireballs at the player from a distance, inflicting high damage and causing burning.")),
        "zombie" => Text(
            Localization.Tr("Zombies are the undead remains of deceased soldiers, risen with a ravenous hunger and no sense of self-preservation."),
            Localization.Tr("When a Zombie senses a nearby player it will begin to charge its target at high speeds. " +
                           "While charging, a Zombie may leap towards the player, dealing massive damage on contact. " +
                           "If it gets close, the Zombie will punch and bite repeatedly."),
            Localization.Tr("When threatened the Zombie may hold up its hands to block incoming attacks briefly."),
            Localization.Tr("It is no small task to kill that which is already dead. Once a Zombie is defeated, destroy its corpse to prevent it from rising again!")),
        _ => (monster.Describe() ?? monster.Description) is { Length: > 0 } text ? TranslateParagraphs(text) : "",
    };

    internal static string VehicleIcon(string name) => name switch
    {
        "bumblebee" => "vehicle_bumble",
        "spiderbot" => "vehicle_spider",
        _ => "vehicle_" + name,
    };

    internal static string DescribeVehicle(Vehicle vehicle) => vehicle.NetName switch
    {
        "bumblebee" => Text(
            Paragraph("The %s vehicle seats up to three passengers, who are given different roles based on the order they enter.",
                White(vehicle.DisplayName)),
            Localization.Tr("The first player to enter will be the pilot, flying the vehicle up into the skies. " +
                           "The first player can also shoot a green healing wave to heal other teammates on the ground."),
            Localization.Tr("The second player to enter is the right gunner and the third player to enter is the left gunner."),
            Localization.Tr("When the pilot exits the vehicle, the players switch roles by moving up in position.")),
        "racer" => Text(
            Paragraph("The %s vehicle looks like a jet, but does not fly upwards very well. " +
                           "It seats only one passenger, who becomes the pilot.",
                White(vehicle.DisplayName)),
            Paragraph("It can be accelerated by pressing %s, which consumes power from the same source as the primary weapon.",
                GuidePages.Key("jump")),
            Paragraph("The %s primary weapon is a laser and the secondary weapon shoots energy balls that explode on impact.",
                White(vehicle.DisplayName))),
        "raptor" => Text(
            Paragraph("The %s vehicle is a flying vehicle that takes only one pilot, who can operate both of the weapons.",
                White(vehicle.DisplayName)),
            Paragraph("The two weapons have different reticles for them similar to the %s. " +
                           "The primary weapon is laser-based, shooting toward the white reticle. " +
                           "The secondary weapon drops bombs to the ground, aiming towards the green reticle. " +
                           "The white reticle always points to the head of the vehicle, while the green reticle's location is determined by the momentum of the vehicle.",
                White("Spiderbot"))),
        "spiderbot" => Text(
            Paragraph("The %s vehicle walks and jumps around, and takes only one rider.",
                White(vehicle.DisplayName)),
            Localization.Tr("It can jump from very high altitudes while protecting the rider."),
            Paragraph("It has two weapons the rider can control, which have different reticles for them similar to the %s. " +
                           "The primary weapon is a machine gun, and is shot toward the green reticle. " +
                           "The secondary weapon shoots rockets towards the white reticle. " +
                           "The white reticle points to the front of the vehicle, while the green reticle also points to the front but with a bit of delay when the %s rotates.",
                White("Raptor"), White(vehicle.DisplayName))),
        _ => "",
    };

    internal static string DescribeTurret(Turret turret) => turret.NetName switch
    {
        "ewheel" => Text(
            Paragraph("The %s is a mobile turret that rolls around, attacking its targets when they enter its line of sight.",
                White(turret.DisplayName)),
            Paragraph("It attacks by shooting laser beams similar to those of the %s, from both of its two cannons.",
                WeaponName("blaster", "Blaster"))),
        "flac" => Text(
            Paragraph("The %s is a turret similar to flak cannons, instead firing anti-projectile flak shells to take down projectiles fired by its targets.",
                White(turret.DisplayName)),
            Paragraph("The shells it shoots target projectiles such as the %s secondary fire grenade, %s secondary fire balls, %s rockets, and other missiles.",
                WeaponName("mortar", "Mortar"), WeaponName("electro", "Electro"), WeaponName("devastator", "Devastator"))),
        "fusreac" => Text(
            Paragraph("The %s is a bit of a unique turret, instead helping out other turrets rather than directly attacking its targets. " +
                           "It has no weapon of its own, and instead works by generating power for nearby turrets, so that they can attack their shared targets more often.",
                White(turret.DisplayName)),
            Localization.Tr("This is the only turret that doesn't directly attack its targets.")),
        "hellion" => Text(
            Paragraph("The %s fires homing missiles similar to those of the %s towards its targets.",
                White(turret.DisplayName), WeaponName("devastator", "Devastator")),
            Paragraph("Although the missiles usually deal less damage than a %s rocket, the %s has two cannons so it can dish out quite the punch.",
                WeaponName("devastator", "Devastator"), White(turret.DisplayName))),
        "hk" => Text(
            Paragraph("The %s fires a single powerful homing rocket with the ability to evade obstacles to find its target. " +
                           "The rocket it fires is similar to that of the %s.",
                White(turret.DisplayName), WeaponName("devastator", "Devastator"))),
        "machinegun" => Text(
            Paragraph("As the name suggests, the %s fires bullets in a burst at its targets, similar to the %s.",
                White(turret.DisplayName), WeaponName("machinegun", "Machinegun"))),
        "mlrs" => Text(
            Paragraph("The %s fires a rapid burst of 6 rockets similar to those of the %s towards its targets.",
                White(turret.DisplayName), WeaponName("devastator", "Devastator")),
            Localization.Tr("Getting caught in the line of fire of this turret can be very deadly, " +
                           "but thankfully it doesn't shoot if you get too close, so as to not damage itself.")),
        "phaser" => Text(
            Paragraph("The %s is a turret that fires an invisible beam of energy towards its targets.",
                White(turret.DisplayName)),
            Paragraph("Looks are deceiving with the %s, since although the beam of energy can't be seen, it constantly deals damage to and slows down players who intercept its path.",
                White(turret.DisplayName))),
        "plasma" => Text(
            Paragraph("The %s is a turret that fires electric balls forwards, similar to those of the %s.",
                White(turret.DisplayName), WeaponName("electro", "Electro")),
            Paragraph("It is a weaker version of the %s, which has two electric ball cannons.",
                White("Dual Plasma Cannon"))),
        "plasma_dual" => Text(
            Paragraph("The %s is a turret that fires electric balls forwards from its two cannons, shooting balls similar to those of the %s.",
                White(turret.DisplayName), WeaponName("electro", "Electro")),
            Paragraph("It is a stronger version of the %s, which only has one electric ball cannon.",
                White("Plasma Cannon"))),
        "tesla" => Text(
            Paragraph("The %s is a turret that electrocutes all who dare come near it, sending powerful high-voltage arcs of lightning to strike its targets.",
                White(turret.DisplayName)),
            Paragraph("Since the arcs of lightning can jump between nearby targets, it is best to just stay away from the %s.",
                White(turret.DisplayName))),
        "walker" => Text(
            Paragraph("The %s is an arachnid-like walking turret that hunts down its targets.",
                White(turret.DisplayName)),
            Paragraph("It has two weapons, both a machinegun like the %s for medium range combat, and a rocket launcher shooting homing missiles like the %s for long range targets. " +
                           "It also has the ability to melee close by targets.",
                WeaponName("machinegun", "Machinegun"), WeaponName("devastator", "Devastator"))),
        _ => "",
    };

    private static string TranslateParagraphs(string text)
        => string.Join("\n\n", text.Split("\n\n", StringSplitOptions.None).Select(Localization.Tr));
}
