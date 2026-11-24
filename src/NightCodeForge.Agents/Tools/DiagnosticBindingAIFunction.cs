using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace NightCodeForge.Agents.Tools;

/// <summary>
/// Wraps an <see cref="AIFunction"/> so JSON deserialization, type-conversion, or parameter-binding
/// failures return an actionable diagnostic string (including failing parameter and expected schema shape)
/// instead of letting <see cref="FunctionInvokingChatClient"/> collapse them into <c>"Error: Function failed."</c>.
/// </summary>
public sealed class DiagnosticBindingAIFunction : DelegatingAIFunction
{
    private const int MaxSchemaDepth = 6;
    private const int MaxEnumPreviewCount = 6;
    private const string SchemaPropertiesKey = "properties";
    private const string SchemaRequiredKey = "required";
    private const string SchemaTypeKey = "type";
    private const string SchemaItemsKey = "items";
    private const string SchemaEnumKey = "enum";
    private const string SchemaAnyOfKey = "anyOf";
    private const string SchemaOneOfKey = "oneOf";
    private const string SchemaRefKey = "$ref";
    private const string SchemaNullType = "null";
    private const string SchemaArrayType = "array";
    private const string SchemaObjectType = "object";
    private const string FallbackAnyType = "any";
    private const string LocalRefPrefix = "#/";

    private DiagnosticBindingAIFunction(AIFunction innerFunction)
        : base(innerFunction)
    {
    }

    /// <summary>
    /// Wraps <paramref name="innerFunction"/> with binding-diagnostic handling unless already wrapped
    /// (directly or beneath an outer <see cref="DelegatingAIFunction"/>).
    /// </summary>
    public static AIFunction Wrap(AIFunction innerFunction)
    {
        ArgumentNullException.ThrowIfNull(innerFunction);
        return innerFunction is DiagnosticBindingAIFunction ||
               innerFunction.GetService<DiagnosticBindingAIFunction>() is not null
            ? innerFunction
            : new DiagnosticBindingAIFunction(innerFunction);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            return await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (TryAnalyzeBindingFailure(InnerFunction, arguments, ex, out var bindingEx, out var failingParam))
        {
            return FormatDiagnosticMessage(InnerFunction, bindingEx, failingParam);
        }
    }

    private static bool TryAnalyzeBindingFailure(
        AIFunction function,
        AIFunctionArguments arguments,
        Exception ex,
        out Exception bindingEx,
        out string? failingParam)
    {
        var candidate = UnwrapBindingException(ex);
        if (candidate == null)
        {
            bindingEx = ex;
            failingParam = null;
            return false;
        }

        bindingEx = candidate;

        if (function.UnderlyingMethod is { } method)
        {
            if (TryFindFailingMethodParameter(method, function.JsonSerializerOptions, arguments, out failingParam, out var marshalFailure))
            {
                if (marshalFailure != null &&
                    candidate is ArgumentException argEx &&
                    string.IsNullOrEmpty(argEx.ParamName))
                {
                    bindingEx = marshalFailure;
                }

                return true;
            }

            // Every declared parameter bound cleanly, so the exception came from inside the tool body.
            failingParam = null;
            return false;
        }

        failingParam = ResolveFailingParameterFromSchema(candidate, arguments, function.JsonSchema);
        return true;
    }

    private static Exception? UnwrapBindingException(Exception ex)
    {
        var current = ex;
        while (current is TargetInvocationException tie && tie.InnerException != null)
        {
            current = tie.InnerException;
        }

        return current is JsonException
            or ArgumentException
            or FormatException
            or InvalidCastException
            or OverflowException
            or NotSupportedException
            ? current
            : null;
    }

