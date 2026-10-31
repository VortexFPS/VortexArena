using System;
using System.Collections.Generic;
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The guide's free-text pages — menu/xonotic/guide/pages.qc: the Introduction, Movement and Mods topics
/// (REGISTER_INTRODUCTION_PAGE / _MOVEMENT_PAGE / _MODS_PAGE), each a name and paragraphs joined by a blank line
/// (PAGE_TEXT_INIT / PAR). Names the text refers to are drawn in their thing's colour, as COLORED_NAME does.
/// </summary>
public static class GuidePages
{
    public sealed record Page(string Name, Func<string> Describe);

    private static string T(string s) => Localization.Tr(s);

    /// <summary>PAR: the paragraph, sprintf'd with its arguments, after a blank line.</summary>
    private static string Text(params string[] paragraphs) => string.Join("\n\n", paragraphs);

    private static string F(string format, params string[] args)
    {
        // sprintf with %s only, which is all these pages use.
        var sb = new System.Text.StringBuilder();
        int a = 0;
        for (int i = 0; i < format.Length; i++)
        {
            if (format[i] == '%' && i + 1 < format.Length && format[i + 1] == 's')
            {
                sb.Append(a < args.Length ? args[a++] : "");
                i++;
            }
            else
                sb.Append(format[i]);
        }
        return sb.ToString();
    }

    /// <summary>COLORED_NAME: rgb_to_hexcolor(m_color) + the name + "^7".</summary>
    private static string Colored(string name, float r, float g, float b)
    {
        static char D(float c) => "0123456789ABCDEF"[(int)Math.Floor(Math.Clamp(c, 0f, 1f) * 15f + 0.5f)];
        return $"^x{D(r)}{D(g)}{D(b)}{T(name)}^7";
    }

    private static string Key(string action) => "^3" + T(action) + "^7";

    private static string Blaster => Colored("Blaster", 0.969f, 0.443f, 0.482f);
    private static string Crylink => Colored("Crylink", 0.918f, 0.435f, 0.976f);
    private static string Devastator => Colored("Devastator", 0.914f, 0.745f, 0.341f);
    private const string GuideUrl = "^5xonotic.org/guide^7";

    public static readonly IReadOnlyList<Page> Introduction = new Page[]
    {
        new(T("About this guide"), () => Text(
            T("Welcome to Xonotic, the free and fast arena shooter!"),
            T("This guide covers the basics one should know about the weapons, items, and other aspects of the game, but most of what you will learn will come from trying it out in practice."),
            T("Please note that this guide describes the default game behavior with vanilla settings. Xonotic is highly customizable and there are tons of servers out there that run with lightly or highly modified settings."),
            F(T("You can usually find out which settings are modified by issuing %s in console after connecting to a server."), "^3cvar_changes^7"))),
        new(T("Community"), () => Text(
            T("Xonotic stands out for its friendly, mature, and welcoming player community, and many long-term players argue that this is probably one of the best things about Xonotic. If you approach other players friendly and respectfully, this will be recognized, while acting toxic on the other hand will not make your life easier on the servers."),
            F(T("The best way to get in touch with the community is to just start playing online and use the in-game chat. You can also connect to the official communication channels (%s), and of course the official forum."),
                F(T("%s on Matrix, %s on QuakeNet IRC, or the Discord"), "^5#main:xonotic.org^7", "^5#xonotic^7")))),
        new(T("Getting good"), () => Text(
            T("The skill gap between new and experienced players is often immense. However, this is mostly because experienced players have learned how to make use of the game mechanics. If you see experienced players doing tricks you don't understand, don't hesitate to ask anyone on the server!"),
            T("Another way to learn is to spectate experienced players. Observe which weapons they combo and the movement techniques they make use of. Also look at which weapons they choose depending on the gameplay situation, for example in close combat, mid-range battles, or against groups of opponents."),
            T("The learning curve is very steep. That said, understanding how the game works will enable you to speed around the map and apply effective weapon combos in no time."))),
        new(T("XonStat"), () => Text(
            T("XonStat is the integrated player statistics application for Xonotic. It stores vital stats like your kill-death ratio, weapon damage and accuracy, and scoreboards from past games."),
            T("Tracking is completely opt-in, and no login is required."),
            F(T("Some of the statistics are available in-game on your player profile, while the rest are accessible at %s."), "^5stats.xonotic.org^7"))),
    };

