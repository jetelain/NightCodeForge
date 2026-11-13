using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Agents.Tests;

public sealed class WorkspaceFileToolModuleTests : IDisposable
{
    private readonly string _workspaceRoot;
    private readonly ISandboxPathResolver _pathResolver;
    private readonly IEditorConfigService _editorConfigService = new EditorConfigService();
    private readonly IEditorConfigFileApplier _editorConfigFileApplier = new EditorConfigFileApplier();

    public WorkspaceFileToolModuleTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), $"ncf-filemodule-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspaceRoot);

        var scope = new CanonicalToolScope(_workspaceRoot, WorkspaceId: 1, TaskId: 42);
        _pathResolver = new WorkspacePathResolverSandboxAdapter(scope, TestPathResolver.Create(), NullLogger.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspaceRoot))
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
    }

    private WorkspaceFileToolModule CreateModule(
        CanonicalToolScope? scope = null,
        ISandboxPathResolver? resolver = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        var effectiveScope = scope ?? new CanonicalToolScope(_workspaceRoot, WorkspaceId: 1, TaskId: 42);
        var effectiveResolver = resolver ?? _pathResolver;

        return scopeFactory != null
            ? new WorkspaceFileToolModule(
                effectiveScope,
                effectiveResolver,
                _editorConfigService,
                _editorConfigFileApplier,
                scopeFactory,
                NullLogger.Instance)
            : new WorkspaceFileToolModule(
                effectiveScope,
                effectiveResolver,
                _editorConfigService,
                _editorConfigFileApplier,
                NullLogger.Instance);
    }

    [Fact]
    public void Constructors_WithNullDependencies_ThrowArgumentNullException()
    {
        var scope = new CanonicalToolScope(_workspaceRoot);

        Assert.Throws<ArgumentNullException>(() => new WorkspaceFileToolModule(
            null!, _pathResolver, _editorConfigService, _editorConfigFileApplier, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => new WorkspaceFileToolModule(
            scope, null!, _editorConfigService, _editorConfigFileApplier, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => new WorkspaceFileToolModule(
            scope, _pathResolver, null!, _editorConfigFileApplier, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => new WorkspaceFileToolModule(
            scope, _pathResolver, _editorConfigService, null!, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => new WorkspaceFileToolModule(
            scope, _pathResolver, _editorConfigService, _editorConfigFileApplier, null!));
        Assert.Throws<ArgumentNullException>(() => new WorkspaceFileToolModule(
            scope, _pathResolver, _editorConfigService, _editorConfigFileApplier, null!, NullLogger.Instance));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1024)]
    public void Constructor_WithNonPositiveMaxReadFileBytes_ThrowsArgumentOutOfRangeException(int invalidBytes)
    {
        var scope = new CanonicalToolScope(_workspaceRoot, MaxReadFileBytes: invalidBytes);

        Assert.Throws<ArgumentOutOfRangeException>(() => CreateModule(scope));
    }

    [Fact]
    public void Constructor_UsesDefaultOrCustomMaxReadFileBytes()
    {
        var defaultModule = CreateModule(new CanonicalToolScope(_workspaceRoot));
        Assert.Equal(WorkflowDefaults.MaxReadFileBytes, defaultModule.MaxReadFileBytes);

        var customModule = CreateModule(new CanonicalToolScope(_workspaceRoot, MaxReadFileBytes: 8192));
        Assert.Equal(8192, customModule.MaxReadFileBytes);
    }

    [Fact]
    public async Task CreateFunctions_WhenSchemaOnly_ThrowsMissingModuleInstanceMessage()
    {
        var functions = WorkspaceFileToolModule.CreateFunctions(null, WorkflowDefaults.MaxReadFileBytes);
        Assert.Equal(3, functions.Count);

        var readFn = functions.Single(f => f.Name == CanonicalToolNames.Read);
        var writeFn = functions.Single(f => f.Name == CanonicalToolNames.Write);
        var editFn = functions.Single(f => f.Name == CanonicalToolNames.Edit);

        var readEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            readFn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["path"] = "a.txt" })).AsTask());
        Assert.Equal(WorkspaceFileToolModule.MissingModuleInstanceMessage, readEx.Message);

        var writeEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writeFn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["path"] = "a.txt",
                ["content"] = "hello"
            })).AsTask());
        Assert.Equal(WorkspaceFileToolModule.MissingModuleInstanceMessage, writeEx.Message);

        var editEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            editFn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["path"] = "a.txt",
                ["old_text"] = "a",
                ["new_text"] = "b"
            })).AsTask());
        Assert.Equal(WorkspaceFileToolModule.MissingModuleInstanceMessage, editEx.Message);
    }

    [Fact]
    public async Task CreateFunctions_BoundToModule_HandlesWriteReadEditAndReadOnlyErrors()
    {
        var module = CreateModule();
        var functions = module.CreateFunctions();
        var readFn = functions.Single(f => f.Name == CanonicalToolNames.Read);
        var writeFn = functions.Single(f => f.Name == CanonicalToolNames.Write);
        var editFn = functions.Single(f => f.Name == CanonicalToolNames.Edit);

        var writeRes = await writeFn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["path"] = "demo.txt",
            ["content"] = "foo bar foo\nsecond line"
        }));
        Assert.Contains("Successfully wrote", writeRes?.ToString());

        var editShortcutRes = await editFn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["path"] = "demo.txt",
            ["old_text"] = "second line",
            ["new_text"] = "updated line"
        }));
        Assert.Contains("Successfully applied 1 edit(s)", editShortcutRes?.ToString());

        var editListReplaceAllRes = await editFn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["path"] = "demo.txt",
            ["edits"] = new List<EditChunk> { new("foo", "baz", ReplaceAll: false) },
            ["replace_all"] = true
        }));
        Assert.Contains("2 occurrence(s) replaced", editListReplaceAllRes?.ToString());

        var missingParamsRes = await editFn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["path"] = "demo.txt"
        }));
        Assert.Equal(WorkspaceFileToolModule.MissingEditParametersMessage, missingParamsRes?.ToString());

        var readRes = await readFn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["path"] = "demo.txt"
        }));
        Assert.Contains("baz bar baz", readRes?.ToString());
        Assert.Contains("updated line", readRes?.ToString());

        var readOnlyModule = CreateModule(new CanonicalToolScope(_workspaceRoot, IsReadOnly: true));
        var roFunctions = readOnlyModule.CreateFunctions();
        var roWriteRes = await roFunctions.Single(f => f.Name == CanonicalToolNames.Write)
            .InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["path"] = "demo.txt",
                ["content"] = "blocked"
            }));
        Assert.Equal("Error: Write operations are not permitted in read-only mode.", roWriteRes?.ToString());

        var roEditRes = await roFunctions.Single(f => f.Name == CanonicalToolNames.Edit)
            .InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["path"] = "demo.txt",
                ["old_text"] = "baz",
                ["new_text"] = "qux"
            }));
        Assert.Equal("Error: Edit operations are not permitted in read-only mode.", roEditRes?.ToString());
    }

    [Fact]
    public async Task ReadAsync_HandlesMissingFile_UnauthorizedPath_Pagination_AndOffsetBeyondEnd()
    {
        var module = CreateModule();

        var missing = await module.ReadAsync("nonexistent.txt");
        Assert.Equal("Error: File not found: 'nonexistent.txt'", missing);

        var outside = await module.ReadAsync("../../outside.txt");
        Assert.StartsWith("Error:", outside);

        await File.WriteAllLinesAsync(Path.Combine(_workspaceRoot, "lines.txt"), ["line1", "line2", "line3", "line4"]);

        var paged = await module.ReadAsync("lines.txt", offset: 2, limit: 2);
        Assert.Contains("line2", paged);
        Assert.Contains("line3", paged);
        Assert.DoesNotContain("line1", paged);
        Assert.DoesNotContain("line4", paged);

        var beyondEnd = await module.ReadAsync("lines.txt", offset: 10, limit: 5);
        Assert.Equal("[File has 4 lines. Requested offset 10 is beyond end of file.]", beyondEnd);
    }

    [Fact]
    public async Task WriteAsync_CreatesSubdirectories_AndEnforcesReadOnlyAndSandbox()
    {
        var module = CreateModule();

        var result = await module.WriteAsync("nested/dir/file.txt", "hello world");
        Assert.Contains("Successfully wrote", result);
        Assert.Equal("hello world", (await File.ReadAllTextAsync(Path.Combine(_workspaceRoot, "nested", "dir", "file.txt"))).TrimEnd('\r', '\n'));
        Assert.Equal(Path.Combine(_workspaceRoot, "nested", "dir", "file.txt"), module.ResolveFullPath("nested/dir/file.txt"));

        var escapeResult = await module.WriteAsync("../../escape.txt", "bad");
        Assert.StartsWith("Error:", escapeResult);

        var roSandbox = new WorkspacePathResolverSandboxAdapter(
            new CanonicalToolScope(_workspaceRoot, IsReadOnly: true),
            TestPathResolver.Create(),
            NullLogger.Instance);
        var roModule = CreateModule(new CanonicalToolScope(_workspaceRoot, IsReadOnly: false), roSandbox);
        await Assert.ThrowsAsync<InvalidOperationException>(() => roModule.WriteAsync("file.txt", "blocked"));
    }

    [Fact]
    public async Task WriteAndEdit_ValidateTaskSpecifications_ViaExplicitAndScopeFactoryValidators()
    {
        var explicitValidator = new Mock<ITaskMarkdownValidator>(MockBehavior.Strict);
        explicitValidator.Setup(v => v.IsTaskSpecificationPath("docs/tasks/task-1.md")).Returns(true);
        explicitValidator.Setup(v => v.IsTaskSpecificationPath("src/Program.cs")).Returns(false);
        explicitValidator
            .Setup(v => v.ValidateAsync(1, 42, "docs/tasks/task-1.md", "invalid spec", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TaskMarkdownValidationResult.Invalid("Error: Circular dependency detected in task-1.md."));

        var module = CreateModule();
        module.TaskMarkdownValidator = explicitValidator.Object;
        Assert.Same(explicitValidator.Object, module.TaskMarkdownValidator);

        var rejectedWrite = await module.WriteAsync("docs/tasks/task-1.md", "invalid spec");
        Assert.Equal("Error: Circular dependency detected in task-1.md.", rejectedWrite);
        Assert.False(File.Exists(Path.Combine(_workspaceRoot, "docs", "tasks", "task-1.md")));

        var allowedNonSpec = await module.WriteAsync("src/Program.cs", "class Program {}");
        Assert.Contains("Successfully wrote", allowedNonSpec);

        var scopedValidator = new Mock<ITaskMarkdownValidator>(MockBehavior.Strict);
        scopedValidator.Setup(v => v.IsTaskSpecificationPath("docs/tasks/task-2.md")).Returns(true);
        scopedValidator
            .Setup(v => v.ValidateAsync(1, 42, "docs/tasks/task-2.md", "bad edit", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TaskMarkdownValidationResult.Invalid("Error: Invalid task markdown on edit."));

        var services = new ServiceCollection();
        services.AddScoped(_ => scopedValidator.Object);
        using var provider = services.BuildServiceProvider();

        var scopedModule = CreateModule(scopeFactory: provider.GetRequiredService<IServiceScopeFactory>());
        Directory.CreateDirectory(Path.Combine(_workspaceRoot, "docs", "tasks"));
        await File.WriteAllTextAsync(Path.Combine(_workspaceRoot, "docs", "tasks", "task-2.md"), "initial");

        var rejectedEdit = await scopedModule.EditAsync("docs/tasks/task-2.md", "initial", "bad edit");
        Assert.Equal("Error: Invalid task markdown on edit.", rejectedEdit);
        Assert.Equal("initial", await File.ReadAllTextAsync(Path.Combine(_workspaceRoot, "docs", "tasks", "task-2.md")));
    }

    [Fact]
    public async Task SettingTaskMarkdownValidatorToNull_OverridesConfiguredDefaultAndScopeFactoryValidators()
    {
        var defaultValidator = new Mock<ITaskMarkdownValidator>(MockBehavior.Strict);
        var scopedValidator = new Mock<ITaskMarkdownValidator>(MockBehavior.Strict);

        var services = new ServiceCollection();
        services.AddScoped(_ => scopedValidator.Object);
        using var provider = services.BuildServiceProvider();

        var module = CreateModule(scopeFactory: provider.GetRequiredService<IServiceScopeFactory>());
        module.ConfigureDefaultTaskMarkdownValidator(defaultValidator.Object, provider.GetRequiredService<IServiceScopeFactory>());
        Assert.Same(defaultValidator.Object, module.TaskMarkdownValidator);

        // Explicitly detaching validator via null must disable validation even when default/ScopeFactory exist
        module.TaskMarkdownValidator = null;
        Assert.Null(module.TaskMarkdownValidator);

        var writeResult = await module.WriteAsync("docs/tasks/task-detached.md", "unvalidated spec");
        Assert.Contains("Successfully wrote", writeResult);

        var editResult = await module.EditAsync("docs/tasks/task-detached.md", "unvalidated spec", "edited spec");
        Assert.Contains("Successfully applied 1 edit(s)", editResult);
    }

    [Fact]
    public async Task EditAsync_CoversValidation_ReplaceAll_CrlfNormalization_AndFuzzyIndentationMatching()
    {
        var module = CreateModule();

        Assert.Equal("Error: At least one edit chunk must be provided.", await module.EditAsync("any.cs", Array.Empty<EditChunk>()));
        Assert.Equal("Error: File not found for edit: 'missing.cs'", await module.EditAsync("missing.cs", "a", "b"));
        Assert.StartsWith("Error:", await module.EditAsync("../../outside.cs", "a", "b"));

        // Ambiguous exact match when replaceAll is false
        var dupFile = Path.Combine(_workspaceRoot, "dup.cs");
        await File.WriteAllTextAsync(dupFile, "int x = 1;\nint x = 1;\n");
        var ambiguous = await module.EditAsync("dup.cs", "int x = 1;", "int x = 2;", replaceAll: false);
        Assert.Contains("matches multiple locations", ambiguous);

        // replaceAll with empty oldText
        var emptyOld = await module.EditAsync("dup.cs", "", "int x = 2;", replaceAll: true);
        Assert.Contains("oldText cannot be empty", emptyOld);

        // replaceAll when target is missing
        var missingTarget = await module.EditAsync("dup.cs", "not_present", "x", replaceAll: true);
        Assert.Contains("could not locate target chunk", missingTarget);

        // replaceAll with LF oldText against CRLF source file
        await File.WriteAllTextAsync(dupFile, "lineA\r\nlineB\r\nlineA\r\nlineB\r\n");
        var crlfReplace = await module.EditAsync("dup.cs", "lineA\nlineB", "lineC\nlineD", replaceAll: true);
        Assert.Contains("2 occurrence(s) replaced", crlfReplace);

        // Strict whitespace language (.py) rejects indentation mismatch
        var pyFile = Path.Combine(_workspaceRoot, "script.py");
        await File.WriteAllTextAsync(pyFile, "def foo():\n    return 1\n");
        var pyFail = await module.EditAsync("script.py", "def foo():\n  return 1", "def foo():\n    return 2");
        Assert.Contains("exact oldText not found in whitespace-sensitive file 'script.py'", pyFail);

        // Fuzzy indentation match on C# file (.cs) adjusts indentation relative to file base indent
        var csFile = Path.Combine(_workspaceRoot, "Service.cs");
        await File.WriteAllTextAsync(csFile,
            "namespace Demo;\npublic class Service\n{\n        public int Compute()\n        {\n            return 1;\n        }\n}\n");

        var fuzzyOk = await module.EditAsync(
            "Service.cs",
            "  public int Compute()\n  {\n      return 1;\n  }",
            "  public int Compute()\n  {\n      var val = 42;\n\n      return val;\n  }");
        Assert.Contains("Successfully applied 1 edit(s)", fuzzyOk);
        var updatedCs = await File.ReadAllTextAsync(csFile);
        Assert.Contains("        public int Compute()", updatedCs);
        Assert.Contains("            var val = 42;", updatedCs);
        Assert.Contains("            return val;", updatedCs);

        // Fuzzy match errors: whitespace-only oldText, 0 matches, and multiple fuzzy matches
        var wsOnly = await module.EditAsync("Service.cs", "   \n  \t ", "replacement");
        Assert.Contains("oldText is empty or whitespace-only", wsOnly);

        var noFuzzy = await module.EditAsync("Service.cs", "public void NonExistent()\n{\n}", "public void X() {}");
        Assert.Contains("Neither exact nor fuzzy indentation match succeeded", noFuzzy);

        await File.WriteAllTextAsync(csFile, "    {\n        DoWork();\n    }\n    {\n        DoWork();\n    }\n");
        var multiFuzzy = await module.EditAsync("Service.cs", "{\n    DoWork();\n}", "{\n    DoOther();\n}");
        Assert.Contains("fuzzy indentation match found 2 matching blocks", multiFuzzy);
    }
}
