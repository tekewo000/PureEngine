# PureEngine 実装計画・進捗

最終更新：2026-09-22

この文書を「どこまでできたか」「次に何をするか」の一覧として使う。
設計上の仕様は [EngineArchitecture.md](EngineArchitecture.md)、操作方法・起動手順は [README.md](../README.md) を参照する。
実装済みと動作確認済みは区別する。2026-09-21、ライフサイクルの仕様整理とCoreの最小実行機構を完了。2026-09-22、Priorityの保持・Inspector・保存・実行順と、ゲーム用コンストラクタ注入（Coreのfactory、Game登録、編集・Play接続、PlaySession）を完了。2026-09-21、EditorのPlay／Stopボタン接続を完了。2026-09-23、共通ログAPIとEditorのConsole・Play接続を完了。2026-09-22、アーキテクチャ改善A1（プロジェクト側のサービス登録）を完了。

## 機能追加前のアーキテクチャ改善（完了）

**[アーキテクチャ改善5項目](ArchitectureImprovements.md)は5／5項目完了。2026-09-22にA1〜A5を接続した状態で検証した。**

プロジェクト側のサービス登録、MainWindowの責務分離、非同期コンパイル、プロジェクト単位の型登録、UI非依存のRuntimeを統合済み。各項目の完了条件・実装・検証結果はリンク先で管理する。新機能は今回追加していない。

## 現在の到達点

2026-09-22、診断対策と再発チェックを追加（コミット `d653219`）。namespaceを維持したコード整理、提案レベルを含む一括修正・検査、CI設定、ゲーム用のライフサイクルIDE0051抑制を実装済み。詳細は下記の検証状況を参照。

**LauncherからProjectを作成・再開し、シーンのオブジェクトにC#クラスを付けて値とPriorityを編集し、YAMLで保存・復元できる。**

制作データを編集する基盤に加え、Coreで独立した実行用Sceneを作り、画面なしでStart／Update／DestroyをPriority順に実行できる。EditorのツールバーにあるPlay／Stopで開始・停止でき、実行中の編集・切替は無効化する。Scene View／Gameの描画はまだ行わない。

## 実装済み

