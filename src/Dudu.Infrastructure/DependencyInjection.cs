using System.Security.Cryptography;
using Dudu.Core.Abstractions;
using Dudu.Core.CheckIns;
using Dudu.Core.Focus;
using Dudu.Core.Models;
using Dudu.Core.Notes;
using Dudu.Core.Pet;
using Dudu.Core.Reminders;
using Dudu.Core.Tasks;
using Dudu.Core.Time;
using Dudu.Infrastructure.Crypto;
using Dudu.Infrastructure.Data;
using Dudu.Infrastructure.Data.Repositories;
using Dudu.Infrastructure.Remote;
using Dudu.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Dudu.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddDuduInfrastructure(
        this IServiceCollection services,
        DatabaseOptions options,
        RelayOptions? relayOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.AddSingleton<Database>();
        services.AddSingleton<DatabaseBackupService>();
        services.AddSingleton<LocalDataMaintenanceService>();

        RegisterRepository<CheckInRepository, ICheckInRepository>(services);
        RegisterRepository<CountdownRepository, ICountdownRepository>(services);
        RegisterRepository<FocusSessionRepository, IFocusSessionRepository>(services);
        RegisterRepository<LocalNoteRepository, ILocalNoteRepository>(services);
        RegisterRepository<PetPlacementRepository, IPetPlacementRepository>(services);
        RegisterRepository<PreferencesRepository, IPreferencesRepository>(services);
        RegisterRepository<ProfileRepository, IProfileRepository>(services);
        RegisterRepository<RemoteEnvelopeRepository, IRemoteEnvelopeRepository>(services);
        RegisterRepository<ReminderRepository, IReminderRepository>(services);
        services.AddSingleton<IReminderWriter>(static provider =>
            provider.GetRequiredService<ReminderRepository>());
        RegisterRepository<TaskRepository, ITaskRepository>(services);

        services.AddSingleton<AppUnitOfWork>();
        services.AddSingleton<IAppUnitOfWork>(static provider =>
            provider.GetRequiredService<AppUnitOfWork>());
        services.AddSingleton<CompanionFeatureTransactionService>();
        services.AddSingleton<ICompanionFeatureTransactions>(static provider =>
            provider.GetRequiredService<CompanionFeatureTransactionService>());
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IRandomSource, CryptographicRandomSource>();
        services.AddSingleton<Preferences>(static _ => DefaultPreferences());
        services.AddSingleton<IReminderDueSink, NullReminderDueSink>();

        services.AddSingleton<CheckInService>();
        services.AddSingleton<FocusService>();
        services.AddSingleton<TaskService>();
        services.AddSingleton<ReminderEngine>();
        services.AddSingleton<LocalNoteSelector>();
        services.AddSingleton<AmbientScheduler>();
        services.AddSingleton(static _ => PetStateMachine.CreateIdle());

        services.AddSingleton<ISecretStore>(static provider =>
        {
            var options = provider.GetRequiredService<DatabaseOptions>();
            var databaseDirectory = Path.GetDirectoryName(options.DatabasePath)
                ?? throw new InvalidOperationException("The database path has no directory.");
            return new DpapiSecretStore(Path.Combine(databaseDirectory, "secrets"));
        });
        services.AddSingleton<DesktopKeyService>();
        services.AddSingleton<IRemoteNoteArrivalSink, NullRemoteNoteArrivalSink>();

        if (relayOptions?.BaseUrl is not null)
        {
            services.AddSingleton(relayOptions);
            services.AddSingleton(static _ => new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15),
                MaxResponseContentBufferSize = 256 * 1024,
            });
            services.AddSingleton<IRelayClient, RelayClient>();
            services.AddSingleton<PollBackoff>();
            services.AddSingleton<RemoteSyncService>();
            services.AddSingleton<IPairingService, RelayPairingService>();
        }
        else
        {
            services.AddSingleton<IPairingService, OfflinePairingService>();
        }

        return services;
    }

    private static void RegisterRepository<TImplementation, TInterface>(
        IServiceCollection services)
        where TImplementation : class, TInterface
        where TInterface : class
    {
        services.AddSingleton<TImplementation>();
        services.AddSingleton<TInterface>(static provider =>
            provider.GetRequiredService<TImplementation>());
    }

    private static Preferences DefaultPreferences() => new(
        AppTheme.System,
        new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
        ReducedMotion: false,
        LocalNoteDailyLimit: 3,
        LaunchAtSignIn: true,
        AlwaysOnTop: false,
        HidePetDuringFullscreen: true,
        AmbientMinimumInterval: TimeSpan.FromMinutes(15));

    private sealed class CryptographicRandomSource : IRandomSource
    {
        public int Next(int exclusiveMax)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exclusiveMax);
            return RandomNumberGenerator.GetInt32(exclusiveMax);
        }
    }

    private sealed class NullReminderDueSink : IReminderDueSink
    {
        public Task NotifyAsync(
            ReminderOccurrence occurrence,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(occurrence);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class NullRemoteNoteArrivalSink : IRemoteNoteArrivalSink
    {
        public Task NotifyAsync(Guid messageId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
