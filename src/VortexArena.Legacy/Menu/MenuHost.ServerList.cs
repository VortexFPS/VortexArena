// Port of Base/darkplaces/mvm_cmds.c VM_M_getserverliststat, VM_M_resetserverlistmasks,
// VM_M_setserverlistmaskstring, VM_M_setserverlistmasknumber, VM_M_resortserverlist,
// VM_M_getserverliststring, VM_M_getserverlistnumber, VM_M_setserverlistsort, VM_M_refreshserverlist,
// VM_M_getserverlistindexforkey, VM_M_addwantedserverlistkey.
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Menu;

public sealed partial class MenuHost
{
    private void AddServerListBuiltins()
    {
        _own["VM_M_getserverliststat"] = GetServerListStat;
        _own["VM_M_resetserverlistmasks"] = vm => { Parms(0, "VM_M_resetserverlistmasks"); ServerList.ResetMasks(); };
        _own["VM_M_setserverlistmaskstring"] = SetServerListMaskString;
        _own["VM_M_setserverlistmasknumber"] = SetServerListMaskNumber;
        _own["VM_M_resortserverlist"] = vm => { Parms(0, "VM_M_resortserverlist"); ServerList.RebuildView(); };
        _own["VM_M_setserverlistsort"] = SetServerListSort;
        _own["VM_M_refreshserverlist"] = RefreshServerList;
        _own["VM_M_getserverliststring"] = GetServerListString;
        _own["VM_M_getserverlistnumber"] = GetServerListNumber;
        _own["VM_M_getserverlistindexforkey"] = GetServerListIndexForKey;
        _own["VM_M_addwantedserverlistkey"] = vm => Parms(1, "VM_M_addwantedserverlistkey");
    }

    // #611 float(float type) gethostcachevalue: 0 servers in the view, 1 in the cache, 2 masters asked,
    // 3 master replies, 4 servers asked, 5 server replies, 6 the sort field, 7 the sort flags.
    private void GetServerListStat(QcVm vm)
    {
        Parms(1, "VM_M_getserverliststat");
        int type = ArgInt(0);
        vm.ReturnFloat(0);
        switch (type)
        {
            case 0: vm.ReturnFloat(ServerList.ViewCount); break;
            case 1: vm.ReturnFloat(ServerList.CacheCount); break;
            case 2: vm.ReturnFloat(ServerList.MasterQueryCount); break;
            case 3: vm.ReturnFloat(ServerList.MasterReplyCount); break;
            case 4: vm.ReturnFloat(ServerList.ServerQueryCount); break;
            case 5: vm.ReturnFloat(ServerList.ServerReplyCount); break;
            case 6: vm.ReturnFloat((int)ServerList.SortField); break;
            case 7: vm.ReturnFloat(ServerList.SortFlags); break;
            default: Warning($"VM_M_getserverliststat: bad type ({type}) passed!\n"); break;
        }
    }

    private HostCacheMask? MaskArg(string name)
    {
        int number = ArgInt(0);
        HostCacheMask? mask = ServerList.Mask(number);
        if (mask is null) Warning($"{name}: invalid mask number ({number}) passed!\n");
        return mask;
    }

    // dp_strlcpy into the mask's buffer.
    private static string Cut(string text, int size) => text.Length < size ? text : text[..(size - 1)];

    // #616 void(float mask, float fld, string str, float op) sethostcachemaskstring
    private void SetServerListMaskString(QcVm vm)
    {
        Parms(4, "VM_M_setserverlistmaskstring");
        string text = vm.ArgString(2);
        if (MaskArg("VM_M_setserverlistmaskstring") is not { } mask) return;
        int field = ArgInt(1);
        switch ((HostCacheField)field)
        {
            case HostCacheField.CName: mask.Info.CName = Cut(text, HostCacheInfo.CNameSize); break;
            case HostCacheField.Name: mask.Info.Name = Cut(text, HostCacheInfo.NameSize); break;
            case HostCacheField.QcStatus: mask.Info.QcStatus = Cut(text, HostCacheInfo.QcStatusSize); break;
            case HostCacheField.Players: mask.Info.Players = Cut(text, HostCacheInfo.PlayersSize); break;
            case HostCacheField.Map: mask.Info.Map = Cut(text, HostCacheInfo.MapSize); break;
            case HostCacheField.Mod: mask.Info.Mod = Cut(text, HostCacheInfo.ModSize); break;
            case HostCacheField.Game: mask.Info.Game = Cut(text, HostCacheInfo.GameSize); break;
            default:
                Warning($"VM_M_setserverlistmaskstring: Bad field number ({field}) passed!\n");
                return;
        }
        mask.Active = true;
        mask.Tests[field] = (HostCacheOp)ArgInt(3);
    }

    // #617 void(float mask, float fld, float num, float op) sethostcachemasknumber
    private void SetServerListMaskNumber(QcVm vm)
    {
        Parms(4, "VM_M_setserverlistmasknumber");
        if (MaskArg("VM_M_setserverlistmasknumber") is not { } mask) return;
        int number = ArgInt(2), field = ArgInt(1);
        switch ((HostCacheField)field)
        {
            case HostCacheField.MaxPlayers: mask.Info.MaxPlayers = number; break;
            case HostCacheField.NumPlayers: mask.Info.NumPlayers = number; break;
            case HostCacheField.NumBots: mask.Info.NumBots = number; break;
            case HostCacheField.NumHumans: mask.Info.NumHumans = number; break;
            case HostCacheField.Ping: mask.Info.Ping = number; break;
            case HostCacheField.Protocol: mask.Info.Protocol = number; break;
            case HostCacheField.FreeSlots: mask.Info.FreeSlots = number; break;
            case HostCacheField.Category: mask.Info.Category = number; break;
            case HostCacheField.IsFavorite: mask.Info.IsFavorite = number != 0; break;
            default:
                Warning($"VM_M_setserverlistmasknumber: Bad field number ({field}) passed!\n");
                return;
        }
        mask.Active = true;
        mask.Tests[field] = (HostCacheOp)ArgInt(3);
    }

