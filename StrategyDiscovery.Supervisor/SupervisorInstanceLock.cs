namespace QuantConnect.Genetic.Supervisor;

/// <summary>Holds an exclusive output-directory lock for the lifetime of one supervisor.</summary>
public sealed class SupervisorInstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private SupervisorInstanceLock(FileStream stream) => _stream = stream;

    public static SupervisorInstanceLock Acquire(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var lockPath = Path.Combine(outputDirectory, "supervisor.lock");
        try
        {
            return new SupervisorInstanceLock(new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException($"Another supervisor already holds '{lockPath}'.", exception);
        }
    }

    public void Dispose() => _stream.Dispose();
}
