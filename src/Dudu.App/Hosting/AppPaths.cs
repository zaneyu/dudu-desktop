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
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DuduDesktop");
        return new AppPaths(
            Root: root,
            Database: Path.Combine(root, "dudu.db"),
            Backups: Path.Combine(root, "backups"),
            Secrets: Path.Combine(root, "secrets"),
            Logs: Path.Combine(root, "logs"));
    }
}
