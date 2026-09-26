using System.Globalization;
using System.Reflection;
using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>Inspector selection, edits, validation, and expansion state, without holding controls.</summary>
public sealed class InspectorViewModel(EditorDocuments documents) : EditorObservable
{
    private readonly Dictionary<Guid, string> _errors = [];
    private bool _selecting;
    public event Action<EditedDocumentKind>? DocumentEdited;
    public SceneObject? SelectedObject { get; private set; }
    public IReadOnlyList<object> Components { get; private set; } = [];
    public Dictionary<string, bool> CollapsedCards { get; } = [with(StringComparer.Ordinal)];
    public Dictionary<string, bool> CollapsedMembers { get; } = [with(StringComparer.Ordinal)];
    public bool IsReadOnly { get; set => SetProperty(ref field, value); }
    public bool HasNameError { get; private set; }
    public int InvalidCount => _errors.Count;
    public bool HasFieldErrors => InvalidCount != 0;
    public bool HasInputErrors => HasNameError || InvalidCount != 0;
    public bool HasObject => SelectedObject is not null;
    public bool HasNoComponents => HasObject && Components.Count == 0;
    public string ObjectId => SelectedObject?.Id.ToString() ?? "";
    public string ComponentsTitle => $"Components ({Components.Count})";
    public string ErrorText => $"Error {InvalidCount}";
    public string? ErrorHint => InvalidCount == 0 ? null
        : $"{InvalidCount} field(s) have invalid input — fix the highlighted fields to save.";

    public string Name
    {
        get;
        set
        {
            if (!SetProperty(ref field, value ?? "") || _selecting || IsReadOnly) return;
            HasNameError = SelectedObject is not null && string.IsNullOrWhiteSpace(field);
            Changed(nameof(HasNameError));
            Changed(nameof(HasInputErrors));
            if (!HasNameError && SelectedObject is { } item && item.Name != field.Trim())
            {
                item.Rename(field);
                MarkEdited(null);
            }
        }
    } = "";

    public void Select(SceneObject? item)
    {
        _selecting = true;
        try
        {
            SelectedObject = item;
            Name = item?.Name ?? "";
            HasNameError = false;
            RefreshComponents();
            Changed(nameof(SelectedObject));
            Changed(nameof(ObjectId));
            Changed(nameof(HasObject));
            Changed(nameof(HasNameError));
            Changed(nameof(HasInputErrors));
        }
        finally { _selecting = false; }
    }

    public void RefreshComponents()
    {
        Components = SelectedObject is { } item ? [.. item.Components] : [];
        Changed(nameof(Components));
        Changed(nameof(ComponentsTitle));
        Changed(nameof(HasNoComponents));
    }

    public void SetError(Guid fieldId, string? message)
    {
        if (message is null) _errors.Remove(fieldId);
        else _errors[fieldId] = message;
        Changed(nameof(InvalidCount));
        Changed(nameof(HasFieldErrors));
        Changed(nameof(HasInputErrors));
        Changed(nameof(ErrorText));
        Changed(nameof(ErrorHint));
    }

    public bool IsInvalid(Guid fieldId) => _errors.ContainsKey(fieldId);

    public static object? ReadMember(object owner, MemberInfo member) => member switch
    {
        FieldInfo field => field.GetValue(owner),
        PropertyInfo property => property.GetValue(owner),
        _ => throw new NotSupportedException($"Unsupported member: {member.Name}"),
    };

    public void SetMember(object owner, MemberInfo member, object? value)
    {
        if (IsReadOnly || Equals(ReadMember(owner, member), value)) return;
        switch (member)
        {
            case FieldInfo field: field.SetValue(owner, value); break;
            case PropertyInfo property: property.SetValue(owner, value); break;
            default: throw new NotSupportedException($"Unsupported member: {member.Name}");
        }
        MarkEdited(owner);
    }

    public string? SetNumericText(object owner, MemberInfo member, string? text)
    {
        if (IsReadOnly) return null;
        var type = member is FieldInfo field ? field.FieldType : ((PropertyInfo)member).PropertyType;
        object value;
        if (type == typeof(int) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) value = integer;
        else if (type == typeof(float) && float.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var single) && float.IsFinite(single)) value = single;
        else if (type == typeof(double) && double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) value = number;
        else return type == typeof(int) ? "Enter an integer" : "Enter a number";
        SetMember(owner, member, value);
        return null;
    }

    public void MarkEdited(object? owner)
    {
        if (IsReadOnly) return;
        var kind = documents.MarkChanged(owner);
        DocumentEdited?.Invoke(kind);
    }
}
