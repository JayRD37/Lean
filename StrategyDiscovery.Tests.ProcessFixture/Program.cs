using System.Diagnostics;
using System.Reflection;

if (args.Length == 1 && args[0] == "--hold")
{
    Console.WriteLine("READY");
    Console.Out.Flush();
    await Task.Delay(Timeout.InfiniteTimeSpan);
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

Console.Error.WriteLine("Usage: --launch-child-and-exit <metadata-path> | --hold");
return 2;
