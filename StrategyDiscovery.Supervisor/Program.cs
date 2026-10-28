namespace QuantConnect.Genetic.Supervisor;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("Usage: dotnet QuantConnect.Genetic.Supervisor.dll --config <supervisor.json> --enable-paper");
            Console.WriteLine("The supervisor supports Alpaca paper trading only. It has no live-money mode.");
            return 0;
        }

        var configIndex = Array.IndexOf(args, "--config");
        var enabled = args.Contains("--enable-paper", StringComparer.Ordinal);
        if (configIndex < 0 || configIndex + 1 >= args.Length || !enabled)
        {
            Console.Error.WriteLine("Refusing to start: provide --config <path> and explicit --enable-paper.");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
            Console.Error.WriteLine("Shutdown requested; the paper algorithm will liquidate managed positions before exit.");
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var settings = SupervisorSettings.Load(args[configIndex + 1]);
            await new SupervisorHost(settings).RunAsync(cancellation.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }
}
