using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.GitHub;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.GitHub;

public class GitHubDeviceAuthServiceTests
{
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Handler { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Handler(request));
        }
    }

    [Fact]
    public async Task RequestDeviceCodeAsync_SendsCorrectParameters_AndReturnsParsedResponse()
    {
        var handler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                Assert.Equal(HttpMethod.Post, req.Method);
                Assert.Equal("https://github.com/login/device/code", req.RequestUri?.ToString());

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        device_code = "dev-code-123",
                        user_code = "WDJB-MJHT",
                        verification_uri = "https://github.com/login/device",
                        expires_in = 900,
                        interval = 5
                    }))
                };
            }
        };

        var httpClient = new HttpClient(handler);
        var tokenStoreMock = new Mock<IGitHubTokenStore>();
        var service = new GitHubDeviceAuthService(httpClient, tokenStoreMock.Object, NullLogger<GitHubDeviceAuthService>.Instance);

        var result = await service.RequestDeviceCodeAsync("custom-client-id");

        Assert.Equal("dev-code-123", result.DeviceCode);
        Assert.Equal("WDJB-MJHT", result.UserCode);
        Assert.Equal("https://github.com/login/device", result.VerificationUri);
        Assert.Equal(900, result.ExpiresIn);
        Assert.Equal(5, result.Interval);
    }

    [Fact]
    public async Task PollForAccessTokenAsync_HandlesAuthorizationPending_ThenReturnsAndSavesToken()
    {
        var attempts = 0;
        var handler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                if (req.RequestUri?.ToString().Contains("access_token") == true)
                {
                    attempts++;
                    if (attempts == 1)
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(new
                            {
                                error = "authorization_pending",
                                error_description = "The authorization request is pending."
                            }))
                        };
                    }

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new
                        {
                            access_token = "ghu_new_token_123",
                            expires_in = 28800,
                            refresh_token = "ghr_new_refresh_456",
                            refresh_token_expires_in = 15768000,
                            token_type = "bearer"
                        }))
                    };
                }

                if (req.RequestUri?.ToString().Contains("user") == true)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new
                        {
                            id = 12345L,
                            login = "octocat"
                        }))
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        };

        var httpClient = new HttpClient(handler);
        var tokenStoreMock = new Mock<IGitHubTokenStore>();
        GitHubOAuthTokenPayload? savedPayload = null;
        tokenStoreMock
            .Setup(s => s.SaveTokenAsync(It.IsAny<GitHubOAuthTokenPayload>(), It.IsAny<CancellationToken>()))
            .Callback<GitHubOAuthTokenPayload, CancellationToken>((p, _) => savedPayload = p)
            .Returns(Task.CompletedTask);

        var service = new GitHubDeviceAuthService(httpClient, tokenStoreMock.Object, NullLogger<GitHubDeviceAuthService>.Instance);

        var payload = await service.PollForAccessTokenAsync(
            "dev-code",
            interval: 1,
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Equal("ghu_new_token_123", payload.AccessToken);
        Assert.Equal("ghr_new_refresh_456", payload.RefreshToken);
        Assert.Equal("octocat", payload.AuthenticatedUserLogin);
        Assert.NotNull(savedPayload);
        Assert.Equal("octocat", savedPayload.AuthenticatedUserLogin);
    }

    [Fact]
    public async Task PollForAccessTokenAsync_WhenAccessDenied_ThrowsGitHubAuthenticationException()
    {
        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    error = "access_denied",
                    error_description = "The user denied authorization."
                }))
            }
        };

        var httpClient = new HttpClient(handler);
        var tokenStoreMock = new Mock<IGitHubTokenStore>();
        var service = new GitHubDeviceAuthService(httpClient, tokenStoreMock.Object, NullLogger<GitHubDeviceAuthService>.Instance);

        var ex = await Assert.ThrowsAsync<GitHubAuthenticationException>(() =>
            service.PollForAccessTokenAsync("dev-code", interval: 1, expiresAt: DateTimeOffset.UtcNow.AddMinutes(1)));

        Assert.Contains("denied", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetValidAccessTokenAsync_WhenTokenNotExpiring_ReturnsExistingTokenWithoutRefresh()
    {
        var existingPayload = new GitHubOAuthTokenPayload
        {
            AccessToken = "ghu_valid_current_token",
            AccessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(4),
            RefreshToken = "ghr_refresh_token"
        };

        var handler = new MockHttpMessageHandler
        {
            Handler = _ => throw new InvalidOperationException("No HTTP requests expected.")
        };

        var httpClient = new HttpClient(handler);
        var tokenStoreMock = new Mock<IGitHubTokenStore>();
        tokenStoreMock.Setup(s => s.GetTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync(existingPayload);

        var service = new GitHubDeviceAuthService(httpClient, tokenStoreMock.Object, NullLogger<GitHubDeviceAuthService>.Instance);

        var token = await service.GetValidAccessTokenAsync();

        Assert.Equal("ghu_valid_current_token", token);
    }

    [Fact]
    public async Task GetValidAccessTokenAsync_WhenWithin5MinutesOfExpiration_TriggersRefresh()
    {
        var expiringPayload = new GitHubOAuthTokenPayload
        {
            AccessToken = "ghu_expiring_soon",
            AccessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(2), // Expiring in 2 min
            RefreshToken = "ghr_valid_refresh_token",
            RefreshTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddMonths(5)
        };

        var handler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                Assert.Equal(HttpMethod.Post, req.Method);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        access_token = "ghu_rotated_fresh_token",
                        expires_in = 28800,
                        refresh_token = "ghr_rotated_fresh_refresh_token",
                        refresh_token_expires_in = 15768000
                    }))
                };
            }
        };

        var httpClient = new HttpClient(handler);
        var tokenStoreMock = new Mock<IGitHubTokenStore>();
        tokenStoreMock.Setup(s => s.GetTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expiringPayload);
        tokenStoreMock.Setup(s => s.SaveTokenAsync(It.IsAny<GitHubOAuthTokenPayload>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var service = new GitHubDeviceAuthService(httpClient, tokenStoreMock.Object, NullLogger<GitHubDeviceAuthService>.Instance);

        var validToken = await service.GetValidAccessTokenAsync();

        Assert.Equal("ghu_rotated_fresh_token", validToken);
        tokenStoreMock.Verify(s => s.SaveTokenAsync(It.Is<GitHubOAuthTokenPayload>(p => p.AccessToken == "ghu_rotated_fresh_token" && p.RefreshToken == "ghr_rotated_fresh_refresh_token"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_WhenRefreshTokenRejected_ThrowsGitHubAuthenticationException()
    {
        var existingPayload = new GitHubOAuthTokenPayload
        {
            AccessToken = "ghu_old_token",
            AccessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            RefreshToken = "ghr_bad_refresh"
        };

        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    error = "bad_refresh_token",
                    error_description = "The refresh token is invalid or expired."
                }))
            }
        };

        var httpClient = new HttpClient(handler);
        var tokenStoreMock = new Mock<IGitHubTokenStore>();
        tokenStoreMock.Setup(s => s.GetTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync(existingPayload);

        var service = new GitHubDeviceAuthService(httpClient, tokenStoreMock.Object, NullLogger<GitHubDeviceAuthService>.Instance);

        var ex = await Assert.ThrowsAsync<GitHubAuthenticationException>(() => service.RefreshAccessTokenAsync());

        Assert.Contains("bad_refresh_token", ex.Message);
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_ConcurrentCalls_SingleFlightLockDeduplicatesHttpRequests()
    {
        var currentToken = new GitHubOAuthTokenPayload
        {
            AccessToken = "ghu_concurrent_old",
            AccessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            RefreshToken = "ghr_concurrent_refresh",
            RefreshTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddMonths(1)
        };

        var httpCalls = 0;
        var handler = new MockHttpMessageHandler
        {
            Handler = _ =>
            {
                Interlocked.Increment(ref httpCalls);
                // Simulate network latency
                Thread.Sleep(50);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        access_token = "ghu_concurrent_new",
                        expires_in = 28800,
                        refresh_token = "ghr_concurrent_new_refresh",
                        refresh_token_expires_in = 15768000
                    }))
                };
            }
        };

        var httpClient = new HttpClient(handler);
        var tokenStoreMock = new Mock<IGitHubTokenStore>();

        // When SaveTokenAsync is called, update currentToken state in-memory so subsequent read sees refreshed token
        tokenStoreMock
            .Setup(s => s.GetTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => currentToken);

        tokenStoreMock
            .Setup(s => s.SaveTokenAsync(It.IsAny<GitHubOAuthTokenPayload>(), It.IsAny<CancellationToken>()))
            .Callback<GitHubOAuthTokenPayload, CancellationToken>((p, _) => currentToken = p)
            .Returns(Task.CompletedTask);

        var service = new GitHubDeviceAuthService(httpClient, tokenStoreMock.Object, NullLogger<GitHubDeviceAuthService>.Instance);

        // Run multiple concurrent refresh tasks
        var task1 = service.RefreshAccessTokenAsync();
        var task2 = service.RefreshAccessTokenAsync();
        var task3 = service.RefreshAccessTokenAsync();

        var results = await Task.WhenAll(task1, task2, task3);

        // Only 1 HTTP call should have been made due to the single-flight lock and double-check
        Assert.Equal(1, httpCalls);
        Assert.All(results, r => Assert.Equal("ghu_concurrent_new", r.AccessToken));
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_WhenForcedOnNonExpiringToken_RefreshesTokenAndPreservesCustomClientId()
    {
        var currentToken = new GitHubOAuthTokenPayload
        {
            AccessToken = "ghu_revoked_early_token",
            AccessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(6), // Still 6 hours in future locally
            RefreshToken = "ghr_valid_refresh",
            RefreshTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddMonths(3),
            ClientId = "custom-app-client-id"
        };

        string? capturedFormContent = null;
        var handler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                capturedFormContent = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        access_token = "ghu_forced_refreshed_token",
                        expires_in = 28800,
                        refresh_token = "ghr_forced_new_refresh",
                        refresh_token_expires_in = 15768000
                    }))
                };
            }
        };

        var httpClient = new HttpClient(handler);
        var tokenStoreMock = new Mock<IGitHubTokenStore>();
        tokenStoreMock.Setup(s => s.GetTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => currentToken);
        tokenStoreMock
            .Setup(s => s.SaveTokenAsync(It.IsAny<GitHubOAuthTokenPayload>(), It.IsAny<CancellationToken>()))
            .Callback<GitHubOAuthTokenPayload, CancellationToken>((p, _) => currentToken = p)
            .Returns(Task.CompletedTask);

        var service = new GitHubDeviceAuthService(httpClient, tokenStoreMock.Object, NullLogger<GitHubDeviceAuthService>.Instance);

        var refreshed = await service.RefreshAccessTokenAsync();

        Assert.Equal("ghu_forced_refreshed_token", refreshed.AccessToken);
        Assert.Equal("custom-app-client-id", refreshed.ClientId);
        Assert.NotNull(capturedFormContent);
        Assert.Contains("client_id=custom-app-client-id", capturedFormContent);
    }

    [Fact]
    public async Task PollForAccessTokenAsync_WhenHttpClientErrorOccurs_FailsFastWithGitHubAuthenticationException()
    {
        var attempts = 0;
        var handler = new MockHttpMessageHandler
        {
            Handler = _ =>
            {
                attempts++;
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"message\":\"Bad Request\"}")
                };
            }
        };

        var httpClient = new HttpClient(handler);
        var tokenStoreMock = new Mock<IGitHubTokenStore>();
        var service = new GitHubDeviceAuthService(httpClient, tokenStoreMock.Object, NullLogger<GitHubDeviceAuthService>.Instance);

        await Assert.ThrowsAsync<GitHubAuthenticationException>(() =>
            service.PollForAccessTokenAsync("dev-code", interval: 1, expiresAt: DateTimeOffset.UtcNow.AddMinutes(5)));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task GetAuthenticatedUserAsync_On401Unauthorized_PerformsReactiveRefreshAndReturnsUser()
    {
        var currentToken = new GitHubOAuthTokenPayload
        {
            AccessToken = "ghu_stale_token",
            AccessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(4),
            RefreshToken = "ghr_refresh_token",
            RefreshTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddMonths(3)
        };

        var userAttempts = 0;
        var handler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                if (req.RequestUri?.ToString().Contains("/user") == true)
                {
                    userAttempts++;
                    if (userAttempts == 1)
                    {
                        return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                    }

                    Assert.Equal("ghu_refreshed_user_token", req.Headers.Authorization?.Parameter);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new
                        {
                            id = 777L,
                            login = "octocat",
                            name = "The Octocat"
                        }))
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        access_token = "ghu_refreshed_user_token",
                        expires_in = 28800,
                        refresh_token = "ghr_rotated_refresh",
                        refresh_token_expires_in = 15768000
                    }))
                };
            }
        };

        var httpClient = new HttpClient(handler);
        var tokenStoreMock = new Mock<IGitHubTokenStore>();
        tokenStoreMock.Setup(s => s.GetTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => currentToken);
        tokenStoreMock
            .Setup(s => s.SaveTokenAsync(It.IsAny<GitHubOAuthTokenPayload>(), It.IsAny<CancellationToken>()))
            .Callback<GitHubOAuthTokenPayload, CancellationToken>((p, _) => currentToken = p)
            .Returns(Task.CompletedTask);

        var service = new GitHubDeviceAuthService(httpClient, tokenStoreMock.Object, NullLogger<GitHubDeviceAuthService>.Instance);

        var user = await service.GetAuthenticatedUserAsync();

        Assert.NotNull(user);
        Assert.Equal("octocat", user.Login);
        Assert.Equal(2, userAttempts);
    }
}

