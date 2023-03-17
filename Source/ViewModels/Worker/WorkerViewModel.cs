﻿using System.Linq;
using System.Timers;
using Caliburn.Micro;
using FastBuild.Dashboard.Services.Worker;

namespace FastBuild.Dashboard.ViewModels.Worker;

internal class WorkerViewModel : PropertyChangedBase, IMainPage
{
    private readonly IWorkerAgentService _workerAgentService;

    private bool _isTicking;
    private string _statusTitle;
    private string _workerErrorMessage;

    public WorkerViewModel()
    {
        StatusTitle = "Preparing...";

        _workerAgentService = IoC.Get<IWorkerAgentService>();
        _workerAgentService.WorkerRunStateChanged += WorkerAgentService_WorkerRunStateChanged;
        _workerAgentService.Initialize();

        var tickTimer = new Timer(500)
        {
            AutoReset = true
        };
        tickTimer.Elapsed += Tick;
        tickTimer.Start();
    }

    public bool IsWorkerRunning => _workerAgentService.IsRunning;

    public string WorkerErrorMessage
    {
        get => _workerErrorMessage;
        private set
        {
            if (value == _workerErrorMessage) return;

            _workerErrorMessage = value;
            this.NotifyOfPropertyChange();
        }
    }

    public string StatusTitle
    {
        get => _statusTitle;
        private set
        {
            if (value == _statusTitle) return;

            _statusTitle = value;
            this.NotifyOfPropertyChange();
        }
    }

    public BindableCollection<WorkerCoreStatusViewModel> CoreStatuses { get; }
        = new BindableCollection<WorkerCoreStatusViewModel>();

    public string DisplayName => "Local Worker";

    public string Icon => "Worker";

    private void Tick(object sender, ElapsedEventArgs e)
    {
        if (!IsWorkerRunning) return;

        if (_isTicking) return;

        _isTicking = true;

        var statuses = _workerAgentService.GetStatus();

        for (var i = CoreStatuses.Count - 1; i > statuses.Length; --i) CoreStatuses.RemoveAt(i);

        for (var i = CoreStatuses.Count; i < statuses.Length; ++i) CoreStatuses.Add(new WorkerCoreStatusViewModel(i));

        for (var i = 0; i < CoreStatuses.Count; ++i) CoreStatuses[i].UpdateStatus(statuses[i]);

        if (statuses.All(s => s.State == WorkerCoreState.Disabled))
            StatusTitle = "Disabled";
        else if (statuses.Any(s => s.State == WorkerCoreState.Working))
            StatusTitle = "Working";
        else
            StatusTitle = "Idle";

        _isTicking = false;
    }

    private void WorkerAgentService_WorkerRunStateChanged(object sender, WorkerRunStateChangedEventArgs e)
    {
        this.NotifyOfPropertyChange(nameof(IsWorkerRunning));
        WorkerErrorMessage = e.ErrorMessage;

        if (!e.IsRunning) StatusTitle = "Worker Error";
    }
}