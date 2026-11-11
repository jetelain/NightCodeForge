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

        var parsed = TryParseQuestionsFromJsonString(rawJson, requireJsonPrefix: false);
        if (parsed is { Count: > 0 })
        {
            return parsed;
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
            return Normalize(typedList);
        }

        if (qObj is IEnumerable<QuestionItem> typedEnum)
        {
            return Normalize(typedEnum);
        }

        if (qObj is JsonElement je)
        {
            return ParseFromJsonElement(je);
        }

        if (qObj is string str && !string.IsNullOrWhiteSpace(str))
        {
            var parsed = TryParseQuestionsFromJsonString(str, requireJsonPrefix: true);
            if (parsed != null && parsed.Count > 0)
            {
                return parsed;
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
                var trimmed = prompt.TrimStart();
                if (trimmed.StartsWith('{') || trimmed.StartsWith('[') || trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    var inner = TryParseQuestionsFromJsonString(prompt, requireJsonPrefix: true);
                    if (inner != null && inner.Count > 0)
                    {
                        return inner;
                    }
                }

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
                    result.AddRange(Normalize(new[] { qi }));
                }
                else if (item is Dictionary<string, object?> d)
                {
                    string prompt = GetQuestionPrompt(d);
                    if (!string.IsNullOrWhiteSpace(prompt))
                    {
                        var trimmed = prompt.TrimStart();
                        if (trimmed.StartsWith('{') || trimmed.StartsWith('[') || trimmed.StartsWith("```", StringComparison.Ordinal))
                        {
                            var inner = TryParseQuestionsFromJsonString(prompt, requireJsonPrefix: true);
                            if (inner != null && inner.Count > 0)
                            {
                                result.AddRange(inner);
                                continue;
                            }
                        }

                        string? rationale = GetQuestionRationale(d);
                        var options = ExtractOptionsFromDictionary(d);
                        result.Add(new QuestionItem(prompt, rationale, options));
                    }
                }
                else if (item is JsonElement itemJe)
                {
                    var parsed = ParseQuestionElements(itemJe);
                    result.AddRange(parsed);
                }
                else if (item is string s && !string.IsNullOrWhiteSpace(s))
                {
                    var trimmed = s.TrimStart();
                    if (trimmed.StartsWith('{') || trimmed.StartsWith('[') || trimmed.StartsWith("```", StringComparison.Ordinal))
                    {
                        var inner = TryParseQuestionsFromJsonString(s, requireJsonPrefix: true);
                        if (inner != null && inner.Count > 0)
                        {
                            result.AddRange(inner);
                            continue;
                        }
                    }
                    result.Add(new QuestionItem(s));
                }
            }
            return result;
        }

        return null;
    }

    /// <summary>
    /// Normalizes a collection of <see cref="QuestionItem"/> records, expanding any items whose prompts
    /// contain stringified JSON arrays or objects into distinct <see cref="QuestionItem"/> records.
    /// </summary>
    public static IReadOnlyList<QuestionItem> Normalize(IEnumerable<QuestionItem>? items)
    {
        if (items == null)
        {
            return Array.Empty<QuestionItem>();
        }

        var itemList = items as IReadOnlyList<QuestionItem> ?? items.ToList();
        bool needsExpansion = false;
        foreach (var item in itemList)
        {
            if (item != null && !string.IsNullOrWhiteSpace(item.Prompt))
            {
                var trimmed = item.Prompt.TrimStart();
                if (trimmed.StartsWith('[') || trimmed.StartsWith('{') || trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    needsExpansion = true;
                    break;
                }
            }
        }

        if (!needsExpansion)
        {
            return itemList;
        }

        var result = new List<QuestionItem>(itemList.Count);
        foreach (var item in itemList)
        {
            if (item == null) continue;

            if (!string.IsNullOrWhiteSpace(item.Prompt))
            {
                var trimmed = item.Prompt.TrimStart();
                if (trimmed.StartsWith('[') || trimmed.StartsWith('{') || trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    var inner = TryParseQuestionsFromJsonString(item.Prompt, requireJsonPrefix: true);
                    if (inner is { Count: > 0 })
                    {
                        var first = inner[0];
                        if (inner.Count == 1 && first != null && (first.Options == null || first.Options.Count == 0) && item.Options is { Count: > 0 })
                        {
                            result.Add(new QuestionItem(first.Prompt, first.Rationale ?? item.Rationale, item.Options));
                        }
                        else
                        {
                            result.AddRange(Normalize(inner));
                        }
                        continue;
                    }
                }
            }

            result.Add(item);
        }

        return result;
    }

    /// <summary>
    /// Converts a <see cref="Question"/> entity into one or more <see cref="QuestionItem"/> records,
    /// safely unpacking any nested JSON embedded in the prompt.
    /// </summary>
    public static IReadOnlyList<QuestionItem> ToQuestionItems(Question? question)
    {
        if (question == null)
        {
            return Array.Empty<QuestionItem>();
        }

        var options = ExtractOptionsFromObject(question.OptionsJson);
        var baseItem = new QuestionItem(question.Prompt, question.Rationale, options);
        return Normalize(new[] { baseItem });
    }

    /// <summary>
    /// Converts a collection of <see cref="Question"/> entities into a flattened list of <see cref="QuestionItem"/> records.
    /// </summary>
    public static IReadOnlyList<QuestionItem> ToQuestionItems(IEnumerable<Question>? questions)
    {
        if (questions == null)
        {
            return Array.Empty<QuestionItem>();
        }

        var result = new List<QuestionItem>();
        foreach (var q in questions)
        {
            if (q != null)
            {
                result.AddRange(ToQuestionItems(q));
            }
        }
        return result;
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
                var parsedItems = ParseQuestionElements(item);
                list.AddRange(parsedItems);
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
                            var parsed = TryParseQuestionsFromJsonString(str, requireJsonPrefix: true);
                            if (parsed != null && parsed.Count > 0)
                            {
                                return parsed;
                            }
                            return new List<QuestionItem> { new(str) };
                        }
                    }
                    if (prop.ValueKind == JsonValueKind.Object)
                    {
                        var parsed = ParseQuestionElements(prop);
                        if (parsed.Count > 0)
                        {
                            return parsed;
                        }
                    }
                }
            }

            var items = ParseQuestionElements(el);
            list.AddRange(items);
        }
        else if (el.ValueKind == JsonValueKind.String)
        {
            var str = el.GetString();
            if (!string.IsNullOrWhiteSpace(str))
            {
                var parsed = TryParseQuestionsFromJsonString(str, requireJsonPrefix: true);
                if (parsed != null && parsed.Count > 0)
                {
                    return parsed;
                }
                list.Add(new QuestionItem(str));
            }
        }

        return list;
    }

    private static List<QuestionItem>? TryParseQuestionsFromJsonString(string str, bool requireJsonPrefix)
    {
        var trimmed = str.TrimStart();
        if (requireJsonPrefix &&
            !trimmed.StartsWith('[') &&
            !trimmed.StartsWith('{') &&
            !trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(str);
            return ParseFromJsonElement(doc.RootElement);
        }
        catch (JsonException)
        {
            var repaired = JsonRepairHelper.Repair(str);
            if (!string.Equals(repaired, str, StringComparison.Ordinal))
            {
                try
                {
                    using var doc = JsonDocument.Parse(repaired);
                    return ParseFromJsonElement(doc.RootElement);
                }
                catch (JsonException)
                {
                    // Fallback: string was not valid JSON even after repair
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Parses a single <see cref="JsonElement"/> into a <see cref="QuestionItem"/>, or null if invalid.
    /// </summary>
    public static QuestionItem? ParseSingleQuestionElement(JsonElement el)
    {
        var items = ParseQuestionElements(el);
        return items.Count > 0 ? items[0] : null;
    }

    /// <summary>
    /// Parses a single <see cref="JsonElement"/> into a list of <see cref="QuestionItem"/> records,
    /// expanding any nested JSON strings or arrays.
    /// </summary>
    public static List<QuestionItem> ParseQuestionElements(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            var str = el.GetString();
            if (string.IsNullOrWhiteSpace(str))
            {
                return new List<QuestionItem>();
            }

            var parsed = TryParseQuestionsFromJsonString(str, requireJsonPrefix: true);
            if (parsed is { Count: > 0 })
            {
                return parsed;
            }

            return new List<QuestionItem> { new(str) };
        }

        if (el.ValueKind == JsonValueKind.Array)
        {
            return ParseFromJsonElement(el);
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
                            if (trimmed.StartsWith('{') || trimmed.StartsWith('[') || trimmed.StartsWith("```", StringComparison.Ordinal))
                            {
                                var innerItems = TryParseQuestionsFromJsonString(pStr, requireJsonPrefix: true);
                                if (innerItems is { Count: > 0 })
                                {
                                    return innerItems;
                                }
                            }
                            prompt = pStr;
                            break;
                        }
                    }
                    else if (pProp.ValueKind == JsonValueKind.Object || pProp.ValueKind == JsonValueKind.Array)
                    {
                        var inner = ParseFromJsonElement(pProp);
                        if (inner.Count > 0)
                        {
                            return inner;
                        }
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
                return new List<QuestionItem> { new(prompt, rationale, options) };
            }
        }

        return new List<QuestionItem>();
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
                        var parsedOptions = TryParseOptionsArrayFromJsonString(str);
                        if (parsedOptions is { Count: > 0 })
                        {
                            return parsedOptions;
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
            var parsedOptions = TryParseOptionsArrayFromJsonString(optStr);
            if (parsedOptions is { Count: > 0 })
            {
                return parsedOptions;
            }
        }

        if (opts is IEnumerable<object?> objEnum)
        {
            var list = objEnum.Select(o => o?.ToString() ?? "").Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            return list.Count > 0 ? list : null;
        }

        return null;
    }

    private static List<string>? TryParseOptionsArrayFromJsonString(string raw)
    {
        var trimmed = raw.TrimStart();
        if (!trimmed.StartsWith('[') && !trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return null;
        }

        var candidate = raw;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var list = doc.RootElement.EnumerateArray()
                        .Select(x => x.ValueKind == JsonValueKind.String ? (x.GetString() ?? "") : x.ToString())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToList();
                    if (list.Count > 0)
                    {
                        return list;
                    }
                }
                return null;
            }
            catch (JsonException) when (attempt == 0)
            {
                var repaired = JsonRepairHelper.Repair(raw);
                if (string.Equals(repaired, raw, StringComparison.Ordinal))
                {
                    return null;
                }
                candidate = repaired;
            }
            catch (JsonException)
            {
                return null;
            }
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
