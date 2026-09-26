using System.ComponentModel;
using Dudu.App.ViewModels;
using Dudu.Core.Abstractions;
using Xunit;

namespace Dudu.App.Tests.ViewModels;

/// <summary>UI/UX regressions on the Connection (pairing) page's view model.</summary>
public sealed class ConnectionPageUxTests
{
    private static PairingCodeResult LiveCode(SettingsDataPagesFixture fixture, string code = "ABCD2345") =>
        new(PairingAvailability.Available, code, fixture.Clock.UtcNow.AddMinutes(10));

    [Fact]
    public void Confirmation_panel_is_hidden_until_an_action_is_requested()
    {
        var fixture = SettingsDataPagesFixture.Create();
        var viewModel = new ConnectionViewModel(fixture.Context);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.False(viewModel.HasPendingConfirmation);
        Assert.False(viewModel.CancelConfirmationCommand.CanExecute(null));

        viewModel.RequestForgetPairingCommand.Execute(null);
        Assert.True(viewModel.HasPendingConfirmation);
        Assert.Contains(nameof(ConnectionViewModel.HasPendingConfirmation), changed);
        Assert.True(viewModel.CancelConfirmationCommand.CanExecute(null));

        changed.Clear();
        viewModel.CancelConfirmationCommand.Execute(null);
        Assert.False(viewModel.HasPendingConfirmation);
        Assert.Contains(nameof(ConnectionViewModel.HasPendingConfirmation), changed);
    }

    [Fact]
    public async Task Expired_code_is_no_longer_offered_and_its_ready_message_clears()
    {
        var fixture = SettingsDataPagesFixture.Create();
        fixture.Pairing.NextCode = () => LiveCode(fixture);
        var viewModel = new ConnectionViewModel(fixture.Context);

        await viewModel.CreateCodeAsync(TestContext.Current.CancellationToken);
        Assert.True(viewModel.HasLiveCode);
        Assert.Equal("pairing code ABCD2345", viewModel.PairingCodeText);
        Assert.Contains("10 min left", viewModel.CodeExpiryText);
        Assert.Equal("yayyy code ready for 10 min", viewModel.StatusMessage);

        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(9).AddSeconds(30);
        viewModel.UpdateCodeExpiry();
        Assert.Contains("less than 1 min left", viewModel.CodeExpiryText);
        Assert.Equal("yayyy code ready for 10 min", viewModel.StatusMessage);

        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
        viewModel.UpdateCodeExpiry();

        Assert.False(viewModel.HasLiveCode);
        Assert.DoesNotContain("ABCD2345", viewModel.PairingCodeText);
        Assert.Equal("that code expired make a new one", viewModel.PairingCodeText);
        Assert.Equal("code expired", viewModel.CodeExpiryText);
        Assert.Null(viewModel.StatusMessage);
        Assert.Contains(nameof(ConnectionViewModel.PairingCodeText), changed);
        Assert.Contains(nameof(ConnectionViewModel.CodeExpiryText), changed);
    }

    [Fact]
    public async Task Code_expiry_is_shown_in_the_pcs_time_zone_with_a_countdown()
    {
        var fixture = SettingsDataPagesFixture.Create(SettingsDataPagesFixture.UtcMinusFive);
        fixture.Pairing.NextCode = () => LiveCode(fixture);
        var viewModel = new ConnectionViewModel(fixture.Context);

        await viewModel.CreateCodeAsync(TestContext.Current.CancellationToken);

        // 08:10 UTC is 03:10 at UTC-5.
        var expectedLocal = new DateTime(2026, 9, 19, 3, 10, 0).ToString("t", global::System.Globalization.CultureInfo.CurrentCulture);
        Assert.Equal($"code expires at {expectedLocal} 10 min left", viewModel.CodeExpiryText);
    }

