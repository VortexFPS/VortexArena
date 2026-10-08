namespace VortexArena.Modding;

/// <summary>
/// A token bucket over two quantities at once - how many messages and how many bytes - used on both
/// ends of the mod channel and for offers and uploads. The caller supplies the clock (seconds, any
/// origin), so behaviour is the same in a test as in a game; a clock that jumps backwards or returns
/// NaN grants nothing instead of refilling.
/// </summary>
public sealed class ModRateLimiter
{
    private readonly double _perSecond, _burst, _bytesPerSecond, _burstBytes;
    private double _tokens, _byteTokens;
    private double _last = double.NaN;

    /// <param name="perSecond">Sustained messages per second.</param>
    /// <param name="burst">Messages that may be sent at once after a quiet spell.</param>
    /// <param name="bytesPerSecond">Sustained bytes per second; zero or less means bytes are not limited.</param>
    /// <param name="burstBytes">Bytes that may be sent at once after a quiet spell.</param>
    public ModRateLimiter(double perSecond, double burst, double bytesPerSecond = 0, double burstBytes = 0)
    {
        _perSecond = Math.Max(0, perSecond);
        _burst = Math.Max(1, burst);
        _bytesPerSecond = bytesPerSecond;
        _burstBytes = Math.Max(0, burstBytes);
        _tokens = _burst;
        _byteTokens = _burstBytes;
    }

    /// <summary>Takes one message of <paramref name="bytes"/> bytes from the budget, or returns false and takes nothing.</summary>
    public bool TryConsume(int bytes, double now)
    {
        Refill(now);
        if (bytes < 0 || _tokens < 1) return false;
        if (_bytesPerSecond > 0)
        {
            if (_byteTokens < bytes) return false;
            _byteTokens -= bytes;
        }
        _tokens -= 1;
        return true;
    }

    /// <summary>Bytes that could be consumed right now (for sizing the next piece of an upload).</summary>
    public int AvailableBytes(double now)
    {
        Refill(now);
        if (_tokens < 1) return 0;
        return _bytesPerSecond > 0 ? (int)Math.Min(int.MaxValue, _byteTokens) : int.MaxValue;
    }

    private void Refill(double now)
    {
        if (double.IsNaN(now) || double.IsInfinity(now)) return;
        if (double.IsNaN(_last) || now < _last)
        {
            _last = now;
            return;
        }
        double elapsed = now - _last;
        _last = now;
        _tokens = Math.Min(_burst, _tokens + elapsed * _perSecond);
        if (_bytesPerSecond > 0) _byteTokens = Math.Min(_burstBytes, _byteTokens + elapsed * _bytesPerSecond);
    }
}
