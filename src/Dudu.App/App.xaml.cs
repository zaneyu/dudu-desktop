using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Dudu.App.Hosting;
using Dudu.App.System;
using Dudu.App.Windows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Dudu.App;

public sealed partial class App : Application
{
    private static Func<string, CancellationToken, Task<WindowsCompanionBootstrap>>? _bootstrapFactory;
    private static CompanionUiActions? _productionActions;
    private WindowsCompanionBootstrap? _bootstrap;
    private CompanionStartupRunner? _startupRunner;
    private Task? _startupTask;
    private Window? _settingsWindow;
    private CompanionSettingsContext? _settingsContext;
    private string _launchArguments = string.Empty;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly AwaitableUiDispatcher _uiDispatcher;
    private Timer? _performanceAllocationTimer;

    public App()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("Dudu must start on a WinUI dispatcher thread.");
        _uiDispatcher = new AwaitableUiDispatcher(
            () => _dispatcherQueue.HasThreadAccess,
            callback => _dispatcherQueue.TryEnqueue(() => callback()));
        EnsureDefaultBootstrapFactory();
    }

    public static void ConfigureBootstrap(
        Func<string, CancellationToken, Task<WindowsCompanionBootstrap>> bootstrapFactory)
    {
        _bootstrapFactory = bootstrapFactory
            ?? throw new ArgumentNullException(nameof(bootstrapFactory));
    }

    public static void ConfigureProduction(CompanionUiActions actions)
    {
        _productionActions = actions ?? throw new ArgumentNullException(nameof(actions));
        _bootstrapFactory = static (arguments, cancellationToken) =>
            Task.FromResult(
                WindowsCompanionProductionComposition.CreateBootstrap(
                    arguments,
                    _productionActions!));
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        ApplyTextScaleOverrideFromEnvironment();
        ApplyPerformanceAllocationReportingFromEnvironment();
        _launchArguments = args.Arguments ?? string.Empty;
        if (CompanionLaunchOptions.Parse(_launchArguments).SelfTest)
        {
            // Self-test short-circuits before the bootstrap factory runs: no
            // window, tray, overlay, preference write, startup registration,
            // or relay connection is ever created for this launch.
            _startupTask = RunSelfTestAndExitAsync();
            return;
        }

        EnsureDefaultBootstrapFactory();
        _startupRunner = new CompanionStartupRunner(
            _bootstrapFactory!,
            ReportStartupFailure,
            ExitApplicationCore);
        _startupTask = _startupRunner.RunAsync(args.Arguments ?? string.Empty, CancellationToken.None);
        _ = ObserveStartupAsync(_startupTask);
    }

    internal Task? StartupTask => _startupTask;

    private static async Task RunSelfTestAndExitAsync()
    {
        var exitCode = await SelfTestRunner.RunAsync(AppPaths.ForCurrentUser());
        Environment.Exit(exitCode);
    }

    private void EnsureDefaultBootstrapFactory()
    {
        if (_bootstrapFactory is not null) return;
        _productionActions ??= new CompanionUiActions(
            OpenHome,
            OpenSettings,
            ExitApplicationAsync,
            ConfigureSettings,
            NavigateSettingsDestinationAsync);
        _bootstrapFactory = static (arguments, cancellationToken) =>
            Task.FromResult(
                WindowsCompanionProductionComposition.CreateBootstrap(
                    arguments,
                    _productionActions!));
    }

    private async Task ObserveStartupAsync(Task startupTask)
    {
        try
        {
            await startupTask;
            _bootstrap = _startupRunner?.Bootstrap;
            if (_settingsContext is not null
                && CompanionLaunchOptions.Parse(_launchArguments)
                    .ShouldOpenSettings(_settingsContext.Profile))
            {
                await OpenHome(CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            ReportStartupFailure(exception);
            await ExitApplicationAsync(CancellationToken.None);
        }
    }

    private Task OpenHome(CancellationToken cancellationToken) =>
        _uiDispatcher.InvokeAsync(
            () => OpenSettingsCore(_settingsContext?.StartupSettings
                ?? throw new InvalidOperationException("Settings context is not ready.")),
            cancellationToken);

    private void ConfigureSettings(CompanionSettingsContext context)
    {
        _settingsContext = context ?? throw new ArgumentNullException(nameof(context));
    }

    private Task OpenSettings(
        StartupSettingsService startup,
        CancellationToken cancellationToken) =>
        _uiDispatcher.InvokeAsync(() => OpenSettingsCore(startup), cancellationToken);

    private void OpenSettingsCore(StartupSettingsService startup)
    {
        if (_settingsContext is null)
        {
            throw new InvalidOperationException("Settings context is not ready.");
        }

        if (_settingsWindow is null)
        {
            var view = new SettingsWindow(_settingsContext);
            _settingsWindow = new Window { Title = "dudu settings", Content = view };
            try
            {
                _settingsWindow.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
            }
            catch (Exception exception)
            {
                Trace.TraceInformation("Dudu Mica backdrop unavailable: {0}", exception.Message);
            }

            _settingsWindow.ExtendsContentIntoTitleBar = true;
            _settingsWindow.SetTitleBar(view.TitleBarElement);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.Activate();
    }

    private Task NavigateSettingsDestinationAsync(
        string destination,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        return _uiDispatcher.InvokeAsync(
            () => NavigateSettingsDestinationCore(destination),
            cancellationToken);
    }

    private void NavigateSettingsDestinationCore(string destination)
    {
        OpenSettingsCore(_settingsContext?.StartupSettings
            ?? throw new InvalidOperationException("Settings context is not ready."));
        if (_settingsWindow?.Content is not SettingsWindow settings)
        {
            throw new InvalidOperationException("The settings window is not available for navigation.");
        }

        settings.NavigateTo(destination);
    }

    /// <summary>
    /// Test-only text-scaling hook for <c>tests/Dudu.UiTests</c>. When
    /// <c>DUDU_TEXT_SCALE_PERCENT</c> is set to a positive integer, overrides the WinUI
    /// <c>ControlContentThemeFontSize</c> theme resource by that percentage so standard
    /// controls grow the way a Windows text-size accessibility setting would, without a
    /// <c>ScaleTransform</c> (which would scale layout bounds instead of font metrics and
    /// would not reproduce real clipping at larger point sizes).
    /// </summary>
    private void ApplyTextScaleOverrideFromEnvironment()
    {
        const double DefaultControlContentThemeFontSize = 15d;

        var raw = Environment.GetEnvironmentVariable("DUDU_TEXT_SCALE_PERCENT");
        if (string.IsNullOrWhiteSpace(raw)
            || !int.TryParse(raw, CultureInfo.InvariantCulture, out var percent)
            || percent <= 0
            || percent == 100)
        {
            return;
        }

        Resources["ControlContentThemeFontSize"] = DefaultControlContentThemeFontSize * percent / 100.0;
    }

    /// <summary>
    /// Test-only managed-allocation telemetry hook for
    /// <c>tests/Dudu.WindowsHarness/PerformanceScenario.cs</c>. When
    /// <c>DUDU_PERFORMANCE_ALLOCATION_REPORT</c> names a file path, appends a
    /// <c>unixTimeMs,totalAllocatedBytes</c> line to it every 60 seconds, starting 60 seconds
    /// after launch so the first sample is taken after startup allocation has settled ("after
    /// warmup"). The harness reads the first and last lines to compute a slope in MB/hour; it
    /// never reads this process's memory directly, since <see cref="GC.GetTotalAllocatedBytes"/>
    /// only reports the calling process's own managed heap.
    /// </summary>
    private void ApplyPerformanceAllocationReportingFromEnvironment()
    {
        var reportPath = Environment.GetEnvironmentVariable("DUDU_PERFORMANCE_ALLOCATION_REPORT");
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            return;
        }

        var samplePeriod = TimeSpan.FromSeconds(60);
        _performanceAllocationTimer = new Timer(
            _ => AppendAllocationSample(reportPath),
            state: null,
            dueTime: samplePeriod,
            period: samplePeriod);
    }

    private static void AppendAllocationSample(string reportPath)
    {
        try
        {
            var timestampUtcMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var totalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true);
            File.AppendAllText(
                reportPath,
                string.Create(CultureInfo.InvariantCulture, $"{timestampUtcMilliseconds},{totalAllocatedBytes}\n"));
        }
        catch (IOException)
        {
            // Best-effort telemetry only; a transient write failure must never crash the app.
        }
    }

    private static void ReportStartupFailure(Exception exception) =>
        Trace.TraceError("Dudu startup failed: {0}", exception);

    private Task ExitApplicationAsync(CancellationToken cancellationToken) =>
        _uiDispatcher.InvokeAsync(ExitApplicationCore, cancellationToken);

    private static void ExitApplicationCore()
    {
        Current?.Exit();
    }
}