    [Fact]
    public async Task Create_code_failure_explains_the_actual_reason_instead_of_always_relay_offline()
    {
        var fixture = SettingsDataPagesFixture.Create();
        var viewModel = new ConnectionViewModel(fixture.Context);

        fixture.Pairing.Reason = PairingStatusReason.RelayNotConfigured;
        fixture.Pairing.NextCode = () => PairingCodeResult.Offline;
        await viewModel.CreateCodeAsync(TestContext.Current.CancellationToken);
        Assert.Equal("no relay set up yet so codes cant be made notes stay local", viewModel.ErrorMessage);

        fixture.Pairing.Reason = PairingStatusReason.None;
        fixture.Pairing.NextCode = () => new PairingCodeResult(PairingAvailability.NeedsRepair, null, null);
        await viewModel.CreateCodeAsync(TestContext.Current.CancellationToken);
        Assert.Equal("pairing needs fixing forget pairing on this pc then make a new code", viewModel.ErrorMessage);

        fixture.Pairing.NextCode = () => PairingCodeResult.Offline;
        await viewModel.CreateCodeAsync(TestContext.Current.CancellationToken);
        Assert.Equal("oh no pairing unavailable relay offline", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Relay_failure_while_creating_a_code_shows_plain_copy_not_developer_text()
    {
        var fixture = SettingsDataPagesFixture.Create();
        fixture.Pairing.CreateCodeException = new RelayUnavailableException("The relay request timed out.");
        var viewModel = new ConnectionViewModel(fixture.Context);

        await viewModel.CreateCodeAsync(TestContext.Current.CancellationToken);

        Assert.Equal("cant reach the relay right now try again in a bit", viewModel.ErrorMessage);
        Assert.DoesNotContain("timed out", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task A_failed_create_code_clears_the_previous_success_line()
    {
        var fixture = SettingsDataPagesFixture.Create();
        fixture.Pairing.NextCode = () => LiveCode(fixture);
        var viewModel = new ConnectionViewModel(fixture.Context);
        await viewModel.CreateCodeAsync(TestContext.Current.CancellationToken);
        Assert.True(viewModel.HasStatus);

        fixture.Pairing.NextCode = () => PairingCodeResult.Offline;
        await viewModel.CreateCodeAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.HasError);
        Assert.False(viewModel.HasStatus);
        Assert.Null(viewModel.StatusMessage);
    }

    [Fact]
    public async Task Stale_error_clears_when_the_user_cancels_or_requests_another_action()
    {
        var fixture = SettingsDataPagesFixture.Create();
        fixture.Pairing.RevokeResult = PairingOperationResult.Unavailable("cannot revoke sessions right now");
        var viewModel = new ConnectionViewModel(fixture.Context);

        viewModel.RequestRevokeSessionsCommand.Execute(null);
        await viewModel.ConfirmAsync(TestContext.Current.CancellationToken);
        Assert.Equal("cannot revoke sessions right now", viewModel.ErrorMessage);

        viewModel.CancelConfirmationCommand.Execute(null);
        Assert.Null(viewModel.ErrorMessage);
        Assert.False(viewModel.HasError);

        viewModel.RequestRevokeSessionsCommand.Execute(null);
        await viewModel.ConfirmAsync(TestContext.Current.CancellationToken);
        Assert.True(viewModel.HasError);

        viewModel.RequestForgetPairingCommand.Execute(null);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task Actions_are_disabled_while_a_confirmed_action_is_running()
    {
        var fixture = SettingsDataPagesFixture.Create();
        fixture.Pairing.RevokeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new ConnectionViewModel(fixture.Context);

        viewModel.RequestRevokeSessionsCommand.Execute(null);
        var running = viewModel.ConfirmCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.CreateCodeCommand.CanExecute(null));
        Assert.False(viewModel.RefreshCommand.CanExecute(null));
        Assert.False(viewModel.RequestForgetPairingCommand.CanExecute(null));
        Assert.False(viewModel.RequestDeleteRemoteDeviceCommand.CanExecute(null));
        Assert.False(viewModel.CancelConfirmationCommand.CanExecute(null));

        fixture.Pairing.RevokeGate.SetResult();
        await running;

        Assert.False(viewModel.IsBusy);
        Assert.Equal(1, fixture.Pairing.RevokeCallCount);
        Assert.True(viewModel.CreateCodeCommand.CanExecute(null));
        Assert.True(viewModel.RefreshCommand.CanExecute(null));
        Assert.True(viewModel.RequestForgetPairingCommand.CanExecute(null));
        Assert.False(viewModel.HasPendingConfirmation);
    }

    [Fact]
    public async Task Refresh_picks_up_a_new_sender_session_without_leaving_the_page()
    {
        var fixture = SettingsDataPagesFixture.Create();
        var viewModel = new ConnectionViewModel(fixture.Context);
        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("no sender sessions yet ah", viewModel.SessionCountText);

        fixture.Pairing.SessionCount = 1;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("1 paired sender session", viewModel.SessionCountText);
        Assert.True(viewModel.IsPaired);
    }

    [Fact]
    public void Connection_page_binds_the_new_ux_state()
    {
        var xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Dudu.App", "Pages", "ConnectionPage.xaml"));
        var code = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Dudu.App", "Pages", "ConnectionPage.xaml.cs"));

        Assert.Contains("Visibility=\"{x:Bind ViewModel.HasPendingConfirmation, Mode=OneWay}\" AutomationProperties.AutomationId=\"ConnectionConfirmationPanel\"", xaml);
        Assert.Contains("Command=\"{x:Bind ViewModel.RefreshCommand}\"", xaml);
        Assert.Contains("ViewModel.UpdateCodeExpiry()", code);
        Assert.Contains("ViewModel.PairingCodeText", code);
        Assert.Contains("ViewModel.CodeExpiryText", code);
        // TextBlocks carry a fixed AutomationProperties.Name in XAML; the code-behind must keep
        // it in step with the displayed text or Narrator never reads the code/status.
        Assert.Contains("AutomationProperties.SetName(block, text)", code);
    }

    internal static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DuduDesktop.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
