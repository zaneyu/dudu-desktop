using Xunit;

namespace Dudu.App.Tests.Ui;

public sealed class XamlContractTests
{
    [Fact]
    public void Onboarding_and_primary_button_xaml_use_valid_accessible_contracts()
    {
        var root = FindRepositoryRoot();
        var onboarding = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "OnboardingPage.xaml"));
        var controls = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Themes", "Controls.xaml"));
        var colors = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Themes", "Colors.xaml"));

        Assert.Contains("SmallChange=\"0.1\"", onboarding);
        Assert.DoesNotContain("StepFrequency=", onboarding);
        Assert.Contains("Foreground=\"{ThemeResource PrimaryButtonForegroundBrush}\"", controls);
        Assert.Contains("SystemColorHighlightTextColor", colors);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PRODUCT.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found from the test output path.");
    }
}
