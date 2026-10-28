namespace Sample;

public sealed class RateLimiter
{
    private readonly int _capacity;
    private int _tokens;

    public RateLimiter(int capacity)
    {
        _capacity = capacity;
        _tokens = capacity;
    }

    public bool TryAcquire()
    {
        if (_tokens == 0) return false;
        _tokens--;
        return true;
    }

    public void Refill() => _tokens = _capacity;
}
