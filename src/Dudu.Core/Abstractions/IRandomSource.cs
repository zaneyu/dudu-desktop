namespace Dudu.Core.Abstractions;

public interface IRandomSource
{
    int Next(int exclusiveMax);
}