| 領域 | できること | 主な実装 |
| --- | --- | --- |
| Launcher | Project新規作成、既存Projectを開く、最近開いたProject、Editorからの復帰 | [LauncherWindow](../src/PureEngine.Editor/Windows/LauncherWindow.axaml.cs)、[ProjectSession](../src/PureEngine.Editor/Projects/ProjectSession.cs) |
| Project | manifest、複数シーン、起動シーン指定、相対パス、Projectフォルダの移動 | [ProjectFile](../src/PureEngine.Editor/Projects/ProjectFile.cs)、[ProjectDocument](../src/PureEngine.Core/Projects/ProjectDocument.cs) |
| Project Explorer | フォルダツリーとファイル一覧、シーンを開く、作成・改名・削除・更新、名前指定で空のsealedクラスを作るCreate C#。組み込みComponents一覧は表示しない | [MainWindow.ProjectExplorer](../src/PureEngine.Editor/Windows/MainWindow.ProjectExplorer.cs) |
| Editorの配置 | 左がScene View／Game、中央がStuffs、右がInspector、下部がProject／Console。ペインのサイズ変更 | [MainWindow.axaml](../src/PureEngine.Editor/Windows/MainWindow.axaml) |
| シーンとオブジェクト | ID・名前、追加・選択・名前変更・削除 | [Scenes](../src/PureEngine.Core/Scenes/Scene.cs) |
| クラスのアタッチ | 普通のC#インスタンスをAttach／GetComponentで扱う。同じ型の重複を拒否 | [SceneObject](../src/PureEngine.Core/Scenes/SceneObject.cs) |
| ドラッグ＆ドロップ | Projectの自作C#ファイルからStuffsの行、または選択中オブジェクトのInspectorへアタッチ | [MainWindow](../src/PureEngine.Editor/Windows/MainWindow.axaml.cs) |
| 属性 | Inspector・Start・Update・Destroyの定義、Inspectorメンバーとライフサイクルメソッドの検出 | [ComponentSchema](../src/PureEngine.Core/Components/ComponentSchema.cs) |
| Coreの実行 | 実行用Sceneの複製、開始・明示的な更新・停止、追加・削除予約、例外の報告と後片付け、Priority順の実行 | [SceneRuntime](../src/PureEngine.Core/Scenes/SceneRuntime.cs) |
| EditorのPlay／Stop | ツールバーのPlay／Stop、独立Sceneでの開始・一定間隔の更新・停止、編集中Sceneの分離、実行中の編集・切替の無効化、入力エラー時の開始拒否、失敗表示と後片付け | [MainWindow.Play](../src/PureEngine.Editor/Windows/MainWindow.Play.cs)、[MainWindow.axaml](../src/PureEngine.Editor/Windows/MainWindow.axaml) |
| Inspector | string・int・float・boolの表示と編集、数値の無効表示・エラー数、Escで復元、非有限floatの拒否、存在するライフサイクルのPriority表示と編集 | [MainWindow](../src/PureEngine.Editor/Windows/MainWindow.axaml.cs) |
| シーン保存 | YAML version 1、ID・名前・typeId・Inspector値・Priorityの保存と復元、固定IDのクラス登録表 | [SceneSerializer](../src/PureEngine.Core/Scenes/SceneSerializer.cs)、[ComponentRegistry](../src/PureEngine.Core/Components/ComponentRegistry.cs) |
| Priority | アタッチごとのStart／Update／Destroy保持、Inspector表示、YAML保存・Clone、実行順適用、変更可能期間の拒否 | [SceneObject](../src/PureEngine.Core/Scenes/SceneObject.cs)、[SceneRuntime](../src/PureEngine.Core/Scenes/SceneRuntime.cs)、[SceneSerializer](../src/PureEngine.Core/Scenes/SceneSerializer.cs) |
| ゲーム用コンストラクタ注入 | 普通のC#コンストラクタで依存を受け取る。Project側の登録口（`ConfigureGameServices`）と組み込み登録から、編集・Play別のprovider＋Scopeで生成。登録変更を含む再読み込み・Project読み込みは成功後に採用し、失敗時は旧状態を維持。終了順と失敗時解放を維持 | [ProjectGameServices](../src/PureEngine.Editor/Game/ProjectGameServices.cs)、[GameServices](../src/PureEngine.Editor/Game/GameServices.cs)、[GameSession・PlaySession](../src/PureEngine.Runtime/GameSession.cs)、[SceneSerializer](../src/PureEngine.Core/Scenes/SceneSerializer.cs)、[SceneRuntime](../src/PureEngine.Core/Scenes/SceneRuntime.cs)、[ComponentAssets](../src/PureEngine.Editor/Components/ComponentAssets.cs)、[MainWindow.UserCode](../src/PureEngine.Editor/Windows/MainWindow.UserCode.cs)、[ProjectSession](../src/PureEngine.Editor/Projects/ProjectSession.cs) |
| 保存時の保護 | 未保存確認、入力エラー中の保存拒否、検証後のシーン切り替え、一時ファイルからの置き換え | [MainWindow.Persistence](../src/PureEngine.Editor/Windows/MainWindow.Persistence.cs)、[SceneFile](../src/PureEngine.Editor/Scenes/SceneFile.cs) |
| 共通ログとConsole | `Log.Info/Warning/Error` の共有API、呼び出し元・例外詳細の保持、有界キュー。Consoleの一覧・フィルター・検索・詳細・コピー・Clear／Clear on Play、Play開始・停止・失敗とRuntime全件の重複なし取り込み | [Log](../src/PureEngine.Core/Log.cs)、[MainWindow.Console](../src/PureEngine.Editor/Windows/MainWindow.Console.cs)、[MainWindow.Play](../src/PureEngine.Editor/Windows/MainWindow.Play.cs)、[MainWindow.axaml](../src/PureEngine.Editor/Windows/MainWindow.axaml) |
| ProjectごとのC# | 任意フォルダのC#からアタッチ、保存・移動の監視、Stop後の反映、未保存値・Priority保持、失敗時の旧状態保持とConsole診断 | [UserCodeCompiler](../src/PureEngine.Editor/Compilation/UserCodeCompiler.cs)、[MainWindow.UserCode](../src/PureEngine.Editor/Windows/MainWindow.UserCode.cs) |
| 外部C#エディター | 新規作成・既存Projectを開く際にnet11.0のcsproj・slnxと不足分のSDK設定を生成。Core・DI・Analyzer参照を含み、手動設定は保持 | [ProjectCodeWorkspace](../src/PureEngine.Editor/Projects/ProjectCodeWorkspace.cs) |
| ライフサイクルの未使用診断 | Start・Update・Destroy属性付きメソッドのIDE0051だけを抑制。通常の未使用メソッド・別の同名属性には診断を残す | [LifecycleUsageSuppressor](../src/PureEngine.Analyzers/LifecycleUsageSuppressor.cs)、[UserCodeChecks](../tests/PureEngine.Editor.Checks/UserCodeChecks.cs) |
| コード品質 | 規約固定、一括修正、提案レベルの再解析、警告をエラー扱いにしたビルド、Core/Editorチェック。push/PR向けCI設定を追加 | [.editorconfig](../.editorconfig)、[code-quality.ps1](../tools/code-quality.ps1)、[CI](../.github/workflows/code-quality.yml) |
| ソース構成 | srcに実装と診断DLL、testsにチェック、docsに文書、toolsに起動・品質検査スクリプト | [PureEngine.slnx](../PureEngine.slnx) |

