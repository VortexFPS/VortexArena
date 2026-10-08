// Port of Base/darkplaces/mvm_cmds.c vm_m_builtins[] (the numbering) and vm_m_extensions[].
namespace VortexArena.Legacy.Menu;

/// <summary>
/// The menu program's builtin table: which C function DarkPlaces puts at which number.
///
/// The numbers are NOT the client program's. The two tables share most of their functions - VM_ftos,
/// VM_drawpic, VM_cvar_string are one piece of C each - but the menu table is older and was never
/// renumbered: VM_ftos is #26 for a client program and #17 here, VM_drawpic is #322 there and #456
/// here, and #4 is setsize for a client program and print for the menu. A menu.dat compiled against
/// this table and run against the client's would call setorigin when it meant error.
///
/// The table is kept as (number, C function name) so that it can be compared, entry for entry, with
/// one generated from the C source (MenuBuiltinTableTests does that when the reference checkout is
/// there). The entries inside "#if 0" in the C - the model-rendering builtins "deactivated until
/// someone has time to do it right" - are NULL there and absent here.
/// </summary>
public static class MenuBuiltinTable
{
    /// <summary>vm_m_builtins[]: every non-NULL entry, in order.</summary>
    public static readonly (int Number, string Function)[] Entries =
    {
        (1, "VM_checkextension"), (2, "VM_error"), (3, "VM_objerror"), (4, "VM_print"), (5, "VM_bprint"), (6, "VM_sprint"),
        (7, "VM_centerprint"), (8, "VM_normalize"), (9, "VM_vlen"), (10, "VM_vectoyaw"), (11, "VM_vectoangles"),
        (12, "VM_random"), (13, "VM_localcmd"), (14, "VM_cvar"), (15, "VM_cvar_set"), (16, "VM_dprint"), (17, "VM_ftos"),
        (18, "VM_fabs"), (19, "VM_vtos"), (20, "VM_etos"), (21, "VM_stof"), (22, "VM_spawn"), (23, "VM_remove"),
        (24, "VM_find"), (25, "VM_findfloat"), (26, "VM_findchain"), (27, "VM_findchainfloat"), (28, "VM_precache_file"),
        (29, "VM_precache_sound"), (30, "VM_coredump"), (31, "VM_traceon"), (32, "VM_traceoff"), (33, "VM_eprint"),
        (34, "VM_rint"), (35, "VM_floor"), (36, "VM_ceil"), (37, "VM_nextent"), (38, "VM_sin"), (39, "VM_cos"),
        (40, "VM_sqrt"), (41, "VM_randomvec"), (42, "VM_registercvar"), (43, "VM_min"), (44, "VM_max"), (45, "VM_bound"),
        (46, "VM_pow"), (47, "VM_M_copyentity"), (48, "VM_fopen"), (49, "VM_fclose"), (50, "VM_fgets"), (51, "VM_fputs"),
        (52, "VM_strlen"), (53, "VM_strcat"), (54, "VM_substring"), (55, "VM_stov"), (56, "VM_strzone"),
        (57, "VM_strunzone"), (58, "VM_tokenize"), (59, "VM_argv"), (60, "VM_isserver"), (61, "VM_clientcount"),
        (62, "VM_clientstate"), (64, "VM_changelevel"), (65, "VM_localsound"), (66, "VM_M_getmousepos"),
        (67, "VM_gettime"), (68, "VM_loadfromdata"), (69, "VM_loadfromfile"), (70, "VM_modulo"), (71, "VM_cvar_string"),
        (72, "VM_crash"), (73, "VM_stackdump"), (74, "VM_search_begin"), (75, "VM_search_end"), (76, "VM_search_getsize"),
        (77, "VM_search_getfilename"), (78, "VM_chr"), (79, "VM_itof"), (80, "VM_ftoe"), (81, "VM_itof"),
        (82, "VM_altstr_count"), (83, "VM_altstr_prepare"), (84, "VM_altstr_get"), (85, "VM_altstr_set"),
        (86, "VM_altstr_ins"), (87, "VM_findflags"), (88, "VM_findchainflags"), (89, "VM_cvar_defstring"),
        (221, "VM_strstrofs"), (222, "VM_str2chr"), (223, "VM_chr2str"), (224, "VM_strconv"), (225, "VM_strpad"),
        (226, "VM_infoadd"), (227, "VM_infoget"), (228, "VM_strncmp"), (229, "VM_strncasecmp"), (230, "VM_strncasecmp"),
        (340, "VM_keynumtostring"), (341, "VM_stringtokeynum"), (342, "VM_getkeybind"), (349, "VM_CL_isdemo"),
        (352, "VM_M_registercommand"), (353, "VM_wasfreed"), (355, "VM_CL_videoplaying"), (356, "VM_findfont"),
        (357, "VM_loadfont"), (401, "VM_M_WriteByte"), (402, "VM_M_WriteChar"), (403, "VM_M_WriteShort"),
        (404, "VM_M_WriteLong"), (405, "VM_M_WriteAngle"), (406, "VM_M_WriteCoord"), (407, "VM_M_WriteString"),
        (408, "VM_M_WriteEntity"), (440, "VM_buf_create"), (441, "VM_buf_del"), (442, "VM_buf_getsize"),
        (443, "VM_buf_copy"), (444, "VM_buf_sort"), (445, "VM_buf_implode"), (446, "VM_bufstr_get"),
        (447, "VM_bufstr_set"), (448, "VM_bufstr_add"), (449, "VM_bufstr_free"), (451, "VM_iscachedpic"),
        (452, "VM_precache_pic"), (453, "VM_freepic"), (454, "VM_drawcharacter"), (455, "VM_drawstring"),
        (456, "VM_drawpic"), (457, "VM_drawfill"), (458, "VM_drawsetcliparea"), (459, "VM_drawresetcliparea"),
        (460, "VM_getimagesize"), (461, "VM_cin_open"), (462, "VM_cin_close"), (463, "VM_cin_setstate"),
        (464, "VM_cin_getstate"), (465, "VM_cin_restart"), (466, "VM_drawline"), (467, "VM_drawcolorcodedstring"),
        (468, "VM_stringwidth"), (469, "VM_drawsubpic"), (470, "VM_drawrotpic"), (471, "VM_asin"), (472, "VM_acos"),
        (473, "VM_atan"), (474, "VM_atan2"), (475, "VM_tan"), (476, "VM_strlennocol"), (477, "VM_strdecolorize"),
        (478, "VM_strftime"), (479, "VM_tokenizebyseparator"), (480, "VM_strtolower"), (481, "VM_strtoupper"),
        (484, "VM_strreplace"), (485, "VM_strireplace"), (487, "VM_gecko_create"), (488, "VM_gecko_destroy"),
        (489, "VM_gecko_navigate"), (490, "VM_gecko_keyevent"), (491, "VM_gecko_movemouse"), (492, "VM_gecko_resize"),
        (493, "VM_gecko_get_texture_extent"), (494, "VM_crc16"), (495, "VM_cvar_type"), (496, "VM_numentityfields"),
        (497, "VM_entityfieldname"), (498, "VM_entityfieldtype"), (499, "VM_getentityfieldstring"),
        (500, "VM_putentityfieldstring"), (503, "VM_whichpack"), (510, "VM_uri_escape"), (511, "VM_uri_unescape"),
        (512, "VM_etof"), (513, "VM_uri_get"), (514, "VM_tokenize_console"), (515, "VM_argv_start_index"),
        (516, "VM_argv_end_index"), (517, "VM_buf_cvarlist"), (518, "VM_cvar_description"), (532, "VM_log"),
        (533, "VM_getsoundtime"), (534, "VM_soundlength"), (535, "VM_buf_loadfile"), (536, "VM_buf_writefile"),
        (537, "VM_bufstr_find"), (538, "VM_matchpattern"), (601, "VM_M_setkeydest"), (602, "VM_M_getkeydest"),
        (603, "VM_M_setmousetarget"), (604, "VM_M_getmousetarget"), (605, "VM_callfunction"), (606, "VM_writetofile"),
        (607, "VM_isfunction"), (608, "VM_M_getresolution"), (609, "VM_keynumtostring"), (610, "VM_findkeysforcommand"),
        (611, "VM_M_getserverliststat"), (612, "VM_M_getserverliststring"), (613, "VM_parseentitydata"),
        (614, "VM_stringtokeynum"), (615, "VM_M_resetserverlistmasks"), (616, "VM_M_setserverlistmaskstring"),
        (617, "VM_M_setserverlistmasknumber"), (618, "VM_M_resortserverlist"), (619, "VM_M_setserverlistsort"),
        (620, "VM_M_refreshserverlist"), (621, "VM_M_getserverlistnumber"), (622, "VM_M_getserverlistindexforkey"),
        (623, "VM_M_addwantedserverlistkey"), (624, "VM_CL_getextresponse"), (625, "VM_netaddress_resolve"),
        (626, "VM_M_getgamedirinfo"), (627, "VM_sprintf"), (630, "VM_setkeybind"), (631, "VM_getbindmaps"),
        (632, "VM_setbindmaps"), (633, "VM_M_crypto_getkeyfp"), (634, "VM_M_crypto_getidfp"),
        (635, "VM_M_crypto_getencryptlevel"), (636, "VM_M_crypto_getmykeyfp"), (637, "VM_M_crypto_getmyidfp"),
        (639, "VM_digest_hex"), (641, "VM_M_crypto_getmyidstatus"), (642, "VM_coverage"), (643, "VM_M_crypto_getidstatus"),
    };

