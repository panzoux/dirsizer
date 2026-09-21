static class DriveRoot
{
    // Returns the drive root as typed, without a trailing backslash ("C:"), or throws ArgumentException.
    public static string Validate(string value)
    {
        var drive = value.Trim().TrimEnd('\\');
        if (drive.Length == 2 && drive[1] == ':') return drive;
        throw new ArgumentException("The volume must be a drive root such as C:\\.");
    }
}
