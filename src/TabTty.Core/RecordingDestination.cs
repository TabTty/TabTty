using System.Runtime.InteropServices;
using System.Text;

namespace TabTty.Core;

internal sealed record RecordingDestination(string Path, string EffectivePath, string? DriveTarget)
{
    internal static RecordingDestination Capture(string profilePath, ConnectionProfile profile)
    {
        string path = OrdinaryWindowsPath.Resolve(profile.LogDirectory,
            System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(profilePath))!);
        if (!OperatingSystem.IsWindows() || path.StartsWith(@"\\", StringComparison.Ordinal))
            return new(path, path, null);
        return ReadDrive(path);
    }

    internal string Verify()
    {
        if (DriveTarget is not null && ReadDrive(Path) != this)
            throw new RecordingDestinationChangedException();
        return EffectivePath;
    }

    private static RecordingDestination ReadDrive(string path, int remaining = 8)
    {
        string drive = path[..2];
        var target = new StringBuilder(1024);
        if (QueryDosDevice(drive, target, target.Capacity) == 0)
            throw new RecordingDestinationChangedException();
        string mapping = target.ToString();
        if (mapping.StartsWith(@"\Device\HarddiskVolume", StringComparison.OrdinalIgnoreCase))
            return new(path, path, mapping);
        if (mapping.StartsWith(@"\??\", StringComparison.Ordinal) &&
            System.IO.Path.IsPathFullyQualified(mapping[4..]))
        {
            if (remaining == 0) throw new RecordingDestinationChangedException();
            string targetPath = System.IO.Path.Combine(mapping[4..], path[3..]);
            if (targetPath.StartsWith(@"\\", StringComparison.Ordinal))
                return new(path, targetPath, mapping);
            var underlying = ReadDrive(targetPath, remaining - 1);
            return new(path, underlying.EffectivePath, mapping + "\0" + underlying.DriveTarget);
        }
        var remote = new StringBuilder(32768);
        int length = remote.Capacity;
        string effective = WNetGetConnection(drive, remote, ref length) == 0
            ? System.IO.Path.Combine(remote.ToString(), path[3..]) : path;
        return new(path, effective, mapping);
    }

    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint QueryDosDevice(string drive, StringBuilder target, int capacity);

    [DllImport("mpr.dll", EntryPoint = "WNetGetConnectionW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WNetGetConnection(string drive, StringBuilder target, ref int capacity);
}

internal sealed class RecordingDestinationChangedException() : IOException(Description)
{
    internal const string Description = "The reviewed recording destination changed.";
}