    /// <summary>
    /// vm_m_extensions[]: what checkextension answers true for. As the C has it, with three removed
    /// because this host does not have what they name: DP_CRYPTO (no d0_blind_id: the crypto builtins
    /// answer "no key"), DP_CINEMATIC_DPV (no video playback) and DP_QC_RENDER_SCENE (the menu table's
    /// scene builtins are NULL in the C too; the name is in its list by oversight).
    /// </summary>
    public static readonly string[] Extensions =
    {
        "BX_WAL_SUPPORT", "DP_COVERAGE", "DP_CSQC_BINDMAPS", "DP_GFX_FONTS", "DP_GFX_FONTS_FREETYPE", "DP_UTF8",
        "DP_FONT_VARIABLEWIDTH", "DP_MENU_EXTRESPONSEPACKET", "DP_QC_ASINACOSATANATAN2TAN", "DP_QC_AUTOCVARS", "DP_QC_CMD",
        "DP_QC_CRC16", "DP_QC_CVAR_TYPE", "DP_QC_CVAR_DESCRIPTION", "DP_QC_DIGEST", "DP_QC_DIGEST_SHA256",
        "DP_QC_FINDCHAIN_TOFIELD", "DP_QC_I18N", "DP_QC_LOG", "DP_QC_SPRINTF", "DP_QC_STRFTIME", "DP_QC_STRINGBUFFERS",
        "DP_QC_STRINGBUFFERS_CVARLIST", "DP_QC_STRINGBUFFERS_EXT_WIP", "DP_QC_STRINGCOLORFUNCTIONS",
        "DP_QC_STRING_CASE_FUNCTIONS", "DP_QC_STRREPLACE", "DP_QC_TOKENIZEBYSEPARATOR", "DP_QC_TOKENIZE_CONSOLE",
        "DP_QC_UNLIMITEDTEMPSTRINGS", "DP_QC_URI_ESCAPE", "DP_QC_URI_GET", "DP_QC_URI_POST", "DP_QC_WHICHPACK", "FTE_STRINGS",
        "DP_QC_FS_SEARCH_PACKFILE",
    };

    /// <summary>
    /// The name a shared builtin class registers a C function under (QcCoreBuiltins and QcStringBuiltins
    /// use the QuakeC names of the client table): "VM_ftos" is "ftos". Two differ: #229 is VM_strncasecmp
    /// declared with two parameters, which the shared class calls strcasecmp, and VM_getimagesize is
    /// draw_getimagesize in the client's QuakeC.
    /// </summary>
    public static string SharedName(string function) =>
        function.StartsWith("VM_", StringComparison.Ordinal) ? function[3..] : function;
}
