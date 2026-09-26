using System;
using System.IO;
using System.Reflection;
using System.Threading;
using FastBuild.Dashboard.Configuration;
using NLog;

namespace FastBuild.Dashboard.Services.Update;

/// <summary>
/// Timer-driven singleton that polls the deployments share (FASTBUILD_DEPLOYMENTS_PATH) for
/// FBuildWorker / FBDashboard updates, mirroring the BrokerageService pattern. Worker updates
/// are applied automatically; Dashboard self-updates are only announced via an event.
/// All events are raised on a background timer thread; UI subscribers must marshal to the
/// dispatcher before touching WPF.
/// </summary>
internal sealed class DeploymentService : IDeploymentService
{
    private const string DeploymentsPathVariable = "FASTBUILD_DEPLOYMENTS_PATH";
    private const int FirstPollDelayMs = 30 * 1000;
    private const int PollIntervalMs = 15 * 60 * 1000;

    private const string WorkerShareFolderName = "FBuildWorker";
    private const string WorkerShareExeFileName = "FBuildWorker.exe";
    private const string WorkerLocalFolderName = "FBuild";
    private const string WorkerLocalExeFileName = "FBuildWorker.exe";
    private const string DashboardShareFolderName = "FBDashboard";
    private const string DashboardSetupExeFileName = "FBDashboard-setup.exe";
    private const string SidecarFileName = "version.txt";

    private static readonly Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly Timer _pollTimer;
    private readonly object _pollLock = new object();

    private string _lastAppliedWorkerVersion;
    private string _lastOfferedDashboardVersion;
    private string _lastWarnedUnreachablePath;

    public event Action<DeploymentCandidate> WorkerUpdateApplied;
    public event Action<DeploymentCandidate> DashboardUpdateAvailable;

    public DeploymentService()
    {
        if (string.IsNullOrEmpty(DeploymentPath))
        {
            Logger.Info($"{DeploymentsPathVariable} is not set; deployment auto-update is disabled.");
            return;
        }

        Logger.Info($"Deployment auto-update enabled (share: {DeploymentPath}).");
        _pollTimer = new Timer(PollCallback, null, FirstPollDelayMs, PollIntervalMs);
    }

    public string DeploymentPath => Environment.GetEnvironmentVariable(DeploymentsPathVariable);

    public DeploymentPollResult CheckForUpdate(DeploymentComponent component)
    {
        var deploymentsPath = DeploymentPath;
        if (string.IsNullOrEmpty(deploymentsPath))
            return DeploymentPollResult.None;

        try
        {
            switch (component)
            {
                case DeploymentComponent.FBuildWorker:
                {
                    DeploymentEvaluator.TryReadVersionFile(GetLocalWorkerSidecarPath(),
                        out var localWorkerVersion);

                    return DeploymentEvaluator.EvaluateWorker(
                        Path.Combine(deploymentsPath, WorkerShareFolderName), WorkerShareExeFileName,
                        localWorkerVersion);
                }
                case DeploymentComponent.Dashboard:
                {
                    return DeploymentEvaluator.EvaluateDashboard(
                        Path.Combine(deploymentsPath, DashboardShareFolderName), DashboardSetupExeFileName,
                        GetLocalDashboardVersion().ToString());
                }
                default:
                    return DeploymentPollResult.None;
            }
        }
        catch (IOException ex)
        {
            Logger.Debug(ex, $"Failed to evaluate {component} deployment.");
            return DeploymentPollResult.None;
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.Debug(ex, $"Failed to evaluate {component} deployment.");
            return DeploymentPollResult.None;
        }
    }

    public bool ApplyWorkerUpdate(DeploymentCandidate candidate)
    {
        if (candidate == null || candidate.Component != DeploymentComponent.FBuildWorker)
            return false;

        var workerFolderPath = GetLocalWorkerFolderPath();
        var targetExePath = Path.Combine(workerFolderPath, WorkerLocalExeFileName);

        if (!DeploymentEvaluator.ApplyWorkerBinarySwap(candidate.ShareExePath, targetExePath))
            return false;

        try
        {
            Directory.CreateDirectory(workerFolderPath);
            File.WriteAllText(Path.Combine(workerFolderPath, SidecarFileName), candidate.ShareVersion);
        }
        catch (IOException ex)
        {
            Logger.Warn(ex,
                $"Worker binary swapped to {candidate.ShareVersion} but the local sidecar write failed.");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.Warn(ex,
                $"Worker binary swapped to {candidate.ShareVersion} but the local sidecar write failed.");
            return false;
        }

        return true;
    }

