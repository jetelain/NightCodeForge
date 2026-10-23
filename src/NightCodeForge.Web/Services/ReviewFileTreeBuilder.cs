using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Node representation in the hierarchical review file tree.
/// </summary>
public class ReviewFileTreeNode
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public bool IsExpanded { get; set; } = true;
    public List<ReviewFileTreeNode> Children { get; set; } = new();
    public FileDiffSummary? File { get; set; }

    public int TotalAdditions => IsDirectory ? Children.Sum(c => c.TotalAdditions) : (File?.Additions ?? 0);
    public int TotalDeletions => IsDirectory ? Children.Sum(c => c.TotalDeletions) : (File?.Deletions ?? 0);
}

/// <summary>
/// Builds a clean hierarchical tree view for changed files in code review,
/// compressing single-child directory chains and ensuring prominent leaf filename visibility.
/// </summary>
public static class ReviewFileTreeBuilder
{
    public static List<ReviewFileTreeNode> BuildTree(IEnumerable<FileDiffSummary>? files)
    {
        if (files == null) return new List<ReviewFileTreeNode>();

        var root = new ReviewFileTreeNode
        {
            Name = "root",
            FullPath = "",
            IsDirectory = true
        };

        foreach (var file in files)
        {
            var normalizedPath = file.Path.Replace('\\', '/').Trim('/');
            var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) continue;

            var current = root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                var segment = segments[i];
                var nextDir = current.Children.FirstOrDefault(c => c.IsDirectory && c.Name == segment);
                if (nextDir == null)
                {
                    nextDir = new ReviewFileTreeNode
                    {
                        Name = segment,
                        FullPath = string.IsNullOrEmpty(current.FullPath) ? segment : $"{current.FullPath}/{segment}",
                        IsDirectory = true,
                        IsExpanded = true
                    };
                    current.Children.Add(nextDir);
                }
                current = nextDir;
            }

            var fileName = segments[^1];
            current.Children.Add(new ReviewFileTreeNode
            {
                Name = fileName,
                FullPath = normalizedPath,
                IsDirectory = false,
                File = file
            });
        }

        // Compress single-child directory chains
        CompressSingleChildDirectories(root);

        // Sort children recursively: directories first, then alphabetical
        SortNodes(root.Children);

        return root.Children;
    }

    private static void CompressSingleChildDirectories(ReviewFileTreeNode node)
    {
        for (int i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            if (child.IsDirectory)
            {
                CompressSingleChildDirectories(child);

                // If child is a directory with no files and exactly 1 child directory
                while (child.IsDirectory && child.Children.Count == 1 && child.Children[0].IsDirectory)
                {
                    var grandChild = child.Children[0];
                    child.Name = $"{child.Name}/{grandChild.Name}";
                    child.FullPath = grandChild.FullPath;
                    child.Children = grandChild.Children;
                }
            }
        }
    }

    private static void SortNodes(List<ReviewFileTreeNode> nodes)
    {
        nodes.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory)
            {
                return a.IsDirectory ? -1 : 1;
            }
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        foreach (var n in nodes)
        {
            if (n.IsDirectory && n.Children.Count > 0)
            {
                SortNodes(n.Children);
            }
        }
    }
}
