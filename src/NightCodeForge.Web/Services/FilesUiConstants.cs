namespace NightCodeForge.Web.Services;

/// <summary>
/// Named constants for the Repository File Viewer page (/files), query parameters, element IDs, and JS interop hooks.
/// </summary>
public static class FilesUiConstants
{
    public const string RouteSegment = "files";
    public const string SectionDisplayName = "Repository Files";

    public const string RefQueryParameter = "ref";
    public const string PathQueryParameter = "path";
    public const string TaskIdQueryParameter = "taskId";
    public const string LinesQueryParameter = "lines";

    public const string LineElementIdPrefix = "file-line-";
    public const string MainPaneElementId = "files-main-pane";

    public const double TreeBaseIndentRem = 0.45;
    public const double TreeDepthIndentRem = 0.9;

    public const string ScrollToLineJsFunction = "NightCodeForge.scrollCodeLineIntoView";
    public const string CopyTextToClipboardJsFunction = "NightCodeForge.copyTextToClipboard";
}
