using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PureEngine.Core;
using PureEngine.Editor;
using UiButton = PureEngine.Core.Button;
using UiText = PureEngine.Core.Text;

static class UiMenuChecks
{
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
        var store = (EditSceneStore)typeof(MainWindow).GetField("_editScene", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
        var components = (ProjectComponents)typeof(MainWindow).GetField("_components", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
        var objects = editor.FindControl<TreeView>("SceneObjects")!;
        try
        {
            var scene = store.Current;
            var menu = editor.FindControl<Grid>("SceneSurface")!.ContextMenu!;
            var ui = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "UI"));
            MenuItem[] subs = [.. ui.Items.OfType<MenuItem>()];
            Check(subs.Length == 3 && Equals(subs[0].Header, "Image") && Equals(subs[1].Header, "Button")
                && Equals(subs[2].Header, "Text"),
                "UI submenu must contain Image, Button, and Text.");
            Click(subs[0]);
            var image = scene.Objects.Single();
            Check(image.Name == "Image" && image.Components.Count == 3 && image.GetComponent<PureEngine.Core.Image>() is not null
                && UiComponentRequirements.GetMissing(image).Count == 0, "Image creation must include its layout requirements.");
            Check(ReferenceEquals((objects.SelectedItem as HierarchyNode)?.Ref, image) && store.IsDirty,
                "Creation must select the new tree node and dirty the scene.");
            Click(subs[1]);
            var button = scene.Objects[1];
            Check(button.Name == "Button" && button.Components.Count == 4 && button.GetComponent<UiButton>() is not null
                && button.GetComponent<PureEngine.Core.Image>() is not null && UiComponentRequirements.GetMissing(button).Count == 0,
                "Button creation must include its visuals and layout requirements.");
            Check(ReferenceEquals(button.Parent, image) && ReferenceEquals((objects.SelectedItem as HierarchyNode)?.Ref, button),
                "UI creation must follow Add Empty's selected-parent rule and select the child.");
            Check(editor.FindControl<StackPanel>("ComponentEditors")!.Children.Count == 4,
                "Inspector must show all four Button components.");
            Click(subs[0]);
            Check(scene.Objects[2].Name == "Image (1)", "Repeated UI names must not collide.");
            var serializer = new SceneSerializer(components.Registry);
            var yaml = serializer.Serialize(scene);
            var restored = serializer.Deserialize(yaml);
            Check(restored.Objects[1].GetComponent<UiButton>()!.Interactable
                && ReferenceEquals(restored.Objects[1].Parent, restored.Objects[0]) && serializer.Serialize(restored) == yaml,
                "UI values and hierarchy must survive save/load.");

            typeof(MainWindow).GetMethod("StartPlay", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null);
            Check(editor.IsPlaying && !ui.IsEnabled, "UI submenu must be disabled during Play.");
            Click(subs[1]);
            Check(scene.Objects.Count == 3, "Even a directly raised click must not create UI during Play.");
            typeof(MainWindow).GetMethod("StopPlay", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null);
            Check(!editor.IsPlaying && ui.IsEnabled, "UI submenu must return after Stop.");
            Click(subs[1]);
            Check(scene.Objects[3].Name == "Button (1)", "UI creation must work again after Stop.");
            Click(subs[2]);
            var textParent = scene.Objects[3];
            var label = scene.Objects[4];
            Check(label.Name == "Text" && label.Components.Count == 3 && label.GetComponent<UiText>() is { Content: "New Text" }
                && UiComponentRequirements.GetMissing(label).Count == 0, "Text creation must include its layout requirements and readable defaults.");
            Check(ReferenceEquals(label.Parent, textParent) && ReferenceEquals((objects.SelectedItem as HierarchyNode)?.Ref, label),
                "Text creation must follow Add Empty's selected-parent rule and select the child.");
            Check(editor.FindControl<StackPanel>("ComponentEditors")!.Children.Count == 3,
                "Inspector must show all three Text components.");
            Click(subs[2]);
            Check(scene.Objects[5].Name == "Text (1)", "Repeated Text names must not collide.");
            var textSerializer = new SceneSerializer(components.Registry);
            var textYaml = textSerializer.Serialize(scene);
            var textRestored = textSerializer.Deserialize(textYaml);
            Check(textRestored.Objects[4].GetComponent<UiText>()!.Content == "New Text"
                && ReferenceEquals(textRestored.Objects[4].Parent, textRestored.Objects[3])
                && textSerializer.Serialize(textRestored) == textYaml,
                "Text values and hierarchy must survive save/load.");

            store.MarkClean();
            var selected = objects.SelectedItem;
            components.Registry.Unregister(typeof(UiButton));
            Click(subs[1]);
            Check(scene.Objects.Count == 6 && !store.IsDirty && ReferenceEquals(objects.SelectedItem, selected),
                "Attachment failure must roll back the new object without changing selection or dirty state.");
            Check(editor.FindControl<TextBlock>("FileStatus")!.Text!.Contains("Could not attach Button"),
                "Attachment failure must be reported.");
        }
        finally
        {
            store.MarkClean();
            editor.Close();
            Dispatcher.UIThread.RunJobs();
        }
        Console.WriteLine("PASS: Stuffs UI creation, layout requirements, parent selection, persistence, Play guard, and failure rollback.");
    }
}