## まだできないこと・制限

- Start／Update／DestroyはCoreでPriority順に実行できる。EditorのPlay／Stopで開始・停止できる。ゲーム画面の描画・プレビューは未実装。
- Parent、親子ツリー、オブジェクト・素材への参照の保存は未実装。
- Projectの自作C#を自動コンパイル・登録する。独自csproj設定、外部NuGet依存の復元、Play中の実行状態を維持した差し替えは未対応。コンパイルはバックグラウンドで行い、Scene移行と採用はUIスレッドで行う。
- ゲーム用IDE0051抑制は生成csprojのAnalyzer参照で提供する。既存Projectは更新したEditorで再Openする。手動csprojへの参照追加は利用者が行う。CA1822など他の診断の自動抑制や、リポジトリの品質設定一式のゲームへの配布は対象外。
- Inspectorと保存の対応型はstring・int・float・bool。配列・リスト・独自型などは未対応。サービス参照に `[Inspector]` を付けない。
- YAMLのコメント保持・自動マイグレーションは未実装。固定typeIdは維持できるが、保存メンバーの改名にはデータ移行が必要。
- ゲーム内UI、描画、プレビュー、ゲーム実行ファイル、ゲーム進行のセーブ、通信・Steamは未実装。
- ペイン配置などのEditor設定の永続化は未実装。最近開いたProjectの履歴は保存済み。

## 仕様整理と次の実装順

### 1. ライフサイクルの実行条件を確定する（完了）

- [x] 基本のメソッド制約、不正な宣言の拒否、例外時の実行停止と後片付けを決める。
- [x] 全Start後にUpdate、追加分は次フレームから、削除予約後の呼び出し抑止と末尾のDestroy、Stop時の破棄と再Play時の新規作成を決める。
- [x] 編集用Sceneと実行用Sceneを分け、Play時に現在の制作データとInspector値を引き継ぎ、属性のないメンバーは初期値とする。
- [x] Start前の削除・Start途中の例外でのDestroy対象範囲、コールバック中のStop、Destroy中の変更拒否など境界条件を具体化する。

基本仕様・境界条件と呼び出し順の例は [EngineArchitecture.md](EngineArchitecture.md) に記載済み。探索結果・デリゲートの再利用と変更時だけの更新リスト変更を実装し、性能を測定した。

完了条件：実装前に必要なルールがアーキテクチャ文書に明記され、サンプルの期待する呼び出し順を説明できる。

### 2. Coreで最小の実行機構を作る（完了）

