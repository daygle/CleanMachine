using CleanMachine.Windows;
using System.Xml.Linq;
using Xunit;

namespace CleanMachine.Windows.Tests;

/// <summary>Invariants about the shipped MSIX package itself: the desktop shortcut
/// target, the in-repo version defaults, and the restricted-capability set. These
/// assert on packaging artifacts rather than on cleanup behaviour, so they are
/// kept apart from the runtime-safety tests in ManifestAndSafetyTests.</summary>
public sealed class StorePackagingTests
{
    /// <summary>The desktop shortcut must target the package identity, not the
    /// version-stamped WindowsApps executable: the folder an exe-targeting link
    /// points at is deleted on the next update, which is what broke the user's
    /// desktop icon after every in-app update.</summary>
    [Fact]
    public void MsixDesktopShortcutTargetsThePackageIdentityNotTheVersionedExe()
    {
        var family = "CleanMachine_1234567890abcdef_abcdef1234567890abcdef1234567890abcdef1234567890abcdef";

        var target = MsixUninstallService.BuildMsixShortcutTarget(family);

        Assert.Equal($"shell:AppsFolder\\{family}!App", target);
        Assert.DoesNotContain("WindowsApps", target, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".exe", target, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\", target[(target.IndexOf('\\') + 1)..], StringComparison.Ordinal);
    }

    /// <summary>The in-repo version defaults (RELEASE.md checklist step 1) must stay
    /// in lockstep: the MSIX package identity and the Win32 assembly identity are
    /// bumped together on every release. The release workflow stamps the package
    /// identity from the tag but never rewrites app.manifest, so this checked-in
    /// pair is the only thing that keeps them from silently drifting apart again.</summary>
    [Fact]
    public void PackageAndAppManifestVersionsAgree()
    {
        var root = FindRepoRoot();
        Assert.True(root is not null,
            "Repo root (containing CleanMachine.Windows\\) was not found above the test output directory.");

        var appx = XDocument.Load(Path.Combine(root!, "CleanMachine.Windows", "Package.appxmanifest"));
        var appManifest = XDocument.Load(Path.Combine(root!, "CleanMachine.Windows", "app.manifest"));

        // Descend by LocalName so the appx default xmlns and the asm.v1 xmlns on
        // app.manifest never have to be spelled out (and can never go stale).
        var packageVersion = appx.Descendants().FirstOrDefault(e => e.Name.LocalName == "Identity")?
            .Attribute("Version")?.Value;
        var assemblyVersion = appManifest.Descendants().FirstOrDefault(e => e.Name.LocalName == "assemblyIdentity")?
            .Attribute("version")?.Value;

        Assert.False(string.IsNullOrWhiteSpace(packageVersion),
            "Package.appxmanifest has no Identity Version attribute.");
        Assert.False(string.IsNullOrWhiteSpace(assemblyVersion),
            "app.manifest has no assemblyIdentity version attribute.");
        // Four-part X.Y.Z.0: the shape the release workflow stamps from the tag.
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", packageVersion);
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", assemblyVersion);
        Assert.Equal(packageVersion, assemblyVersion);
    }

    /// <summary>Partner Center requires a written justification for every restricted
    /// capability, and the justification in RELEASE.md covers exactly one of them.
    /// A newly added restricted capability silently ships without a justification
    /// and fails certification, so the set is pinned here.</summary>
    [Fact]
    public void PackageDeclaresOnlyTheJustifiedRestrictedCapability()
    {
        var root = FindRepoRoot();
        Assert.True(root is not null, "Repo root was not found above the test output directory.");

        var appx = XDocument.Load(Path.Combine(root!, "CleanMachine.Windows", "Package.appxmanifest"));
        var restricted = appx.Descendants()
            .Where(e => e.Name.LocalName == "Capability")
            .Select(e => e.Attribute("Name")?.Value)
            .OfType<string>()
            .ToArray();

        // runFullTrust is required by the desktop:Extension full-trust entry that
        // backs the WinUI 3 app; the justification in RELEASE.md explains why.
        Assert.Equal(new[] { "runFullTrust" }, restricted);
    }

    /// <summary>Three separate Partner Center Identity values are validated at
    /// ingestion, and a mismatch in any of them is only reported after the whole
    /// 175 MB package has uploaded. All three are stamped per run and re-checked in
    /// the built package; this pins that wiring so a stamp cannot be quietly dropped
    /// from the workflow and the checked-in placeholders shipped again.</summary>
    [Fact]
    public void EveryPartnerCenterIdentityFieldIsStampedAndVerified()
    {
        var root = FindRepoRoot();
        Assert.True(root is not null, "Repo root was not found above the test output directory.");

        var workflow = File.ReadAllText(
            Path.Combine(root!, ".github", "workflows", "release-windows.yml"));

        foreach (var variable in new[] { "STORE_PUBLISHER", "STORE_PUBLISHER_DISPLAY_NAME", "STORE_APP_NAME" })
            Assert.Contains(variable, workflow, StringComparison.Ordinal);

        // Each field is stamped ...
        Assert.Contains("Properties.PublisherDisplayName", workflow, StringComparison.Ordinal);
        Assert.Contains("Identity.Name", workflow, StringComparison.Ordinal);
        Assert.Contains("Identity.Publisher", workflow, StringComparison.Ordinal);

        // ... and each is re-checked against the variable after the build. Stamping
        // alone would let a wrong variable value through unnoticed.
        Assert.Contains("does not match the publisher display name", workflow, StringComparison.Ordinal);
        Assert.Contains("does not match the identity name reserved", workflow, StringComparison.Ordinal);
        Assert.Contains("does not match STORE_PUBLISHER", workflow, StringComparison.Ordinal);
    }

    /// <summary>"Start with Windows" for the packaged app runs through the
    /// manifest's startup task: a HKCU Run value written from inside the package
    /// is redirected into its private hive and never starts anything. The code
    /// looks the task up by id, so a manifest/code mismatch fails silently at
    /// runtime - the checkbox stays ticked and nothing starts. The task must
    /// also ship off, so installing the app never opts the user in.</summary>
    [Fact]
    public void ManifestDeclaresTheStartupTaskTheCodeLooksUp()
    {
        var root = FindRepoRoot();
        Assert.True(root is not null, "Repo root was not found above the test output directory.");

        var appx = XDocument.Load(Path.Combine(root!, "CleanMachine.Windows", "Package.appxmanifest"));
        var task = appx.Descendants().SingleOrDefault(e => e.Name.LocalName == "StartupTask");

        Assert.NotNull(task);
        Assert.Equal("windows.startupTask", task!.Parent?.Attribute("Category")?.Value);
        Assert.Equal(StartupRegistration.TaskId, task.Attribute("TaskId")?.Value);
        Assert.Equal("false", task.Attribute("Enabled")?.Value);
    }

    /// <summary>Walks up from the test output directory (bin/&lt;config&gt;/&lt;tfm&gt;,
    /// any platform) to the checkout that contains the app project.</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CleanMachine.Windows", "Package.appxmanifest")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
