using System.Collections;
using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using PureEngine.Core;

namespace PureEngine.Editor;

public partial class MainWindow
{
    /// <summary>Edits a single custom-class member in a nested card. Toggles null with Create/Set Null.</summary>
    private Control BuildObjectEditor(object owner, MemberInfo member, string automationName)
    {
        var objectType = GetMemberType(member);
        var ownerId = GetOwnerComponentId(owner);
        var baseStorePath = member.Name;
        return BuildObjectBox(
            () => GetMemberValue(owner, member),
            value => SetMemberValue(owner, member, value),
            objectType, automationName, ownerId, baseStorePath);
    }

    /// <summary>Edits custom-class array and List elements in nested cards.</summary>
    private Control BuildSequenceObjectBox(object component, MemberInfo member, Type elementType, int index, string automationName)
    {
        var ownerId = GetOwnerComponentId(component);
        var baseStorePath = $"{member.Name}[{index}]";
        return BuildObjectBox(
            () => SequenceElement(component, member, index),
            value => SetSequenceElement(component, member, index, value),
            elementType, automationName, ownerId, baseStorePath);
    }

    /// <summary>Edits custom-class dictionary values in nested cards. Shows Null when the key is gone.</summary>
    private Control BuildDictionaryObjectBox(object component, MemberInfo member, Type valueType, string key, string automationName)
    {
        var ownerId = GetOwnerComponentId(component);
        var baseStorePath = SceneReferenceStore.DictionaryPath(member.Name, key);
        return BuildObjectBox(
            () => DictionaryObjectValue(component, member, key),
            value =>
            {
                if (IsPlaying) return;
                if (GetMemberValue(component, member) is not IDictionary dictionary || !dictionary.Contains(key)) return;
                dictionary[key] = value;
                MarkEdited(component);
            },
            valueType, automationName, ownerId, baseStorePath);
    }

    private static object? DictionaryObjectValue(object component, MemberInfo member, string key)
    {
        if (GetMemberValue(component, member) is IDictionary dictionary && dictionary.Contains(key))
            return dictionary[key];
        return null;
    }

    /// <summary>
    /// Shared collapsible card for nested custom classes. Shows only Create when null,
    /// and lists [Inspector] members as rows when a value exists. Create/Set Null rebuilds the contents.
    /// </summary>
    private Control BuildObjectBox(Func<object?> getter, Action<object?> setter, Type objectType, string automationName, Guid? ownerId = null, string? baseStorePath = null)
    {
        if (!InspectorValueTypes.IsSupportedType(objectType) && !SceneReferenceTypes.IsSupportedInspectorType(objectType, Components.Registry))
            return UnsupportedBadge(objectType);
        var root = new StackPanel { Spacing = 6 };
        var fields = new TextBlock { Classes = { "memberType" }, VerticalAlignment = VerticalAlignment.Center };
        var setNull = BuildHeaderIconButton("Icon.Dismiss", $"{automationName}.Null");
        ToolTip.SetTip(setNull, "Set to null.");
        var nullStatus = new TextBlock { Classes = { "memberType" }, Text = "Null", VerticalAlignment = VerticalAlignment.Center };
        var create = BuildHeaderIconButton("Icon.Compose", $"{automationName}.Create");
        ToolTip.SetTip(create, "Create a new instance.");
        var body = new StackPanel { Spacing = 4 };
        var toggle = BuildCollapseToggle($"{automationName}.Collapse", automationName, ViewModel.Inspector.CollapsedMembers,
            nowExpanded => body.IsVisible = nowExpanded);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(toggle);
        left.Children.Add(fields);
        var header = BuildSplitHeader(left, setNull);
        var nullHeader = BuildSplitHeader(nullStatus, create);
        root.Children.Add(header);
        root.Children.Add(nullHeader);
        root.Children.Add(body);
        void refresh()
        {
            foreach (var box in body.GetVisualDescendants().OfType<TextBox>())
                ClearInputError(box);
            body.Children.Clear();
            var value = getter();
            if (value is null || value.GetType() != objectType)
            {
                nullHeader.IsVisible = true;
                header.IsVisible = false;
                body.IsVisible = false;
            }
            else
            {
                nullHeader.IsVisible = false;
                header.IsVisible = true;
                body.IsVisible = !ViewModel.Inspector.CollapsedMembers.TryGetValue(automationName, out var collapsed) || !collapsed;
                var members = ComponentSchema.GetInspectorMembers(objectType);
                fields.Text = $"{members.Count} fields";
                foreach (var member in members)
                {
                    var nestedAutomation = $"{automationName}.{member.Name}";
                    var nestedStore = baseStorePath is null ? null : $"{baseStorePath}.{member.Name}";
                    body.Children.Add(BuildNestedMemberRow(value, member, nestedAutomation, ownerId, nestedStore, refresh));
                }
            }
            UpdateErrorBadge();
            QueuePendingUserCodeReload();
        }
        create.Click += (_, _) =>
        {
            if (IsPlaying) return;
            try
            {
                setter(Activator.CreateInstance(objectType)!);
            }
            catch (Exception error)
            {
                SetFileStatus(error.ToString(), true);
                return;
            }
            refresh();
        };
        setNull.Click += (_, _) =>
        {
            if (IsPlaying) return;
            setter(null);
            if (ownerId is { } id && baseStorePath is not null)
                Documents.Current.Current.References.RemovePathsForMember(id, baseStorePath);
            refresh();
        };
        ToolTip.SetTip(root, $"{objectType.Name} — Create to edit, Set Null to clear");
        refresh();
        return root;
    }
}
