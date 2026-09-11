using Dudu.Core;
using Xunit;

namespace Dudu.Core.Tests;

public sealed class ProductInfoTests
{
    [Fact]
    public void Product_identity_is_stable()
    {
        Assert.Equal("Dudu Desktop", ProductInfo.Name);
        Assert.Equal(1, ProductInfo.ProtocolVersion);
    }
}