    public static readonly IReadOnlyList<Page> Movement = new Page[]
    {
        new(T("Introduction"), () => Text(
            T("Xonotic is a very fast-paced arena shooter. Fast-paced does not only relate to reaction times but also to movement speed. That means mastering movement skills is an essential part of Xonotic gameplay and must not be underestimated."),
            T("For a new player, the most important tricks to learn are:"),
            F("* %s\n* %s\n* %s\n* %s\n* %s\n* %s", T("Bunny Hopping"), T("Strafe Turning"), T("Blaster Jumping"), T("Rocket Flying"), T("Crylink Running"), T("Ramp Jumping")))),
        new(T("Bunny Hopping"), () => Text(
            T("Bunny hopping (\"bhopping\") is a technique known also from other arena shooters."),
            F(T("Due to air control, you can accelerate while in the air far beyond the maximum ground movement speed. To avoid ground friction, players constantly hop by simply keeping %s pressed, so that ground contact doesn't slow them down. This allows you to get around the map much faster than if you would just walk."), Key("jump")),
            F(T("Therefore, keeping %s held while moving quickly is one of the most essential movement strategies, used by all experienced players."), Key("jump")),
            T("With default settings, the speed you can reach by bunny hopping is capped at 900 qu/s (\"units per second\"). CTS/DeFRaG servers usually run with different CPMA-like physics that remove this cap."))),
        new(T("Strafe Turning"), () => Text(
            T("Bunny hopping around corners requires \"strafe turning\" or you will lose a lot of speed (or simply not be able to make the turn). Strafe turning is a method used to quickly change the direction you're traveling while mid-air, without losing speed."),
            F(T("While bunny hopping, first release %s, then press the strafe key into the turn direction (%s or %s), then start turning smoothly into the turn direction. As always, during the entire strafe turn keep %s held."),
                Key("move forward"), Key("strafe left"), Key("strafe right"), Key("jump")),
            T("Note that if you press the strafe key before releasing the forward key, you will suffer a speed penalty."),
            T("If you do it right, you will even continue to accelerate in strafe turns."),
            F(T("For an illustration of this technique, see %s."), GuideUrl))),
        new(T("Blaster Jumping"), () => Text(
            F(T("In most situations, the %s is more a movement tool than a weapon. That's why many players will often have single digit accuracy for the %s. It is instead especially useful to make high jumps."), Blaster, Blaster),
            F(T("To do a \"laser jump,\" simply fire the %s at your own feet and jump immediately afterwards. Experiment a bit with the timing of firing the %s and jumping to get a feeling of which timings will get you how high."), Blaster, Blaster))),
        new(T("Wall Blastering"), () => Text(
            F(T("If you don't fire the %s at the floor but instead against a wall next to you, you can get significant horizontal speed. You can do this to pick up speed off the stand or even when passing a wall while already bunny hopping."), Blaster),
            F(T("To use this technique, start a bunny hop. Right after jumping, flick back with your mouse at about 8 o'clock if the wall is to your left (or 4 o'clock if the wall is to your right), aim at the level of your head, and fire the %s. Immediately flick back to your movement direction and continue to bunny hop."), Blaster),
            F(T("In order to waste as little speed as possible while flicking back, switch to a strafe key for the duration of the flick. This means using %s if the wall is to your left, and %s if the wall is to your right."), Key("strafe left"), Key("strafe right")),
            F(T("For an illustration of this technique, see %s."), GuideUrl))),
        new(T("Rocket Flying"), () => Text(
            F(T("The %s can be used just like the %s for movement, but it inflicts much more damage to the player. In gametypes without self damage however it is a very versatile movement tool."), Devastator, Blaster),
            T("While mid-air, you can also fire a rocket underneath yourself and immediately detonate it. The explosion will push you upwards a little bit, and can prolong a jump."),
            F(T("Note that this strategy only really works if the %s mutator is enabled."), Colored("Rocket Flying", 1, 1, 1)))),
        new(T("Crylink Running"), () => Text(
            F(T("The drag force of the %s secondary fire can not only break other players' movement, but can also give yourself a significant speed boost."), Crylink),
            F(T("To \"crylink run,\" shoot the %s secondary at the floor (or a parallel wall) just in front of you and begin bunny hopping to catapult yourself off the stand into high speed. Finding the best angle to aim at takes some practice, and changes depending on your speed."), Crylink, Crylink))),
        new(T("Crylink Wall Climbing"), () => Text(
            F(T("By shooting the %s secondary against a wall next to you (just above your head), you can climb up walls quickly. This is called \"wall climbing\" with the %s."), Crylink, Crylink),
            T("This is also a good way to save yourself from falling off the map, as it reliably stops your downfall."))),
        new(T("Ramp Jumping"), () => Text(
            T("Ramps are an important part of Xonotic map design. Any sloped surface that you hit with the right speed and the right angle will give you a significant upwards boost and enable you to reach places that are otherwise accessible only with laser jumps, which cost health and make noise."),
            T("When doing ramp jumps, it is vital to hit the ramp at a flat angle to receive the upwards boost and not lose too much speed."),
            F(T("In order to make a ramp jump, approach a sloped surface (\"ramp\"), jump just before the ramp and keep %s pressed so you jump again once you hit the ramp."), Key("jump")),
            T("The higher the speed you have when you hit the ramp (at the right angle), the stronger the upwards boost. As always, you can gain speed before the ramp by bunny hopping."),
            T("Note that when trying to make a ramp jump while bunny hopping, it is vital to time your hops carefully so you get the angle right."),
            F(T("For an illustration of this technique, see %s."), GuideUrl))),
        new(T("Advanced Movement"), () => Text(
            T("Advanced techniques like \"strafe jumping\" (\"strafing\") or \"circle jumping\" (\"CJ\") offer only limited benefits in vanilla gameplay, but are crucial on CTS/DeFRaG servers that use slightly different physics."),
            F(T("If you want to master these techniques, be sure to check out %s."), "^5xdf.gg/guide^7"))),
    };

