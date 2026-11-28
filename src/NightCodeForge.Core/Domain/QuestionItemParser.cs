using System.Text.Json;

namespace NightCodeForge.Core.Domain;

/// <summary>
/// Parser and normalization utilities for <see cref="QuestionItem"/> collections and decision choices.
/// </summary>
public static class QuestionItemParser
{
    private const string SingularQuestionKey = "question";

    private static readonly string[] QuestionKeys =
    [
        "questions",
        SingularQuestionKey,
        "items",
        "question_items",
        "questionItems"
    ];

    private static readonly string[] PromptKeys =
    [
        "prompt",
        "question"
    ];

    private static readonly string[] OptionKeys =
    [
        "options",
        "optionsJson",
        "options_json"
    ];

    private static readonly string[] RationaleKeys =
    [
        "rationale"
    ];

    private static readonly string[] OptionLabelKeys =
    [
        "label",
        "title",
        "name",
        "text",
        "value",
        "option",
        "choice",
        "answer",
        "id",
        "key"
    ];

    private static readonly string[] OptionDescriptionKeys =
    [
        "description",
        "desc",
        "detail",
        "details",
        "summary",
        "rationale",
        "explanation",
        "reason",
        "why",
        "context",
        "note"
    ];

    /// <summary>
    /// Leniently binds a raw <c>ask</c> tool <c>questions</c> argument (array, object, string, or <see cref="JsonElement"/>)
    /// into a list of <see cref="QuestionItem"/> records while preserving blank-prompt entries (so tool validation can reject
    /// them explicitly) and returning <c>false</c> when non-question types or malformed JSON are encountered.
    /// </summary>
    public static bool TryBindAskQuestions(object? rawQuestions, out IReadOnlyList<QuestionItem>? questions)
    {
        if (rawQuestions is null)
        {
            questions = Array.Empty<QuestionItem>();
            return true;
        }

        if (rawQuestions is IEnumerable<QuestionItem> typedQuestions)
        {
            var list = typedQuestions.ToList();
            if (list.Any(q => q is null))
            {
                questions = null;
                return false;
            }

            questions = Normalize(list);
            return true;
        }

        if (rawQuestions is JsonElement je)
        {
            return TryBindAskJsonElement(je, isRoot: true, out questions);
        }

        if (rawQuestions is string str)
        {
            if (string.IsNullOrWhiteSpace(str))
            {
                questions = Array.Empty<QuestionItem>();
                return true;
            }

            if (LooksLikeJsonOrCodeFence(str))
            {
                if (TryParseJsonElementLenient(str, out var parsedRoot))
                {
                    return TryBindAskJsonElement(parsedRoot, isRoot: true, out questions);
                }

                if (LooksLikeMalformedJsonAttempt(str))
                {
                    questions = null;
                    return false;
                }
            }

            questions = new[] { new QuestionItem(str) };
            return true;
        }

        if (rawQuestions is IDictionary<string, object?> dict)
        {
            return TryBindAskDictionary(dict, isRoot: true, out questions);
        }

        if (rawQuestions is IEnumerable<object?> objItems)
        {
            var result = new List<QuestionItem>();
            foreach (var item in objItems)
            {
                if (!TryBindAskElementObject(item, out var itemQuestions))
                {
                    questions = null;
                    return false;
                }

                result.AddRange(itemQuestions);
            }

            questions = result;
            return true;
        }

        questions = null;
        return false;
    }

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

