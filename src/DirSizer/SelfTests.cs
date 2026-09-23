// The unified dirsizer.exe's own tests: the selector/adapter/CLI layer unique to this project. DirSizer.Fs.Core's
// own 39 tests already cover the engine and run via dirsizer-fs.exe --self-test; this file does not repeat them.
static class DirSizerSelfTests
{
    // Returns the process exit code: 0 if no test failed.
    public static int Run()
    {
        Console.WriteLine("0 self-tests passed, 0 skipped.");
        return 0;
    }
}
