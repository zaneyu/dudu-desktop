namespace Dudu.App.Animation;

/// <summary>
/// Resamples premultiplied BGRA pixels for the layered window: area
/// averaging (box filter) when shrinking and bilinear when enlarging,
/// applied separably with fixed-point weights. Replaces nearest-neighbour
/// sampling, which made the 512px artwork and bubble text jagged when drawn
/// into a smaller (or DPI-enlarged) pet window.
/// </summary>
/// <remarks>
/// Filtering premultiplied values with non-negative weights that sum to one
/// keeps every colour channel &lt;= alpha, and monotone rounding preserves
/// that after quantization. Kernels and scratch buffers are cached per size,
/// so steady-state frames do not allocate. Not thread-safe: the owner
/// serializes calls.
/// </remarks>
internal sealed class PremultipliedBgraResampler
{
    private const int WeightBits = 14;
    private const int WeightOne = 1 << WeightBits;
    private const int IntermediateShift = WeightBits - 8;

    private AxisKernel? _horizontal;
    private AxisKernel? _vertical;
    private ushort[] _intermediate = [];
    private int[] _rowAccumulator = [];

    public void Resample(
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        Span<byte> destination,
        int destinationWidth,
        int destinationHeight,
        int destinationStride)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || sourceStride < sourceWidth * 4)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        }

        if (destinationWidth <= 0 || destinationHeight <= 0 || destinationStride < destinationWidth * 4)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationWidth));
        }

        if (source.Length < checked((sourceHeight - 1) * sourceStride + sourceWidth * 4)
            || destination.Length < checked((destinationHeight - 1) * destinationStride + destinationWidth * 4))
        {
            throw new ArgumentException("Pixel buffers are smaller than their declared geometry.");
        }

        if (sourceWidth == destinationWidth && sourceHeight == destinationHeight)
        {
            var rowBytes = sourceWidth * 4;
            for (var row = 0; row < sourceHeight; row++)
            {
                source.Slice(row * sourceStride, rowBytes)
                    .CopyTo(destination.Slice(row * destinationStride, rowBytes));
            }

            return;
        }

        var horizontal = _horizontal = AxisKernel.GetOrCreate(_horizontal, sourceWidth, destinationWidth);
        var vertical = _vertical = AxisKernel.GetOrCreate(_vertical, sourceHeight, destinationHeight);
        var intermediateRow = destinationWidth * 4;
        var intermediateLength = checked(sourceHeight * intermediateRow);
        if (_intermediate.Length < intermediateLength) _intermediate = new ushort[intermediateLength];
        if (_rowAccumulator.Length < intermediateRow) _rowAccumulator = new int[intermediateRow];
        var intermediate = _intermediate.AsSpan(0, intermediateLength);
        var accumulator = _rowAccumulator.AsSpan(0, intermediateRow);

        // Horizontal pass: each source row -> destination width, stored as
        // value * 256 so the vertical pass keeps 8 extra bits of precision.
        for (var row = 0; row < sourceHeight; row++)
        {
            var sourceRow = source.Slice(row * sourceStride, sourceWidth * 4);
            var outputRow = intermediate.Slice(row * intermediateRow, intermediateRow);
            for (var x = 0; x < destinationWidth; x++)
            {
                var start = horizontal.Start[x];
                var count = horizontal.Count[x];
                var offset = horizontal.Offset[x];
                int b = 0, g = 0, r = 0, a = 0;
                for (var tap = 0; tap < count; tap++)
                {
                    var weight = horizontal.Weights[offset + tap];
                    var pixel = (start + tap) * 4;
                    b += sourceRow[pixel] * weight;
                    g += sourceRow[pixel + 1] * weight;
                    r += sourceRow[pixel + 2] * weight;
                    a += sourceRow[pixel + 3] * weight;
                }

                var target = x * 4;
                outputRow[target] = RoundToIntermediate(b);
                outputRow[target + 1] = RoundToIntermediate(g);
                outputRow[target + 2] = RoundToIntermediate(r);
                outputRow[target + 3] = RoundToIntermediate(a);
            }
        }

        // Vertical pass: weighted sum of intermediate rows per output row.
        for (var y = 0; y < destinationHeight; y++)
        {
            accumulator.Clear();
            var start = vertical.Start[y];
            var count = vertical.Count[y];
            var offset = vertical.Offset[y];
            for (var tap = 0; tap < count; tap++)
            {
                var weight = vertical.Weights[offset + tap];
                var inputRow = intermediate.Slice((start + tap) * intermediateRow, intermediateRow);
                for (var index = 0; index < intermediateRow; index++)
                {
                    accumulator[index] += inputRow[index] * weight;
                }
            }

            var outputRow = destination.Slice(y * destinationStride, intermediateRow);
            for (var index = 0; index < intermediateRow; index++)
            {
                var value = (accumulator[index] + (1 << (WeightBits + 7))) >> (WeightBits + 8);
                outputRow[index] = (byte)(value > byte.MaxValue ? byte.MaxValue : value);
            }
        }
    }

    private static ushort RoundToIntermediate(int weightedSum) =>
        (ushort)((weightedSum + (1 << (IntermediateShift - 1))) >> IntermediateShift);

    /// <summary>Per-axis filter taps: for output index i, source pixels
    /// Start[i] .. Start[i] + Count[i] - 1 with Weights[Offset[i] ..].</summary>
    internal sealed class AxisKernel
    {
        private AxisKernel(int sourceLength, int destinationLength, int[] start, int[] count, int[] offset, int[] weights)
        {
            SourceLength = sourceLength;
            DestinationLength = destinationLength;
            Start = start;
            Count = count;
            Offset = offset;
            Weights = weights;
        }

        public int SourceLength { get; }
        public int DestinationLength { get; }
        public int[] Start { get; }
        public int[] Count { get; }
        public int[] Offset { get; }
        public int[] Weights { get; }

        public static AxisKernel GetOrCreate(AxisKernel? cached, int sourceLength, int destinationLength) =>
            cached is not null
            && cached.SourceLength == sourceLength
            && cached.DestinationLength == destinationLength
                ? cached
                : Create(sourceLength, destinationLength);

        public static AxisKernel Create(int sourceLength, int destinationLength)
        {
            var scale = sourceLength / (double)destinationLength;
            var start = new int[destinationLength];
            var count = new int[destinationLength];
            var offset = new int[destinationLength];
            var weights = new List<int>(destinationLength * 3);
            Span<double> coverage = stackalloc double[Math.Max(2, (int)Math.Ceiling(scale) + 2)];
            for (var index = 0; index < destinationLength; index++)
            {
                int first;
                int taps;
                if (scale > 1)
                {
                    // Box filter: exact coverage of this output pixel's
                    // footprint over the source pixels.
                    var left = index * scale;
                    var right = Math.Min(sourceLength, (index + 1) * scale);
                    first = (int)Math.Floor(left);
                    var last = Math.Min(sourceLength - 1, (int)Math.Ceiling(right) - 1);
                    taps = last - first + 1;
                    for (var tap = 0; tap < taps; tap++)
                    {
                        var pixel = first + tap;
                        coverage[tap] = Math.Max(0, Math.Min(right, pixel + 1) - Math.Max(left, pixel));
                    }
                }
                else
                {
                    // Bilinear between the two nearest source centres.
                    var center = (index + 0.5) * scale - 0.5;
                    first = (int)Math.Floor(center);
                    var fraction = center - first;
                    if (first < 0)
                    {
                        first = 0;
                        fraction = 0;
                    }

                    if (first >= sourceLength - 1)
                    {
                        first = sourceLength - 1;
                        fraction = 0;
                    }

                    taps = fraction > 0 ? 2 : 1;
                    coverage[0] = 1 - fraction;
                    if (taps == 2) coverage[1] = fraction;
                }

                start[index] = first;
                count[index] = taps;
                offset[index] = weights.Count;
                AppendNormalized(weights, coverage[..taps]);
            }

            return new AxisKernel(sourceLength, destinationLength, start, count, offset, weights.ToArray());
        }

        private static void AppendNormalized(List<int> weights, ReadOnlySpan<double> coverage)
        {
            var total = 0d;
            foreach (var value in coverage) total += value;
            var sum = 0;
            var largest = weights.Count;
            for (var tap = 0; tap < coverage.Length; tap++)
            {
                var weight = total > 0
                    ? (int)Math.Round(coverage[tap] / total * WeightOne, MidpointRounding.AwayFromZero)
                    : (tap == 0 ? WeightOne : 0);
                weights.Add(weight);
                sum += weight;
                if (weight > weights[largest]) largest = weights.Count - 1;
            }

            // Make the weights sum to exactly one so flat colour is preserved.
            weights[largest] += WeightOne - sum;
        }
    }
}
