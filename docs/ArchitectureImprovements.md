# 機能追加前のアーキテクチャ改善

最終確認：2026-09-22

## 方針と対応状況

5項目をすべて完了してから新機能追加へ進む。**現在は5／5項目完了。** mainのA1と、b1〜b4のA2〜A5を接続し、統合後の動作を確認した。描画・入力・Parent・Undo／Redo・通信・Steamなどの新機能は今回追加していない。

完了は実装だけでなく、以下の完了条件と統合チェックを満たした状態を指す。

| ID | 改善項目 | 状態 | 主な実装・検証 |
| --- | --- | --- | --- |
| A1 | プロジェクト側のゲーム用サービス登録 | 完了 | ProjectGameServices、GameServices、ProjectServiceRegistrationChecks |
| A2 | MainWindowの責務分離 | 完了 | EditSceneStore、EditorOperationGate、UserCodeReloadCoordinator、EditorSeparationChecks |
| A3 | コンパイルのバックグラウンド化 | 完了 | UserCodeCompileTracker、ProjectSession.OpenAsync、MainWindow.UserCode、UserCodeBackgroundChecks |
| A4 | プロジェクト単位の型登録と読込コードの所有 | 完了 | ProjectComponents、ProjectSession、ProjectIsolationChecks |
| A5 | Editorに依存しない実行接続 | 完了 | PureEngine.Runtime、GameDependencyChecks、PlayConnectionChecks |
| 統合 | 上記5項目の接続 | 完了 | IntegratedArchitectureChecks、既存Core／Editorチェック |

## A1：プロジェクト側のサービス登録

自作C#の `public static void ConfigureGameServices(IServiceCollection services)` を1つ検出し、組み込み登録に加えて適用する。登録がない既存プロジェクトは従来どおり動く。登録口の重複・不正な形式・async void・登録中の例外は報告し、採用しない。

- [x] エンジンを変更せず、プロジェクト側で登録したサービスをComponentのコンストラクタで受け取れる。
- [x] 編集・Play・再Playに同じ登録を適用し、Singletonを含むサービス群を分離する。
- [x] 登録・依存解決・移行失敗時は旧状態を維持し、候補資源を解放する。
- [x] 登録変更の再読み込みと、Componentからサービスへの終了順・単発解放を維持する。

実装：[ProjectGameServices](../src/PureEngine.Editor/Game/ProjectGameServices.cs)、[GameServices](../src/PureEngine.Editor/Game/GameServices.cs)。
検証：[ProjectServiceRegistrationChecks](../tests/PureEngine.Editor.Checks/ProjectServiceRegistrationChecks.cs)。生成する外部エディター用csprojにもDIの参照を追加した。

## A2：MainWindowの責務分離

編集Scene・保存先・Dirty・編集用サービス群を `EditSceneStore` が保持する。保存・Play・再読み込みの可否はUI非依存の `EditorOperationGate` で判定する。`UserCodeReloadCoordinator.Apply` は候補サービスの生成、Scene移行、型登録と編集状態の採用、旧資源の解放を担当する。MainWindowは操作受付、非同期処理の接続、ダイアログ、選択・表示更新を担当する。

- [x] 再読み込みの中核処理をWindow／Controlなしで実行・検証できる。
- [x] 編集Scene・サービス・実行Session・コンパイル結果の所有者と変更経路が明確になった。
- [x] 保存・Play・再読み込みの競合を、画面から渡す状態の値で判定する。
- [x] 未保存確認、入力エラー中の保留、Play中の編集禁止、失敗表示、終了時解放を維持する。

実装：[EditSceneStore](../src/PureEngine.Editor/Editing/EditSceneStore.cs)、[EditorOperationGate](../src/PureEngine.Editor/Editing/EditorOperationGate.cs)、[UserCodeReloadCoordinator](../src/PureEngine.Editor/Compilation/UserCodeReloadCoordinator.cs)。
検証：[EditorSeparationChecks](../tests/PureEngine.Editor.Checks/EditorSeparationChecks.cs)、EditorOwnershipChecks、既存画面チェック。

## A3：コンパイルのバックグラウンド実行

Launcherは `ProjectSession.OpenAsync`、Editorの再読み込みは `UserCodeCompileTracker` を使う。ソースの読み取り・コンパイルをワーカースレッドで行い、サービス生成・Scene復元／移行・採用はUIスレッドへ戻って行う。

- [x] プロジェクトを開く際と再読み込みの際に、コンパイルがUIスレッドを占有しない。
- [x] 世代と所有者の有効性を確認し、連続変更・切替・終了で古くなった結果を採用せず解放する。
- [x] 採用時点のSceneから移行し、コンパイル中の編集を保持する。Play・ファイル操作・入力エラー中は採用を保留する。
- [x] 失敗時は旧状態を維持し、修正後に再試行できる。
- [x] 完了順逆転、保留、終了後の完了、起動キャンセルとUI応答を検証する。

