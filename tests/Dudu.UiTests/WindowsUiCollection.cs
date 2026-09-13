using Xunit;

namespace Dudu.UiTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WindowsUiCollection
{
    public const string Name = "Windows UI";
}
