namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Service for rendering dynamic prompt templates using a structured context model.
/// </summary>
public interface IPromptTemplateRenderer
{
    /// <summary>
    /// Renders the specified template with the provided model.
    /// </summary>
    /// <param name="templateText">The template text (e.g. Scriban syntax).</param>
    /// <param name="model">The context model object exposing template variables.</param>
    /// <returns>Rendered prompt string.</returns>
    string Render(string templateText, object model);
}
