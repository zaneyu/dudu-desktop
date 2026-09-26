using Dudu.App.Animation;
using Dudu.Core.Assets;
using Dudu.Core.Time;
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
        Assert.Equal(0, frame.Bytes.Span[0]);
        Assert.Equal(0, frame.Bytes.Span[1]);
        Assert.InRange(frame.Bytes.Span[2], 127, 128);
        Assert.InRange(frame.Bytes.Span[3], 127, 128);

        using var fullOpacity = fixture.Composer.Compose(fixture.Pack, fixture.Animation, 0);
        Assert.Equal(0, fullOpacity.Bytes.Span[0]);
        Assert.Equal(0, fullOpacity.Bytes.Span[1]);
        Assert.Equal(255, fullOpacity.Bytes.Span[2]);
        Assert.Equal(255, fullOpacity.Bytes.Span[3]);
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
    public void Invalid_frame_input_does_not_leak_a_rented_buffer()
    {
        using var fixture = ComposerFixture.Create();

        var invalid = new AssetFrame { File = "../unsafe.png", DurationMs = 100 };
        Assert.Throws<AssetManifestException>(() => fixture.Composer.Compose(
            fixture.Pack,
            fixture.Animation,
            invalid));
    }

    // Regression for the decoded-frame cache having no eviction: 8 distinct
    // single-frame animations at 16 MiB decoded each (128 MiB combined) is
    // well past the composer's 64 MiB cap, so composing every animation in
    // order only succeeds if the cache evicts older entries instead of
    // throwing once the cap is hit. Each animation stays comfortably under
    // the cap on its own (16 MiB), which matters because pack admission
    // (ValidatePackLimits) now rejects on a single animation's own decoded
    // working set, not the pack-wide total -- so this has to be modeled as
    // several small animations rather than one 128 MiB one.
    [Fact]
    public void Frames_still_compose_after_decoded_cache_exceeds_its_byte_cap()
    {
        using var fixture = ComposerFixture.CreateWithDistinctSizedAnimations(animationCount: 8, dimension: 2048);

        for (var i = 0; i < fixture.Animations.Count; i++)
        {
            using var frame = fixture.Composer.Compose(fixture.Pack, fixture.Animations[i], 0);
            var expectedGray = ExpectedGray(i);
            Assert.Equal(expectedGray, frame.Bytes.Span[0]);
            Assert.Equal(expectedGray, frame.Bytes.Span[1]);
            Assert.Equal(expectedGray, frame.Bytes.Span[2]);
            Assert.Equal(255, frame.Bytes.Span[3]);
        }

        // The first animation's frame is long evicted by now: re-requesting
        // it must re-decode from disk rather than throw or hand back
        // stale/disposed bytes from the evicted (and disposed) SKImage.
        using var revisited = fixture.Composer.Compose(fixture.Pack, fixture.Animations[0], 0);
        var firstGray = ExpectedGray(0);
        Assert.Equal(firstGray, revisited.Bytes.Span[0]);
        Assert.Equal(firstGray, revisited.Bytes.Span[1]);
        Assert.Equal(firstGray, revisited.Bytes.Span[2]);
        Assert.Equal(255, revisited.Bytes.Span[3]);
    }

    // Proves the decoded-frame cache evicts least-recently-used entries, not
    // first-in-first-out ones. Each of the 4 animations' frames decodes to
    // ~25 MiB, so only 2 can be resident under the composer's 64 MiB cap at
    // once. The access pattern 0,1,0,2,0,3 keeps touching animation 0, so
    // under LRU it must never be the eviction victim -- animations 1 and 2
    // are evicted in its place. To prove animation 0 is never evicted (and
    // never silently re-decoded), its source file is deleted from disk right
    // after it is first cached: if the composer ever evicted and then
    // re-requested it, the re-decode would fail loudly instead of the test
    // passing by coincidence.
    [Fact]
    public void Cache_eviction_prefers_least_recently_used_entries_over_first_in_first_out()
    {
        using var fixture = ComposerFixture.CreateWithDistinctSizedAnimations(animationCount: 4, dimension: 2560);

        ComposeAndAssert(fixture, 0);
        ComposeAndAssert(fixture, 1);
        ComposeAndAssert(fixture, 0);

        File.Delete(fixture.FramePaths[0]);

        ComposeAndAssert(fixture, 2);
        ComposeAndAssert(fixture, 0);
        ComposeAndAssert(fixture, 3);
        ComposeAndAssert(fixture, 0);
    }

    private static void ComposeAndAssert(ComposerFixture fixture, int animationIndex)
    {
        using var frame = fixture.Composer.Compose(fixture.Pack, fixture.Animations[animationIndex], 0);
        var expectedGray = ExpectedGray(animationIndex);
        Assert.Equal(expectedGray, frame.Bytes.Span[0]);
        Assert.Equal(expectedGray, frame.Bytes.Span[1]);
        Assert.Equal(expectedGray, frame.Bytes.Span[2]);
        Assert.Equal(255, frame.Bytes.Span[3]);
    }

    [Fact]
    public void Partner_clock_pill_is_painted_at_the_top_centre_and_leaves_the_rest_of_dudu_alone()
    {
        using var fixture = ComposerFixture.Create();
        fixture.Composer.SetPartnerClock(new PartnerClock(new FixedClock(
            new DateTimeOffset(2026, 7, 1, 13, 5, 0, TimeSpan.Zero))));

        using var frame = fixture.Composer.Compose(fixture.Pack, fixture.Animation, 0, scale: 200);

        Assert.False(IsPureRed(frame, frame.Width / 2, 14), "the UK pill should cover the top centre");
        Assert.True(IsPureRed(frame, frame.Width / 2, frame.Height - 10));
        Assert.True(IsPureRed(frame, 1, 1), "the pill is centred, never stretched to the edges");
    }

    [Fact]
    public void Partner_clock_pill_stays_click_through_so_it_never_becomes_a_drag_handle()
    {
        using var fixture = ComposerFixture.Create();
        fixture.Composer.SetPartnerClock(new PartnerClock(new FixedClock(
            new DateTimeOffset(2026, 7, 1, 13, 5, 0, TimeSpan.Zero))));

        using var frame = fixture.Composer.Compose(fixture.Pack, fixture.Animation, 0, scale: 200);
        var region = Assert.IsType<Dudu.App.Overlay.PixelRect>(frame.ClickThroughRegion);
        Assert.True(region.Contains(frame.Width / 2, 14));
        Assert.False(region.Contains(frame.Width / 2, frame.Height - 10));

        var hitCopy = frame.Bytes.ToArray();
        Dudu.App.Overlay.OverlayHitTest.ClearAlpha(hitCopy, frame.Width, frame.Height, frame.Stride, region);
        Assert.False(Dudu.App.Overlay.OverlayHitTest.IsInteractive(
            hitCopy, frame.Width, frame.Height, frame.Stride, frame.Width, frame.Height, frame.Width / 2, 14));
        Assert.True(Dudu.App.Overlay.OverlayHitTest.IsInteractive(
            hitCopy, frame.Width, frame.Height, frame.Stride, frame.Width, frame.Height, frame.Width / 2, frame.Height - 10));
        Assert.False(IsPureRed(frame, frame.Width / 2, 14), "the presented pixels keep the pill");

        fixture.Composer.SetPartnerClock(null);
        using var cleared = fixture.Composer.Compose(fixture.Pack, fixture.Animation, 0, scale: 200);
        Assert.Null(cleared.ClickThroughRegion);
    }

    [Fact]
    public void Partner_clock_pill_is_skipped_on_a_tiny_canvas_and_when_cleared()
    {
        using var fixture = ComposerFixture.Create();
        fixture.Composer.SetPartnerClock(new PartnerClock(new FixedClock(
            new DateTimeOffset(2026, 1, 15, 22, 30, 0, TimeSpan.Zero))));

        using (var tiny = fixture.Composer.Compose(fixture.Pack, fixture.Animation, 0, scale: 50))
        {
            Assert.True(IsPureRed(tiny, tiny.Width / 2, 6));
        }

        fixture.Composer.SetPartnerClock(null);
        using var cleared = fixture.Composer.Compose(fixture.Pack, fixture.Animation, 0, scale: 200);
        Assert.True(IsPureRed(cleared, cleared.Width / 2, 14));
    }

    [Fact]
    public void Partner_clock_pill_renderer_never_returns_a_clipped_pill()
    {
        var palette = OverlaySurfacePalette.For(Dudu.Core.Models.AppTheme.Dark, highContrast: true);
        foreach (var size in new[] { 96, 120, 200, 400, 1024 })
        {
            using var pill = PartnerClockPillRenderer.Render("UK 14:05", isNight: size % 2 == 0, palette, size, size);
            if (pill is null) continue;
            Assert.InRange(pill.X, 0, size - pill.Image.Width);
            Assert.InRange(pill.Y, 0, size - pill.Image.Height);
        }

        Assert.Null(PartnerClockPillRenderer.Render("UK 14:05", false, palette, 95, 400));
    }

    private static bool IsPureRed(RenderedFrame frame, int x, int y)
    {
        var offset = y * frame.Stride + x * 4;
        var pixel = frame.Bytes.Span.Slice(offset, 4);
        return pixel[0] == 0 && pixel[1] == 0 && pixel[2] == 255 && pixel[3] == 255;
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;

        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static byte ExpectedGray(int frameIndex) => (byte)(20 + frameIndex * 25);

    private sealed class ComposerFixture : IDisposable
    {
        private ComposerFixture(
            string root,
            AssetPack pack,
            AssetAnimation animation,
            SkiaFrameComposer composer,
            IReadOnlyList<AssetAnimation>? animations = null,
            IReadOnlyList<string>? framePaths = null)
        {
            Root = root;
            Pack = pack;
            Animation = animation;
            Composer = composer;
            Animations = animations ?? [animation];
            FramePaths = framePaths ?? [];
        }

        private string Root { get; }

        public AssetPack Pack { get; }

        public AssetAnimation Animation { get; }

        public IReadOnlyList<AssetAnimation> Animations { get; }

        public IReadOnlyList<string> FramePaths { get; }

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

        /// <summary>Builds a pack of <paramref name="animationCount"/> distinct
        /// single-frame animations, each a solid-color PNG that decodes far
        /// larger than it encodes, so each animation's own decoded size stays
        /// small enough to pass admission while their combined size blows past
        /// the composer's byte cap once several are composed in sequence.
        /// </summary>
        public static ComposerFixture CreateWithDistinctSizedAnimations(int animationCount, int dimension)
        {
            var root = Path.Combine(Path.GetTempPath(), "dudu-composer-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            var animations = new List<AssetAnimation>(animationCount);
            var framePaths = new List<string>(animationCount);
            var animationsByKey = new Dictionary<string, AssetAnimation>(StringComparer.Ordinal);
            for (var i = 0; i < animationCount; i++)
            {
                var fileName = $"frame-{i}.png";
                var gray = ExpectedGray(i);
                using var bitmap = new SKBitmap(new SKImageInfo(dimension, dimension, SKColorType.Bgra8888, SKAlphaType.Premul));
                bitmap.Erase(new SKColor(gray, gray, gray, 255));
                using var image = SKImage.FromBitmap(bitmap);
                using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                var fullPath = Path.Combine(root, fileName);
                File.WriteAllBytes(fullPath, encoded.ToArray());
                framePaths.Add(fullPath);

                var animation = new AssetAnimation
                {
                    Frames = [new AssetFrame { File = fileName, DurationMs = 100 }],
                    Loop = "once",
                    NominalSize = new PixelSize(4, 4),
                    Anchor = new PixelPoint(0, 0),
                    ReducedMotion = fileName,
                };
                animations.Add(animation);
                animationsByKey[$"idle-{i}"] = animation;
            }

            var manifest = new AssetManifest
            {
                SchemaVersion = 1,
                PackId = "fixture-distinct-sized",
                Version = "1",
                PrivateUseOnly = true,
                Attribution = new AssetAttribution { Creator = "test" },
                Outfits = new Dictionary<string, AssetOutfit>(StringComparer.Ordinal)
                {
                    ["base"] = new AssetOutfit { Animations = animationsByKey },
                },
            };
            var pack = new AssetPack(Path.Combine(root, "manifest.json"), manifest);
            return new ComposerFixture(root, pack, animations[0], new SkiaFrameComposer(pack), animations, framePaths);
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
