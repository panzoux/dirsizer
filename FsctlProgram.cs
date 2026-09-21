using System.ComponentModel;

ConsolePause.Register();
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
    Options.PrintHelp("dirsizer-fsctl", """
        Reads the MFT one record at a time with FSCTL_GET_NTFS_FILE_RECORD. This is the
        reference implementation: correctness is defined by it. dirsizer-bulk produces
        the same result by reading the raw $MFT in large blocks (experimental).
        """);
    return 0;
}
if (options.SelfTest)
{
    SelfTests.Run();
    return 0;
}

try
{
    var result = new Scanner(options).Run();
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