        if (qObj is IDictionary<string, object?> singleDict)
        {
            foreach (var qk in QuestionKeys)
            {
                if (TryGetValueIgnoreCase(singleDict, qk, out var nested) && !ReferenceEquals(nested, singleDict))
                {
                    if (string.Equals(qk, SingularQuestionKey, StringComparison.OrdinalIgnoreCase) &&
                        nested is null or string or JsonElement { ValueKind: JsonValueKind.String or JsonValueKind.Null or JsonValueKind.Undefined })
                    {
                        continue;
                    }

                    if (nested == null)
                    {
                        continue;
                    }

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

                var trimmed = prompt.TrimStart();
                if (trimmed.StartsWith('{') || trimmed.StartsWith('[') || trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    var inner = TryParseQuestionsFromJsonString(prompt, requireJsonPrefix: true);
                    if (inner != null && inner.Count > 0)
                    {
                        if (inner.Count == 1)
                        {
                            return new List<QuestionItem>
                            {
                                MergeSingleNestedQuestion(inner[0], rationale, options)
                            };
                        }

                        return inner;
                    }
                }

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
                else if (item is IDictionary<string, object?> d)
                {
                    string prompt = GetQuestionPrompt(d);
                    if (!string.IsNullOrWhiteSpace(prompt))
                    {
                        string? rationale = GetQuestionRationale(d);
                        var options = ExtractOptionsFromDictionary(d);

                        var trimmed = prompt.TrimStart();
                        if (trimmed.StartsWith('{') || trimmed.StartsWith('[') || trimmed.StartsWith("```", StringComparison.Ordinal))
                        {
                            var inner = TryParseQuestionsFromJsonString(prompt, requireJsonPrefix: true);
                            if (inner != null && inner.Count > 0)
                            {
                                if (inner.Count == 1)
                                {
                                    result.Add(MergeSingleNestedQuestion(inner[0], rationale, options));
                                }
                                else
                                {
                                    result.AddRange(inner);
                                }
                                continue;
                            }
                        }

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
        bool needsNormalization = false;
        foreach (var item in itemList)
        {
            if (item == null)
            {
                needsNormalization = true;
                break;
            }

            if (!string.IsNullOrWhiteSpace(item.Prompt))
            {
                var trimmed = item.Prompt.TrimStart();
                if (trimmed.StartsWith('[') || trimmed.StartsWith('{') || trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    needsNormalization = true;
                    break;
                }
            }

            if (HasUnnormalizedOptions(item.Options))
            {
                needsNormalization = true;
                break;
            }
        }

        if (!needsNormalization)
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
                        if (inner.Count == 1 && first != null)
                        {
                            var mergedOptions = (first.Options is { Count: > 0 } ? NormalizeOptionsList(first.Options) : null)
                                ?? NormalizeOptionsList(item.Options);
                            var mergedRationale = !string.IsNullOrWhiteSpace(first.Rationale) ? first.Rationale : item.Rationale;
                            result.Add(new QuestionItem(first.Prompt, mergedRationale, mergedOptions));
                        }
                        else
                        {
                            result.AddRange(Normalize(inner));
                        }
                        continue;
                    }
                }
            }

            result.Add(HasUnnormalizedOptions(item.Options)
                ? item with { Options = NormalizeOptionsList(item.Options) }
                : item);
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
                if (TryGetPropertyIgnoreCase(el, key, out var prop))
                {
                    if (prop.ValueKind == JsonValueKind.Array)
                    {
                        return ParseFromJsonElement(prop);
                    }
                    if (prop.ValueKind == JsonValueKind.String)
                    {
                        if (string.Equals(key, SingularQuestionKey, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

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
            string? rationale = null;
            foreach (var rk in RationaleKeys)
            {
                if (TryGetPropertyIgnoreCase(el, rk, out var rProp) && rProp.ValueKind == JsonValueKind.String)
                {
                    rationale = rProp.GetString();
                    break;
                }
            }

            var options = ParseOptionsFromJsonElement(el);

            string prompt = string.Empty;
            foreach (var pk in PromptKeys)
            {
                if (TryGetPropertyIgnoreCase(el, pk, out var pProp))
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
                                    if (innerItems.Count == 1)
                                    {
                                        return new List<QuestionItem>
                                        {
                                            MergeSingleNestedQuestion(innerItems[0], rationale, options)
                                        };
                                    }

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
            if (TryGetPropertyIgnoreCase(el, key, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Array)
                {
                    var list = prop.EnumerateArray()
                        .Select(FormatOptionElement)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s!)
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

        if (opts is IEnumerable<string> strEnum)
        {
            var list = NormalizeOptionsList(strEnum);
            return list is { Count: > 0 } ? list : null;
        }

        if (opts is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Array)
            {
                var list = je.EnumerateArray()
                    .Select(FormatOptionElement)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!)
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

        if (opts is System.Collections.IEnumerable rawEnum && rawEnum is not string and not System.Collections.IDictionary)
        {
            var list = new List<string>();
            foreach (var opt in rawEnum)
            {
                var formatted = FormatOptionObject(opt);
                if (!string.IsNullOrWhiteSpace(formatted))
                {
                    list.Add(formatted);
                }
            }
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
                        .Select(FormatOptionElement)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s!)
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
                // Justification: repaired candidate is still not a valid JSON options array; return null cleanly.
                return null;
            }
        }

        return null;
    }

    private static bool TryParseJsonElementLenient(string raw, out JsonElement root)
    {
        var candidate = raw;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                root = doc.RootElement.Clone();
                return true;
            }
            catch (JsonException) when (attempt == 0)
            {
                var repaired = JsonRepairHelper.Repair(raw);
                if (string.Equals(repaired, raw, StringComparison.Ordinal))
                {
                    break;
                }

                candidate = repaired;
            }
            catch (JsonException)
            {
                // Justification: string is not valid JSON even after lenient repair; caller handles binding failure.
                break;
            }
        }

        root = default;
        return false;
    }

    private static bool TryBindAskJsonElement(JsonElement el, bool isRoot, out IReadOnlyList<QuestionItem>? questions)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                if (isRoot)
                {
                    questions = Array.Empty<QuestionItem>();
                    return true;
                }

                questions = null;
                return false;

            case JsonValueKind.Array:
                if (!isRoot && el.GetArrayLength() == 0)
                {
                    questions = null;
                    return false;
                }

                var list = new List<QuestionItem>();
                foreach (var item in el.EnumerateArray())
                {
                    if (!TryBindAskJsonQuestionItem(item, out var itemQuestions))
                    {
                        questions = null;
                        return false;
                    }

                    list.AddRange(itemQuestions);
                }

                questions = list;
                return true;

            case JsonValueKind.Object:
                if (isRoot)
                {
                    foreach (var key in QuestionKeys)
                    {
                        if (!TryGetPropertyIgnoreCase(el, key, out var prop))
                        {
                            continue;
                        }

                        if (string.Equals(key, SingularQuestionKey, StringComparison.OrdinalIgnoreCase) &&
                            prop.ValueKind == JsonValueKind.String)
                        {
                            continue;
                        }

                        if (prop.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                        {
                            if (string.Equals(key, SingularQuestionKey, StringComparison.OrdinalIgnoreCase) &&
                                HasOtherNonNullPromptProperty(el, key))
                            {
                                continue;
                            }

                            questions = Array.Empty<QuestionItem>();
                            return true;
                        }

                        return TryBindAskJsonElement(prop, isRoot: true, out questions);
                    }
                }

                if (TryBindAskJsonObjectItem(el, out var objQuestions))
                {
                    questions = objQuestions;
                    return true;
                }

                questions = null;
                return false;

            case JsonValueKind.String:
                var str = el.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(str))
                {
                    questions = isRoot ? Array.Empty<QuestionItem>() : new[] { new QuestionItem(str) };
                    return true;
                }

                if (LooksLikeJsonOrCodeFence(str))
                {
                    if (TryParseJsonElementLenient(str, out var innerRoot))
                    {
                        return TryBindAskJsonElement(innerRoot, isRoot: true, out questions);
                    }

                    if (LooksLikeMalformedJsonAttempt(str))
                    {
                        questions = null;
                        return false;
                    }
                }

                questions = new[] { new QuestionItem(str) };
                return true;

            default:
                questions = null;
                return false;
        }
    }

    private static bool TryBindAskJsonQuestionItem(JsonElement item, out IReadOnlyList<QuestionItem> questions)
    {
        if (item.ValueKind is JsonValueKind.String or JsonValueKind.Object)
        {
            if (TryBindAskJsonElement(item, isRoot: false, out var bound) && bound != null)
            {
                questions = bound;
                return true;
            }
        }
        else if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() > 0)
        {
            if (TryBindAskJsonElement(item, isRoot: false, out var bound) && bound != null)
            {
                questions = bound;
                return true;
            }
        }

        questions = Array.Empty<QuestionItem>();
        return false;
    }

