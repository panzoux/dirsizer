ConsolePause.Register();
IndexOptions options;
try
{
    options = IndexOptions.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}
if (options.Help)
{
    IndexOptions.PrintHelp();
    return 0;
}
if (options.SelfTest)
{
    SelfTests.Run();
    return IndexSelfTests.Run();
}
try
{
    var run = IndexRunner.Run(options);
    IndexOutput.Write(run, options, Console.Out, Console.Error);
    return run.ExitCode;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException or InvalidDataException)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    if (exception is UnauthorizedAccessException || exception is System.ComponentModel.Win32Exception win32 && win32.NativeErrorCode is 5 or 1314)
        Console.Error.WriteLine("Open the terminal as Administrator. dirsizer-index reads NTFS metadata and requires volume access.");
    return 1;
}
