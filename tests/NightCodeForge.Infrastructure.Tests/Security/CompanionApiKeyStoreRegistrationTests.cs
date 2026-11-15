using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using NightCodeForge.Core.Services;
using Pmad.LocalApi.Security;

namespace NightCodeForge.Infrastructure.Tests.Security;

public sealed class CompanionApiKeyStoreRegistrationTests : IDisposable
{
    private readonly string _tempRoot;

    public CompanionApiKeyStoreRegistrationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"ncf-companion-key-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of temp directory on test teardown.
            }
        }
    }

    [Fact]
    public void AddNightCodeForgeCoreAndInfrastructure_RegistersKeyStoreAndDraftStoreAsSingletons()
    {
        var hostEnvMock = new Mock<IHostEnvironment>();
        hostEnvMock.SetupGet(e => e.ContentRootPath).Returns(_tempRoot);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(hostEnvMock.Object);
        services.AddNightCodeForgeCoreAndInfrastructure($"Data Source={Path.Combine(_tempRoot, "test.db")};");

        using var provider = services.BuildServiceProvider();

        var protector1 = provider.GetRequiredService<IKeyProtector>();
        var protector2 = provider.GetRequiredService<IKeyProtector>();
        Assert.Same(protector1, protector2);

        var store1 = provider.GetRequiredService<IApiKeyStore>();
        var store2 = provider.GetRequiredService<IApiKeyStore>();
        Assert.Same(store1, store2);

        var draftStore1 = provider.GetRequiredService<ICompanionTriageDraftStore>();
        var draftStore2 = provider.GetRequiredService<ICompanionTriageDraftStore>();
        Assert.Same(draftStore1, draftStore2);

        var expectedKeyPath = Path.Combine(_tempRoot, "config", "default-key.dat");
        Assert.Equal(expectedKeyPath, store1.DefaultKeyFilePath);

        var defaultKey = store1.GetDefaultKey();
        Assert.False(string.IsNullOrWhiteSpace(defaultKey));
        Assert.True(File.Exists(expectedKeyPath));
        Assert.True(store1.ValidateKey(defaultKey));
        Assert.False(store1.ValidateKey("invalid-key-value"));
        Assert.False(store1.ValidateKey(null));
    }

    [Fact]
    public void CompanionTriageDraftStore_CreatesRetrievesAndPrunesExpiredDraftsAfter24Hours()
    {
        var timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
        var store = new CompanionTriageDraftStore(timeProvider);

        var draft1 = store.CreateDraft(
            workspaceId: 7,
            title: "First Assay Report",
            reportMarkdown: "# Findings\nCrash on empty input.",
            baseBranch: "main",
            sessionDirectoryPath: @"C:\sessions\one");

        Assert.Equal(12, draft1.DraftId.Length);
        Assert.Equal(7, draft1.WorkspaceId);
        Assert.Equal("First Assay Report", draft1.Title);
        Assert.Equal("# Findings\nCrash on empty input.", draft1.ReportMarkdown);
        Assert.Equal("main", draft1.BaseBranch);
        Assert.Equal(@"C:\sessions\one", draft1.SessionDirectoryPath);
        Assert.Equal(timeProvider.GetUtcNow(), draft1.CreatedAt);

        Assert.Same(draft1, store.GetDraft($" {draft1.DraftId} "));
        Assert.Null(store.GetDraft(null));
        Assert.Null(store.GetDraft("   "));
        Assert.Null(store.GetDraft("nonexistent00"));

        // Still valid at exactly 24 hours
        timeProvider.Advance(TimeSpan.FromHours(24));
        Assert.Same(draft1, store.GetDraft(draft1.DraftId));

        // Expired once past 24 hours
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(store.GetDraft(draft1.DraftId));

        // Creating a new draft prunes expired entries while keeping the fresh draft
        var draft2 = store.CreateDraft(7, "Fresh Report", "Body", "develop");
        timeProvider.Advance(TimeSpan.FromHours(24).Add(TimeSpan.FromSeconds(1)));
        var draft3 = store.CreateDraft(7, "Third Report", "Body 3", "main");

        Assert.Null(store.GetDraft(draft2.DraftId));
        Assert.Same(draft3, store.GetDraft(draft3.DraftId));
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public MutableTimeProvider(DateTimeOffset initialUtcNow)
        {
            _utcNow = initialUtcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration)
        {
            _utcNow = _utcNow.Add(duration);
        }
    }
}
