// Port of Base/darkplaces/sv_ents_csqc.c EntityFrameCSQC_LostAllFrames, EntityFrameCSQC_LostFrame,
// EntityFrameCSQC_AllocFrame, EntityFrameCSQC_DeallocFrame, EntityFrameCSQC_WriteFrame; the
// csqcentity* members of client_t and csqcentityframedb_t (server.h); the SCOPE_* bits (protocol.h);
// the SendFlags/Version upkeep at the end of sv_send.c SV_PrepareEntityForSending; and the csqc
// lines of sv_main.c SV_SendServerinfo and SVVM_free_edict.
using VortexArena.Legacy.Protocol;

namespace VortexArena.Legacy.Server;

/// <summary>
/// The game's QuakeC, as far as CSQC entity networking needs it. An entity whose <c>.SendEntity</c>
/// field holds a function is not described by the engine (svc_entities) at all: that function writes
/// whatever it likes into the message and the client's QuakeC reads it back. The engine only frames
/// those payloads, and decides when one must be written again.
/// </summary>
public interface ISvCsqcEntityHost
{
    /// <summary>svs.maxclients. Entities 1..MaxClients are players and are always written first.</summary>
    int MaxClients { get; }
    /// <summary>prog->num_edicts: one past the highest entity slot in use.</summary>
    int NumEdicts { get; }
    /// <summary>The cvar sv_sendentities_csqc_randomize_order (default 1): start the non-player
    /// entities at a random one each frame, so that when they do not all fit it is not always the same
    /// ones left out.</summary>
    bool RandomizeOrder { get; }
    /// <summary>rand() % <paramref name="exclusiveMax"/>, for <see cref="RandomizeOrder"/>. The host
    /// owns the generator so that a run can be repeated; <paramref name="exclusiveMax"/> is at least 1.</summary>
    int NextRandom(int exclusiveMax);
    /// <summary>PRVM_serveredictfunction(edict, SendEntity) != 0.</summary>
    bool HasSendEntity(int entityNumber);
    /// <summary>The entity's <c>.SendFlags</c> field: the bits its QuakeC set since they were last collected.</summary>
    float GetSendFlags(int entityNumber);
    /// <summary>Sets <c>.SendFlags</c> to 0, which is how the QuakeC learns the bits were read.</summary>
    void ClearSendFlags(int entityNumber);
    /// <summary>The entity's <c>.Version</c> field, the older way of saying "send me again": 0 if unused.</summary>
    float GetVersion(int entityNumber);
    /// <summary>
    /// Runs the entity's <c>.SendEntity(entity to, float sendflags)</c>: <c>self</c> is
    /// <paramref name="entityNumber"/>, the first parameter is <paramref name="toClientEntityNumber"/>
    /// and the second <paramref name="sendFlags"/>. For the duration of the call the QuakeC's
    /// MSG_ENTITY writes (WriteByte(MSG_ENTITY, ...), which the engine routes to
    /// sv.writeentitiestoclient_msg) must go to <paramref name="msg"/>, where the entity's number has
    /// just been written. Returns the function's result: false means "do not send me to this client".
    /// The caller takes back whatever was written if the result is false or the payload does not fit.
    /// </summary>
    bool CallSendEntity(int entityNumber, int toClientEntityNumber, int sendFlags, DpMessageWriter msg);
}

/// <summary>
/// The server-wide half of CSQC entity upkeep (sv.csqcentityversion[] and the block at the end of
/// SV_PrepareEntityForSending). Once per server frame, for each entity that has a SendEntity function,
/// <see cref="CollectSendFlags"/> takes the bits the QuakeC set and the caller adds them to every
/// client's <see cref="SvCsqcEntityFrames"/>.
/// </summary>
public sealed class SvCsqcEntityVersions
{
    // unsigned char csqcentityversion[MAX_EDICTS], grown on demand up to that size.
    private byte[] _version = Array.Empty<byte>();

    /// <summary>A new level: server_t is zeroed.</summary>
    public void Clear() => Array.Clear(_version);

    /// <summary>SVVM_free_edict: a removed entity's version is forgotten. Each client's
    /// <see cref="SvCsqcEntityFrames.EdictFreed"/> must be told as well.</summary>
    public void EdictFreed(int entityNumber)
    {
        if ((uint)entityNumber < (uint)_version.Length)
            _version[entityNumber] = 0;
    }

