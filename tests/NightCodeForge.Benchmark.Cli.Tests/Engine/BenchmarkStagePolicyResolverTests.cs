using NightCodeForge.Agents.Context;
using NightCodeForge.Benchmark.Cli.Engine;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Benchmark.Cli.Tests.Engine;

public sealed class BenchmarkStagePolicyResolverTests
{
    [Theory]
    [InlineData(BenchmarkProblemCategory.Implementation, StandardStageIds.Implementation, true, true)]
    [InlineData(BenchmarkProblemCategory.Implementation, StandardStageIds.Implementation, false, false)]
    [InlineData(BenchmarkProblemCategory.CodeReview, StandardStageIds.CodeReviewAndFix, true, true)]
    [InlineData(BenchmarkProblemCategory.CodeReview, StandardStageIds.CodeReviewAndFix, false, false)]
    [InlineData(BenchmarkProblemCategory.TaskReview, StandardStageIds.TaskReview, true, true)]
    [InlineData(BenchmarkProblemCategory.TaskReview, StandardStageIds.TaskReview, false, false)]
    public void Resolve_GatesToolsAndSkillsPerCategory(
        BenchmarkProblemCategory category,
        string expectedStageId,
        bool enableStackSkills,
        bool expectLoadSkill)
    {
        var policy = BenchmarkStagePolicyResolver.Resolve(category, enableStackSkills);

        Assert.Equal(expectedStageId, policy.StageId);
        Assert.Equal(AgentMode.Full, policy.RequiredMode);
        Assert.False(string.IsNullOrWhiteSpace(policy.StageDirective));

        Assert.Contains(CanonicalToolNames.Read, policy.PermittedTools);
        Assert.Contains(CanonicalToolNames.Edit, policy.PermittedTools);
        Assert.Contains(CanonicalToolNames.Write, policy.PermittedTools);
        Assert.Contains(CanonicalToolNames.Bash, policy.PermittedTools);
        Assert.DoesNotContain(CanonicalToolNames.ProposeTasks, policy.PermittedTools);
        Assert.DoesNotContain(CanonicalToolNames.WebSearch, policy.PermittedTools);

        if (expectLoadSkill)
        {
            Assert.Contains(CanonicalToolNames.LoadSkill, policy.PermittedTools);
        }
        else
        {
            Assert.DoesNotContain(CanonicalToolNames.LoadSkill, policy.PermittedTools);
        }

        if (category == BenchmarkProblemCategory.Implementation)
        {
            Assert.DoesNotContain(CanonicalToolNames.Ask, policy.PermittedTools);
            Assert.DoesNotContain(CanonicalToolNames.RequestChanges, policy.PermittedTools);
        }
        else if (category == BenchmarkProblemCategory.CodeReview)
        {
            Assert.Contains(CanonicalToolNames.RequestChanges, policy.PermittedTools);
            Assert.DoesNotContain(CanonicalToolNames.Ask, policy.PermittedTools);
        }
        else if (category == BenchmarkProblemCategory.TaskReview)
        {
            Assert.Contains(CanonicalToolNames.Ask, policy.PermittedTools);
            Assert.DoesNotContain(CanonicalToolNames.RequestChanges, policy.PermittedTools);
        }
    }

