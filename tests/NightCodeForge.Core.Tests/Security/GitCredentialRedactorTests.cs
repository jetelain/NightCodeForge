using NightCodeForge.Core.Security;
using Xunit;

namespace NightCodeForge.Core.Tests.Security;

public class GitCredentialRedactorTests
{
    [Fact]
    public void Redact_SingleAndDoubleQuotedExtraHeaders_RedactsHeaderValues()
    {
        var commandLine =
            "git -c 'http.extraHeader=AUTHORIZATION: basic dXNlcjpzZWNyZXQ=' -c \"http.extraHeader=AUTHORIZATION: Bearer secret-tok\" fetch origin";

        var redacted = GitCredentialRedactor.Redact(commandLine);

        Assert.DoesNotContain("dXNlcjpzZWNyZXQ=", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-tok", redacted, StringComparison.Ordinal);
        Assert.Contains($"'http.extraHeader={GitCredentialRedactor.RedactedPlaceholder}'", redacted, StringComparison.Ordinal);
        Assert.Contains($"\"http.extraHeader={GitCredentialRedactor.RedactedPlaceholder}\"", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Authorization: Bearer ghp_secret12345", "Authorization: [REDACTED]")]
    [InlineData("Authorization: token super_secret_token", "Authorization: [REDACTED]")]
    [InlineData("Authorization: CustomScheme opaque-secret-value", "Authorization: [REDACTED]")]
    [InlineData("http.extraHeader=X-Custom-Token: my-secret", "http.extraHeader=X-Custom-Token: [REDACTED]")]
    public void Redact_AuthorizationAndCustomHeaders_RedactsSecretRegardlessOfScheme(string input, string expected)
    {
        var redacted = GitCredentialRedactor.Redact(input);

        Assert.Equal(expected, redacted);
    }

    [Theory]
    [InlineData("https://deploy-user:s3cr3t@gitlab.example.com/org/repo.git", "https://deploy-user:[REDACTED]@gitlab.example.com/org/repo.git")]
    [InlineData("http://glpat-token123@git.internal.corp/team/project.git", "http://[REDACTED]@git.internal.corp/team/project.git")]
    [InlineData("https://x-access-token:ghs_abc123@github.com/acme/repo.git", "https://x-access-token:[REDACTED]@github.com/acme/repo.git")]
    public void Redact_HttpUrlCredentialsAcrossArbitraryHosts_RedactsSecrets(string input, string expected)
    {
        var redacted = GitCredentialRedactor.Redact(input);

        Assert.Equal(expected, redacted);
    }

    [Theory]
    [InlineData("http.extraHeader=AUTHORIZATION: basic c2VjcmV0", "http.extraHeader=AUTHORIZATION: basic [REDACTED]")]
    [InlineData("http.https://github.com/.extraHeader=AUTHORIZATION: Custom val1 val2", "http.https://github.com/.extraHeader=AUTHORIZATION: Custom [REDACTED]")]
    [InlineData("https://user:pass@bitbucket.org/org/repo.git", "https://user:[REDACTED]@bitbucket.org/org/repo.git")]
    public void RedactArgument_StandaloneCredentialArgument_RedactsEntireValue(string argument, string expected)
    {
        var redacted = GitCredentialRedactor.RedactArgument(argument);

        Assert.Equal(expected, redacted);
    }

    [Fact]
    public void Redact_NullOrEmptyAndIdempotency_BehavesSafely()
    {
        Assert.Equal(string.Empty, GitCredentialRedactor.Redact(null));
        Assert.Equal(string.Empty, GitCredentialRedactor.Redact(string.Empty));
        Assert.Equal(string.Empty, GitCredentialRedactor.RedactArgument(null));

        const string raw = "push https://token123@gitlab.example.com/acme/app.git ghp_AbcDef99";
        var once = GitCredentialRedactor.Redact(raw);
        var twice = GitCredentialRedactor.Redact(once);

        Assert.Equal(once, twice);
        Assert.DoesNotContain("token123", once, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_AbcDef99", once, StringComparison.Ordinal);
    }
}
