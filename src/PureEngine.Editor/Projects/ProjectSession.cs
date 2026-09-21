using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>
/// プロジェクトと、そのプロジェクト専用の型所有者（ProjectComponents）、検証済み起動シーンを束ねる。
/// 型登録と読込コードはComponentsが所有し、別プロジェクトの所有者に触れない。
/// 開く処理・再読み込みは候補Registryで検証してから採用し、失敗時は既存の登録とSceneを保持する。
/// MainWindowへ渡した後は所有権（ComponentsとScene）をMainWindowへ移す。移したSessionを破棄しない。
/// 所有権と終了順序：編集SceneのComponent破棄 → 編集サービス破棄 → コード解放要求（Components.Dispose）。
/// typeId・types.json・保存済みシーンの互換は維持する。
/// </summary>
public sealed class ProjectSession : IDisposable
{
    public ProjectFile Project { get; }
    public ProjectComponents Components { get; }
    public Scene Scene { get; }

    private bool _disposed;
    private bool _ownershipTransferred;

    private ProjectSession(ProjectFile project, ProjectComponents components, Scene scene)
    {
        Project = project;
        Components = components;
        Scene = scene;
    }

    /// <summary>MainWindowへ所有権を移す。移した後はSessionを破棄せず、MainWindowが破棄する。</summary>
    internal void TransferOwnership()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ownershipTransferred = true;
    }

    public static ProjectSession Open(string manifestPath, Func<Type, object>? factory = null)
    {
        var project = ProjectFile.Open(manifestPath);
        ProjectCodeWorkspace.Ensure(project);
        var components = new ProjectComponents();
        UserCodeCompileResult compiled;
        try
        {
            compiled = UserCodeCompiler.CompileProject(project.RootDirectory);
        }
        catch (Exception)
        {
            components.Dispose();
            throw;
        }
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
            // 候補Registryで検証する。失敗した開処理が他のプロジェクトの登録を変えない。
            var registry = components.CreateCandidateRegistry(compiled);
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
            components.Adopt(compiled.Success ? compiled : null);
            adopted = true;
            return new(project, components, scene);
        }
        finally
        {
            if (!adopted)
            {
                try
                {
                    if (scene is not null) ComponentAssets.DisposeComponents(scene.Objects.SelectMany(item => item.Components));
                }
                finally
                {
                    try { compiled.LoadContext?.Unload(); } catch { }
                    components.Dispose();
                }
            }
        }
    }

    public static ProjectSession Create(string parentDirectory, string name)
    {
        var components = new ProjectComponents();
        try
        {
            var scene = new Scene();
            var yaml = new SceneSerializer(components.Registry).Serialize(scene);
            var project = ProjectFile.Create(parentDirectory, name, yaml);
            ProjectCodeWorkspace.Ensure(project);
            return new(project, components, scene);
        }
        catch
        {
            components.Dispose();
            throw;
        }
    }

    /// <summary>
    /// MainWindowへ渡さなかったSessionの後片付け。編集SceneのComponentを破棄してからコード解放を要求する。
    /// 所有権を移したSessionでは何もしない。
    /// </summary>
    public void Dispose()
    {
        if (_disposed || _ownershipTransferred) return;
        _disposed = true;
        try { ComponentAssets.DisposeComponents(Scene.Objects.SelectMany(item => item.Components)); }
        finally { Components.Dispose(); }
    }
}