    private void PollCallback(object state)
    {
        // Never overlap polls: the previous one may still be blocked on network IO.
        if (!Monitor.TryEnter(_pollLock))
            return;

        try
        {
            var deploymentsPath = DeploymentPath;
            if (string.IsNullOrEmpty(deploymentsPath))
                return;

            if (!Directory.Exists(deploymentsPath))
            {
                if (!string.Equals(_lastWarnedUnreachablePath, deploymentsPath, StringComparison.OrdinalIgnoreCase))
                {
                    _lastWarnedUnreachablePath = deploymentsPath;
                    Logger.Warn($"Deployments share is unreachable: {deploymentsPath}");
                }
                return;
            }

            _lastWarnedUnreachablePath = null;

            PollWorker();
            PollDashboard();
        }
        finally
        {
            Monitor.Exit(_pollLock);
        }
    }

    private void PollWorker()
    {
        var result = CheckForUpdate(DeploymentComponent.FBuildWorker);
        if (result.Action == DeploymentAction.None)
        {
            Logger.Debug("Worker deployment check: no update pending.");
            return;
        }

        var candidate = result.Candidate;

        if (!AppSettings.Default.AutoUpdateEnabled)
        {
            Logger.Info(
                $"FASTBuild worker update {candidate.ShareVersion} is available, but auto-update is disabled.");
            return;
        }

        if (string.Equals(_lastAppliedWorkerVersion, candidate.ShareVersion, StringComparison.Ordinal))
        {
            Logger.Debug($"Worker update {candidate.ShareVersion} was already applied this session.");
            return;
        }

        if (!ApplyWorkerUpdate(candidate))
        {
            Logger.Warn($"Failed to apply FASTBuild worker update {candidate.ShareVersion}; retrying next poll.");
            return;
        }

        _lastAppliedWorkerVersion = candidate.ShareVersion;
        Logger.Info($"FASTBuild worker updated to {candidate.ShareVersion}.");

        // Raised on the poll timer thread - subscribers must marshal to the UI thread.
        WorkerUpdateApplied?.Invoke(candidate);
    }

    private void PollDashboard()
    {
        var result = CheckForUpdate(DeploymentComponent.Dashboard);
        if (result.Action == DeploymentAction.None)
        {
            Logger.Debug("Dashboard deployment check: no update pending.");
            return;
        }

        var candidate = result.Candidate;

        if (!AppSettings.Default.AutoUpdateEnabled)
        {
            Logger.Info(
                $"FASTBuild Dashboard update {candidate.ShareVersion} is available, but auto-update is disabled.");
            return;
        }

        if (string.Equals(_lastOfferedDashboardVersion, candidate.ShareVersion, StringComparison.Ordinal))
        {
            Logger.Debug($"Dashboard update {candidate.ShareVersion} was already offered this session.");
            return;
        }

        _lastOfferedDashboardVersion = candidate.ShareVersion;
        Logger.Info($"FASTBuild Dashboard update {candidate.ShareVersion} is available.");

        // Raised on the poll timer thread - subscribers must marshal to the UI thread.
        DashboardUpdateAvailable?.Invoke(candidate);
    }

    /// <summary>Returns the ORIGINAL install location (the real {app}), not the shadow copy.
    /// When self-shadowed, the running process lives in %Temp%\FBDashboard and the
    /// ShadowContext carries the original exe location (see App.SetStartupWithWindows).</summary>
    private static string ResolveInstallRoot()
    {
        var app = App.Current;
        if (app?.IsShadowProcess == true &&
            !string.IsNullOrEmpty(app.ShadowContext?.OriginalLocation))
        {
            return Path.GetDirectoryName(app.ShadowContext.OriginalLocation);
        }

        var installRoot = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrEmpty(installRoot))
            installRoot = AppDomain.CurrentDomain.BaseDirectory;

        return installRoot;
    }

    private static string GetLocalWorkerFolderPath()
    {
        return Path.Combine(ResolveInstallRoot(), WorkerLocalFolderName);
    }

    private static string GetLocalWorkerExePath()
    {
        return Path.Combine(GetLocalWorkerFolderPath(), WorkerLocalExeFileName);
    }

    private static string GetLocalWorkerSidecarPath()
    {
        return Path.Combine(GetLocalWorkerFolderPath(), SidecarFileName);
    }

    private static Version GetLocalDashboardVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version;
    }
}