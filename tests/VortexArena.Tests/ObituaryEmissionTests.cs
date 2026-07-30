using System.Linq;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T40: the server combat-event EMISSION — the obituary kill feed / frag centerprints / killstreak announcer
/// (the C# port of qcsrc/server/damage.qc Obituary + the per-weapon wr_killmessage/wr_suicidemessage). Drives
/// <see cref="Scores.Obituary"/> over the <see cref="Combat.Death"/> bus with a recording notification sink and
/// asserts the exact notifications QC sends. Also unit-tests the central <see cref="DeathMessages"/> selection
/// table. Runs in the GlobalState collection (process-global Api.Services + NotificationSystem.Sink + registries).
/// </summary>
[Collection("GlobalState")]
public class ObituaryEmissionTests : IDisposable
{
    private readonly INotificationSink _prevSink;
    private readonly IEngineServices _prevServices;
    private readonly NotificationSystem.RecordingSink _rec = new();
    private readonly List<Scores> _subscribed = new();

    public ObituaryEmissionTests()
    {
        Notifications.RegisterAll();        // the obituary names + the CHOICE arg-count backfill source
        DeathMessages.ResetChoiceArgCounts();

        _prevServices = Api.Services;
        Api.Services = new EngineServices(new CollisionWorld()); // real cvars/clock; no sound needed here
        _prevSink = NotificationSystem.Sink;
        NotificationSystem.Sink = _rec;
        NotificationSystem.WarmupStage = false;     // a live match (so first blood + frag choices fire)
        NotificationSystem.DefaultChoiceValue = 1;  // option A (terse) — QC N__NORMAL default
        NotificationSystem.ChoiceValues.Clear();
    }

    public void Dispose()
    {
        // detach our Scores from the static Combat.Death chain so a later test isn't perturbed.
        foreach (var s in _subscribed) s.UnsubscribeFromDeaths();
        NotificationSystem.Sink = _prevSink;
        NotificationSystem.WarmupStage = false;
        Api.Services = _prevServices;
    }

    private static Player NewPlayer(string name, int team = 0)
    {
        var p = new Player { NetName = name, Flags = EntFlags.Client };
        p.Team = team;
        p.SetResource(ResourceType.Health, 100f);
        p.SetResource(ResourceType.Armor, 25f);
        return p;
    }

    // ---- DeathMessages selection table (wr_killmessage / wr_suicidemessage) -------------------------

    [Fact]
    public void SelectKillMessage_Devastator_Branches_On_Splash()
    {
        string dtSplash = DeathTypes.WithHitType(DeathTypes.FromWeapon("devastator"), DeathTypes.Splash);
        string dtDirect = DeathTypes.FromWeapon("devastator");
        Assert.Equal("WEAPON_DEVASTATOR_MURDER_SPLASH", DeathMessages.SelectKillMessage("devastator", dtSplash));
        Assert.Equal("WEAPON_DEVASTATOR_MURDER_DIRECT", DeathMessages.SelectKillMessage("devastator", dtDirect));
        // QC: HITTYPE_BOUNCE also routes to SPLASH.
        string dtBounce = DeathTypes.WithHitType(DeathTypes.FromWeapon("devastator"), DeathTypes.Bounce);
        Assert.Equal("WEAPON_DEVASTATOR_MURDER_SPLASH", DeathMessages.SelectKillMessage("devastator", dtBounce));
    }

    [Fact]
    public void SelectKillMessage_Electro_Branches_Secondary_Then_Bounce()
    {
        string baseDt = DeathTypes.FromWeapon("electro");
        Assert.Equal("WEAPON_ELECTRO_MURDER_BOLT", DeathMessages.SelectKillMessage("electro", baseDt));
        Assert.Equal("WEAPON_ELECTRO_MURDER_COMBO",
            DeathMessages.SelectKillMessage("electro", DeathTypes.WithHitType(baseDt, DeathTypes.Bounce)));
        Assert.Equal("WEAPON_ELECTRO_MURDER_ORBS",
            DeathMessages.SelectKillMessage("electro", DeathTypes.WithHitType(baseDt, DeathTypes.Secondary)));
    }

    [Fact]
    public void SelectKillMessage_Vortex_And_Vaporizer_Are_Single()
    {
        Assert.Equal("WEAPON_VORTEX_MURDER", DeathMessages.SelectKillMessage("vortex", DeathTypes.FromWeapon("vortex")));
        Assert.Equal("WEAPON_VAPORIZER_MURDER", DeathMessages.SelectKillMessage("vaporizer", DeathTypes.FromWeapon("vaporizer")));
    }

