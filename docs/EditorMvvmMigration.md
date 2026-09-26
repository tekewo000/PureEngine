# Editor MVVM移行

専用ブランチで、既存の編集・保存・Play・再読み込みの動作を維持しながら、文書管理→各ペイン→MainWindowの接続整理の順に移行した。

## 順序と完了条件

1. **文書管理**：Scene・Prefab・DataAsset・表編集の所有、保存、dirty、切替、移行をUIから分離する。候補の検証、キャンセル時の保持、Component→サービス→コードの解放順を維持し、Windowなしのチェックを追加する。
2. **各ペイン**：Hierarchy・Inspector・Project・Console・DataAsset編集にViewModelを設け、表示状態・選択・入力検証・操作を担当させる。AvaloniaのControl、フォーカス、D&D、動的な編集部品の生成はViewに残す。
3. **MainWindowの接続整理**：MainWindowはViewとViewModelの接続、ネイティブ入力、ダイアログ、描画ホストを担当する。状態の重複や全処理を持つ巨大なViewModelを作らない。

Core・Runtime・Renderingの責務、namespace、保存形式は維持する。MVVM専用の外部依存は必要が生じない限り追加しない。各段階で `./tools/code-quality.ps1 -Check` を通し、最終差分をレビューした後、landスキルでCIを確認してmainへ反映する。

## 進捗

- 文書管理：所有・保存・コード移行の基盤分離を実装し、ローカル品質チェックを通過。
- 各ペイン：Console・Project・Hierarchy・Inspector・単体DataAsset・DataAsset表のViewModelとバインディングを実装し、ローカル品質チェックを通過。
- MainWindowの接続整理：実装し、ローカル品質チェックを通過。
- ローカル品質チェック：全段階で通過。CIとmainへの反映の証跡は対応するPR／Actionsで管理する。

検証結果は段階ごとに追記し、実画面の確認とHeadlessの検証を区別する。

### 文書管理の検証（2026-09-26）

`EditorDocuments` がScene・Prefab・単体DataAsset・表編集を所有する。この段階では表のControlと行高はViewに残し、データの保存・候補移行と分離した。`UserCodeReloadCoordinator` は単体DataAssetと表の候補を先に準備し、Sceneの移行成功時だけ一緒に採用する。終了確認途中では単体DataAssetを破棄せず、すべての確認が済むまで保持する。

`EditorDocumentChecks` をWindow生成前に実行し、文書切替、入れ子の所有判定、部分的な書込失敗後のdirty保持と再試行、事前シリアライズ失敗、候補移行、解放失敗時のサービス解放と二重解放防止を確認した。既存チェックのprivate field参照を既存／新規の読み取り専用プロパティへ更新し、検査する動作は維持した。

`./tools/code-quality.ps1 -Check` は提案診断、警告をエラー扱いにしたビルド、Core／Editor Checksを含め通過。初回は旧field名を間接参照する既存チェックで失敗し、参照先の更新後に全チェックを再実行して通過した。実画面・CIは未確認。

### 各ペインの検証（2026-09-26）

標準の`INotifyPropertyChanged`と`ICommand`を使い、外部依存を追加せずに各ペインの状態を分離した。Consoleは履歴・絞り込み・選択・Clear、Projectはフォルダと一覧、Hierarchyは展開・複数選択、Inspectorは選択・名前／数値編集・入力エラーとdirtyの振り分けを担当する。単体DataAssetと表は表示・保存判定・型一覧・行高を担当し、ファイル走査・行の追加／複製／削除は文書管理へ移した。スクロール、フォーカス、ポインター、動的Controlの生成はViewに残す。

`EditorPaneChecks`でWindowなしの通知・コマンド・選択保持・名前／数値の拒否・Play時の編集禁止・Project一覧を検証した。Console、Hierarchy、Scene View、表、単体DataAsset、PrefabのHeadlessチェックも実行し、最後に`./tools/code-quality.ps1 -Check`全体が通過した。

移行中に検出したDataAsset Inspectorの表示切替回帰は、文書を開いている状態と選択中の編集対象を分けて修正した。旧privateフィールドを参照していた既存チェックはViewModel経由へ更新し、動作の検査は保持した。別途、C#自動反映の待機タイムアウトとWindowsのファイル置換エラーが各一度発生したが、後続の個別・全体チェックでは再現しなかった。待ち時間・判定条件・保存処理を緩めず、反映失敗時の診断情報を追加した。実画面・CIは未確認。

### MainWindow接続整理の検証（2026-09-26）

`EditorViewModel` がProjectSessionからProjectComponents・編集文書・増分コンパイル状態を受け取る。`PlayViewModel` が実行Sessionとエラーを所有し、`CompilationViewModel` が要求世代・候補・採用を管理する。採用後はViewの購読がなくても各ペインの参照を更新する。保存コマンド、ファイル操作の競合状態、タイトルと操作可否はモデルに集約した。Hierarchyの追加・複製・削除もモデルへ移し、Scene／Prefabの読み込み・準備・旧Component解放は文書管理が扱う。

MainWindowはネイティブ入力、動的Control生成、ダイアログ、描画ホストとモデルの接続を担当する。コンテキストメニューにも初回表示前からDataContextを渡す。Gameタブ切替後にタイマーを開始する順序を維持し、終了時は購読とタイマーを解除してからモデルの所有資源を解放する。終了エラーがあっても実行・文書・サービス・コードの後片付けを続行し、保持されたモデルから古いユーザー型への参照を外す。

`EditorShellChecks`はWindowなしでPlayコマンド、編集データとの分離、操作可否、実際のC#再コンパイルによるペイン更新、互換性のない変更の拒否、終了を検証する。既存のPlay／Console／Hierarchy／Inspector／DataAsset／Prefab／再読み込みチェックも通過した。サービス登録の検証は明示的な要求を制御するためWatcherを停止し、自動配達と世代競合はUserCodeChecks・背景コンパイル・統合チェックで引き続き検証する。検査する条件は維持した。

最終の `./tools/code-quality.ps1 -Check` は提案診断、警告をエラー扱いにしたビルド、Core／Editor Checksを含め通過。実画面・実GPUの追加検証は行っていない。

## 保守時の責務

| 場所 | 責務 |
| --- | --- |
| `Editing/` | 文書所有、保存、移行、データの後片付け |
| `ViewModels/` | 選択・表示・入力エラー・操作可否、Play・コンパイルの状態とコマンド |
| `Windows/` | Control生成、ポインター・フォーカス・スクロール、ダイアログ、描画ホスト、購読の接続と解除 |
| `Core`／`Runtime`／`Rendering` | 既存の保存形式、実行、描画の責務を維持 |

新しい編集状態や処理を追加するときは、まずWindowなしで必要な動作を検証する。Control固有の入力と表示については既存のHeadless経路で接続も確認する。