- [x] Startを開始時に1回、Updateを明示的な1ステップごとに実行する。
- [x] Destroyを確定した削除・停止のルールに従って実行する。
- [x] Editorに依存せず、サンプルクラスで回数・dt・停止後の状態をチェックする。
- [x] Play時の準備コストと、Update対象数ごとの1ステップの時間・割り当て量を分けて測定する。

完了条件：画面なしでサンプルクラスを開始・更新・終了できる。

### 3. Priorityをアタッチ設定として実装する（完了）

- [x] ライフサイクル別にPriorityを保持する。初期値0、負の値も許可する。ゲーム側に基底クラス・メンバーを要求せず、アタッチごとに独立させる。
- [x] 存在するライフサイクルのPriorityだけInspectorに表示する。入力検証・無効表示・Esc復元・未保存・保存拒否は既存の仕組みに合わせる。
- [x] YAMLで保存・復元し、小さい順に実行する。同値の順番は保証せず、テストでも固定しない。旧形式は0として読む。
- [x] Cloneで引き継ぎ、実行用と編集用の分離を維持する。実行中の変更可能期間（Start／Updateは初回Start前、DestroyはDestroy前）と明示的な拒否をAPI・文書・チェックで統一する。

完了条件：保存・復元した設定で実行順が変わり、チェックで確認できる。2026-09-22にCore・Editorのチェックと性能再測定で確認。

### 4. Editorから実行・停止する

- [x] 既存のPreview部分を実際の開始・停止操作につなぐ（`PlaySession` が接続口として準備済み）。
- [x] 実行中の状態・例外を確認できるようにする。
- [x] 停止後に編集用データが意図せず変わらないことを確認する。

完了条件：Editorでサンプルを実行・停止し、再実行できる。描画技術の選定は別途扱う。

### 5. 自作C#の読み込み・自動反映（完了）

- [x] 任意フォルダのC#をコンパイルし、元のファイル位置からアタッチする。
- [x] ファイル変更をまとめて検知し、Play中とInspector入力エラー中は反映を保留する。
- [x] 既存シーンの未保存値・ID・名前・Priorityを保持し、移行不能時は旧コードとデータを保護する。
- [x] Consoleの診断、保存後の復帰、Project切替時の登録・監視終了を確認する。

アタッチ条件・複数クラス・制限はREADMEの「自作C#と自動反映」を参照。

### その後の候補（順番は未決定）

| 候補 | 着手前に決めること |
| --- | --- |
| Parentと参照 | 循環の拒否、親削除時の子の扱い、参照切れの扱い |
| ゲーム内UI・描画・プレビュー | 描画方式、Editorへの埋め込み、実行アプリとの共有 |
| Undo／Redo・Editor設定 | 対象操作、履歴の単位、設定の保存場所 |
| ローカル複数実行・通信 | ホストとクライアント、状態同期、テスト用起動方式 |
| Steam | ロビー・招待・参加、接続検証 |

描画・Steamなど設計書で保留している内容は、ここに載せたことをもって着手しない。

## 検証状況

### 診断対策・再発チェック（2026-09-22）

コミット `d653219` の実装で `./tools/code-quality.ps1 -Check` がPASS。Debug構成のビルドは警告・エラー0件、提案レベルの解析に残件なし、Core/Editorチェックもすべて成功した。

- 実際のSDKのIDE0051診断を使い、Suppressorなしでは全対象に診断が出ること、追加後はStart・Update・Destroy属性付きだけ消えることを確認。通常メソッド、別の同名属性、属性の別名・完全修飾名、属性を外した後の診断復帰も確認した。
- 生成csprojのAnalyzer DLL参照、新規生成・既存Projectへの補完・手動管理設定の保持をEditorチェックで確認した。
- 一時的なCA1822違反を入れ、`-Check` がソースを変更せず失敗し、修正モードもインスタンスメソッドを勝手にstatic化せず残件を報告することを確認した。
- 既存namespace宣言の変更がないことを差分で確認。ライフサイクルや異常系テストに必要な例外は理由付きで局所的に抑制した。

