UnifiedCliOptions options;
try
{
    options = UnifiedCliOptions.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}
if (options.Help)
{
    UnifiedCliOptions.PrintHelp();
    return 0;
}
if (options.SelfTest) return DirSizerSelfTests.Run();

try
{
    var result = ScanStrategySelector.Scan(options.Root, options.ToUnifiedScanOptions());
    UnifiedOutput.Write(result, options, Console.Out, Console.Error);
    return options.Strict && result.Unreadable > 0 ? 3 : 0;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}
