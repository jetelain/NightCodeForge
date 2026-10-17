namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Thrown when a target base branch does not exist in the repository during a squash and merge operation.
/// </summary>
public class BaseBranchNotFoundException : InvalidOperationException
{
    public string BranchName { get; }
    public string BaseBranch => BranchName;
    public string RepositoryPath { get; }

    public BaseBranchNotFoundException(string branchName, string repositoryPath)
        : base($"Target base branch '{branchName}' was not found in repository '{repositoryPath}'.")
    {
        BranchName = branchName;
        RepositoryPath = repositoryPath;
    }

    public BaseBranchNotFoundException(string branchName, string repositoryPath, Exception? innerException)
        : base($"Target base branch '{branchName}' was not found in repository '{repositoryPath}'.", innerException)
    {
        BranchName = branchName;
        RepositoryPath = repositoryPath;
    }
}
