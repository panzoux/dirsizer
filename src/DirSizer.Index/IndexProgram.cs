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
Console.Error.WriteLine("error: scanning is not wired in yet (Task 7 of the plan).");
return 1;
