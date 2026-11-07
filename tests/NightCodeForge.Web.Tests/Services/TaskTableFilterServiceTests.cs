using NightCodeForge.Core.Domain;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests.Services;

public class TaskTableFilterServiceTests
{
    private readonly DateTime _baseTime = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FilterAndSort_TextSearch_MatchesIdOrTitleOrDescription()
    {
        var tasks = new List<ForgeTask>
        {
            new() { Id = 10, Title = "Optimize Database Indexing", Description = "PostgreSQL tuning", Priority = 2 },
            new() { Id = 20, Title = "Add Auth Token Support", Description = "OAuth2 endpoints", Priority = 1 },
            new() { Id = 30, Title = "Documentation cleanup", Description = "Fix docs typo in database guide", Priority = 3 }
        };

        // By #ID
        var byId = TaskTableFilterService.FilterAndSort(tasks, "#20", TaskStatusFilter.All, null, TaskRecencyFilter.AllTime, TaskSortColumn.Id, true, _baseTime);
        Assert.Single(byId);
        Assert.Equal(20, byId[0].Id);

        // By title substring
        var byTitle = TaskTableFilterService.FilterAndSort(tasks, "Auth", TaskStatusFilter.All, null, TaskRecencyFilter.AllTime, TaskSortColumn.Id, true, _baseTime);
        Assert.Single(byTitle);
        Assert.Equal(20, byTitle[0].Id);

        // By description substring
        var byDesc = TaskTableFilterService.FilterAndSort(tasks, "PostgreSQL", TaskStatusFilter.All, null, TaskRecencyFilter.AllTime, TaskSortColumn.Id, true, _baseTime);
        Assert.Single(byDesc);
        Assert.Equal(10, byDesc[0].Id);

        // Numeric query matching description when no task has that ID
        var tasksWithNumberInDesc = new List<ForgeTask>
        {
            new() { Id = 1, Title = "Fix regression", Description = "Resolves issue 404 in router", Priority = 1 },
            new() { Id = 2, Title = "Other task", Description = "General cleanup", Priority = 2 }
        };
        var byNumericDesc = TaskTableFilterService.FilterAndSort(tasksWithNumberInDesc, "404", TaskStatusFilter.All, null, TaskRecencyFilter.AllTime, TaskSortColumn.Id, true, _baseTime);
        Assert.Single(byNumericDesc);
        Assert.Equal(1, byNumericDesc[0].Id);
    }

    [Fact]
    public void FilterAndSort_StatusAndPriorityAndRecencyFilters_FilterAccurately()
    {
        var tasks = new List<ForgeTask>
        {
            new() { Id = 1, Title = "Failed Task 24h", Status = TaskOverallStatus.Failed, Priority = 1, UpdatedAt = _baseTime.AddHours(-3) },
            new() { Id = 2, Title = "Failed Task Old", Status = TaskOverallStatus.Failed, Priority = 2, UpdatedAt = _baseTime.AddDays(-10) },
            new() { Id = 3, Title = "Closed Task 24h", Status = TaskOverallStatus.Closed, Priority = 1, UpdatedAt = _baseTime.AddHours(-1) },
            new() { Id = 4, Title = "Open Task 7d", Status = TaskOverallStatus.Open, Priority = 3, UpdatedAt = _baseTime.AddDays(-2) }
        };

        // Status filter: Failed
        var failedOnly = TaskTableFilterService.FilterAndSort(tasks, null, TaskStatusFilter.Failed, null, TaskRecencyFilter.AllTime, TaskSortColumn.Id, true, _baseTime);
        Assert.Equal(2, failedOnly.Count);
        Assert.All(failedOnly, t => Assert.Equal(TaskOverallStatus.Failed, t.Status));

        // Priority filter: P1
        var p1Only = TaskTableFilterService.FilterAndSort(tasks, null, TaskStatusFilter.All, 1, TaskRecencyFilter.AllTime, TaskSortColumn.Id, true, _baseTime);
        Assert.Equal(2, p1Only.Count);
        Assert.All(p1Only, t => Assert.Equal(1, t.Priority));

        // Recency filter: Last 24 Hours
        var recent24h = TaskTableFilterService.FilterAndSort(tasks, null, TaskStatusFilter.All, null, TaskRecencyFilter.Last24Hours, TaskSortColumn.Id, true, _baseTime);
        Assert.Equal(2, recent24h.Count);
        Assert.Contains(recent24h, t => t.Id == 1);
        Assert.Contains(recent24h, t => t.Id == 3);

        // Recency filter: Older Than 7 Days
        var older7d = TaskTableFilterService.FilterAndSort(tasks, null, TaskStatusFilter.All, null, TaskRecencyFilter.OlderThan7Days, TaskSortColumn.Id, true, _baseTime);
        Assert.Single(older7d);
        Assert.Equal(2, older7d[0].Id);
    }

    [Fact]
    public void FilterAndSort_DeterministicSorting_SupportsAscendingAndDescending()
    {
        var tasks = new List<ForgeTask>
        {
            new() { Id = 3, Title = "C", Priority = 3, CreatedAt = _baseTime.AddHours(-10), UpdatedAt = _baseTime.AddHours(-1) },
            new() { Id = 1, Title = "A", Priority = 1, CreatedAt = _baseTime.AddHours(-20), UpdatedAt = _baseTime.AddHours(-5) },
            new() { Id = 2, Title = "B", Priority = 2, CreatedAt = _baseTime.AddHours(-15), UpdatedAt = _baseTime.AddHours(-2) }
        };

        // Sort by Id Ascending
        var byIdAsc = TaskTableFilterService.FilterAndSort(tasks, null, TaskStatusFilter.All, null, TaskRecencyFilter.AllTime, TaskSortColumn.Id, true, _baseTime);
        Assert.Equal(new[] { 1, 2, 3 }, byIdAsc.Select(t => t.Id).ToArray());

        // Sort by Id Descending
        var byIdDesc = TaskTableFilterService.FilterAndSort(tasks, null, TaskStatusFilter.All, null, TaskRecencyFilter.AllTime, TaskSortColumn.Id, false, _baseTime);
        Assert.Equal(new[] { 3, 2, 1 }, byIdDesc.Select(t => t.Id).ToArray());

        // Sort by Priority Ascending (P1 -> P2 -> P3)
        var byPriorityAsc = TaskTableFilterService.FilterAndSort(tasks, null, TaskStatusFilter.All, null, TaskRecencyFilter.AllTime, TaskSortColumn.Priority, true, _baseTime);
        Assert.Equal(new[] { 1, 2, 3 }, byPriorityAsc.Select(t => t.Id).ToArray());

        // Sort by UpdatedAt Descending (most recent first)
        var byUpdatedDesc = TaskTableFilterService.FilterAndSort(tasks, null, TaskStatusFilter.All, null, TaskRecencyFilter.AllTime, TaskSortColumn.UpdatedAt, false, _baseTime);
        Assert.Equal(new[] { 3, 2, 1 }, byUpdatedDesc.Select(t => t.Id).ToArray());
    }
}

