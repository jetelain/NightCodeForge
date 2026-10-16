namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Scriban-based implementation of IPromptTemplateRenderer providing whitespace-trimmed,
/// conditional templating for agent prompts.
/// </summary>
public class ScribanPromptTemplateRenderer : IPromptTemplateRenderer
{
    public string Render(string templateText, object model)
    {
        if (string.IsNullOrWhiteSpace(templateText))
        {
            return string.Empty;
        }

        var template = Scriban.Template.Parse(templateText);
        if (template.HasErrors)
        {
            var errors = string.Join("; ", template.Messages.Select(m => m.Message));
            throw new InvalidOperationException($"Failed parsing Scriban prompt template: {errors}");
        }

        return template.Render(model, member => member.Name);
    }
}
