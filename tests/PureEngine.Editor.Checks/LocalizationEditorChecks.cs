using System.Numerics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PureEngine.Core;
using PureEngine.Editor;
using PureEngine.Rendering;
using Button = Avalonia.Controls.Button;

internal static class LocalizationEditorChecks
{
    public static void Run(string root)
    {
        PreviewLanguageDefaults();
        TableCrudAndSave(root);
        Console.WriteLine("PASS: localization tab grid, save/reopen, dirty Play guard, key selection, and localized rendering.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static T Control<T>(MainWindow window, string name) where T : Control => window.FindControl<T>(name)!;

    private static void Click(Button button)
    {
        if (button.Command is { } command) command.Execute(button.CommandParameter);
        else button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void PreviewLanguageDefaults()
    {
        var editor = new MainWindow();
        editor.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var box = editor.FindControl<ComboBox>("PreviewLanguageBox");
            Check(box is not null, "The toolbar must offer a preview language selector.");
            Check(editor.ViewModel.Localization.PreviewLanguage == "ja"
                && editor.ViewModel.Localization.AvailableLanguages.SequenceEqual(["ja"]),
                "Preview language must start at the default with no project.");
            Check(!box!.IsEnabled, "Preview language must be disabled with no project.");
            Check(editor.ViewModel.Play.PlayLocalization is null, "No Play run must own a localization service.");
            Check(editor.FindControl<TabItem>("LocalizationTab") is not null, "The viewport must offer a Localization tab.");
        }
        finally
        {
            editor.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void TableCrudAndSave(string root)
    {
        using var session = ProjectSession.Create(root, "LocTable");
        var project = session.Project;
        var editor = new MainWindow(session);
        editor.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Check(!editor.ViewModel.Localization.Document.IsDirty, "A fresh project must open a clean table.");
            Control<TabControl>(editor, "ViewportTabs").SelectedIndex = 3;
            Dispatcher.UIThread.RunJobs();
            Click(Control<Button>(editor, "AddLocalizationKeyButton"));
            var keyBox = editor.GetVisualDescendants().OfType<TextBox>()
                .Single(box => (box.GetValue(AutomationProperties.NameProperty) as string)?.EndsWith(".Key") == true);
            keyBox.Text = "menu.start";
            Dispatcher.UIThread.RunJobs();
            var jaBox = editor.GetVisualDescendants().OfType<TextBox>()
                .Single(box => (box.GetValue(AutomationProperties.NameProperty) as string)?.EndsWith(".Text.ja") == true);
            jaBox.Text = "スタート";
            var voiceBox = editor.GetVisualDescendants().OfType<TextBox>()
                .Single(box => (box.GetValue(AutomationProperties.NameProperty) as string)?.EndsWith(".Voice.ja") == true);
            voiceBox.Text = "voice/start_ja";
            Dispatcher.UIThread.RunJobs();
            Check(editor.ViewModel.Localization.Document.IsDirty, "Table edits must mark the document dirty.");
            Check(Control<TabItem>(editor, "LocalizationTab").Header!.ToString()!.EndsWith('*'),
                "A table edit must mark the tab dirty.");
            Control<TextBox>(editor, "AddLocalizationLanguageBox").Text = "ko";
            Click(Control<Button>(editor, "AddLocalizationLanguageButton"));
            var koBox = editor.GetVisualDescendants().OfType<TextBox>()
                .Single(box => (box.GetValue(AutomationProperties.NameProperty) as string)?.EndsWith(".Text.ko") == true);
            koBox.Text = "시작";
            Dispatcher.UIThread.RunJobs();
            Check(editor.ViewModel.Localization.CanSave,
                $"Save must be available (dirty={editor.ViewModel.Localization.Document.IsDirty} status={editor.ViewModel.Status}).");
            Click(Control<Button>(editor, "SaveLocalizationButton"));
            Check(!editor.ViewModel.Localization.Document.IsDirty,
                $"Save must clear dirty immediately (title={editor.ViewModel.Localization.Title} status={editor.ViewModel.Status}).");
            var path = project.LocalizationPath;
            Program.Until(() => File.Exists(path), "Save must write the table file at the project root.");
            var yaml = File.ReadAllText(path);
            Check(yaml.Contains("menu.start") && yaml.Contains("スタート") && yaml.Contains("시작")
                && yaml.Contains("voice/start_ja"), "Save must persist keys, texts, and voice slots.");
            Check(!editor.ViewModel.Localization.Document.IsDirty,
                $"Save must clear the dirty state (title={editor.ViewModel.Localization.Title} status={editor.ViewModel.Status}).");
            Check(editor.ViewModel.Localization.AvailableLanguages.SequenceEqual(["ja", "ko"]),
                "Save must publish the language columns to the preview.");
            var entryId = editor.ViewModel.Localization.Document.Table.Entries.Single().Id;
            var candidates = (System.Collections.IList)typeof(MainWindow).GetMethod("ReferenceCandidates",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [typeof(LocalizedTextId)])!;
            Check(candidates.Count == 1, "The Inspector selector must list the table keys.");
            Click(Control<Button>(editor, "AddLocalizationKeyButton"));
            editor.ViewModel.Play.Start();
            Dispatcher.UIThread.RunJobs();
            Check(!editor.ViewModel.Play.IsPlaying, "Dirty tables must block Play.");
            Click(Control<Button>(editor, "SaveLocalizationButton"));
            editor.ViewModel.Play.Start();
            Dispatcher.UIThread.RunJobs();
            Check(editor.ViewModel.Play.IsPlaying, "Saved tables must allow Play.");
            Check(editor.ViewModel.Play.PlayLocalization is not null
                && editor.ViewModel.Play.PlayLocalization.CurrentLanguage == editor.ViewModel.Localization.PreviewLanguage,
                "Play runs must start from the preview language.");
            editor.ViewModel.Play.Stop();
            Dispatcher.UIThread.RunJobs();
            Check(!editor.ViewModel.Play.IsPlaying && editor.ViewModel.Play.PlayLocalization is null,
                "Stop must release the run localization.");
            var scene = editor.ViewModel.Documents.Scene.Current;
            session.Components.Registry.Register<LocalizedOwner>("checks.loc-owner");
            var item = scene.AddEmpty();
            item.Attach(new LocalizedOwner { Entry = new LocalizedTextId(entryId) });
            var sceneYaml = editor.ViewModel.PrepareSceneSave();
            Check(sceneYaml is not null && sceneYaml.Contains(entryId.ToString("D")) && sceneYaml.Contains("loc:"),
                "Scene save must store the entry ID.");
            scene.Remove(item);
        }
        finally
        {
            if (editor.ViewModel.Play.IsPlaying) editor.ViewModel.Play.Stop();
            editor.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    public sealed class LocalizedOwner
    {
        [Inspector] public LocalizedTextId? Entry { get; set; }
    }
}
