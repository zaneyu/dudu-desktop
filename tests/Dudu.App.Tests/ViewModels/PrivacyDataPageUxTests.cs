using Dudu.App.ViewModels;
using Xunit;

namespace Dudu.App.Tests.ViewModels;

/// <summary>UI/UX regressions on the Privacy &amp; Data page's view model.</summary>
public sealed class PrivacyDataPageUxTests
{
    [Fact]
    public void Confirmation_panel_is_hidden_until_an_action_is_requested()
    {
        var fixture = SettingsDataPagesFixture.Create();
        var viewModel = new PrivacyDataViewModel(fixture.Context);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.False(viewModel.HasPendingConfirmation);
        Assert.False(viewModel.CancelConfirmationCommand.CanExecute(null));

        viewModel.RequestDeleteLocalDataCommand.Execute(null);

        Assert.True(viewModel.HasPendingConfirmation);
        Assert.Contains(nameof(PrivacyDataViewModel.HasPendingConfirmation), changed);
        Assert.True(viewModel.CancelConfirmationCommand.CanExecute(null));
    }

    [Fact]
    public void Returning_to_the_page_disarms_a_confirmation_left_pending_on_an_earlier_visit()
    {
        var calls = 0;
        var fixture = SettingsDataPagesFixture.Create(deleteLocalDataAsync: _ =>
        {
            calls++;
            return Task.CompletedTask;
        });
        var viewModel = new PrivacyDataViewModel(fixture.Context);

        viewModel.RequestDeleteLocalDataCommand.Execute(null);
        Assert.True(viewModel.ConfirmCommand.CanExecute(null));

        viewModel.ResetTransientState(); // what the page runs on every Loaded

        Assert.Equal(PrivacyConfirmationAction.None, viewModel.PendingConfirmation);
        Assert.False(viewModel.HasPendingConfirmation);
        Assert.False(viewModel.ConfirmCommand.CanExecute(null));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Returning_to_the_page_clears_an_old_visits_result_lines()
    {
        var fixture = SettingsDataPagesFixture.Create(backupAsync: _ => Task.CompletedTask);
        var viewModel = new PrivacyDataViewModel(fixture.Context);
        await viewModel.BackupAsync(TestContext.Current.CancellationToken);
        Assert.True(viewModel.HasStatus);

        viewModel.ResetTransientState();

        Assert.False(viewModel.HasStatus);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task Other_data_actions_are_disabled_while_a_backup_is_running()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleted = 0;
        var fixture = SettingsDataPagesFixture.Create(
            backupAsync: _ => gate.Task,
            deleteLocalDataAsync: _ =>
            {
                deleted++;
                return Task.CompletedTask;
            });
        var viewModel = new PrivacyDataViewModel(fixture.Context);

        var backup = viewModel.BackupCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.RequestDeleteLocalDataCommand.CanExecute(null));
        Assert.False(viewModel.RequestRestoreCommand.CanExecute(null));
        Assert.False(viewModel.RequestDeleteRemoteDataCommand.CanExecute(null));
        Assert.False(viewModel.ConfirmCommand.CanExecute(null));

        gate.SetResult();
        await backup;

        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.RequestDeleteLocalDataCommand.CanExecute(null));
        Assert.Equal(0, deleted);
    }

    [Fact]
    public async Task A_failed_action_clears_the_previous_success_line()
    {
        var fixture = SettingsDataPagesFixture.Create(
            backupAsync: _ => Task.CompletedTask,
            restoreAsync: _ => Task.FromException(new InvalidOperationException("No local backup is available to restore.")));
        var viewModel = new PrivacyDataViewModel(fixture.Context);
        await viewModel.BackupAsync(TestContext.Current.CancellationToken);
        Assert.Equal("oki backup created on this pc", viewModel.StatusMessage);

        viewModel.RequestRestoreCommand.Execute(null);
        await viewModel.ConfirmAsync(TestContext.Current.CancellationToken);

        Assert.Equal("No local backup is available to restore.", viewModel.ErrorMessage);
        Assert.Null(viewModel.StatusMessage);
        Assert.False(viewModel.HasStatus);
    }

    [Fact]
    public async Task Restore_copy_does_not_promise_an_automatic_restart()
    {
        var fixture = SettingsDataPagesFixture.Create(restoreAsync: _ => Task.CompletedTask);
        var viewModel = new PrivacyDataViewModel(fixture.Context);

        viewModel.RequestRestoreCommand.Execute(null);
        Assert.DoesNotContain("restarts", viewModel.ConfirmationMessage);
        Assert.Contains("latest backup", viewModel.ConfirmationMessage);

        await viewModel.ConfirmAsync(TestContext.Current.CancellationToken);
        Assert.Equal("okkk backup restored restart dudu if something looks off", viewModel.StatusMessage);
    }

    [Fact]
    public void Privacy_page_binds_the_new_ux_state()
    {
        var root = ConnectionPageUxTests.FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "PrivacyDataPage.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "Pages", "PrivacyDataPage.xaml.cs"));

        Assert.Contains("Visibility=\"{x:Bind ViewModel.HasPendingConfirmation, Mode=OneWay}\" AutomationProperties.AutomationId=\"PrivacyConfirmationPanel\"", xaml);
        Assert.Contains("IsActive=\"{x:Bind ViewModel.IsBusy, Mode=OneWay}\"", xaml);
        Assert.Contains("ViewModel.ResetTransientState()", code);
    }
}
