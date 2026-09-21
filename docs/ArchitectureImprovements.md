# 機能追加前のアーキテクチャ改善

## 方針と対応状況

以下の5項目をすべて完了してから、描画・入力・Parent・Undo／Redo・通信・Steamなどの新機能追加へ進む。改善に必要な実装と検証は先に行う。

**現在は1／5項目完了。A1が完了し、A2〜A5は未完了。** A2〜A5は課題・対応範囲・完了条件を文書化した段階で、改善実装はまだ行っていない。

「完了」はコードの変更だけでなく、各項目の完了条件を満たし、対応する動作チェックが通った状態を指す。着手後も完了条件が残っている項目は「未完了（対応中）」とする。

| ID | 改善項目 | 状態 | 完了の証跡 |
| --- | --- | --- | --- |
| A1 | プロジェクト側からゲーム用サービスを登録できるようにする | 完了 | プロジェクト登録口（`src/PureEngine.Editor/Game/ProjectGameServices.cs`）、編集・Play接続（`GameSession.cs`、`PlaySession.cs`）、再読み込み・Project読み込み（`MainWindow.UserCode.cs`、`Projects/ProjectSession.cs`、`Windows/LauncherWindow.axaml.cs`）。2026-09-22に `dotnet run --project tests/PureEngine.Core.Checks -c Release` と `dotnet run --project tests/PureEngine.Editor.Checks -c Release` がPASS（Editor側に `ProjectServiceRegistrationChecks` を追加）。 |
| A2 | MainWindowに集中した責務を分離する | 未完了（未着手） | — |
| A3 | C#コンパイルをUIスレッドから分離する | 未完了（未着手） | — |
| A4 | 型登録と読込コードをプロジェクト単位で所有する | 未完了（統合待ち） | A4節に実装箇所・検証結果・確認日を記録。Core/Editor Checks PASS。A1/A2/A3/A5統合は未検証。全体件数の更新は統合担当が行う。 |
| A5 | ゲーム実行の接続部分をEditorから独立させる | 未完了（未着手） | — |

## A1：プロジェクト側のサービス登録

**現状：完了（2026-09-22確認）。** プロジェクトの自作C#に `public static void ConfigureGameServices(IServiceCollection services)` を1つだけ定義し、エンジンのソースを変更せずにサービスを登録できる。編集・Play・再Playで同じ登録を使い、各セッションのサービス群は Singleton も含めて分離する。Coreは引き続き `Func<Type, object>` だけを受け、DIライブラリに依存しない。

完了条件：

- [x] エンジンのソースを変更せず、プロジェクト側でサービスを登録し、Componentのコンストラクタで受け取れる。
- [x] 編集時・Play時・再Play時に登録が適用され、サービスのインスタンスは既存仕様どおり分離される。
- [x] 登録や依存解決の失敗を報告し、生成済み資源を解放する。コード再読み込みでは、準備に失敗した新しい登録を採用しない。
- [x] サービス登録を含むコード変更の再読み込みを確認し、Componentとサービスの終了順・単発解放を維持する。

## A2：MainWindowの責務分離

**現状：未完了。** MainWindowはpartialファイルに分かれているが、同一クラスがInspector、Explorer、保存、Play、Console、コード再読み込みの状態と制御を持つ。ファイル分割だけでは、画面と処理の依存は分離されていない。

コード再読み込みの準備・採用・失敗処理を最初の切り出し対象とする。保存やPlayの調整も、画面に依存しない処理は既存のSessionや適切な所有者へ移し、MainWindowは操作の受付、ダイアログ、表示更新を担当する。全面的なMVVM化や、行数を減らすだけの分割は完了条件にしない。

主な対象：[MainWindow.UserCode](../src/PureEngine.Editor/Windows/MainWindow.UserCode.cs)、[MainWindow.Persistence](../src/PureEngine.Editor/Windows/MainWindow.Persistence.cs)、[MainWindow.Play](../src/PureEngine.Editor/Windows/MainWindow.Play.cs)。

