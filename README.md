# PureEngine

C#で作る、UI中心の2Dマルチプレイゲーム向けエディター。
現在はLauncherからProjectを開き、オブジェクト・アタッチしたクラスの値を編集し、YAMLで保存・復元できます。

設計方針・Attribute・Priority・保存データ・将来の構想は [EngineArchitecture.md](docs/EngineArchitecture.md) にまとめています。
実装済みの範囲・制限・次の作業・検証状況は [実装計画・進捗](docs/ImplementationPlan.md) にまとめています。

## 現在できること

- 灰色のダークテーマで、タブの切り替えとペインのサイズ変更ができる。
- 起動時にLauncherを表示し、Projectの新規作成・既存Projectの選択・最近開いたProjectからの再開ができる。
- 下部のProject Explorerは左にフォルダツリー、右にファイル一覧を表示する。Componentsからコンパイル済みの `PlayerStats` と `RoundSettings` を選べる。
- 一覧と保存用の型IDは `ComponentAssets.cs` の `Registry.Register<T>("固定ID")` に明示登録する。自動探索や外部C#ファイルの取り込みは未実装。
- Scene View／Gameは左、Stuffsは中央、Inspectorは右に配置する。
- Project ExplorerのComponentsにあるクラスをStuffsのオブジェクト行、または選択中オブジェクトのInspectorへドラッグ＆ドロップしてアタッチする。追加したクラス名はInspectorのComponentsに表示する。同じ型の重複、Stuffsの余白、未選択のInspectorへのドロップは受け付けない。
- Stuffsの右クリックメニュー「Add Empty」でオブジェクトを追加し、Inspectorの「Name」で名前を編集する。
- オブジェクトを右クリックして「Delete」、またはStuffsで選択してDeleteキーで削除する。余白を右クリックすると選択が解除され、削除は無効になる。
- Inspectorで `[Inspector]` 付きのstring・int・float・boolを編集し、YAMLで保存・読み込みできる。
- .NET 11 RC1とAvaloniaでビルドし、Windows上で表示を確認済み。

## 技術

- .NET SDK 11.0.100-rc.1.26425.128（global.jsonで固定）
- Avalonia 12.1.2 / Fluent ダークテーマ
- YamlDotNet 18.1.0

## シーンの保存・読み込み

- File → Open Scene（Ctrl+O）：シーンを読み込む。
- File → Save Scene（Ctrl+S）：上書き保存。初回は保存先を選ぶ。
- File → Save Scene As（Ctrl+Shift+S）：別名で保存。
- 拡張子は `.pure.scene.yaml`。例として [Examples/Main.pure.scene.yaml](Examples/Main.pure.scene.yaml) を開ける。
- 未保存の変更はタイトルの `*` で示す。別シーンを開くときや終了時にSave／Discard／Cancelを選ぶ。
- Inspectorに入力エラーがある間は保存しない。成否とエラー詳細は画面下部に表示する。

保存対象はオブジェクトのID・名前、登録済みクラスの固定ID、`[Inspector]` 付きの値。
読み込みは別のSceneへ復元し、成功してから現在のSceneと入れ替える。保存は同じフォルダの一時ファイルへ書き終えてから置き換える。
YAMLのコメントは再保存で失われる。Parent、Priority、オブジェクト参照、Editorのペイン配置は現在の保存対象に含めない。

## Project

LauncherのNew Projectで名前と親フォルダを指定し、Create Projectを押すと、新しいフォルダと空のMainシーンを作ってEditorを開く。既存の同名フォルダは上書きしない。

```text
MyGame/
  Project.pure.project.yaml
  Scenes/
    Main.pure.scene.yaml
    Scene.pure.scene.yaml
```

