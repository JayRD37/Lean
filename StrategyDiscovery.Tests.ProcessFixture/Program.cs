using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;

if (args.Length == 2 && args[0] == "--config")
{
    var config = JsonNode.Parse(File.ReadAllText(args[1]))?.AsObject()
        ?? throw new InvalidDataException("Fixture config is empty.");
    var mode = config["fixture-mode"]?.GetValue<string>();
    var stateFile = config["fixture-state-file"]?.GetValue<string>()
        ?? throw new InvalidDataException("Fixture state path is missing.");

    if (mode == "optimizer")
    {
        var resultsDirectory = config["results-destination-folder"]?.GetValue<string>()
            ?? throw new InvalidDataException("Optimizer results directory is missing.");
        Directory.CreateDirectory(resultsDirectory);
        var genome = new JsonObject
        {
            ["Family"] = 0,
            ["Timeframe"] = 2,
            ["FastPeriod"] = 10,
            ["SlowPeriod"] = 40,
            ["EntryThresholdTenths"] = 12,
            ["ExitThresholdTenths"] = 4,
            ["MaxPositions"] = 3,
            ["MaxExposurePercent"] = 30,
            ["EntryThreshold"] = 1.2,
            ["ExitThreshold"] = 0.4
        };
        var rankings = new JsonObject
        {
            ["randomizedBaselineComparison"] = new JsonObject
            {
                ["baselineValidationCount"] = 1,
                ["medianBaselineFitness"] = 0.1
            },
            ["validation"] = new JsonArray
            {
                new JsonObject
                {
                    ["Population"] = 0,
                    ["RobustnessAdjustedFitness"] = 0.5,
                    ["Genome"] = genome.DeepClone(),
                    ["ParameterRobustness"] = new JsonObject
                    {
                        ["RequestedProbeCount"] = 1,
                        ["ValidProbeCount"] = 1
                    },
                    ["Windows"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["Genome"] = genome.DeepClone(),
                            ["Metrics"] = new JsonObject { ["TradeCount"] = 20, ["Drawdown"] = 0.05 }
                        }
                    }
                }
            }
        };
        File.WriteAllText(Path.Combine(resultsDirectory, "genetic-search-rankings.json"), rankings.ToJsonString());
        File.AppendAllText(stateFile, $"optimizer|{Environment.ProcessId}{Environment.NewLine}");
        return 0;
    }

    if (mode == "paper")
    {
        var launches = File.Exists(stateFile)
            ? File.ReadAllLines(stateFile).Count(line => line.StartsWith("paper|", StringComparison.Ordinal))
            : 0;
        File.AppendAllText(stateFile, $"paper|{launches + 1}|{Environment.ProcessId}{Environment.NewLine}");

        var killSwitch = config["parameters"]?["discovery.kill-switch-file"]?.GetValue<string>()
            ?? throw new InvalidDataException("Paper kill switch is missing.");
        Console.WriteLine("PAPER_READY");
        Console.Out.Flush();
        while (!File.Exists(killSwitch)) await Task.Delay(20);
        return 0;
    }

    throw new InvalidDataException($"Unknown fixture mode '{mode}'.");
}

if (args.Length == 1 && args[0] == "--hold")
{
    Console.WriteLine("READY");
    Console.Out.Flush();
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

if (args.Length == 3 && args[0] == "--exit-when-file")
{
    Console.WriteLine("READY");
    Console.Out.Flush();
    while (!File.Exists(args[1])) await Task.Delay(20);
    File.WriteAllText(args[2], "stopped");
    return 0;
}

if (args.Length == 2 && args[0] == "--launch-child-and-exit")
{
    var startInfo = new ProcessStartInfo(Environment.ProcessPath ?? "dotnet")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    startInfo.ArgumentList.Add("--hold");

    using var child = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start the child fixture process.");
    var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
    if (!string.Equals(ready, "READY", StringComparison.Ordinal))
    {
        child.Kill(entireProcessTree: true);
        throw new InvalidOperationException("The child fixture process did not report ready.");
    }

    File.WriteAllText(args[1], $"{child.Id}|{child.StartTime.ToUniversalTime():O}");
    return 0;
}

if (args.Length == 4 && args[0] == "--launch-recoverable-child-and-exit")
{
    var startInfo = new ProcessStartInfo(Environment.ProcessPath ?? "dotnet")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    startInfo.ArgumentList.Add("--exit-when-file");
    startInfo.ArgumentList.Add(args[2]);
    startInfo.ArgumentList.Add(args[3]);

    using var child = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start the recoverable paper-child fixture.");
    var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
    if (!string.Equals(ready, "READY", StringComparison.Ordinal))
    {
        child.Kill(entireProcessTree: true);
        throw new InvalidOperationException("The recoverable paper child did not report ready.");
    }

    File.WriteAllText(args[1], $"{child.Id}|{child.StartTime.ToUniversalTime():O}");
    return 0;
}

Console.Error.WriteLine("Usage: --config <path> | --launch-child-and-exit <metadata-path> | --launch-recoverable-child-and-exit <metadata-path> <stop-path> <done-path> | --exit-when-file <stop-path> <done-path> | --hold");
return 2;
