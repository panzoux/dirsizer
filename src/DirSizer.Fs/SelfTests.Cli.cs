// Root path handling and command-line options.
static partial class FsSelfTests
{
    static partial void AddCliTests(List<SelfTest> tests)
    {
        tests.Add(new("root paths are normalized to display and extended forms", RootPathForms));
        tests.Add(new("options: defaults and values", OptionsAccepted));
        tests.Add(new("options: bad input is rejected", OptionsRejected));
        tests.Add(new("--enumerator: specs give their canonical form, bad ones are rejected", EnumeratorSpecs));
    }

    static void EnumeratorSpecs()
    {
        var good = new (string Text, string Canonical)[]
        {
            ("find", "find"), ("FIND", "find"), ("find:nolarge", "find:nolarge"),
            ("handle", "handle:full:64"), ("handle:idextd", "handle:idextd:64"), ("handle:idextd:4", "handle:idextd:4"), ("handle:full:1024", "handle:full:1024"),
            ("nt", "nt:dir:64"), ("nt:idextd", "nt:idextd:64"), ("nt:full:256", "nt:full:256"), ("NT:Dir:4", "nt:dir:4"),
            ("auto", "auto"), ("AUTO", "auto"),
        };
        foreach (var (text, canonical) in good) AssertEqual(canonical, EnumeratorSpec.Parse(text).Canonical, $"--enumerator {text}");
        foreach (var text in new[] { "", "bogus", "find:full", "find:nolarge:4", "handle:dir", "handle:", "handle::64", "handle:full:3", "handle:full:1025", "handle:full:abc", "nt:foo", "nt:full:64:1", "auto:full", "auto:64" })
            AssertThrows<ArgumentException>(() => EnumeratorSpec.Parse(text), $"--enumerator '{text}' is rejected");

        AssertEqual("find", FsOptions.Parse(["x"]).Enumerator.Canonical, "the default enumerator is still find (this branch does not change it)");
        AssertEqual("auto", EnumeratorSpec.Parse("auto").CreateFactory().Name, "auto resolves to a factory named auto");
        Assert(EnumeratorSpec.Parse("auto").CreateFactory() is AutoFactory, "auto's factory is really an AutoFactory");
        AssertEqual("nt:dir:4", FsOptions.Parse(["x", "--enumerator=nt:dir:4"]).Enumerator.Canonical, "--enumerator=SPEC");
        AssertEqual("handle:idextd:64", FsOptions.Parse(["x", "--enumerator", "handle:idextd"]).Enumerator.Canonical, "--enumerator SPEC");
        AssertThrows<ArgumentException>(() => FsOptions.Parse(["x", "--enumerator"]), "--enumerator without a value");
        AssertThrows<ArgumentException>(() => FsOptions.Parse(["x", "--enumerator=bogus"]), "--enumerator with a bad value");
    }