    [Fact]
    public void ConfigureWorkspaceSkills_ExcludesMdFetchForCSharpAndForcesDisabledWhenOff()
    {
        var enabledWorkspace = new Workspace();
        BenchmarkStagePolicyResolver.ConfigureWorkspaceSkills(enabledWorkspace, enableStackSkills: true, BenchmarkLanguages.CSharp);

        Assert.Equal(GlobalSkillsInjectionMode.ForceEnabled, enabledWorkspace.GlobalSkillsInjectionMode);
        Assert.Contains(BenchmarkSkillNames.MdFetch, enabledWorkspace.DisabledGlobalSkillsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(BenchmarkSkillNames.CSharpNav, enabledWorkspace.DisabledGlobalSkillsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(BenchmarkSkillNames.CodeCondenser, enabledWorkspace.DisabledGlobalSkillsJson, StringComparison.OrdinalIgnoreCase);

        var disabledWorkspace = new Workspace();
        BenchmarkStagePolicyResolver.ConfigureWorkspaceSkills(disabledWorkspace, enableStackSkills: false, BenchmarkLanguages.CSharp);

        Assert.Equal(GlobalSkillsInjectionMode.ForceDisabled, disabledWorkspace.GlobalSkillsInjectionMode);
        Assert.Null(disabledWorkspace.DisabledGlobalSkillsJson);
    }

    [Theory]
    [InlineData("csharp")]
    [InlineData("c#")]
    [InlineData("dotnet")]
    public void ConfigureWorkspaceSkills_CSharpLanguages_KeepCSharpStackSkillsActive(string language)
    {
        var workspace = new Workspace();
        BenchmarkStagePolicyResolver.ConfigureWorkspaceSkills(workspace, enableStackSkills: true, language);

        var resolved = GlobalSkillsResolver.Resolve(
            workspace,
            projectConfig: null,
            [BenchmarkSkillNames.CSharpNav, BenchmarkSkillNames.CodeCondenser, BenchmarkSkillNames.MdFetch, "general-debugging"]);

        Assert.True(resolved.IsEnabled);
        Assert.Contains(BenchmarkSkillNames.CSharpNav, resolved.ActiveSkills);
        Assert.Contains(BenchmarkSkillNames.CodeCondenser, resolved.ActiveSkills);
        Assert.Contains("general-debugging", resolved.ActiveSkills);
        Assert.DoesNotContain(BenchmarkSkillNames.MdFetch, resolved.ActiveSkills);
    }

    [Theory]
    [InlineData("python")]
    [InlineData("javascript")]
    [InlineData("typescript")]
    [InlineData("rust")]
    public void ConfigureWorkspaceSkills_NonCSharpLanguages_ExcludeCSharpStackSkillsWhilePreservingOtherSkills(string language)
    {
        var workspace = new Workspace();
        BenchmarkStagePolicyResolver.ConfigureWorkspaceSkills(workspace, enableStackSkills: true, language);

        var resolved = GlobalSkillsResolver.Resolve(
            workspace,
            projectConfig: null,
            [BenchmarkSkillNames.CSharpNav, BenchmarkSkillNames.CodeCondenser, BenchmarkSkillNames.MdFetch, "general-debugging"]);

        Assert.True(resolved.IsEnabled);
        Assert.DoesNotContain(BenchmarkSkillNames.CSharpNav, resolved.ActiveSkills);
        Assert.DoesNotContain(BenchmarkSkillNames.CodeCondenser, resolved.ActiveSkills);
        Assert.DoesNotContain(BenchmarkSkillNames.MdFetch, resolved.ActiveSkills);
        Assert.Contains("general-debugging", resolved.ActiveSkills);

        var policy = BenchmarkStagePolicyResolver.Resolve(BenchmarkProblemCategory.Implementation, enableStackSkills: true);
        Assert.Contains(CanonicalToolNames.LoadSkill, policy.PermittedTools);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ConfigureWorkspaceSkills_NullOrWhitespaceLanguage_DefaultsToCSharp(string? language)
    {
        var workspace = new Workspace();
        BenchmarkStagePolicyResolver.ConfigureWorkspaceSkills(workspace, enableStackSkills: true, language);

        var resolved = GlobalSkillsResolver.Resolve(
            workspace,
            projectConfig: null,
            [BenchmarkSkillNames.CSharpNav, BenchmarkSkillNames.CodeCondenser, BenchmarkSkillNames.MdFetch, "general-debugging"]);

        Assert.True(resolved.IsEnabled);
        Assert.Contains(BenchmarkSkillNames.CSharpNav, resolved.ActiveSkills);
        Assert.Contains(BenchmarkSkillNames.CodeCondenser, resolved.ActiveSkills);
        Assert.DoesNotContain(BenchmarkSkillNames.MdFetch, resolved.ActiveSkills);
    }

    [Fact]
    public void ConfigureWorkspaceSkills_UnknownLanguageAndDisabledNonCSharp_GateSkillsCorrectly()
    {
        var goWorkspace = new Workspace();
        BenchmarkStagePolicyResolver.ConfigureWorkspaceSkills(goWorkspace, enableStackSkills: true, "go");

        var goResolved = GlobalSkillsResolver.Resolve(
            goWorkspace,
            projectConfig: null,
            [BenchmarkSkillNames.CSharpNav, BenchmarkSkillNames.CodeCondenser, BenchmarkSkillNames.MdFetch, "general-debugging"]);

        Assert.True(goResolved.IsEnabled);
        Assert.DoesNotContain(BenchmarkSkillNames.CSharpNav, goResolved.ActiveSkills);
        Assert.DoesNotContain(BenchmarkSkillNames.CodeCondenser, goResolved.ActiveSkills);
        Assert.DoesNotContain(BenchmarkSkillNames.MdFetch, goResolved.ActiveSkills);
        Assert.Contains("general-debugging", goResolved.ActiveSkills);

        var disabledPyWorkspace = new Workspace();
        BenchmarkStagePolicyResolver.ConfigureWorkspaceSkills(disabledPyWorkspace, enableStackSkills: false, BenchmarkLanguages.Python);

        Assert.Equal(GlobalSkillsInjectionMode.ForceDisabled, disabledPyWorkspace.GlobalSkillsInjectionMode);
        Assert.Null(disabledPyWorkspace.DisabledGlobalSkillsJson);
    }

    [Fact]
    public void BuildProjectConfig_ProducesStackAccurateCommandsAndExecutionGuidelinesForAllStacks()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"ncf-stage-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var pyDir = Path.Combine(tempRoot, "py");
            var tsDir = Path.Combine(tempRoot, "ts");
            var jsDir = Path.Combine(tempRoot, "js");
            var rsDir = Path.Combine(tempRoot, "rs");
            var csRootDir = Path.Combine(tempRoot, "cs-root");
            var csSubDir = Path.Combine(tempRoot, "cs-sub");
            Directory.CreateDirectory(pyDir);
            Directory.CreateDirectory(tsDir);
            Directory.CreateDirectory(jsDir);
            Directory.CreateDirectory(rsDir);
            Directory.CreateDirectory(csRootDir);
            Directory.CreateDirectory(Path.Combine(csSubDir, "src"));

            File.WriteAllText(Path.Combine(tsDir, "package.json"), """{"devDependencies":{"vitest":"^2.0.0"}}""");
            File.WriteAllText(Path.Combine(tsDir, "vitest.unit.config.ts"), "export default {}");
            File.WriteAllText(Path.Combine(jsDir, "package.json"), """{"scripts":{"test:mocha":"mocha"},"devDependencies":{"mocha":"^10.0.0"}}""");
            File.WriteAllText(Path.Combine(csRootDir, "App.sln"), "");
            File.WriteAllText(Path.Combine(csSubDir, "src", "Lib.sln"), "");

            var agentsMdService = new AgentsMdService();

            var pyConfig = BenchmarkStagePolicyResolver.BuildProjectConfig(
                new BenchmarkInstance { InstanceId = "py-1", Language = BenchmarkLanguages.Python, Repo = "pallets/flask", BaseCommit = "abc", ProblemStatement = "Fix" },
                pyDir,
                ExecutionMode.ContainerSandbox);
            Assert.Equal(CodeStack.Python, pyConfig.CodeStack);
            Assert.Equal(CodeStackPresets.Python.BuildCommand, pyConfig.BuildCommand);
            Assert.Equal(CodeStackPresets.Python.TestCommand, pyConfig.TestCommand);
            Assert.DoesNotContain("--junitxml", pyConfig.TestCommand, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".ncf-bench-results", pyConfig.TestCommand, StringComparison.OrdinalIgnoreCase);
            Assert.True(pyConfig.BuildTimeoutSeconds > 0 && pyConfig.TestTimeoutSeconds > 0);
            var pyGuidelines = agentsMdService.FormatExecutionGuidelines(pyConfig, new Workspace());
            Assert.DoesNotContain("dotnet build", pyGuidelines, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dotnet test", pyGuidelines, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("--no-build", pyGuidelines, StringComparison.OrdinalIgnoreCase);

            var tsConfig = BenchmarkStagePolicyResolver.BuildProjectConfig(
                new BenchmarkInstance { InstanceId = "ts-1", Language = BenchmarkLanguages.TypeScript, Repo = "vuejs/core", BaseCommit = "abc", ProblemStatement = "Fix" },
                tsDir,
                ExecutionMode.ContainerSandbox);
            Assert.Equal(CodeStack.Node, tsConfig.CodeStack);
            Assert.Null(tsConfig.BuildCommand);
            Assert.Equal("npx vitest run -c vitest.unit.config.ts", tsConfig.TestCommand);
            Assert.DoesNotContain("--reporter=junit", tsConfig.TestCommand, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".ncf-bench-results", tsConfig.TestCommand, StringComparison.OrdinalIgnoreCase);
            var tsGuidelines = agentsMdService.FormatExecutionGuidelines(tsConfig, new Workspace());
            Assert.DoesNotContain("dotnet build", tsGuidelines, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dotnet test", tsGuidelines, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Build Command:", tsGuidelines, StringComparison.Ordinal);
            Assert.DoesNotContain("Incremental Compilation Invariant", tsGuidelines, StringComparison.Ordinal);

            var jsConfig = BenchmarkStagePolicyResolver.BuildProjectConfig(
                new BenchmarkInstance { InstanceId = "js-1", Language = BenchmarkLanguages.JavaScript, Repo = "axios/axios", BaseCommit = "abc", ProblemStatement = "Fix" },
                jsDir,
                ExecutionMode.ContainerSandbox);
            Assert.Equal(CodeStack.Node, jsConfig.CodeStack);
            Assert.Null(jsConfig.BuildCommand);
            Assert.Equal("npm run test:mocha", jsConfig.TestCommand);
            Assert.DoesNotContain("mocha-junit-reporter", jsConfig.TestCommand, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".ncf-bench-results", jsConfig.TestCommand, StringComparison.OrdinalIgnoreCase);

            var rsConfig = BenchmarkStagePolicyResolver.BuildProjectConfig(
                new BenchmarkInstance { InstanceId = "rs-1", Language = BenchmarkLanguages.Rust, Repo = "burntsushi/ripgrep", BaseCommit = "abc", ProblemStatement = "Fix" },
                rsDir,
                ExecutionMode.ContainerSandbox);
            Assert.Equal(CodeStack.Rust, rsConfig.CodeStack);
            Assert.Equal("cargo build", rsConfig.BuildCommand);
            Assert.Contains("cargo test", rsConfig.TestCommand);
            var rsGuidelines = agentsMdService.FormatExecutionGuidelines(rsConfig, new Workspace());
            Assert.DoesNotContain("dotnet", rsGuidelines, StringComparison.OrdinalIgnoreCase);

            var csRootConfig = BenchmarkStagePolicyResolver.BuildProjectConfig(
                new BenchmarkInstance { InstanceId = "cs-1", Language = BenchmarkLanguages.CSharp, Repo = "serilog/serilog", BaseCommit = "abc", ProblemStatement = "Fix" },
                csRootDir,
                ExecutionMode.ContainerSandbox);
            Assert.Equal(CodeStack.DotNet, csRootConfig.CodeStack);
            Assert.Equal("dotnet build", csRootConfig.BuildCommand);
            Assert.Equal("dotnet test --no-build", csRootConfig.TestCommand);
            Assert.DoesNotContain("--logger trx", csRootConfig.TestCommand, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".ncf-bench-results", csRootConfig.TestCommand, StringComparison.OrdinalIgnoreCase);

            var csSubConfig = BenchmarkStagePolicyResolver.BuildProjectConfig(
                new BenchmarkInstance { InstanceId = "cs-2", Language = BenchmarkLanguages.CSharp, Repo = "spectreconsole/spectre.console", BaseCommit = "abc", ProblemStatement = "Fix" },
                csSubDir,
                ExecutionMode.ContainerSandbox);
            Assert.Equal("dotnet build src/Lib.sln", csSubConfig.BuildCommand);
            Assert.Equal("dotnet test src/Lib.sln --no-build", csSubConfig.TestCommand);
            Assert.DoesNotContain("--logger trx", csSubConfig.TestCommand, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".ncf-bench-results", csSubConfig.TestCommand, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void BuildProjectConfig_HandlesJestMultiCsprojAliasesAndCustomMochaWithoutVerifierArtifacts()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"ncf-stage-policy-ext-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var jestDir = Path.Combine(tempRoot, "jest");
            var multiCsDir = Path.Combine(tempRoot, "multi-cs");
            var csSubDir = Path.Combine(tempRoot, "cs-sub");
            var mochaDir = Path.Combine(tempRoot, "mocha");
            Directory.CreateDirectory(jestDir);
            Directory.CreateDirectory(Path.Combine(multiCsDir, "tests", "A"));
            Directory.CreateDirectory(Path.Combine(multiCsDir, "tests", "B"));
            Directory.CreateDirectory(Path.Combine(csSubDir, "src"));
            Directory.CreateDirectory(mochaDir);

            File.WriteAllText(Path.Combine(jestDir, "package.json"), """{"devDependencies":{"jest":"^29.0.0"}}""");
            File.WriteAllText(Path.Combine(multiCsDir, "tests", "A", "A.Tests.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(multiCsDir, "tests", "B", "B.Tests.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(csSubDir, "src", "Lib.sln"), "");

            var jestConfig = BenchmarkStagePolicyResolver.BuildProjectConfig(
                new BenchmarkInstance { InstanceId = "jest-1", Language = BenchmarkLanguages.TypeScript, Repo = "owner/jest-app", BaseCommit = "abc", ProblemStatement = "Fix" },
                jestDir,
                ExecutionMode.ContainerSandbox);
            Assert.Equal("npx jest --ci", jestConfig.TestCommand);
            Assert.DoesNotContain("jest-junit", jestConfig.TestCommand, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".ncf-bench-results", jestConfig.TestCommand, StringComparison.OrdinalIgnoreCase);

            var multiCsConfig = BenchmarkStagePolicyResolver.BuildProjectConfig(
                new BenchmarkInstance { InstanceId = "cs-multi", Language = BenchmarkLanguages.CSharp, Repo = "owner/multi", BaseCommit = "abc", ProblemStatement = "Fix" },
                multiCsDir,
                ExecutionMode.ContainerSandbox);
            Assert.Equal("dotnet build tests/A/A.Tests.csproj && dotnet build tests/B/B.Tests.csproj", multiCsConfig.BuildCommand);
            Assert.Equal("dotnet test tests/A/A.Tests.csproj --no-build && dotnet test tests/B/B.Tests.csproj --no-build", multiCsConfig.TestCommand);

            foreach (var alias in new[] { "c#", "" })
            {
                var aliasConfig = BenchmarkStagePolicyResolver.BuildProjectConfig(
                    new BenchmarkInstance { InstanceId = $"cs-alias-{alias}", Language = alias, Repo = "owner/lib", BaseCommit = "abc", ProblemStatement = "Fix" },
                    csSubDir,
                    ExecutionMode.ContainerSandbox);
                Assert.Equal(CodeStack.DotNet, aliasConfig.CodeStack);
                Assert.Equal("dotnet build src/Lib.sln", aliasConfig.BuildCommand);
                Assert.Equal("dotnet test src/Lib.sln --no-build", aliasConfig.TestCommand);
            }

            var customMochaConfig = BenchmarkStagePolicyResolver.BuildProjectConfig(
                new BenchmarkInstance
                {
                    InstanceId = "mocha-custom",
                    Language = BenchmarkLanguages.JavaScript,
                    Repo = "owner/mocha-app",
                    BaseCommit = "abc",
                    ProblemStatement = "Fix",
                    CustomTestCommand = "npm run test:mocha"
                },
                mochaDir,
                ExecutionMode.ContainerSandbox);
            Assert.Equal("npm run test:mocha", customMochaConfig.TestCommand);
            Assert.DoesNotContain("mocha-junit-reporter", customMochaConfig.TestCommand, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".ncf-bench-results", customMochaConfig.TestCommand, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Resolve_AdaptsDirectivesPerCategoryWithoutMisleadingDocsTasksOrDotNetFallbacks()
    {
        var implPolicy = BenchmarkStagePolicyResolver.Resolve(BenchmarkProblemCategory.Implementation, enableStackSkills: false);
        Assert.DoesNotContain("docs/tasks/", implPolicy.StageDirective, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dotnet build", implPolicy.StageDirective, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dotnet test --no-build", implPolicy.StageDirective, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<execution_guidelines>", implPolicy.StageDirective, StringComparison.Ordinal);

        var crPolicy = BenchmarkStagePolicyResolver.Resolve(BenchmarkProblemCategory.CodeReview, enableStackSkills: false);
        Assert.DoesNotContain("docs/tasks/", crPolicy.StageDirective, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<request_changes>", crPolicy.StageDirective, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("</request_changes>", crPolicy.StageDirective, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("request_changes(feedback, reason)", crPolicy.StageDirective, StringComparison.Ordinal);
        Assert.Contains("Do NOT make unsolicited cosmetic, formatting, or line-wrapping edits", crPolicy.StageDirective, StringComparison.Ordinal);
        Assert.Contains("Required unit or integration tests specified in the task are missing", crPolicy.StageDirective, StringComparison.Ordinal);

        var trPolicy = BenchmarkStagePolicyResolver.Resolve(BenchmarkProblemCategory.TaskReview, enableStackSkills: false);
        Assert.Contains("docs/tasks/", trPolicy.StageDirective, StringComparison.Ordinal);
        Assert.Contains("'standard'", trPolicy.StageDirective, StringComparison.Ordinal);
        Assert.Contains("'difficult'", trPolicy.StageDirective, StringComparison.Ordinal);
        Assert.Contains("'expert'", trPolicy.StageDirective, StringComparison.Ordinal);
        Assert.Contains("'ask'", trPolicy.StageDirective, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSandboxEnvironmentGuidance_ReturnsNodeFallbackGuidanceOnlyForJsAndTs()
    {
        var tsGuidance = BenchmarkStagePolicyResolver.GetSandboxEnvironmentGuidance(BenchmarkLanguages.TypeScript);
        var jsGuidance = BenchmarkStagePolicyResolver.GetSandboxEnvironmentGuidance(BenchmarkLanguages.JavaScript);

        Assert.NotNull(tsGuidance);
        Assert.Contains("--ignore-scripts", tsGuidance, StringComparison.Ordinal);
        Assert.Contains("/tmp/ncf-npm-global", tsGuidance, StringComparison.Ordinal);
        Assert.Equal(tsGuidance, jsGuidance);

        Assert.Null(BenchmarkStagePolicyResolver.GetSandboxEnvironmentGuidance(BenchmarkLanguages.CSharp));
        Assert.Null(BenchmarkStagePolicyResolver.GetSandboxEnvironmentGuidance(BenchmarkLanguages.Python));
        Assert.Null(BenchmarkStagePolicyResolver.GetSandboxEnvironmentGuidance(BenchmarkLanguages.Rust));
    }

    [Fact]
    public void BuildProjectConfig_PrefersMainSolutionOverAnalyzerSolutionInSubdirectory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"ncf-spectre-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "src"));
        try
        {
            File.WriteAllText(Path.Combine(tempRoot, "src", "Spectre.Console.Analyzer.sln"),
                """
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Spectre.Console.Analyzer", "Spectre.Console.Analyzer\Spectre.Console.Analyzer.csproj", "{11111111-1111-1111-1111-111111111111}"
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Spectre.Console.Analyzer.Tests", "Tests\Spectre.Console.Analyzer.Tests\Spectre.Console.Analyzer.Tests.csproj", "{22222222-2222-2222-2222-222222222222}"
                """);
            File.WriteAllText(Path.Combine(tempRoot, "src", "Spectre.Console.sln"),
                """
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Spectre.Console", "Spectre.Console\Spectre.Console.csproj", "{33333333-3333-3333-3333-333333333333}"
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Spectre.Console.Tests", "Tests\Spectre.Console.Tests\Spectre.Console.Tests.csproj", "{44444444-4444-4444-4444-444444444444}"
                """);

            var config = BenchmarkStagePolicyResolver.BuildProjectConfig(
                new BenchmarkInstance { InstanceId = "spectre-1", Language = BenchmarkLanguages.CSharp, Repo = "spectreconsole/spectre.console", BaseCommit = "abc", ProblemStatement = "Fix" },
                tempRoot,
                ExecutionMode.ContainerSandbox);

            Assert.Equal("dotnet build src/Spectre.Console.sln", config.BuildCommand);
            Assert.Equal("dotnet test src/Spectre.Console.sln --no-build", config.TestCommand);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void BuildProjectConfig_NodeMonorepo_SelectsPackageOnlyBuildScriptMatchingLockfileAndFallsBackCleanly()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"ncf-node-build-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var yarnDir = Path.Combine(tempRoot, "docusaurus");
            var pnpmDir = Path.Combine(tempRoot, "pnpm-mono");
            var npmDir = Path.Combine(tempRoot, "npm-mono");
            var malformedDir = Path.Combine(tempRoot, "malformed");
            var missingDir = Path.Combine(tempRoot, "missing");
            Directory.CreateDirectory(yarnDir);
            Directory.CreateDirectory(pnpmDir);
            Directory.CreateDirectory(npmDir);
            Directory.CreateDirectory(malformedDir);
            Directory.CreateDirectory(missingDir);

            File.WriteAllText(Path.Combine(yarnDir, "package.json"), """{"scripts":{"build":"yarn build:packages && yarn build:website","build:packages":"lerna run build:packages"}}""");
            File.WriteAllText(Path.Combine(yarnDir, "yarn.lock"), "");

            File.WriteAllText(Path.Combine(pnpmDir, "package.json"), """{"scripts":{"build":"turbo build","build:packages":"pnpm -r --filter ./packages/* build"}}""");
            File.WriteAllText(Path.Combine(pnpmDir, "pnpm-lock.yaml"), "");

            File.WriteAllText(Path.Combine(npmDir, "package.json"), """{"scripts":{"build":"npm run build:packages && npm run build:docs","build:packages":"tsc -b packages"}}""");

            File.WriteAllText(Path.Combine(malformedDir, "package.json"), "{ invalid json");

            var yarnConfig = BenchmarkStagePolicyResolver.BuildProjectConfig(
                new BenchmarkInstance { InstanceId = "docusaurus", Language = BenchmarkLanguages.TypeScript, Repo = "facebook/docusaurus", BaseCommit = "abc", ProblemStatement = "Fix" },
                yarnDir,
                ExecutionMode.ContainerSandbox);
            Assert.Equal("yarn build:packages", yarnConfig.BuildCommand);

            var pnpmConfig = BenchmarkStagePolicyResolver.BuildProjectConfig(
                new BenchmarkInstance { InstanceId = "pnpm-1", Language = BenchmarkLanguages.TypeScript, Repo = "owner/pnpm", BaseCommit = "abc", ProblemStatement = "Fix" },
                pnpmDir,
                ExecutionMode.ContainerSandbox);
            Assert.Equal("pnpm run build:packages", pnpmConfig.BuildCommand);

            var npmConfig = BenchmarkStagePolicyResolver.BuildProjectConfig(
                new BenchmarkInstance { InstanceId = "npm-1", Language = BenchmarkLanguages.JavaScript, Repo = "owner/npm", BaseCommit = "abc", ProblemStatement = "Fix" },
                npmDir,
                ExecutionMode.ContainerSandbox);
            Assert.Equal("npm run build:packages", npmConfig.BuildCommand);

            var malformedConfig = BenchmarkStagePolicyResolver.BuildProjectConfig(
                new BenchmarkInstance { InstanceId = "bad-1", Language = BenchmarkLanguages.TypeScript, Repo = "owner/bad", BaseCommit = "abc", ProblemStatement = "Fix" },
                malformedDir,
                ExecutionMode.ContainerSandbox);
            Assert.Null(malformedConfig.BuildCommand);

            var missingConfig = BenchmarkStagePolicyResolver.BuildProjectConfig(
                new BenchmarkInstance { InstanceId = "miss-1", Language = BenchmarkLanguages.TypeScript, Repo = "owner/miss", BaseCommit = "abc", ProblemStatement = "Fix" },
                missingDir,
                ExecutionMode.ContainerSandbox);
            Assert.Null(missingConfig.BuildCommand);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Step1163_GetSandboxEnvironmentGuidance_PythonWithPipConstraints_SurfacesConstraintsAndEnvVar()
    {
        var unpinned = BenchmarkStagePolicyResolver.GetSandboxEnvironmentGuidance(BenchmarkLanguages.Python, null);
        Assert.Null(unpinned);

        var pinned = BenchmarkStagePolicyResolver.GetSandboxEnvironmentGuidance(BenchmarkLanguages.Python, ["Werkzeug<3.0"]);
        Assert.NotNull(pinned);
        Assert.Contains("Werkzeug<3.0", pinned);
        Assert.Contains("PIP_CONSTRAINT=/tmp/ncf-pip-constraints.txt", pinned);
        Assert.Contains("/tmp/ncf-pip-constraints.txt", pinned);
    }
}
