namespace NightCodeForge.Infrastructure.Tests.Configuration;

using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Resilience;
using NightCodeForge.Infrastructure.Configuration;
using Xunit;

public class RetryPolicyConfigurationBindingTests : IDisposable
{
    private readonly string _tempDirectory;

    public RetryPolicyConfigurationBindingTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "ncf_retry_config_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public void RetryPolicyOptions_BindsSecondsAndOverridesCorrectly_FromConfiguration()
    {
        var json = """
        {
          "RetryPolicy": {
            "MaxRetries": 5,
            "BaseDelaySeconds": 2.5,
            "MaxDelaySeconds": 25.0,
            "MaxRetryDurationSeconds": 45.0,
            "MaxTimeoutRetries": 2,
            "PerAttemptTimeoutSeconds": 12.0
          }
        }
        """;

        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        var options = new RetryPolicyOptions();
        configuration.GetSection(RetryPolicyOptions.SectionName).Bind(options);

        Assert.Equal(5, options.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(2.5), options.BaseDelay);
        Assert.Equal(2.5, options.BaseDelaySeconds);
        Assert.Equal(TimeSpan.FromSeconds(25.0), options.MaxDelay);
        Assert.Equal(25.0, options.MaxDelaySeconds);
        Assert.Equal(TimeSpan.FromSeconds(45.0), options.MaxRetryDuration);
        Assert.Equal(45.0, options.MaxRetryDurationSeconds);
        Assert.Equal(2, options.MaxTimeoutRetries);
        Assert.Equal(TimeSpan.FromSeconds(12.0), options.PerAttemptTimeout);
        Assert.Equal(12.0, options.PerAttemptTimeoutSeconds);
    }

    [Fact]
    public void LlmRetryPolicyOptions_BindsFromConfiguration_OverridingDefaults()
    {
        var json = """
        {
          "LlmRetryPolicy": {
            "MaxRetries": 40,
            "BaseDelaySeconds": 2.0,
            "MaxDelaySeconds": 60.0,
            "MaxRetryDurationSeconds": 3600.0,
            "MaxTimeoutRetries": 5
          }
        }
        """;

        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        var options = new LlmRetryPolicyOptions();
        configuration.GetSection(LlmRetryPolicyOptions.SectionName).Bind(options);

        Assert.Equal(40, options.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(2.0), options.BaseDelay);
        Assert.Equal(TimeSpan.FromSeconds(60.0), options.MaxDelay);
        Assert.Equal(TimeSpan.FromSeconds(3600.0), options.MaxRetryDuration);
        Assert.Equal(5, options.MaxTimeoutRetries);
    }

    [Fact]
    public void UserConfigJson_OverridesFactoryDefaultsInAppsettings()
    {
        var appsettingsJson = """
        {
          "RetryPolicy": {
            "MaxRetries": 3,
            "BaseDelaySeconds": 1.0
          },
          "LlmRetryPolicy": {
            "MaxRetries": 20,
            "MaxRetryDurationSeconds": 1800.0
          }
        }
        """;

        var userConfigJson = """
        {
          "RetryPolicy": {
            "MaxRetries": 7
          },
          "LlmRetryPolicy": {
            "MaxRetries": 50,
            "MaxRetryDurationSeconds": 900.0
          }
        }
        """;

        var appsettingsPath = Path.Combine(_tempDirectory, "appsettings.json");
        File.WriteAllText(appsettingsPath, appsettingsJson);

        var configDir = Path.Combine(_tempDirectory, "config");
        Directory.CreateDirectory(configDir);
        var userConfigPath = Path.Combine(configDir, "config.json");
        File.WriteAllText(userConfigPath, userConfigJson);

        var builder = new ConfigurationBuilder();
        builder.SetBasePath(_tempDirectory);
        builder.AddJsonFile("appsettings.json", optional: false);
        HostConfigurationSources.AddUserConfiguration(builder, _tempDirectory, environment: null, logger: null);

        var configuration = builder.Build();

        var retryOptions = new RetryPolicyOptions();
        configuration.GetSection(RetryPolicyOptions.SectionName).Bind(retryOptions);

        var llmRetryOptions = new LlmRetryPolicyOptions();
        configuration.GetSection(LlmRetryPolicyOptions.SectionName).Bind(llmRetryOptions);

        // Factory default BaseDelay preserved, user MaxRetries overridden
        Assert.Equal(7, retryOptions.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(1.0), retryOptions.BaseDelay);

        // Factory defaults overridden by user config
        Assert.Equal(50, llmRetryOptions.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(900.0), llmRetryOptions.MaxRetryDuration);
        Assert.Equal(TimeSpan.FromSeconds(30.0), llmRetryOptions.MaxDelay); // default preserved
    }

