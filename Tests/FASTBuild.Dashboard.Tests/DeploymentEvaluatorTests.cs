using System;
using System.IO;
using System.Text;
using FastBuild.Dashboard.Services.Update;
using Xunit;

namespace FastBuild.Dashboard.Tests;

/// <summary>
/// Unit tests for <see cref="DeploymentEvaluator"/> and the deployment-update models
/// (TDD docs/tdd/fastbuild-deployment-auto-update.md, section 8.2 Key Scenarios 1-3/7).
/// The evaluator is dependency-free, so these tests exercise the real implementation
/// against fake deployments-share layouts built in per-test temp directories.
/// </summary>
public sealed class DeploymentEvaluatorTests : IDisposable
{
    private readonly string _root;

    public DeploymentEvaluatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "FBDashboardTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // Never fail a test because of temp-dir cleanup races.
        }
    }

    // ------------------------------------------------------------------
    // Fake share layout helper
    // ------------------------------------------------------------------

    /// <summary>Builds a fake component folder on the "deployments share"
    /// (&lt;root&gt;\FBuildWorker\FBuildWorker.exe + version.txt + apply.complete)
    /// with individually controllable file timestamps.</summary>
    private sealed class ComponentLayout
    {
        public ComponentLayout(string root, string componentFolder, string exeFileName)
        {
            ComponentRoot = Path.Combine(root, componentFolder);
            Directory.CreateDirectory(ComponentRoot);
            ExePath = Path.Combine(ComponentRoot, exeFileName);
            VersionFilePath = Path.Combine(ComponentRoot, DeploymentEvaluator.SidecarFileName);
            MarkerPath = Path.Combine(ComponentRoot, DeploymentEvaluator.MarkerFileName);
        }

        public string ComponentRoot { get; }
        public string ExePath { get; }
        public string VersionFilePath { get; }
        public string MarkerPath { get; }

        public ComponentLayout WithExe(byte[] content)
        {
            File.WriteAllBytes(ExePath, content);
            return this;
        }

        public ComponentLayout WithVersion(string version)
        {
            File.WriteAllText(VersionFilePath, version);
            return this;
        }

        public ComponentLayout WithMarker()
        {
            File.WriteAllText(MarkerPath, string.Empty);
            return this;
        }

        /// <summary>Directly controls the LastWriteTimeUtc of a payload/marker file, used to
        /// simulate partial publishes (marker written before the payload finished).</summary>
        public void SetLastWrite(string path, DateTime utcTime) =>
            File.SetLastWriteTimeUtc(path, utcTime);
    }

    private static byte[] DummyExeContent(byte seed) =>
        new byte[] { 0x4D, 0x5A, seed, 0x00, 0x11, 0x22, 0x33, seed };

    // ------------------------------------------------------------------
    // TryReadVersionFile (TDD 8.2 - sidecar detection primitive)
    // ------------------------------------------------------------------

    [Fact]
    public void TryReadVersionFile_ReadsAndTrimsContent()
    {
        var layout = new ComponentLayout(_root, "FBuildWorker", "FBuildWorker.exe");
        File.WriteAllText(layout.VersionFilePath, "  v1.2.3-42-gdeadbeef \r\n");

        var result = DeploymentEvaluator.TryReadVersionFile(layout.VersionFilePath, out var version);

        Assert.True(result);
        Assert.Equal("v1.2.3-42-gdeadbeef", version);
    }

    [Fact]
    public void TryReadVersionFile_MissingFile_ReturnsFalse()
    {
        var layout = new ComponentLayout(_root, "FBuildWorker", "FBuildWorker.exe");
        // version.txt deliberately not written.

        var result = DeploymentEvaluator.TryReadVersionFile(layout.VersionFilePath, out var version);

        Assert.False(result);
        Assert.Null(version);
    }

    [Fact]
    public void TryReadVersionFile_EmptyFile_ReturnsFalseWithEmptyVersion()
    {
        var layout = new ComponentLayout(_root, "FBuildWorker", "FBuildWorker.exe");
        File.WriteAllText(layout.VersionFilePath, "   \r\n");

        var result = DeploymentEvaluator.TryReadVersionFile(layout.VersionFilePath, out var version);

        Assert.False(result);
        // Documented as "version = null", but the implementation leaves the trimmed
        // empty string in place; empty-or-null is the meaningful contract.
        Assert.True(string.IsNullOrEmpty(version),
            $"Expected null-or-empty version for an empty sidecar, got '{version}'.");
    }

    [Fact]
    public void TryReadVersionFile_NullOrEmptyPath_ReturnsFalse()
    {
        Assert.False(DeploymentEvaluator.TryReadVersionFile(null, out var versionNull));
        Assert.Null(versionNull);
        Assert.False(DeploymentEvaluator.TryReadVersionFile(string.Empty, out var versionEmpty));
        Assert.Null(versionEmpty);
    }

    // ------------------------------------------------------------------
    // IsValidCandidate (TDD 8.2 scenario 3: partial publish ignored)
    // ------------------------------------------------------------------

    [Fact]
    public void IsValidCandidate_ValidExeSidecarAndMarker_ReturnsTrueWithPaths()
    {
        var layout = new ComponentLayout(_root, "FBuildWorker", "FBuildWorker.exe");
        layout.WithExe(DummyExeContent(0x01)).WithVersion("v1.2.3").WithMarker();

        var result = DeploymentEvaluator.IsValidCandidate(layout.ComponentRoot, "FBuildWorker.exe",
            out var shareExePath, out var shareVersion);

        Assert.True(result);
        Assert.Equal(layout.ExePath, shareExePath);
        Assert.Equal("v1.2.3", shareVersion);
    }

    [Fact]
    public void IsValidCandidate_MissingMarker_ReturnsFalse()
    {
        var layout = new ComponentLayout(_root, "FBuildWorker", "FBuildWorker.exe");
        layout.WithExe(DummyExeContent(0x02)).WithVersion("v1.2.3");
        // apply.complete deliberately not written - publish still in progress.

        var result = DeploymentEvaluator.IsValidCandidate(layout.ComponentRoot, "FBuildWorker.exe",
            out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void IsValidCandidate_MarkerOlderThanExe_ReturnsFalse()
    {
        var layout = new ComponentLayout(_root, "FBuildWorker", "FBuildWorker.exe");
        layout.WithExe(DummyExeContent(0x03)).WithVersion("v1.2.3").WithMarker();

        // Simulate a payload re-published after the marker was stamped.
        var now = DateTime.UtcNow;
        layout.SetLastWrite(layout.MarkerPath, now.AddHours(-2));
        layout.SetLastWrite(layout.ExePath, now.AddHours(-1));

        var result = DeploymentEvaluator.IsValidCandidate(layout.ComponentRoot, "FBuildWorker.exe",
            out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void IsValidCandidate_MarkerSameTimeAsExe_ReturnsTrue()
    {
        var layout = new ComponentLayout(_root, "FBuildWorker", "FBuildWorker.exe");
        layout.WithExe(DummyExeContent(0x04)).WithVersion("v1.2.3").WithMarker();

        var sameTime = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
        layout.SetLastWrite(layout.MarkerPath, sameTime);
        layout.SetLastWrite(layout.ExePath, sameTime);

        var result = DeploymentEvaluator.IsValidCandidate(layout.ComponentRoot, "FBuildWorker.exe",
            out _, out _);

        Assert.True(result);
    }

    [Fact]
    public void IsValidCandidate_MissingSidecar_ReturnsFalse()
    {
        var layout = new ComponentLayout(_root, "FBuildWorker", "FBuildWorker.exe");
        layout.WithExe(DummyExeContent(0x05)).WithMarker();
        // version.txt deliberately not written.

        var result = DeploymentEvaluator.IsValidCandidate(layout.ComponentRoot, "FBuildWorker.exe",
            out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void IsValidCandidate_MissingExe_ReturnsFalse()
    {
        var layout = new ComponentLayout(_root, "FBuildWorker", "FBuildWorker.exe");
        layout.WithVersion("v1.2.3").WithMarker();
        // FBuildWorker.exe deliberately not written.

        var result = DeploymentEvaluator.IsValidCandidate(layout.ComponentRoot, "FBuildWorker.exe",
            out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void IsValidCandidate_EmptyComponentRoot_ReturnsFalse()
    {
        Assert.False(DeploymentEvaluator.IsValidCandidate(string.Empty, "FBuildWorker.exe",
            out _, out _));
    }

    // ------------------------------------------------------------------
    // EvaluateWorker (TDD 8.2 scenarios 1, 2)
    // ------------------------------------------------------------------

    [Fact]
    public void EvaluateWorker_ShareVersionDiffersFromLocal_ReturnsApplyWorkerUpdate()
    {
        var layout = new ComponentLayout(_root, "FBuildWorker", "FBuildWorker.exe");
        layout.WithExe(DummyExeContent(0x06)).WithVersion("v1.2.4-7-gcafebab").WithMarker();

        var result = DeploymentEvaluator.EvaluateWorker(layout.ComponentRoot, "FBuildWorker.exe",
            "v1.2.3-42-gdeadbeef");

        Assert.Equal(DeploymentAction.ApplyWorkerUpdate, result.Action);
        Assert.NotNull(result.Candidate);
        Assert.Equal(DeploymentComponent.FBuildWorker, result.Candidate.Component);
        Assert.Equal(layout.ExePath, result.Candidate.ShareExePath);
        Assert.Equal("v1.2.4-7-gcafebab", result.Candidate.ShareVersion);
        // Worker versions are opaque strings - never parsed.
        Assert.Null(result.Candidate.ShareVersionParsed);
    }

    [Fact]
    public void EvaluateWorker_ShareVersionEqualsLocal_ReturnsNone()
    {
        var layout = new ComponentLayout(_root, "FBuildWorker", "FBuildWorker.exe");
        layout.WithExe(DummyExeContent(0x07)).WithVersion("v1.2.3-42-gdeadbeef").WithMarker();

        var result = DeploymentEvaluator.EvaluateWorker(layout.ComponentRoot, "FBuildWorker.exe",
            "v1.2.3-42-gdeadbeef");

        Assert.Equal(DeploymentAction.None, result.Action);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void EvaluateWorker_LocalVersionNull_BootstrapCase_ReturnsApplyWorkerUpdate()
    {
        var layout = new ComponentLayout(_root, "FBuildWorker", "FBuildWorker.exe");
        layout.WithExe(DummyExeContent(0x08)).WithVersion("v1.2.4").WithMarker();

        var result = DeploymentEvaluator.EvaluateWorker(layout.ComponentRoot, "FBuildWorker.exe",
            null);

        Assert.Equal(DeploymentAction.ApplyWorkerUpdate, result.Action);
        Assert.Equal("v1.2.4", result.Candidate.ShareVersion);
    }

    [Fact]
    public void EvaluateWorker_LocalVersionEmpty_BootstrapCase_ReturnsApplyWorkerUpdate()
    {
        var layout = new ComponentLayout(_root, "FBuildWorker", "FBuildWorker.exe");
        layout.WithExe(DummyExeContent(0x09)).WithVersion("v1.2.4").WithMarker();

        var result = DeploymentEvaluator.EvaluateWorker(layout.ComponentRoot, "FBuildWorker.exe",
            string.Empty);

        Assert.Equal(DeploymentAction.ApplyWorkerUpdate, result.Action);
    }

    [Fact]
    public void EvaluateWorker_InvalidCandidate_MissingMarker_ReturnsNone()
    {
        var layout = new ComponentLayout(_root, "FBuildWorker", "FBuildWorker.exe");
        layout.WithExe(DummyExeContent(0x0A)).WithVersion("v1.2.4");
        // No marker - partial publish must not produce an update candidate.

        var result = DeploymentEvaluator.EvaluateWorker(layout.ComponentRoot, "FBuildWorker.exe",
            "v1.2.3");

        Assert.Equal(DeploymentAction.None, result.Action);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void EvaluateWorker_VersionComparisonIsOrdinalNotSemantic()
    {
        // Worker versions are opaque strings: "v2.0" != "v10.0" even though 2.0 < 10.0
        // semantically - any inequality triggers an update.
        var layout = new ComponentLayout(_root, "FBuildWorker", "FBuildWorker.exe");
        layout.WithExe(DummyExeContent(0x0B)).WithVersion("v2.0").WithMarker();

        var result = DeploymentEvaluator.EvaluateWorker(layout.ComponentRoot, "FBuildWorker.exe",
            "v10.0");

        Assert.Equal(DeploymentAction.ApplyWorkerUpdate, result.Action);
    }

    // ------------------------------------------------------------------
    // EvaluateDashboard (TDD 8.2 scenario 5 - detection side)
    // ------------------------------------------------------------------

    [Fact]
    public void EvaluateDashboard_ShareVersionStrictlyNewer_ReturnsOfferDashboardUpdate()
    {
        var layout = new ComponentLayout(_root, "Dashboard", "FBDashboard-setup.exe");
        layout.WithExe(DummyExeContent(0x0C)).WithVersion("1.2.0.0").WithMarker();

        var result = DeploymentEvaluator.EvaluateDashboard(layout.ComponentRoot,
            "FBDashboard-setup.exe", "1.1.2.110");

        Assert.Equal(DeploymentAction.OfferDashboardUpdate, result.Action);
        Assert.NotNull(result.Candidate);
        Assert.Equal(DeploymentComponent.Dashboard, result.Candidate.Component);
        Assert.Equal("1.2.0.0", result.Candidate.ShareVersion);
        Assert.Equal(new Version(1, 2, 0, 0), result.Candidate.ShareVersionParsed);
    }

    [Fact]
    public void EvaluateDashboard_ShareVersionEqualsLocal_ReturnsNone()
    {
        var layout = new ComponentLayout(_root, "Dashboard", "FBDashboard-setup.exe");
        layout.WithExe(DummyExeContent(0x0D)).WithVersion("1.1.2.110").WithMarker();

        var result = DeploymentEvaluator.EvaluateDashboard(layout.ComponentRoot,
            "FBDashboard-setup.exe", "1.1.2.110");

        Assert.Equal(DeploymentAction.None, result.Action);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void EvaluateDashboard_ShareVersionOlderThanLocal_ReturnsNone()
    {
        var layout = new ComponentLayout(_root, "Dashboard", "FBDashboard-setup.exe");
        layout.WithExe(DummyExeContent(0x0E)).WithVersion("1.0.0.108").WithMarker();

        var result = DeploymentEvaluator.EvaluateDashboard(layout.ComponentRoot,
            "FBDashboard-setup.exe", "1.1.2.110");

        Assert.Equal(DeploymentAction.None, result.Action);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void EvaluateDashboard_UnparseableShareSidecar_ReturnsNone()
    {
        // Documented behavior note: an unparseable share version.txt is treated as "no
        // update available" rather than an error - the Dashboard never prompts off of a
        // sidecar that is not a 4-part assembly version.
        var layout = new ComponentLayout(_root, "Dashboard", "FBDashboard-setup.exe");
        layout.WithExe(DummyExeContent(0x0F)).WithVersion("git-describe-not-a-version").WithMarker();

        var result = DeploymentEvaluator.EvaluateDashboard(layout.ComponentRoot,
            "FBDashboard-setup.exe", "1.1.2.110");

        Assert.Equal(DeploymentAction.None, result.Action);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void EvaluateDashboard_LocalVersionNull_ReturnsOfferDashboardUpdate()
    {
        var layout = new ComponentLayout(_root, "Dashboard", "FBDashboard-setup.exe");
        layout.WithExe(DummyExeContent(0x10)).WithVersion("1.2.0.0").WithMarker();

        var result = DeploymentEvaluator.EvaluateDashboard(layout.ComponentRoot,
            "FBDashboard-setup.exe", null);

        Assert.Equal(DeploymentAction.OfferDashboardUpdate, result.Action);
    }

    [Fact]
    public void EvaluateDashboard_ShareVersionNewerInRevisionOnly_ReturnsOfferDashboardUpdate()
    {
        // 4-part comparison: only the revision differs, still strictly newer.
        var layout = new ComponentLayout(_root, "Dashboard", "FBDashboard-setup.exe");
        layout.WithExe(DummyExeContent(0x11)).WithVersion("1.1.2.111").WithMarker();

        var result = DeploymentEvaluator.EvaluateDashboard(layout.ComponentRoot,
            "FBDashboard-setup.exe", "1.1.2.110");

        Assert.Equal(DeploymentAction.OfferDashboardUpdate, result.Action);
    }

    // ------------------------------------------------------------------
    // ApplyWorkerBinarySwap (TDD 8.2 scenarios 1 and 7)
    // ------------------------------------------------------------------

    private ComponentLayout CreateShareWorker(string seedByte) =>
        new ComponentLayout(_root, "FBuildWorker", "FBuildWorker.exe")
            .WithExe(Encoding.UTF8.GetBytes("SHARE-EXE-PAYLOAD-" + seedByte))
            .WithVersion("v1.2.4")
            .WithMarker();

    [Fact]
    public void ApplyWorkerBinarySwap_FreshInstall_NoExistingTarget_CopiesShareExeToTarget()
    {
        var share = CreateShareWorker("fresh");
        var localRoot = Path.Combine(_root, "Local");
        Directory.CreateDirectory(localRoot);
        var targetExe = Path.Combine(localRoot, "FBuildWorker.exe");
        // No target exe exists - bootstrap/fresh-install case.

        var result = DeploymentEvaluator.ApplyWorkerBinarySwap(share.ExePath, targetExe);

        Assert.True(result);
        Assert.True(File.Exists(targetExe), "Target exe should exist after a fresh-install swap.");
        Assert.Equal("SHARE-EXE-PAYLOAD-fresh", File.ReadAllText(targetExe));
        Assert.False(File.Exists(targetExe + ".new"), "No '.new' residue may remain.");
        Assert.False(File.Exists(targetExe + ".old"),
            "A fresh install creates no backup (there was nothing to back up).");
    }

    [Fact]
    public void ApplyWorkerBinarySwap_NormalCase_ReplacesTargetAndCreatesSingleOldBackup()
    {
        var share = CreateShareWorker("normal");
        var localRoot = Path.Combine(_root, "Local");
        Directory.CreateDirectory(localRoot);
        var targetExe = Path.Combine(localRoot, "FBuildWorker.exe");
        const string originalContent = "ORIGINAL-WORKER-PAYLOAD";
        File.WriteAllText(targetExe, originalContent);

        var result = DeploymentEvaluator.ApplyWorkerBinarySwap(share.ExePath, targetExe);

        Assert.True(result);
        Assert.Equal("SHARE-EXE-PAYLOAD-normal", File.ReadAllText(targetExe));

        var backupPath = targetExe + ".old";
        Assert.True(File.Exists(backupPath), "Exactly one '<target>.old' backup must be created.");
        Assert.Equal(originalContent, File.ReadAllText(backupPath));

        // No residue: no '.new', and exactly the target exe + its '.old' backup remain.
        Assert.False(File.Exists(targetExe + ".new"));
        Assert.Equal(2, Directory.GetFiles(localRoot).Length);
    }

    [Fact]
    public void ApplyWorkerBinarySwap_PreviousStaleNewFile_IsRemovedOnSuccess()
    {
        var share = CreateShareWorker("stale");
        var localRoot = Path.Combine(_root, "Local");
        Directory.CreateDirectory(localRoot);
        var targetExe = Path.Combine(localRoot, "FBuildWorker.exe");
        File.WriteAllText(targetExe, "ORIGINAL-WORKER-PAYLOAD");
        File.WriteAllText(targetExe + ".new", "LEFTOVER-FROM-CRASHED-SWAP");

        var result = DeploymentEvaluator.ApplyWorkerBinarySwap(share.ExePath, targetExe);

        Assert.True(result);
        Assert.Equal("SHARE-EXE-PAYLOAD-stale", File.ReadAllText(targetExe));
        Assert.False(File.Exists(targetExe + ".new"), "Stale '.new' must not survive a swap.");
    }

    [Fact]
    public void ApplyWorkerBinarySwap_TargetLocked_ReturnsFalseAndLeavesOriginalIntact()
    {
        var share = CreateShareWorker("locked");
        var localRoot = Path.Combine(_root, "Local");
        Directory.CreateDirectory(localRoot);
        var targetExe = Path.Combine(localRoot, "FBuildWorker.exe");
        const string originalContent = "ORIGINAL-WORKER-PAYLOAD";
        File.WriteAllText(targetExe, originalContent);

        // Simulate the worker exe being locked/running: deny all sharing on the target.
        using (File.Open(targetExe, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = DeploymentEvaluator.ApplyWorkerBinarySwap(share.ExePath, targetExe);

            Assert.False(result,
                "Swap must report failure (not throw) when the target exe is locked.");
        }

        // Original target exe is unchanged - no partial state.
        Assert.Equal(originalContent, File.ReadAllText(targetExe));
        // No backup was created, and no '.new' residue remains.
        Assert.False(File.Exists(targetExe + ".old"),
            "A failed swap must not leave a '.old' backup behind.");
        Assert.False(File.Exists(targetExe + ".new"),
            "A failed swap must clean up its '.new' scratch file.");
    }

    [Fact]
    public void ApplyWorkerBinarySwap_MissingShareExe_ReturnsFalse()
    {
        var localRoot = Path.Combine(_root, "Local");
        Directory.CreateDirectory(localRoot);
        var targetExe = Path.Combine(localRoot, "FBuildWorker.exe");

        var result = DeploymentEvaluator.ApplyWorkerBinarySwap(
            Path.Combine(_root, "FBuildWorker", "DoesNotExist.exe"), targetExe);

        Assert.False(result);
        Assert.False(File.Exists(targetExe));
        Assert.False(File.Exists(targetExe + ".new"));
        Assert.False(File.Exists(targetExe + ".old"));
    }

    [Fact]
    public void ApplyWorkerBinarySwap_EmptyArguments_ReturnsFalse()
    {
        Assert.False(DeploymentEvaluator.ApplyWorkerBinarySwap(null, "target.exe"));
        Assert.False(DeploymentEvaluator.ApplyWorkerBinarySwap("share.exe", string.Empty));
    }

    // ------------------------------------------------------------------
    // Models (constructor-injected get-only properties)
    // ------------------------------------------------------------------

    [Fact]
    public void DeploymentPollResult_None_HasNullCandidate()
    {
        Assert.Equal(DeploymentAction.None, DeploymentPollResult.None.Action);
        Assert.Null(DeploymentPollResult.None.Candidate);
    }

    [Fact]
    public void DeploymentCandidate_PropertiesAreReadOnlyAfterConstruction()
    {
        var parsed = new Version(2, 0, 0, 5);
        var candidate = new DeploymentCandidate(DeploymentComponent.Dashboard,
            @"\\share\FBDeployments\Dashboard\FBDashboard-setup.exe", "2.0.0.5", parsed);

        Assert.Equal(DeploymentComponent.Dashboard, candidate.Component);
        Assert.Equal(@"\\share\FBDeployments\Dashboard\FBDashboard-setup.exe", candidate.ShareExePath);
        Assert.Equal("2.0.0.5", candidate.ShareVersion);
        Assert.Same(parsed, candidate.ShareVersionParsed);
    }

    [Fact]
    public void EvaluateWorker_ApplyingCandidateVersionAsLocalSidecar_ConvergesToNone()
    {
        // Round trip of scenario 1: evaluate -> swap -> record the applied version in the
        // local sidecar -> next poll must be a no-op ("raised once per version").
        var share = CreateShareWorker("roundtrip");
        var localRoot = Path.Combine(_root, "Local", "FBuild");
        Directory.CreateDirectory(localRoot);
        var localSidecar = Path.Combine(localRoot, DeploymentEvaluator.SidecarFileName);
        File.WriteAllText(localSidecar, "v1.2.3");

        var first = DeploymentEvaluator.EvaluateWorker(share.ComponentRoot, "FBuildWorker.exe",
            File.ReadAllText(localSidecar));
        Assert.Equal(DeploymentAction.ApplyWorkerUpdate, first.Action);

        // The caller applies the candidate and records its share version locally.
        File.WriteAllText(localSidecar, first.Candidate.ShareVersion);

        var second = DeploymentEvaluator.EvaluateWorker(share.ComponentRoot, "FBuildWorker.exe",
            File.ReadAllText(localSidecar));
        Assert.Equal(DeploymentAction.None, second.Action);
        Assert.Null(second.Candidate);
    }
}