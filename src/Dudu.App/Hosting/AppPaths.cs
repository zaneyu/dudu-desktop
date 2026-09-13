namespace Dudu.App.Hosting;

public sealed record AppPaths(
    string Root,
    string Database,
    string Backups,
    string Secrets,
    string Logs)
{
    public static AppPaths ForCurrentUser()
    {
        var requestedRoot = Environment.GetEnvironmentVariable("DUDU_DATA_ROOT");
        var root = string.IsNullOrWhiteSpace(requestedRoot)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DuduDesktop")
            : requestedRoot;
        return ForRoot(root);
    }

    public static AppPaths ForRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        root = Path.GetFullPath(root);
        return new AppPaths(
            Root: root,
            Database: Path.Combine(root, "dudu.db"),
            Backups: Path.Combine(root, "backups"),
            Secrets: Path.Combine(root, "secrets"),
            Logs: Path.Combine(root, "logs"));
    }
}
