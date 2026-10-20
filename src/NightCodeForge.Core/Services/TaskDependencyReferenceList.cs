using System.Collections;

namespace NightCodeForge.Core.Services;

/// <summary>
/// Flexible collection of dependency references for task frontmatter.
/// Supports both integer database IDs (e.g. 42) and string references (e.g. "base-models", "2026-10-20-base-models.md").
/// </summary>
public class TaskDependencyReferenceList : IList<object>
{
    private readonly List<object> _items;

    public TaskDependencyReferenceList()
    {
        _items = new List<object>();
    }

    public TaskDependencyReferenceList(IEnumerable<object> items)
    {
        _items = items != null ? items.Where(x => x != null).ToList() : new List<object>();
    }

    public TaskDependencyReferenceList(IEnumerable<int> ids)
    {
        _items = ids != null ? ids.Select(id => (object)id).ToList() : new List<object>();
    }

    public TaskDependencyReferenceList(IEnumerable<string> refs)
    {
        _items = refs != null ? refs.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => (object)r).ToList() : new List<object>();
    }

    public static implicit operator TaskDependencyReferenceList(List<int> ids) => new(ids);
    public static implicit operator TaskDependencyReferenceList(int[] ids) => new(ids);
    public static implicit operator TaskDependencyReferenceList(List<string> refs) => new(refs);
    public static implicit operator TaskDependencyReferenceList(string[] refs) => new(refs);

    public int Count => _items.Count;
    public bool IsReadOnly => false;

    public object this[int index]
    {
        get => _items[index];
        set => _items[index] = value ?? throw new ArgumentNullException(nameof(value));
    }

    public void Add(object item)
    {
        if (item != null) _items.Add(item);
    }

    public void Add(int id) => _items.Add(id);
    public void Add(string reference)
    {
        if (!string.IsNullOrWhiteSpace(reference)) _items.Add(reference);
    }

    public void Clear() => _items.Clear();

    public bool Contains(object item)
    {
        if (item == null) return false;
        foreach (var existing in _items)
        {
            if (Equals(existing, item)) return true;
            if (item is int intVal && int.TryParse(existing?.ToString(), out var parsedInt) && parsedInt == intVal)
                return true;
            if (item is string strVal && string.Equals(existing?.ToString(), strVal, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public void CopyTo(object[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

    public int IndexOf(object item)
    {
        if (item == null) return -1;
        for (int i = 0; i < _items.Count; i++)
        {
            var existing = _items[i];
            if (Equals(existing, item)) return i;
            if (item is int intVal && int.TryParse(existing?.ToString(), out var parsedInt) && parsedInt == intVal)
                return i;
            if (item is string strVal && string.Equals(existing?.ToString(), strVal, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    public void Insert(int index, object item) => _items.Insert(index, item ?? throw new ArgumentNullException(nameof(item)));

    public bool Remove(object item)
    {
        var idx = IndexOf(item);
        if (idx >= 0)
        {
            _items.RemoveAt(idx);
            return true;
        }
        return false;
    }

    public void RemoveAt(int index) => _items.RemoveAt(index);

    public IEnumerator<object> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    public override bool Equals(object? obj)
    {
        if (obj == null) return false;
        if (obj is string) return false;

        if (obj is IEnumerable<int> intEnum)
        {
            var intList = intEnum.ToList();
            if (intList.Count != _items.Count) return false;
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i] is int id && id == intList[i]) continue;
                if (int.TryParse(_items[i]?.ToString(), out var parsed) && parsed == intList[i]) continue;
                return false;
            }
            return true;
        }

        if (obj is IEnumerable enumObj)
        {
            var otherList = enumObj.Cast<object>().ToList();
            if (otherList.Count != _items.Count) return false;
            for (int i = 0; i < _items.Count; i++)
            {
                if (Equals(_items[i], otherList[i])) continue;
                if (string.Equals(_items[i]?.ToString(), otherList[i]?.ToString(), StringComparison.OrdinalIgnoreCase)) continue;
                return false;
            }
            return true;
        }

        return base.Equals(obj);
    }

    public override int GetHashCode()
    {
        int hash = 17;
        foreach (var item in _items)
        {
            hash = hash * 31 + (item?.ToString()?.ToLowerInvariant().GetHashCode() ?? 0);
        }
        return hash;
    }
}
