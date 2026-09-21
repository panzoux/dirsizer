// Root path handling and command-line options.
static partial class FsSelfTests
{
    static partial void AddCliTests(List<SelfTest> tests)
    {
        tests.Add(new("root paths are normalized to display and extended forms", RootPathForms));
        tests.Add(new("options: defaults and values", OptionsAccepted));
        tests.Add(new("options: bad input is rejected", OptionsRejected));
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
        AssertThrows<ArgumentException>(() => RootPath.Normalize("  "), "empty path");
    }

    static void OptionsAccepted()
    {
        var defaults = FsOptions.Parse([@"C:\"]);
        AssertEqual(@"C:\", defaults.Path, "path");
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
    }
}