完了条件：

- [ ] コード再読み込みの中核処理をMainWindow外で実行・検証できる。
- [ ] 編集Scene・実行Session・再読み込み状態の所有者と変更経路が明確になり、複数のpartialファイルで同じ制御を重複実装しない。
- [ ] 保存・Play・再読み込みの競合条件をUIコントロールの直接参照に依存せず判定できる。入力エラーの有無などは画面から必要な情報として渡す。
- [ ] 未保存確認、入力エラー中の保留、Play中の編集禁止、失敗表示、終了時の解放が従来どおり動く。

## A3：コンパイルのバックグラウンド実行

**現状：未完了。** [UserCodeWatcher](../src/PureEngine.Editor/Compilation/UserCodeWatcher.cs) がUIスレッドに変更通知を渡し、[ReloadUserCode](../src/PureEngine.Editor/Windows/MainWindow.UserCode.cs) が同期的にコンパイルする。プロジェクトが大きくなるほど、コンパイル中に画面操作が止まりやすい。

ソースの読み取り・コンパイルをバックグラウンドで行い、編集Sceneの移行・結果の採用・表示更新はUI側で行う。単に非同期化するだけでなく、採用時点の状態を確認する。

完了条件：

- [ ] プロジェクトを開く際とコード再読み込みの際に、コンパイルがUIスレッドを占有しない。
- [ ] 連続保存、プロジェクト切替、終了により不要になった古い結果を採用せず、その読込資源を解放する。
- [ ] コンパイル中に編集した値を古いSceneで上書きしない。採用時もPlay・ファイル操作・入力エラーの制約を守る。
- [ ] コンパイル失敗時は直前の正常な状態を維持し、修正後に再試行できる。
- [ ] 処理の重なりや完了順の逆転を含むチェックと、コンパイル中のUI応答確認を記録する。

## A4：プロジェクト単位の型登録と読込コードの所有

**現状：完了（A1/A2/A3/A5との統合は未検証のため統合待ち）。** 可変なstatic登録を解消し、`ProjectComponents` がプロジェクト単位にRegistry・自作型一覧とソース対応・採用中のコンパイル結果と読込コード（ALC）を所有する。`ComponentAssets` は状態を持たない共通処理（破棄・組み込み登録・候補生成・明示登録のアタッチ）のみを提供する。`ProjectSession` は `Project`・`Components`・`Scene` を束ね、開処理は候補Registryで検証してから `Adopt` する。`MainWindow` はSessionから所有権を移して `Serializer`・Inspectorのアタッチ・コード移行・Playにそのプロジェクトの登録を明示的に渡す。

型登録と読込コードをProjectSessionなどのプロジェクト単位の所有者へ移す。Serializer、Inspector、コンパイル結果の採用、Playには、そのプロジェクトの登録を明示的に渡す。複数ウィンドウを開く新しいUIの追加は必要としない。

完了条件：

- [x] プロジェクトごとにRegistry、ファイルと型の対応、読込コードの寿命を管理し、可変なstatic登録に依存しない。
- [x] 2つのプロジェクトを同時に保持し、一方の再読み込み・終了が他方の型解決、保存、Playへ影響しないことをチェックする。
- [x] プロジェクトを開く処理や再読み込みに失敗しても、既存の登録とSceneを保持する。
- [x] Component・サービスの終了とコードの解放要求の順序が明確で、不要な旧コードへの参照を所有者に残さない。
- [x] 既存のtypeIdと `.pureengine/types.json` の保存互換を維持する。

