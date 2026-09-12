using Dudu.App.Animation;
using Dudu.Core.Assets;
using SkiaSharp;
using Xunit;

namespace Dudu.App.Tests.Animation;

public sealed class SkiaFrameComposerTests
{
    [Fact]
    public void Composition_exposes_premultiplied_bgra_with_explicit_dimensions_and_stride()
    {
        using var fixture = ComposerFixture.Create();
        using var frame = fixture.Composer.Compose(
            fixture.Pack,
            fixture.Animation,
            0,
            scale: 2,
            opacity: 0.5f,
            semanticDuration: TimeSpan.FromMilliseconds(100));

        Assert.Equal(2, frame.Width);
        Assert.Equal(2, frame.Height);
        Assert.Equal(8, frame.Stride);
        Assert.Equal(0.5f, frame.Opacity);
        Assert.Equal("fixture/red.png", frame.Source);
        Assert.Equal(frame.Stride * frame.Height, frame.Bytes.Length);
        Assert.Equal(128, frame.Bytes.Span[0]);
        Assert.Equal(128, frame.Bytes.Span[3]);
    }

    [Fact]
    public void Disposed_frame_cannot_expose_reused_bytes()
    {
        using var fixture = ComposerFixture.Create();
        var frame = fixture.Composer.Compose(fixture.Pack, fixture.Animation, 0);

        frame.Dispose();

        Assert.True(frame.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => _ = frame.Bytes);
    }

    [Fact]
    public void Invalid_scale_and_dimensions_are_rejected()
    {
        using var fixture = ComposerFixture.Create();

        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Composer.Compose(fixture.Pack, fixture.Animation, 0, scale: double.NaN));
        var invalid = new AssetAnimation
        {
            Frames = fixture.Animation.Frames,
            Loop = fixture.Animation.Loop,
            NominalSize = new PixelSize(0, 1),
            Anchor = fixture.Animation.Anchor,
            ReducedMotion = fixture.Animation.ReducedMotion,
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Composer.Compose(fixture.Pack, invalid, 0));
    }

    [Fact]
    public async Task Repeated_cached_frames_allocate_less_than_fifty_kib()
    {
        using var fixture = ComposerFixture.Create();
        using (fixture.Composer.Compose(fixture.Pack, fixture.Animation, 0))
        {
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 300; index++)
        {
            using var frame = fixture.Composer.Compose(fixture.Pack, fixture.Animation, 0);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 50 * 1024);
        await Task.CompletedTask;
    }

    private sealed class ComposerFixture : IDisposable
    {
        private ComposerFixture(string root, AssetPack pack, AssetAnimation animation, SkiaFrameComposer composer)
        {
            Root = root;
            Pack = pack;
            Animation = animation;
            Composer = composer;
        }

        private string Root { get; }

        public AssetPack Pack { get; }

        public AssetAnimation Animation { get; }

        public SkiaFrameComposer Composer { get; }

        public static ComposerFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "dudu-composer-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            using var bitmap = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul));
            bitmap.SetPixel(0, 0, SKColors.Red);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(root, "red.png"), encoded.ToArray());

            var animation = new AssetAnimation
            {
                Frames = [new AssetFrame { File = "red.png", DurationMs = 100 }],
                Loop = "once",
                NominalSize = new PixelSize(1, 1),
                Anchor = new PixelPoint(0, 0),
                ReducedMotion = "red.png",
            };
            var manifest = new AssetManifest
            {
                SchemaVersion = 1,
                PackId = "fixture",
                Version = "1",
                PrivateUseOnly = true,
                Attribution = new AssetAttribution { Creator = "test" },
                Outfits = new Dictionary<string, AssetOutfit>(StringComparer.Ordinal)
                {
                    ["base"] = new AssetOutfit { Animations = new Dictionary<string, AssetAnimation> { ["idle"] = animation } },
                },
            };
            var pack = new AssetPack(Path.Combine(root, "manifest.json"), manifest);
            return new ComposerFixture(root, pack, animation, new SkiaFrameComposer(pack));
        }

        public void Dispose()
        {
            Composer.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
