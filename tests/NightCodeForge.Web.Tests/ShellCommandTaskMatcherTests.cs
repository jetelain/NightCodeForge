using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class ShellCommandTaskMatcherTests
{
    [Theory]
    [InlineData("/workspace/task-1", 1)]
    [InlineData("/workspace/task-1/", 1)]
    [InlineData("/home/user/.nightcodeforge/wks/1/task-1/src", 1)]
    [InlineData("C:\\repo\\task-1", 1)]
    [InlineData("C:\\repo\\task-1\\src\\app", 1)]
    [InlineData("/workspace/task_1", 1)]
    [InlineData("C:\\workspaces\\task_1\\build", 1)]
    [InlineData("task-1", 1)]
    [InlineData("task_1", 1)]
    [InlineData("/WORKSPACE/TASK-1/SRC", 1)]
    [InlineData("C:\\NCF\\Task_42\\sub", 42)]
    public void IsCommandForTask_WhenMatchingPathSegment_ReturnsTrue(string path, int taskId)
    {
        var result = ShellCommandTaskMatcher.IsCommandForTask(path, taskId);
        Assert.True(result);
    }

    [Theory]
    [InlineData("/workspace/task-10", 1)]
    [InlineData("/workspace/task-11", 1)]
    [InlineData("/workspace/task-19", 1)]
    [InlineData("/workspace/task-100", 1)]
    [InlineData("C:\\repo\\task-10\\src", 1)]
    [InlineData("C:\\repo\\task-19\\src", 1)]
    [InlineData("/workspace/task_10", 1)]
    [InlineData("/workspace/task-1", 10)]
    [InlineData("/workspace/task-2", 1)]
    [InlineData("/workspace/clone", 1)]
    [InlineData("/workspace/task-1-artifact", 1)]
    [InlineData("task-10", 1)]
    [InlineData("task-100", 1)]
    public void IsCommandForTask_WhenSegmentDoesNotMatchExactTaskId_ReturnsFalse(string path, int taskId)
    {
        var result = ShellCommandTaskMatcher.IsCommandForTask(path, taskId);
        Assert.False(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsCommandForTask_WhenPathNullOrWhitespace_ReturnsFalse(string? path)
    {
        Assert.False(ShellCommandTaskMatcher.IsCommandForTask(path, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-99)]
    public void IsCommandForTask_WhenTaskIdInvalid_ReturnsFalse(int taskId)
    {
        Assert.False(ShellCommandTaskMatcher.IsCommandForTask("/workspace/task-1", taskId));
    }
}
