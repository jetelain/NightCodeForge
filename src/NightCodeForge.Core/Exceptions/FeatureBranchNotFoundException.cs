namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Thrown when a feature branch does not exist locally or on any remote during a squash and merge operation.
/// </summary>
public class FeatureBranchNotFoundException : InvalidOperationException
{
    public string BranchName { get; }
    public string FeatureBranch => BranchName;
    public string RepositoryPath { get; }

    public FeatureBranchNotFoundException(string branchName, string repositoryPath)
        : base($"Feature branch '{branchName}' was not found locally or on remotes in repository '{repositoryPath}'.")
    {
        BranchName = branchName;
        RepositoryPath = repositoryPath;
    }

    public FeatureBranchNotFoundException(string branchName, string repositoryPath, Exception? innerException)
        : base($"Feature branch '{branchName}' was not found locally or on remotes in repository '{repositoryPath}'.", innerException)
    {
        BranchName = branchName;
        RepositoryPath = repositoryPath;
    }
}
