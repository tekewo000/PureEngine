using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>A project and its validated startup scene, ready to hand to an editor.</summary>
public sealed record ProjectSession(ProjectFile Project, Scene Scene)
{
    public static ProjectSession Open(string manifestPath, Func<Type, object>? factory = null)
    {
        var project = ProjectFile.Open(manifestPath);
        ProjectCodeWorkspace.Ensure(project);
        var compiled = UserCodeCompiler.CompileProject(project.RootDirectory);
        var adopted = false;
        Scene? scene = null;
        try
        {
            foreach (var diagnostic in compiled.Diagnostics)
            {
                var message = UserCodeCompiler.FormatDiagnostic(diagnostic);
                if (diagnostic.IsError) Log.Engine.Error(message);
                else Log.Engine.Warning(message);
            }
            // Validate against a candidate registry; a failed project open must not change the active one.
            var registry = ComponentAssets.CreateRegistry(compiled);
            _ = project.ListDirectories();
            _ = project.ListFiles("Scenes");
            try
            {
                scene = new SceneSerializer(registry).Deserialize(File.ReadAllText(project.StartupScenePath), factory);
            }
            catch (Exception error) when (!compiled.Success)
            {
                throw new InvalidDataException("C#のコンパイルに失敗したため起動シーンを開けません。\n"
                    + string.Join(Environment.NewLine, compiled.Diagnostics.Where(d => d.IsError)
                        .Select(UserCodeCompiler.FormatDiagnostic)), error);
            }
            ComponentAssets.SetUserCode(compiled.Success ? compiled : null);
            adopted = true;
            return new(project, scene);
        }
        finally
        {
            if (!adopted)
            {
                try
                {
                    if (scene is not null) ComponentAssets.DisposeComponents(scene.Objects.SelectMany(item => item.Components));
                }
                finally { compiled.LoadContext?.Unload(); }
            }
        }
    }

    public static ProjectSession Create(string parentDirectory, string name)
    {
        var scene = new Scene();
        var yaml = new SceneSerializer(ComponentAssets.Registry).Serialize(scene);
        var project = ProjectFile.Create(parentDirectory, name, yaml);
        ProjectCodeWorkspace.Ensure(project);
        ComponentAssets.ClearUserCode();
        return new(project, scene);
    }
}