    private static bool TryFindFailingMethodParameter(
        MethodInfo method,
        JsonSerializerOptions? serializerOptions,
        AIFunctionArguments arguments,
        out string? failingParam,
        out Exception? marshalFailure)
    {
        marshalFailure = null;
        var effectiveOptions = serializerOptions ?? AIJsonUtilities.DefaultOptions;
        var parameters = method.GetParameters();

        if (effectiveOptions.UnmappedMemberHandling == System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow &&
            arguments.Count > 0)
        {
            var expectedNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var param in parameters)
            {
                if (!IsSpecialFrameworkParameter(param.ParameterType) && !string.IsNullOrWhiteSpace(param.Name))
                {
                    expectedNames.Add(ResolveParameterSchemaName(param));
                }
            }

            foreach (var key in arguments.Keys)
            {
                if (!expectedNames.Contains(key))
                {
                    failingParam = key;
                    return true;
                }
            }
        }

        foreach (var param in parameters)
        {
            if (IsSpecialFrameworkParameter(param.ParameterType) || string.IsNullOrWhiteSpace(param.Name))
            {
                continue;
            }

            var argName = ResolveParameterSchemaName(param);
            if (!arguments.TryGetValue(argName, out var rawValue))
            {
                if (!HasEffectiveDefaultValue(param))
                {
                    failingParam = argName;
                    return true;
                }

                continue;
            }

            if (rawValue is null || param.ParameterType.IsInstanceOfType(rawValue))
            {
                continue;
            }

            if (!TryMarshalParameterValue(rawValue, param.ParameterType, effectiveOptions, out marshalFailure))
            {
                failingParam = argName;
                return true;
            }
        }

