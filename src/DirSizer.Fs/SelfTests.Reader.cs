using System.Runtime.CompilerServices;

// The native structure, the directory reader against a real temporary folder, and its LARGE_FETCH fallback against an injected find-first call.
static partial class FsSelfTests
{
    static partial void AddReaderTests(List<SelfTest> tests)
    {
        tests.Add(new("Win32FindData has the 592-byte native layout and a 64-bit file size", Win32FindDataLayout));
        tests.Add(new("reader lists entries, skips . and .., reports names and attributes", ReaderListsEntries));
        tests.Add(new("reader classifies find-first errors: missing, empty, denied, other", ReaderClassifiesErrors));
        tests.Add(new("LARGE_FETCH: rejected once, retried without, and off from then on", LargeFetchFallsBack));
        tests.Add(new("LARGE_FETCH: a failing retry is an ordinary failure and keeps the flag", LargeFetchKeptWhenRetryFails));
    }

    static void Win32FindDataLayout()
    {
        // 4 (attributes) + 3 * 8 (times) + 4 * 4 (sizes, reserved) + 260 * 2 (name) + 14 * 2 (alternate name)
        AssertEqual(592, Unsafe.SizeOf<Win32FindData>(), "WIN32_FIND_DATAW size");
        // The two halves of the size combine into 64 bits (a file over 4 GiB), without needing a huge file.
        var data = new Win32FindData { FileSizeHigh = 1, FileSizeLow = 5 };
        AssertEqual(4294967301L, Win32Find.FileSize(in data), "64-bit file size");
    }

    sealed class CollectingSink : IEntrySink
    {
        public readonly Dictionary<string, uint> Entries = [];
        public readonly Dictionary<string, long> Sizes = [];

        public void OnEntry(uint attributes, long size, ReadOnlySpan<char> name)
        {
            var key = new string(name);
            Entries[key] = attributes;
            Sizes[key] = size;
        }
    }

    // A find-first call that always fails with the given error, recording whether the LARGE_FETCH flag was set on each call.
    static FindFirstFn AlwaysFails(int error, List<bool> calls) =>
        (string pattern, ref Win32FindData data, bool largeFetch) =>
        {
            calls.Add(largeFetch);
            return new FindFirstResult(Win32Find.InvalidHandle, error);
        };

    static ReadResult ReadWithError(int error, out string calls)
    {
        var list = new List<bool>();
        var result = new FindFirstFactory(true, AlwaysFails(error, list)).Create().Read(@"\\?\C:\anything", new CollectingSink());
        calls = string.Join(',', list);
        return result;
    }

    // Stands in for a filesystem that rejects FIND_FIRST_EX_LARGE_FETCH with the given error; every other call is the real one.
    sealed class LargeFetchRejecter(int error)
    {
        public readonly List<bool> Calls = [];

        public FindFirstResult Call(string pattern, ref Win32FindData data, bool largeFetch)
        {
            lock (Calls) Calls.Add(largeFetch);
            return largeFetch ? new FindFirstResult(Win32Find.InvalidHandle, error) : Win32Find.FindFirst(pattern, ref data, false);
        }
    }

    static void ReaderListsEntries()
    {
        using var tree = new TempTree();
        tree.MakeFile("one.txt", 10);
        tree.MakeFile("\u65e5\u672c\u8a9e.txt", 20);
        tree.MakeDir("sub");
        tree.MakeFile("hidden.txt", 5);
        File.SetAttributes(tree.Full("hidden.txt"), FileAttributes.Hidden);

        var sink = new CollectingSink();
        var result = new FindFirstFactory().Create().Read(tree.Base, sink);
        AssertEqual(ReadOutcome.Complete, result.Outcome, "outcome");
        AssertEqual("hidden.txt,one.txt,sub,\u65e5\u672c\u8a9e.txt", string.Join(',', SortedNames(sink.Entries)), "entries (no . or ..), in ordinal order");
        Assert((sink.Entries["sub"] & Win32Find.DirectoryAttribute) != 0, "sub is a directory");
        Assert((sink.Entries["one.txt"] & Win32Find.DirectoryAttribute) == 0, "one.txt is not a directory");
        Assert((sink.Entries["hidden.txt"] & (uint)FileAttributes.Hidden) != 0, "hidden files are listed, with their attribute");
        AssertEqual(10L, sink.Sizes["one.txt"], "size of one.txt (the size fields are read from the right offsets)");
        AssertEqual(20L, sink.Sizes["日本語.txt"], "size of the Unicode-named file");
    }

