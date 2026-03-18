namespace SirThaddeus.WebSearch;

public interface IPlaceDetailsProvider
{
    string Name { get; }
    bool IsConfigured { get; }
}