    private static bool TryBindAskJsonObjectItem(JsonElement el, out IReadOnlyList<QuestionItem> questions)
    {
        if (!TryExtractAskOptionsFromJsonElement(el, out var options))
        {
            questions = Array.Empty<QuestionItem>();
            return false;
        }

        string? rationale = null;
        foreach (var rk in RationaleKeys)
        {
            if (TryGetPropertyIgnoreCase(el, rk, out var rProp))
            {
                if (rProp.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    break;
                }

                if (rProp.ValueKind != JsonValueKind.String)
                {
                    questions = Array.Empty<QuestionItem>();
                    return false;
                }

                rationale = rProp.GetString();
                break;
            }
        }

        string prompt = string.Empty;
        bool hasPromptKey = false;
        foreach (var pk in PromptKeys)
        {
            if (!TryGetPropertyIgnoreCase(el, pk, out var pProp))
            {
                continue;
            }

            hasPromptKey = true;

            if (pProp.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            if (pProp.ValueKind == JsonValueKind.String)
            {
                var pStr = pProp.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(pStr) && LooksLikeJsonOrCodeFence(pStr))
                {
                    if (TryParseJsonElementLenient(pStr, out var nestedRoot) &&
                        TryBindAskJsonElement(nestedRoot, isRoot: true, out var innerItems) &&
                        innerItems is { Count: > 0 })
                    {
                        if (innerItems.Count == 1 && innerItems[0] is { } first)
                        {
                            questions = new[]
                            {
                                MergeSingleNestedQuestion(first, rationale, options)
                            };
                            return true;
                        }

                        questions = innerItems;
                        return true;
                    }
                }

                prompt = pStr;
                break;
            }

            if (pProp.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                if (TryBindAskJsonElement(pProp, isRoot: true, out var inner) && inner is { Count: > 0 })
                {
                    questions = inner;
                    return true;
                }
            }

            questions = Array.Empty<QuestionItem>();
            return false;
        }

        if (!hasPromptKey)
        {
            questions = Array.Empty<QuestionItem>();
            return false;
        }

        questions = new[] { new QuestionItem(prompt, rationale, options) };
        return true;
    }

