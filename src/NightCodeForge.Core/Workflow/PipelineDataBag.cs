using System.Collections.Concurrent;
using System.Text.Json;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Thread-safe, serializable property bag persisting cross-stage metrics and artifacts across workflow executions.
/// </summary>
public class PipelineDataBag
{
    private readonly ConcurrentDictionary<string, JsonElement> _data = new();

    public PipelineDataBag()
    {
    }

    public PipelineDataBag(IDictionary<string, JsonElement> initialData)
    {
        foreach (var (k, v) in initialData)
        {
            _data[k] = v;
        }
    }

    public void Set<T>(string key, T value)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        var element = JsonSerializer.SerializeToElement(value);
        _data[key] = element;
    }

    public T? Get<T>(string key)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        if (_data.TryGetValue(key, out var element))
        {
            return JsonSerializer.Deserialize<T>(element);
        }
        return default;
    }

    public bool TryGet<T>(string key, out T? value)
    {
        if (key != null && _data.TryGetValue(key, out var element))
        {
            try
            {
                value = JsonSerializer.Deserialize<T>(element);
                return true;
            }
            catch
            {
                value = default;
                return false;
            }
        }
        value = default;
        return false;
    }

    public bool ContainsKey(string key)
    {
        return key != null && _data.ContainsKey(key);
    }

    public bool Remove(string key)
    {
        return key != null && _data.TryRemove(key, out _);
    }

    public IReadOnlyDictionary<string, JsonElement> GetAll()
    {
        return new Dictionary<string, JsonElement>(_data);
    }

    public string ToJson()
    {
        return JsonSerializer.Serialize(_data);
    }

    public static PipelineDataBag FromJson(string? json)
    {
        var bag = new PipelineDataBag();
        if (string.IsNullOrWhiteSpace(json))
        {
            return bag;
        }

        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (dict != null)
            {
                foreach (var (key, value) in dict)
                {
                    bag._data[key] = value;
                }
            }
        }
        catch
        {
            // Fallback for empty or corrupt payload
        }

        return bag;
    }
}