    public static readonly IReadOnlyList<Page> Mods = new Page[]
    {
        new(T("Overkill"), () => Text(
            F(T("%s is a mod that heavily changes the game, giving players fewer but more powerful weapons, nades, and more movement options."), Colored("Overkill", 1, 1, 1)),
            F(T("In terms of weaponry, players have infinite ammo and spawn in with the %s, %s, and %s, and can pick up the %s and %s superweapons on the map sometimes. These weapons are a lot more powerful than the regular weapons, so their time-to-kill is quite low."),
                Colored("Overkill Shotgun", 0.518f, 0.608f, 0.659f), Colored("Overkill MachineGun", 0.678f, 0.886f, 0.267f),
                Colored("Overkill Nex", 0.459f, 0.765f, 0.835f), Colored("Overkill Heavy MachineGun", 0.992f, 0.471f, 0.396f),
                Colored("Overkill Rocket Propelled Chainsaw", 0.914f, 0.745f, 0.341f)),
            F(T("The secondary fire of all weapons shoots a laser which does not damage or push enemies, but can be used to push yourself around. Also since the %s mutator is active, all players have offhand nades."), Colored("Nades", 1, 1, 1)),
            F(T("As for movement tricks, %s is enabled by default in %s, allowing for rapidly changing your movement direction."), Colored("Dodging", 1, 1, 1), Colored("Overkill", 1, 1, 1)))),
        new(T("XPM"), () => Text(
            F(T("%s (\"Xonotic Pro-Mode\") is a set of gameplay settings often used for competitive play, particularly in %s."), T("XPM"), Colored("Duel", 1, 1, 1)),
            F(T("It adds extra rules for picking up items, preventing players with 100 or more health or armor from taking %s, %s, %s, and %s pickups, in order to give other players a fairer chance of rebuilding their stack. It also disables spawn protection and enables friendly fire in team-based gametypes."),
                Colored("Big health", 1, 0, 0), Colored("Medium health", 1, 0, 0), Colored("Big armor", 0, 1, 0), Colored("Medium armor", 0, 1, 0)))),
    };
}
