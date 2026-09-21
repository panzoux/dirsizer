// The root the user names, in the two forms the tool needs: the display form for output ("C:\Users", "\\server\share") and the
// extended-length form for the API ("\\?\C:\Users", "\\?\UNC\server\share"), which works for paths over 260 characters whatever
// the LongPathsEnabled setting is.
readonly record struct RootPath(string Display, string Extended)
{
    // Throws ArgumentException for an empty or invalid path. Does not check that the directory exists.
    public static RootPath Normalize(string argument)
    {
        var text = argument.Trim();
        if (text.Length == 0) throw new ArgumentException("A directory path is required, for example C:\\.");
        // "C:" alone means the drive root here, as in the NTFS tools (Windows itself would take it as the current directory of C:).
        if (text.Length == 2 && char.IsAsciiLetter(text[0]) && text[1] == ':') text += "\\";
        string full;
        try
        {
            full = Path.GetFullPath(text);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"Invalid path: {argument}", exception);
        }
        var display = ToDisplay(full);
        var rootLength = Path.GetPathRoot(display)?.Length ?? 0;
        if (display.Length > rootLength) display = display.TrimEnd('\\');
        return new RootPath(display, ToExtended(display));
    }

    public static string ToExtended(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path[2..];
        return @"\\?\" + path;
    }

    public static string ToDisplay(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path[4..];
        return path;
    }
}
