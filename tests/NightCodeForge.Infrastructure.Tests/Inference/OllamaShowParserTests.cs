using System.Text.Json;
using NightCodeForge.Infrastructure.Inference;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.Inference;

public class OllamaShowParserTests
{
    [Fact]
    public void ParseThinkingCapability_Qwen38Payload_ParsesStringLevelsAndDefault()
    {
        var json = """
        {
          "thinking": {
            "values": [false, "low", "medium", "xhigh"],
            "default": "medium"
          }
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var capability = OllamaShowParser.ParseThinkingCapability(doc.RootElement);

        Assert.True(capability.SupportsThinking);
        Assert.False(capability.SupportsBooleanOnly);
        Assert.Equal(["low", "medium", "xhigh"], capability.StringLevels);
        Assert.Equal("medium", capability.DefaultLevel);
        Assert.Equal("🧠 Reasoning: low, medium, xhigh", capability.FormattedBadge);
    }

    [Fact]
    public void ParseThinkingCapability_BooleanValuesPayload_ParsesAsBooleanOnly()
    {
        var json = """
        {
          "thinking": {
            "values": [false, true],
            "default": true
          }
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var capability = OllamaShowParser.ParseThinkingCapability(doc.RootElement);

        Assert.True(capability.SupportsThinking);
        Assert.True(capability.SupportsBooleanOnly);
        Assert.Empty(capability.StringLevels);
        Assert.Null(capability.DefaultLevel);
        Assert.Equal("🧠 Reasoning: boolean (on/off)", capability.FormattedBadge);
    }

    [Fact]
    public void ParseThinkingCapability_LegacyCapabilitiesPayload_FallsBackToBooleanOnly()
    {
        var json = """
        {
          "capabilities": ["completion", "thinking"]
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var capability = OllamaShowParser.ParseThinkingCapability(doc.RootElement);

        Assert.True(capability.SupportsThinking);
        Assert.True(capability.SupportsBooleanOnly);
        Assert.Empty(capability.StringLevels);
        Assert.Null(capability.DefaultLevel);
        Assert.Equal("🧠 Reasoning: boolean (on/off)", capability.FormattedBadge);
    }

    [Fact]
    public void ParseThinkingCapability_NonThinkingModelPayload_ReturnsNone()
    {
        var json = """
        {
          "capabilities": ["completion"]
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var capability = OllamaShowParser.ParseThinkingCapability(doc.RootElement);

        Assert.False(capability.SupportsThinking);
        Assert.False(capability.SupportsBooleanOnly);
        Assert.Empty(capability.StringLevels);
        Assert.Null(capability.DefaultLevel);
        Assert.Equal(string.Empty, capability.FormattedBadge);
    }

    [Fact]
    public void ParseThinkingCapability_NestedInModelInfoOrDetails_ParsesSuccessfully()
    {
        var jsonModelInfo = """
        {
          "model_info": {
            "thinking": {
              "values": [false, "low", "high"],
              "default": "low"
            }
          }
        }
        """;
        using var doc1 = JsonDocument.Parse(jsonModelInfo);
        var cap1 = OllamaShowParser.ParseThinkingCapability(doc1.RootElement);
        Assert.True(cap1.SupportsThinking);
        Assert.Equal(["low", "high"], cap1.StringLevels);
        Assert.Equal("low", cap1.DefaultLevel);

        var jsonDetails = """
        {
          "details": {
            "thinking": {
              "values": [false, true]
            }
          }
        }
        """;
        using var doc2 = JsonDocument.Parse(jsonDetails);
        var cap2 = OllamaShowParser.ParseThinkingCapability(doc2.RootElement);
        Assert.True(cap2.SupportsThinking);
        Assert.True(cap2.SupportsBooleanOnly);
    }

    [Fact]
    public void ParseThinkingCapability_MalformedOrEmptyPayload_DoesNotThrowAndReturnsNone()
    {
        using var emptyDoc = JsonDocument.Parse("{}");
        var emptyCap = OllamaShowParser.ParseThinkingCapability(emptyDoc.RootElement);
        Assert.False(emptyCap.SupportsThinking);

        using var arrayDoc = JsonDocument.Parse("[]");
        var arrayCap = OllamaShowParser.ParseThinkingCapability(arrayDoc.RootElement);
        Assert.False(arrayCap.SupportsThinking);
    }

    [Fact]
    public void ParseThinkingCapability_StringBooleanTokensInValues_ExcludesFalseFromStringLevels()
    {
        var jsonWithStringFalse = """
        {
          "thinking": {
            "values": ["false", "low", "xhigh"],
            "default": "false"
          }
        }
        """;
        using var doc1 = JsonDocument.Parse(jsonWithStringFalse);
        var cap1 = OllamaShowParser.ParseThinkingCapability(doc1.RootElement);
        Assert.True(cap1.SupportsThinking);
        Assert.False(cap1.SupportsBooleanOnly);
        Assert.Equal(["low", "xhigh"], cap1.StringLevels);
        Assert.Null(cap1.DefaultLevel);

        var jsonStringBooleansOnly = """
        {
          "thinking": {
            "values": ["false", "true"]
          }
        }
        """;
        using var doc2 = JsonDocument.Parse(jsonStringBooleansOnly);
        var cap2 = OllamaShowParser.ParseThinkingCapability(doc2.RootElement);
        Assert.True(cap2.SupportsThinking);
        Assert.True(cap2.SupportsBooleanOnly);
        Assert.Empty(cap2.StringLevels);
    }
}