    [Fact]
    public void SelectKillMessage_Unknown_Weapon_Falls_Back_To_Generic_Frag()
        => Assert.Equal("DEATH_MURDER_FRAG", DeathMessages.SelectKillMessage("nonexistent", DeathTypes.FromWeapon("nonexistent")));

    [Fact]
    public void SelectSuicideMessage_Devastator_And_Hitscan_Portals()
    {
        Assert.Equal("WEAPON_DEVASTATOR_SUICIDE", DeathMessages.SelectSuicideMessage("devastator", DeathTypes.FromWeapon("devastator")));
        // QC: the hitscan weapons return the THINKING_WITH_PORTALS easter egg on suicide.
        Assert.Equal("WEAPON_THINKING_WITH_PORTALS", DeathMessages.SelectSuicideMessage("vortex", DeathTypes.FromWeapon("vortex")));
        Assert.Equal("WEAPON_THINKING_WITH_PORTALS", DeathMessages.SelectSuicideMessage("machinegun", DeathTypes.FromWeapon("machinegun")));
    }

    [Fact]
    public void SelectSpecial_Maps_Self_And_Murder_Families()
    {
        Assert.Equal("DEATH_SELF_FALL", DeathMessages.SelectSpecial(DeathTypes.Fall, murder: false));
        Assert.Equal("DEATH_MURDER_FALL", DeathMessages.SelectSpecial(DeathTypes.Fall, murder: true));
        Assert.Equal("DEATH_SELF_DROWN", DeathMessages.SelectSpecial(DeathTypes.Drown, murder: false));
        // telefrag has no self form -> generic; murder form exists.
        Assert.Equal("DEATH_SELF_GENERIC", DeathMessages.SelectSpecial(DeathTypes.Telefrag, murder: false));
        Assert.Equal("DEATH_MURDER_TELEFRAG", DeathMessages.SelectSpecial(DeathTypes.Telefrag, murder: true));
    }

    /// <summary>
    /// [A5 #4] QC Obituary_SpecialDeath reads the REGISTERED death_msgself/death_msgmurder per deathtype
    /// (deathtypes/all.inc). A category death must NOT fall back to the generic FRAG/GENERIC line: a monster
    /// murder is the shared DEATH_MURDER_MONSTER with a per-monster DEATH_SELF_MON_* self line; a turret murder
    /// is the shared DEATH_MURDER_CHEAT with a per-turret DEATH_SELF_TURRET_* self line; a vehicle carries its
    /// own per-vehicle self+murder line. SelectSpecial must consult the category registry, not the flat switch.
    /// </summary>
    [Fact]
    public void SelectSpecial_Monster_Turret_Vehicle_UseCategoryRegisteredMessages()
    {
        // Monster: shared murder, per-monster self (all.inc rows 15-22).
        Assert.Equal("DEATH_MURDER_MONSTER", DeathMessages.SelectSpecial(DeathTypes.MonsterSpider, murder: true));
        Assert.Equal("DEATH_SELF_MON_SPIDER", DeathMessages.SelectSpecial(DeathTypes.MonsterSpider, murder: false));
        Assert.Equal("DEATH_MURDER_MONSTER", DeathMessages.SelectSpecial(DeathTypes.MonsterMage, murder: true));
        Assert.Equal("DEATH_SELF_MON_MAGE", DeathMessages.SelectSpecial(DeathTypes.MonsterMage, murder: false));

        // Turret: shared DEATH_MURDER_CHEAT murder, per-turret self (all.inc rows 36-48). Bare turret -> bare self.
        Assert.Equal("DEATH_MURDER_CHEAT", DeathMessages.SelectSpecial("turret_mlrs", murder: true));
        Assert.Equal("DEATH_SELF_TURRET_MLRS", DeathMessages.SelectSpecial("turret_mlrs", murder: false));
        Assert.Equal("DEATH_MURDER_CHEAT", DeathMessages.SelectSpecial(DeathTypes.Turret, murder: true));
        Assert.Equal("DEATH_SELF_TURRET", DeathMessages.SelectSpecial(DeathTypes.Turret, murder: false));

        // Vehicle: per-vehicle self AND murder (all.inc rows 49-61).
        Assert.Equal("DEATH_MURDER_VH_WAKI_ROCKET", DeathMessages.SelectSpecial("vh_waki_rocket", murder: true));
        Assert.Equal("DEATH_SELF_VH_WAKI_ROCKET", DeathMessages.SelectSpecial("vh_waki_rocket", murder: false));
        // A vehicle GUN registers murder-only (death_msgself == NULL) -> generic self fallback.
        Assert.Equal("DEATH_MURDER_VH_WAKI_GUN", DeathMessages.SelectSpecial("vh_waki_gun", murder: true));
        Assert.Equal("DEATH_SELF_GENERIC", DeathMessages.SelectSpecial("vh_waki_gun", murder: false));
    }

