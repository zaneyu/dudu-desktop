using Dudu.App.Animation;
using Dudu.App.Overlay;
using Dudu.Core.Assets;
using Dudu.Core.Models;

if (!args.Contains("--scenario", StringComparer.Ordinal)
    || Array.IndexOf(args, "layered-window") < 0)
{
    Console.Error.WriteLine("Manual harness only. Run with --scenario layered-window on Windows.");
    return 2;
}

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("The layered-window harness requires Windows x64 and is intentionally manual.");
    return 3;
}

var manifestPath = args.Length > 1 && File.Exists(args[1])
    ? args[1]
    : Path.Combine(Environment.CurrentDirectory, "src", "Dudu.App", "Assets", "Packs", "fallback", "manifest.json");

if (!File.Exists(manifestPath))
{
    Console.Error.WriteLine($"Manifest not found: {manifestPath}");
    return 4;
}

var pack = await AssetManifestLoader.LoadAsync(manifestPath, CancellationToken.None);
using var composer = new SkiaFrameComposer(pack);
var animation = pack.Manifest.Outfits["base"].Animations["idle"];
using var presenter = new LayeredFramePresenter();
using var host = await OverlayWindowHost.CreateAsync(
    presenter,
    new PetPlacement("CURRENT", 0.8, 0.8, 1),
    animation.NominalSize,
    openHome: () => Console.WriteLine("OpenHome"),
    showContextMenu: () => Console.WriteLine("Context menu"));

using var frame = composer.Compose(pack, animation, 0);
await presenter.PresentAsync(frame, CancellationToken.None);
host.Show();
Console.WriteLine("Layered overlay running. Transparent pixels pass through; Enter exits.");
Console.ReadLine();
host.Hide();
return 0;
