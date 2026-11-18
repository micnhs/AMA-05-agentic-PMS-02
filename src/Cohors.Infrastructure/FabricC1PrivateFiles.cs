using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public static class FabricC1PrivateFiles
{
    public static void RequireFile(string path)
    {
        RequireAbsoluteNonLink(path);
        Guard.Against(!File.Exists(path), "c1_private_file_missing", "Protected input file does not exist.", 409);
        RequireDirectory(Path.GetDirectoryName(path)!);
        if (OperatingSystem.IsWindows()) RequireWindowsAccess(new FileInfo(path).GetAccessControl());
        else Guard.Against((File.GetUnixFileMode(path) & OtherAccess) != 0,
            "c1_private_file_access", "Input file must not grant group or other access.", 409);
    }

    public static void RequireDirectory(string path)
    {
        RequireAbsoluteNonLink(path);
        Guard.Against(!Directory.Exists(path), "c1_private_directory_missing", "Protected state directory does not exist.", 409);
        if (OperatingSystem.IsWindows()) RequireWindowsAccess(new DirectoryInfo(path).GetAccessControl());
        else Guard.Against((File.GetUnixFileMode(path) & OtherAccess) != 0,
            "c1_private_file_access", "State directory must not grant group or other access.", 409);
    }

    public static void RequireAbsoluteNonLink(string path)
    {
        Guard.Against(!Path.IsPathFullyQualified(path), "c1_private_file_path", "Explicit absolute private path required.", 409);
        FileSystemInfo? item = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        while (item is not null)
        {
            Guard.Against(item.Exists && (item.LinkTarget is not null || (item.Attributes & FileAttributes.ReparsePoint) != 0),
                "c1_private_file_link", "Private state and credentials must not traverse links or reparse points.", 409);
            item = item is DirectoryInfo directory ? directory.Parent : (item as FileInfo)?.Directory;
        }
    }

    private const UnixFileMode OtherAccess = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    [SupportedOSPlatform("windows")]
    private static void RequireWindowsAccess(FileSystemSecurity security)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var caller = identity.User ?? throw new DomainException("c1_private_file_access", "Cannot determine current Windows owner.", 409);
        var permitted = new[]
        {
            caller, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        };
        Guard.Against(!permitted.Any(principal => principal.Equals(security.GetOwner(typeof(SecurityIdentifier)))), "c1_private_file_owner",
            "Private material has an unexpected owner.", 409);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            Guard.Against(rule.AccessControlType == AccessControlType.Allow && !permitted.Any(principal => principal.Equals(rule.IdentityReference)),
                "c1_private_file_access", "Private material grants access to an unrelated Windows principal.", 409);
    }
}