    // ---- the obituary emission over the death bus --------------------------------------------------

    private Scores SubscribedScores(bool teamGame)
    {
        var s = new Scores();
        s.SubscribeToDeaths(teamGame, ownsScore: false);
        _subscribed.Add(s);
        return s;
    }

    /// <summary>Fire a kill on the Combat.Death bus (the path Scores subscribes to).</summary>
    private static void FireDeath(Player victim, Player? attacker, string deathType)
    {
        var ev = new DeathEvent { Victim = victim, Attacker = attacker, DeathType = deathType };
        Combat.Death.Call(ref ev);
    }

    [Fact]
    public void EnemyFrag_With_Vortex_Emits_KillFeed_And_FragCenters()
    {
        var s = SubscribedScores(teamGame: false);
        var a = NewPlayer("Attacker");
        var b = NewPlayer("Victim");
        s.Register(a); s.Register(b);

        string dt = DeathTypes.FromWeapon("vortex");
        FireDeath(b, a, dt);

        // kill feed: the WEAPON_VORTEX_MURDER info line (via the victim's MULTI sub + the all-except INFO).
        Assert.Contains(_rec.Log, d => d.Notification.RegistryName == "INFO_WEAPON_VORTEX_MURDER");
        // frag centerprints: the attacker's "you fragged" + the victim's "you were fragged" (CHOICE option A).
        Assert.Contains(_rec.Log, d => d.Notification.RegistryName == "CENTER_DEATH_MURDER_FRAG" && ReferenceEquals(d.Target, a));
        Assert.Contains(_rec.Log, d => d.Notification.RegistryName == "CENTER_DEATH_MURDER_FRAGGED" && ReferenceEquals(d.Target, b));
    }

    [Fact]
    public void FirstBlood_Sets_KillCount_Minus_One_On_The_First_Enemy_Frag()
    {
        var s = SubscribedScores(teamGame: false);
        var a = NewPlayer("A");
        var b = NewPlayer("B");
        s.Register(a); s.Register(b);

        FireDeath(b, a, DeathTypes.FromWeapon("vortex"));

        // QC first blood: kill_count_to_attacker = -1 (the CHOICE_FRAG f1=spree_cen arg), target = -2.
        var fragA = _rec.Log.First(d => d.Notification.RegistryName == "CENTER_DEATH_MURDER_FRAG" && ReferenceEquals(d.Target, a));
        Assert.Equal(-1f, fragA.FloatArgs[0]); // spree_cen == kill_count_to_attacker
        var fraggedB = _rec.Log.First(d => d.Notification.RegistryName == "CENTER_DEATH_MURDER_FRAGGED" && ReferenceEquals(d.Target, b));
        Assert.Equal(-2f, fraggedB.FloatArgs[0]); // spree_cen == kill_count_to_target
    }

    [Fact]
    public void Suicide_By_Fall_Emits_DeathSelfFall()
    {
        var s = SubscribedScores(teamGame: false);
        var b = NewPlayer("Faller");
        s.Register(b);

        // attacker == victim (or null) + a special deathtype -> DEATH_SELF_FALL.
        FireDeath(b, b, DeathTypes.Fall);

        Assert.Contains(_rec.Log, d => d.Notification.RegistryName == "INFO_DEATH_SELF_FALL");
        // and NOT a frag/teamkill line.
        Assert.DoesNotContain(_rec.Log, d => d.Notification.RegistryName.Contains("MURDER"));
    }

    [Fact]
    public void Teamkill_Emits_TeamkillCenters_And_Info_Not_A_Weapon_Line()
    {
        var s = SubscribedScores(teamGame: true);
        var a = NewPlayer("Traitor", team: Teams.Red);
        var b = NewPlayer("Buddy", team: Teams.Red);
        s.Register(a); s.Register(b);

        FireDeath(b, a, DeathTypes.FromWeapon("vortex"));

        Assert.Contains(_rec.Log, d => d.Notification.RegistryName == "CENTER_DEATH_TEAMKILL_FRAG" && ReferenceEquals(d.Target, a));
        Assert.Contains(_rec.Log, d => d.Notification.RegistryName == "CENTER_DEATH_TEAMKILL_FRAGGED" && ReferenceEquals(d.Target, b));
        // INFO_DEATH_TEAMKILL_<victimteam> (RED) to everyone.
        Assert.Contains(_rec.Log, d => d.Notification.RegistryName == "INFO_DEATH_TEAMKILL_RED");
        // a teamkill never sends a weapon murder line.
        Assert.DoesNotContain(_rec.Log, d => d.Notification.RegistryName == "INFO_WEAPON_VORTEX_MURDER");
    }