GitHub Actionsのワークフローは追加済み。GitHub上での実行結果は未確認。今回のIDE0051検証は実際のRoslynによる生成プロジェクトのビルドで行い、Zed画面を直接確認した結果ではない。

### これまでの機能検証

2026-09-22、A1〜A5の統合後にCore／EditorのReleaseチェックがPASS。実際のEditor経路で非同期結果の採用順、編集中の値保持、Play・ファイル操作・入力エラー中の保留、終了後の結果解放、起動時のキャンセル、プロジェクト登録と共通Runtimeの接続を確認。UI応答とComponent生成スレッドはAvalonia Headlessで確認した。詳細は[改善計画の証跡](ArchitectureImprovements.md)を参照。

2026-09-22、アーキテクチャ改善A1（プロジェクト側のサービス登録）の実装後に以下を実行し、両方PASS。

```powershell
dotnet run --project tests/PureEngine.Core.Checks -c Release
dotnet run --project tests/PureEngine.Editor.Checks -c Release
```

- 追加分（Editor・ProjectServiceRegistrationChecks）：プロジェクト内の自作サービスとComponentで、編集・Playでの注入成功、同一セッション内の共有と編集・Play・再Play間の分離（Singletonを含む）、サービス登録変更の再読み込み成功（未保存値・ID・Priority・選択・未保存状態の維持と編集用サービス群の入替）、登録失敗（登録処理の例外・複数定義・形式不正）・依存解決失敗・Scene移行失敗時の旧状態保持（旧コード・編集Scene・サービス群・登録の維持と候補資源の解放）、正常終了・失敗時の解放順（Component→Scope・provider）と単発解放、登録口のない既存プロジェクトとの互換性（組み込み登録のみで従来どおり動作）を確認。
- 既存分：前回（共通ログ・Console追加後）の全項目を再確認。`ProjectSession.Open` の戻り値変更に伴い、Editorチェックの呼び出し側を更新（所有権は呼び出し側が持ち、失敗時は候補資源を解放）。
- 実画面の見た目、ネイティブファイルダイアログ、Project Explorerの全操作、タイマーの実測間隔は自動チェックの対象外。

クラス改名対応：クラス名＋名前空間変更後のID・Inspector値・Priority維持、保存後の再Open、旧シーンの名前空間変更からの初回移行、多対多改名時の拒否と管理ファイル保持、複数クラスの名前空間変更をEditorチェックで確認。

Create C#追加：Editor Headlessで左右の右クリックメニューのClickから名前入力・作成を確認。指定ひな形、拡張子省略、重複拒否、不正名拒否、キャンセル、自動コンパイルへの接続を検証。

C#編集用Workspace追加：Editorチェックで新規生成、既存Projectへの補完、変更なしの再書き込み抑止、手動csproj・SDK設定の保護を確認。TestProjectの生成csprojを.NET 11 SDKでビルドし、警告・エラー0件。Zedが使用しているRoslynサーバーへLSP接続し、生成slnxからの読み込みとLog.Info／Warning／Errorの補完応答を確認。

自作C#の自動反映追加時：Core・EditorのReleaseチェックがPASS。Editor Headlessで実ファイルを保存・移動し、コンパイル、自動更新、複数クラス／recordの対応付け、Play中の保留、Inspector入力エラーの保持、未保存値・Priority・選択フォルダの保持、コンパイル／スキーマ／コンストラクタ失敗からの復帰、保存・再開、別Projectの読み込み失敗時の既存登録保護を確認。ネイティブ画面でのドラッグ操作は今回の自動チェック対象外。


2026-09-23、共通ログAPIとEditorのConsole・Play接続の実装後に以下を実行し、両方PASS。

```powershell
dotnet run --project tests/PureEngine.Core.Checks -c Release
dotnet run --project tests/PureEngine.Editor.Checks -c Release
```

