using System.Runtime.CompilerServices;

// The native structure, the directory reader against a real temporary folder, and its LARGE_FETCH fallback against an injected find-first call.
static partial class FsSelfTests
{
    static partial void AddReaderTests(List<SelfTest> tests)
    {
        tests.Add(new("Win32FindData has the 592-byte native layout", Win32FindDataLayout));
        tests.Add(new("reader lists entries, skips . and .., reports names and attributes", ReaderListsEntries));
        tests.Add(new("reader reports a missing directory as failed", ReaderMissingDirectory));
        tests.Add(new("LARGE_FETCH: rejected once, retried without, and off from then on", LargeFetchFallsBack));
        tests.Add(new("LARGE_FETCH: a failing retry is an ordinary failure and keeps the flag", LargeFetchKeptWhenRetryFails));
    }

    static void Win32FindDataLayout()
    {
        // 4 (attributes) + 3 * 8 (times) + 4 * 4 (sizes, reserved) + 260 * 2 (name) + 14 * 2 (alternate name)
        AssertEqual(592, Unsafe.SizeOf<Win32FindData>(), "WIN32_FIND_DATAW size");
    }

    sealed class CollectingSink : IEntrySink
    {
        public readonly Dictionary<string, uint> Entries = [];

        public void OnEntry(in Win32FindData entry) => Entries[Win32Find.NameString(in entry)] = entry.FileAttributes;
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
        var data = new Win32FindData();
        var result = new DirectoryReader().Read(tree.Base, ref data, sink);
        AssertEqual(ReadOutcome.Complete, result.Outcome, "outcome");
        AssertEqual("hidden.txt,one.txt,sub,\u65e5\u672c\u8a9e.txt", string.Join(',', SortedNames(sink.Entries)), "entries (no . or ..), in ordinal order");
        Assert((sink.Entries["sub"] & Win32Find.DirectoryAttribute) != 0, "sub is a directory");
        Assert((sink.Entries["one.txt"] & Win32Find.DirectoryAttribute) == 0, "one.txt is not a directory");
        Assert((sink.Entries["hidden.txt"] & (uint)FileAttributes.Hidden) != 0, "hidden files are listed, with their attribute");
    }

    static string[] SortedNames(Dictionary<string, uint> entries)
    {
        var names = new List<string>(entries.Keys);
        names.Sort(StringComparer.Ordinal);
        return names.ToArray();
    }

    static void ReaderMissingDirectory()
    {
        using var tree = new TempTree();
        var data = new Win32FindData();
        var result = new DirectoryReader().Read(tree.Full("does-not-exist"), ref data, new CollectingSink());
        AssertEqual(ReadOutcome.Failed, result.Outcome, "outcome");
        Assert(result.Error is 2 or 3, $"expected ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND, got {result.Error}");
    }

    static void LargeFetchFallsBack()
    {
        using var tree = new TempTree();
        tree.MakeFile("one.txt", 10);
        var rejecter = new LargeFetchRejecter(Win32Find.ErrorInvalidParameter);
        var reader = new DirectoryReader(rejecter.Call);
        Assert(reader.LargeFetch, "the flag starts on");
        var data = new Win32FindData();

        var first = reader.Read(tree.Base, ref data, new CollectingSink());
        AssertEqual(ReadOutcome.Complete, first.Outcome, "the retry without the flag succeeds");
        Assert(!reader.LargeFetch, "the flag is off after a successful retry");
        var second = reader.Read(tree.Base, ref data, new CollectingSink());
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
        var reader = new DirectoryReader(AlwaysInvalidParameter);
        var data = new Win32FindData();
        var result = reader.Read(@"\\?\C:\anything", ref data, new CollectingSink());
        AssertEqual(ReadOutcome.Failed, result.Outcome, "outcome");
        AssertEqual(Win32Find.ErrorInvalidParameter, result.Error, "error");
        AssertEqual("True,False", string.Join(',', calls), "exactly one retry");
        Assert(reader.LargeFetch, "the flag stays on: the retry failed too, so the flag was not the cause");
    }
}