    private static QuestionItem MergeSingleNestedQuestion(
        QuestionItem inner,
        string? outerRationale,
        IReadOnlyList<string>? outerOptions)
    {
        var effectiveRationale = !string.IsNullOrWhiteSpace(inner.Rationale) ? inner.Rationale : outerRationale;
        var effectiveOptions = inner.Options is { Count: > 0 } ? inner.Options : outerOptions;
        return new QuestionItem(inner.Prompt, effectiveRationale, effectiveOptions);
    }

    private static bool HasOtherNonNullPromptProperty(JsonElement el, string excludedKey)
    {
        foreach (var key in PromptKeys.Concat(QuestionKeys))
        {
            if (string.Equals(key, excludedKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryGetPropertyIgnoreCase(el, key, out var prop) &&
                prop.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasOtherNonNullPromptEntry(IDictionary<string, object?> dict, string excludedKey)
    {
        foreach (var key in PromptKeys.Concat(QuestionKeys))
        {
            if (string.Equals(key, excludedKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryGetValueIgnoreCase(dict, key, out var val) &&
                val is not (null or JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractAskOptionsFromJsonElement(JsonElement el, out List<string>? options)
    {
        foreach (var key in OptionKeys)
        {
            if (!TryGetPropertyIgnoreCase(el, key, out var prop))
            {
                continue;
            }

            if (prop.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                options = null;
                return true;
            }

            if (prop.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in prop.EnumerateArray())
                {
                    if (item.ValueKind is JsonValueKind.Array)
                    {
                        options = null;
                        return false;
                    }

                    var formatted = FormatOptionElement(item);
                    if (!string.IsNullOrWhiteSpace(formatted))
                    {
                        list.Add(formatted);
                    }
                }

                options = list.Count > 0 ? list : null;
                return true;
            }

            if (prop.ValueKind == JsonValueKind.String)
            {
                var str = prop.GetString();
                if (string.IsNullOrWhiteSpace(str))
                {
                    options = null;
                    return true;
                }

                if (LooksLikeJsonOrCodeFence(str))
                {
                    var parsed = TryParseOptionsArrayFromJsonString(str);
                    if (parsed != null)
                    {
                        options = parsed;
                        return true;
                    }

                    options = null;
                    return false;
                }

                options = null;
                return false;
            }

            options = null;
            return false;
        }

        options = null;
        return true;
    }

    private static bool TryBindAskElementObject(object? item, out IReadOnlyList<QuestionItem> questions)
    {
        switch (item)
        {
            case null:
                questions = Array.Empty<QuestionItem>();
                return false;

            case QuestionItem qi:
                questions = Normalize(new[] { qi });
                return true;

            case JsonElement je:
                return TryBindAskJsonQuestionItem(je, out questions);

            case string str:
                if (string.IsNullOrWhiteSpace(str))
                {
                    questions = new[] { new QuestionItem(str) };
                    return true;
                }

                if (LooksLikeJsonOrCodeFence(str))
                {
                    if (TryParseJsonElementLenient(str, out var root) &&
                        TryBindAskJsonElement(root, isRoot: true, out var parsed) &&
                        parsed != null)
                    {
                        questions = parsed;
                        return true;
                    }

                    if (LooksLikeMalformedJsonAttempt(str))
                    {
                        questions = Array.Empty<QuestionItem>();
                        return false;
                    }
                }

                questions = new[] { new QuestionItem(str) };
                return true;

            case IDictionary<string, object?> dict:
                if (TryBindAskDictionary(dict, isRoot: false, out var dictQuestions) && dictQuestions != null)
                {
                    questions = dictQuestions;
                    return true;
                }

                questions = Array.Empty<QuestionItem>();
                return false;

            default:
                questions = Array.Empty<QuestionItem>();
                return false;
        }
    }

    private static bool TryBindAskDictionary(
        IDictionary<string, object?> dict,
        bool isRoot,
        out IReadOnlyList<QuestionItem>? questions)
    {
        if (isRoot)
        {
            foreach (var qk in QuestionKeys)
            {
                if (!TryGetValueIgnoreCase(dict, qk, out var nested) || ReferenceEquals(nested, dict))
                {
                    continue;
                }

                var nestedScalar = nested switch
                {
                    string s => s,
                    JsonElement nestedJe when nestedJe.ValueKind == JsonValueKind.String => nestedJe.GetString(),
                    _ => null
                };
                if (string.Equals(qk, SingularQuestionKey, StringComparison.OrdinalIgnoreCase))
                {
                    if (nestedScalar != null)
                    {
                        continue;
                    }

                    if (nested is null or JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } &&
                        HasOtherNonNullPromptEntry(dict, qk))
                    {
                        continue;
                    }
                }

                return TryBindAskQuestions(nested, out questions);
            }
        }

        if (!TryExtractAskOptionsFromDictionary(dict, out var options))
        {
            questions = null;
            return false;
        }

        string? rationale = null;
        foreach (var rk in RationaleKeys)
        {
            if (TryGetValueIgnoreCase(dict, rk, out var rVal))
            {
                if (rVal is null or JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
                {
                    break;
                }

                if (rVal is string rStr)
                {
                    rationale = rStr;
                    break;
                }

                if (rVal is JsonElement rJe && rJe.ValueKind == JsonValueKind.String)
                {
                    rationale = rJe.GetString();
                    break;
                }

                questions = null;
                return false;
            }
        }

        string prompt = string.Empty;
        bool hasPromptKey = false;
        foreach (var pk in PromptKeys)
        {
            if (!TryGetValueIgnoreCase(dict, pk, out var pVal))
            {
                continue;
            }

            hasPromptKey = true;

            if (pVal is null or JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
            {
                continue;
            }

            string? promptStr = pVal switch
            {
                string s => s,
                JsonElement pJe when pJe.ValueKind == JsonValueKind.String => pJe.GetString() ?? string.Empty,
                _ => null
            };

            if (promptStr != null)
            {
                if (!string.IsNullOrWhiteSpace(promptStr) && LooksLikeJsonOrCodeFence(promptStr))
                {
                    if (TryParseJsonElementLenient(promptStr, out var nestedRoot) &&
                        TryBindAskJsonElement(nestedRoot, isRoot: true, out var innerItems) &&
                        innerItems is { Count: > 0 })
                    {
                        if (innerItems.Count == 1 && innerItems[0] is { } first)
                        {
                            questions = new[]
                            {
                                MergeSingleNestedQuestion(first, rationale, options)
                            };
                            return true;
                        }

                        questions = innerItems;
                        return true;
                    }
                }

                prompt = promptStr;
                break;
            }

            if (pVal is IDictionary<string, object?> or IEnumerable<object?> or JsonElement { ValueKind: JsonValueKind.Object or JsonValueKind.Array })
            {
                if (TryBindAskQuestions(pVal, out var inner) && inner is { Count: > 0 })
                {
                    questions = inner;
                    return true;
                }
            }

            questions = null;
            return false;
        }

        if (!hasPromptKey)
        {
            questions = Array.Empty<QuestionItem>();
            return false;
        }

        questions = new[] { new QuestionItem(prompt, rationale, options) };
        return true;
    }

    private static bool TryExtractAskOptionsFromDictionary(IDictionary<string, object?> dict, out List<string>? options)
    {
        foreach (var key in OptionKeys)
        {
            if (!TryGetValueIgnoreCase(dict, key, out var rawOpts))
            {
                continue;
            }

            if (rawOpts is null or JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
            {
                options = null;
                return true;
            }

            if (rawOpts is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.Array)
                {
                    var jeList = new List<string>();
                    foreach (var item in je.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Array)
                        {
                            options = null;
                            return false;
                        }

                        var formatted = FormatOptionElement(item);
                        if (!string.IsNullOrWhiteSpace(formatted))
                        {
                            jeList.Add(formatted);
                        }
                    }

                    options = jeList.Count > 0 ? jeList : null;
                    return true;
                }

                if (je.ValueKind == JsonValueKind.String)
                {
                    rawOpts = je.GetString();
                }
                else
                {
                    options = null;
                    return false;
                }
            }

            if (rawOpts is IEnumerable<string> strEnum)
            {
                options = NormalizeOptionsList(strEnum);
                return true;
            }

            if (rawOpts is string optStr)
            {
                if (string.IsNullOrWhiteSpace(optStr))
                {
                    options = null;
                    return true;
                }

                if (LooksLikeJsonOrCodeFence(optStr))
                {
                    var parsed = TryParseOptionsArrayFromJsonString(optStr);
                    if (parsed != null)
                    {
                        options = parsed;
                        return true;
                    }
                }

                options = null;
                return false;
            }

            if (rawOpts is System.Collections.IDictionary)
            {
                options = null;
                return false;
            }

            if (rawOpts is System.Collections.IEnumerable rawEnum)
            {
                var list = new List<string>();
                foreach (var opt in rawEnum)
                {
                    if (opt is System.Collections.IEnumerable and not string and not System.Collections.IDictionary and not IEnumerable<KeyValuePair<string, object?>> and not IEnumerable<KeyValuePair<string, string>>)
                    {
                        options = null;
                        return false;
                    }

                    var formatted = FormatOptionObject(opt);
                    if (!string.IsNullOrWhiteSpace(formatted))
                    {
                        list.Add(formatted);
                    }
                }

                options = list.Count > 0 ? list : null;
                return true;
            }

            options = null;
            return false;
        }

        options = null;
        return true;
    }

    private static bool LooksLikeJsonOrCodeFence(string value)
    {
        var trimmed = value.AsSpan().TrimStart();
        return trimmed.StartsWith("{") || trimmed.StartsWith("[") || trimmed.StartsWith("```");
    }

    private static bool LooksLikeMalformedJsonAttempt(string value)
    {
        var trimmed = value.AsSpan().TrimStart();
        if (trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.StartsWith("{"))
        {
            var afterBrace = trimmed[1..].TrimStart();
            return afterBrace.IsEmpty || afterBrace[0] is '"' or '\'' or '}';
        }

        if (trimmed.StartsWith("["))
        {
            var afterBracket = trimmed[1..].TrimStart();
            return afterBracket.IsEmpty || afterBracket[0] is '{' or '"' or '\'' or '[' or ']';
        }

        return false;
    }

    private static bool HasUnnormalizedOptions(IReadOnlyList<string>? options)
    {
        if (options == null)
        {
            return false;
        }

        if (options.Count == 0)
        {
            return true;
        }

        foreach (var opt in options)
        {
            if (string.IsNullOrWhiteSpace(opt) || !string.Equals(opt, opt.Trim(), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string>? NormalizeOptionsList(IEnumerable<string>? options)
    {
        if (options == null)
        {
            return null;
        }

        var normalized = options
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToList();

        return normalized.Count > 0 ? normalized : null;
    }

    private static string? FormatOptionElement(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                var str = el.GetString();
                return string.IsNullOrWhiteSpace(str) ? null : str.Trim();

            case JsonValueKind.Object:
                var label = FindJsonOptionField(el, OptionLabelKeys);
                var description = FindJsonOptionField(el, OptionDescriptionKeys);
                return CombineOptionParts(label, description) ?? FindFirstJsonScalarField(el);

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return el.ToString().Trim();

            default:
                return null;
        }
    }

    private static string? FormatOptionObject(object? opt)
    {
        switch (opt)
        {
            case null:
                return null;
            case string str:
                return string.IsNullOrWhiteSpace(str) ? null : str.Trim();
            case JsonElement je:
                return FormatOptionElement(je);
            case IEnumerable<KeyValuePair<string, object?>> objPairs:
                return FormatOptionPairs(objPairs);
            case IEnumerable<KeyValuePair<string, string>> strPairs:
                return FormatOptionPairs(strPairs.Select(p => new KeyValuePair<string, object?>(p.Key, p.Value)));
            case System.Collections.IDictionary nonGenericDict:
                return FormatOptionPairs(EnumerateNonGenericDictionary(nonGenericDict));
            case System.Collections.IEnumerable:
                return null;
            default:
                var text = opt.ToString()?.Trim();
                return string.IsNullOrWhiteSpace(text) ? null : text;
        }
    }

    private static IEnumerable<KeyValuePair<string, object?>> EnumerateNonGenericDictionary(System.Collections.IDictionary dict)
    {
        foreach (System.Collections.DictionaryEntry entry in dict)
        {
            if (entry.Key is string key)
            {
                yield return new KeyValuePair<string, object?>(key, entry.Value);
            }
        }
    }

    private static string? FormatOptionPairs(IEnumerable<KeyValuePair<string, object?>> pairs)
    {
        var materialised = pairs as IReadOnlyList<KeyValuePair<string, object?>> ?? pairs.ToList();
        return CombineOptionParts(
            FindPairOptionField(materialised, OptionLabelKeys),
            FindPairOptionField(materialised, OptionDescriptionKeys))
            ?? FindFirstPairScalarField(materialised);
    }

    private static string? FindFirstJsonScalarField(JsonElement obj)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            var extracted = ExtractScalarString(prop.Value);
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                return extracted;
            }
        }

        return null;
    }

    private static string? FindFirstPairScalarField(IReadOnlyList<KeyValuePair<string, object?>> pairs)
    {
        foreach (var pair in pairs)
        {
            var extracted = ExtractScalarObject(pair.Value);
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                return extracted;
            }
        }

        return null;
    }

    private static string? FindJsonOptionField(JsonElement obj, string[] candidateKeys)
    {
        foreach (var candidateKey in candidateKeys)
        {
            if (TryGetPropertyIgnoreCase(obj, candidateKey, out var prop))
            {
                var extracted = ExtractScalarString(prop);
                if (!string.IsNullOrWhiteSpace(extracted))
                {
                    return extracted;
                }
            }
        }

        return null;
    }

    private static string? FindPairOptionField(IReadOnlyList<KeyValuePair<string, object?>> pairs, string[] candidateKeys)
    {
        foreach (var candidateKey in candidateKeys)
        {
            foreach (var pair in pairs)
            {
                if (string.Equals(pair.Key, candidateKey, StringComparison.OrdinalIgnoreCase))
                {
                    var extracted = ExtractScalarObject(pair.Value);
                    if (!string.IsNullOrWhiteSpace(extracted))
                    {
                        return extracted;
                    }
                    break;
                }
            }
        }

        return null;
    }

    private static string? ExtractScalarObject(object? value) =>
        value switch
        {
            null => null,
            string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
            JsonElement je => ExtractScalarString(je),
            System.Collections.IDictionary => null,
            System.Collections.IEnumerable => null,
            _ => value.ToString()?.Trim() is { Length: > 0 } trimmed ? trimmed : null
        };

    private static string? ExtractScalarString(JsonElement prop) =>
        prop.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(prop.GetString()) ? null : prop.GetString()!.Trim(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => prop.ToString().Trim(),
            _ => null
        };

    private static string? CombineOptionParts(string? label, string? description)
    {
        if (!string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(description))
        {
            return string.Equals(label, description, StringComparison.OrdinalIgnoreCase)
                ? label
                : $"{label} — {description}";
        }

        if (!string.IsNullOrWhiteSpace(label))
        {
            return label;
        }

        return !string.IsNullOrWhiteSpace(description) ? description : null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string propertyName, out JsonElement value)
    {
        if (obj.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetValueIgnoreCase(IDictionary<string, object?> dict, string key, out object? value)
    {
        if (dict.TryGetValue(key, out value))
        {
            return true;
        }

        foreach (var pair in dict)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string GetQuestionPrompt(IDictionary<string, object?> dict)
    {
        foreach (var k in PromptKeys)
        {
            if (TryGetValueIgnoreCase(dict, k, out var p) && p != null)
            {
                var extracted = ExtractRawStringOrScalar(p);
                if (!string.IsNullOrWhiteSpace(extracted))
                {
                    return extracted;
                }
            }
        }
        return "";
    }

    private static string? GetQuestionRationale(IDictionary<string, object?> dict)
    {
        foreach (var k in RationaleKeys)
        {
            if (TryGetValueIgnoreCase(dict, k, out var r) && r != null)
            {
                var extracted = ExtractRawStringOrScalar(r);
                if (extracted != null)
                {
                    return extracted;
                }
            }
        }
        return null;
    }

    private static string? ExtractRawStringOrScalar(object? value) =>
        value switch
        {
            null => null,
            string s => s,
            JsonElement je => je.ValueKind switch
            {
                JsonValueKind.String => je.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => je.ToString(),
                _ => null
            },
            System.Collections.IDictionary => null,
            System.Collections.IEnumerable => null,
            _ => value.ToString()
        };

    private static List<string>? ExtractOptionsFromDictionary(IDictionary<string, object?> dict)
    {
        foreach (var key in OptionKeys)
        {
            if (TryGetValueIgnoreCase(dict, key, out var opts) && opts != null)
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
