// Port of nothing in DarkPlaces: this is the bookkeeping that lets an immediate-mode scene
// (clvm_cmds.c VM_CL_R_ClearScene / VM_CL_R_AddEntities / VM_CL_R_RenderScene resubmit everything every
// frame into r_refdef.scene) drive a retained-mode renderer. DarkPlaces' own counterpart is
// "r_refdef.scene.numentities = 0" - it has nothing to keep.
namespace VortexArena.Legacy.Presentation;

/// <summary>
/// Mark-and-sweep over render proxies keyed by an integer (an edict number, or a made-up key for an
/// engine entity or a one-off). Each frame: <see cref="BeginFrame"/>, then <see cref="Touch"/> for
/// every submission, then <see cref="Sweep"/>, which reports the keys that were shown last frame and
/// not submitted in this one (to hide) and the ones that have gone unsubmitted for
/// <see cref="ReleaseAfterFrames"/> frames (to free). The owner keeps the nodes; this keeps the truth
/// about which are current, so that part is testable without a renderer.
/// </summary>
public sealed class LegacySceneLedger
{
    private sealed class Slot
    {
        public long LastFrame;
        public bool Shown;
        public string Model = "";
        public int Skin;
    }

    private readonly Dictionary<int, Slot> _slots = new();
    private long _frame;

    /// <summary>The most proxies kept alive. A submission past it is refused (R_AddEntity's "scene is full").</summary>
    public int Capacity { get; init; } = 4096;
    /// <summary>Frames a proxy may go unsubmitted before it is released. 0 releases at the first sweep.</summary>
    public int ReleaseAfterFrames { get; init; } = 120;

    public int Count => _slots.Count;
    /// <summary>Proxies submitted in the current frame.</summary>
    public int SubmittedThisFrame { get; private set; }
    /// <summary>Submissions refused over the ledger's lifetime because it was full.</summary>
    public long Refused { get; private set; }

    public void BeginFrame()
    {
        _frame++;
        SubmittedThisFrame = 0;
    }

    /// <summary>What a submission means for its proxy.</summary>
    public enum TouchResult
    {
        /// <summary>The scene is full; nothing was recorded.</summary>
        Refused,
        /// <summary>No proxy existed: build one.</summary>
        Create,
        /// <summary>A proxy exists but is for another model or skin: rebuild it.</summary>
        Rebuild,
        /// <summary>The proxy is current: update its transform and state.</summary>
        Update,
    }

    /// <summary>Records that <paramref name="key"/> was submitted this frame with that model and skin.</summary>
    public TouchResult Touch(int key, string model, int skin)
    {
        if (_slots.TryGetValue(key, out Slot? slot))
        {
            if (slot.LastFrame != _frame) SubmittedThisFrame++;
            slot.LastFrame = _frame;
            slot.Shown = true;
            if (slot.Skin == skin && string.Equals(slot.Model, model, StringComparison.Ordinal)) return TouchResult.Update;
            slot.Model = model;
            slot.Skin = skin;
            return TouchResult.Rebuild;
        }
        if (_slots.Count >= Capacity)
        {
            Refused++;
            return TouchResult.Refused;
        }
        _slots[key] = new Slot { LastFrame = _frame, Shown = true, Model = model, Skin = skin };
        SubmittedThisFrame++;
        return TouchResult.Create;
    }

    /// <summary>Whether <paramref name="key"/> was submitted in the current frame.</summary>
    public bool IsCurrent(int key) => _slots.TryGetValue(key, out Slot? slot) && slot.LastFrame == _frame;

    /// <summary>
    /// Ends the frame. <paramref name="hide"/> receives each key that was visible and was not
    /// submitted this frame; <paramref name="release"/> each key dropped from the ledger.
    /// </summary>
    public void Sweep(List<int> hide, List<int> release)
    {
        hide.Clear();
        release.Clear();
        foreach ((int key, Slot slot) in _slots)
        {
            if (slot.LastFrame == _frame) continue;
            if (slot.Shown)
            {
                slot.Shown = false;
                hide.Add(key);
            }
            if (_frame - slot.LastFrame > ReleaseAfterFrames) release.Add(key);
        }
        foreach (int key in release) _slots.Remove(key);
    }

    /// <summary>Drops one key at once (its edict was freed). True if it was known.</summary>
    public bool Release(int key) => _slots.Remove(key);

    /// <summary>Drops everything (a new level); <paramref name="released"/> receives every key.</summary>
    public void Clear(List<int> released)
    {
        released.Clear();
        released.AddRange(_slots.Keys);
        _slots.Clear();
    }
}
