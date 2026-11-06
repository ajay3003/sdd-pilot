using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using BirkNext.Api.Services.LocalSecurity;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Controllers;

/// <summary>
/// BQA-002: the launch code is owner-only. Before, the folder and file only inherited their parent's ACL; under C:\ (where the tester package
/// is documented to be extracted) that grants Authenticated Users modify access, so another local account could read the code. The folder and
/// file now carry an explicit, protected ACL — current user and SYSTEM only — even when the parent is widely accessible, and an existing
/// install is repaired on start.
/// </summary>
public sealed class LocalSessionCodeAccessTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "birknext-acl-" + Guid.NewGuid().ToString("N"));

    public LocalSessionCodeAccessTests()
    {
        Directory.CreateDirectory(_parent);
        // A widely accessible parent, like C:\ for authenticated users: everything below would inherit it.
        if (OperatingSystem.IsWindows()) GrantInheritable(_parent, new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null));
    }

    [SupportedOSPlatform("windows")]
    private static void GrantInheritable(string path, SecurityIdentifier sid)
    {
        var info = new DirectoryInfo(path);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    private LocalSessionService Create(string directory) => new(directory, TimeProvider.System, NullLogger<LocalSessionService>.Instance);

    [Fact]
    public void TheCodeFolderAndFile_AreOwnerOnly_EvenUnderAWidelyAccessibleParent()
    {
        var directory = Path.Combine(_parent, "local-session");
        var service = Create(directory);

        if (OperatingSystem.IsWindows())
        {
            AssertOwnerOnly(new DirectoryInfo(directory).GetAccessControl());
            AssertOwnerOnly(new FileInfo(service.CodePath).GetAccessControl());
        }
        else
        {
            File.GetUnixFileMode(directory).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.GetUnixFileMode(service.CodePath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void AnInstallCreatedBeforeTheRule_IsRepairedOnStart_AndRotationKeepsTheFileOwnerOnly()
    {
        var directory = Path.Combine(_parent, "local-session");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "launch-code.txt"), "ABCD-EFGH-JKLM"); // inherits the wide parent ACL, as an old install did

        var service = Create(directory);
        service.CurrentCode.Should().Be("ABCD-EFGH-JKLM", "the existing code is kept");
        service.Rotate();

        if (OperatingSystem.IsWindows())
        {
            AssertOwnerOnly(new DirectoryInfo(directory).GetAccessControl());
            AssertOwnerOnly(new FileInfo(service.CodePath).GetAccessControl());
        }
        else
        {
            File.GetUnixFileMode(service.CodePath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AssertOwnerOnly(FileSystemSecurity security)
    {
        security.AreAccessRulesProtected.Should().BeTrue("no access is inherited from the parent folder");
        using var identity = WindowsIdentity.GetCurrent();
        var allowed = new HashSet<SecurityIdentifier> { identity.User!, new(WellKnownSidType.LocalSystemSid, null) };
        var sids = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Where(r => r.AccessControlType == AccessControlType.Allow).Select(r => (SecurityIdentifier)r.IdentityReference).ToList();
        sids.Should().OnlyContain(sid => allowed.Contains(sid), "only the owner and SYSTEM may read the launch code");
        sids.Should().Contain(identity.User!);
    }

    public void Dispose()
    {
        try { Directory.Delete(_parent, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
