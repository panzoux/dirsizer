using System.ComponentModel;

Options options;
try
{
    options = Options.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}
if (options.Help)
{
    Options.PrintHelp("dirsizer-bulk", """
        EXPERIMENTAL. Reads the raw $MFT in large blocks and then runs the same parsing,
        merging, and aggregation as dirsizer-fsctl. About 1.4x faster in measurements
        (it varies). It never falls back to dirsizer-fsctl: a failure is reported and
        exits with 1.

        Exit codes: 0 = ok; 1 = error; 3 = the result was produced, but the MFT layout
        changed while it was read (even after one rescan), so it is not a consistent
        snapshot. Changes inside existing records during the scan are not detected.
        """);
    return 0;
}
if (options.SelfTest)
{
    SelfTests.Run();
    BulkSelfTests.Run();
    return 0;
}

try
{
    var result = BulkIntegration.Run(options);
    Output.Write(result, options);
    return result.ExitCode;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    if (exception is UnauthorizedAccessException || exception is Win32Exception win32 && win32.NativeErrorCode is 5 or 1314)
        Console.Error.WriteLine("Open the terminal as Administrator. The scanner is read-only and requires NTFS volume access.");
    return 1;
}