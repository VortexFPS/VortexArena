// Port of Base/darkplaces/keys.c keynames[], Key_StringToKeynum, Key_KeynumToString, Key_GetBind and
// Key_FindKeysForCommand, with the key numbers of keys.h keynum_t.
using System.Text;

namespace VortexArena.Legacy.Csqc;

/// <summary>
/// DarkPlaces' key numbering and key names. The client program stores key numbers (in binds it draws,
/// in the input events it is sent) and converts them with keynumtostring / stringtokeynum, so both
/// directions have to agree with the engine the program was written for, not with Godot's key codes.
/// </summary>
public static class CsqcKeys
{
    /// <summary>MAX_KEYS (keys.h MAX_KEY_BINDS).</summary>
    public const int MaxKeys = 0xAC00;
    /// <summary>MAX_BINDMAPS (qdefs.h).</summary>
    public const int MaxBindMaps = 8;

    /// <summary>keynames[], in the C's order. A key number can appear twice (KP_INS and KP_0); the
    /// first entry is the one keynumtostring answers with.</summary>
    public static readonly (string Name, int Key)[] Names =
    {
        ("TAB", 9), ("ENTER", 13), ("ESCAPE", 27), ("SPACE", 32), ("BACKSPACE", 127), ("UPARROW", 128), ("DOWNARROW", 129),
        ("LEFTARROW", 130), ("RIGHTARROW", 131), ("ALT", 132), ("CTRL", 133), ("SHIFT", 134), ("F1", 135), ("F2", 136),
        ("F3", 137), ("F4", 138), ("F5", 139), ("F6", 140), ("F7", 141), ("F8", 142), ("F9", 143), ("F10", 144),
        ("F11", 145), ("F12", 146), ("INS", 147), ("DEL", 148), ("PGDN", 149), ("PGUP", 150), ("HOME", 151), ("END", 152),
        ("PAUSE", 153), ("NUMLOCK", 154), ("CAPSLOCK", 155), ("SCROLLOCK", 156), ("KP_INS", 157), ("KP_0", 157),
        ("KP_END", 158), ("KP_1", 158), ("KP_DOWNARROW", 159), ("KP_2", 159), ("KP_PGDN", 160), ("KP_3", 160),
        ("KP_LEFTARROW", 161), ("KP_4", 161), ("KP_5", 162), ("KP_RIGHTARROW", 163), ("KP_6", 163), ("KP_HOME", 164),
        ("KP_7", 164), ("KP_UPARROW", 165), ("KP_8", 165), ("KP_PGUP", 166), ("KP_9", 166), ("KP_DEL", 167),
        ("KP_PERIOD", 167), ("KP_SLASH", 168), ("KP_DIVIDE", 168), ("KP_MULTIPLY", 169), ("KP_MINUS", 170), ("KP_PLUS", 171),
        ("KP_ENTER", 172), ("KP_EQUALS", 173), ("PRINTSCREEN", 174), ("MOUSE1", 512), ("MOUSE2", 513), ("MOUSE3", 514),
        ("MWHEELUP", 515), ("MWHEELDOWN", 516), ("MOUSE4", 517), ("MOUSE5", 518), ("MOUSE6", 519), ("MOUSE7", 520),
        ("MOUSE8", 521), ("MOUSE9", 522), ("MOUSE10", 523), ("MOUSE11", 524), ("MOUSE12", 525), ("MOUSE13", 526),
        ("MOUSE14", 527), ("MOUSE15", 528), ("MOUSE16", 529), ("JOY1", 768), ("JOY2", 769), ("JOY3", 770), ("JOY4", 771),
        ("JOY5", 772), ("JOY6", 773), ("JOY7", 774), ("JOY8", 775), ("JOY9", 776), ("JOY10", 777), ("JOY11", 778),
        ("JOY12", 779), ("JOY13", 780), ("JOY14", 781), ("JOY15", 782), ("JOY16", 783), ("AUX1", 784), ("AUX2", 785),
        ("AUX3", 786), ("AUX4", 787), ("AUX5", 788), ("AUX6", 789), ("AUX7", 790), ("AUX8", 791), ("AUX9", 792),
        ("AUX10", 793), ("AUX11", 794), ("AUX12", 795), ("AUX13", 796), ("AUX14", 797), ("AUX15", 798), ("AUX16", 799),
        ("AUX17", 800), ("AUX18", 801), ("AUX19", 802), ("AUX20", 803), ("AUX21", 804), ("AUX22", 805), ("AUX23", 806),
        ("AUX24", 807), ("AUX25", 808), ("AUX26", 809), ("AUX27", 810), ("AUX28", 811), ("AUX29", 812), ("AUX30", 813),
        ("AUX31", 814), ("AUX32", 815), ("X360_DPAD_UP", 816), ("X360_DPAD_DOWN", 817), ("X360_DPAD_LEFT", 818),
        ("X360_DPAD_RIGHT", 819), ("X360_START", 820), ("X360_BACK", 821), ("X360_LEFT_THUMB", 822),
        ("X360_RIGHT_THUMB", 823), ("X360_LEFT_SHOULDER", 824), ("X360_RIGHT_SHOULDER", 825), ("X360_A", 826),
        ("X360_B", 827), ("X360_X", 828), ("X360_Y", 829), ("X360_LEFT_TRIGGER", 830), ("X360_RIGHT_TRIGGER", 831),
        ("X360_LEFT_THUMB_UP", 832), ("X360_LEFT_THUMB_DOWN", 833), ("X360_LEFT_THUMB_LEFT", 834),
        ("X360_LEFT_THUMB_RIGHT", 835), ("X360_RIGHT_THUMB_UP", 836), ("X360_RIGHT_THUMB_DOWN", 837),
        ("X360_RIGHT_THUMB_LEFT", 838), ("X360_RIGHT_THUMB_RIGHT", 839), ("JOY_UP", 840), ("JOY_DOWN", 841),
        ("JOY_LEFT", 842), ("JOY_RIGHT", 843), ("SEMICOLON", 59), ("TILDE", 126), ("BACKQUOTE", 96), ("QUOTE", 34),
        ("APOSTROPHE", 39), ("BACKSLASH", 92), ("MIDINOTE0", 896), ("MIDINOTE1", 897), ("MIDINOTE2", 898),
        ("MIDINOTE3", 899), ("MIDINOTE4", 900), ("MIDINOTE5", 901), ("MIDINOTE6", 902), ("MIDINOTE7", 903),
        ("MIDINOTE8", 904), ("MIDINOTE9", 905), ("MIDINOTE10", 906), ("MIDINOTE11", 907), ("MIDINOTE12", 908),
        ("MIDINOTE13", 909), ("MIDINOTE14", 910), ("MIDINOTE15", 911), ("MIDINOTE16", 912), ("MIDINOTE17", 913),
        ("MIDINOTE18", 914), ("MIDINOTE19", 915), ("MIDINOTE20", 916), ("MIDINOTE21", 917), ("MIDINOTE22", 918),
        ("MIDINOTE23", 919), ("MIDINOTE24", 920), ("MIDINOTE25", 921), ("MIDINOTE26", 922), ("MIDINOTE27", 923),
        ("MIDINOTE28", 924), ("MIDINOTE29", 925), ("MIDINOTE30", 926), ("MIDINOTE31", 927), ("MIDINOTE32", 928),
        ("MIDINOTE33", 929), ("MIDINOTE34", 930), ("MIDINOTE35", 931), ("MIDINOTE36", 932), ("MIDINOTE37", 933),
        ("MIDINOTE38", 934), ("MIDINOTE39", 935), ("MIDINOTE40", 936), ("MIDINOTE41", 937), ("MIDINOTE42", 938),
        ("MIDINOTE43", 939), ("MIDINOTE44", 940), ("MIDINOTE45", 941), ("MIDINOTE46", 942), ("MIDINOTE47", 943),
        ("MIDINOTE48", 944), ("MIDINOTE49", 945), ("MIDINOTE50", 946), ("MIDINOTE51", 947), ("MIDINOTE52", 948),
        ("MIDINOTE53", 949), ("MIDINOTE54", 950), ("MIDINOTE55", 951), ("MIDINOTE56", 952), ("MIDINOTE57", 953),
        ("MIDINOTE58", 954), ("MIDINOTE59", 955), ("MIDINOTE60", 956), ("MIDINOTE61", 957), ("MIDINOTE62", 958),
        ("MIDINOTE63", 959), ("MIDINOTE64", 960), ("MIDINOTE65", 961), ("MIDINOTE66", 962), ("MIDINOTE67", 963),
        ("MIDINOTE68", 964), ("MIDINOTE69", 965), ("MIDINOTE70", 966), ("MIDINOTE71", 967), ("MIDINOTE72", 968),
        ("MIDINOTE73", 969), ("MIDINOTE74", 970), ("MIDINOTE75", 971), ("MIDINOTE76", 972), ("MIDINOTE77", 973),
        ("MIDINOTE78", 974), ("MIDINOTE79", 975), ("MIDINOTE80", 976), ("MIDINOTE81", 977), ("MIDINOTE82", 978),
        ("MIDINOTE83", 979), ("MIDINOTE84", 980), ("MIDINOTE85", 981), ("MIDINOTE86", 982), ("MIDINOTE87", 983),
        ("MIDINOTE88", 984), ("MIDINOTE89", 985), ("MIDINOTE90", 986), ("MIDINOTE91", 987), ("MIDINOTE92", 988),
        ("MIDINOTE93", 989), ("MIDINOTE94", 990), ("MIDINOTE95", 991), ("MIDINOTE96", 992), ("MIDINOTE97", 993),
        ("MIDINOTE98", 994), ("MIDINOTE99", 995), ("MIDINOTE100", 996), ("MIDINOTE101", 997), ("MIDINOTE102", 998),
        ("MIDINOTE103", 999), ("MIDINOTE104", 1000), ("MIDINOTE105", 1001), ("MIDINOTE106", 1002), ("MIDINOTE107", 1003),
        ("MIDINOTE108", 1004), ("MIDINOTE109", 1005), ("MIDINOTE110", 1006), ("MIDINOTE111", 1007), ("MIDINOTE112", 1008),
        ("MIDINOTE113", 1009), ("MIDINOTE114", 1010), ("MIDINOTE115", 1011), ("MIDINOTE116", 1012), ("MIDINOTE117", 1013),
        ("MIDINOTE118", 1014), ("MIDINOTE119", 1015), ("MIDINOTE120", 1016), ("MIDINOTE121", 1017), ("MIDINOTE122", 1018),
        ("MIDINOTE123", 1019), ("MIDINOTE124", 1020), ("MIDINOTE125", 1021), ("MIDINOTE126", 1022), ("MIDINOTE127", 1023),
    };