    // The C casts the float straight to unsigned int, which is undefined for a negative or huge value.
    // Those become 0 and all-ones here; QuakeC only ever stores small non-negative integers.
    private static uint ToUInt(float value)
    {
        if (!(value >= 1))
            return 0;
        return value >= 4294967296.0f ? uint.MaxValue : (uint)value;
    }

    /// <summary>
    /// Reads the entity's <c>.SendFlags</c> and clears it, and applies the legacy <c>.Version</c> rule:
    /// a version different from the one last seen means "send everything". Returns the bits to add to
    /// every client (0 for none). Call only for an entity that has a SendEntity function.
    /// </summary>
    public uint CollectSendFlags(ISvCsqcEntityHost host, int entityNumber)
    {
        ArgumentNullException.ThrowIfNull(host);
        if ((uint)entityNumber >= DpProtocol.MaxEdicts)
            return 0;
        // get self.SendFlags and clear them
        // (to let the QC know that they've been read)
        uint sendFlags = ToUInt(host.GetSendFlags(entityNumber));
        host.ClearSendFlags(entityNumber);
        // legacy self.Version system
        uint version = ToUInt(host.GetVersion(entityNumber));
        if (version != 0)
        {
            if (entityNumber >= _version.Length)
                Array.Resize(ref _version, Math.Min(DpProtocol.MaxEdicts, Math.Max(_version.Length * 2, (entityNumber + 256) & ~255)));
            // The remembered version is one byte and the comparison is not: a Version above 255 never
            // matches, so such an entity is sent whole every frame. That is DarkPlaces' behaviour.
            if (_version[entityNumber] != version)
                sendFlags = 0xFFFFFF;
            _version[entityNumber] = (byte)version;
        }
        return sendFlags;
    }
}

/// <summary>
/// One client's svc_csqcentities encoder: client_t's csqcentityscope[], csqcentitysendflags[] and
/// csqcentityframehistory.
///
/// <code>
/// byte  svc_csqcentities (58)
/// repeat:
///   ushort n                      0 ends the list
///   if n &amp; 0x8000: entity n&amp;0x7FFF is removed
///   else: whatever that entity's SendEntity function wrote
/// </code>
///
/// Per entity the client is tracked by a scope (does the client think it exists, and what is to be
/// done this frame) and by sendflags (which of its properties still have to reach the client; the
/// QuakeC gives the bits their meaning, the engine only accumulates them). Every frame that carried
/// something is remembered in a ring of the last 256, with the entities and the flags each was sent
/// with. The frames share their numbers with the svc_entities stream, so when the client's
/// acknowledgements skip a number, <see cref="LostFrame"/> looks that frame up and puts its flags back.
/// </summary>
public sealed class SvCsqcEntityFrames
{
    public const int FramesInHistory = 256;    // NUM_CSQCENTITYDB_FRAMES
    public const int EntitiesPerFrame = 256;   // NUM_CSQCENTITIES_PER_FRAME

    /// <summary>SCOPE_WANTREMOVE: a remove has been scheduled. Never set together with WantUpdate.</summary>
    public const byte ScopeWantRemove = 1;
    /// <summary>SCOPE_WANTUPDATE: an update has been scheduled.</summary>
    public const byte ScopeWantUpdate = 2;
    public const byte ScopeWantSend = ScopeWantRemove | ScopeWantUpdate;
    /// <summary>SCOPE_EXISTED_ONCE: the entity was sent at some time. All these get resent on a full loss.</summary>
    public const byte ScopeExistedOnce = 4;
    /// <summary>SCOPE_ASSUMED_EXISTING: the client is assumed to have the entity, so it needs a remove when it goes.</summary>
    public const byte ScopeAssumedExisting = 8;

    /// <summary>Every sendflag the engine itself sets: "send the whole entity".</summary>
    public const uint FullSendFlags = 0xFFFFFF;

    private readonly ISvCsqcEntityHost _host;

    // csqcentityscope[MAX_EDICTS] and csqcentitysendflags[MAX_EDICTS], grown on demand up to that size.
    // A slot beyond the arrays is in its reset state: scope 0, sendflags FullSendFlags.
    private byte[] _scope = Array.Empty<byte>();
    private uint[] _sendFlags = Array.Empty<uint>();
    private int _numEdicts;