    [Fact]
    public void DependencyInjection_WiresConfiguredOptions_ViaConfigure()
    {
        var json = """
        {
          "RetryPolicy": {
            "MaxRetries": 8
          },
          "LlmRetryPolicy": {
            "MaxRetries": 15
          }
        }
        """;

        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        var services = new ServiceCollection();
        services.Configure<RetryPolicyOptions>(configuration.GetSection(RetryPolicyOptions.SectionName));
        services.Configure<LlmRetryPolicyOptions>(configuration.GetSection(LlmRetryPolicyOptions.SectionName));

        var sp = services.BuildServiceProvider();

        var retry = sp.GetRequiredService<IOptions<RetryPolicyOptions>>().Value;
        var llmRetry = sp.GetRequiredService<IOptions<LlmRetryPolicyOptions>>().Value;

        Assert.Equal(8, retry.MaxRetries);
        Assert.Equal(15, llmRetry.MaxRetries);
    }

    [Fact]
    public void UserConfigJson_WithTimeSpanFormat_OverridesSecondsFormatInAppsettings()
    {
        var appsettingsJson = """
        {
          "RetryPolicy": {
            "MaxRetries": 3,
            "BaseDelaySeconds": 1.0,
            "MaxDelaySeconds": 15.0,
            "MaxRetryDurationSeconds": 30.0
          },
          "LlmRetryPolicy": {
            "MaxRetries": 20,
            "BaseDelaySeconds": 1.0,
            "MaxDelaySeconds": 30.0,
            "MaxRetryDurationSeconds": 1800.0
          }
        }
        """;

        var userConfigJson = """
        {
          "RetryPolicy": {
            "BaseDelay": "00:00:05",
            "MaxDelay": "00:00:45",
            "MaxRetryDuration": "00:02:00",
            "PerAttemptTimeout": "00:00:20"
          },
          "LlmRetryPolicy": {
            "BaseDelay": "00:00:03",
            "MaxDelay": "00:01:30",
            "MaxRetryDuration": "00:45:00"
          }
        }
        """;

        File.WriteAllText(Path.Combine(_tempDirectory, "appsettings.json"), appsettingsJson);
        var configDir = Path.Combine(_tempDirectory, "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "config.json"), userConfigJson);

        var builder = new ConfigurationBuilder();
        builder.SetBasePath(_tempDirectory);
        builder.AddJsonFile("appsettings.json", optional: false);
        HostConfigurationSources.AddUserConfiguration(builder, _tempDirectory, environment: null, logger: null);

        var configuration = builder.Build();

        var retryOptions = new RetryPolicyOptions();
        configuration.GetSection(RetryPolicyOptions.SectionName).Bind(retryOptions);

        var llmRetryOptions = new LlmRetryPolicyOptions();
        configuration.GetSection(LlmRetryPolicyOptions.SectionName).Bind(llmRetryOptions);

        Assert.Equal(TimeSpan.FromSeconds(5), retryOptions.BaseDelay);
        Assert.Equal(TimeSpan.FromSeconds(45), retryOptions.MaxDelay);
        Assert.Equal(TimeSpan.FromMinutes(2), retryOptions.MaxRetryDuration);
        Assert.Equal(TimeSpan.FromSeconds(20), retryOptions.PerAttemptTimeout);

        Assert.Equal(TimeSpan.FromSeconds(3), llmRetryOptions.BaseDelay);
        Assert.Equal(TimeSpan.FromSeconds(90), llmRetryOptions.MaxDelay);
        Assert.Equal(TimeSpan.FromMinutes(45), llmRetryOptions.MaxRetryDuration);
    }

    [Fact]
    public void AddNightCodeForgeCoreAndInfrastructure_BindsOperationsControlOptions_WhenConfigurationRegistered()
    {
        var json = """
        {
          "OperationsControl": {
            "ReclaimVramOnPause": true
          }
        }
        """;

        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddNightCodeForgeCoreAndInfrastructure();

        using var sp = services.BuildServiceProvider();
        var opsOptions = sp.GetRequiredService<IOptions<NightCodeForge.Core.Services.OperationsControlOptions>>().Value;

        Assert.True(opsOptions.ReclaimVramOnPause);
        Assert.True(opsOptions.ShouldReclaimVramOnPause);
    }

    [Fact]
    public void AddNightCodeForgeCoreAndInfrastructure_BindsRetryPolicyAndLlmRetryPolicy_FromConfiguration()
    {
        var json = """
        {
          "RetryPolicy": {
            "MaxRetries": 6,
            "BaseDelaySeconds": 2.0
          },
          "LlmRetryPolicy": {
            "MaxRetries": 35,
            "MaxRetryDurationSeconds": 1200.0
          }
        }
        """;

        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddNightCodeForgeCoreAndInfrastructure();

        using var sp = services.BuildServiceProvider();
        var retryOptions = sp.GetRequiredService<IOptions<RetryPolicyOptions>>().Value;
        var llmRetryOptions = sp.GetRequiredService<IOptions<LlmRetryPolicyOptions>>().Value;

        Assert.Equal(6, retryOptions.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(2.0), retryOptions.BaseDelay);
        Assert.Equal(35, llmRetryOptions.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(1200.0), llmRetryOptions.MaxRetryDuration);
    }

    [Fact]
    public void AddNightCodeForgeCoreAndInfrastructure_WhenOptionsConfiguredBeforeRegistration_ExplicitConfigureOverridesConfiguration()
    {
        var json = """
        {
          "OperationsControl": {
            "ReclaimVramOnPause": true
          },
          "RetryPolicy": {
            "MaxRetries": 3
          },
          "LlmRetryPolicy": {
            "MaxRetries": 20
          }
        }
        """;

        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.Configure<NightCodeForge.Core.Services.OperationsControlOptions>(o => o.ReclaimVramOnPause = false);
        services.Configure<RetryPolicyOptions>(o => o.MaxRetries = 9);
        services.Configure<LlmRetryPolicyOptions>(o => o.MaxRetries = 45);
        services.AddNightCodeForgeCoreAndInfrastructure();

        using var sp = services.BuildServiceProvider();
        var opsOptions = sp.GetRequiredService<IOptions<NightCodeForge.Core.Services.OperationsControlOptions>>().Value;
        var retryOptions = sp.GetRequiredService<IOptions<RetryPolicyOptions>>().Value;
        var llmRetryOptions = sp.GetRequiredService<IOptions<LlmRetryPolicyOptions>>().Value;

        Assert.False(opsOptions.ReclaimVramOnPause);
        Assert.Equal(9, retryOptions.MaxRetries);
        Assert.Equal(45, llmRetryOptions.MaxRetries);

        // Calling BindConfigurationSectionAsDefault repeatedly must not insert duplicate IConfigureOptions descriptors
        int countBefore = services.Count(d => d.ServiceType == typeof(IConfigureOptions<RetryPolicyOptions>));
        NightCodeForge.Infrastructure.Resilience.HttpClientBuilderExtensions.BindConfigurationSectionAsDefault<RetryPolicyOptions>(services, RetryPolicyOptions.SectionName);
        int countAfter = services.Count(d => d.ServiceType == typeof(IConfigureOptions<RetryPolicyOptions>));
        Assert.Equal(countBefore, countAfter);
    }
}