- 追加分（Core・Log）：`Log.Info/Warning/Error` の3種、呼び出し元情報の保持と転送、例外の内部・スタック詳細、通常ログのスタック非取得、別スレッド安全、有界キュー（1000件）と破棄数、無受信時の bounded、Avalonia非依存、記録のみで送出・停止なしを確認。
- 追加分（Editor・Console）：時刻・種類・先頭の一覧、件数付きフィルター、検索、詳細・記録箇所・例外詳細とコピー、Clear、Clear on Play（初期ON・Start前・開始ログ保持）、自動スクロールの位置保持、表示のみフィルターと再表示、まとめて取り込み（約200ms）、キュー／履歴上限（各1000件）と破棄表示を確認。
- 追加分（Play接続）：開始・停止・準備失敗・更新失敗・終了失敗のConsole表示、全件取り込みとStep／Stop／Disposeの重複防止、Object名・型・ライフサイクル名の保持、例外側情報の使用、既存ステータスと終了エラー時の残留維持、Stop後の可読、閉鎖時のタイマー解除と再開時の単一取り込みを確認。
- 既存分：前回（Play／Stop接続後）の全項目を再確認。
- 実画面の見た目、ネイティブファイルダイアログ、Project Explorerの全操作、タイマーの実測間隔は自動チェックの対象外。

2026-09-21、EditorのPlay／Stop接続の実装後に以下を実行し、両方PASS。

```powershell
dotnet run --project tests/PureEngine.Core.Checks -c Release
dotnet run --project tests/PureEngine.Editor.Checks -c Release
```

- Editor（Play／Stop接続）：ツールバーのPlay／Stopと実行状態に応じた有効化、独立SceneでのStart1回・継続Update・Stop時のDestroy1回、再Play時の新規SceneRuntime、編集中Sceneの実行前状態の保持、Inspector入力エラー時の開始拒否と理由表示、実行中の編集・切替の無効化とStop後の復帰、開始失敗・更新失敗時の後片付けと画面下部への表示、二重停止のno-op、実行中のウィンドウ終了時の終了・解放をHeadlessで確認。
- 既存分：前回（ゲーム用コンストラクタ注入後）の全項目を再確認。
- 実画面のPlay操作の見た目とタイマーの実測間隔は自動チェックの対象外。画面下部のメッセージで開始・停止を確認する。

- DI 終了処理の回帰確認：Stop 単独、Start 前の停止、コールバック中の Dispose／Stop、Runtime 直接停止、Start／Update 失敗で、Component → サービスの終了順序と単発解放を確認。Editor Headless では再読込・キャンセル・新規シーン切替・削除・終了時の Component 解放と、解放例外後の継続を確認。

2026-09-22、ゲーム用コンストラクタ注入の実装後に以下を実行し、両方PASS。

```powershell
dotnet run --project tests/PureEngine.Core.Checks -c Release
dotnet run --project tests/PureEngine.Editor.Checks -c Release
```

- 追加分（Core・factory）：`new()` 制約なしの型登録、Restore／Clone／Runtime／TryAttach の factory 生成、factory 未指定時の従来生成、factory 失敗時の報告（再試行なし）と対象の特定・元例外の保持、null・非exact type の拒否、ctor 実行後に Inspector 復元、準備失敗時の Start 抑止と生成逆順解放、通常終了の Destroy→Dispose 単発順序。
- 追加分（Game・MS DI）：`new` での直接生成、編集時の追加・保存・再読込、Play での注入と Inspector 復元、同一 Play 内の Scoped 共有、編集と Play・再 Play 間の分離（Singleton を含む provider 分離）、必須依存不足の Start 前失敗と対象特定、正常・失敗時の所有資源の単発解放とサービス二重解放の防止。
- 既存分：前回（Priority 追加後）の全項目を再確認。factory なしの既存 Component と既存テストは従来どおり動作する。
- Editor：既存の Headless 項目に加え、起動・作成・再開の経路が編集用 factory（編集用 Scope）で動作することを確認。Play／Stop ボタン接続は未実装のため `PlaySession` の単体実行で確認。
- 性能：同日に `dotnet run --project tests/PureEngine.Core.Checks -c Release -- --runtime-benchmark` を再測定。定常Stepの割り当ては全ケース0 Bを維持（factory 未指定経路の追加コストなし）。時間は単一環境での測定で桁の変化なし（例：1万空Updateで約0.064ms／ステップ、前回約0.137ms／ステップ、環境変動の範囲）。

