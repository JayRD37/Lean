namespace QuantConnect.Genetic;

/// <summary>Controls whether a strategy may submit new orders after brokerage incidents.</summary>
public sealed class BrokerageSafetyGate
{
    private readonly object _sync = new();
    private bool _isConnected = true;
    private bool _isTripped;
    private string? _tripReason;

    public bool IsConnected
    {
        get { lock (_sync) return _isConnected; }
    }

    public bool IsTripped
    {
        get { lock (_sync) return _isTripped; }
    }

    public string? TripReason
    {
        get { lock (_sync) return _tripReason; }
    }

    public bool CanSubmitOrders
    {
        get { lock (_sync) return _isConnected && !_isTripped; }
    }

    public bool CanLiquidate
    {
        get { lock (_sync) return _isConnected; }
    }

    public bool MustLiquidate
    {
        get { lock (_sync) return _isTripped; }
    }

    public void Disconnect()
    {
        lock (_sync) _isConnected = false;
    }

    public void Reconnect()
    {
        lock (_sync)
        {
            if (!_isTripped) _isConnected = true;
        }
    }

    public void Trip(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A trip reason is required.", nameof(reason));
        lock (_sync)
        {
            _tripReason ??= reason;
            _isTripped = true;
        }
    }
}