    static void RootPathForms()
    {
        AssertEqual(@"C:\", RootPath.Normalize("C:").Display, "drive letter alone is the drive root");
        AssertEqual(@"C:\", RootPath.Normalize(@"C:\").Display, "drive root keeps its backslash");
        AssertEqual(@"C:\Users", RootPath.Normalize(@"C:\Users\").Display, "trailing backslash is trimmed");
        AssertEqual(@"C:\a\c", RootPath.Normalize(@"C:\a\b\..\c").Display, ".. is resolved");
        AssertEqual(@"\\?\C:\Users", RootPath.Normalize(@"C:\Users").Extended, "extended form of a drive path");
        AssertEqual(@"\\?\C:\", RootPath.Normalize(@"C:\").Extended, "extended form of a drive root");
        AssertEqual(@"\\server\share", RootPath.Normalize(@"\\server\share\").Display, "share root display");
        AssertEqual(@"\\?\UNC\server\share", RootPath.Normalize(@"\\server\share").Extended, "share root extended form");
        AssertEqual(@"C:\x", RootPath.Normalize(@"\\?\C:\x").Display, "an extended-length argument is shown without the prefix");

        // An extended-length argument is reduced to the ordinary form first, because the API takes "\\?\" paths literally.
        AssertEqual(@"C:\b", RootPath.Normalize(@"\\?\C:\a\..\b").Display, ".. in an extended-length argument is resolved");
        AssertEqual(@"\\?\C:\b", RootPath.Normalize(@"\\?\C:\a\..\b").Extended, "and the extended form is rebuilt from the result");
        AssertEqual(@"C:\", RootPath.Normalize(@"\\?\C:\\").Display, "an extended drive root keeps its backslash");
        AssertEqual(@"\\server\share\d", RootPath.Normalize(@"\\?\UNC\server\share\d\").Display, "an extended UNC argument");
        AssertEqual(@"C:\x\y", RootPath.Normalize("C:/x//y").Display, "forward and doubled separators");
        AssertEqual(Path.GetFullPath("src"), RootPath.Normalize("src").Display, "a relative path is resolved against the current directory");

        // A volume with no drive letter is named by its GUID; that form is kept as typed.
        const string volume = @"\\?\Volume{12345678-1234-1234-1234-123456789abc}";
        AssertEqual(volume + @"\", RootPath.Normalize(volume).Display, "a volume root gets its trailing backslash");
        AssertEqual(volume + @"\", RootPath.Normalize(volume + @"\").Extended, "and is its own extended form");
        AssertEqual(volume + @"\dir", RootPath.Normalize(volume + @"\dir\").Display, "a folder on a volume");

        // The extended form is what makes paths over 260 characters work.
        var longName = new string('x', 300);
        var longRoot = RootPath.Normalize(@"C:\" + longName);
        AssertEqual(@"\\?\C:\" + longName, longRoot.Extended, "a 300-character name keeps the extended prefix and its full length");

        AssertThrows<ArgumentException>(() => RootPath.Normalize(""), "empty path");
        AssertThrows<ArgumentException>(() => RootPath.Normalize("  "), "blank path");
        AssertThrows<ArgumentException>(() => RootPath.Normalize(@"\\.\C:\"), "a device path");
        AssertThrows<ArgumentException>(() => RootPath.Normalize(@"\\?\GLOBALROOT\Device\x"), "an extended-length device path");
        AssertThrows<ArgumentException>(() => RootPath.Normalize(@"\\server"), "a network path without a share");
        AssertThrows<ArgumentException>(() => RootPath.Normalize(@"\\?\Volume{no-closing-brace"), "a broken volume path");
        var quote = MessageOfArgumentException(() => RootPath.Normalize("C:\\dir\""));
        Assert(quote.Contains("quote"), "a quote in the path is explained (a trailing backslash before a closing quote escapes it)");
    }

    // The message of the ArgumentException that the action must throw.
    static string MessageOfArgumentException(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException exception)
        {
            return exception.Message;
        }
        throw new Exception("expected ArgumentException, nothing was thrown");
    }

    static void OptionsAccepted()
    {
        var defaults = FsOptions.Parse([@"C:\"]);
        AssertEqual(@"C:\", defaults.Root, "path");
        AssertEqual(25, defaults.Top, "default top");
        AssertEqual(0, defaults.Workers, "default workers (automatic)");
        Assert(defaults.Dirs && !defaults.Files && !defaults.Json && !defaults.Strict && !defaults.Benchmark, "default flags");

        var all = FsOptions.Parse([@"D:\data", "--top=7", "--files", "--json", "--strict", "--benchmark", "--workers", "3"]);
        AssertEqual(7, all.Top, "--top=N");
        AssertEqual(3, all.Workers, "--workers N");
        Assert(all.Files && all.Json && all.Strict && all.Benchmark, "flags");

        AssertEqual(5, FsOptions.Parse(["--top", "5", "x"]).Top, "--top N");
        AssertEqual(4, FsOptions.Parse(["x", "--workers=4"]).Workers, "--workers=N");
        Assert(FsOptions.Parse(["--help"]).Help, "help needs no path");
        Assert(FsOptions.Parse(["--self-test"]).SelfTest, "self-test needs no path");
        Assert(FsOptions.Parse(["-h"]).Help, "-h");
        AssertEqual(1, FsOptions.Parse(["x", "--top", "0"]).Top, "--top 0 is clamped to 1, as in the NTFS tools");
        AssertEqual(256, FsOptions.Parse(["x", "--workers=256"]).Workers, "the largest number of workers");
    }

    static void OptionsRejected()
    {
        AssertThrows<ArgumentException>(() => FsOptions.Parse([]), "no path");
        AssertThrows<ArgumentException>(() => FsOptions.Parse(["a", "b"]), "two paths");
        AssertThrows<ArgumentException>(() => FsOptions.Parse(["a", "--bogus"]), "unknown option");
        AssertThrows<ArgumentException>(() => FsOptions.Parse(["a", "--workers", "0"]), "zero workers");
        AssertThrows<ArgumentException>(() => FsOptions.Parse(["a", "--workers=999"]), "too many workers");
        AssertThrows<ArgumentException>(() => FsOptions.Parse(["a", "--workers"]), "workers without a value");
        AssertThrows<ArgumentException>(() => FsOptions.Parse(["a", "--top"]), "top without a value");
        AssertThrows<ArgumentException>(() => FsOptions.Parse(["a", "--top=abc"]), "top that is not a number");
        AssertThrows<ArgumentException>(() => FsOptions.Parse(["a", "--top", "--files"]), "top followed by another option");
        AssertThrows<ArgumentException>(() => FsOptions.Parse(["a", "--workers=abc"]), "workers that is not a number");
        AssertThrows<ArgumentException>(() => FsOptions.Parse(["a", "--workers=257"]), "one worker too many");
        AssertThrows<ArgumentException>(() => FsOptions.Parse(["a", "--workers", "-1"]), "a negative number of workers");
    }
}