2026-09-21、Start／Stopライフサイクル整備後に以下を実行し、両方PASS。

```powershell
dotnet run --project tests/PureEngine.Core.Checks -c Release
dotnet run --project tests/PureEngine.Editor.Checks -c Release
```

- Core：オブジェクト操作、アタッチ、属性検出、YAML往復、不正データの拒否、保存失敗時の保護、Projectの作成・移動・復元。
- Core実行：全Start後のUpdate、回数・dt、Inspector値の複製と非Inspector値の初期化、再実行、次フレームへの追加、削除予約・自己削除・Start前の削除、例外時の停止と全対象の後片付け、Stopの再入、Destroy中の変更拒否、不正宣言と継承・override。
- ライフサイクル整備（Core）：正常Start→更新→Stop、準備失敗時のStart／Destroy抑止と生成済みIDisposableの逆順解放、Start途中失敗時の残りStart抑止と全対象Destroy＋Dispose、Destroy／Dispose例外の継続と報告（メソッド名 `Dispose` を含む）、Stop／Dispose重複のno-opと単発保証、再Play時の新規インスタンスと状態非持ち越し、Play中の編集シーン分離（値・追加削除・Priority）。
- Priority（Core）：初期値0と負値、アタッチごとの独立、Start／Update／Destroyの独立、複数オブジェクトでの昇順、同値の順序不問、旧形式読み込み・保存往復・不正値拒否、Clone維持と編集／実行の分離、動的追加・削除予約・Stop・例外時の後片付け、変更可能期間の境界と拒否、開始バッチへの割り込み禁止。
- Editor：HeadlessでLauncher起動、新規作成、履歴からの再開、未保存確認のCancel／Discard、Launcher復帰、失敗時の表示、終了。
- Editor（Priority Inspector）：存在するライフサイクルのみ表示、表示名、編集と負値、入力エラーと保存拒否、Esc復元、未保存状態、通常メンバーとの分離。
- 実画面の見た目、ネイティブファイルダイアログ、Project Explorerの全操作をこれらのテストで網羅したとは扱わない。今回、実画面の操作確認は行っていない。

2026-09-22、Priority追加後に以下を実行し、両方PASS（参考。内容は上記に含めて再確認済み）。

```powershell
dotnet run --project tests/PureEngine.Core.Checks -c Release
dotnet run --project tests/PureEngine.Editor.Checks -c Release
```

- Core：オブジェクト操作、アタッチ、属性検出、YAML往復、不正データの拒否、保存失敗時の保護、Projectの作成・移動・復元。
- Core実行：全Start後のUpdate、回数・dt、Inspector値の複製と非Inspector値の初期化、再実行、次フレームへの追加、削除予約・自己削除・Start前の削除、例外時の停止と全対象の後片付け、Stopの再入、Destroy中の変更拒否、不正宣言と継承・override。
- Priority（Core）：初期値0と負値、アタッチごとの独立、Start／Update／Destroyの独立、複数オブジェクトでの昇順、同値の順序不問、旧形式読み込み・保存往復・不正値拒否、Clone維持と編集／実行の分離、動的追加・削除予約・Stop・例外時の後片付け、変更可能期間の境界と拒否、開始バッチへの割り込み禁止。
- Editor：HeadlessでLauncher起動、新規作成、履歴からの再開、未保存確認のCancel／Discard、Launcher復帰、失敗時の表示、終了。
- Editor（Priority Inspector）：存在するライフサイクルのみ表示、表示名、編集と負値、入力エラーと保存拒否、Esc復元、未保存状態、通常メンバーとの分離。
- 実画面の見た目、ネイティブファイルダイアログ、Project Explorerの全操作をこれらのテストで網羅したとは扱わない。今回、実画面の操作確認は行っていない。

### 実行機構の性能測定（2026-09-22、Priority適用後）

```powershell
dotnet run --project tests/PureEngine.Core.Checks -c Release -- --runtime-benchmark
```

