using System;

namespace FastBuild.Dashboard.Services.Update;

public enum DeploymentComponent
{
    FBuildWorker,
    Dashboard
}

public enum DeploymentAction
{
    None,
    ApplyWorkerUpdate,
    OfferDashboardUpdate
}

/// <summary>A valid, complete candidate discovered on the deployments share.</summary>
public sealed class DeploymentCandidate
{
    public DeploymentCandidate(DeploymentComponent component, string shareExePath, string shareVersion,
        Version shareVersionParsed)
    {
        Component = component;
        ShareExePath = shareExePath;
        ShareVersion = shareVersion;
        ShareVersionParsed = shareVersionParsed;
    }

    public DeploymentComponent Component { get; }

    /// <summary>Full path of the payload executable on the share,
    /// e.g. &lt;root&gt;\FBuildWorker\FBuildWorker.exe.</summary>
    public string ShareExePath { get; }

    /// <summary>Content of the share version.txt sidecar (trimmed).</summary>
    public string ShareVersion { get; }

    /// <summary>Parsed share version; non-null only for Dashboard components (4-part assembly version).</summary>
    public Version ShareVersionParsed { get; }
}

/// <summary>Result of evaluating the share against local state this poll cycle.</summary>
public sealed class DeploymentPollResult
{
    public static DeploymentPollResult None { get; } =
        new DeploymentPollResult(DeploymentAction.None, null);

    public DeploymentPollResult(DeploymentAction action, DeploymentCandidate candidate)
    {
        Action = action;
        Candidate = candidate;
    }

    public DeploymentAction Action { get; }

    /// <summary>The discovered candidate; null when Action is None.</summary>
    public DeploymentCandidate Candidate { get; }
}