実装箇所：`src/PureEngine.Editor/Projects/ProjectComponents.cs`（所有者：Registry・UserTypes・UserFileTypes・Adopt/Clear/Dispose・CreateCandidateRegistry）、`src/PureEngine.Editor/Components/ComponentAssets.cs`（stateless化：RegisterBuiltins・CreateCandidateRegistry・CanAttach/TryAttach(registry, ...)・DisposeComponents）、`src/PureEngine.Editor/Projects/ProjectSession.cs`（Components所有・候補検証・失敗時旧状態保持・TransferOwnership/Dispose）、`src/PureEngine.Editor/Windows/MainWindow.*`（所有権移譲・明示登録のSerializer/アタッチ/Play/再読み込み・終了順序）、`tests/PureEngine.Editor.Checks/ProjectIsolationChecks.cs`（2プロジェクト同時保持・同名クラス・型解決/保存/Play独立・再読み込み/失敗/終了後の独立）、既存チェックの所有者対応（Core/Editor Checks全経路）。
検証：`dotnet run --project tests/PureEngine.Core.Checks -c Release` PASS、`dotnet run --project tests/PureEngine.Editor.Checks -c Release` PASS（EditorはPlay/所有権/Console/ユーザコード/分離/Priority/Launcherの全PASSを含む）。確認日：2026-09-22。
所有構造の変更は `docs/EngineArchitecture.md`（生成箇所・所有者・終了順序・C#再読み込み節）へ反映済み。組み込み型は所有者生成時に各Registryへ登録（sample.*維持、user.*のみ差し替え）する方針を明示した。
他項目との接続：A2・A3は `ProjectComponents.CreateCandidateRegistry`／`Adopt`／`Dispose`（状態確認は `ActiveUserCode`）を入口に使うこと。A1のサービス登録契約・A5の実行Session配置は作り直していない。A1/A2/A3/A5との統合動作は未検証（統合待ち）。

## A5：Editorに依存しない実行接続

**現状：未完了。** CoreのSceneRuntimeは画面なしで動くが、[GameSession](../src/PureEngine.Editor/Game/GameSession.cs) と [PlaySession](../src/PureEngine.Editor/Game/PlaySession.cs) はEditorアセンブリに置かれている。CoreチェックもこれらのEditorソースをリンクして使っており、共有する実行部分の境界がまだない。


サービス生成、Component生成、SceneRuntimeの開始・更新・停止を接続する部分を、Editor・Avaloniaに依存しない場所へ切り出す。配置するプロジェクト名や分割数は実装時に最小構成で決める。描画・入力・配布用ゲームアプリの実装は、この項目の完了条件には含めない。

完了条件：

- [ ] 実行接続部分からEditor・Avaloniaへの依存がなく、CoreもUI・DIライブラリへの非依存を維持する。
- [ ] EditorのPlayと、UIなしの実行チェックが同じ実行接続コードを利用する。
- [ ] A1のプロジェクト側サービス登録を使い、Sceneの準備・Start・Step・Stop・再実行を確認する。
- [ ] 正常終了と準備・開始・更新・終了の失敗時に、Componentからサービスへの解放順と単発解放を維持する。
- [ ] 共有する実行接続コードについて、テスト側でEditorソースをリンクする方式を解消する。

## 進め方と完了判定

項目のIDは評価時の5項目に対応する。実装順は依存関係に合わせ、A4・A5で所有と依存の境界を固め、A1を接続し、A2・A3でEditorの調整と非同期化を進める想定とする。関連項目を同じ変更で扱う場合も、完了判定は項目ごとに行う。

既存機能の回帰確認には以下を使い、新しい完了条件には必要な動作チェックを追加する。

```powershell
dotnet run --project tests/PureEngine.Core.Checks -c Release
dotnet run --project tests/PureEngine.Editor.Checks -c Release
```

直前のアーキテクチャ評価では、既存の両チェックはPASSした。この結果は現在の機能の確認であり、上記5項目の完了を意味しない。

対応時は各チェックボックスと一覧の状態を更新し、「完了の証跡」に実装箇所・実行したチェック・結果・確認日を残す。5項目すべての完了を確認するまで、新機能追加へ進まない。
