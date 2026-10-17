using NightCodeForge.Core.Domain;

namespace NightCodeForge.Web.Tests;

public static class TestWorkflowFixtures
{
    public static WorkflowDefinition CreateStandardWorkflow()
    {
        var wf = StandardWorkflowDefinitions.CreateStandard3AgentWorkflow();
        wf.Id = 1;

        int stageIdCounter = 1;
        foreach (var s in wf.Stages)
        {
            s.Id = stageIdCounter++;
            s.Workflow = wf;
            s.WorkflowId = wf.Id;
        }

        return wf;
    }
}
