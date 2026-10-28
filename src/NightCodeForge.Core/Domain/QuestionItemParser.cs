using System.Text.Json;

namespace NightCodeForge.Core.Domain;

/// <summary>
/// Parser and normalization utilities for <see cref="QuestionItem"/> collections and decision choices.
/// </summary>
public static class QuestionItemParser
{
    private static readonly string[] QuestionKeys =
    [
        "questions",
        "Questions",
        "question",
        "Question",
        "items",
        "Items",
        "question_items",
        "questionItems"
    ];

    private static readonly string[] PromptKeys =
    [
        "prompt",
        "Prompt",
        "question",
        "Question"
    ];

    private static readonly string[] OptionKeys =
    [
        "options",
        "Options",
        "optionsJson",
        "OptionsJson"
    ];

    private static readonly string[] RationaleKeys =
    [
        "rationale",
        "Rationale"
    ];

    /// <summary>
    /// Parses a raw JSON string into a list of <see cref="QuestionItem"/> records, falling back to raw content if parsing fails.
    /// </summary>
    public static IReadOnlyList<QuestionItem> Parse(string? rawJson, string? fallbackContent = null)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return new[] { new QuestionItem(fallbackContent ?? "Clarification requested") };
        }

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var items = ParseFromJsonElement(doc.RootElement);
            if (items.Count > 0)
            {
                return items;
            }
        }
        catch (JsonException)
        {
            // Non-JSON string; fall back to raw content or fallback prompt
        }

        return new[] { new QuestionItem(fallbackContent ?? rawJson ?? "Clarification requested") };
    }

    /// <summary>
    /// Parses an untyped object (such as an argument dictionary value, list, or JsonElement) into a list of <see cref="QuestionItem"/> records.
    /// </summary>
    public static IReadOnlyList<QuestionItem>? ParseFromObject(object? qObj)
    {
        if (qObj == null)
        {
            return null;
        }

        if (qObj is IReadOnlyList<QuestionItem> typedList)
        {
            return typedList;
        }

        if (qObj is IEnumerable<QuestionItem> typedEnum)
        {
            return typedEnum.ToList();
        }

        if (qObj is JsonElement je)
        {
            return ParseFromJsonElement(je);
        }

        if (qObj is string str && !string.IsNullOrWhiteSpace(str))
        {
            var trimmed = str.TrimStart();
            if (trimmed.StartsWith("[") || trimmed.StartsWith("{"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(str);
                    return ParseFromJsonElement(doc.RootElement);
                }
                catch (JsonException)
                {
                    // Fallback: string was not valid JSON
                }
            }
            return new List<QuestionItem> { new(str) };
        }

        if (qObj is Dictionary<string, object?> singleDict)
        {
            foreach (var qk in QuestionKeys)
            {
                if (singleDict.TryGetValue(qk, out var nested) && nested != null && nested != singleDict)
                {
                    var parsedNested = ParseFromObject(nested);
                    if (parsedNested != null && parsedNested.Count > 0)
                    {
                        return parsedNested;
                    }
                }
            }

            string prompt = GetQuestionPrompt(singleDict);
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                string? rationale = GetQuestionRationale(singleDict);
                var options = ExtractOptionsFromDictionary(singleDict);
                return new List<QuestionItem> { new(prompt, rationale, options) };
            }
        }

        if (qObj is IEnumerable<object?> items)
        {
            var result = new List<QuestionItem>();
            foreach (var item in items)
            {
                if (item is QuestionItem qi)
                {
                    result.Add(qi);
                }
                else if (item is Dictionary<string, object?> d)
                {
                    string prompt = GetQuestionPrompt(d);
                    if (!string.IsNullOrWhiteSpace(prompt))
                    {
                        string? rationale = GetQuestionRationale(d);
                        var options = ExtractOptionsFromDictionary(d);
                        result.Add(new QuestionItem(prompt, rationale, options));
                    }
                }
                else if (item is JsonElement itemJe)
                {
                    var parsed = ParseSingleQuestionElement(itemJe);
                    if (parsed != null)
                    {
                        result.Add(parsed);
                    }
                }
                else if (item is string s && !string.IsNullOrWhiteSpace(s))
                {
                    result.Add(new QuestionItem(s));
                }
            }
            return result;
        }

        return null;
    }

    /// <summary>
    /// Parses a <see cref="JsonElement"/> into a list of <see cref="QuestionItem"/> records.
    /// </summary>
    public static List<QuestionItem> ParseFromJsonElement(JsonElement el)
    {
        var list = new List<QuestionItem>();

        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                var parsed = ParseSingleQuestionElement(item);
                if (parsed != null)
                {
                    list.Add(parsed);
                }
            }
        }
        else if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in QuestionKeys)
            {
                if (el.TryGetProperty(key, out var prop))
                {
                    if (prop.ValueKind == JsonValueKind.Array)
                    {
                        return ParseFromJsonElement(prop);
                    }
                    if (prop.ValueKind == JsonValueKind.String)
                    {
                        var str = prop.GetString();
                        if (!string.IsNullOrWhiteSpace(str))
                        {
                            var trimmed = str.TrimStart();
                            if (trimmed.StartsWith("[") || trimmed.StartsWith("{"))
                            {
                                try
                                {
                                    using var innerDoc = JsonDocument.Parse(str);
                                    return ParseFromJsonElement(innerDoc.RootElement);
                                }
                                catch (JsonException)
                                {
                                    // Fallback: value was not a valid inner JSON string
                                }
                            }
                            return new List<QuestionItem> { new(str) };
                        }
                    }
                    if (prop.ValueKind == JsonValueKind.Object)
                    {
                        var single = ParseSingleQuestionElement(prop);
                        if (single != null)
                        {
                            return new List<QuestionItem> { single };
                        }
                    }
                }
            }

            var item = ParseSingleQuestionElement(el);
            if (item != null)
            {
                list.Add(item);
            }
        }
        else if (el.ValueKind == JsonValueKind.String)
        {
            var str = el.GetString();
            if (!string.IsNullOrWhiteSpace(str))
            {
                var trimmed = str.TrimStart();
                if (trimmed.StartsWith("[") || trimmed.StartsWith("{"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(str);
                        return ParseFromJsonElement(doc.RootElement);
                    }
                    catch (JsonException)
                    {
                        // Fallback: string was not valid JSON
                    }
                }
                list.Add(new QuestionItem(str));
            }
        }

        return list;
    }

    /// <summary>
    /// Parses a single <see cref="JsonElement"/> into a <see cref="QuestionItem"/>, or null if invalid.
    /// </summary>
    public static QuestionItem? ParseSingleQuestionElement(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            var str = el.GetString();
            return !string.IsNullOrWhiteSpace(str) ? new QuestionItem(str) : null;
        }

        if (el.ValueKind == JsonValueKind.Object)
        {
            string prompt = string.Empty;
            foreach (var pk in PromptKeys)
            {
                if (el.TryGetProperty(pk, out var pProp))
                {
                    if (pProp.ValueKind == JsonValueKind.String)
                    {
                        var pStr = pProp.GetString();
                        if (!string.IsNullOrWhiteSpace(pStr))
                        {
                            var trimmed = pStr.TrimStart();
                            if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
                            {
                                try
                                {
                                    using var innerDoc = JsonDocument.Parse(pStr);
                                    var innerItems = ParseFromJsonElement(innerDoc.RootElement);
                                    if (innerItems.Count > 0)
                                    {
                                        return innerItems[0];
                                    }
                                }
                                catch (JsonException)
                                {
                                    // Fallback: prompt string was not an inner JSON structure
                                }
                            }
                            prompt = pStr;
                            break;
                        }
                    }
                    else if (pProp.ValueKind == JsonValueKind.Object)
                    {
                        return ParseSingleQuestionElement(pProp);
                    }
                }
            }

            string? rationale = null;
            foreach (var rk in RationaleKeys)
            {
                if (el.TryGetProperty(rk, out var rProp) && rProp.ValueKind == JsonValueKind.String)
                {
                    rationale = rProp.GetString();
                    break;
                }
            }

            var options = ParseOptionsFromJsonElement(el);

            if (!string.IsNullOrWhiteSpace(prompt))
            {
                return new QuestionItem(prompt, rationale, options);
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts option choices from a question <see cref="JsonElement"/>.
    /// </summary>
    public static List<string>? ParseOptionsFromJsonElement(JsonElement el)
    {
        foreach (var key in OptionKeys)
        {
            if (el.TryGetProperty(key, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Array)
                {
                    var list = prop.EnumerateArray()
                        .Select(x => x.ValueKind == JsonValueKind.String ? (x.GetString() ?? "") : x.ToString())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToList();
                    if (list.Count > 0) return list;
                }
                else if (prop.ValueKind == JsonValueKind.String)
                {
                    var str = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(str))
                    {
                        var trimmed = str.TrimStart();
                        if (trimmed.StartsWith("["))
                        {
                            try
                            {
                                using var optDoc = JsonDocument.Parse(str);
                                if (optDoc.RootElement.ValueKind == JsonValueKind.Array)
                                {
                                    var list = optDoc.RootElement.EnumerateArray()
                                        .Select(x => x.ValueKind == JsonValueKind.String ? (x.GetString() ?? "") : x.ToString())
                                        .Where(s => !string.IsNullOrWhiteSpace(s))
                                        .ToList();
                                    if (list.Count > 0) return list;
                                }
                            }
                            catch (JsonException)
                            {
                                // Fallback: options string was not a valid JSON array
                            }
                        }
                    }
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Extracts option choices from an untyped object (List, IEnumerable, JsonElement, or string).
    /// </summary>
    public static List<string>? ExtractOptionsFromObject(object? opts)
    {
        if (opts == null) return null;

        if (opts is List<string> strList) return strList.Count > 0 ? strList : null;
        if (opts is IEnumerable<string> strEnum)
        {
            var list = strEnum.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            return list.Count > 0 ? list : null;
        }

        if (opts is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Array)
            {
                var list = je.EnumerateArray()
                    .Select(x => x.ValueKind == JsonValueKind.String ? (x.GetString() ?? "") : x.ToString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
                return list.Count > 0 ? list : null;
            }
            if (je.ValueKind == JsonValueKind.String)
            {
                opts = je.GetString();
            }
        }

        if (opts is string optStr && !string.IsNullOrWhiteSpace(optStr))
        {
            var trimmed = optStr.TrimStart();
            if (trimmed.StartsWith("["))
            {
                try
                {
                    using var doc = JsonDocument.Parse(optStr);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        var list = doc.RootElement.EnumerateArray()
                            .Select(x => x.ValueKind == JsonValueKind.String ? (x.GetString() ?? "") : x.ToString())
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .ToList();
                        if (list.Count > 0) return list;
                    }
                }
                catch (JsonException)
                {
                    // Fallback: options string was not a valid JSON array
                }
            }
        }

        if (opts is IEnumerable<object?> objEnum)
        {
            var list = objEnum.Select(o => o?.ToString() ?? "").Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            return list.Count > 0 ? list : null;
        }

        return null;
    }

    private static string GetQuestionPrompt(Dictionary<string, object?> dict)
    {
        foreach (var k in PromptKeys)
        {
            if (dict.TryGetValue(k, out var p) && p != null)
            {
                return p.ToString() ?? "";
            }
        }
        return "";
    }

    private static string? GetQuestionRationale(Dictionary<string, object?> dict)
    {
        foreach (var k in RationaleKeys)
        {
            if (dict.TryGetValue(k, out var r) && r != null)
            {
                return r.ToString();
            }
        }
        return null;
    }

    private static List<string>? ExtractOptionsFromDictionary(Dictionary<string, object?> dict)
    {
        foreach (var key in OptionKeys)
        {
            if (dict.TryGetValue(key, out var opts) && opts != null)
            {
                var extracted = ExtractOptionsFromObject(opts);
                if (extracted != null && extracted.Count > 0)
                {
                    return extracted;
                }
            }
        }
        return null;
    }
}
