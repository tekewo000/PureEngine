using PureEngine.Core;
using PureEngine.Runtime;

namespace PureEngine.Editor;

/// <summary>
/// 編集Scene・パス・Dirty・編集用サービスの所有者。MainWindowのpartial間に分散していた編集状態の
/// 変更経路をここに集約し、画面更新（ItemsSource・Inspector・タイトル）はMainWindowが行う。
/// Avaloniaに依存しない。旧Sceneの破棄は呼び出し側へ返して行い、
/// Componentからサービスへの終了順はCoordinator（採用）またはMainWindow（終了）が保証する。
/// </summary>
public sealed class EditSceneStore(Scene initial, string? path = null, bool dirty = false)
{
    public GameSession Services { get; private set; } = GameSession.Create(GameServices.Configure);

    public GameSession ReplaceServices(GameSession services)
    {
        var previous = Services;
        Services = services;
        return previous;
    }

    /// <summary>編集中のScene。実行Sessionが持つ複製とは別の実体。</summary>
    public Scene Current { get; private set; } = initial ?? throw new ArgumentNullException(nameof(initial));

    /// <summary>編集中Sceneの保存先。未保存の新規シーンはnull。</summary>
    public string? Path { get; private set; } = path;

    /// <summary>未保存の変更があるかどうか。</summary>
    public bool IsDirty { get; private set; } = dirty;

    public void MarkChanged() => IsDirty = true;

    public void MarkSaved(string? path)
    {
        Path = path;
        IsDirty = false;
    }

    public void MarkClean() => IsDirty = false;

    /// <summary>Scene本体を変えずに保存先パスだけ付け替える。Explorerの改名・移動時の参照付け替え用。</summary>
    public void SetPath(string? path) => Path = path;

    internal void SetDirtyForTest(bool dirty) => IsDirty = dirty;

    /// <summary>
    /// 新しい編集Sceneを採用し、旧Sceneを返す。旧SceneのComponent破棄は呼び出し側が行う。
    /// Dirtyは呼び出し側が指定する。再読み込みでは旧Dirtyを維持し、保存項目の変更時もtrueにする。
    /// </summary>
    public Scene Replace(Scene next, string? path, bool dirty)
    {
        ArgumentNullException.ThrowIfNull(next);
        var previous = Current;
        Current = next;
        Path = path;
        IsDirty = dirty;
        return previous;
    }

    /// <summary>終了時など、編集Sceneを空にして旧Sceneを返す。破棄は呼び出し側が行う。</summary>
    public Scene Reset()
    {
        var previous = Current;
        Current = new Scene();
        Path = null;
        IsDirty = false;
        return previous;
    }
}