Intel Core i5-13400F、Windows 10.0.26200 x64、.NET 11.0.0-rc.1.26425.128、Release。各ケース1オブジェクト1component、Inspectorのintを1つ持つ。Start／DestroyとUpdateは空で、Updateはインライン化を禁止。描画・ゲーム処理・追加削除は含めない。

準備は複製・デリゲートの結び付け・Startを含み、元Sceneの作成とStopを含めない。同一プロセスで最初の準備を別に測り、その後5回の中央値を記録。定常Stepは各ケース500msウォームアップ後、10,000ステップ×5回の中央値。割り当て量は `GC.GetAllocatedBytesForCurrentThread` の差であり、保持メモリ量ではない。

| オブジェクト数 | Update対象数 | 最初の準備 ms | 以後の準備 ms | 準備の割り当て B | Step µs | Stepの割り当て B |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 0 | 0 | 2.949 | 0.001 | 1,248 | 0.004 | 0 |
| 100 | 100 | 7.008 | 0.368 | 233,960 | 0.239 | 0 |
| 1,000 | 1,000 | 4.597 | 5.181 | 2,297,392 | 3.013 | 0 |
| 10,000 | 10,000 | 36.593 | 47.338 | 22,848,832 | 136.715 | 0 |
| 10,000 | 0 | 47.743 | 38.376 | 19,683,856 | 0.011 | 0 |

定常Stepの割り当ては全ケース0 Bを維持し、毎フレームの並べ替えやリスト再生成は行っていない（整列は開始バッチ・更新昇格・削除／停止時に限定）。時間は単一環境での測定であり、固定しきい値を合否条件にはしない。前回（2026-09-21）との差は環境変動の範囲であり、桁の変化はない。

### 実行機構の性能測定（2026-09-21、参考）

```powershell
dotnet run --project tests/PureEngine.Core.Checks -c Release -- --runtime-benchmark
```

Intel Core i5-13400F、Windows 10.0.26200 x64、.NET 11.0.0-rc.1.26425.128、Release。各ケース1オブジェクト1component、Inspectorのintを1つ持つ。Start／DestroyとUpdateは空で、Updateはインライン化を禁止。描画・ゲーム処理・追加削除は含めない。

準備は複製・デリゲートの結び付け・Startを含み、元Sceneの作成とStopを含めない。同一プロセスで最初の準備を別に測り、その後5回の中央値を記録。定常Stepは各ケース500msウォームアップ後、10,000ステップ×5回の中央値。割り当て量は `GC.GetAllocatedBytesForCurrentThread` の差であり、保持メモリ量ではない。

| オブジェクト数 | Update対象数 | 最初の準備 ms | 以後の準備 ms | 準備の割り当て B | Step µs | Stepの割り当て B |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 0 | 0 | 3.210 | 0.001 | 1,248 | 0.004 | 0 |
| 100 | 100 | 6.377 | 0.311 | 215,448 | 0.265 | 0 |
| 1,000 | 1,000 | 4.623 | 7.145 | 2,113,280 | 3.391 | 0 |
| 10,000 | 10,000 | 39.250 | 34.864 | 21,591,104 | 123.114 | 0 |
| 10,000 | 0 | 61.587 | 32.542 | 18,403,880 | 0.011 | 0 |

1万個の空Updateで約0.123ms／ステップ。型キャッシュ・JIT・GC・マシン負荷の影響を含む単一環境での測定であり、ゲーム全体のFPSを示すものではない。定常更新の0 Bは今回の空コールバック条件の値で、追加削除・エラー記録には割り当てがある。時間の固定しきい値をチェックの合否条件にはしない。

## この文書の更新ルール

1. 機能を実装したら「実装済み」と「制限」を更新し、対応する次工程のチェックを変更する。
2. 確認した内容・日付を「検証状況」に残す。コードがあるだけで動作確認済みにしない。
3. 仕様が変わったらEngineArchitecture.md、使い方が変わったらREADME.mdも更新する。
4. 次の候補と合意済みの作業を区別し、未着手の計画を完了扱いにしない。
