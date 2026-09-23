using System.Runtime.InteropServices;
using System.Text;

// Thrown only from a strategy's own availability probe (see the unified-scan-strategy design spec, "The
// availability/failure boundary"). Never thrown for any other reason, never caught anywhere but
// ScanStrategySelector, and never used to convert a real scan failure into "try something else".
public sealed class StrategyUnavailableException(string strategy, Exception cause) : Exception($"{strategy}: {cause.Message}", cause)
{
    public string Strategy { get; } = strategy;
}

// "Is this drive NTFS", extracted from the same GetVolumeInformation P/Invoke FsctlScanner.cs already
// declares privately for its own error messages (that copy stays there until Step 3 moves the whole file and
// switches it to call this instead -- see the design spec).
public static class VolumeInfo
{
    public static bool IsNtfs(string driveRoot)
    {
        var name = new StringBuilder(261);
        return GetVolumeInformation($"{driveRoot}\\", null, 0, out _, out _, out _, name, name.Capacity)
            && name.ToString() == "NTFS";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeInformation(string rootPath, StringBuilder? volumeName, int volumeNameSize, out uint serialNumber, out uint maximumComponentLength, out uint filesystemFlags, StringBuilder filesystemName, int filesystemNameSize);
}

// dirsizer.exe's own, uniform result -- every IScanStrategy adapts its native result into this. Dedicated
// executables are unaffected: they call their native Scan/Run method directly and print their own, unchanged,
// richer output. See the design spec, "IScanStrategy".
public sealed record UnifiedScanResult(
    string Volume, int Top,
    UnifiedItem Root, UnifiedItem[] RootChildren, UnifiedItem[] Directories, UnifiedItem[] Files,
    long DirectoriesScanned, long Unreadable, long FileCount, long Bytes, string[] ErrorSamples,
    string Strategy, string? StrategyFallback, string? StrategyFallbackReason, double TotalMs);

public readonly record struct UnifiedItem(string Path, long Size);

// What dirsizer.exe itself accepts (see the design spec, "Common CLI options"), before each strategy's own
// adapter translates it into that strategy's native settings type.
public sealed record UnifiedScanOptions(int Top, bool Files, bool Dirs, bool Strict, int Workers);
