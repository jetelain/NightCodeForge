using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace NightCodeForge.Core.Services;

/// <summary>
/// Custom YamlDotNet type converter for TaskDependencyReferenceList.
/// Supports serializing and deserializing mixed integer IDs and string references.
/// </summary>
public class TaskDependencyReferenceListYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(TaskDependencyReferenceList);

    private static bool IsNullScalar(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        string.Equals(value, "null", StringComparison.OrdinalIgnoreCase) ||
        value == "~";

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.TryConsume<SequenceStart>(out _))
        {
            var list = new TaskDependencyReferenceList();
            while (!parser.TryConsume<SequenceEnd>(out _))
            {
                if (parser.TryConsume<Scalar>(out var scalar))
                {
                    if (IsNullScalar(scalar.Value))
                    {
                        continue;
                    }

                    if (int.TryParse(scalar.Value, out var intId))
                    {
                        list.Add(intId);
                    }
                    else
                    {
                        list.Add(scalar.Value.Trim());
                    }
                }
                else
                {
                    parser.SkipThisAndNestedEvents();
                }
            }
            return list;
        }

        if (parser.TryConsume<Scalar>(out var singleScalar))
        {
            if (IsNullScalar(singleScalar.Value)) return null;
            var list = new TaskDependencyReferenceList();
            if (int.TryParse(singleScalar.Value, out var intId))
            {
                list.Add(intId);
            }
            else
            {
                list.Add(singleScalar.Value.Trim());
            }
            return list;
        }

        return null;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer rootSerializer)
    {
        if (value is TaskDependencyReferenceList list)
        {
            if (list.Count == 0)
            {
                emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, isImplicit: true, SequenceStyle.Flow));
                emitter.Emit(new SequenceEnd());
                return;
            }

            emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, isImplicit: true, SequenceStyle.Block));
            foreach (var item in list)
            {
                emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, item?.ToString() ?? string.Empty, ScalarStyle.Any, isPlainImplicit: true, isQuotedImplicit: false));
            }
            emitter.Emit(new SequenceEnd());
        }
    }
}