保留中に新しい要求が来た場合は古い候補を破棄し、最新の結果を待つ。実行中のRoslynコンパイル自体は強制中断せず、不要になった結果を終了後に解放する。

実装：[UserCodeBackgroundCompile](../src/PureEngine.Editor/Compilation/UserCodeBackgroundCompile.cs)、[MainWindow.UserCode](../src/PureEngine.Editor/Windows/MainWindow.UserCode.cs)、[ProjectSession](../src/PureEngine.Editor/Projects/ProjectSession.cs)。
検証：[UserCodeBackgroundChecks](../tests/PureEngine.Editor.Checks/UserCodeBackgroundChecks.cs)、[IntegratedArchitectureChecks](../tests/PureEngine.Editor.Checks/IntegratedArchitectureChecks.cs)。コンパイルの完了を制御し、その間にAvaloniaのUI処理が実行されること、Component生成がUIスレッドへ戻ることをHeadlessで確認した。

## A4：プロジェクト単位の型登録と読込コードの所有

`ProjectComponents` がRegistry・自作型一覧・ファイル対応・採用中コードを所有する。`ComponentAssets` は状態を持たない共通処理だけを提供する。`ProjectSession` は起動Scene・編集用サービス・ProjectComponentsを所有し、Editorへ引き渡す。

- [x] 可変なstatic登録に依存せず、プロジェクト単位で型とコードを管理する。
- [x] 同名クラスを持つ2つのプロジェクトを同時保持し、一方の再読み込み・終了後も他方の型解決・保存・Playが独立する。
- [x] 開く処理や再読み込みの失敗時に既存状態を維持する。
- [x] 旧Component、旧サービス、旧コードの順に解放し、所有者の旧コード参照を外す。
- [x] typeIdと `.pureengine/types.json` の保存互換を維持する。

実装：[ProjectComponents](../src/PureEngine.Editor/Projects/ProjectComponents.cs)、[ProjectSession](../src/PureEngine.Editor/Projects/ProjectSession.cs)。
検証：[ProjectIsolationChecks](../tests/PureEngine.Editor.Checks/ProjectIsolationChecks.cs)、UserCodeChecks、統合チェック。コードのUnloadは解放要求であり、利用者が保持する型やインスタンスまで強制回収するものではない。

## A5：Editorに依存しない実行接続

`GameSession` と `PlaySession` を `PureEngine.Runtime` へ移した。依存方向はEditor → Runtime → Core。Runtimeは登録処理を `Action<IServiceCollection>` として外部から受け取り、Editor・Avalonia・Editorサンプルを参照しない。

- [x] RuntimeはEditor・Avaloniaに非依存、CoreはUI・DIライブラリに非依存。
- [x] EditorのPlayとUIなしのチェックが同じRuntimeを利用する。
- [x] A1の登録を使った準備・Start・Step・Stop・再実行を確認する。
- [x] 正常・準備失敗・ライフサイクル失敗時の解放順と単発解放を維持する。
- [x] GameSession／PlaySessionのソースリンクを廃止し、チェック側もRuntimeをプロジェクト参照する。

実装：[GameSession](../src/PureEngine.Runtime/GameSession.cs)、[PlaySession](../src/PureEngine.Runtime/PlaySession.cs)。
検証：[GameDependencyChecks](../tests/PureEngine.Core.Checks/GameDependencyChecks.cs)、PlayConnectionChecks、ProjectServiceRegistrationChecks。描画・配布用アプリは今回の範囲に含めない。

## 統合時の修正と検証の証跡

取り込み元：A1 `150e860`、A2／b1 `e882aa0`、A3／b2 `31e5d67`、A4／b3 `b0e586a`、A5／b4 `1da2b18`。

単体ブランチに残っていたstatic登録、旧Session API、同期的なEditor呼び出しを接続し直した。再読み込みは新コード用のサービス群で準備してから採用し、旧資源を解放する。A2の準備・採用処理はA4の所有者へ集約し、重複する候補Registry生成やpublish用の既定static経路を削除した。

2026-09-22、統合したコードで以下を実行しPASS。

```powershell
dotnet run --project tests/PureEngine.Core.Checks -c Release
dotnet run --project tests/PureEngine.Editor.Checks -c Release
```

追加の統合チェックでは、実際のMainWindow経路で完了順逆転、現在値・選択・Priorityの保持、旧Componentの単発解放、Play／ファイル操作／入力エラーによる保留、終了後の結果解放を確認した。ProjectSession.OpenAsyncではUI応答、UIスレッドでのComponent生成、キャンセル後の解放と別Sessionの保持を確認した。

UI応答の確認はAvalonia Headlessで行った。ネイティブ画面の目視検証や、実ゲームの描画性能測定を行った結果ではない。