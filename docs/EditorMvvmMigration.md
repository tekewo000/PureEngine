# Editor MVVM移行

専用ブランチで、既存の編集・保存・Play・再読み込みの動作を維持しながら順に移行する。

## 順序と完了条件

1. **文書管理**：Scene・Prefab・DataAsset・表編集の所有、保存、dirty、切替、移行をUIから分離する。候補の検証、キャンセル時の保持、Component→サービス→コードの解放順を維持し、Windowなしのチェックを追加する。
2. **各ペイン**：Hierarchy・Inspector・Project・Console・DataAsset編集にViewModelを設け、表示状態・選択・入力検証・操作を担当させる。AvaloniaのControl、フォーカス、D&D、動的な編集部品の生成はViewに残す。
3. **MainWindowの接続整理**：MainWindowはViewとViewModelの接続、ネイティブ入力、ダイアログ、描画ホストを担当する。状態の重複や全処理を持つ巨大なViewModelを作らない。

Core・Runtime・Renderingの責務、namespace、保存形式は維持する。MVVM専用の外部依存は必要が生じない限り追加しない。各段階で `./tools/code-quality.ps1 -Check` を通し、最終差分をレビューした後、landスキルでCIを確認してmainへ反映する。

## 進捗

- 文書管理：所有・保存・コード移行の基盤分離を実装し、ローカル品質チェックを通過。ペインの操作コマンドは次段階で接続する。
- 各ペイン：未着手。
- MainWindowの接続整理：未着手。
- ローカル品質チェック：文書管理の変更で通過。CI・mainへの反映：未実施。

検証結果は段階ごとに追記し、実画面の確認とHeadlessの検証を区別する。

### 文書管理の検証（2026-09-26）

`EditorDocuments` がScene・Prefab・単体DataAsset・表編集を所有する。表のControlと行高はViewに残し、データの保存・候補移行と分離した。`UserCodeReloadCoordinator` は単体DataAssetと表の候補を先に準備し、Sceneの移行成功時だけ一緒に採用する。終了確認途中では単体DataAssetを破棄せず、すべての確認が済むまで保持する。

`EditorDocumentChecks` をWindow生成前に実行し、文書切替、入れ子の所有判定、部分的な書込失敗後のdirty保持と再試行、事前シリアライズ失敗、候補移行、解放失敗時のサービス解放と二重解放防止を確認した。既存チェックのprivate field参照を既存／新規の読み取り専用プロパティへ更新し、検査する動作は維持した。

`./tools/code-quality.ps1 -Check` は提案診断、警告をエラー扱いにしたビルド、Core／Editor Checksを含め通過。初回は旧field名を間接参照する既存チェックで失敗し、参照先の更新後に全チェックを再実行して通過した。実画面・CIは未確認。
