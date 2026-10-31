// Client fire-sound prediction bookkeeping (cl_predictfire). Pure C# (no Godot) so the matching rules are
// unit-testable; NetGame owns one and consults it from UpdateLocalFireFeedback and OnSoundReceived.

using System.Collections.Generic;

namespace VortexArena.Net;

/// <summary>
/// Pairs each fire sound the client played early for its own shot (the predicted copy) with the server's copy
/// of that same shot, so exactly one of the two is heard.
///
/// <para>Base Xonotic has no client fire-sound prediction: every shot's sound is the server's
/// <c>sound(actor, CH_WEAPON_A, ...)</c>, heard by the shooter too. The port plays the PRIMARY fire sound
/// locally the instant the shot is predicted and must then drop the server's echo of it. The first version of
/// that drop matched on the sample name alone — any of the local player's networked sounds whose sample was in
/// the predicted set was thrown away. The secondary attacks that share a sample with some weapon's primary
/// (Machinegun burst and Hagar volley: their own primary sample; Mortar, HLAC and Porto secondaries: likewise;
/// the Vaporizer laser: the Blaster's and Crylink's) were never predicted, so the shooter heard nothing at all
/// for them (playtest #65/#66).</para>
///
/// <para>The ledger drops a networked copy only when it can be paired with a predicted copy that is still
/// waiting for its echo. A networked copy with no predicted partner plays, and is kept as a short-lived credit:
/// if the client's own refire clock predicts the same sample a moment AFTER the server's copy arrived (clock
/// drift on sustained fire), that late predicted copy is skipped instead of doubling the shot.</para>
/// </summary>
public sealed class PredictedFireSoundLedger
{
    private readonly List<(string Sample, float Time)> _predicted = new();
    private readonly List<(string Sample, float Time)> _unpaired = new();

    /// <summary>How long a predicted copy waits for the server's echo before it is forgotten (seconds). Must
    /// cover the round trip plus a tick or two; a stale entry could otherwise swallow a later shot's sound.</summary>
    public float EchoWindow { get; set; } = 1.0f;

    /// <summary>How long an unpaired networked copy can still absorb a late predicted copy (seconds).</summary>
    public float LateWindow { get; set; } = 0.15f;

    /// <summary>Predicted copies still waiting for their echo (diagnostics/tests).</summary>
    public int PendingPredicted => _predicted.Count;

    /// <summary>
    /// The client predicted a shot whose sound is <paramref name="sample"/>. Returns whether to play it now:
    /// false when the server's copy of that shot already arrived and played (it is paired here instead).
    /// </summary>
    public bool OnPredicted(string sample, float now)
    {
        Expire(now);
        int i = IndexOf(_unpaired, sample);
        if (i >= 0)
        {
            _unpaired.RemoveAt(i);
            return false;
        }
        _predicted.Add((sample, now));
        return true;
    }

    /// <summary>
    /// The server's copy of one of the local player's own fire sounds arrived. Returns whether to drop it:
    /// true when it pairs with a predicted copy already played; false (play it) otherwise.
    /// </summary>
    public bool OnNetworked(string sample, float now)
    {
        Expire(now);
        int i = IndexOf(_predicted, sample);
        if (i >= 0)
        {
            _predicted.RemoveAt(i);
            return true;
        }
        _unpaired.Add((sample, now));
        return false;
    }

    /// <summary>Forget everything (map change, death, prediction toggled).</summary>
    public void Clear()
    {
        _predicted.Clear();
        _unpaired.Clear();
    }

    private void Expire(float now)
    {
        _predicted.RemoveAll(e => now - e.Time > EchoWindow || e.Time > now);
        _unpaired.RemoveAll(e => now - e.Time > LateWindow || e.Time > now);
    }

    // Oldest first: the lists are in arrival order, so the first match is the oldest entry.
    private static int IndexOf(List<(string Sample, float Time)> list, string sample)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i].Sample, sample, System.StringComparison.Ordinal))
                return i;
        return -1;
    }
}