    /// <summary>
    /// [A5 #8] The FRAG centerprint is a MSG_CHOICE resolved PER RECIPIENT (QC Send_Notification_Core reads
    /// CS(recipient).msg_choice_choices[idx] in its send loop). Two attackers with different
    /// notification_CHOICE_FRAG preferences must each receive THEIR option: the terse picker gets
    /// CENTER_DEATH_MURDER_FRAG (A), the verbose picker gets CENTER_DEATH_MURDER_FRAG_VERBOSE (B) — never crossed.
    /// FRAG is challow=Warmup, so the per-client value is only honoured during warmup.
    /// </summary>
    [Fact]
    public void EnemyFrag_Resolves_FragChoice_PerRecipient_TerseVsVerbose()
    {
        var s = SubscribedScores(teamGame: false);

        var fragChoice = Notifications.ByName(MsgType.Choice, "FRAG")!;
        // Two attacker preferences, seeded with the QC defaults the server would hold (FillDefault = option A),
        // then the verbose attacker overrides FRAG to option B — exactly what Commands.GetChoiceState yields.
        var terse = new NotificationChoiceState();
        terse.FillDefault(NotificationChoiceState.OptionA);
        var verbose = new NotificationChoiceState();
        verbose.FillDefault(NotificationChoiceState.OptionA);
        verbose.Set(fragChoice.ChoiceIdx, NotificationChoiceState.OptionB);

        var aTerse = NewPlayer("Terse");
        var aVerbose = NewPlayer("Verbose");
        // wire the per-recipient resolver the way GameWorld wires Commands.GetChoiceState.
        s.ChoiceStateProvider = p =>
            ReferenceEquals(p, aTerse) ? terse :
            ReferenceEquals(p, aVerbose) ? verbose : null;

        NotificationSystem.WarmupStage = true; // FRAG is challow=Warmup → the choice is honoured

        var v1 = NewPlayer("V1");
        var v2 = NewPlayer("V2");
        s.Register(aTerse); s.Register(aVerbose); s.Register(v1); s.Register(v2);

        FireDeath(v1, aTerse, DeathTypes.FromWeapon("vortex"));
        FireDeath(v2, aVerbose, DeathTypes.FromWeapon("vortex"));

        // each attacker received THEIR option …
        Assert.Contains(_rec.Log, d => d.Notification.RegistryName == "CENTER_DEATH_MURDER_FRAG" && ReferenceEquals(d.Target, aTerse));
        Assert.Contains(_rec.Log, d => d.Notification.RegistryName == "CENTER_DEATH_MURDER_FRAG_VERBOSE" && ReferenceEquals(d.Target, aVerbose));
        // … and NOT the other's.
        Assert.DoesNotContain(_rec.Log, d => d.Notification.RegistryName == "CENTER_DEATH_MURDER_FRAG_VERBOSE" && ReferenceEquals(d.Target, aTerse));
        Assert.DoesNotContain(_rec.Log, d => d.Notification.RegistryName == "CENTER_DEATH_MURDER_FRAG" && ReferenceEquals(d.Target, aVerbose));
    }

    [Fact]
    public void KillStreak_Announces_03_Then_05_At_The_Milestones()
    {
        var s = SubscribedScores(teamGame: false);
        var a = NewPlayer("Spree");
        s.Register(a);

        // 3 consecutive enemy frags (fresh victim each time) -> KILLSTREAK_03 on the 3rd.
        for (int i = 0; i < 3; i++)
        {
            var v = NewPlayer($"V{i}");
            s.Register(v);
            FireDeath(v, a, DeathTypes.FromWeapon("vortex"));
        }
        Assert.Contains(_rec.Log, d => d.Notification.RegistryName == "ANNCE_KILLSTREAK_03" && ReferenceEquals(d.Target, a));

        // up to 5 -> KILLSTREAK_05.
        for (int i = 3; i < 5; i++)
        {
            var v = NewPlayer($"V{i}");
            s.Register(v);
            FireDeath(v, a, DeathTypes.FromWeapon("vortex"));
        }
        Assert.Contains(_rec.Log, d => d.Notification.RegistryName == "ANNCE_KILLSTREAK_05");
        // no spurious milestone at counts between 3 and 5.
        Assert.DoesNotContain(_rec.Log, d => d.Notification.RegistryName == "ANNCE_KILLSTREAK_10");
    }

}
