using Moq;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Benchmark.Cli.Verification;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Benchmark.Cli.Tests.Verification;

public sealed class BenchmarkToolchainPreflightValidatorTests
{
    private static BenchmarkInstance CreateRustInstance(string? customImage = null) => new()
    {
        InstanceId = "burntsushi__ripgrep-2576",
        Category = BenchmarkProblemCategory.Implementation,
        Language = BenchmarkLanguages.Rust,
        Repo = "burntsushi/ripgrep",
        BaseCommit = "abc123",
        ProblemStatement = "Fix ripgrep bug",
        CustomContainerImage = customImage
    };

    private static BenchmarkToolchainPreflightValidator CreateValidator(
        HostSdkDiscoveryDetails details,
        IBenchmarkCustomImageBuilder? customImageBuilder = null)
    {
        var sdkService = new Mock<IHostSdkDiscoveryService>();
        sdkService
            .Setup(s => s.DiscoverSdkDetailsAsync("Ubuntu-24.04", It.IsAny<CancellationToken>()))
            .ReturnsAsync(details);

        var containerFactory = new Mock<IContainerEnvironmentFactory>();
        containerFactory.SetupGet(f => f.DefaultWslDistribution).Returns("Ubuntu-24.04");

        return new BenchmarkToolchainPreflightValidator(
            sdkService.Object,
            containerFactory.Object,
            customImageBuilder ?? Mock.Of<IBenchmarkCustomImageBuilder>());
    }

