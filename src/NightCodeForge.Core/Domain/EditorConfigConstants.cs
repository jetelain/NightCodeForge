namespace NightCodeForge.Core.Domain;

/// <summary>
/// Domain constants and baseline configuration for repository .editorconfig cross-platform hygiene.
/// </summary>
public static class EditorConfigConstants
{
    public const string BaselineEditorConfigContent =
@"root = true

[*]
end_of_line = lf
insert_final_newline = true
trim_trailing_whitespace = true
charset = utf-8
indent_style = space
indent_size = 4

[*.{cs,csx}]
indent_style = space
indent_size = 4

[*.{json,yml,yaml,md}]
indent_style = space
indent_size = 2

[*.{js,jsx,ts,tsx}]
indent_style = space
indent_size = 2

[*.py]
indent_style = space
indent_size = 4

[*.rs]
indent_style = space
indent_size = 4

[*.go]
indent_style = tab
indent_size = 4

[*.{bat,cmd}]
end_of_line = crlf";
}