    // csqcentityframehistory[NUM_CSQCENTITYDB_FRAMES], as parallel arrays; framenum -1 is an empty
    // slot. A slot's lists are allocated the first time it is used.
    private readonly int[] _historyFrameNum = new int[FramesInHistory];
    private readonly int[] _historyNum = new int[FramesInHistory];
    private readonly ushort[]?[] _historyEntNo = new ushort[FramesInHistory][];
    private readonly int[]?[] _historySendFlags = new int[FramesInHistory][];
    private int _historyNext;
    private int _lastReset;

    // EntityFrameCSQC_LostFrame's "static int recoversendflags[MAX_EDICTS]", per client so nothing is shared.
    private int[] _recoverSendFlags = Array.Empty<int>();

    public SvCsqcEntityFrames(ISvCsqcEntityHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        Array.Fill(_historyFrameNum, -1);
    }

    /// <summary>csqcnumedicts: how many entity slots this client's bookkeeping covers.</summary>
    public int NumEdicts => _numEdicts;

    /// <summary>csqcentityframe_lastreset: the frame from which everything has been sent again anyway,
    /// so that a loss before it needs no handling; -1 between a full reset and the next frame.</summary>
    public int LastResetFrame => _lastReset;

    /// <summary>The SCOPE_* bits of an entity.</summary>
    public byte Scope(int entityNumber) => (uint)entityNumber < (uint)_scope.Length ? _scope[entityNumber] : (byte)0;

    /// <summary>The sendflags an entity still owes this client.</summary>
    public uint SendFlags(int entityNumber) => (uint)entityNumber < (uint)_sendFlags.Length ? _sendFlags[entityNumber] : FullSendFlags;

    /// <summary>How many frames the history ring holds at the moment.</summary>
    public int FramesRemembered
    {
        get
        {
            int n = 0;
            foreach (int f in _historyFrameNum)
                if (f >= 0) n++;
            return n;
        }
    }

