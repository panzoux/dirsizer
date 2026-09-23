// The root the user names, in the two forms the tool needs: the display form for output ("C:\Users", "\\server\share") and the
// extended-length form for the API ("\\?\C:\Users", "\\?\UNC\server\share"), which works for paths over 260 characters whatever
// the LongPathsEnabled setting is.
readonly record struct RootPath(string Display, string Extended)
{
    // On .NET Core Path.GetInvalidPathChars() lists only '|' and the control characters; a quote, '<' and '>' cannot be in a Windows
    // path either, and a quote is what a trailing backslash before a closing quote leaves behind.
    static readonly char[] InvalidPathChars = [.. Path.GetInvalidPathChars(), '"', '<', '>'];

    // Throws ArgumentException for an empty path, a character that cannot be in a path, a device path, or a network path without a
    // server and a share. Does not check that the directory exists.
    public static RootPath Normalize(string argument)
    {
        var text = argument.Trim();
        if (text.Length == 0) throw new ArgumentException("A directory path is required, for example C:\\.");
        var invalid = text.IndexOfAny(InvalidPathChars);
        if (invalid >= 0)
        {
            throw new ArgumentException(text[invalid] == '"'
                ? "The path contains a quote character. A backslash right before a closing quote escapes it: leave the trailing backslash out, for example \"C:\\Program Files\"."
                : $"The path contains a character that is not allowed in a path: {text[invalid]}");
        }

        // \\?\Volume{guid}\ names a volume that has no drive letter. It is already an extended-length path and stripping its prefix
        // would leave something that is not a path, so it is used as typed, apart from the trailing backslash. "." and ".." in it
        // are not resolved.
        if (text.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase))
        {
            var closing = text.IndexOf('}');
            var volume = closing < 0 ? "" : text[..(closing + 1)];
            var below = closing < 0 ? "" : text[(closing + 1)..].TrimEnd('\\');
            if (closing < 0 || below.Length > 0 && below[0] != '\\') throw new ArgumentException($"Invalid volume path: {argument}");
            var volumePath = below.Length == 0 ? volume + "\\" : volume + below;
            return new RootPath(volumePath, volumePath);
        }

        // Reduce an extended-length argument to its ordinary form first. GetFullPath does not normalise "\\?\" paths, and the extended
        // form is built again below from the normalised text, so "." or ".." or "/" in the argument are resolved, never taken literally.
        if (text.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) text = @"\\" + text[8..];
        else if (text.StartsWith(@"\\?\", StringComparison.Ordinal) && text.Length >= 6 && char.IsAsciiLetter(text[4]) && text[5] == ':') text = text[4..];
        else if (text.StartsWith(@"\\?\", StringComparison.Ordinal) || text.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException($"Device paths are not supported: {argument}");

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
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // A device name such as C:\con comes back from GetFullPath as \\.\con.
            var parts = full[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && parts[0] is "." or "?") throw new ArgumentException($"Device paths are not supported: {argument}");
            if (parts.Length < 2) throw new ArgumentException($"A network path must name a server and a share, for example \\\\server\\share: {argument}");
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
        if (path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)) return path;   // a volume path has no shorter form
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path[4..];
        return path;
    }
}
