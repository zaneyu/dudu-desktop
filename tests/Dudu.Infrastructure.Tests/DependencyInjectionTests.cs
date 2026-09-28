using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Infrastructure;
using Dudu.Infrastructure.Data;
using Dudu.Infrastructure.Data.Repositories;
using Dudu.Core.Pet;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dudu.Infrastructure.Tests;

public sealed class DependencyInjectionTests
{
    [Theory]
    [InlineData("Dudu.Core.Reminders.ReminderEngine")]
    [InlineData("Dudu.Core.Reminders.ReminderScheduler")]
    [InlineData("Dudu.Core.Reminders.LocalReminderDefaults")]
    [InlineData("Dudu.Core.Abstractions.IReminderRepository")]
    [InlineData("Dudu.Core.Models.Reminder")]
    public void Removed_reminder_types_no_longer_exist(string typeName)
    {
        Assert.Null(typeof(Preferences).Assembly.GetType(typeName));
    }

    [Fact]
    public void Removed_reminder_repository_no_longer_exists()
    {
        Assert.Null(typeof(Database).Assembly.GetType("Dudu.Infrastructure.Data.Repositories.ReminderRepository"));
    }

    [Theory]
    [InlineData("Dudu.Infrastructure.Data.Repositories.TaskRepository")]
    [InlineData("Dudu.Infrastructure.Data.Repositories.FocusSessionRepository")]
    public void Removed_task_and_focus_repositories_no_longer_exist(string typeName)
    {
        Assert.Null(typeof(Database).Assembly.GetType(typeName));
    }

    [Theory]
    [InlineData("Dudu.Core.Countdowns.CountdownService")]
    [InlineData("Dudu.Core.CheckIns.CheckInService")]
    [InlineData("Dudu.Core.Reminders.ReminderEngine")]
    [InlineData("Dudu.Core.Focus.FocusService")]
    [InlineData("Dudu.Core.Tasks.TaskService")]
    [InlineData("Dudu.Core.Notes.LocalNoteSelector")]
    [InlineData("Dudu.Core.Models.Countdown")]
    [InlineData("Dudu.Core.Models.MoodCheckIn")]
    [InlineData("Dudu.Core.Abstractions.ICountdownRepository")]
    [InlineData("Dudu.Core.Abstractions.ICheckInRepository")]
    public void Removed_feature_types_no_longer_exist(string typeName)
    {
        Assert.Null(typeof(Dudu.Core.Models.Preferences).Assembly.GetType(typeName));
    }

    [Theory]
    [InlineData("Dudu.Infrastructure.Data.Repositories.CountdownRepository")]
    [InlineData("Dudu.Infrastructure.Data.Repositories.CheckInRepository")]
    public void Removed_countdown_and_check_in_repositories_no_longer_exist(string typeName)
    {
        Assert.Null(typeof(Database).Assembly.GetType(typeName));
    }

    [Fact]
    public async Task Infrastructure_registration_resolves_every_repository_once()
    {
        using var fixture = ServiceFixture.Build();
        using var provider = fixture.Provider;

        Assert.IsType<HeldPresentationRepository>(provider.GetRequiredService<IHeldPresentationRepository>());
        Assert.IsType<LocalNoteRepository>(provider.GetRequiredService<ILocalNoteRepository>());
        Assert.IsType<PetPlacementRepository>(provider.GetRequiredService<IPetPlacementRepository>());
        Assert.IsType<PreferencesRepository>(provider.GetRequiredService<IPreferencesRepository>());
        Assert.IsType<ProfileRepository>(provider.GetRequiredService<IProfileRepository>());
        Assert.IsType<RemoteEnvelopeRepository>(provider.GetRequiredService<IRemoteEnvelopeRepository>());
        Assert.IsType<CompanionFeatureTransactionService>(
            provider.GetRequiredService<ICompanionFeatureTransactions>());
        Assert.Same(
            provider.GetRequiredService<DatabaseBackupService>(),
            provider.GetRequiredService<DatabaseBackupService>());
        Assert.Same(
            provider.GetRequiredService<LocalDataMaintenanceService>(),
            provider.GetRequiredService<LocalDataMaintenanceService>());

        Assert.Same(
            provider.GetRequiredService<ILocalNoteRepository>(),
            provider.GetRequiredService<ILocalNoteRepository>());
        Assert.Same(
            provider.GetRequiredService<LocalNoteRepository>(),
            provider.GetRequiredService<ILocalNoteRepository>());
        Assert.Same(
            provider.GetRequiredService<Database>(),
            provider.GetRequiredService<Database>());
        Assert.Same(
            provider.GetRequiredService<ISecretStore>(),
            provider.GetRequiredService<ISecretStore>());
        Assert.Same(
            provider.GetRequiredService<PetStateMachine>(),
            provider.GetRequiredService<PetStateMachine>());
        Assert.Equal(
            PetState.Idle,
            provider.GetRequiredService<PetStateMachine>().Current.State);
        var pairing = provider.GetRequiredService<IPairingService>();
        Assert.Equal(
            PairingAvailability.Offline,
            await pairing.GetStateAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await pairing.GetSessionCountAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await pairing.ListSessionsAsync(TestContext.Current.CancellationToken));
        await pairing.RevokeSessionAsync("opaque-session-handle", TestContext.Current.CancellationToken);
    }

    private sealed class ServiceFixture : IDisposable
    {
        private ServiceFixture(ServiceProvider provider, string root)
        {
            Provider = provider;
            Root = root;
        }

        public ServiceProvider Provider { get; }

        private string Root { get; }

        public static ServiceFixture Build()
        {
            var root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "dudu-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var options = new DatabaseOptions(
                System.IO.Path.Combine(root, "dudu.db"),
                System.IO.Path.Combine(root, "backups"));
            var provider = new ServiceCollection()
                .AddDuduInfrastructure(options)
                .BuildServiceProvider(new ServiceProviderOptions
                {
                    ValidateScopes = true,
                    ValidateOnBuild = true,
                });
            return new ServiceFixture(provider, root);
        }

        public void Dispose()
        {
            Provider.Dispose();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
