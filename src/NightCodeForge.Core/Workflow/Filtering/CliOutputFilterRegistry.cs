namespace NightCodeForge.Core.Workflow.Filtering;

/// <summary>
/// Default registry of semantic CLI output filters (<c>dotnet</c>/<c>msbuild</c>, <c>node</c>/<c>tsc</c>, <c>cargo</c>).
/// </summary>
public sealed class CliOutputFilterRegistry : ICliOutputFilterRegistry
{
    public static ICliOutputFilterRegistry Default { get; } = new CliOutputFilterRegistry();

    private readonly IReadOnlyList<ICliOutputFilter> _filters;

    public CliOutputFilterRegistry()
        : this([new DotNetCliOutputFilter(), new NodeTscCliFilter(), new CargoCliFilter()])
    {
    }

    public CliOutputFilterRegistry(IEnumerable<ICliOutputFilter> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        _filters = filters.Where(f => f != null).ToList();
    }

    public ICliOutputFilter? GetFilter(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        foreach (var filter in _filters)
        {
            if (filter.CanHandle(command))
            {
                return filter;
            }
        }

        return null;
    }
}
