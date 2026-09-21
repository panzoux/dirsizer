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

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
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