        failingParam = null;
        return false;
    }

    private static string ResolveParameterSchemaName(ParameterInfo parameter) =>
        parameter.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name
        ?? parameter.Name
        ?? string.Empty;

    private static bool IsSpecialFrameworkParameter(Type parameterType) =>
        parameterType == typeof(CancellationToken) ||
        parameterType == typeof(AIFunctionArguments) ||
        parameterType == typeof(IServiceProvider);

    private static bool HasEffectiveDefaultValue(ParameterInfo parameter) =>
        parameter.HasDefaultValue ||
        parameter.IsOptional ||
        parameter.GetCustomAttribute<DefaultValueAttribute>() != null;

    private static bool TryMarshalParameterValue(
        object rawValue,
        Type parameterType,
        JsonSerializerOptions serializerOptions,
        out Exception? failure)
    {
        failure = null;
        try
        {
            object? converted;
            switch (rawValue)
            {
                case JsonElement element:
                    converted = element.Deserialize(parameterType, serializerOptions);
                    break;
                case JsonDocument document:
                    converted = document.Deserialize(parameterType, serializerOptions);
                    break;
                case JsonNode node:
                    converted = node.Deserialize(parameterType, serializerOptions);
                    break;
                default:
                    if (rawValue is string text && IsPotentiallyJson(text))
                    {
                        try
                        {
                            converted = JsonSerializer.Deserialize(text, parameterType, serializerOptions);
                            return converted is null || parameterType.IsInstanceOfType(converted);
                        }
                        catch (JsonException)
                        {
                            // Fall back to serializing the raw string as JSON, matching AIFunctionFactory.
                        }
                    }

                    var json = JsonSerializer.Serialize(rawValue, rawValue.GetType(), serializerOptions);
                    converted = JsonSerializer.Deserialize(json, parameterType, serializerOptions);
                    break;
            }

            return converted is null || parameterType.IsInstanceOfType(converted);
        }
        catch (Exception ex) when (ex is JsonException
            or ArgumentException
            or FormatException
            or InvalidCastException
            or OverflowException
            or NotSupportedException)
        {
            failure = ex;
            return false;
        }
    }

    private static bool IsPotentiallyJson(string value)
    {
        var trimmed = value.AsSpan().TrimStart();
        if (trimmed.IsEmpty)
        {
            return false;
        }

        char c = trimmed[0];
        return c is '"' or '[' or '{' ||
               char.IsAsciiDigit(c) ||
               c == '-' ||
               trimmed.StartsWith("null", StringComparison.Ordinal) ||
               trimmed.StartsWith("true", StringComparison.Ordinal) ||
               trimmed.StartsWith("false", StringComparison.Ordinal) ||
               trimmed.StartsWith("//", StringComparison.Ordinal) ||
               trimmed.StartsWith("/*", StringComparison.Ordinal);
    }

    private static string FormatDiagnosticMessage(
        AIFunction function,
        Exception bindingEx,
        string? failingParam)
    {
        var schema = function.JsonSchema;
        var jsonPath = (bindingEx as JsonException)?.Path;
        var expectedShape = FormatExpectedShape(schema);

        var location = failingParam != null
            ? (!string.IsNullOrWhiteSpace(jsonPath)
                ? $" (parameter '{failingParam}', path '{jsonPath}')"
                : $" (parameter '{failingParam}')")
            : (!string.IsNullOrWhiteSpace(jsonPath)
                ? $" (path '{jsonPath}')"
                : string.Empty);

        var reason = bindingEx.Message.ReplaceLineEndings(" ").Trim();
        return $"Error: Invalid arguments for tool '{function.Name}'{location}: {reason} Expected parameters: {expectedShape}.";
    }

    private static string? ResolveFailingParameterFromSchema(
        Exception bindingEx,
        AIFunctionArguments arguments,
        JsonElement schema)
    {
        var declaredProperties = new HashSet<string>(StringComparer.Ordinal);
        if (schema.ValueKind == JsonValueKind.Object &&
            schema.TryGetProperty(SchemaPropertiesKey, out var props) &&
            props.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in props.EnumerateObject())
            {
                declaredProperties.Add(prop.Name);
            }

            if (schema.TryGetProperty(SchemaRequiredKey, out var required) && required.ValueKind == JsonValueKind.Array)
            {
                foreach (var reqItem in required.EnumerateArray())
                {
                    if (reqItem.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var reqName = reqItem.GetString();
                    if (!string.IsNullOrWhiteSpace(reqName) && !arguments.ContainsKey(reqName))
                    {
                        return reqName;
                    }
                }
            }
        }

        if (bindingEx is ArgumentException argEx &&
            !string.IsNullOrWhiteSpace(argEx.ParamName) &&
            declaredProperties.Contains(argEx.ParamName))
        {
            return argEx.ParamName;
        }

        var jsonPath = (bindingEx as JsonException)?.Path;
        if (!string.IsNullOrWhiteSpace(jsonPath))
        {
            var rootProperty = ExtractRootPropertyFromJsonPath(jsonPath);
            if (!string.IsNullOrWhiteSpace(rootProperty) && declaredProperties.Contains(rootProperty))
            {
                return rootProperty;
            }
        }

        return null;
    }

    private static string? ExtractRootPropertyFromJsonPath(string jsonPath)
    {
        var trimmed = jsonPath.Trim();
        if (trimmed.StartsWith("$.", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }
        else if (trimmed.StartsWith('$'))
        {
            trimmed = trimmed[1..];
        }

        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var end = trimmed.IndexOfAny(['.', '[']);
        return end < 0 ? trimmed : (end == 0 ? null : trimmed[..end]);
    }

    private static string FormatExpectedShape(JsonElement schema)
    {
        var resolvedRoot = ResolveLocalSchemaRef(schema, schema);
        if (resolvedRoot.ValueKind == JsonValueKind.Object &&
            resolvedRoot.TryGetProperty(SchemaPropertiesKey, out var props) &&
            props.ValueKind == JsonValueKind.Object)
        {
            return FormatObjectProperties(resolvedRoot, props, schema, depth: 0, wrapInBraces: true);
        }

        return schema.ValueKind == JsonValueKind.Undefined ? "{}" : schema.GetRawText();
    }

    private static string FormatObjectProperties(
        JsonElement objectSchema,
        JsonElement properties,
        JsonElement rootSchema,
        int depth,
        bool wrapInBraces)
    {
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (objectSchema.TryGetProperty(SchemaRequiredKey, out var reqEl) && reqEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in reqEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var name = item.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    required.Add(name);
                }
            }
        }

        var fields = new List<string>();
        foreach (var prop in properties.EnumerateObject())
        {
            var optMarker = required.Contains(prop.Name) ? string.Empty : "?";
            fields.Add($"{prop.Name}{optMarker}: {FormatSchemaType(prop.Value, rootSchema, depth + 1)}");
        }

        var joined = string.Join(", ", fields);
        return wrapInBraces ? $"{{{joined}}}" : joined;
    }

    private static string FormatSchemaType(JsonElement schema, JsonElement rootSchema, int depth)
    {
        if (depth > MaxSchemaDepth || schema.ValueKind != JsonValueKind.Object)
        {
            return FallbackAnyType;
        }

        var resolved = ResolveLocalSchemaRef(schema, rootSchema);
        if (resolved.ValueKind != JsonValueKind.Object)
        {
            return FallbackAnyType;
        }

        if ((resolved.TryGetProperty(SchemaAnyOfKey, out var union) || resolved.TryGetProperty(SchemaOneOfKey, out union)) &&
            union.ValueKind == JsonValueKind.Array)
        {
            var nonNullBranches = new List<string>();
            foreach (var branch in union.EnumerateArray())
            {
                if (IsNullTypeSchema(branch))
                {
                    continue;
                }
                nonNullBranches.Add(FormatSchemaType(branch, rootSchema, depth));
            }

            if (nonNullBranches.Count > 0)
            {
                return string.Join(" | ", nonNullBranches.Distinct(StringComparer.Ordinal));
            }
        }

        var primaryType = ResolvePrimaryType(resolved);
        if (string.Equals(primaryType, SchemaArrayType, StringComparison.OrdinalIgnoreCase))
        {
            if (resolved.TryGetProperty(SchemaItemsKey, out var items))
            {
                return $"array of {FormatSchemaType(items, rootSchema, depth + 1)}";
            }
            return SchemaArrayType;
        }

        if (string.Equals(primaryType, SchemaObjectType, StringComparison.OrdinalIgnoreCase) ||
            resolved.TryGetProperty(SchemaPropertiesKey, out _))
        {
            if (resolved.TryGetProperty(SchemaPropertiesKey, out var props) && props.ValueKind == JsonValueKind.Object)
            {
                return FormatObjectProperties(resolved, props, rootSchema, depth, wrapInBraces: true);
            }
            return SchemaObjectType;
        }

        if (resolved.TryGetProperty(SchemaEnumKey, out var enumEl) && enumEl.ValueKind == JsonValueKind.Array)
        {
            var values = enumEl.EnumerateArray().Select(e => e.ToString()).Take(MaxEnumPreviewCount).ToList();
            if (values.Count > 0)
            {
                return $"enum({string.Join("|", values)})";
            }
        }

        return primaryType ?? FallbackAnyType;
    }

    private static JsonElement ResolveLocalSchemaRef(JsonElement schema, JsonElement rootSchema)
    {
        var current = schema;
        for (int hops = 0; hops < MaxSchemaDepth; hops++)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(SchemaRefKey, out var refEl) ||
                refEl.ValueKind != JsonValueKind.String)
            {
                return current;
            }

            var pointer = refEl.GetString();
            if (string.IsNullOrWhiteSpace(pointer) || !pointer.StartsWith(LocalRefPrefix, StringComparison.Ordinal))
            {
                return current;
            }

            var target = rootSchema;
            bool resolved = true;
            foreach (var rawSegment in pointer[LocalRefPrefix.Length..].Split('/'))
            {
                var segment = rawSegment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                if (target.ValueKind == JsonValueKind.Object && target.TryGetProperty(segment, out var next))
                {
                    target = next;
                }
                else
                {
                    resolved = false;
                    break;
                }
            }

            if (!resolved)
            {
                return current;
            }

            current = target;
        }

        return current;
    }

    private static bool IsNullTypeSchema(JsonElement branch)
    {
        if (branch.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (branch.TryGetProperty(SchemaTypeKey, out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
        {
            return string.Equals(typeEl.GetString(), SchemaNullType, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string? ResolvePrimaryType(JsonElement schema)
    {
        if (!schema.TryGetProperty(SchemaTypeKey, out var typeEl))
        {
            return null;
        }

        if (typeEl.ValueKind == JsonValueKind.String)
        {
            return typeEl.GetString();
        }

        if (typeEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in typeEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var candidate = item.GetString();
                if (!string.IsNullOrWhiteSpace(candidate) &&
                    !string.Equals(candidate, SchemaNullType, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
