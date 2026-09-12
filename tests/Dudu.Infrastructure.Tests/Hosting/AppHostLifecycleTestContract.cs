namespace Dudu.App.Hosting;

// AppHost is compiled into this otherwise Infrastructure-only test project.
// The production lifecycle interface lives beside its WinUI dependencies, so
// this test-only contract keeps the host's portable behavior independently testable.
public interface IAppHostLifecycle
{
    Task ResumeAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
