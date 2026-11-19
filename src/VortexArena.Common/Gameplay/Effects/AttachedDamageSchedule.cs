namespace VortexArena.Common.Gameplay;

/// <summary>Client-clock lifetime and emission cadence for one model attachment.</summary>
public struct AttachedDamageSchedule(float start, float lifetime, bool dead)
{
    private readonly float _expires = start + lifetime;
    private float _nextEmission = start;
    private bool _wasDead = dead;

    public static float Lifetime(byte damage, float scale, float min, float max)
        => MathF.Max(min, MathF.Min(damage * scale, max));

    public bool IsFinished(float now, bool dead)
    {
        bool respawned = _wasDead && !dead;
        _wasDead = dead;
        return respawned || now >= _expires;
    }

    public bool Tick(float now, float ticrate, int ownerCount, bool distribute)
    {
        if (now < _nextEmission)
            return false;
        _nextEmission = now + ticrate * (distribute ? ownerCount : 1);
        return true;
    }
}