    [Fact]
    public async Task ValidateAsync_WhenCargoAndRustcMissingOnHost_FailsFastWithActionableError()
    {
        var validator = CreateValidator(new HostSdkDiscoveryDetails(
            MountPaths: ["/usr/share/dotnet"],
            EnvironmentVariables: new Dictionary<string, string>(),
            CommandPaths: new Dictionary<string, string>()));

        var error = await validator.ValidateAsync(
            CreateRustInstance(),
            BenchmarkProblemCategory.Implementation,
            ExecutionMode.ContainerSandbox);

        Assert.NotNull(error);
        Assert.Contains("burntsushi__ripgrep-2576", error);
        Assert.Contains("rustup", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("custom_container_image", error);
    }

    [Fact]
    public async Task ValidateAsync_WhenCargoAndRustcOnlyInExcludedSystemDir_FailsFast()
    {
        var validator = CreateValidator(new HostSdkDiscoveryDetails(
            MountPaths: ["/usr/share/dotnet"],
            EnvironmentVariables: new Dictionary<string, string>(),
            CommandPaths: new Dictionary<string, string>
            {
                ["cargo"] = "/usr/bin/cargo",
                ["rustc"] = "/usr/bin/rustc"
            }));

        var error = await validator.ValidateAsync(
            CreateRustInstance(),
            BenchmarkProblemCategory.CodeReview,
            ExecutionMode.ContainerSandbox);

        Assert.NotNull(error);
        Assert.Contains("/usr/bin/cargo", error);
        Assert.Contains("excluded system directory", error);
        Assert.Contains("rustup", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_WhenCargoUsesCargoProxyWithoutMountableRustup_FailsFast()
    {
        var validator = CreateValidator(new HostSdkDiscoveryDetails(
            MountPaths: ["/home/user/.cargo"],
            EnvironmentVariables: new Dictionary<string, string>(),
            CommandPaths: new Dictionary<string, string>
            {
                ["cargo"] = "/home/user/.cargo/bin/cargo",
                ["rustc"] = "/home/user/.cargo/bin/rustc"
            }));

        var error = await validator.ValidateAsync(
            CreateRustInstance(),
            BenchmarkProblemCategory.Implementation,
            ExecutionMode.ContainerSandbox);

        Assert.NotNull(error);
        Assert.Contains("RUSTUP_HOME", error);
    }

    [Fact]
    public async Task ValidateAsync_WhenMountableCargoAndRustupPresent_Passes()
    {
        var validator = CreateValidator(new HostSdkDiscoveryDetails(
            MountPaths: ["/home/user/.cargo", "/home/user/.rustup"],
            EnvironmentVariables: new Dictionary<string, string> { ["RUSTUP_HOME"] = "/home/user/.rustup" },
            CommandPaths: new Dictionary<string, string>
            {
                ["cargo"] = "/home/user/.cargo/bin/cargo",
                ["rustc"] = "/home/user/.cargo/bin/rustc"
            }));

        var error = await validator.ValidateAsync(
            CreateRustInstance(),
            BenchmarkProblemCategory.Implementation,
            ExecutionMode.ContainerSandbox);

        Assert.Null(error);
    }

    [Theory]
    [InlineData(BenchmarkProblemCategory.TaskReview, ExecutionMode.ContainerSandbox, BenchmarkLanguages.Rust, null)]
    [InlineData(BenchmarkProblemCategory.Implementation, ExecutionMode.BareHost, BenchmarkLanguages.Rust, null)]
    [InlineData(BenchmarkProblemCategory.Implementation, ExecutionMode.ContainerSandbox, BenchmarkLanguages.CSharp, null)]
    [InlineData(BenchmarkProblemCategory.Implementation, ExecutionMode.ContainerSandbox, BenchmarkLanguages.Rust, "rust:1.80")]
    public async Task ValidateAsync_SkipsTaskReviewNonContainerNonRustAndCustomImageInstances(
        BenchmarkProblemCategory category,
        ExecutionMode mode,
        string language,
        string? customImage)
    {
        var sdkService = new Mock<IHostSdkDiscoveryService>(MockBehavior.Strict);
        var containerFactory = new Mock<IContainerEnvironmentFactory>();
        var validator = new BenchmarkToolchainPreflightValidator(
            sdkService.Object,
            containerFactory.Object,
            Mock.Of<IBenchmarkCustomImageBuilder>());

        var instance = new BenchmarkInstance
        {
            InstanceId = "skip-test",
            Category = category,
            Language = language,
            Repo = "owner/repo",
            BaseCommit = "abc123",
            ProblemStatement = "Task",
            CustomContainerImage = customImage
        };

        var error = await validator.ValidateAsync(instance, category, mode);
        Assert.Null(error);
        sdkService.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("ncf-agent-base:v6")]
    [InlineData("ncf-agent-base:v5")]
    public async Task ValidateAsync_WhenCustomContainerImageIsManagedBaseImage_StillRunsRustPreflight(string managedImage)
    {
        var validator = CreateValidator(new HostSdkDiscoveryDetails(
            MountPaths: ["/usr/share/dotnet"],
            EnvironmentVariables: new Dictionary<string, string>(),
            CommandPaths: new Dictionary<string, string>()));

        var error = await validator.ValidateAsync(
            CreateRustInstance(managedImage),
            BenchmarkProblemCategory.Implementation,
            ExecutionMode.ContainerSandbox);

        Assert.NotNull(error);
        Assert.Contains("rustup", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_WhenNonDotCargoHomeUsedWithoutMountableRustup_FailsFast()
    {
        var validator = CreateValidator(new HostSdkDiscoveryDetails(
            MountPaths: ["/opt/rust/cargo"],
            EnvironmentVariables: new Dictionary<string, string>
            {
                ["CARGO_HOME"] = "/opt/rust/cargo"
            },
            CommandPaths: new Dictionary<string, string>
            {
                ["cargo"] = "/opt/rust/cargo/bin/cargo",
                ["rustc"] = "/opt/rust/cargo/bin/rustc"
            }));

        var error = await validator.ValidateAsync(
            CreateRustInstance(),
            BenchmarkProblemCategory.Implementation,
            ExecutionMode.ContainerSandbox);

        Assert.NotNull(error);
        Assert.Contains("RUSTUP_HOME", error);
    }

    [Fact]
    public async Task ValidateAsync_WhenCustomRustupHomeLacksRustupSuffix_FailsFastToMatchRuntimeForwarding()
    {
        var validator = CreateValidator(new HostSdkDiscoveryDetails(
            MountPaths: ["/home/user/.cargo", "/opt/rust/toolchains"],
            EnvironmentVariables: new Dictionary<string, string>
            {
                ["RUSTUP_HOME"] = "/opt/rust/toolchains"
            },
            CommandPaths: new Dictionary<string, string>
            {
                ["cargo"] = "/home/user/.cargo/bin/cargo",
                ["rustc"] = "/home/user/.cargo/bin/rustc"
            }));

        var error = await validator.ValidateAsync(
            CreateRustInstance(),
            BenchmarkProblemCategory.Implementation,
            ExecutionMode.ContainerSandbox);

        Assert.NotNull(error);
        Assert.Contains("RUSTUP_HOME", error);
    }

    [Fact]
    public async Task ValidateAsync_WhenDistroPackagedRustupToolchainPresent_Passes()
    {
        var validator = CreateValidator(new HostSdkDiscoveryDetails(
            MountPaths: ["/etc/ssl/certs", "/home/user/.rustup"],
            EnvironmentVariables: new Dictionary<string, string>(),
            CommandPaths: new Dictionary<string, string>
            {
                ["cargo"] = "/home/user/.rustup/toolchains/stable-x86_64-unknown-linux-gnu/bin/cargo",
                ["rustc"] = "/home/user/.rustup/toolchains/stable-x86_64-unknown-linux-gnu/bin/rustc"
            }));

        var error = await validator.ValidateAsync(
            CreateRustInstance(),
            BenchmarkProblemCategory.Implementation,
            ExecutionMode.ContainerSandbox);

        Assert.Null(error);
    }

    [Fact]
    public async Task ValidateAsync_WhenDistroRustupHasNoActiveToolchain_FailsFast()
    {
        var validator = CreateValidator(new HostSdkDiscoveryDetails(
            MountPaths: ["/etc/ssl/certs", "/home/user/.rustup"],
            EnvironmentVariables: new Dictionary<string, string>(),
            CommandPaths: new Dictionary<string, string>
            {
                ["cargo"] = "/usr/bin/cargo",
                ["rustc"] = "/usr/bin/rustc"
            }));

        var error = await validator.ValidateAsync(
            CreateRustInstance(),
            BenchmarkProblemCategory.Implementation,
            ExecutionMode.ContainerSandbox);

        Assert.NotNull(error);
        Assert.Contains("excluded system directory", error);
        Assert.Contains("rustup default stable", error);
    }

    [Fact]
    public async Task ValidateAsync_WhenToolchainPathUsedWithoutMountableRustupHome_FailsFast()
    {
        var validator = CreateValidator(new HostSdkDiscoveryDetails(
            MountPaths: ["/opt/rust/custom-toolchains"],
            EnvironmentVariables: new Dictionary<string, string>(),
            CommandPaths: new Dictionary<string, string>
            {
                ["cargo"] = "/opt/rust/custom-toolchains/toolchains/stable/bin/cargo",
                ["rustc"] = "/opt/rust/custom-toolchains/toolchains/stable/bin/rustc"
            }));

        var error = await validator.ValidateAsync(
            CreateRustInstance(),
            BenchmarkProblemCategory.Implementation,
            ExecutionMode.ContainerSandbox);

        Assert.NotNull(error);
        Assert.Contains("RUSTUP_HOME", error);
    }

    [Theory]
    [InlineData(BenchmarkProblemCategory.Implementation)]
    [InlineData(BenchmarkProblemCategory.CodeReview)]
    [InlineData(BenchmarkProblemCategory.TaskReview)]
    public async Task ValidateAsync_WhenCustomImageIsManagedPythonChildImageInContainerSandbox_EnsuresImageAcrossAllCategories(
        BenchmarkProblemCategory category)
    {
        var imageBuilder = new Mock<IBenchmarkCustomImageBuilder>();
        imageBuilder.Setup(b => b.IsManagedBenchmarkImage(BenchmarkContainerImages.Python311ChildImage)).Returns(true);
        imageBuilder.Setup(b => b.EnsureImageAsync(BenchmarkContainerImages.Python311ChildImage, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sdkService = new Mock<IHostSdkDiscoveryService>(MockBehavior.Strict);
        var validator = new BenchmarkToolchainPreflightValidator(
            sdkService.Object,
            new Mock<IContainerEnvironmentFactory>().Object,
            imageBuilder.Object);

        var instance = new BenchmarkInstance
        {
            InstanceId = $"py-{category}",
            Category = category,
            Language = BenchmarkLanguages.Python,
            Repo = "psf/requests",
            BaseCommit = "abc123",
            ProblemStatement = "Fix python",
            CustomContainerImage = BenchmarkContainerImages.Python311ChildImage
        };

        var error = await validator.ValidateAsync(instance, category, ExecutionMode.ContainerSandbox);
        Assert.Null(error);
        imageBuilder.Verify(b => b.EnsureImageAsync(BenchmarkContainerImages.Python311ChildImage, It.IsAny<CancellationToken>()), Times.Once);

        // BareHost mode must skip container image provisioning
        var bareError = await validator.ValidateAsync(instance, category, ExecutionMode.BareHost);
        Assert.Null(bareError);
        imageBuilder.Verify(b => b.EnsureImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ValidateAsync_WhenCustomImageBuildFails_ReturnsFailFastErrorMessage()
    {
        var imageBuilder = new Mock<IBenchmarkCustomImageBuilder>();
        imageBuilder.Setup(b => b.IsManagedBenchmarkImage(BenchmarkContainerImages.Python311ChildImage)).Returns(true);
        imageBuilder.Setup(b => b.EnsureImageAsync(BenchmarkContainerImages.Python311ChildImage, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BenchmarkException("Failed to build benchmark custom image 'ncf-bench-python-3.11:v1' (exit code 1): apt-get failed"));

        var validator = new BenchmarkToolchainPreflightValidator(
            new Mock<IHostSdkDiscoveryService>(MockBehavior.Strict).Object,
            new Mock<IContainerEnvironmentFactory>().Object,
            imageBuilder.Object);

        var instance = new BenchmarkInstance
        {
            InstanceId = "py-build-fail",
            Category = BenchmarkProblemCategory.Implementation,
            Language = BenchmarkLanguages.Python,
            Repo = "psf/requests",
            BaseCommit = "abc123",
            ProblemStatement = "Fix python",
            CustomContainerImage = BenchmarkContainerImages.Python311ChildImage
        };

        var error = await validator.ValidateAsync(instance, BenchmarkProblemCategory.Implementation, ExecutionMode.ContainerSandbox);
        Assert.NotNull(error);
        Assert.Contains("ncf-bench-python-3.11:v1", error);
        Assert.Contains("apt-get failed", error);
    }
}
