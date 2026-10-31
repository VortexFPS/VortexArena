using VortexArena.Net;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// The predicted-fire-sound pairing (playtest #65/#66): the server's copy of the local player's own shot is
/// dropped only when a predicted copy of that shot already played. Secondary attacks that share a sample with a
/// primary (Machinegun burst = uzi_fire) were silenced for the shooter when the drop matched on sample alone.
/// </summary>
public class PredictedFireSoundLedgerTests
{
    private const string Uzi = "weapons/uzi_fire.wav";

    [Fact]
    public void Predicted_Shot_Drops_Its_Echo_Once()
    {
        var l = new PredictedFireSoundLedger();
        Assert.True(l.OnPredicted(Uzi, 10.00f));    // played locally
        Assert.True(l.OnNetworked(Uzi, 10.05f));    // echo dropped
        Assert.False(l.OnNetworked(Uzi, 10.06f));   // a second server copy has no partner: plays
    }

    [Fact]
    public void Unpredicted_Secondary_Burst_Plays_Every_Round()
    {
        // Machinegun burst: three server rounds, none predicted (the client predicts primary only).
        var l = new PredictedFireSoundLedger();
        Assert.False(l.OnNetworked(Uzi, 5.00f));
        Assert.False(l.OnNetworked(Uzi, 5.06f));
        Assert.False(l.OnNetworked(Uzi, 5.12f));
    }

    [Fact]
    public void Sustained_Fire_Pairs_One_For_One()
    {
        var l = new PredictedFireSoundLedger();
        int heard = 0;
        for (int i = 0; i < 20; i++)
        {
            float t = 1f + i * 0.1f;
            if (l.OnPredicted(Uzi, t)) heard++;
            if (!l.OnNetworked(Uzi, t + 0.03f)) heard++;
        }
        Assert.Equal(20, heard);
        Assert.Equal(0, l.PendingPredicted);
    }

    [Fact]
    public void Server_Copy_Arriving_First_Absorbs_The_Late_Prediction()
    {
        var l = new PredictedFireSoundLedger();
        Assert.False(l.OnNetworked(Uzi, 3.00f));    // server's shot arrived first: plays
        Assert.False(l.OnPredicted(Uzi, 3.02f));    // the client's clock predicts it a moment later: skipped
        Assert.True(l.OnPredicted(Uzi, 3.50f));     // a later, genuinely new shot plays
    }

    [Fact]
    public void Stale_Prediction_Expires_Instead_Of_Eating_A_Later_Shot()
    {
        // A predicted shot the server never fired (ammo ran out mid-hold) must not swallow the sound of a burst
        // fired a few seconds later.
        var l = new PredictedFireSoundLedger { EchoWindow = 0.5f };
        Assert.True(l.OnPredicted(Uzi, 1.0f));
        Assert.False(l.OnNetworked(Uzi, 4.0f));
    }

    [Fact]
    public void Different_Samples_Never_Pair()
    {
        var l = new PredictedFireSoundLedger();
        Assert.True(l.OnPredicted("weapons/shotgun_fire.wav", 2f));
        Assert.False(l.OnNetworked(Uzi, 2.05f));
    }
}
