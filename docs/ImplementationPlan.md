# PureEngine 実装計画・進捗

最終更新：2026-09-21

この文書を「どこまでできたか」「次に何をするか」の一覧として使う。
設計上の仕様は [EngineArchitecture.md](EngineArchitecture.md)、操作方法・起動手順は [README.md](../README.md) を参照する。
実装済みと動作確認済みは区別する。ライフサイクルの基本仕様は2026-09-21に合意済み。以下の実装工程は未着手であり、仕様の合意を実装完了とは扱わない。

## 現在の到達点

**LauncherからProjectを作成・再開し、シーンのオブジェクトにC#クラスを付けて値を編集し、YAMLで保存・復元できる。**

制作データを編集する基盤まで実装済み。Scene View／Gameはまだ描画・ゲーム実行を行わない。

## 実装済み

| 領域 | できること | 主な実装 |
| --- | --- | --- |
| Launcher | Project新規作成、既存Projectを開く、最近開いたProject、Editorからの復帰 | [LauncherWindow](../src/PureEngine.Editor/Windows/LauncherWindow.axaml.cs)、[ProjectSession](../src/PureEngine.Editor/Projects/ProjectSession.cs) |
| Project | manifest、複数シーン、起動シーン指定、相対パス、Projectフォルダの移動 | [ProjectFile](../src/PureEngine.Editor/Projects/ProjectFile.cs)、[ProjectDocument](../src/PureEngine.Core/Projects/ProjectDocument.cs) |
| Project Explorer | フォルダツリーとファイル一覧、シーンを開く、作成・改名・削除・更新、Components一覧 | [MainWindow.ProjectExplorer](../src/PureEngine.Editor/Windows/MainWindow.ProjectExplorer.cs) |
| Editorの配置 | 左がScene View／Game、中央がStuffs、右がInspector、下部がProject／Console。ペインのサイズ変更 | [MainWindow.axaml](../src/PureEngine.Editor/Windows/MainWindow.axaml) |
| シーンとオブジェクト | ID・名前、追加・選択・名前変更・削除 | [Scenes](../src/PureEngine.Core/Scenes/Scene.cs) |
| クラスのアタッチ | 普通のC#インスタンスをAttach／GetComponentで扱う。同じ型の重複を拒否 | [SceneObject](../src/PureEngine.Core/Scenes/SceneObject.cs) |
| ドラッグ＆ドロップ | ComponentsからStuffsの行、または選択中オブジェクトのInspectorへアタッチ | [MainWindow](../src/PureEngine.Editor/Windows/MainWindow.axaml.cs) |
| 属性 | Inspector・Start・Update・Destroyの定義、Inspectorメンバーとライフサイクルメソッドの検出 | [ComponentSchema](../src/PureEngine.Core/Components/ComponentSchema.cs) |
| Inspector | string・int・float・boolの表示と編集、数値の無効表示・エラー数、Escで復元、非有限floatの拒否 | [MainWindow](../src/PureEngine.Editor/Windows/MainWindow.axaml.cs) |
| シーン保存 | YAML version 1、ID・名前・typeId・Inspector値の保存と復元、固定IDのクラス登録表 | [SceneSerializer](../src/PureEngine.Core/Scenes/SceneSerializer.cs)、[ComponentRegistry](../src/PureEngine.Core/Components/ComponentRegistry.cs) |
| 保存時の保護 | 未保存確認、入力エラー中の保存拒否、検証後のシーン切り替え、一時ファイルからの置き換え | [MainWindow.Persistence](../src/PureEngine.Editor/Windows/MainWindow.Persistence.cs)、[SceneFile](../src/PureEngine.Editor/Scenes/SceneFile.cs) |
| ソース構成 | srcに実装、testsにチェック、docsに文書、toolsに起動スクリプト | [PureEngine.slnx](../PureEngine.slnx) |

## まだできないこと・制限

- Start／Update／Destroyは**検出まで**。呼び出す実行機構とPlay／Stopは未実装。
- Priorityの保持・Inspector表示・保存・実行順への適用は未実装。
- Parent、親子ツリー、オブジェクト・素材への参照の保存は未実装。
- クラスの登録はEditorにコンパイルされた共通のサンプルが対象。ProjectごとのC#コンパイル、外部 `.cs` の読み込み、自動探索は未実装。
- Inspectorと保存の対応型はstring・int・float・bool。配列・リスト・独自型などは未対応。
- YAMLのコメント保持・自動マイグレーションは未実装。固定typeIdは維持できるが、保存メンバーの改名にはデータ移行が必要。
- ゲーム内UI、描画、プレビュー、ゲーム実行ファイル、ゲーム進行のセーブ、通信・Steamは未実装。
- ペイン配置などのEditor設定の永続化は未実装。最近開いたProjectの履歴は保存済み。

