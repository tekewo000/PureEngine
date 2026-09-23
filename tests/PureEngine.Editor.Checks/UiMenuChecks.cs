using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Editor;
using Ui = PureEngine.Core.Components;

/// <summary>
/// Stuffsの右クリックメニュー「UI」からImage／Buttonを作れることのEditor側の確認。
/// 画面は表示せず、HeadlessのMainWindowでメニュー構成・作成・選択・保存・Play中の保護を確かめる。
/// </summary>
static class UiMenuChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Call(MainWindow window, string method, params object?[] args) =>
        typeof(MainWindow).GetMethod(method, Private)!.Invoke(window, args);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Click(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    public static void Run()
    {
        var editor = new MainWindow();
        editor.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var surface = editor.FindControl<Grid>("SceneSurface")!;
            var objects = editor.FindControl<ListBox>("SceneObjects")!;
            var menu = surface.ContextMenu!;
            var tops = menu.Items.OfType<MenuItem>().ToArray();
            Check(tops.Length == 3, "Stuffs menu must be Add Empty, UI, and Delete.");
            Check(Equals(tops[0].Header, "Add Empty"), "First Stuffs menu item must stay Add Empty.");
            var ui = tops.OfType<MenuItem>().Single(item => Equals(item.Header, "UI"));
            Check(Equals(ui.Name, "AddUiMenuItem"), "UI submenu must be named AddUiMenuItem.");
            var subs = ui.Items!.OfType<MenuItem>().ToArray();
            Check(subs.Length == 2 && Equals(subs[0].Header, "Image") && Equals(subs[1].Header, "Button"),
                "UI submenu must contain Image and Button.");
            Check(Equals(subs[0].Name, "AddUiImageMenuItem") && Equals(subs[1].Name, "AddUiButtonMenuItem"),
                "UI submenu items must be named.");
            Check(Equals(tops[2].Header, "Delete"), "Last Stuffs menu item must stay Delete.");
            Check(menu.Items.OfType<Separator>().Any(), "Stuffs menu must keep the separator.");

            Click(subs[0]);
            Check(objects.Items.Count == 1, "UI/Image must create one object.");
            var first = (SceneObject)objects.Items[0]!;
            Check(first.Name == "Image" && first.GetComponent<Ui.Image>() is not null,
                "UI/Image must attach the Image component.");
            Check(ReferenceEquals(objects.SelectedItem, first), "UI creation must select the new object.");
            Check(editor.Title!.StartsWith("* "), "UI creation must mark the scene dirty.");

            Click(subs[0]);
            var second = (SceneObject)objects.Items[1]!;
            Check(second.Name == "Image (1)", "Repeated UI/Image names must not collide.");

            Click(subs[1]);
            var buttonItem = (SceneObject)objects.Items[2]!;
            Check(buttonItem.Name == "Button", "UI/Button must use the Button name.");
            Check(buttonItem.GetComponent<Ui.Image>() is not null && buttonItem.GetComponent<Ui.Button>() is not null,
                "UI/Button must carry Image (visuals) and Button.");
            Dispatcher.UIThread.RunJobs();
            var cards = editor.FindControl<StackPanel>("ComponentEditors")!;
            Check(cards.Children.Count == 2, "Inspector must show both Button components.");

            var store = (EditSceneStore)typeof(MainWindow).GetProperty("EditSceneStore", Private)!.GetValue(editor)!;
            var yaml = new SceneSerializer(ComponentAssets.Registry).Serialize(store.Current);
            Check(yaml.Contains("core.image") && yaml.Contains("core.button"), "UI creation must be serializable.");
            var restored = new SceneSerializer(ComponentAssets.Registry).Deserialize(yaml);
            Check(restored.Objects.Count == 3 && restored.Objects[2].GetComponent<Ui.Button>()!.Interactable,
                "UI creation must survive YAML round-trip.");

            Call(editor, "StartPlay");
            Check(editor.IsPlaying, "UI Play-guard check requires Play.");
            Check(!ui.IsEnabled, "UI submenu must be disabled during Play.");
            var count = objects.Items.Count;
            Click(subs[1]);
            Check(objects.Items.Count == count, "UI creation must be blocked during Play.");
            Call(editor, "StopPlay");
            Check(!editor.IsPlaying && ui.IsEnabled, "UI submenu must return after Stop.");
            Click(subs[1]);
            Check(((SceneObject)objects.Items[3]!).Name == "Button (1)", "UI/Button names must not collide.");
        }
        finally
        {
            editor.Close();
            Dispatcher.UIThread.RunJobs();
            foreach (var dialog in editor.OwnedWindows.Where(window => window.Title == "Unsaved Scene").ToArray())
            {
                dialog.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
                    .Single(button => Equals(button.Content, "Discard"))
                    .RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
            }
        }
        Console.WriteLine("PASS: Stuffs UI submenu creates Image and Button objects.");
    }
}
