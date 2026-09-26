using System;

namespace FastBuild.Dashboard.Services.Update;

/// <summary>Contract mirrors IBrokerageService (singleton, registered in AppBootstrapper.Configure).</summary>
public interface IDeploymentService
{
    /// <summary>Resolves FASTBUILD_DEPLOYMENTS_PATH; null/empty disables the feature.</summary>
    string DeploymentPath { get; }

    /// <summary>Compares one component's share sidecar vs local state. Pure logic; unit-testable.</summary>
    DeploymentPollResult CheckForUpdate(DeploymentComponent component);

    /// <summary>Swaps {app}\FBuild\FBuildWorker.exe (via .new + File.Replace, keeping one .old
    /// backup) and writes the local version.txt. Returns false on any IO failure (no partial swap).</summary>
    bool ApplyWorkerUpdate(DeploymentCandidate candidate);

    /// <summary>Raised after a worker update was applied (tray balloon, once per version).
    /// Raised on a background timer thread; UI subscribers must marshal before touching WPF.</summary>
    event Action<DeploymentCandidate> WorkerUpdateApplied;

    /// <summary>Raised when the share holds a newer FBDashboard-setup.exe (tray prompt → /SILENT).
    /// Raised on a background timer thread; UI subscribers must marshal before touching WPF.</summary>
    event Action<DeploymentCandidate> DashboardUpdateAvailable;
}