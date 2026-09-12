using Dudu.Core.Abstractions;
using Xunit;

namespace Dudu.Core.Tests.Abstractions;

public sealed class PairingContractTests
{
    [Fact]
    public void Session_summary_exposes_only_the_opaque_revocation_handle()
    {
        var properties = typeof(PairingSessionSummary)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(["SessionId"], properties);
    }
}
