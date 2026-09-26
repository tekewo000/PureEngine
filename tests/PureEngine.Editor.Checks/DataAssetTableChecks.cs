using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Editor;
using Button = Avalonia.Controls.Button;

static class DataAssetTableChecks
{
    public static void Run(string parent)
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        static T Control<T>(Window window, string name) where T : Control => window.FindControl<T>(name)!;
        static TextBox Box(MainWindow window, string automationName) => window.GetVisualDescendants().OfType<TextBox>()
            .Single(box => Equals(box.GetValue(AutomationProperties.NameProperty) as string, automationName));
        static bool HasBox(MainWindow window, string automationName) => window.GetVisualDescendants().OfType<TextBox>()
            .Any(box => Equals(box.GetValue(AutomationProperties.NameProperty) as string, automationName));
        static void Click(Button button)
        {
            if (button.Command is { } command) command.Execute(button.CommandParameter);
            else button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }
        static void Answer(string title, string answer)
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
            Program.Until(() => desktop.Windows.Any(window => window.Title == title));
            var dialog = desktop.Windows.Single(window => window.Title == title);
            Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, answer)));
        }
        static object? Call(MainWindow window, string name, params object?[] args)
        {
            var result = typeof(MainWindow).GetMethod(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!.Invoke(window, args);
            if (result is Task task) Program.Wait(task);
            return result;
        }

        using var created = ProjectSession.Create(parent, "DataAssetTable");
        var root = created.Project.RootDirectory;
        File.WriteAllText(Path.Combine(root, "Sword.cs"), """
            using System.Collections.Generic;
            using PureEngine.Core;
            namespace Game;
            [DataAsset("Items/Weapon")]
            public sealed class SwordData
            {
                [Inspector] public string Name { get; set; } = "Iron";
                [Inspector] public int Attack { get; set; } = 10;
                [Inspector] public List<int> Tags { get; set; } = [1, 2];
                [Inspector] public Stats? Stats { get; set; } = new Stats();
            }
            public sealed class Stats
            {
                [Inspector] public int Hp { get; set; } = 30;
            }
            """);
        created.Dispose();
        using var session = ProjectSession.Open(Path.Combine(root, "Project.pure.project.yaml"));
        var type = session.Components.DataAssetTypes.Single();
        var first = Path.Combine(root, "Sword.pure.asset.yaml");
        var second = Path.Combine(root, "Axe.pure.asset.yaml");
        DataAssetFile.Create(first, type, session.Components.Registry);
        DataAssetFile.Create(second, type, session.Components.Registry);
        var editor = new MainWindow(session);
        editor.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var types = Control<ComboBox>(editor, "DataAssetTableTypes");
            Check(types.Items.OfType<DataAssetDescriptor>().Any(descriptor => descriptor.Type == type),
                "The table type picker must list the [DataAsset] type.");
            types.SelectedItem = types.Items.OfType<DataAssetDescriptor>().Single(descriptor => descriptor.Type == type);
            Control<TabControl>(editor, "ViewportTabs").SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            Program.Until(() => HasBox(editor, "Table.SwordData.Sword.Attack"),
                "Selecting a type must list its assets as rows.");
            Check(HasBox(editor, "Table.SwordData.Axe.Attack"), "Every asset file of the type must become a row.");
            Check(Control<TextBlock>(editor, "DataAssetTableStatus").Text!.Contains("2 row(s)"),
                "The table status must count rows.");

            // Scalar edit marks the table dirty, never the scene.
            var attack = Box(editor, "Table.SwordData.Sword.Attack");
            Check(attack.Text == "10", $"Initial Attack must be 10, got '{attack.Text}'.");
            attack.Text = "25";
            Dispatcher.UIThread.RunJobs();
            Check(Control<TabItem>(editor, "DataAssetTableTab").Header!.ToString()!.EndsWith('*'),
                "A table edit must mark the tab dirty.");
            Check(!editor.Title!.StartsWith("* "), "A table edit must not mark the scene dirty.");
            Check(Control<Button>(editor, "SaveDataAssetTableButton").IsEnabled, "A dirty table must enable Save All.");
            Click(Control<Button>(editor, "SaveDataAssetTableButton"));
            Program.Until(() => !Control<TabItem>(editor, "DataAssetTableTab").Header!.ToString()!.EndsWith('*'));
            Check(File.ReadAllText(first).Contains("Attack: 25"), "Save All must write the edited row.");

            // Invalid input shows the table badge and blocks the bulk save.
            attack = Box(editor, "Table.SwordData.Sword.Attack");
            attack.Text = "abc";
            Dispatcher.UIThread.RunJobs();
            Check(Control<TextBlock>(editor, "DataAssetTableError").IsVisible, "Invalid table input must show an error badge.");
            Click(Control<Button>(editor, "SaveDataAssetTableButton"));
            Check(!File.ReadAllText(first).Contains("abc"), "Invalid input must not reach the file.");
            attack.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Dispatcher.UIThread.RunJobs();
            Check(attack.Text == "25", $"Esc must restore the last value, got '{attack.Text}'.");
            Check(!Control<TextBlock>(editor, "DataAssetTableError").IsVisible, "Esc must clear the table error.");

            // Bulk save writes every dirty row at once.
            Box(editor, "Table.SwordData.Sword.Attack").Text = "30";
            Box(editor, "Table.SwordData.Axe.Attack").Text = "40";
            Dispatcher.UIThread.RunJobs();
            Click(Control<Button>(editor, "SaveDataAssetTableButton"));
            Program.Until(() => !Control<TabItem>(editor, "DataAssetTableTab").Header!.ToString()!.EndsWith('*'));
            Check(File.ReadAllText(first).Contains("Attack: 30") && File.ReadAllText(second).Contains("Attack: 40"),
                "Save All must write every dirty row.");

            // Nested boxes and collection boxes edit in place.
            var hp = Box(editor, "Table.SwordData.Sword.Stats.Hp");
            Check(hp.Text == "30", $"Nested Hp must be 30, got '{hp.Text}'.");
            hp.Text = "99";
            var add = editor.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.GetValue(AutomationProperties.NameProperty), "Table.SwordData.Sword.Tags.Add"));
            Click(add);
            Dispatcher.UIThread.RunJobs();
            Click(Control<Button>(editor, "SaveDataAssetTableButton"));
            Program.Until(() => !Control<TabItem>(editor, "DataAssetTableTab").Header!.ToString()!.EndsWith('*'));
            var yaml = File.ReadAllText(first);
            Check(yaml.Contains("Hp: 99"), "Nested edits must save through the table.");
            Check(yaml.Split('\n').Count(line => line.TrimStart().StartsWith("- ")) >= 3,
                "Collection Add inside a cell must save the new element.");

            // Add Row creates a file and a row; Delete removes both after confirmation.
            Click(Control<Button>(editor, "AddDataAssetTableRowButton"));
            Program.Until(() => Control<TextBlock>(editor, "DataAssetTableStatus").Text!.Contains("3 row(s)"));
            var added = Directory.EnumerateFiles(root, "*.pure.asset.yaml", SearchOption.AllDirectories).ToArray();
            Check(added.Length == 3, "Add Row must create an asset file.");
            var addedBase = Path.GetFileNameWithoutExtension(added.Single(path => path != first && path != second));
            var addedShort = addedBase.EndsWith(".pure.asset", StringComparison.Ordinal)
                ? addedBase[..^".pure.asset".Length] : addedBase;
            var delete = editor.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.GetValue(AutomationProperties.NameProperty), $"Table.SwordData.{addedShort}.Delete"));
            Click(delete);
            Answer("Delete", "Delete");
            Program.Until(() => Control<TextBlock>(editor, "DataAssetTableStatus").Text!.Contains("2 row(s)"));
            Check(Directory.EnumerateFiles(root, "*.pure.asset.yaml", SearchOption.AllDirectories).Count() == 2,
                "Delete must remove the row file.");

            // Closing with a dirty table confirms; Cancel preserves the input.
            Box(editor, "Table.SwordData.Sword.Attack").Text = "31";
            Dispatcher.UIThread.RunJobs();
            editor.Close();
            Answer("Unsaved Data Assets", "Cancel");
            Check(editor.IsVisible && Box(editor, "Table.SwordData.Sword.Attack").Text == "31",
                "Closing with a dirty table must confirm and Cancel must preserve the input.");
            Click(Control<Button>(editor, "SaveDataAssetTableButton"));
            Program.Until(() => !Control<TabItem>(editor, "DataAssetTableTab").Header!.ToString()!.EndsWith('*'));

            // Compatible reload rebinds rows and preserves values.
            var sourcePath = Path.Combine(root, "Sword.cs");
            var source = File.ReadAllText(sourcePath);
            File.WriteAllText(sourcePath, source.Replace("[Inspector] public int Attack", "[Inspector] public int Defense { get; set; } = 3;\n                [Inspector] public int Attack"));
            Call(editor, "ReloadUserCode");
            Program.Until(() => HasBox(editor, "Table.SwordData.Sword.Defense"),
                "Reload must add the member column.");
            Check(Box(editor, "Table.SwordData.Sword.Defense").Text == "3"
                && Box(editor, "Table.SwordData.Sword.Attack").Text == "31",
                "Reload must add the member column and preserve row values.");
            File.WriteAllText(sourcePath, source);
            Call(editor, "ReloadUserCode");
            Program.Until(() => !HasBox(editor, "Table.SwordData.Sword.Defense"),
                "Reverting the member must drop the column.");
            Check(Box(editor, "Table.SwordData.Sword.Attack").Text == "31",
                "Reverting the member must drop the column and keep row values.");
            Click(Control<Button>(editor, "SaveDataAssetTableButton"));
            Program.Until(() => !Control<TabItem>(editor, "DataAssetTableTab").Header!.ToString()!.EndsWith('*'));

            // Play locks the table; Stop restores it.
            Call(editor, "StartPlay");
            Check(editor.IsPlaying && !Control<StackPanel>(editor, "DataAssetTableRows").IsEnabled,
                "Play must lock the data asset table.");
            Call(editor, "StopPlay");
            Check(Control<StackPanel>(editor, "DataAssetTableRows").IsEnabled, "Stop must restore the table.");
        }
        finally
        {
            editor.Close();
            Dispatcher.UIThread.RunJobs();
        }
        Console.WriteLine("PASS: data asset table bulk edit/save, invalid guard, nested and collection cells, row add/delete, reload rebind, and Play lock.");
    }
}
