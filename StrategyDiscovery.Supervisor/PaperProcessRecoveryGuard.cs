using System.Diagnostics;
using Newtonsoft.Json;
using QuantConnect.Genetic;

namespace QuantConnect.Genetic.Supervisor;

public sealed record PaperProcessIdentity(
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    string? ConfigPath = null,
    StrategyGenome? Genome = null,
    double? Fitness = null,
    StrategyPopulation? Population = null,
    int ValidationWindowCount = 0);

/// <summary>Detects orphaned LEAN paper children so a restarted supervisor cannot create a duplicate session.</summary>
public static class PaperProcessRecoveryGuard
{
    private static readonly TimeSpan ProcessStartTimeTolerance = TimeSpan.FromSeconds(2);

    public static PaperProcessIdentity? FindOrphan(string statePath, Func<int, DateTimeOffset?> getProcessStartTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(getProcessStartTimeUtc);
        if (!File.Exists(statePath)) return null;

        PaperProcessIdentity identity;
        try
        {
            identity = JsonConvert.DeserializeObject<PaperProcessIdentity>(File.ReadAllText(statePath))
                ?? throw new InvalidDataException("Active paper session state is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Active paper session state is unreadable; refusing to start a second paper session.", exception);
        }

        if (identity.ProcessId < 1) throw new InvalidDataException("Active paper session state contains an invalid process ID.");
        var actualStartTime = getProcessStartTimeUtc(identity.ProcessId);
        if (actualStartTime is null || !MatchesStartTime(identity, actualStartTime.Value))
        {
            DeleteEphemeralConfig(identity.ConfigPath);
            File.Delete(statePath);
            return null;
        }

        return identity;
    }

    public static bool MatchesStartTime(PaperProcessIdentity identity, DateTimeOffset actualStartTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return (actualStartTimeUtc - identity.StartedAtUtc).Duration() <= ProcessStartTimeTolerance;
    }

    public static void Save(string statePath, PaperProcessIdentity identity)
    {
        if (identity.ProcessId < 1) throw new ArgumentOutOfRangeException(nameof(identity), "Process ID must be positive.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);
        var temporaryPath = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(identity));
            File.Move(temporaryPath, statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static void ClearIfOwned(string statePath, PaperProcessIdentity identity)
    {
        var current = FindOrphan(statePath, processId => processId == identity.ProcessId ? identity.StartedAtUtc : null);
        if (current == identity && File.Exists(statePath)) File.Delete(statePath);
    }

    private static void DeleteEphemeralConfig(string? configPath)
    {
        if (!string.IsNullOrWhiteSpace(configPath)) PaperConfigFactory.DeleteEphemeralConfig(configPath);
    }

    public static DateTimeOffset? GetProcessStartTimeUtc(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return new DateTimeOffset(process.StartTime.ToUniversalTime());
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