## 仕様整理と次の実装順

### 1. ライフサイクルの実行条件を確定する

- [x] 基本のメソッド制約、不正な宣言の拒否、例外時の実行停止と後片付けを決める。
- [x] 全Start後にUpdate、追加分は次フレームから、削除予約後の呼び出し抑止と末尾のDestroy、Stop時の破棄と再Play時の新規作成を決める。
- [x] 編集用Sceneと実行用Sceneを分け、Play時に現在の制作データとInspector値を引き継ぎ、属性のないメンバーは初期値とする。
- [ ] 実装時に、Start前の削除・Start途中の例外でのDestroy対象範囲など境界条件を具体化する。

基本仕様と呼び出し順の例は [EngineArchitecture.md](EngineArchitecture.md) に記載済み。性能面は、探索結果とデリゲートの再利用・変更時だけの更新リスト整列を実装方針とし、実装後に測定する。

完了条件：実装前に必要なルールがアーキテクチャ文書に明記され、サンプルの期待する呼び出し順を説明できる。

### 2. Coreで最小の実行機構を作る

- [ ] Startを開始時に1回、Updateを明示的な1ステップごとに実行する。
- [ ] Destroyを確定した削除・停止のルールに従って実行する。
- [ ] Editorに依存せず、サンプルクラスで回数・dt・停止後の状態をチェックする。
- [ ] Play時の準備コストと、Update対象数ごとの1ステップの時間・割り当て量を分けて測定する。

完了条件：画面なしでサンプルクラスを開始・更新・終了できる。

### 3. Priorityをアタッチ設定として実装する

- [ ] ライフサイクル別にPriorityを保持する。初期値0、負の値も許可する。
- [ ] 存在するライフサイクルのPriorityだけInspectorに表示する。
- [ ] YAMLで保存・復元し、小さい順に実行する。同値の順番は保証しない。

完了条件：保存・復元した設定で実行順が変わり、チェックで確認できる。

### 4. Editorから実行・停止する

- [ ] 既存のPreview部分を実際の開始・停止操作につなぐ。
- [ ] 実行中の状態・例外を確認できるようにする。
- [ ] 停止後に編集用データが意図せず変わらないことを確認する。

完了条件：Editorでサンプルを実行・停止し、再実行できる。描画技術の選定は別途扱う。

### その後の候補（順番は未決定）

| 候補 | 着手前に決めること |
| --- | --- |
| Parentと参照 | 循環の拒否、親削除時の子の扱い、参照切れの扱い |
| Projectごとのゲームコード | コンパイル方法、クラス登録、エラー表示、再読み込み |
| ゲーム内UI・描画・プレビュー | 描画方式、Editorへの埋め込み、実行アプリとの共有 |
| Undo／Redo・Editor設定 | 対象操作、履歴の単位、設定の保存場所 |
| ローカル複数実行・通信 | ホストとクライアント、状態同期、テスト用起動方式 |
| Steam | ロビー・招待・参加、接続検証 |

描画・Steamなど設計書で保留している内容は、ここに載せたことをもって着手しない。

## 検証状況

2026-09-21、ライフサイクルの合意内容と性能の実装方針を文書へ反映。今回の変更は文書のみで、実行機構の実装・性能測定・テストの再実行は行っていない。

2026-09-21、src／testsへの移動後の構成で以下を再実行し、両方PASS。

```powershell
dotnet run --project tests/PureEngine.Core.Checks -c PlanCheck
dotnet run --project tests/PureEngine.Editor.Checks -c PlanCheck
```

- Core：オブジェクト操作、アタッチ、属性検出、YAML往復、不正データの拒否、保存失敗時の保護、Projectの作成・移動・復元。
- Editor：HeadlessでLauncher起動、新規作成、履歴からの再開、未保存確認のCancel／Discard、Launcher復帰、失敗時の表示、終了。
- 実画面の見た目、ネイティブファイルダイアログ、Project Explorerの全操作をこれらのテストで網羅したとは扱わない。今回の文書更新では実画面の操作確認は行っていない。

## この文書の更新ルール

1. 機能を実装したら「実装済み」と「制限」を更新し、対応する次工程のチェックを変更する。
2. 確認した内容・日付を「検証状況」に残す。コードがあるだけで動作確認済みにしない。
3. 仕様が変わったらEngineArchitecture.md、使い方が変わったらREADME.mdも更新する。
4. 次の候補と合意済みの作業を区別し、未着手の計画を完了扱いにしない。
