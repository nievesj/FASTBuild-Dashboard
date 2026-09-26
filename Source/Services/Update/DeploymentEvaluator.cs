using System;
using System.IO;

namespace FastBuild.Dashboard.Services.Update;

/// <summary>
/// Pure, dependency-free deployment evaluation logic (no NLog, no Caliburn, no AppSettings).
/// All methods take explicit paths, never throw, and are safe to run against real temp
/// folders so they can be unit-tested standalone.
/// </summary>
public static class DeploymentEvaluator
{
    public const string SidecarFileName = "version.txt";
    public const string MarkerFileName = "apply.complete";
    private const string NewSuffix = ".new";
    private const string OldSuffix = ".old";

    /// <summary>Reads a version.txt sidecar file. Returns false (with version = null) when the
    /// file is missing, unreadable, or empty.</summary>
    public static bool TryReadVersionFile(string versionFilePath, out string version)
    {
        version = null;

        if (string.IsNullOrEmpty(versionFilePath))
            return false;

        try
        {
            if (!File.Exists(versionFilePath))
                return false;

            version = File.ReadAllText(versionFilePath).Trim();
            return version.Length > 0;
        }
        catch (IOException)
        {
            version = null;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            version = null;
            return false;
        }
    }

    /// <summary>Validates one component folder on the deployments share against the share layout
    /// contract: the payload exe exists, the version.txt sidecar is readable, and the
    /// apply.complete marker exists with LastWriteTimeUtc >= the exe's LastWriteTimeUtc
    /// (an older marker means a publish is still in progress).</summary>
    public static bool IsValidCandidate(string componentRoot, string exeFileName,
        out string shareExePath, out string shareVersion)
    {
        shareExePath = null;
        shareVersion = null;

        if (string.IsNullOrEmpty(componentRoot) || string.IsNullOrEmpty(exeFileName))
            return false;

        try
        {
            shareExePath = Path.Combine(componentRoot, exeFileName);
            if (!File.Exists(shareExePath))
                return false;

            if (!TryReadVersionFile(Path.Combine(componentRoot, SidecarFileName), out shareVersion))
                return false;

            var markerPath = Path.Combine(componentRoot, MarkerFileName);
            if (!File.Exists(markerPath))
                return false;

            var markerTime = File.GetLastWriteTimeUtc(markerPath);
            var exeTime = File.GetLastWriteTimeUtc(shareExePath);

            return markerTime >= exeTime;
        }
        catch (IOException)
        {
            shareExePath = null;
            shareVersion = null;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            shareExePath = null;
            shareVersion = null;
            return false;
        }
    }

    /// <summary>Evaluates a worker component folder against the local worker version. Worker
    /// versions are opaque strings (e.g. git describe output) and are compared by inequality;
    /// an unknown local version (missing local sidecar) always yields an update candidate.</summary>
    public static DeploymentPollResult EvaluateWorker(string componentRoot, string exeFileName,
        string localVersion)
    {
        if (!IsValidCandidate(componentRoot, exeFileName, out var shareExePath, out var shareVersion))
            return DeploymentPollResult.None;

        if (!string.IsNullOrEmpty(localVersion) &&
            string.Equals(localVersion, shareVersion, StringComparison.Ordinal))
            return DeploymentPollResult.None;

        var candidate = new DeploymentCandidate(DeploymentComponent.FBuildWorker, shareExePath,
            shareVersion, null);
        return new DeploymentPollResult(DeploymentAction.ApplyWorkerUpdate, candidate);
    }

    /// <summary>Evaluates a Dashboard component folder against the local (running) Dashboard
    /// version. Both versions must parse with Version.TryParse (4-part assembly versions);
    /// only a strictly newer share version yields a candidate — equal or older is a no-op.</summary>
    public static DeploymentPollResult EvaluateDashboard(string componentRoot, string exeFileName,
        string localVersion)
    {
        if (!IsValidCandidate(componentRoot, exeFileName, out var shareExePath, out var shareVersion))
            return DeploymentPollResult.None;

        if (!Version.TryParse(shareVersion, out var shareVersionParsed))
            return DeploymentPollResult.None;

        Version.TryParse(localVersion, out var localVersionParsed);

        if (localVersionParsed != null && shareVersionParsed.CompareTo(localVersionParsed) <= 0)
            return DeploymentPollResult.None;

        var candidate = new DeploymentCandidate(DeploymentComponent.Dashboard, shareExePath,
            shareVersion, shareVersionParsed);
        return new DeploymentPollResult(DeploymentAction.OfferDashboardUpdate, candidate);
    }

    /// <summary>Swaps a local exe with a share exe: copies the share payload to
    /// '&lt;target&gt;.new', then uses File.Replace to swap it in while keeping a single
    /// '&lt;target&gt;.old' backup. A missing target exe (fresh install) is handled with a
    /// plain move. Deletes a stale '.new' file on failure. Returns false on any IO failure
    /// and never throws; the current exe and '.old' backup stay untouched on failure.</summary>
    public static bool ApplyWorkerBinarySwap(string shareExePath, string targetExePath)
    {
        if (string.IsNullOrEmpty(shareExePath) || string.IsNullOrEmpty(targetExePath))
            return false;

        var newFilePath = targetExePath + NewSuffix;
        var oldFilePath = targetExePath + OldSuffix;

        try
        {
            // Remove a stale '.new' left behind by a previous failed swap; if this file is
            // locked the copy below fails too and we bail out with the target untouched.
            File.Delete(newFilePath);

            File.Copy(shareExePath, newFilePath, true);

            if (File.Exists(targetExePath))
                File.Replace(newFilePath, targetExePath, oldFilePath);
            else
                File.Move(newFilePath, targetExePath);

            return true;
        }
        catch (IOException)
        {
            TryDeleteFile(newFilePath);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            TryDeleteFile(newFilePath);
            return false;
        }
    }

    /// <summary>Best-effort delete of a scratch file; returns false when the file could not be
    /// removed (it will be retried on the next swap attempt).</summary>
    private static bool TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}