    /// <summary>
    /// The "reset csqc entity versions" block of SV_SendServerinfo, run whenever the client is sent
    /// svc_serverinfo (connect, reconnect, level change): nothing is assumed to exist on the client
    /// and every entity is owed in full.
    ///
    /// Deviation: DarkPlaces does not touch csqcentityframe_lastreset there, so a value left by a full
    /// resend on the previous level outlives the level, and losses of the new level's frames below it
    /// (frame numbers start again at 1) are silently ignored. It is put back to its initial 0 here.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_scope);
        Array.Fill(_sendFlags, FullSendFlags);
        Array.Clear(_historyNum);
        Array.Fill(_historyFrameNum, -1);
        _numEdicts = 0;
        _historyNext = 0;
        _lastReset = 0;
    }

    private void EnsureCapacity(int count)
    {
        count = Math.Min(count, DpProtocol.MaxEdicts);
        int old = _scope.Length;
        if (count <= old)
            return;
        int size = Math.Min(DpProtocol.MaxEdicts, Math.Max(old * 2, (count + 255) & ~255));
        Array.Resize(ref _scope, size);
        Array.Resize(ref _sendFlags, size);
        Array.Fill(_sendFlags, FullSendFlags, old, size - old);
    }

    /// <summary>"move sendflags into the per-client sendflags" (SV_PrepareEntityForSending): add the
    /// bits <see cref="SvCsqcEntityVersions.CollectSendFlags"/> returned for an entity.</summary>
    public void AddSendFlags(int entityNumber, uint sendFlags)
    {
        if (sendFlags == 0 || (uint)entityNumber >= DpProtocol.MaxEdicts)
            return;
        EnsureCapacity(entityNumber + 1);
        _sendFlags[entityNumber] |= sendFlags;
    }

    /// <summary>SVVM_free_edict: "make sure csqc networking is aware of the removed entity". Whatever
    /// takes the slot next is owed in full.</summary>
    public void EdictFreed(int entityNumber)
    {
        if ((uint)entityNumber < (uint)_sendFlags.Length)
            _sendFlags[entityNumber] = FullSendFlags;
    }

    /// <summary>
    /// EntityFrameCSQC_LostAllFrames: mark ALL csqc entities as requiring a FULL resend. Used when a
    /// frame is lost that the history no longer covers. ("I know this is a bad workaround, but better
    /// than nothing.")
    /// </summary>
    public void LostAllFrames()
    {
        for (int i = 0; i < _numEdicts; i++)
        {
            if ((_scope[i] & ScopeExistedOnce) == 0)
                continue;
            // FULL RESEND. We can't clear SCOPE_ASSUMED_EXISTING yet as this would cancel removes on a rejected send attempt.
            _sendFlags[i] |= FullSendFlags;
            // If it was ever sent to that client as a CSQC entity (and is one no longer)...
            if (!_host.HasSendEntity(i))
                _scope[i] |= ScopeAssumedExisting; // FORCE REMOVE.
        }
    }

    /// <summary>
    /// EntityFrameCSQC_LostFrame: the client never received frame <paramref name="frameNum"/>. If the
    /// history has it, every entity it carried gets its flags back, less the bits that a later frame
    /// has carried since, and a lost removal is scheduled again. If the frame is older than anything
    /// remembered, everything is resent (<see cref="LostAllFrames"/>); if it is merely absent between
    /// remembered frames it carried no CSQC entities, and there is nothing to do. Any value is safe to
    /// pass.
    /// </summary>
    public void LostFrame(int frameNum)
    {
        if (_lastReset < 0)
            return;
        if (frameNum < _lastReset)
            return; // no action required, as we resent that data anyway

        // is our frame out of history?
        int ringFirst = _historyNext; // oldest entry
        int ringLast = (ringFirst + FramesInHistory - 1) % FramesInHistory; // most recently added entry

        bool valid = false;
        int j;
        for (j = 0; j < FramesInHistory; j++)
        {
            int f = _historyFrameNum[(ringFirst + j) % FramesInHistory];
            if (f < 0)
                continue;
            if (f == frameNum)
                break;
            if (f < frameNum)
                valid = true;
        }
        if (j == FramesInHistory)
        {
            if (valid) // got beaten, i.e. there is a frame < framenum
            {
                // a non-csqc frame got lost... great
                return;
            }
            // a too old frame got lost... sorry, cannot handle this
            LostAllFrames();
            _lastReset = -1;
            return;
        }

        // so j is the frame that got lost
        // ringlast is the frame that we have to go to
        ringFirst = (ringFirst + j) % FramesInHistory;
        if (ringLast < ringFirst)
            ringLast += FramesInHistory;

        if (_recoverSendFlags.Length < _scope.Length)
            _recoverSendFlags = new int[_scope.Length];
        int[] recover = _recoverSendFlags;
        Array.Clear(recover);

        for (j = ringFirst; j <= ringLast; j++)
        {
            int slot = j % FramesInHistory;
            int f = _historyFrameNum[slot];
            ushort[]? entNo = _historyEntNo[slot];
            int[]? flags = _historySendFlags[slot];
            if (f < 0 || entNo is null || flags is null)
            {
                // deleted frame
            }
            else if (f < frameNum)
            {
                // a frame in the past... should never happen
            }
            else if (f == frameNum)
            {
                // handling the actually lost frame now
                for (int i = 0; i < _historyNum[slot]; i++)
                {
                    int sf = flags[i];
                    int ent = entNo[i];
                    if (sf < 0) // remove
                        recover[ent] |= -1; // all bits, including sign
                    else if (sf > 0)
                        recover[ent] |= sf;
                }
            }
            else
            {
                // handling the frames that followed it now
                for (int i = 0; i < _historyNum[slot]; i++)
                {
                    int sf = flags[i];
                    int ent = entNo[i];
                    if (sf < 0) // remove
                    {
                        recover[ent] = 0; // no need to update, we got a more recent remove (and will fix it THEN)
                        // The C breaks here ("no flags left to remove..."), which leaves the rest of
                        // this frame's entities unsubtracted, not just the rest of this entity's
                        // flags. Kept: the only effect is that those entities are sent once more than
                        // strictly needed.
                        break;
                    }
                    if (sf > 0)
                        recover[ent] &= ~sf; // no need to update these bits, we already got them later
                }
            }
        }

        for (int i = 0; i < _numEdicts; i++)
        {
            if (recover[i] < 0)
                _scope[i] |= ScopeAssumedExisting; // FORCE REMOVE.
            else
                _sendFlags[i] |= (uint)recover[i];
        }
    }

    // EntityFrameCSQC_AllocFrame: take the oldest slot of the ring for a new frame.
    private int AllocFrame(int frameNum)
    {
        int ringFirst = _historyNext; // oldest entry
        _historyNext = (_historyNext + 1) % FramesInHistory;
        _historyFrameNum[ringFirst] = frameNum;
        _historyNum[ringFirst] = 0;
        _historyEntNo[ringFirst] ??= new ushort[EntitiesPerFrame];
        _historySendFlags[ringFirst] ??= new int[EntitiesPerFrame];
        return ringFirst;
    }

    // EntityFrameCSQC_DeallocFrame: give back the slot just allocated, when the frame turned out to
    // carry nothing, so that empty frames do not push real ones out of the history.
    private void DeallocFrame(int frameNum)
    {
        int ringFirst = _historyNext; // oldest entry
        int ringLast = (ringFirst + FramesInHistory - 1) % FramesInHistory; // most recently added entry
        if (frameNum == _historyFrameNum[ringLast])
        {
            _historyFrameNum[ringLast] = -1;
            _historyNum[ringLast] = 0;
            _historyNext = ringLast;
        }
        // else "Trying to dealloc the wrong entity frame": cannot happen, WriteFrame deallocates only
        // the frame it has just allocated.
    }

    // The three lines the C repeats for every entity that is not to be sent as a CSQC entity this
    // frame: no update, a remove if the client has it, and everything owed should it come back.
    private void MarkNotSent(int number)
    {
        _scope[number] &= unchecked((byte)~ScopeWantSend);
        if ((_scope[number] & ScopeAssumedExisting) != 0)
            _scope[number] |= ScopeWantRemove;
        _sendFlags[number] = FullSendFlags;
    }

    /// <summary>
    /// EntityFrameCSQC_WriteFrame. <paramref name="numbers"/> lists, in rising order, the entities
    /// visible to this client that are networked by QuakeC (ACTIVE_SHARED in sv_ents.c);
    /// <paramref name="frameNum"/> is the number the svc_entities frame of the same packet will have
    /// (<see cref="SvEntityFrame5Database.LatestFrameNumber"/> + 1), and
    /// <paramref name="toClientEntityNumber"/> the client's own entity, passed to SendEntity.
    ///
    /// Removals are written first, then updates: players in order, then the other entities starting
    /// from a random one if the host asks for that. An update is written only for an entity that owes
    /// the client some sendflags; a new entity always gets <see cref="FullSendFlags"/>. An update that
    /// does not fit is taken back out of the message and tried again next frame.
    ///
    /// Returns whether anything was written. If so the caller must make sure an svc_entities frame
    /// with this number goes out in the same packet (EntityFrame5_WriteFrame's need_empty), because it
    /// is that frame's acknowledgement which tells the server whether these entities arrived.
    /// </summary>
    public bool WriteFrame(DpMessageWriter msg, int maxSize, ReadOnlySpan<ushort> numbers, int frameNum, int toClientEntityNumber)
    {
        ArgumentNullException.ThrowIfNull(msg);
        bool sectionStarted = false;
        int maxClients = _host.MaxClients;
        int dbFrame = AllocFrame(frameNum);
        ushort[] dbEntNo = _historyEntNo[dbFrame]!;
        int[] dbSendFlags = _historySendFlags[dbFrame]!;
        int dbNum = 0;

        if (_lastReset < 0)
            _lastReset = frameNum;

        // sizebuf_t would Host_Error on a write past its own end; the budget never exceeds what the
        // message can hold, so no engine-side write here can be dropped.
        maxSize = Math.Min(maxSize, msg.MaxSize);
        maxSize -= 24; // always fit in an empty svc_entities message (for packet loss detection!)

        // make sure there is enough room to store the svc_csqcentities byte,
        // the terminator (0x0000) and at least one entity update
        // (This return leaves the frame just allocated in the history, empty. That is the C's
        // behaviour and it is harmless: losing such a frame recovers nothing.)
        if (msg.Length + 32 >= maxSize)
            return false;

        int numEdicts = Math.Min(_host.NumEdicts, DpProtocol.MaxEdicts);
        if (_numEdicts < numEdicts)
            _numEdicts = numEdicts;
        EnsureCapacity(_numEdicts);

        int number = 1;
        foreach (ushort n in numbers)
        {
            // Deviation: the C trusts the list to be sorted and inside the edict range. An entry that
            // is not is skipped, so nothing indexes outside the client's arrays.
            if (n < number || n >= _numEdicts)
                continue;
            for (; number < n; number++)
                MarkNotSent(number);
            _scope[number] &= unchecked((byte)~ScopeWantSend);
            if (_host.HasSendEntity(number))
                _scope[number] |= ScopeWantUpdate;
            else
            {
                if ((_scope[number] & ScopeAssumedExisting) != 0)
                    _scope[number] |= ScopeWantRemove;
                _sendFlags[number] = FullSendFlags;
            }
            number++;
        }
        int end = _numEdicts;
        for (; number < end; number++)
            MarkNotSent(number);

        // now try to emit the entity updates
        // First send all removals.
        int nonPlayerIndex = 0;
        for (number = 1; number < end; number++)
        {
            if ((_scope[number] & ScopeWantSend) == 0)
                continue;
            if (dbNum >= EntitiesPerFrame)
                goto outofspace;
            if ((_scope[number] & ScopeWantRemove) != 0) // Also implies ASSUMED_EXISTING.
            {
                // A removal. SendFlags have no power here.
                // write a remove message
                // first write the message identifier if needed
                if (!sectionStarted)
                {
                    sectionStarted = true;
                    msg.WriteByte((int)Svc.CsqcEntities);
                }
                // write the remove message
                msg.WriteShort(number | 0x8000);
                _scope[number] &= unchecked((byte)~(ScopeWantSend | ScopeAssumedExisting));
                _sendFlags[number] = FullSendFlags; // resend completely if it becomes active again
                dbEntNo[dbNum] = (ushort)number;
                dbSendFlags[dbNum] = -1;
                dbNum++;
                if (msg.Length + 17 >= maxSize)
                    goto outofspace;
            }
            else
            {
                // An update.
                // Nothing to send? FINE.
                if (_sendFlags[number] == 0)
                    continue;
                if (number > maxClients)
                    ++nonPlayerIndex;
            }
        }

        // If sv_sendentities_csqc_randomize_order is false, this is always 0.
        // As such, nonplayer_splitpoint_number will be exactly
        // svs.maxclients + 1. Thus, the shifting below will be a NOP.
        //
        // Otherwise, a random subsection of the non-player entities will be
        // sent in the first pass, and the rest in the second pass.
        //
        // This makes it random which entities will be sent or not in case of
        // running out of space in the message, guaranteeing that every entity
        // eventually gets a chance to be sent.
        //
        // Note that player entities are never included in this. This is to
        // ensure they keep having priority over anything else. If even sending
        // the player entities alone runs out of message space, the experience
        // will be horrible anyway, not much we can do about it - except maybe
        // better culling.
        int nonPlayerSplitPointNumber = maxClients + 1;
        if (_host.RandomizeOrder && nonPlayerIndex > 0)
        {
            // rand() % nonplayer_index; the host's answer is forced into range rather than trusted.
            int nonPlayerSplitPoint = (int)((uint)_host.NextRandom(nonPlayerIndex) % (uint)nonPlayerIndex);

            // Convert the split point to an entity number.
            // This must use the exact same conditions as the above
            // incrementing of nonplayer_index.
            nonPlayerIndex = 0;
            for (number = 1; number < end; number++)
            {
                if ((_scope[number] & ScopeWantSend) == 0)
                    continue;
                if (dbNum >= EntitiesPerFrame)
                    goto outofspace;
                if ((_scope[number] & ScopeWantRemove) != 0)
                    continue;
                // An update.
                // Nothing to send? FINE.
                if (_sendFlags[number] == 0)
                    continue;
                if (number > maxClients)
                {
                    if (nonPlayerIndex == nonPlayerSplitPoint)
                    {
                        nonPlayerSplitPointNumber = number;
                        break;
                    }
                    ++nonPlayerIndex;
                }
            }
        }

        for (int num = 1; num < end; num++)
        {
            // Remap entity numbers as follows:
            // - 1..maxclients stays as is
            // - Otherwise, rotate so that maxclients+1 becomes nonplayer_splitpoint_number.
            number = num <= maxClients
                ? num
                : num - (maxClients + 1) + nonPlayerSplitPointNumber;
            if (number >= end)
                number -= end - (maxClients + 1);
            if ((uint)number >= (uint)end)
                continue; // cannot happen while maxclients >= 0; an index is not worth betting on that

            if ((_scope[number] & ScopeWantSend) == 0)
                continue;
            if (dbNum >= EntitiesPerFrame)
                goto outofspace;
            if ((_scope[number] & ScopeWantRemove) != 0)
                continue;

            // save the cursize value in case we overflow and have to rollback
            int oldCurSize = msg.Length;

            // An update.
            int sendFlags = unchecked((int)_sendFlags[number]);
            // Nothing to send? FINE.
            if (sendFlags == 0)
                continue;
            // If it's a new entity, always assume sendflags 0xFFFFFF.
            if ((_scope[number] & ScopeAssumedExisting) == 0)
                sendFlags = unchecked((int)FullSendFlags);

            // write an update
            if (_host.HasSendEntity(number))
            {
                if (!sectionStarted)
                    msg.WriteByte((int)Svc.CsqcEntities);
                int oldCurSize2 = msg.Length;
                msg.WriteShort(number);
                // (msg->allowoverflow = true: the QuakeC may write more than the message holds without
                // that being fatal. DpMessageWriter always behaves so.)
                bool accepted = _host.CallSendEntity(number, toClientEntityNumber, sendFlags, msg);
                if (!accepted)
                {
                    // Send rejected by CSQC. This means we want to remove it.
                    // CSQC requests we remove this one.
                    if ((_scope[number] & ScopeAssumedExisting) != 0)
                    {
                        msg.Rollback(oldCurSize2);
                        msg.WriteShort(number | 0x8000);
                        _scope[number] &= unchecked((byte)~(ScopeWantSend | ScopeAssumedExisting));
                        _sendFlags[number] = 0;
                        dbEntNo[dbNum] = (ushort)number;
                        dbSendFlags[dbNum] = -1;
                        dbNum++;
                        // and take note that we have begun the svc_csqcentities
                        // section of the packet
                        sectionStarted = true;
                        if (msg.Length + 17 >= maxSize)
                            goto outofspace;
                    }
                    else
                    {
                        // Nothing to do. Just don't do it again.
                        msg.Rollback(oldCurSize);
                        _scope[number] &= unchecked((byte)~ScopeWantSend);
                        _sendFlags[number] = 0;
                    }
                    continue;
                }
                // Deviation: when the QuakeC overflows the message itself (not just the budget), the C's
                // SZ_GetSpace empties the whole buffer and carries on, so the size test below passes on
                // a message that has lost everything written before. An overflow counts as "does not
                // fit" here.
                if (!msg.Overflowed && msg.Length + 2 <= maxSize)
                {
                    // an update has been successfully written
                    _sendFlags[number] = 0;
                    dbEntNo[dbNum] = (ushort)number;
                    dbSendFlags[dbNum] = sendFlags;
                    dbNum++;
                    _scope[number] &= unchecked((byte)~ScopeWantSend);
                    _scope[number] |= ScopeExistedOnce | ScopeAssumedExisting;
                    // and take note that we have begun the svc_csqcentities
                    // section of the packet
                    sectionStarted = true;
                    if (msg.Length + 17 >= maxSize)
                        goto outofspace;
                    continue;
                }
            }
            // self.SendEntity returned false (or does not exist) or the
            // update was too big for this packet - rollback the buffer to its
            // state before the writes occurred, we'll try again next frame
            msg.Rollback(oldCurSize);
        }

    outofspace:
        if (sectionStarted)
        {
            // write index 0 to end the update (0 is never used by real entities)
            msg.WriteShort(0);
        }

        _historyNum[dbFrame] = dbNum;
        if (dbNum == 0)
        {
            // if no single ent got added, remove the frame from the DB again, to allow
            // for a larger history
            DeallocFrame(frameNum);
        }

        return sectionStarted;
    }
}
