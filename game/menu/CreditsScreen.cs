using Godot;
using System;

namespace VortexArena.Game.Menu;

/// <summary>
/// Credits screen — full port of the Xonotic credits roll from
/// <c>qcsrc/menu/xonotic/credits.qc</c>. Auto-scrolling list with pause/resume.
/// </summary>
public partial class CreditsScreen : MenuScreen
{
    private ScrollContainer _scroll = null!;
    private Button _autoButton = null!;
    private bool _autoScroll = true;
    private double _scrollAccumulator;

    private const float ScrollSpeed = 28f;

    private enum EntryKind { Title, Function }

    // Ported verbatim from credits.qc CREDITS() macro (keep in sync with xonotic.org/team).
    private static readonly (EntryKind Kind, string Section, string[] Names)[] Credits =
    {
        (EntryKind.Title, "Core Team", new[]
        {
            "Ant \"Antibody\" Zucaro",
            "Antonio \"terencehill\" Piu",
            "bones_was_here",
            "Merlijn Hofstra",
            "Rudolf \"divVerent\" Polzer",
            "Ruszkai \"CuBe0wL\" Ákos",
            "Tyler \"-z-\" Mulligan",
        }),
        (EntryKind.Title, "Extended Team", new[]
        {
            "AsciiWolf",
            "Dr. Jaska",
            "Freddy",
            "Halogene",
            "Jan \"zykure\" Behrens",
            "k9er",
            "Morosophos",
            "MrBougo",
            "nilyt/nyov",
            "Nitroxis",
            "packer",
            "Severin \"sev\" Meyer",
            "Thomas \"illwieckz\" Debesse",
            "Victor \"LegendGuard\" Jaume",
            "Yannick \"SpiKe\" Le Guen",
            "z411",
        }),
        (EntryKind.Function, "Website", new[]
        {
            "Ant \"Antibody\" Zucaro (web)",
            "Freddy",
            "Merlijn Hofstra",
            "Tyler \"-z-\" Mulligan (web / game)",
        }),
        (EntryKind.Function, "Stats", new[]
        {
            "Ant \"Antibody\" Zucaro",
            "Jan \"zykure\" Behrens",
        }),
        (EntryKind.Function, "Art", new[]
        {
            "KingPimpCommander",
            "Pearce \"theShadow\" Michal",
            "Peter \"Morphed\" Pielak",
            "Sahil \"DiaboliK\" Singhal",
            "Sam \"LJFHutch\" Hutchinson",
            "Severin \"sev\" Meyer",
        }),
        (EntryKind.Function, "Animation", new[]
        {
            "nifrek",
            "Sahil \"DiaboliK\" Singhal",
        }),
        (EntryKind.Function, "Campaign", new[]
        {
            "Dr. Jaska",
            "Marvin \"Mirio\" Beck",
            "Yannick \"SpiKe\" Le Guen",
        }),
        (EntryKind.Function, "Level Design", new[]
        {
            "Amadeusz \"amade/proraide\" Sławiński",
            "Ben \"MooKow\" Banker",
            "cityy",
            "Cortez",
            "Cuinn \"Cuinnton\" Herrick",
            "Debugger",
            "Hugo \"Calinou\" Locurcio",
            "Jakob \"tZork\" Markström Gröhn",
            "Konrad \"Justin\" Slawinski",
            "L0",
            "Łukasz \"kuniu the frogg\" Polek",
            "Maddin",
            "Maik \"SavageX\" Merten",
            "Marvin \"Mirio\" Beck",
            "MintOX",
            "packer",
            "Pearce \"theShadow\" Michal",
            "Rasmus \"FruitieX\" Eskola",
            "Ruszkai \"CuBe0wL\" Ákos",
            "Severin \"sev\" Meyer",
            "ShadoW",
            "t0uYK8Ne",
            "Yannick \"SpiKe\" Le Guen",
        }),
        (EntryKind.Function, "Music / Sound FX", new[]
        {
            "AquaNova (Archer)",
            "blkrbt",
            "chooksta",
            "Independent.nu",
            "Lea \"TheAudioMonkey\" Edwards",
            "[master]mind",
            "Merlijn Hofstra",
            "Mick Rippon",
            "Nick \"bitbomb\" Lucca",
            "remaxim",
            "Saulo \"mand1nga\" Gil",
            "{SC0RP} - Ian \"ID\" Dorrell",
            "Stephan",
            "unfa",
        }),
        (EntryKind.Function, "Game Code", new[]
        {
            "Antonio \"terencehill\" Piu",
            "bones_was_here",
            "Des",
            "Dr. Jaska",
            "Jakob \"tZork\" Markström Gröhn",
            "Juhu",
            "k9er",
            "martin-t",
            "Matthias \"matthiaskrgr\" Krüger",
            "Mattia \"Melanosuchus\" Basaglia",
            "Rasmus \"FruitieX\" Eskola",
            "Rudolf \"divVerent\" Polzer",
            "Samual \"Ares\" Lenks",
            "TimePath",
            "Victor \"LegendGuard\" Jaume",
            "z411",
            "Zac \"Mario\" Jardine",
        }),
        (EntryKind.Function, "Marketing / PR", new[]
        {
            "Ruszkai \"CuBe0wL\" Ákos",
            "Samual \"Ares\" Lenks",
            "Saulo \"mand1nga\" Gil",
            "Tyler \"-z-\" Mulligan",
        }),
        (EntryKind.Function, "Legal", new[]
        {
            "Merlijn Hofstra",
            "Rudolf \"divVerent\" Polzer",
        }),
        (EntryKind.Title, "Game Engine", Array.Empty<string>()),
        (EntryKind.Function, "DarkPlaces", new[]
        {
            "Ashley Rose \"LadyHavoc\" Hale",
        }),
        (EntryKind.Function, "Engine Additions", new[]
        {
            "bones_was_here",
            "David \"Cloudwalk\" Knapp",
            "Rudolf \"divVerent\" Polzer",
            "Samual \"Ares\" Lenks",
        }),
        (EntryKind.Title, "Compiler", Array.Empty<string>()),
        (EntryKind.Function, "GMQCC", new[]
        {
            "Dale \"graphitemaster\" Weiler",
            "Wolfgang \"Blub\\0\" Bumiller",
        }),
        (EntryKind.Title, "Translators", Array.Empty<string>()),
        (EntryKind.Function, "Asturian", new[]
        {
            "enolp",
            "Llumex03",
            "Tornes \"Tornes.Llume\" Ḷḷume",
            "Ximielga",
        }),
        (EntryKind.Function, "Belarusian", new[]
        {
            "Mihail \"meequz\" Varantsou",
            "Pavel \"Pashok11\" Mordachev",
        }),
        (EntryKind.Function, "Bulgarian", new[]
        {
            "Alexander \"alex4o\" Bonin",
            "DelianST",
            "ifohancroft",
            "Krasimir \"kmikov\" Mikov",
            "lokster",
            "Nik \"cozmo\" Dim",
            "set_killer",
            "ubone",
            "С Станев",
        }),
        (EntryKind.Function, "Chinese (China)", new[]
        {
            "Antonidas",
            "Armcoon",
            "CodingJellyfish",
            "EricChen1",
            "kalawore",
            "Largee",
            "Liang \"dxkliu\" Liu",
            "Losier \"losierb\" Blackheath",
            "Matthew \"wjjmatthew\" Wu",
            "moetale",
            "NaitLee",
            "Richard \"seedship\" Nai",
            "sapphireliu",
            "yujiff",
            "韬 \"jiegushijia\" 刘",
        }),
        (EntryKind.Function, "Chinese (Hong Kong)", new[]
        {
            "Antonidas",
            "CodingJellyfish",
            "Largee",
            "kalawore",
            "Liang \"dxkliu\" Liu",
            "Losier \"losierb\" Blackheath",
            "Matthew \"wjjmatthew\" Wu",
            "moetale",
            "NaitLee",
            "sapphireliu",
            "韬 \"jiegushijia\" 刘",
        }),
        (EntryKind.Function, "Chinese (Taiwan)", new[]
        {
            "Alisha",
            "Antonidas",
            "Armcoon",
            "CodingJellyfish",
            "EricChen1",
            "Jeff \"s8321414\" Huang",
            "Largee",
            "Liang \"dxkliu\" Liu",
            "Losier \"losierb\" Blackheath",
            "kalawore",
            "Matthew \"wjjmatthew\" Wu",
            "msn1018927464",
            "NaitLee",
            "sapphireliu",
            "Simon \"XMLSDK\" Chow",
            "韬 \"jiegushijia\" 刘",
            "黃柏諺",
        }),
        (EntryKind.Function, "Czech", new[]
        {
            "Adam \"Admi335\" Říha",
            "Adam \"SakDrakken\" Krasa",
            "Aleš \"ramses1\" Svoboda",
            "fasdasd \"kitfildom\" sdasd",
            "gamingforyou875",
            "Jan \"kockahonza\" Kocka",
            "Jiří \"Havro\" Vrána",
            "martin-t",
            "Martin Krámský",
            "shogun assassin/woky",
            "Superovoce",
            "Tomáš \"CZHeron\" Volavka",
            "woky",
        }),
        (EntryKind.Function, "Dutch", new[]
        {
            "Alexander \"freefang\" van Dam",
            "Contijn \"Sojiro84\" Buijs",
            "Joeke \"Mappack\" de Graaf",
            "Jonathan \"Jonakeys\" van der Steege",
            "joostruis",
            "PinkRobot",
            "vegiburger",
        }),
        (EntryKind.Function, "English (Australia)", new[]
        {
            "Ben Dundon",
            "k9er",
            "Laurene \"sunflowers\" Albrand",
            "Matthew \"wjjmatthew\" Wu",
            "Stuart \"Cefiar\" Young",
            "Zac \"Mario\" Jardine",
        }),
        (EntryKind.Function, "English (United Kingdom)", new[]
        {
            "arduinoisgreat",
            "k9er",
        }),
        (EntryKind.Function, "Finnish", new[]
        {
            "Dr. Jaska",
            "Heidi Wenger",
            "Henry \"Exitium\" Sanmark",
            "irisxerno",
            "Jaakko Saarikko",
            "Jonas \"PowaTree\" Sahlberg",
            "LINUX SAUNA",
            "Oftox",
            "Oi Suomi On!",
            "Rasmus \"FruitieX\" Eskola",
            "ZakkeX",
        }),
        (EntryKind.Function, "French", new[]
        {
            "_biloute",
            "Aodren \"Gwlanbzh\" Le Gloanec",
            "adrien \"VRad\" vigneron",
            "Adgenodux",
            "Gwlanbzh",
            "HelloWorld42404",
            "Hugo \"Calinou\" Locurcio",
            "Iso \"coughingmouse\" Lee",
            "Maxime \"Taximus\" Paradis",
            "Marvin \"Mirio\" Beck",
            "Nicolas \"signed\" Formichella",
            "RedGuff",
            "Thomas \"illwieckz\" Debesse",
            "Yannick \"SpiKe\" Le Guen",
        }),
        (EntryKind.Function, "Galician", new[]
        {
            "Nin \"ninjum\" Him",
            "Victor \"LegendGuard\" Jaume",
        }),
        (EntryKind.Function, "German", new[]
        {
            "Alex \"tiprogrammierer.alex\" Progger",
            "BL4NKY",
            "cvcxc",
            "diacriticalhit",
            "Erik \"Ablu\" Schilling",
            "Iwan \"qubodup\" Gabovitch",
            "Jope \"Sless\" Withers",
            "Larson \"skps\" März",
            "Logan \"norsvenska\" Zerfass",
            "Markus \"Skoppes\" Erhard",
            "Paul \"Snapper\"",
            "Robert \"HbmMods\" Katzinsky",
            "Rudolf \"divVerent\" Polzer",
            "TheTrueBrot",
            "Wuzzy",
            "Yepoleb",
        }),
        (EntryKind.Function, "Greek", new[]
        {
            "Αντώνιος \"antonis97apple\" Τσίγκας",
            "Γιώργος Καρδάμης",
            "Hector \"The_Smasher_1992\" Champipis",
            "Konstantinos \"LDinos\" Mihalenas",
            "MasterWord",
            "Marinus \"Savvoritias\" Savoritias",
            "Mensious",
            "Pandelis \"pandem6nium\" Biltiroglou",
            "Vindex",
            "Yannis \"Evropi\" Anthymidis",
        }),
        (EntryKind.Function, "Hungarian", new[]
        {
            "Ács \"acszoltan111\" Zoltán",
            "Ákos Ruszkai",
            "Barnabás \"lordgalimow\" Klemens",
            "MmAaXx500",
            "Peter \"fpeterhu\" Ferenczy",
            "Ruszkai \"CuBe0wL\" Ákos",
            "Titusz \"diduuz\" Érsek",
            "Rob \"xaN1C4n3\"",
            "Zsolt \"Yellowberry\" Zitting",
        }),
        (EntryKind.Function, "Indonesian", new[]
        {
            "Angeline Meilia",
            "Ariesandy Hidayat",
            "red koala",
        }),
        (EntryKind.Function, "Irish", new[]
        {
            "Kevin \"kscanne\" Scannell",
        }),
        (EntryKind.Function, "Italian", new[]
        {
            "amedeo463",
            "Antonio \"terencehill\" Piu",
            "Costa",
            "Felice \"MaidenBeast\" Sallustio",
            "Giovanni \"rizzogianni73\" Rizzello",
            "Jessica Amoruso",
            "stdi",
            "XCostaX",
        }),
        (EntryKind.Function, "Japanese", new[]
        {
            "Antoni Das",
            "Lento",
            "Space Ace",
            "Ryu \"ryusho2523\" N.",
            "Victor \"LegendGuard\" Jaume",
            "z411",
            "堀川 \"bapuru524\" 健康",
        }),
        (EntryKind.Function, "Kazakh", new[]
        {
            "Артем \"bystrov.arterm\" Быстров",
        }),
        (EntryKind.Function, "Korean", new[]
        {
            "BYEONGJIN \"ahnkoon\" AN",
            "Jisoo \"LimJiSoo0719\" Lim",
            "Iso \"coughingmouse\" Lee",
            "Seokho Son",
        }),
        (EntryKind.Function, "Latin", new[]
        {
            "oblector o",
        }),
        (EntryKind.Function, "Polish", new[]
        {
            "4m",
            "Alex \"tiprogrammierer.alex\" Progger",
            "Amadeusz \"amade/proraide\" Sławiński",
            "Artur \"artur9010\" Motyka",
            "Cris \"Kshyso\" Sus",
            "David Agzo",
            "Eryk \"ewm\" Michalak",
            "Jakub \"_Mpcs\" Niklas",
            "Jakub \"KubeQ11\" Pędziszewski",
            "John \"Myran\" Smith",
            "Kacper \"kacperski1\" Herchel",
            "Karol \"kRkk\" Kosek",
            "Kriss \"Kriss7475\" Chr",
            "Lukasz Sekalski",
            "Marcin \"mpnogaj\" Nogaj",
            "Oliwier Jaszczyszyn",
            "Paweł \"siwyi\" Goca",
            "Piotr \"vipkoza\" Kozica",
            "qqiLMFjv1iBfT3p6TNxjUThsaTVIXhQc",
            "Rafał \"Okava\" Szymański",
            "Robert \"Szkodnix\" Wolniak",
            "Saikuru \"saikuru0\" Zero",
            "Sertomas",
            "SFS Atlas",
            "tasopis",
            "theQuos",
            "Wojciech \"g_host\" Sikora",
        }),
        (EntryKind.Function, "Portuguese", new[]
        {
            "Ivan Paulos \"greylica\" Tomé",
            "Jean Trindade \"Muleke_Trairao\" Pereira",
            "lecalam",
            "NotThatPrivate",
            "Pedrada19",
            "Ricardo Manuel \"hellgardia\" da Cruz Coelho da Silva",
            "Rui \"xendez\"",
            "xXxCHAOTICxXx",
        }),
        (EntryKind.Function, "Portuguese (Brazil)", new[]
        {
            "Arno \"Bleyom\" Heinrich",
            "Ivan Paulos \"greylica\" Tomé",
            "Jean Trindade \"Muleke_Trairao\" Pereira",
            "NotThatPrivate",
            "Ricardo Manuel \"hellgardia\" da Cruz Coelho da Silva",
            "Rui \"xendez\"",
            "yy0zz",
            "zerowhy",
        }),
        (EntryKind.Function, "Romanian", new[]
        {
            "Adrian-Ciprian \"adrian.tinjala\" Tînjală",
            "busterdbk",
            "Cuzenco \"andonis1616\" Andrei Robert",
            "Daniel \"dasj\" Șerbănescu",
            "Edward205",
            "Iulian \"ElektroBoom\" Oancea",
            "Mircea \"Taoki\" Kitsune",
            "Sorin \"unic_sorin\" Botirla",
            "Tudor \"TropiKo\" Ionel",
        }),
        (EntryKind.Function, "Russian", new[]
        {
            "Alex \"alextalker7\" Talker",
            "Alexandr \"zrg\"",
            "Alexei \"PlasmaSheep\" B.",
            "Andrei \"adem4ik\" Stepanov",
            "Andrey \"dekrY\" P.",
            "Artem \"skybon\" Vorotnikov",
            "Александр ABATAPA",
            "Артём \"Temak\" Котлубай",
            "Blueberryy",
            "Dmitro \"Gamebot\" Sokhin",
            "gravicappa",
            "HelloWorld42404",
            "Hot Dog",
            "jusio",
            "Lord Canistra",
            "Mikita \"rudzik8\" Wiśniewski",
            "Morosophos",
            "Nikoli",
            "Pavel \"Pashok11\" Mordachev",
            "Plato \"SemperPeritus\" Efimov",
            "Sergej \"Clearness High\" Lutsyk",
            "Simple88",
            "Темак",
            "Пидарасенька",
        }),
        (EntryKind.Function, "Serbian", new[]
        {
            "Саша \"salepetronije\" Петровић",
            "Marko M. Kostić",
            "Nikola \"asd222\" Dundjerski",
            "Pendulla",
            "Rafael \"Ristovski\"",
        }),
        (EntryKind.Function, "Spanish", new[]
        {
            "0000simon",
            "Alan \"aagp\" Garcia",
            "Antonio \"Antoniosirc\" Sirera",
            "Ari_tent",
            "Belén \"BelenVM\" Velasco",
            "brunodeleo",
            "Damian \"starfire24680\" Kurek",
            "Excruciatus \"crucesignatus\" X",
            "Juan \"Perju\" Perez",
            "Kammy",
            "Lorenzo \"lololailo\" Soriano",
            "Luciano \"NeonKnightOA\" Balducchi",
            "roader_gentoo",
            "Rodrigo Mouton Laudin",
            "Roi Asher Gerszkoviez",
            "SouL",
            "Starfire24680",
            "Victor \"LegendGuard\" Jaume",
            "Vitama Piru Leta",
            "Yllelder",
            "Yotta Mxt",
            "z411",
        }),
        (EntryKind.Function, "Swedish", new[]
        {
            "Gustaf \"Hanicef\" Alhäll",
            "Karl-Oskar \"machine\" Rikås",
            "Logan \"norsvenska\" Zerfass",
            "marcus256",
            "Hampus \"xunz\" Kreitz",
        }),
        (EntryKind.Function, "Turkish", new[]
        {
            "Abdurrahman \"akkus12345\" AKKUŞ",
            "aggbhh20",
            "Ahmet \"ahmetlii\"",
            "Çağlar \"caglarturali\" Turalı",
            "Bekir \"bkrucarci\"",
            "Demiray \"tulliana\" Muhterem",
            "Efeospt \"Efeisot\" KMR",
            "Gokdeniz.Kucukali",
            "ibra kap",
            "Lucifer \"Lucifer25x\" Morningstar",
            "Mehmet Ali \"bluedream1381\" Kaplan",
            "Tan Siret \"yutyocraft\" Akıncı",
            "xe1st",
        }),
        (EntryKind.Function, "Ukrainian", new[]
        {
            "BakerDoge",
            "Dmitro \"Gamebot\" Sokhin",
            "Ihor \"iRomanyshyn\" Romanyshyn",
            "Ihor \"uandreew\" Andreev",
            "Illia \"imbirWIthSugar\" Serediuk",
            "Oleh \"BlaXpirit\" Prypin",
            "Vasyl \"CHUVACK\" Kushniruk",
            "Vasyl \"Harmata\" Melnyk",
            "Yuriy \"herrniemand\" Ackermann",
        }),
        (EntryKind.Title, "Past Contributors", new[]
        {
            "Akari",
            "Alexander \"motorsep\" Zubov",
            "Alexander \"naryl\" Suhoverhov",
            "Amos \"torus\" Dudley",
            "Andreas \"Black\" Kirsch",
            "Attila \"WW3\" Houtkooper",
            "BigMac",
            "Braden \"meoblast001\" Walters",
            "Brain Younds",
            "BuddyFriendGuy",
            "Chris \"amethyst7\" Matz",
            "Christian Ice",
            "Clinton \"Kaziganthe\" Freeman",
            "Dan \"Digger\" Korostelev",
            "Dan \"Wazat\" Hale",
            "Diomedes",
            "Dokujisan",
            "Donkey",
            "dstrek",
            "Dustin Geeraert",
            "Edgenetwork",
            "Edward \"Ed\" Holness",
            "Eric \"Munyul Verminard\" Sambach",
            "Erik \"Ablu\" Schilling",
            "Fabien \"H. Reaper\" Tschirhart",
            "Florian Paul \"lda17h\" Schmidt",
            "FrikaC",
            "Garth \"Zombie\" Hendy",
            "GATTS",
            "Gerd \"Elysis\" Raudenbusch",
            "Gottfried \"Toddd\" Hofmann",
            "Henning \"Tymo\" Janssen",
            "Innovati",
            "Jeff",
            "JH0nny",
            "Jitspoe",
            "Jody Gallagher",
            "Jope \"Sless\" Withers",
            "Jubilant",
            "Juergen \"LowDragon\" Timm",
            "KadaverJack",
            "Kevin \"Tyrann\" Shanahan",
            "Kristian \"morfar\" Johansson",
            "Kurt Dereli",
            "lcatlnx",
            "Lee David Ash",
            "Lee Vermeulen",
            "leileilol",
            "Lyberta",
            "magorian",
            "Marius \"GreEn`mArine\" Shekow",
            "Marko \"Urre\" Permanto",
            "Marvin \"Mirio\" Beck",
            "Mathieu \"Elric\" Olivier",
            "Mattrew \"Tronyn\" Rye",
            "MauveBib",
            "Mephisto",
            "Mepper",
            "michaelb",
            "Michael \"Tenshihan\" Quinn",
            "Mircea \"Taoki\" Kitsune",
            "Munyul",
            "Netzwerg",
            "NoelCower",
            "Oleh \"BlaXpirit\" Prypin",
            "Parapraxis",
            "parasti",
            "Paul Scott",
            "Paul \"Strahlemann\" Evers",
            "Penguinum",
            "Petithomme",
            "PlasmaSheep",
            "Przemysław \"atheros\" Grzywacz",
            "Q1 Retexturing Project",
            "Qantourisc",
            "Rick \"Rat\" Kelley",
            "Robert \"ai\" Kuroto",
            "Ronan",
            "Sajt",
            "Samual \"Ares\" Lenks",
            "Saulo \"mand1nga\" Gil",
            "Shaggy",
            "Shank",
            "s1lence",
            "Simon O’Callaghan",
            "slava",
            "Soelen",
            "SomeGuy",
            "SoulKeeper_p",
            "Spike",
            "Spirit",
            "Stephan \"esteel\" Stahl",
            "Steve Vermeulen",
            "Supajoe",
            "Sydes",
            "Tei",
            "The player with the unnecessarily long name",
            "Tomaz",
            "Ulrich Galbraith",
            "Vortex",
            "William Libert",
            "William \"Willis\" Weilep",
            "Yves \"EviLair\" Allaire",
            "Zac \"Mario\" Jardine",
            "Zenex",
            "... and a goat",
        }),
    };

