using Xunit;

namespace Dudu.Infrastructure.Tests.Hosting;

public sealed class StartupContractTests
{
    [Fact]
    public void App_constructor_initializes_merged_application_resources_before_startup()
    {
        var root = FindRepositoryRoot();
        var appCode = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "App.xaml.cs"));
        var appXaml = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "App.xaml"));
        var constructorStart = appCode.IndexOf("public App()", StringComparison.Ordinal);
        var constructorEnd = appCode.IndexOf("\n    }", constructorStart, StringComparison.Ordinal);

        Assert.True(constructorStart >= 0);
        Assert.True(constructorEnd > constructorStart);
        var constructor = appCode[constructorStart..constructorEnd];
        Assert.Contains("InitializeComponent();", constructor);
        Assert.Contains("Source=\"Themes/Colors.xaml\"", appXaml);
        Assert.Contains("Source=\"Themes/Controls.xaml\"", appXaml);
    }

    [Fact]
    public void App_resources_merge_winui_control_resources_before_custom_dictionaries()
    {
        var root = FindRepositoryRoot();
        var appXaml = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "App.xaml"));
        var winUiResources = appXaml.IndexOf(
            "<XamlControlsResources xmlns=\"using:Microsoft.UI.Xaml.Controls\" />",
            StringComparison.Ordinal);
        var colorsResources = appXaml.IndexOf(
            "<ResourceDictionary Source=\"Themes/Colors.xaml\" />",
            StringComparison.Ordinal);
        var controlsResources = appXaml.IndexOf(
            "<ResourceDictionary Source=\"Themes/Controls.xaml\" />",
            StringComparison.Ordinal);

        Assert.True(winUiResources >= 0);
        Assert.True(colorsResources > winUiResources);
        Assert.True(controlsResources > winUiResources);
    }

    [Fact]
    public void Startup_hotkey_and_tray_registration_are_best_effort()
    {
        var root = FindRepositoryRoot();
        var bootstrap = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Dudu.App",
            "Hosting",
            "WindowsCompanionBootstrap.cs"));

        // Startup registers the shortcut persisted in preferences (it used to hard-code
        // HotkeyGesture.Default, silently reverting a chosen shortcut on every restart) and
        // still falls back to the default when the stored one is unusable or taken.
        Assert.Contains("PersistedHotkeyRegistration.Apply(", bootstrap);
        Assert.Contains("hotkey.SetGesture(HotkeyGesture.Default)", bootstrap);
        Assert.DoesNotContain("_hotkey.SetGesture(HotkeyGesture.Default)", bootstrap);
        Assert.Contains("Dudu global hotkey unavailable", bootstrap);
        Assert.Contains("_tray.Attach(", bootstrap);
        Assert.Contains("Dudu tray icon unavailable", bootstrap);
    }

    [Fact]
    public void Startup_toast_activation_subscription_is_best_effort()
    {
        var root = FindRepositoryRoot();
        var composition = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Dudu.App",
            "Hosting",
            "WindowsCompanionProductionComposition.cs"));

        Assert.Contains("NotificationInvoked +=", composition);
        Assert.Contains("Dudu toast activation unavailable", composition);
    }

    [Fact]
    public void Startup_requires_windows_11_24h2_with_a_dedicated_phase()
    {
        var root = FindRepositoryRoot();
        var composition = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Dudu.App",
            "Hosting",
            "WindowsCompanionProductionComposition.cs"));

        Assert.Contains("IsWindowsVersionAtLeast(10, 0, 26100)", composition);
        Assert.Contains("\"os-version\"", composition);

        var installer = File.ReadAllText(Path.Combine(root, "installer", "DuduDesktop.iss"));
        Assert.Contains("MinVersion=10.0.26100", installer);
    }

    [Fact]
    public void Delete_local_data_does_not_require_a_configured_relay()
    {
        var root = FindRepositoryRoot();
        var composition = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Dudu.App",
            "Hosting",
            "WindowsCompanionProductionComposition.cs"));

        // RemoteSyncService only exists when a relay URL resolves; the offline
        // wipe must degrade to local-only instead of throwing.
        Assert.DoesNotContain("GetRequiredService<RemoteSyncService>()", composition);
        Assert.Contains("GetService<RemoteSyncService>()", composition);
    }

    [Fact]
    public void Corrupt_database_is_quarantined_instead_of_crash_looping()
    {
        var root = FindRepositoryRoot();
        var database = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Dudu.Infrastructure",
            "Data",
            "Database.cs"));

        Assert.Contains("dudu-corrupt-", database);
        Assert.Contains("IsCorruption", database);
    }

    [Fact]
    public void Safe_mode_does_not_start_the_full_overlay_host()
    {
        var root = FindRepositoryRoot();
        var composition = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Dudu.App",
            "Hosting",
            "WindowsCompanionProductionComposition.cs"));
        var safeRuntime = composition.IndexOf(
            "private sealed class SafeModePrimaryRuntime",
            StringComparison.Ordinal);

        Assert.True(safeRuntime >= 0);
        var body = composition[safeRuntime..];
        Assert.DoesNotContain("await host.StartAsync", body, StringComparison.Ordinal);
        Assert.Contains("await actions.OpenSettings", body, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DuduDesktop.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