- LauncherのOpen Projectから `Project.pure.project.yaml` を選ぶと、起動シーンを開く。
- Recent Projectsはダブルクリック、または選択してEnterで開く。履歴は最大12件を `%LOCALAPPDATA%/PureEngine/recent-projects.json` に保存し、Projectフォルダには含めない。
- File → New Scene（Ctrl+N）で空のシーンを作り、Ctrl+SでScenesフォルダ内へ保存する。
- 下部Project ExplorerのフォルダツリーからScenesを選び、右のシーンをダブルクリック、または選択してEnterで開く。サブフォルダもツリーから選べる。
- File → Set Current Scene as Startupで現在のシーンを起動シーンに設定する。
- File → Return to Launcher（Ctrl+Shift+O）、またはEditorを閉じるとLauncherに戻る。未保存の変更はSave／Discard／Cancelで確認する。Launcherを閉じるとアプリが終了する。
- Project内のシーンは `.pure.scene.yaml` で保存する。パスはProjectからの相対パスなので、フォルダごと移動できる。
- Project／シーンの切り替えでも未保存確認が働く。壊れたProjectや起動シーンの読み込みに失敗しても、現在のProjectとシーンは置き換えない。

[サンプルProject](Examples/SampleProject/Project.pure.project.yaml)にはMainとMenuの2シーンが入っている。
既存の単体シーンを利用する場合は、新しいProjectのScenesフォルダに `.pure.scene.yaml` ファイルをコピーし、Project ExplorerをRefreshして開く。
現在のProject機能はシーン管理が対象。C#クラスの登録は共通の `ComponentAssets` を使い、ProjectごとのC#コンパイルや素材の取り込みは未実装。

## 起動

PowerShellでリポジトリのフォルダを開き、次を実行します。

```powershell
./tools/run-editor.ps1
```

起動スクリプトは `%LOCALAPPDATA%/PureEngine/dotnet/dotnet.exe` があれば使用し、
なければPATH上のdotnetを使用します。

別のPCでは、指定の.NET 11 SDKをインストールしてください。
公式のdotnet-install.ps1で上記のユーザーフォルダへ導入することもできます。

通常のdotnetコマンドが指定SDKを認識する環境では、以下でも起動できます。

```powershell
dotnet run --project src/PureEngine.Editor
```

## 構成

- `src/PureEngine.Core/`：シーン・オブジェクト・属性の定義。
- `src/PureEngine.Editor/`：Avaloniaによる編集画面。
- `tests/PureEngine.Core.Checks/`：Coreの動作チェック。
- `tests/PureEngine.Editor.Checks/`：画面を表示しないLauncher・Editor遷移の動作チェック。
- [EngineArchitecture.md](docs/EngineArchitecture.md)：設計仕様と未決定事項。

Coreのクラスのアタッチ・取得と属性検出、Editorからのアタッチ・値の編集、YAMLシーン保存、Coreのライフサイクル実行は実装済み。Editorからのゲーム実行、Steam連携、ゲーム内UI配置はまだ実装していません。

## Coreのライフサイクル実行

既存のSceneとComponentRegistryから、編集データと独立した実行用Sceneを作れます。EditorのPlay／Stopへの接続はまだありません。

```csharp
using var runtime = new SceneRuntime(scene, registry);
runtime.Start();
if (runtime.IsRunning) runtime.Step(1f / 60f); // 呼び出し元が経過秒を渡す
runtime.Stop();
foreach (var error in runtime.Errors)
    Console.WriteLine($"{error.ObjectName}/{error.ComponentType.Name}.{error.MethodName}: {error.Exception}");
```

実行中の追加・アタッチ・削除には `runtime.Scene.AddEmpty()`、`Attach()`、`runtime.Scene.Remove()` を使います。追加分は次のStepでStartし、削除予約後はStart／Updateを呼ばず、フレーム末にDestroyします。再実行は新しいSceneRuntimeを作ります。詳細な制約と例外時の動作は設計書を参照してください。

## コアの動作確認

追加・名前変更・検証・ID・削除・アタッチ・属性検出に加えて、YAMLの保存と復元、文字列の保持、不正データの拒否、保存失敗時の元ファイル保護を確認します。SDKが使える環境で実行してください。

```powershell
dotnet run --project tests/PureEngine.Core.Checks
```

Launcherからの作成・履歴からの再開・未保存確認・Launcherへの復帰・終了は、実画面を操作せず確認できる。

```powershell
dotnet run --project tests/PureEngine.Editor.Checks
```

Coreのチェックにはライフサイクル・編集データの分離・追加削除・例外時の後片付けも含みます。実行機構単体の性能測定は次で再実行できます。測定条件と結果は実装計画・進捗に記載しています。

```powershell
dotnet run --project tests/PureEngine.Core.Checks -c Release -- --runtime-benchmark
```
