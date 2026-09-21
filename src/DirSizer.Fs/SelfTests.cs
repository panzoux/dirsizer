using System.Diagnostics;
using System.Runtime.ExceptionServices;

// The built-in tests (dirsizer --self-test). They need no elevation and no volume: file-system tests build a fixture in a
// temporary folder and remove it afterwards. Each group of tests lives in its own file and registers itself through one of
// the partial methods below; a partial method with no body in the build is simply not called.

readonly record struct SelfTest(string Name, Action Body);

// A test that cannot run in this environment (for example no permission to create a junction) skips itself visibly.
sealed class SkipException(string reason) : Exception(reason);

static partial class FsSelfTests
{
    static partial void AddCliTests(List<SelfTest> tests);
    static partial void AddModelTests(List<SelfTest> tests);
    static partial void AddReaderTests(List<SelfTest> tests);
    static partial void AddWalkTests(List<SelfTest> tests);
    static partial void AddOutputTests(List<SelfTest> tests);

    // Returns the process exit code: 0 if no test failed.
    public static int Run()
    {
        var tests = new List<SelfTest>();
        AddCliTests(tests);
        AddModelTests(tests);
        AddReaderTests(tests);
        AddWalkTests(tests);
        AddOutputTests(tests);

        var failed = 0;
        var skipped = 0;
        foreach (var test in tests)
        {
            try
            {
                RunWithTimeout(test);
                Console.WriteLine($"ok    {test.Name}");
            }
            catch (SkipException skip)
            {
                skipped++;
                Console.WriteLine($"skip  {test.Name}: {skip.Message}");
            }
            catch (Exception exception)
            {
                failed++;
                // A failed assertion is a plain Exception; anything else is a surprise, so its type is worth seeing.
                var text = exception.GetType() == typeof(Exception) ? exception.Message : $"{exception.GetType().Name}: {exception.Message}";
                Console.WriteLine($"FAIL  {test.Name}: {text}");
            }
        }
        Console.WriteLine(failed == 0
            ? $"{tests.Count - skipped} self-tests passed, {skipped} skipped."
            : $"{failed} of {tests.Count} self-tests FAILED.");
        return failed == 0 ? 0 : 1;
    }

    static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(120);

    // A test that hangs (a deadlock in the walk would) must fail, not stop the whole run. The body runs on its own background
    // thread; if it does not finish in time the test fails and the run goes on.
    static void RunWithTimeout(SelfTest test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test.Body();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }) { IsBackground = true, Name = "self-test" };
        thread.Start();
        if (!thread.Join(TestTimeout)) throw new Exception($"timed out after {TestTimeout.TotalSeconds:N0} s (a hang or a deadlock)");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static void AssertEqual<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    static void AssertThrows<TException>(Action action, string what) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new Exception($"{what}: expected {typeof(TException).Name}, nothing was thrown");
    }

    // Runs a program and returns its exit code, or -1 if it cannot be started.
    static int RunProgram(string fileName, string arguments)
    {
        try
        {
            var info = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(info);
            if (process is null) return -1;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return -1;
        }
    }
}

// A temporary directory tree. Root is the display form; every file-system operation of a fixture goes through Full(), which uses
// the extended-length form so that fixtures can have paths over 260 characters.
sealed class TempTree : IDisposable
{
    readonly List<Action> _cleanup = [];

    public string Root { get; }
    public string Base { get; }

    public TempTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "dirsizer-fs-selftest-" + Guid.NewGuid().ToString("N"));
        Base = @"\\?\" + Root;
        Directory.CreateDirectory(Base);
    }

    public string Full(string relative) => relative.Length == 0 ? Base : Base + "\\" + relative;

    public void MakeDir(string relative) => Directory.CreateDirectory(Full(relative));

    // A file whose end-of-file is `size` (no data is written; the size is what the directory listing reports).
    public void MakeFile(string relative, long size)
    {
        var path = Full(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        stream.SetLength(size);
    }

    // Runs when the tree is disposed, before it is deleted (last registered first). For undoing things that block deletion.
    public void OnDispose(Action cleanup) => _cleanup.Add(cleanup);

    public void Dispose()
    {
        for (var index = _cleanup.Count - 1; index >= 0; index--)
        {
            try
            {
                _cleanup[index]();
            }
            catch (Exception)
            {
                // best effort: a failed cleanup must not hide the test result
            }
        }
        try
        {
            Directory.Delete(Base, true);
        }
        catch (Exception)
        {
            // best effort
        }
    }
}
