using Dudu.Core.Models;

namespace Dudu.App.Hosting;

public sealed class RuntimePreferencesState(Preferences initial)
{
    private Preferences _current = initial ?? throw new ArgumentNullException(nameof(initial));

    public Preferences Current => Volatile.Read(ref _current);

    public void Set(Preferences preferences) =>
        Volatile.Write(ref _current, preferences ?? throw new ArgumentNullException(nameof(preferences)));
}