    // #619 void(float fld, float flags) sethostcachesort. The values are stored as given; a field that
    // is not one sorts by index alone ("Bad serverlist_sortbyfield").
    private void SetServerListSort(QcVm vm)
    {
        Parms(2, "VM_M_setserverlistsort");
        ServerList.SortField = (HostCacheField)ArgInt(0);
        ServerList.SortFlags = ArgInt(1);
    }

    // #620 void([float reset]) refreshhostcache
    private void RefreshServerList(QcVm vm)
    {
        Parms(0, 1, "VM_M_refreshserverlist");
        bool reset = vm.ArgCount >= 1 && vm.ArgFloat(0) != 0;
        ServerList.Refresh(reset);
    }

    // The entry a builtin is asked about: number `hostnr` of the view, or (-1, inside the category
    // callback) the one being categorised. Null with a warning for anything else.
    private HostCacheEntry? EntryArg()
    {
        int number = ArgInt(1);
        if (number == -1 && ServerList.CallbackEntry is { } callback) return callback;
        if (number < 0 || number >= ServerList.ViewCount)
        {
            Warning($"VM_M_getserverliststring: bad hostnr ({number}) passed!\n");
            return null;
        }
        return ServerList.View[number];
    }

    // #612 string(float fld, float hostnr) gethostcachestring. The text is a server's (or, for the
    // address, this client's own spelling of where the server is); it is only ever returned as data.
    private void GetServerListString(QcVm vm)
    {
        Parms(2, "VM_M_getserverliststring");
        vm.ReturnInt(0);
        if (EntryArg() is not { } entry) return;
        int field = ArgInt(0);
        switch (field)
        {
            case (int)HostCacheField.CName: vm.ReturnString(entry.Info.CName); break;
            case (int)HostCacheField.Name: vm.ReturnString(entry.Info.Name); break;
            case (int)HostCacheField.QcStatus: vm.ReturnString(entry.Info.QcStatus); break;
            case (int)HostCacheField.Players: vm.ReturnString(entry.Info.Players); break;
            case (int)HostCacheField.Game: vm.ReturnString(entry.Info.Game); break;
            case (int)HostCacheField.Mod: vm.ReturnString(entry.Info.Mod); break;
            case (int)HostCacheField.Map: vm.ReturnString(entry.Info.Map); break;
            // "TODO remove this again"
            case 1024: vm.ReturnString(entry.Line1); break;
            case 1025: vm.ReturnString(entry.Line2); break;
            default: Warning($"VM_M_getserverliststring: bad field number ({field}) passed!\n"); break;
        }
    }

    // #621 float(float fld, float hostnr) gethostcachenumber
    private void GetServerListNumber(QcVm vm)
    {
        Parms(2, "VM_M_getserverlistnumber");
        vm.ReturnInt(0);
        if (EntryArg() is not { } entry) return;
        int field = ArgInt(0);
        switch ((HostCacheField)field)
        {
            case HostCacheField.MaxPlayers: vm.ReturnFloat(entry.Info.MaxPlayers); break;
            case HostCacheField.NumPlayers: vm.ReturnFloat(entry.Info.NumPlayers); break;
            case HostCacheField.NumBots: vm.ReturnFloat(entry.Info.NumBots); break;
            case HostCacheField.NumHumans: vm.ReturnFloat(entry.Info.NumHumans); break;
            case HostCacheField.FreeSlots: vm.ReturnFloat(entry.Info.FreeSlots); break;
            // "display inf when a listed server times out and net_slist_pause blocks its removal"
            case HostCacheField.Ping: vm.ReturnFloat(entry.Info.Ping != 0 ? entry.Info.Ping : float.PositiveInfinity); break;
            case HostCacheField.Protocol: vm.ReturnFloat(entry.Info.Protocol); break;
            case HostCacheField.Category: vm.ReturnFloat(entry.Info.Category); break;
            case HostCacheField.IsFavorite: vm.ReturnFloat(entry.Info.IsFavorite ? 1 : 0); break;
            default: Warning($"VM_M_getserverlistnumber: bad field number ({field}) passed!\n"); break;
        }
    }

    /// <summary>The key names gethostcacheindexforkey knows, in field order.</summary>
    public static readonly string[] HostCacheKeys =
    {
        "cname", "ping", "game", "mod", "map", "name", "maxplayers", "numplayers", "protocol", "numbots", "numhumans",
        "freeslots", "qcstatus", "players", "category", "isfavorite",
    };

    // #622 float(string key) gethostcacheindexforkey: the field number of a name, -1 if there is none.
    private void GetServerListIndexForKey(QcVm vm)
    {
        Parms(1, "VM_M_getserverlistindexforkey");
        string key = vm.ArgString(0);
        CheckEmptyString(key);
        vm.ReturnFloat(Array.IndexOf(HostCacheKeys, key));
    }
}