    private static readonly Dictionary<string, int> ByName = BuildByName();

    private static Dictionary<string, int> BuildByName()
    {
        Dictionary<string, int> map = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, int key) in Names) map.TryAdd(name, key);
        return map;
    }

    private static readonly Dictionary<int, string> ByKey = BuildByKey();

    private static Dictionary<int, string> BuildByKey()
    {
        Dictionary<int, string> map = new();
        foreach ((string name, int key) in Names) map.TryAdd(key, name);
        return map;
    }

    /// <summary>Key_StringToKeynum: -1 for an empty or unknown name; a single character is its own
    /// (lower-cased) code; otherwise a name from the table, or one Unicode character.</summary>
    public static int StringToKeynum(string text)
    {
        if (string.IsNullOrEmpty(text)) return -1;
        // "if (!str[1]) return tolower(str[0])" works on bytes: a one-byte string. A single non-ASCII
        // character is several bytes in the C and falls through to the Unicode case below.
        if (text.Length == 1 && text[0] < 0x80) return char.ToLowerInvariant(text[0]);
        if (ByName.TryGetValue(text, out int key)) return key;

        // u8_getnchar(str, &str, 3): one character of at most three UTF-8 bytes, and nothing after it.
        if (!Rune.TryGetRuneAt(text, 0, out Rune rune) || rune.Value == 0 || rune.Utf8SequenceLength > 3) return -1;
        return rune.Utf16SequenceLength == text.Length ? rune.Value : -1;
    }

    /// <summary>Key_KeynumToString.</summary>
    public static string KeynumToString(int key)
    {
        if (key < 0) return "<KEY NOT FOUND>";
        if (ByKey.TryGetValue(key, out string? name)) return name;
        if (key > 32)
        {
            // tinystr holds a character of at most three UTF-8 bytes (TINYSTR_LEN 4 with the terminator).
            if (key > 0xFFFF || (key >= 0xD800 && key <= 0xDFFF)) return "<KEY NOT FOUND>";
            return ((char)key).ToString();
        }
        return "<UNKNOWN KEYNUM>";
    }
}
