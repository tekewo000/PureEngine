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
    /// <summary>自作クラスのメンバー単体を入れ子カードで編集する。nullはCreate／Set Nullで切り替える。</summary>
    private Control BuildObjectEditor(object owner, MemberInfo member, string automationName)
    {
        var objectType = GetMemberType(member);
        return BuildObjectBox(
            () => GetMemberValue(owner, member),
            value => SetMemberValue(owner, member, value),
            objectType, automationName);
    }

    /// <summary>配列・List要素の自作クラスを入れ子カードで編集する。</summary>
    private Control BuildSequenceObjectBox(object component, MemberInfo member, Type elementType, int index, string automationName) => BuildObjectBox(
            () => SequenceElement(component, member, index),
            value => SetSequenceElement(component, member, index, value),
            elementType, automationName);

    /// <summary>Dictionary値の自作クラスを入れ子カードで編集する。キーが消えたらNull表示になる。</summary>
    private Control BuildDictionaryObjectBox(object component, MemberInfo member, Type valueType, string key, string automationName) => BuildObjectBox(
            () => DictionaryObjectValue(component, member, key),
            value =>
            {
                if (IsPlaying) return;
                if (GetMemberValue(component, member) is not IDictionary dictionary || !dictionary.Contains(key)) return;
                dictionary[key] = value;
                MarkSceneChanged();
            },
            valueType, automationName);

    private static object? DictionaryObjectValue(object component, MemberInfo member, string key)
    {
        if (GetMemberValue(component, member) is IDictionary dictionary && dictionary.Contains(key))
            return dictionary[key];
        return null;
    }

    /// <summary>
    /// 入れ子の自作クラス共通の折りたたみカード。Null時はCreateだけを見せ、
    /// 値がある時は [Inspector] メンバーを行で並べる。Create／Set Nullでは中身を作り直す。
    /// </summary>
    private Control BuildObjectBox(Func<object?> getter, Action<object?> setter, Type objectType, string automationName)
    {
        if (!InspectorValueTypes.IsSupportedType(objectType))
            return UnsupportedBadge(objectType);
        var root = new StackPanel { Spacing = 6 };
        var fields = new TextBlock { Classes = { "memberType" }, VerticalAlignment = VerticalAlignment.Center };
        var setNull = BuildHeaderButton("Set Null", $"{automationName}.Null");
        var nullStatus = new TextBlock { Classes = { "memberType" }, Text = "Null", VerticalAlignment = VerticalAlignment.Center };
        var create = BuildHeaderButton("Create", $"{automationName}.Create");
        var body = new StackPanel { Spacing = 4 };
        var toggle = BuildCollapseToggle($"{automationName}.Collapse", automationName, _collapsedMembers,
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
                _invalidFields.Remove(box);
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
                body.IsVisible = !_collapsedMembers.TryGetValue(automationName, out var collapsed) || !collapsed;
                var members = ComponentSchema.GetInspectorMembers(objectType);
                fields.Text = $"{members.Count} fields";
                foreach (var member in members)
                    body.Children.Add(BuildNestedMemberRow(value, member, $"{automationName}.{member.Name}"));
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
            refresh();
        };
        ToolTip.SetTip(root, $"{objectType.Name} — Create to edit, Set Null to clear");
        refresh();
        return root;
    }
}
