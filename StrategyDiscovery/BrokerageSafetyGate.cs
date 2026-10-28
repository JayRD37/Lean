namespace QuantConnect.Genetic;

/// <summary>Controls whether a strategy may submit new orders after brokerage incidents.</summary>
public sealed class BrokerageSafetyGate
{
    public bool IsConnected { get; private set; } = true;

    public bool IsTripped { get; private set; }

    public string? TripReason { get; private set; }

    public bool CanSubmitOrders => IsConnected && !IsTripped;

    public bool MustLiquidate => IsTripped;

    public void Disconnect() => IsConnected = false;

    public void Reconnect()
    {
        if (!IsTripped) IsConnected = true;
    }

    public void Trip(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A trip reason is required.", nameof(reason));
        IsTripped = true;
        TripReason ??= reason;
    }
}
