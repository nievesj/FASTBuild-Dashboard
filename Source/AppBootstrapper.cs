using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Caliburn.Micro;
using FastBuild.Dashboard.Services;
using FastBuild.Dashboard.Services.Build;
using FastBuild.Dashboard.Services.Build.SourceEditor;
using FastBuild.Dashboard.Services.Update;
using FastBuild.Dashboard.Services.Worker;
using FastBuild.Dashboard.Support;
using FastBuild.Dashboard.ViewModels;
using NLog;

namespace FastBuild.Dashboard;

internal class AppBootstrapper : BootstrapperBase
{
    private readonly SimpleContainer _container = new SimpleContainer();
    private static readonly Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    public AppBootstrapper()
    {
        Logger.Info($"FBuild Dashboard");
        Logger.Info($"Version: {Assembly.GetExecutingAssembly().GetName().Version}");

        Initialize();
    }

    protected override void Configure()
    {
        Logger.Info("Configuring container...");
        _container.Singleton<IWindowManager, WindowManager>();
        _container.Singleton<IEventAggregator, EventAggregator>();
        _container.Singleton<IBuildViewportService, BuildViewportService>();
        _container.Singleton<IBrokerageService, BrokerageService>();
        _container.Singleton<IDeploymentService, DeploymentService>();
        _container.Singleton<IWorkerAgentService, WorkerAgentService>();
        _container.Singleton<IExternalSourceEditorService, ExternalSourceEditorService>();
        
        _container.PerRequest<MainWindowViewModel>();
    }
    protected override object GetInstance(Type service, string key)
    {
        return _container.GetInstance(service, key);
    }
    protected override IEnumerable<object> GetAllInstances(Type service)
    {
        return _container.GetAllInstances(service);
    }
    protected override void BuildUp(object instance)
    {
        _container.BuildUp(instance);
    }

    protected override void OnStartup(object sender, StartupEventArgs e)
    {
#if DEBUG && !DEBUG_SINGLE_INSTANCE
        Logger.Warn("Running in DEBUG!");
        Logger.Info("Display Main Window");
        DisplayRootViewForAsync<MainWindowViewModel>();
#else
        var assemblyLocation = Assembly.GetEntryAssembly()?.Location;

        var identifier = assemblyLocation?.Replace('\\', '_');
        if (!SingleInstance<App>.InitializeAsFirstInstance(identifier))
        {
            Logger.Warn($"Exiting as this is not the first instance!");
            Environment.Exit(0);
        }

        if (App.Current.DoNotSpawnShadowExecutable || App.Current.IsShadowProcess)
        {
            Logger.Info($"Display Main Window");
            var displayTask = DisplayRootViewForAsync<MainWindowViewModel>();
            displayTask.Wait();
        }
        else
        {
            SpawnShadowProcess(e, assemblyLocation);
            Environment.Exit(0);
        }
#endif
    }
    protected override IEnumerable<Assembly> SelectAssemblies()
    {
        return new[] { Assembly.GetExecutingAssembly() };
    }

    private static void CreateShadowContext(string shadowPath)
    {
        var shadowContext = new ShadowContext();
        shadowContext.Save(shadowPath);
    }

    private static void SpawnShadowProcess(StartupEventArgs e, string assemblyLocation)
    {
        var assemblyLocationNoExtension = $"{Path.GetFileNameWithoutExtension(assemblyLocation)}";
        var assemblyDirectory = Path.GetDirectoryName(assemblyLocation);
        var shadowAssemblyName = $"{assemblyLocationNoExtension}.shadow.exe";
        var shadowAssemblyPath = Path.Combine(Path.GetTempPath(), "FBDashboard", shadowAssemblyName);
        var shadowDirectory = Path.GetDirectoryName(shadowAssemblyPath);
        Logger.Info($"Trying to spawn shadow process at {shadowAssemblyPath}");
        try
        {
            if (File.Exists(shadowAssemblyPath))
            {
                Logger.Info("Deleting current shadow exe");
                File.Delete(shadowAssemblyPath);
            }

            Debug.Assert(assemblyLocation != null, "assemblyLocation != null");
            Directory.CreateDirectory(shadowDirectory);
            Logger.Info("Copying shadow exe");
            File.Copy(assemblyLocation, shadowAssemblyPath);

            Logger.Info("Copying NLog.config");
            var shadowNLogConfigPath = Path.Combine(shadowDirectory, "NLog.config");
            var NLogConfigPath = Path.Combine($"{assemblyDirectory}","NLog.config");
            if (File.Exists(NLogConfigPath))
            {
                File.Copy(NLogConfigPath, shadowNLogConfigPath, true);
            }
            else
            {
                // NLog.config is content-copied next to the exe in real builds, but a bare exe
                // (e.g. collected without config files) must not abort the rest of the shadow
                // setup - this copy previously threw IOException and skipped the FBuild folder copy.
                Logger.Warn($"NLog.config not found next to the executable ({NLogConfigPath}) - shadow process will have no logging targets");
            }
            var workerFolder = Path.Combine(assemblyDirectory, "FBuild");
            var workerTargetFolder = Path.Combine(shadowDirectory, "FBuild");
            if (Directory.Exists(workerFolder))
            {
                Logger.Info("Copying FBuild folder");
                Directory.CreateDirectory(workerTargetFolder);
                // Copy all worker files. One locked file (e.g. a worker from a previous shadow
                // instance that was force-killed while still holding the exe) must not abort the
                // remaining copies - the shadow then falls back to whatever is already in place.
                foreach (var newPath in Directory.GetFiles(workerFolder, "*.*", SearchOption.TopDirectoryOnly))
                {
                    var targetPath = newPath.Replace(workerFolder, workerTargetFolder);

                    // Worker settings are user state (worker mode etc.) mutated by the running
                    // shadow - re-copying them on every spawn would reset the user's chosen
                    // mode on every dashboard restart. Seed only when the shadow has none yet.
                    if (newPath.EndsWith(".settings", StringComparison.OrdinalIgnoreCase) && File.Exists(targetPath))
                    {
                        continue;
                    }

                    try
                    {
                        File.Copy(newPath, targetPath, true);
                    }
                    catch (IOException ex)
                    {
                        Logger.Error($"Unable to copy '{newPath}' into the shadow directory: {ex.Message}");
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        Logger.Error($"Unable to copy '{newPath}' into the shadow directory: {ex.Message}");
                    }
                }
            }
            else
            {
                Logger.Error("FBuild folder not found! Unable to copy!");
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            // may be already running
            Logger.Error($"Unable to spawn shadow process: {ex.ToString()}");
        }
        catch (IOException ex)
        {
            // may be already running
            Logger.Error($"Unable to spawn shadow process: {ex.ToString()}");
        }

        CreateShadowContext(shadowAssemblyPath);
        SingleInstance<App>.Cleanup();

        Logger.Info($"Starting shadow process");
        Process.Start(new ProcessStartInfo
        {
            FileName = shadowAssemblyPath,
            Arguments = string.Join(" ", e.Args.Concat(new[] { AppArguments.ShadowProc }))
        });
    }
    protected override void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error($"Caught unhandled exception: {e.Exception}");
    }

    protected override void OnExit(object sender, EventArgs e)
    {
        Logger.Info("Exiting...");
        SingleInstance<App>.Cleanup();
        base.OnExit(sender, e);
    }
}