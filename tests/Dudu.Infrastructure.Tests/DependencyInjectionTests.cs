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
    [Fact]
    public async Task Infrastructure_registration_resolves_every_repository_once()
    {
        using var fixture = ServiceFixture.Build();
        using var provider = fixture.Provider;

        Assert.IsType<ReminderRepository>(provider.GetRequiredService<IReminderRepository>());
        Assert.IsType<CheckInRepository>(provider.GetRequiredService<ICheckInRepository>());
        Assert.IsType<CountdownRepository>(provider.GetRequiredService<ICountdownRepository>());
        Assert.IsType<FocusSessionRepository>(provider.GetRequiredService<IFocusSessionRepository>());
        Assert.IsType<LocalNoteRepository>(provider.GetRequiredService<ILocalNoteRepository>());
        Assert.IsType<PetPlacementRepository>(provider.GetRequiredService<IPetPlacementRepository>());
        Assert.IsType<PreferencesRepository>(provider.GetRequiredService<IPreferencesRepository>());
        Assert.IsType<ProfileRepository>(provider.GetRequiredService<IProfileRepository>());
        Assert.IsType<RemoteEnvelopeRepository>(provider.GetRequiredService<IRemoteEnvelopeRepository>());
        Assert.IsType<TaskRepository>(provider.GetRequiredService<ITaskRepository>());

        Assert.Same(
            provider.GetRequiredService<IFocusSessionRepository>(),
            provider.GetRequiredService<IFocusSessionRepository>());
        Assert.Same(
            provider.GetRequiredService<FocusSessionRepository>(),
            provider.GetRequiredService<IFocusSessionRepository>());
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
        Assert.Equal(
            PairingAvailability.Offline,
            await provider.GetRequiredService<IPairingService>()
                .GetStateAsync(TestContext.Current.CancellationToken));
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
