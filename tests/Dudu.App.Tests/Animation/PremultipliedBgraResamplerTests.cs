using Dudu.App.Animation;
using Xunit;

namespace Dudu.App.Tests.Animation;

public sealed class PremultipliedBgraResamplerTests
{
    [Fact]
    public void Equal_sizes_copy_rows_exactly_honoring_strides()
    {
        var source = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 99, 99, 10, 11, 12, 13, 14, 15, 16, 17, 99, 99 };
        var destination = new byte[16];

        new PremultipliedBgraResampler().Resample(source, 2, 2, 10, destination, 2, 2, 8);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 13, 14, 15, 16, 17 }, destination);
    }

    [Fact]
    public void Halving_averages_each_two_by_two_block_instead_of_point_sampling()
    {
        // Opaque white / transparent checkerboard. Nearest-neighbour picked
        // one pixel per block (fully on or off: jagged); area averaging gives
        // the block's true 50% coverage.
        var source = new byte[4 * 4 * 4];
        for (var y = 0; y < 4; y++)
        for (var x = 0; x < 4; x++)
        {
            if ((x + y) % 2 == 0) source.AsSpan((y * 4 + x) * 4, 4).Fill(255);
        }

        var destination = new byte[2 * 2 * 4];
        new PremultipliedBgraResampler().Resample(source, 4, 4, 16, destination, 2, 2, 8);

        Assert.All(destination, value => Assert.InRange(value, 127, 128));
    }

    [Theory]
    [InlineData(512, 384)]
    [InlineData(512, 576)]
    [InlineData(3, 7)]
    [InlineData(7, 3)]
    public void Flat_colour_is_preserved_at_any_scale(int sourceSide, int destinationSide)
    {
        var source = new byte[sourceSide * sourceSide * 4];
        for (var index = 0; index < source.Length; index += 4)
        {
            source[index] = 40;
            source[index + 1] = 80;
            source[index + 2] = 120;
            source[index + 3] = 200;
        }

        var destination = new byte[destinationSide * destinationSide * 4];
        new PremultipliedBgraResampler().Resample(
            source, sourceSide, sourceSide, sourceSide * 4,
            destination, destinationSide, destinationSide, destinationSide * 4);

        for (var index = 0; index < destination.Length; index += 4)
        {
            Assert.Equal(40, destination[index]);
            Assert.Equal(80, destination[index + 1]);
            Assert.Equal(120, destination[index + 2]);
            Assert.Equal(200, destination[index + 3]);
        }
    }

    [Theory]
    [InlineData(64, 48)]
    [InlineData(48, 64)]
    [InlineData(64, 23)]
    public void Premultiplied_colour_never_exceeds_alpha(int sourceSide, int destinationSide)
    {
        var random = new Random(1234);
        var source = new byte[sourceSide * sourceSide * 4];
        for (var index = 0; index < source.Length; index += 4)
        {
            var alpha = (byte)random.Next(256);
            source[index] = (byte)random.Next(alpha + 1);
            source[index + 1] = (byte)random.Next(alpha + 1);
            source[index + 2] = (byte)random.Next(alpha + 1);
            source[index + 3] = alpha;
        }

        var resampler = new PremultipliedBgraResampler();
        var destination = new byte[destinationSide * destinationSide * 4];
        // Twice: the second call reuses cached kernels and scratch buffers.
        for (var pass = 0; pass < 2; pass++)
        {
            resampler.Resample(
                source, sourceSide, sourceSide, sourceSide * 4,
                destination, destinationSide, destinationSide, destinationSide * 4);
        }

        for (var index = 0; index < destination.Length; index += 4)
        {
            Assert.True(destination[index] <= destination[index + 3]);
            Assert.True(destination[index + 1] <= destination[index + 3]);
            Assert.True(destination[index + 2] <= destination[index + 3]);
        }
    }

    [Theory]
    [InlineData(512, 384)]
    [InlineData(512, 768)]
    [InlineData(5, 2)]
    public void Kernel_weights_are_non_negative_and_sum_to_one(int source, int destination)
    {
        var kernel = PremultipliedBgraResampler.AxisKernel.Create(source, destination);

        for (var index = 0; index < destination; index++)
        {
            var weights = kernel.Weights.AsSpan(kernel.Offset[index], kernel.Count[index]);
            var sum = 0;
            foreach (var weight in weights)
            {
                Assert.True(weight >= 0);
                sum += weight;
            }

            Assert.Equal(1 << 14, sum);
            Assert.InRange(kernel.Start[index], 0, source - 1);
            Assert.InRange(kernel.Start[index] + kernel.Count[index] - 1, 0, source - 1);
        }
    }
}
