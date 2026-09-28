using Dudu.App.ViewModels;
using Xunit;

namespace Dudu.App.Tests.ViewModels;

/// <summary>"Delete my data" on the Settings page: with a relay configured the sync loop
/// is stopped, the remote device is deleted first, and only then is this pc wiped; a
/// remote failure wipes nothing, restarts the loop and offers "wipe this pc only";
/// offline builds and safe mode wipe this pc only without an error.</summary>
public sealed class DeleteMyDataTests
{
    private const string WipedMessage = "all cleaned up -- restart dudu to start fresh";

    [Fact]
    public async Task Relay_configured_success_stops_sync_then_remote_then_local()
    {
        var (vm, calls) = DeleteMyDataHarness.Create(remoteDeleteAvailable: true, remoteThrows: false);

        await vm.DeleteMyDataAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["stop", "remote", "local"], calls);
        Assert.Equal(WipedMessage, vm.StatusMessage);
        Assert.False(vm.HasError);
        Assert.False(vm.IsWipeThisPcOnlyVisible);
        Assert.False(vm.IsDeleteConfirmVisible);
    }

    [Fact]
    public async Task Local_failure_is_reported_not_thrown()
    {
        var (vm, _) = DeleteMyDataHarness.Create(remoteDeleteAvailable: false, remoteThrows: false, localThrows: true);

        await vm.DeleteMyDataAsync(TestContext.Current.CancellationToken);   // must not throw

        Assert.True(vm.HasError);
        Assert.False(vm.HasStatus);
    }

    [Fact]
    public async Task Remote_failure_wipes_nothing_restarts_sync_and_offers_wipe_this_pc_only()
    {
        var (vm, calls) = DeleteMyDataHarness.Create(remoteDeleteAvailable: true, remoteThrows: true);

        await vm.DeleteMyDataAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["stop", "remote", "start"], calls);
        Assert.DoesNotContain("local", calls);
        Assert.StartsWith("couldn't reach the partner server", vm.ErrorMessage);
        // The relay's own error text never reaches the screen.
        Assert.DoesNotContain(DeleteMyDataHarness.RelayDetail, vm.ErrorMessage, StringComparison.Ordinal);
        Assert.False(vm.HasStatus);
        Assert.True(vm.IsWipeThisPcOnlyVisible);
        Assert.True(vm.WipeThisPcOnlyCommand.CanExecute(null));

        calls.Clear();
        await vm.WipeThisPcOnlyAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["local"], calls);
        Assert.Equal(WipedMessage, vm.StatusMessage);
        Assert.False(vm.HasError);
        Assert.False(vm.IsWipeThisPcOnlyVisible);
    }

    [Fact]
    public async Task Remote_cancellation_still_restarts_sync_and_wipes_nothing()
    {
        var (vm, calls) = DeleteMyDataHarness.Create(
            remoteDeleteAvailable: true,
            remoteThrows: false,
            remoteCancels: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => vm.DeleteMyDataAsync(TestContext.Current.CancellationToken));

        Assert.Equal(["stop", "remote", "start"], calls);
    }

    [Fact]
    public async Task No_relay_offline_or_safe_mode_wipes_local_only_without_error()
    {
        var (vm, calls) = DeleteMyDataHarness.Create(remoteDeleteAvailable: false, remoteThrows: true);

        await vm.DeleteMyDataAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["local"], calls);
        Assert.Equal(WipedMessage, vm.StatusMessage);
        Assert.False(vm.HasError);
        Assert.False(vm.IsWipeThisPcOnlyVisible);
    }

    [Fact]
    public void Confirm_panel_opens_on_request_and_closes_on_cancel_without_deleting()
    {
        var (vm, calls) = DeleteMyDataHarness.Create(remoteDeleteAvailable: true, remoteThrows: false);

        Assert.False(vm.IsDeleteConfirmVisible);
        vm.RequestDeleteMyDataCommand.Execute(null);
        Assert.True(vm.IsDeleteConfirmVisible);

        vm.CancelDeleteMyDataCommand.Execute(null);

        Assert.False(vm.IsDeleteConfirmVisible);
        Assert.Empty(calls);
    }

    private static class DeleteMyDataHarness
    {
        public const string RelayDetail = "relay said 503 at some-host";

        public static (SettingsViewModel ViewModel, List<string> Calls) Create(
            bool remoteDeleteAvailable,
            bool remoteThrows,
            bool localThrows = false,
            bool remoteCancels = false)
        {
            var calls = new List<string>();
            var fixture = SettingsDataPagesFixture.Create(
                deleteLocalDataAsync: _ =>
                {
                    calls.Add("local");
                    return localThrows
                        ? Task.FromException(new IOException("disk said no"))
                        : Task.CompletedTask;
                },
                deleteRemoteDataAsync: _ =>
                {
                    calls.Add("remote");
                    if (remoteCancels) return Task.FromCanceled(new CancellationToken(canceled: true));
                    // What the production delegate throws when the relay delete did not complete.
                    return remoteThrows
                        ? Task.FromException(new NotSupportedException(RelayDetail))
                        : Task.CompletedTask;
                },
                stopRemoteSyncAsync: _ =>
                {
                    calls.Add("stop");
                    return Task.CompletedTask;
                },
                startRemoteSyncAsync: _ =>
                {
                    calls.Add("start");
                    return Task.CompletedTask;
                },
                remoteDeleteAvailable: remoteDeleteAvailable);
            return (new SettingsViewModel(fixture.Context), calls);
        }
    }
}
