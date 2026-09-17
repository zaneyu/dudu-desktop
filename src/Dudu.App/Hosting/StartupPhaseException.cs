namespace Dudu.App.Hosting;

/// <summary>Preserves the startup phase that failed without putting private
/// values into the diagnostic record.</summary>
public sealed class StartupPhaseException : Exception
{
    public StartupPhaseException(string phase, Exception innerException)
        : base($"Dudu startup phase '{phase}' failed.", innerException)
    {
        Phase = string.IsNullOrWhiteSpace(phase) ? "unknown" : phase;
    }

    public string Phase { get; }
}