    static string[] SortedNames(Dictionary<string, uint> entries)
    {
        var names = new List<string>(entries.Keys);
        names.Sort(StringComparer.Ordinal);
        return names.ToArray();
    }

    static void ReaderClassifiesErrors()
    {
        using var tree = new TempTree();
        var missing = new FindFirstFactory().Create().Read(tree.Full("does-not-exist"), new CollectingSink());
        AssertEqual(ReadOutcome.Failed, missing.Outcome, "a directory that does not exist");
        AssertEqual(3, missing.Error, "ERROR_PATH_NOT_FOUND");

        var empty = ReadWithError(Win32Find.ErrorFileNotFound, out var emptyCalls);
        AssertEqual(ReadOutcome.Complete, empty.Outcome, "ERROR_FILE_NOT_FOUND means an empty directory (a FAT or exFAT root has no dot entries)");
        AssertEqual("True", emptyCalls, "an empty directory is not retried");

        var denied = ReadWithError(Win32Find.ErrorAccessDenied, out var deniedCalls);
        AssertEqual(ReadOutcome.Denied, denied.Outcome, "ERROR_ACCESS_DENIED");
        AssertEqual("True", deniedCalls, "a denied directory is not retried");

        var other = ReadWithError(32, out var otherCalls);   // ERROR_SHARING_VIOLATION
        AssertEqual(ReadOutcome.Failed, other.Outcome, "any other error");
        AssertEqual(32, other.Error, "the error is kept");
        AssertEqual("True", otherCalls, "another error is not retried");
    }

    static void LargeFetchFallsBack()
    {
        using var tree = new TempTree();
        tree.MakeFile("one.txt", 10);
        var rejecter = new LargeFetchRejecter(Win32Find.ErrorInvalidParameter);
        var factory = new FindFirstFactory(true, rejecter.Call);
        var reader = factory.Create();
        Assert(factory.LargeFetch, "the flag starts on");

        var first = reader.Read(tree.Base, new CollectingSink());
        AssertEqual(ReadOutcome.Complete, first.Outcome, "the retry without the flag succeeds");
        Assert(!factory.LargeFetch, "the flag is off after a successful retry");
        var second = reader.Read(tree.Base, new CollectingSink());
        AssertEqual(ReadOutcome.Complete, second.Outcome, "later reads work");
        AssertEqual("True,False,False", string.Join(',', rejecter.Calls), "calls: with the flag (rejected), retry without, then never with the flag again");
    }

    static void LargeFetchKeptWhenRetryFails()
    {
        var calls = new List<bool>();
        FindFirstResult AlwaysInvalidParameter(string pattern, ref Win32FindData data, bool largeFetch)
        {
            calls.Add(largeFetch);
            return new FindFirstResult(Win32Find.InvalidHandle, Win32Find.ErrorInvalidParameter);
        }
        var factory = new FindFirstFactory(true, AlwaysInvalidParameter);
        var result = factory.Create().Read(@"\\?\C:\anything", new CollectingSink());
        AssertEqual(ReadOutcome.Failed, result.Outcome, "outcome");
        AssertEqual(Win32Find.ErrorInvalidParameter, result.Error, "error");
        AssertEqual("True,False", string.Join(',', calls), "exactly one retry");
        Assert(factory.LargeFetch, "the flag stays on: the retry failed too, so the flag was not the cause");
    }
}