    protected override void BuildUi()
    {
        Name = "CreditsScreen";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 16);
        margin.AddChild(root);

        if (!HostProvidesTitle) root.AddChild(MakeTitle("Credits"));

        _scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        _scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        root.AddChild(_scroll);

        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 10);
        _scroll.AddChild(list);

        list.AddChild(new Control { CustomMinimumSize = new Vector2(0, 24) });

        bool first = true;
        foreach (var (kind, section, names) in Credits)
        {
            if (kind == EntryKind.Title)
            {
                if (!first)
                    list.AddChild(new Control { CustomMinimumSize = new Vector2(0, 12) });

                var title = MakeTitle(section);
                title.HorizontalAlignment = HorizontalAlignment.Center;
                list.AddChild(title);
            }
            else
            {
                var header = MakeHeader(section);
                header.HorizontalAlignment = HorizontalAlignment.Center;
                list.AddChild(header);
            }

            foreach (var name in names)
            {
                var line = MakeLabel(name);
                line.HorizontalAlignment = HorizontalAlignment.Center;
                list.AddChild(line);
            }

            list.AddChild(new Control { CustomMinimumSize = new Vector2(0, 18) });
            first = false;
        }

        _autoButton = MakeButton("Pause scroll", OnToggleAuto);
        root.AddChild(MakeButtonBar(_autoButton, MakeButton("Back", GoBack)));
    }

    public override void _Process(double delta)
    {
        if (!_autoScroll || _scroll is null)
            return;

        int max = (int)_scroll.GetVScrollBar().MaxValue - (int)_scroll.Size.Y;
        if (max <= 0)
            return;

        _scrollAccumulator += ScrollSpeed * delta;
        if (_scrollAccumulator >= 1.0)
        {
            int step = (int)_scrollAccumulator;
            _scrollAccumulator -= step;
            int next = _scroll.ScrollVertical + step;
            if (next >= max)
            {
                next = 0;
                _scrollAccumulator = 0;
            }
            _scroll.ScrollVertical = next;
        }
    }

    private void OnToggleAuto()
    {
        _autoScroll = !_autoScroll;
        _autoButton.Text = _autoScroll ? "Pause scroll" : "Resume scroll";
    }
}
