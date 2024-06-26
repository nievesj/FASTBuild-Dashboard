﻿using System.Collections.Generic;
using System.Windows.Media;
using Caliburn.Micro;

namespace FastBuild.Dashboard.ViewModels.Build;

internal sealed class MergedBuildJobViewModel : PropertyChangedBase, IBuildJobViewModel
{
    private readonly LinkedList<BuildJobViewModel> _mergedJobs;

    public MergedBuildJobViewModel(BuildJobViewModel first, BuildJobViewModel last)
    {
        _mergedJobs = new LinkedList<BuildJobViewModel>();
        _mergedJobs.AddLast(first);
        _mergedJobs.AddLast(last);
    }

    public BuildCoreViewModel OwnerCore => _mergedJobs.First.Value.OwnerCore;
    public double StartTimeOffset => _mergedJobs.First.Value.StartTimeOffset;
    public double EndTimeOffset => _mergedJobs.Last.Value.EndTimeOffset;
    public Brush UIForeground => _mergedJobs.First.Value.UIForeground;
    public Brush UIBackground => _mergedJobs.First.Value.UIBackground;
    public Brush UIBorderBrush => _mergedJobs.First.Value.UIBorderBrush;
    public string DisplayName => $"{0} Jobs";

    public void AddLast(BuildJobViewModel job)
    {
        _mergedJobs.AddLast(job);
        OnJobsChanged();
        NotifyOfPropertyChange(nameof(EndTimeOffset));
    }

    public void AddFirst(BuildJobViewModel job)
    {
        _mergedJobs.AddFirst(job);
        OnJobsChanged();
        NotifyOfPropertyChange(nameof(StartTimeOffset));
    }

    private void OnJobsChanged()
    {
        NotifyOfPropertyChange(nameof(DisplayName));
    }
}