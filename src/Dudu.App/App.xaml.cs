using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Dudu.App.Hosting;
using Dudu.App.Notifications;
using Dudu.App.System;
using Dudu.App.Windows;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
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
    private NotificationActivation? _notificationActivation;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly AwaitableUiDispatcher _uiDispatcher;
    private Timer? _performanceAllocationTimer;

    public App()
    {
        InitializeComponent();
        SubscribeGlobalCrashReporting();
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
        var activation = TryGetAppActivation();
        _notificationActivation = TryGetNotificationActivation(activation);
        _launchArguments = activation?.Kind == ExtendedActivationKind.StartupTask
            ? "--background"
            : args.Arguments ?? string.Empty;
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
        _startupTask = _startupRunner.RunAsync(_launchArguments, CancellationToken.None);
        _ = ObserveStartupAsync(_startupTask);
    }

    internal Task? StartupTask => _startupTask;

    private static async Task RunSelfTestAndExitAsync()
    {
        var exitCode = SelfTestRunner.FailureExitCode;
        try
        {
            exitCode = await SelfTestRunner.RunAsync(AppPaths.ForCurrentUser());
        }
        catch (Exception exception)
        {
            // --self-test is a diagnostic entry point. Its failure must become
            // a deterministic nonzero process exit, never an unobserved task
            // that leaves an installer smoke test waiting forever.
            try
            {
                var paths = AppPaths.ForCurrentUser();
                StartupFailureLogger.Record(paths, "self-test", exception);
            }
            catch
            {
            }
        }

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

            var notificationDestination = NotificationDestination(_notificationActivation);
            if (notificationDestination is not null)
            {
                await NavigateSettingsDestinationAsync(notificationDestination, CancellationToken.None);
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
        catch (Exception)
        {
            // Best-effort telemetry only; any write failure must never crash the app.
        }
    }

    private static void ReportStartupFailure(Exception exception)
    {
        var phase = exception is StartupPhaseException phaseException
            ? phaseException.Phase
            : "bootstrap";
        try
        {
            StartupFailureLogger.Record(AppPaths.ForCurrentUser(), phase, exception);
        }
        catch
        {
            // Failure reporting must never prevent the controlled exit path.
        }

        Trace.TraceError("Dudu startup failed: {0}", exception);
    }

    /// <summary>
    /// Subscribes the last-chance handlers that persist otherwise-invisible
    /// field crashes to <c>startup-failure.log</c> (redacted). Each handler
    /// delegates to <see cref="GlobalCrashReporting"/> and never throws out
    /// of the handler. The WinUI handler deliberately does not mark the
    /// exception handled, so the process still terminates as before — now
    /// with a persisted record of why.
    /// </summary>
    private void SubscribeGlobalCrashReporting()
    {
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private static void OnUnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        GlobalCrashReporting.ReportCurrentUser(
            GlobalCrashReporting.PhaseUnhandledUi,
            e.Exception);
    }

    private static void OnDomainUnhandledException(
        object? sender,
        global::System.UnhandledExceptionEventArgs e) =>
        GlobalCrashReporting.ReportCurrentUser(
            GlobalCrashReporting.PhaseUnhandledDomain,
            e.ExceptionObject as Exception);

    private static void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        GlobalCrashReporting.ReportCurrentUser(
            GlobalCrashReporting.PhaseUnobservedTask,
            e.Exception);
        try
        {
            // The failure is now persisted; observing it here avoids a
            // nondeterministic teardown at finalizer time with no context.
            e.SetObserved();
        }
        catch
        {
        }
    }

    private static AppActivationArguments? TryGetAppActivation()
    {
        try
        {
            return AppInstance.GetCurrent().GetActivatedEventArgs();
        }
        catch (Exception exception)
        {
            // App lifecycle activation is best-effort in unpackaged launches;
            // a normal launch must not fail because it has no package data.
            Trace.TraceInformation("Dudu notification activation unavailable: {0}", exception.Message);
            return null;
        }
    }

    private static NotificationActivation? TryGetNotificationActivation(AppActivationArguments? activation) =>
        activation?.Kind == ExtendedActivationKind.AppNotification
            ? NotificationActivation.TryParse(
                (activation.Data as AppNotificationActivatedEventArgs)?.Arguments)
            : null;

    private static string? NotificationDestination(NotificationActivation? activation) =>
        activation?.Action switch
        {
            NotificationActivationAction.OpenNote => "notes",
            NotificationActivationAction.ReminderDone => "reminders",
            NotificationActivationAction.ReminderSnooze => "reminders",
            _ => null,
        };

    private Task ExitApplicationAsync(CancellationToken cancellationToken) =>
        _uiDispatcher.InvokeAsync(ExitApplicationCore, cancellationToken);

    private static void ExitApplicationCore()
    {
        (Current as App)?._performanceAllocationTimer?.Dispose();
        Current?.Exit();
    }
}
