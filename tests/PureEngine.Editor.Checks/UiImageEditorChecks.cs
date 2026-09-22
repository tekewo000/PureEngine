using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Editor;
using SkiaSharp;

internal static class UiImageEditorChecks
{
    public static void Run(MainWindow editor)
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        static T Control<T>(Window window, string name) where T : Control => window.FindControl<T>(name)!;
        static object? Call(MainWindow window, string method, params object?[] args) =>
            typeof(MainWindow).GetMethod(method,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, args);
        static TextBlock Warning(MainWindow window, string component) => window.GetVisualDescendants().OfType<TextBlock>()
            .Single(block => Equals(block.GetValue(AutomationProperties.NameProperty) as string, $"{component}.Requirements"));
        static ComboBox SpriteCombo(MainWindow window) => window.GetVisualDescendants().OfType<ComboBox>()
            .Single(box => Equals(box.GetValue(AutomationProperties.NameProperty) as string, "Image.Sprite"));

        var sceneObjects = Control<ListBox>(editor, "SceneObjects");
        var editStore = (EditSceneStore)typeof(MainWindow).GetField("_editScene",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(editor)!;
        var scene = editStore.Current;
        var components = (ProjectComponents)typeof(MainWindow).GetField("_components",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(editor)!;

        var addButton = Control<Button>(editor, "AddComponentButton");
        Check(addButton.IsVisible && addButton.IsEnabled, "Add Component entry must be visible and enabled.");

        var item = scene.AddEmpty();
        item.Rename("Ui card");
        sceneObjects.SelectedItem = item;
        Dispatcher.UIThread.RunJobs();
        Check(addButton.IsEffectivelyVisible, "An empty object must expose Add Component through all parent panels.");

        void AddThroughDialog(string typeId)
        {
            addButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
            var dialog = desktop.Windows.Single(window => window.Title == "Add Component");
            var controls = dialog.GetVisualDescendants().OfType<Control>().ToList();
            var search = controls.OfType<TextBox>().Single();
            var list = controls.OfType<ListBox>().Single();
            var add = controls.OfType<Button>().Single(button => Equals(button.Content, "Add"));
            var close = controls.OfType<Button>().Single(button => Equals(button.Content, "Close"));
            list.SelectedIndex = list.Items.Count - 1;
            Dispatcher.UIThread.RunJobs();
            Check(list.SelectedIndex == list.Items.Count - 1, "Selecting a row must not rebuild/reset the list.");
            search.Text = typeId;
            Dispatcher.UIThread.RunJobs();
            Check(list.Items.Count == 1 && add.IsEnabled, "Search must select the unattached matching type.");
            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Check(item.Components.Any(component => component.GetType() == components.Registry.GetType(typeId)), "Dialog must attach the selected component.");
            Check(!add.IsEnabled, "Already-attached selection must disable Add.");
            search.Text = "no-such-component-xyz";
            Dispatcher.UIThread.RunJobs();
            Check(list.Items.Count == 0 && !add.IsEnabled, "Empty search results must disable Add.");
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }

        AddThroughDialog("core.image");
        var imageCard = editor.GetVisualDescendants().OfType<Border>().Single(card => ReferenceEquals(card.Tag, item.GetComponent<global::Image>()));
        imageCard.ContextMenu!.Items.OfType<MenuItem>().Single().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Check(item.Components.Count == 0 && addButton.IsEffectivelyVisible, "Removing the last component must keep Add Component visible.");
        AddThroughDialog("core.image");
        sceneObjects.SelectedItem = null;
        Dispatcher.UIThread.RunJobs();
        sceneObjects.SelectedItem = item;
        Dispatcher.UIThread.RunJobs();
        var requirements = Warning(editor, "Image");
        Check(requirements.IsVisible && requirements.Text!.Contains("Transform"),
            $"Image without Transform/UiElement must warn, got '{requirements.Text}'.");
        Check(editor.Title!.StartsWith("* "), "Component add must mark the scene dirty.");

        AddThroughDialog("core.transform");
        sceneObjects.SelectedItem = null;
        Dispatcher.UIThread.RunJobs();
        sceneObjects.SelectedItem = item;
        Dispatcher.UIThread.RunJobs();
        requirements = Warning(editor, "Image");
        Check(requirements.IsVisible && requirements.Text!.Contains("UiElement"),
            "Image with only Transform must still warn about UiElement.");

        AddThroughDialog("core.ui-element");
        sceneObjects.SelectedItem = null;
        Dispatcher.UIThread.RunJobs();
        sceneObjects.SelectedItem = item;
        Dispatcher.UIThread.RunJobs();
        requirements = Warning(editor, "Image");
        Check(!requirements.IsVisible, "Complete Image combination must clear the diagnostic.");

        var element = item.GetComponent<UiElement>()!;
        Check(item.Detach(element), "UiElement detach failed.");
        sceneObjects.SelectedItem = null;
        Dispatcher.UIThread.RunJobs();
        sceneObjects.SelectedItem = item;
        Dispatcher.UIThread.RunJobs();
        requirements = Warning(editor, "Image");
        Check(requirements.IsVisible, "Removing UiElement must restore the diagnostic.");
        Check(components.TryAttach(item, typeof(UiElement), editStore.Services.Factory), "UiElement re-add failed.");
        sceneObjects.SelectedItem = null;
        Dispatcher.UIThread.RunJobs();
        sceneObjects.SelectedItem = item;
        Dispatcher.UIThread.RunJobs();
        Check(!Warning(editor, "Image").IsVisible, "Re-adding UiElement must clear the diagnostic again.");

        var combo = SpriteCombo(editor);
        Check(combo.Items.Count >= 1, "Sprite selector must list None at least.");
        var image = item.GetComponent<global::Image>()!;
        Check(image.Sprite is null, "New Image Sprite must start as None.");
        var project = (ProjectFile)typeof(MainWindow).GetField("_project",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(editor)!;
        var tempSource = Path.Combine(Path.GetTempPath(), "UiCheck-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var bitmap = new SKBitmap(8, 8))
            {
                bitmap.Erase(SKColors.White);
                using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(tempSource, data.ToArray());
            }
            var imported = ProjectAssets.ImportImage(project.RootDirectory, tempSource);
            Call(editor, "RefreshProjectAssets");
            sceneObjects.SelectedItem = null;
            Dispatcher.UIThread.RunJobs();
            sceneObjects.SelectedItem = item;
            Dispatcher.UIThread.RunJobs();
            combo = SpriteCombo(editor);
            Check(combo.Items.Cast<object>().Any(option => option.ToString()!.Contains(imported.RelativePath)),
                "Imported image must appear in the Sprite selector.");
            combo.SelectedItem = combo.Items.Cast<object>().First(option => option.ToString()!.Contains(imported.RelativePath));
            Dispatcher.UIThread.RunJobs();
            Check(image.Sprite is not null && image.Sprite.ImageId == imported.Id,
                "Sprite selection must set the formal image ID.");
            Check(!Warning(editor, "Image").IsVisible, "Existing image must not warn about missing assets.");
            combo.SelectedItem = combo.Items.Cast<object>().First(option => option.ToString() == "None");
            Dispatcher.UIThread.RunJobs();
            Check(image.Sprite is null, "Sprite None must clear the reference.");
        }
        finally
        {
            if (File.Exists(tempSource)) File.Delete(tempSource);
        }

        image.Sprite = new Sprite(Guid.NewGuid());
        sceneObjects.SelectedItem = null;
        Dispatcher.UIThread.RunJobs();
        sceneObjects.SelectedItem = item;
        Dispatcher.UIThread.RunJobs();
        requirements = Warning(editor, "Image");
        Check(requirements.IsVisible && requirements.Text!.Contains("Missing"),
            "Unknown image IDs must keep their ID and warn.");
        Check(image.Sprite.ImageId != Guid.Empty, "Missing sprite must keep its ID.");

        foreach (var other in scene.Objects.Where(candidate => !ReferenceEquals(candidate, item)).ToList())
            scene.Remove(other);
        sceneObjects.SelectedItem = item;
        Dispatcher.UIThread.RunJobs();
        Call(editor, "StartPlay");
        Dispatcher.UIThread.RunJobs();
        Check(!Control<StackPanel>(editor, "ObjectInspector").IsEnabled,
            "Inspector editing must be disabled while playing.");
        Call(editor, "StopPlay");
        Dispatcher.UIThread.RunJobs();
        Check(Control<StackPanel>(editor, "ObjectInspector").IsEnabled,
            "Inspector editing must return after Stop.");
        Console.WriteLine("PASS: component add entry, Image requirements warn/clear, Sprite select/None/missing, and Play guard.");
    }
}
