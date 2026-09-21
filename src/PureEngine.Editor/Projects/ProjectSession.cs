using PureEngine.Core;

namespace PureEngine.Editor;

/// <summary>A project and its validated startup scene, ready to hand to an editor.</summary>
public sealed record ProjectSession(ProjectFile Project, Scene Scene)
{
    /// <summary>
    /// Projectを開き、起動シーンとその編集用サービス群を返す。
    /// 新コードの登録・サービス生成・Scene移行がすべて成功してから採用し、
    /// 失敗時は直前の正常なコード・登録・Sceneを維持する。
    /// 返した EditServices は呼び出し側が所有し、Scene の編集用 Component を解放してから終了する。
    /// </summary>
    public static (ProjectSession Session, GameSession EditServices) Open(string manifestPath)
    {
        var project = ProjectFile.Open(manifestPath);
        ProjectCodeWorkspace.Ensure(project);
        var compiled = UserCodeCompiler.CompileProject(project.RootDirectory);
        var adopted = false;
        Scene? scene = null;
        GameSession? candidateServices = null;
        Exception? preparationError = null;
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
            // 候補コードの登録から編集用サービス群を作る。曖昧・不正・登録中の失敗はここで報告する。
            // コンパイル自体に失敗した場合はプロジェクト登録なし（組み込みのみ）で開けるか試す。
            try
            {
                candidateServices = GameSession.Create(compiled.Success ? compiled : null);
            }
            catch (Exception error)
            {
                throw new InvalidDataException(
                    $"C#のサービス登録に失敗したため起動シーンを開けません: {error.GetBaseException().Message}", error);
            }
            try
            {
                scene = new SceneSerializer(registry).Deserialize(
                    File.ReadAllText(project.StartupScenePath), candidateServices.Factory);
            }
            catch (Exception error) when (!compiled.Success)
            {
                throw new InvalidDataException("C#のコンパイルに失敗したため起動シーンを開けません。\n"
                    + string.Join(Environment.NewLine, compiled.Diagnostics.Where(d => d.IsError)
                        .Select(UserCodeCompiler.FormatDiagnostic)), error);
            }
            ComponentAssets.SetUserCode(compiled.Success ? compiled : null);
            adopted = true;
            var session = new ProjectSession(project, scene);
            var services = candidateServices;
            candidateServices = null;
            scene = null;
            return (session, services);
        }
        catch (Exception error)
        {
            preparationError = error;
            throw;
        }
        finally
        {
            if (!adopted)
            {
                var cleanupErrors = new List<Exception>();
                try
                {
                    if (scene is not null) ComponentAssets.DisposeComponents(scene.Objects.SelectMany(item => item.Components));
                }
                catch (Exception error) { cleanupErrors.Add(error); }
                try
                {
                    candidateServices?.Dispose();
                }
                catch (Exception error) { cleanupErrors.Add(error); }
                finally { try { compiled.LoadContext?.Unload(); } catch { } }
                if (cleanupErrors.Count != 0 && preparationError is not null)
                    throw new AggregateException("Project open and cleanup failed.", [preparationError, .. cleanupErrors]);
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
