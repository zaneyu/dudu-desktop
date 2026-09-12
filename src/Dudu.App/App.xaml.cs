using Microsoft.UI.Xaml;
using Dudu.App.Hosting;

namespace Dudu.App;

public sealed partial class App : Application
{
    private static Func<string, CancellationToken, Task<WindowsCompanionBootstrap>>? _bootstrapFactory;
    private static CompanionUiActions? _productionActions;
    private WindowsCompanionBootstrap? _bootstrap;

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
                    _productionActions!));
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var factory = _bootstrapFactory
            ?? throw new InvalidOperationException(
                "ConfigureBootstrap or ConfigureProduction must be called before App launch.");
        _ = StartBootstrapAsync(factory, args.Arguments);
    }

    private async Task StartBootstrapAsync(
        Func<string, CancellationToken, Task<WindowsCompanionBootstrap>> factory,
        string arguments)
    {
        var bootstrap = await factory(arguments, CancellationToken.None);
        if (!await bootstrap.StartAsync())
        {
            await bootstrap.DisposeAsync();
            Environment.Exit(0);
            return;
        }

        _bootstrap = bootstrap;
    }
}
