using System.Xml.Linq;
using Dudu.App.Notifications;
using Xunit;

namespace Dudu.App.Tests.Packaging;

public sealed class PackagedRuntimeManifestTests
{
    private static readonly XNamespace Foundation =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Desktop =
        "http://schemas.microsoft.com/appx/manifest/desktop/windows10";
    private static readonly XNamespace Com =
        "http://schemas.microsoft.com/appx/manifest/com/windows10";
    private static readonly XNamespace Virtualization =
        "http://schemas.microsoft.com/appx/manifest/virtualization/windows10";
    private static readonly XNamespace RestrictedCapabilities =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

    [Fact]
    public void Manifest_keeps_only_the_shared_Dudu_LocalAppData_root_unvirtualized()
    {
        var manifest = LoadManifest();

        var excludedDirectories = manifest
            .Descendants(Virtualization + "ExcludedDirectory")
            .Select(element => element.Value)
            .ToArray();

        Assert.Equal(["$(KnownFolder:LocalAppData)\\DuduDesktop"], excludedDirectories);
        Assert.Contains(
            manifest.Descendants(RestrictedCapabilities + "Capability"),
            capability => (string?)capability.Attribute("Name") == "unvirtualizedResources");
    }

    [Fact]
    public void Manifest_declares_the_packaged_startup_task_and_notification_activator_for_the_app_executable()
    {
        var manifest = LoadManifest();
        var extensions = manifest.Descendants(Foundation + "Application")
            .Single()
            .Element(Foundation + "Extensions")!;
        var startup = extensions.Elements(Desktop + "Extension")
            .Single(extension => (string?)extension.Attribute("Category") == "windows.startupTask");
        var activator = extensions.Elements(Desktop + "Extension")
            .Single(extension => (string?)extension.Attribute("Category") == "windows.toastNotificationActivation");
        var server = extensions.Elements(Com + "Extension")
            .Single(extension => (string?)extension.Attribute("Category") == "windows.comServer")
            .Descendants(Com + "ExeServer")
            .Single();

        Assert.Equal("Dudu.App.exe", (string?)startup.Attribute("Executable"));
        Assert.Equal("Windows.FullTrustApplication", (string?)startup.Attribute("EntryPoint"));
        Assert.Equal("DuduDesktopStartupTask", (string?)startup.Element(Desktop + "StartupTask")?.Attribute("TaskId"));
        Assert.Equal("false", (string?)startup.Element(Desktop + "StartupTask")?.Attribute("Enabled"));

        var clsid = (string?)activator.Element(Desktop + "ToastNotificationActivation")?.Attribute("ToastActivatorCLSID");
        Assert.Equal(WindowsAppNotificationSink.PackagedActivatorClsid, clsid);
        Assert.Equal("Dudu.App.exe", (string?)server.Attribute("Executable"));
        Assert.Equal("----AppNotificationActivated:", (string?)server.Attribute("Arguments"));
        Assert.Equal(clsid, (string?)server.Element(Com + "Class")?.Attribute("Id"));
    }

    [Fact]
    public void App_reads_cold_notification_activation_through_AppLifecycle_and_keeps_the_privacy_parser()
    {
        var root = FindRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "App.xaml.cs"));

        Assert.Contains("ExtendedActivationKind.AppNotification", app);
        Assert.Contains("AppNotificationActivatedEventArgs", app);
        Assert.Contains("NotificationActivation.TryParse", app);
        Assert.Contains("NotificationDestination(_notificationActivation)", app);
        Assert.Contains("ExtendedActivationKind.StartupTask", app);
    }

    [Fact]
    public void App_passes_computed_launch_arguments_to_the_bootstrap_runner()
    {
        var root = FindRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "App.xaml.cs"));

        Assert.Contains(
            "_startupRunner.RunAsync(_launchArguments, CancellationToken.None)",
            app);
    }

    private static XDocument LoadManifest()
    {
        var root = FindRepositoryRoot();
        return XDocument.Load(Path.Combine(root, "src", "Dudu.App", "Package.appxmanifest"));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PRODUCT.md"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found above the test output directory.");
    }
}
