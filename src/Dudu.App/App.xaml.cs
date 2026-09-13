using System.Diagnostics;
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
        _launchArguments = args.Arguments ?? string.Empty;
        EnsureDefaultBootstrapFactory();
        _startupRunner = new CompanionStartupRunner(
            _bootstrapFactory!,
            ReportStartupFailure,
            ExitApplicationCore);
        _startupTask = _startupRunner.RunAsync(args.Arguments ?? string.Empty, CancellationToken.None);
        _ = ObserveStartupAsync(_startupTask);
    }

    internal Task? StartupTask => _startupTask;

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

    private static void ReportStartupFailure(Exception exception) =>
        Trace.TraceError("Dudu startup failed: {0}", exception);

    private Task ExitApplicationAsync(CancellationToken cancellationToken) =>
        _uiDispatcher.InvokeAsync(ExitApplicationCore, cancellationToken);

    private static void ExitApplicationCore()
    {
        Current?.Exit();
    }
}
