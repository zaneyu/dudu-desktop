using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Dudu.App.Hosting;

namespace Dudu.App;

public sealed partial class App : Application
{
    private static Func<string, CancellationToken, Task<WindowsCompanionBootstrap>>? _bootstrapFactory;
    private static CompanionUiActions? _productionActions;
    private WindowsCompanionBootstrap? _bootstrap;
    private CompanionStartupRunner? _startupRunner;
    private Task? _startupTask;
    private Window? _homeWindow;
    private Window? _settingsWindow;

    public App()
    {
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
        EnsureDefaultBootstrapFactory();
        _startupRunner = new CompanionStartupRunner(
            _bootstrapFactory!,
            ReportStartupFailure,
            ExitApplication);
        _startupTask = _startupRunner.RunAsync(args.Arguments, CancellationToken.None);
        _ = ObserveStartupAsync(_startupTask);
    }

    internal Task? StartupTask => _startupTask;

    private void EnsureDefaultBootstrapFactory()
    {
        if (_bootstrapFactory is not null) return;
        _productionActions ??= new CompanionUiActions(
            OpenHome,
            OpenSettings,
            ExitApplication);
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
        }
        catch (Exception exception)
        {
            ReportStartupFailure(exception);
            ExitApplication();
        }
    }

    private void OpenHome()
    {
        _homeWindow ??= new Window
        {
            Title = "Dudu Desktop Companion",
            Content = new TextBlock
            {
                Text = "Dudu is running.",
                Margin = new Thickness(24),
            },
        };
        _homeWindow.Activate();
    }

    private void OpenSettings(StartupSettingsService startup)
    {
        var launchAtSignIn = new CheckBox
        {
            Content = "Launch Dudu at sign-in",
            IsChecked = startup.Current.LaunchAtSignIn,
            Margin = new Thickness(24),
        };
        launchAtSignIn.Checked += (_, _) => _ = ApplyStartupSettingAsync(startup, true);
        launchAtSignIn.Unchecked += (_, _) => _ = ApplyStartupSettingAsync(startup, false);
        _settingsWindow ??= new Window
        {
            Title = "Dudu settings",
            Content = launchAtSignIn,
        };
        _settingsWindow.Content = launchAtSignIn;
        _settingsWindow.Activate();
    }

    private static async Task ApplyStartupSettingAsync(
        StartupSettingsService startup,
        bool enabled)
    {
        try
        {
            await startup.SetLaunchAtSignInAsync(enabled);
        }
        catch (Exception exception)
        {
            Trace.TraceError("Dudu startup setting failed: {0}", exception);
        }
    }

    private static void ReportStartupFailure(Exception exception) =>
        Trace.TraceError("Dudu startup failed: {0}", exception);

    private static void ExitApplication()
    {
        Current?.Exit();
    }
}
