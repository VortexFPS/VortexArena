using System.Text;

namespace VortexArena.Server.Bot.Neural;

/// <summary>
/// A canonical description of the observation and action layout: every section's name and width, in the
/// order the vector lays them out. Exchanged at the trainer handshake so the two ends can prove they mean
/// the same thing by the same numbers.
/// </summary>
/// <remarks>
/// <para><b>Why sizes were not enough.</b> The handshake used to compare the observation and action LENGTHS
/// and nothing else. That cannot see a size-preserving change: swap two equal-width sections, or repurpose
/// the floats inside one, and both ends still agree on 302 while disagreeing about what the 302 numbers
/// mean. Nothing crashes. The network keeps producing plausible actions from misread inputs, and the only
/// symptom is a training run that stops improving for no visible reason — which is the failure
/// <c>tools/neural/va_neural/layout.py</c> was written to guard against and could not, on its own, catch.</para>
///
/// <para><b>Why the whole string travels, not just a hash.</b> A hash reports only THAT two layouts differ,
/// which leaves whoever is holding the failed run to diff eleven constants by hand. Roughly two hundred
/// bytes, once per handshake, buys an error that names the section instead. <see cref="Fingerprint"/> still
/// exists for the compact form that belongs in a log line or a run's config.json.</para>
///
/// <para>Section names match the Python mirror's constants (<c>OBS_PROPRIO</c> is <c>proprio</c>, and so on)
/// so an error message reads the same on both sides of the socket.</para>
/// </remarks>
public static class NeuralLayoutDescriptor
{
    /// <summary>
    /// Build the descriptor from the live constants. Deriving it rather than restating it is the point: a
    /// section that changes width cannot change it here and stay silent there.
    /// </summary>
    public static string Build()
    {
        var sb = new StringBuilder(256);

        sb.Append("obs:");
        Section(sb, "proprio", NeuralObservation.ProprioFloats, first: true);
        Section(sb, "weapon", NeuralObservation.WeaponFloats);
        Section(sb, "goal", NeuralObservation.GoalFloats);
        Section(sb, "aim", NeuralObservation.AimFloats);
        Section(sb, "history", NeuralObservation.HistoryFloats);
        Section(sb, "prev_action", NeuralObservation.PrevActionFloats);
        Section(sb, "navfield", NavField.ProbeFloats);
        Section(sb, "navfield_up", NavField.UpperProbeFloats);
        Section(sb, "route", NeuralObservation.RouteFloats);
        Section(sb, "features", MapFeatures.ObservationFloats);
        Section(sb, "trace_fan", NeuralObservation.TraceFanFloats);

        sb.Append("|act:");
        Section(sb, "move", ActionSpace.MoveCount, first: true);
        Section(sb, "jump", ActionSpace.JumpCount);
        Section(sb, "crouch", ActionSpace.CrouchCount);
        Section(sb, "attack1", ActionSpace.Attack1Count);
        Section(sb, "attack2", ActionSpace.Attack2Count);
        Section(sb, "weapon", ActionSpace.WeaponCount);
        // The two continuous heads are one float each and sit after every categorical block. Naming them
        // here keeps a reordering of yaw/pitch visible, which their equal width would otherwise hide.
        Section(sb, "yaw", 1);
        Section(sb, "pitch", 1);

        // The wire action is the ENCODED form (six chosen indices then two continuous values), which is a
        // different number from the logit count and has skewed independently of it before.
        sb.Append("|wire=").Append(ActionEncoding.Size);

        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string name, int width, bool first = false)
    {
        if (!first) sb.Append(',');
        sb.Append(name).Append('=').Append(width);
    }

    /// <summary>
    /// FNV-1a over the descriptor's UTF-8 bytes: the compact form for logs and run manifests.
    /// </summary>
    /// <remarks>
    /// Hand-rolled on purpose. .NET's <c>string.GetHashCode</c> is randomised per process and Python's
    /// <c>hash()</c> is salted per interpreter, so neither can agree with the other or with itself across
    /// runs. FNV-1a is eight lines in both languages and gives the same number everywhere, forever.
    /// </remarks>
    public static ulong Fingerprint(string descriptor)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        foreach (byte b in Encoding.UTF8.GetBytes(descriptor))
        {
            hash ^= b;
            hash *= prime;
        }
        return hash;
    }

    /// <summary>The current layout's fingerprint as sixteen hex digits.</summary>
    public static string ShortForm => Fingerprint(Build()).ToString("x16");
}
