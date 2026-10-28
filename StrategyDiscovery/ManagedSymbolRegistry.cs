namespace QuantConnect.Genetic;

/// <summary>Tracks configured anchors and changing universe members for account-risk budgeting.</summary>
public sealed class ManagedSymbolRegistry<T> where T : notnull
{
    private readonly HashSet<T> _anchors = new();
    private readonly HashSet<T> _activeUniverseMembers = new();
    private readonly HashSet<T> _managed = new();

    public IEnumerable<T> Symbols => _managed;

    public bool Contains(T symbol) => _managed.Contains(symbol);

    public bool IsAnchor(T symbol) => _anchors.Contains(symbol);

    public void AddAnchor(T symbol)
    {
        _anchors.Add(symbol);
        _managed.Add(symbol);
    }

    public void AddUniverseMember(T symbol)
    {
        _activeUniverseMembers.Add(symbol);
        _managed.Add(symbol);
    }

    public void RemoveUniverseMember(T symbol, bool invested)
    {
        if (_anchors.Contains(symbol)) return;
        _activeUniverseMembers.Remove(symbol);
        if (!invested) _managed.Remove(symbol);
    }

    /// <summary>Forget a removed universe member after its account position has closed.</summary>
    public bool OnPositionStateChanged(T symbol, bool invested)
    {
        if (invested || _anchors.Contains(symbol) || _activeUniverseMembers.Contains(symbol)) return false;
        return _managed.Remove(symbol);
    }
}
