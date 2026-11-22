namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Base exception for failures in the standalone Crucible benchmark engine.
/// </summary>
public class BenchmarkException : Exception
{
    public BenchmarkException(string message)
        : base(message)
    {
    }

    public BenchmarkException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a benchmark dataset file is missing, unreadable, empty, or fails schema validation.
/// </summary>
public class BenchmarkDatasetException : BenchmarkException
{
    public string? DatasetPath { get; }
    public string? InstanceId { get; }

    public BenchmarkDatasetException(string message, string? datasetPath = null, string? instanceId = null)
        : base(message)
    {
        DatasetPath = datasetPath;
        InstanceId = instanceId;
    }

    public BenchmarkDatasetException(string message, Exception innerException, string? datasetPath = null, string? instanceId = null)
        : base(message, innerException)
    {
        DatasetPath = datasetPath;
        InstanceId = instanceId;
    }
}

/// <summary>
/// Thrown when caching, provisioning, resetting, or patching an ephemeral benchmark workspace fails.
/// </summary>
public class BenchmarkWorkspaceException : BenchmarkException
{
    public string? InstanceId { get; }
    public string? WorkspacePath { get; }

    public BenchmarkWorkspaceException(string message, string? instanceId = null, string? workspacePath = null)
        : base(message)
    {
        InstanceId = instanceId;
        WorkspacePath = workspacePath;
    }

    public BenchmarkWorkspaceException(string message, Exception innerException, string? instanceId = null, string? workspacePath = null)
        : base(message, innerException)
    {
        InstanceId = instanceId;
        WorkspacePath = workspacePath;
    }
}

/// <summary>
/// Thrown when benchmark verification (container image, test execution, or LLM judge invocation/parsing) fails fatally.
/// </summary>
public class BenchmarkVerificationException : BenchmarkException
{
    public string? InstanceId { get; }

    public BenchmarkVerificationException(string message, string? instanceId = null)
        : base(message)
    {
        InstanceId = instanceId;
    }

    public BenchmarkVerificationException(string message, Exception innerException, string? instanceId = null)
        : base(message, innerException)
    {
        InstanceId = instanceId;
    }
}
