using System.Text.Json;

namespace QuantConnect.Genetic;

public sealed record PortfolioDrawdownState(int Version, decimal HighWatermark, bool Tripped);

/// <summary>Maintains a latched portfolio drawdown limit across LEAN process restarts.</summary>
public sealed class PortfolioDrawdownGuard
{
    private const int StateVersion = 1;
    private readonly decimal _maximumDrawdownFraction;
    private readonly string? _stateFile;

    public decimal HighWatermark { get; private set; }

    public bool IsTripped { get; private set; }

    public PortfolioDrawdownGuard(decimal maximumDrawdownFraction, decimal initialEquity, string? stateFile = null)
    {
        if (maximumDrawdownFraction is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(maximumDrawdownFraction));
        if (initialEquity <= 0) throw new ArgumentOutOfRangeException(nameof(initialEquity));

        _maximumDrawdownFraction = maximumDrawdownFraction;
        _stateFile = string.IsNullOrWhiteSpace(stateFile) ? null : Path.GetFullPath(stateFile);
        var state = _stateFile is null ? null : LoadState(_stateFile);

        HighWatermark = Math.Max(initialEquity, state?.HighWatermark ?? 0m);
        IsTripped = state?.Tripped ?? false;
        Update(initialEquity);
    }

    public bool Update(decimal equity)
    {
        if (IsTripped) return true;

        if (equity <= 0)
        {
            IsTripped = true;
            Persist();
            return true;
        }

        var changed = false;
        if (equity > HighWatermark)
        {
            HighWatermark = equity;
            changed = true;
        }

        if (equity / HighWatermark - 1m <= -_maximumDrawdownFraction)
        {
            IsTripped = true;
            changed = true;
        }

        if (changed) Persist();
        return IsTripped;
    }

    public static PortfolioDrawdownState? LoadState(string stateFile)
    {
        if (!File.Exists(stateFile)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<PortfolioDrawdownState>(File.ReadAllText(stateFile))
                ?? throw new InvalidDataException("Portfolio drawdown state is empty.");
            if (state.Version != StateVersion || state.HighWatermark <= 0)
                throw new InvalidDataException("Portfolio drawdown state has an unsupported version or invalid high watermark.");
            return state;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Portfolio drawdown state is unreadable; refusing to reset the account high watermark.", exception);
        }
    }

    private void Persist()
    {
        if (_stateFile is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
        var temporaryPath = _stateFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var state = new PortfolioDrawdownState(StateVersion, HighWatermark, IsTripped);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state));
            File.Move(temporaryPath, _stateFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
