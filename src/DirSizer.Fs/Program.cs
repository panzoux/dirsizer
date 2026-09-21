FsOptions options;
try
{
    options = FsOptions.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}
if (options.Help)
{
    FsOptions.PrintHelp();
    return 0;
}
if (options.SelfTest) return FsSelfTests.Run();

// Not disposed on purpose: the Ctrl+C handler can still run while the process ends, and cancelling a disposed source throws.
var cancel = new CancellationTokenSource();
var scanning = true;
Console.CancelKeyPress += (_, e) =>
{
    // Only the scan observes the token. Once it is over, Ctrl+C keeps its usual meaning, so that a long output can still be stopped.
    if (!Volatile.Read(ref scanning)) return;
    e.Cancel = true;
    cancel.Cancel();
};
try
{
    var settings = new ScanSettings(
        options.Workers == 0 ? FsOptions.DefaultWorkers : options.Workers,
        options.Top,
        options.Files,
        ShowProgress: !Console.IsErrorRedirected,
        cancel.Token);
    var result = FsScanner.Scan(options.Root, settings);
    Volatile.Write(ref scanning, false);
    FsOutput.Write(result, options, Console.Out, Console.Error);
    return options.Strict && result.Counters.Unreadable > 0 ? 3 : 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("error: canceled");
    return 1;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}
