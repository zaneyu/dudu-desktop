using System.IO.Pipes;
using Dudu.App.Hosting;
using Xunit;

namespace Dudu.App.Tests.Hosting;

/// <summary>
/// A second launch used to open Home behind her other windows: the primary is a
/// background process and nobody handed it the foreground right. The secondary
/// now grants it to the pipe's server process before sending "open Home".
/// </summary>
public sealed class ForegroundHandoffTests
{
    [Fact]
    public async Task A_write_only_activation_client_can_name_the_primary_process()
    {
        // Same shape as WindowsActivationTransport: an In server, an Out client.
        var pipeName = "DuduDesktop.ForegroundHandoffTest." + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);
        var accept = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        await client.ConnectAsync(TestContext.Current.CancellationToken);
        await accept;

        Assert.Equal((uint)Environment.ProcessId, ForegroundHandoff.TryGetServerProcessId(client));

        // The grant itself depends on whether this test host may take the
        // foreground; it must never throw either way.
        _ = ForegroundHandoff.TryAllowPipeServer(client);
    }

    [Fact]
    public void The_secondary_grants_the_foreground_before_it_sends_the_request()
    {
        var transport = ReadSource("Hosting", "SingleInstanceCoordinator.cs");
        var send = Slice(transport, "public async Task SendAsync(", "catch (OperationCanceledException)");

        var connect = send.IndexOf("await client.ConnectAsync(", StringComparison.Ordinal);
        var grant = send.IndexOf("ForegroundHandoff.TryAllowPipeServer(client);", StringComparison.Ordinal);
        var write = send.IndexOf("await client.WriteAsync(", StringComparison.Ordinal);
        Assert.True(connect >= 0 && grant > connect && write > grant, "grant must sit between connect and write");
    }

    [Fact]
    public void Opening_settings_brings_the_window_to_the_front_after_activating_it()
    {
        var app = ReadSource("App.xaml.cs");
        var open = Slice(app, "private void OpenSettingsCore(", "\n    }\n");

        var activate = open.IndexOf("_settingsWindow.Activate();", StringComparison.Ordinal);
        var front = open.IndexOf("BringToFront(_settingsWindow);", StringComparison.Ordinal);
        Assert.True(activate >= 0 && front > activate, "BringToFront must follow Activate");
        Assert.Contains("TrySetForegroundWindow(", Slice(app, "private static void BringToFront(", "\n    }\n"));
    }

    private static string ReadSource(params string[] relative) =>
        File.ReadAllText(Path.Combine(
                Path.Combine(FindRepositoryRoot(), "src", "Dudu.App"),
                Path.Combine(relative)))
            .Replace("\r\n", "\n");

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{startMarker}' not found.");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"'{endMarker}' not found after '{startMarker}'.");
        return source[start..end];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PRODUCT.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
