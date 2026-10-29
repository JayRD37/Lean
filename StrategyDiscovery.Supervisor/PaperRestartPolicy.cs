namespace QuantConnect.Genetic.Supervisor;

public sealed class PaperRestartPolicy(int maximumConsecutiveFailures, TimeSpan healthyRunThreshold, TimeSpan initialBackoff)
{
    public int ConsecutiveFailures { get; private set; }

    public TimeSpan RegisterExit(int exitCode, TimeSpan uptime)
    {
        if (maximumConsecutiveFailures < 1) throw new ArgumentOutOfRangeException(nameof(maximumConsecutiveFailures));
        if (healthyRunThreshold <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(healthyRunThreshold));
        if (initialBackoff <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(initialBackoff));

        if (exitCode == 0 && uptime >= healthyRunThreshold)
        {
            ConsecutiveFailures = 0;
            return TimeSpan.Zero;
        }

        ConsecutiveFailures++;
        if (ConsecutiveFailures >= maximumConsecutiveFailures)
            throw new InvalidOperationException($"Paper process failed {ConsecutiveFailures} consecutive times; automatic restart is stopped for operator review.");

        var multiplier = Math.Pow(2, ConsecutiveFailures - 1);
        return TimeSpan.FromTicks((long)Math.Min(initialBackoff.Ticks * multiplier, TimeSpan.FromHours(1).Ticks));
    }
}
