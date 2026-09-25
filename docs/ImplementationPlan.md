# PureEngine 実装計画・進捗

最終更新：2026-09-23

この文書を「どこまでできたか」「次に何をするか」の一覧として使う。

## Inspector Color対応とローカルImage変更の統合

- `Color`・`Color?`・配列・List・stringキー辞書をInspector／保存／Cloneへ接続。単体とNullableはRGBA数値と色見本、コレクション要素はRGBA数値で編集する。カラーピッカー・HSV／hex入力、コレクション要素の `Color?` は対象外。
- ローカルの `b7856a4`（Image use Color）を保持して統合し、描画境界でRGBAをVector4へ渡す。旧シーンとの保存互換性は [EngineArchitecture.md](EngineArchitecture.md) のYAML節を参照。
- レビューで型付きColor復元時の非有限値検証と、NullableのSet Null時に非表示入力欄のエラーが残る問題を修正。
- 統合後の `./tools/code-quality.ps1 -Check` はローカルで通過。旧Image保存形式の移行、RGBA／alpha描画、非有限値全チャンネル拒否、範囲外の有限値保持、色見本、Nullableエラー解除、配列／リスト／辞書編集を自動チェックした。CI結果はPRに記録し、実画面・実GPUは未確認。

## ローカルColor追加のレビュー

- `Values/Color.cs` にRGBAを保持する値型とHSV・hex変換を追加。保持値そのものは変更せず、変換で使用するチャンネルは非有限値を拒否し、RGB／彩度／明度／alphaを0〜1へ制限する。色相は負数・360度付近の丸めを含めて `[0, 360)` に収める。`ToHsv` はalphaを変換に使用しない。
- `InspectorValueTypes.cs` は内容・namespaceを変えず `Components/` から `Values/` へ移動。この時点では単独の値型のみ追加し、InspectorとImageへの接続は上記の後続工程で行った。
- `ColorChecks` に非有限値・範囲外・負の色相・360度付近・alpha保持の回帰チェックを追加。ローカルの `./tools/code-quality.ps1 -Check` は提案レベル解析、警告をエラー扱いにしたビルド、Core／Editorチェックを含め通過。CI・実画面は未確認。
- レビュー・修正・検証はDelta作業ツリーで実施。普段のチェックアウトへの反映・コミット・GitHub公開とは区別する。

## ローカルmainとGitHub mainの統合（2026-09-23）

- ローカルのScene参照機能（`ed16861`）・Sprite再オープン修正と、GitHubのPR #6（英語化・Unicodeテスト修正）を統合。Scene参照の入れ子Inspector経路と現在のnamespaceを維持し、競合した説明文は英語版へ合わせた。
- 統合後の `./tools/code-quality.ps1 -Check` は通過（提案レベル解析・警告をエラー扱いにしたビルド・Core/Editor全チェック）。この時点の確認はローカル自動検証であり、実画面・実GPU・統合コミットのCIとは区別する。
- 作業用コピーでの完了と普段のチェックアウトへの反映、GitHubへの公開は別工程。依頼された反映先のブランチとコミット一致を確認して完了報告する。Color型の相談は今回の統合に新規実装として含めない。

## プロジェクト再オープン時のSprite表示

- 画像IDの保存は正常でも、素材索引より先にInspectorを構築していたため、再オープン直後に `Missing` が残る問題を修正。
- レビューで、素材スキャン失敗時にComponentの解放が抜けないよう、シーン採用→素材スキャン→Inspector再構築の順に修正。また、名前が `Sprite` のenumプロパティを画像参照と誤認して例外になる問題を再現し、型も検証してから更新するよう修正。
- `SpriteReopenChecks` で、画像全体／切り出し付きSpriteの再オープン直後の選択・警告・説明文・ツールチップを確認。画像の欠落→復帰後の索引更新でも表示が追従し、Sprite参照・切り出し・未保存状態・保存済みYAMLを変更しないことを検証する。
- ローカルの `./tools/code-quality.ps1 -Check` は通過。今回の修正の実画面・実GPU・CIは未確認。先行するEditor単独チェックは既存の `ConsoleChecks.CloseReopen` のログ件数で一度失敗したが、上記の全チェックでは再現しなかった。

## ProjectペインのOSファイル取り込み（2026-09-23）

- ファイル／フォルダのD&Dコピー、同名の連番化、同じ親へのドロップのスキップを実装。操作・画像登録との違いは[README](../README.md)を参照。
- レビューで末尾区切り文字・ルートフォルダの包含判定、ドット付きフォルダの連番、取り込み元リンクの拒否、ストリーム失敗時の不完全ファイル削除を修正。ローカルコピーはバックグラウンドで実行し、途中の失敗でも一覧を更新する。
- 回帰チェックを追加し、ローカルの `./tools/code-quality.ps1 -Check`（提案レベルの解析・警告ゼロのビルド・Core/Editor Checks）は通過。OSからの実画面D&DとCIは未確認。
設計上の仕様は [EngineArchitecture.md](EngineArchitecture.md)、操作方法・起動手順は [README.md](../README.md) を参照する。
実装済み・自動検証済み・実画面確認済みは区別する。2026-09-21、ライフサイクルの仕様整理とCoreの最小実行機構を完了。2026-09-22、Priorityの保持・Inspector・保存・実行順と、ゲーム用コンストラクタ注入（Coreのfactory、Game登録、編集・Play接続、PlaySession）を完了。2026-09-21、EditorのPlay／Stopボタン接続を完了。2026-09-23、共通ログAPIとEditorのConsole・Play接続を完了。2026-09-22、アーキテクチャ改善A1（プロジェクト側のサービス登録）を完了。2026-09-23、下記のUI5項目（Component検索・追加から保存・Cloneまで）を実装し、自動検証を通過した。2026-09-23、V4前半のScene View編集操作（グリッド・パン／ズーム・選択・XY移動Gizmo・F表示）を実装・レビュー修正し、自動検証と実GPUチェックを通過した。実画面は表示を確認済み。2026-09-23、Stuffsの親子ツリー表示とドラッグ＆ドロップの子付け・並べ替えを実装し、自動検証を通過した。Stuffsの主要なドラッグ操作・折りたたみ・改名は実画面でも確認済み。青線とホバー待機の目視、実画面での保存往復、今回の差分のCIは未確認として区別する。2026-09-23、Buttonに先立つ描画順の共通基盤（`RendererComponent.Order`）を実装し、自動検証を通過した。実画面・実GPU・CIは未確認として区別する。2026-09-23、Game表示とButton操作（Game描画・`core.button`・`IUiButtonHandler`・Game入力・更新境界ディスパッチ）を実装し、自動検証を通過した。実画面・実GPU・CIは未確認として区別する。今回はGame表示とButton操作までとし、V3〜V5全体の完了とは区別する。2026-09-23、Transformのみの親のGizmo表示と子の配置追従を修正し、自動検証を通過した。実画面・実GPU・CIは未確認として区別する。

## 次に着手する作業

### Inspector参照欄へのD&D拡張（2026-09-25実装）

- SceneObject／登録Componentの参照欄を、ComboBoxだけでなく欄全体の行で受け付ける。Stuffsの行は従来どおり同じSceneのIDを解決し、PrefabファイルはHierarchy外の生成元Rootまたは型一致Componentを一意に選んで割り当てる（2026-09-25修正）。DataAssetは型一致時だけ既存のID経路で割り当てる。
- Projectの画像ファイルを`Sprite`欄へドラッグできる経路を追加し、登録済みImage IDをそのまま設定する。SceneObject、Component、DataAsset、PrefabのDragOver／Drop、型不一致・複数候補・Play中拒否、参照欄の行全体へのDropをEditor Checksで確認する。
- 参照欄とSprite欄の行に透明な背景を設定し、ラベルと入力欄の間の余白もヒットテスト対象にする。Editor Checksの画像DropはSprite未設定からの割り当てを検証する。余白への実画面Dropは未確認。
- InspectorへのPrefab割り当てで生成される経路を削除。クラス型フィールドに非実行テンプレートを割り当て、保存・Play・コード再読み込みに引き継ぐ。`PrefabSpawner.Instantiate<T>`で明示的に生成する。Drop中の画面更新後に親行で再処理される経路も修正。保存形式と寿命の正本は[設計書](EngineArchitecture.md#prefabs)。
- 回帰チェックに欄／行Dropでオブジェクト数不変、Clearと保存往復、型付き生成と削除、Missing ID保持、コレクション参照、Play分離、テンプレートのライフサイクル非実行とDisposeを追加。ローカルの`./tools/code-quality.ps1 -Check`（提案レベル解析・警告ゼロビルド・Core/Editor Checks）は通過。当初はDLLロックを避けた別出力先で検証し、Editor終了後に通常出力先でも同じチェックを再実行して通過した。実画面の手動D&DとCIは未確認。


### Prefab相当のコピーのみ複製（2026-09-24実装）

- 単一ルート＋子孫を `.pure.prefab.yaml`（version 1・PrefabのID・objects）へ保存し、配置時は普通のSceneObjectとして複製する。リンク・Override・Variant・入れ子は作らない。内部参照だけ新IDへ付け替え、範囲外・画像・データアセット参照は維持し、MissingはScene参照の既存規則に従う。移行規則はScene流用でPrefab独自の仕様は作らない。
- Coreに `PrefabDocument`・`PrefabSerializer`（保存・読み込み・検証・配置・巻き戻し）・`PrefabCatalog`（フォルダ走査と診断）・`PrefabSpawner`（コンストラクター注入で `Spawn`）を追加。値変換・旧名解決・membersChangedは `[Inspector]` 規則を再利用し、`SceneSerializer` の生成・Priority・メンバー走査を内部共有する。`PlaySession` 準備時に実行用Scene・factory・カタログを束縛する。
- EditorはStuffs右クリックの保存、Projectペインの一覧・改名・削除、配置メニューとダブルクリック配置、Project→StuffsへのD&D配置（行上はその子、余白はルート、行のハイライト付き）、Play中の保存・配置・D&D禁止に対応する。保存は既存ファイルを上書きしない。実行中はPlay開始時にカタログを作り直し、編集用と各Play実行で共有しない。
- 回帰チェック（保存往復・新ID・内部／外部参照・コレクションと入れ子・Missing・Priority・旧名と不明項目・型変更と未知型の拒否・構造拒否・巻き戻し・カタログ走査・スポナー束縛・2回のPlay分離とStart／Update生成）を Core Checks の `PrefabChecks` に、保存・一覧・配置・親付け・不正ファイル・ダブルクリック・Playガードを Editor Checks の `PrefabEditorChecks` に追加。ローカルの `./tools/code-quality.ps1 -Check`（提案レベルの解析・警告ゼロのビルド・Core/Editor Checks）は通過。実画面の手動操作・CIは未確認として区別する。
- 設計は [EngineArchitecture.md](EngineArchitecture.md#prefabs)、操作は [README](../README.md#プレハブを使う)を参照。回帰チェックは配置メニュー経路に加え、D&DのDragOver／Dropの実経路とPlayガードを確認する。ドラッグ開始のOS側ループは対象外。

### データアセットの作成と保存（2026-09-24実装）

- 直接参照対応：`[Inspector] public TestDataAssets DataAssets { get; init; }` の宣言を変えず、プロジェクトのアセットをドロップダウン・D&Dで割り当てる。公開ラッパー案は採用せず、保存IDをエンジン側で解決する。Coreで共有・Clone/Play分離・コレクション・Missing/復旧を、Editorで実際のヘッドレスポインターD&D・Clear・保存再Open・C#再反映・2回のPlayを検証する。実画面の手動操作およびユーザーのTestProjectの書き換えは行わない。品質ゲートとCIの結果はこの変更のPRで追跡する。
- 継承なしの普通のクラスに `[DataAsset]` を付けて Create Data Asset メニューから作る。対象はpublic・非abstract・非ジェネリックでpublicな引数なしコンストラクターを持つクラス。メニューパス省略時は型名。使えない型・重複メニューは理由を表示する。
- Coreに `DataAssetAttribute`（`Inherited = false`・任意のメニューパス）・`DataAssetDescriptor`・`DataAssetDocument`・`DataAssetSerializer` を追加。値の変換・旧名解決・membersChanged報告はシーンの `[Inspector]` 規則を再利用し、シーン参照は拒否する。ファイルは `.pure.asset.yaml`（version 1・ID・typeId・values）。
- Editorはコンパイル結果に `DataAssetTypes` を公開し、ProjectペインのTree／Files両メニューにフォルダ階層付きの作成 submenu を出す。作成・一覧・改名・削除に対応し、改名では拡張子を維持する。
- 回帰チェック（記述子判定・メニュー表記・YAML往復・旧名・コンパイル検出・ファイル作成）を Core Checks に追加。設計は [EngineArchitecture.md](EngineArchitecture.md#data-assets)、操作は [README](../README.md#データアセットを作る) を参照。
- つなぎ込み（2026-09-24実装）：Project欄の選択でInspectorに読み込み、シーンと同じ行エディターで編集・保存する。dirtyはアセット到達集合の所有で振り分け、未保存は切替・終了時に確認する。C#再反映はYAMLを新旧の型IDで付け替える。実行中はCoreの `DataAssetStore` を編集用・Play用のサービスに登録し、スナップショットとして読み取る。回帰チェック（ストア走査・Editorの開く・編集・保存検証・選択引継ぎ）を追加し、ローカルの `./tools/code-quality.ps1 -Check` は通過。実画面・CIは未確認として区別する。
- マージ前レビューで、新規作成の非上書き公開、同名ディレクトリの回避、Tree／Files別の作成先、Play中の実行ガードを修正。メニューはType自体ではなく安定した型IDを保持し、クリック時に現在の型を解決する。非public・abstractなど登録されない属性付き型も診断し、シーン参照を含む型はメニュー判定の時点で拒否する。
- 修正後のローカル `./tools/code-quality.ps1 -Check` は終了コード0で通過。Coreの非上書き・不正宣言チェックとEditorのメニュークリック・作成先・初期値・一覧・Scene非変更・Playガードの自動チェックを追加。実画面の手動操作は未確認。CIの結果はPRで追跡する。
- Inspector接続のマージ前レビューで、アセット単独の未保存・無効入力の終了確認、Cancel時の入力保持、Play中の編集禁止、開いたアセットの改名・削除前の確認を修正。C#採用前にアセット復元を検証し、失敗時はScene・アセット・旧コードをまとめて保持する。終了確認・再反映の失敗と成功・ゲーム側コンストラクター注入と2回のPlay間の分離を回帰チェックに追加。検証は同じ品質スクリプトで行い、実画面の手動確認とは区別する。
### SceneObject・ComponentのID参照とInspector接続（2026-09-23合意・実装済み）
### SceneObject・ComponentのID参照とInspector接続（2026-09-23合意・実装済み）

**実装済み。全SceneObject・全ComponentにIDを付け、保存ではID参照、ゲーム実行中は解決済みの通常のC#参照を使う。** ユーザーが重視するのはエンジン自身とゲーム実行時の性能、および使う側のルールの単純さ。Inspectorに割り当てたときだけIDを発行する方式、ゲーム側に `ObjectRef<T>`／都度の `Resolve` を要求する方式は採用しない。ローカルの `./tools/code-quality.ps1 -Check` は通過。実画面のStuffs→欄D&D・保存→再Open・PlayでのButton接続の目視、CI実行は未確認として区別する。10,000件のローカル性能比較は下記レビュー修正後の測定で確認した。

設計の正本は[SceneObject・Componentの直接参照](EngineArchitecture.md#sceneobjectcomponentの直接参照2026-09-23合意実装済み)。従来のV3のObjectRef案に優先する。過去の「ObjectRefは対象外」は過去工程の範囲を示し、今回の着手を禁止するものではない。

#### 実装範囲と工程

ゲーム側の完成形は次の通常のC#宣言・操作とする。Inspectorで選んだ対象と同じ実行用インスタンスへ接続し、detachedなButtonの埋め込みコピーを作らない。

```csharp
[Inspector]
public Button? TestButton { get; set; }

[Start]
private void Start()
{
    if (TestButton is { } button)
        button.Interactable = false;
}
```

| 工程 | 実装内容・完了条件 |
| --- | --- |
| 0. 現行経路と型の区分 | `.codegraph/` があればCodeGraphから調査する。Componentの生成・Attach・Detach・実行中の追加削除、Inspector変換、Capture／Restore／Clone、SceneCodeMigrator、Project単位Registryを追う。登録Component型を参照として扱うことが既存の自作クラス値・Transform・自動型登録へ与える影響を列挙し、正本の区分と移行規則を確定してから編集する。Componentカードで自身の値を編集する操作と、他Component内の参照欄を区別する |
| 1. IDとScene内の索引 | SceneObjectの既存Guidを再利用し、全Componentにエンジン所有のインスタンスGuidを付ける。普通の `new` 自体はフックせず、エンジンの生成／Attach共通経路でSceneへの公開前に必ず付与する。未参照Componentも対象。既存の所有権・重複型制約を維持し、同じ実物の多重所有を拒否する。復元時は保存IDを採用し、非空IDとScene内のObject／Componentを跨ぐ重複を検査する |
| 2. 保存・復元・Clone | `version: 3` を書き出し、Componentの `id` と既存の `typeId` を分離、参照値を `{ ref: <Guid> }` とする。全対象の生成・登録後、全参照を解決し、すべてのStartより前に接続を完了する。前方参照・自己参照・相互参照を許可。CloneはIDを保持しClone先だけで接続する。既存factory、Priority、親子・兄弟順、失敗時の逆順Disposeと旧Scene保護を維持する |
| 3. 旧データ・コード再読み込み | v1／v2は既存SceneObject IDを保持し、不足するComponent IDを一度発行、Editorを未保存化する。旧インラインComponent値を対象名や値の一致から推測して接続しない。移行不能な旧値は元データを保持して明示診断し、再割り当て／明示破棄するまで破壊的な上書き保存を拒否する。保持機構が成立しない場合は復元全体を拒否して元Scene・元ファイルを守る。コード再読み込みは同じ保存・参照解決経路を使い、IDとFormerlySerializedAsを維持し、旧インスタンス／Type／Assemblyを新Sceneの管理情報へ残さない |
| 4. 欠落と変更時の接続更新 | 解決先なしはC#メンバーをnull、管理情報にはIDを保持しMissingとして保存可能にする。対象削除・取り外し・交換・Scene差し替えの変更境界で影響する接続を更新する。別対象への再設定と明示Clearは保持IDを更新／除去する。対象が後で同じIDで復元された場合も再接続する。毎フレーム全メンバーを走査しない。通常のC#からの代入、配列／List／辞書の変更と保持IDの整合、および既にゲーム側で別対象へ変更した欄を古い接続情報で上書きしない処理を検証する |
| 5. Inspector | SceneObject・登録Component型の参照欄に対象名・ID・None／Missing・選択／解除を表示。Stuffs（Hierarchy）から参照欄へのD&Dを、既存のComponentアタッチ形式と分離して追加する。型・Scene所属を検査し、不適合／曖昧な候補を勝手に選ばない。Play中の編集拒否、変更時だけの未保存化、既存のAutomationProperties.Name規約を維持する |
| 6. 検証・文書 | 下記のCore／Editor Checksと性能測定を実施し、問題を修正する。READMEに宣言・設定・Missing・旧値の付け直し手順、設計書に最終仕様、この計画書に検証証跡を記録。実装済み・ローカル自動検証済み・実画面／CI確認済みを区別する |

主な対象は `SceneObject`／`Scene`／`SceneRuntime`、`SceneDocument`／`SceneSerializer`／`SceneCodeMigrator`、`ComponentRegistry`／`ComponentSchema`／`InspectorValueTypes`、Projectの型登録、`MainWindow.Inspector*.cs`／`MainWindow.Hierarchy.cs`、既存Core／Editor Checks。名前だけで変更範囲を固定せず実際の呼び出し元を追い、既存の共通経路へ接続する。

対象はSceneObject・組み込み／自作Componentの単体参照と、既存の対応コンテナー（一次元配列・List・stringキーDictionary）および埋め込み値内の参照。コンテナーの入れ子等、既存の未対応範囲は広げない。参照型は `T?` によるnullを扱い、`Nullable<T>` をclassへ適用しない。Object／Component参照の相互循環と、埋め込み値・親子関係の禁止された循環を混同しない。

Scene全体のCloneと同一Scene内への複製は分ける。同一Scene内にコピーを追加する経路が存在する場合は対象に新IDを発行し、コピー範囲内部の参照だけ新IDへ付け替え、範囲外への参照を維持する。新しい複製UIの追加は要求しない。画像・フォントの参照方式変更、シーンを跨ぐ参照、パス束縛、イベントバス、コード生成、Undo機構も今回の対象外。

`D:\PureEngineProjects\TestProject` は再現例として扱う。本作業だけを根拠に外部Projectの `Test.cs` やシーンを上書きせず、まずリポジトリ内Checksで再現する。`init` の扱いは既存のInspector規約・検証と整合させ、参照実装のためだけに外部コードを `set` へ一括変更しない。

#### 必須の動作検証

- Core：SceneObject／Component双方の参照、型違い・別Scene・未アタッチ対象の拒否、空／重複／不正ID、前方・自己・相互参照、nullとMissingの区別、削除後のID保持と再保存・再接続を確認する。
- 保存・移行：単体／配列／List／辞書／埋め込み値内の往復、FormerlySerializedAs、v1／v2移行と旧インライン値の保護、Clone後の実物の分離とID維持、コード再読み込み成功／失敗時の旧Scene保護を確認する。既存の同一Scene内複製経路がある場合は内部・外部参照の付け替えも検証する。
- 実行：接続がStart前に完了すること、Testの参照先が実際のButtonと同一でクリック購読・Interactable変更がその実物に届くこと、Playから編集Sceneを変更しないこと、実行中の生成・削除境界を確認する。イベント購読そのものは保存・Cloneしない。
- Editor Headless：選択・解除・有効／無効なD&D、対象削除後のMissing、未保存状態、Play中拒否、Scene切替・コード再読み込み後の接続と旧インスタンス解放を確認する。実画面ではStuffs→欄のD&D、保存→再Open、PlayでのButton接続を別途確認する。
- C#／解析設定の実装後はルートの `./tools/code-quality.ps1 -Check` を必須とする。失敗は修正し、未実行・実画面やCIの未確認事項は完了報告へ明記する。

#### 性能の受け入れ条件と測定

性能は「IDがあるから軽い」と推測で完了扱いにしない。実装前後を同じ環境・Release設定で比較し、準備と定常実行を分ける。既存の `--runtime-benchmark` 経路を優先し、小さな測定ケースを追加する。新しいベンチマーク基盤や依存は導入しない。

| 測定対象 | 記録するもの・受け入れ条件 |
| --- | --- |
| 準備と保存 | 対象数N・参照数Rを変え、Deserialize／Clone／Play準備の経過時間・割り当て量、生成後の保持メモリとYAMLサイズを別々に記録。辞書による参照索引構築・接続部分は概ねO(N＋R)を目標とし、参照一つごとの全対象走査を避ける。既存の他処理まで線形と主張しない |
| 編集時 | 参照設定・解除、対象の削除／交換と影響参照数に対する更新時間を記録。変更イベント時だけ接続情報を処理し、アイドル時に全Sceneの参照を再構築しない |
| 定常実行 | 参照なし／同数の直接参照ありでUpdateの時間・割り当て量を比較。例えば1,000／10,000Componentで複数回アクセスするケースを置く。通常のメンバーアクセスにはID検索・反射・接続再構築を挟まないことをコード経路と検索／再接続回数のチェックで確認し、参照維持だけを原因とする定常フレームのGC割り当てを0 Bにする |
| 変更のある実行 | 生成・削除時の更新コストを定常実行から分離して測る。変更時の必要な処理・割り当てを定常0 Bと混同しない |

時間はウォームアップ後の複数回測定の中央値と環境・件数・手順を記録し、不安定な固定ミリ秒しきい値をCIの合否に使わない。再現する悪化は原因を調べて修正または制約として報告する。任意にコピーされたローカル変数や `[Inspector]` 外のC#参照まで自動無効化できるとは説明しない。

**完了条件：通常の `Button?` 欄へ実物を設定でき、IDで保存・復元・Clone・コード移行し、Missingを保持できること。全Start前に接続され、定常実行で参照のための検索・走査・割り当てが追加されず、必須チェックと性能比較の証跡が揃うこと。**

#### ID参照の検証（2026-09-23）

- ローカル品質：`./tools/code-quality.ps1 -Check` は終了コード0でPASS。提案レベル診断・警告をエラー扱いにしたビルドの警告／エラー0、Core／Editorチェックがすべて通過した。
- Core追加（`SceneReferenceChecks`）：SceneObject／Component双方の参照、型違い・別Scene・未アタッチ対象の拒否、空／重複／不正ID、前方・自己・相互参照、単体／配列／List／辞書／埋め込み値内の往復、nullとMissingの区別、削除後のID保持と再保存・同ID再接続、Clone後の実物分離とID維持、Start前接続とButton同一性（クリック購読・Interactable変更が実物へ届くこと、購読自体は保存・Cloneしないこと、Playから編集Sceneを変更しないこと）、旧インライン保持と破壊的保存拒否・明示破棄後の保存可を確認。同一Scene内複製経路は存在しないため、新ID発行・範囲内付け替えの分岐は未使用として区別する。FormerlySerializedAs・v1／v2移行・コード再読み込み成功／失敗時の旧Scene保護は既存経路（保存・参照解決の共有、ID維持、旧インスタンス残存なし）と既存チェックで確認する。
- Editor追加（`ReferenceEditorChecks`）：参照欄の選択・解除、有効／無効なStuffs D&D解決（型・Scene所属検査、曖昧候補の非選択）、対象削除後のMissing表示、変更時だけの未保存化、Play中の編集拒否を確認。Scene切替・コード再読み込み後の接続と旧インスタンス解放は既存の分離・所有権チェックとCoreのClone／移行で確認し、Editor固有の追加断定はしない。レビュー修正で埋め込み内List／辞書の参照編集と、Missingを含む行削除・キー変更の追従を追加検証した。
- 実画面確認：未実施。Headlessでの選択→保存相当→再Open相当→Play→Button接続→Stopは自動検証済みとして区別する。実EditorでのStuffs→欄のD&D、保存→再Open、PlayでのButton接続の目視は未確認。
- CI：未実行。GitHub Actionsの結果確認は未実施。

#### ID参照のレビュー修正・性能測定（2026-09-23）

- 登録Component型の未アタッチ値が埋め込み保存へ戻る経路を除去。v3の不正ref・インライン値、Object／Component間の型違いを拒否し、v1／v2の旧インライン値を保持して保存・Cloneによる消失を防ぐ。再割り当て後に古いMissing IDが復活しないことも検証した。
- 参照を一つ含むだけで未対応型・埋め込み再帰・コンテナー入れ子を許可していた型検証を修正。削除時の走査を共通化し、実行中の削除バッチに一度だけ適用する。Stopでは破棄済みComponentを再走査しない。1万件で未完了だった測定の原因には、停止時の反復全走査と、編集測定の重複名生成の入れ子走査が含まれていた。測定セットアップは一意名を使う。
- Inspectorのプログラムによる選択肢更新とユーザー操作を分離。NoneへのClearは未保存化せず、Detached値も参照欄として再設定できる。入れ子の参照コレクション編集、Missing付き行の削除・辞書キー変更、記号を含む辞書キーの保持パス衝突を修正。Core／Editorの回帰チェックと、実際のRouted DragOver／Dropイベントで接続を確認した。
- 合意どおり登録型を参照へ統一したため、既存のenum／埋め込み値のコード再読み込みテストは補助型を明示的にRegistryから外して値として検証する。通常Projectではpublic具象クラスが自動登録されることをREADMEに明記した。
- 条件：`dotnet run --project tests/PureEngine.Core.Checks -c Release -- --runtime-benchmark`、Windows 10.0.26200・X64・.NET 11 RC1、時間は5標本の中央値、500msウォームアップ、10,000ステップ／標本。固定msしきい値を合否に使わない。以下は修正後の同一実装で参照の有無を比較した値であり、ID導入前との厳密なA/B測定ではない。

| 対象数／参照数 | Play準備 ms | 準備中割り当て B | YAML文字数 | 定常Step μs | B／Step | Stop ms | 実行用グラフの保持量概算 B |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2,000／0 | 8.960 | 9,750,480 | 430,472 | 4.170 | 0 | 0.712 | 2,441,544 |
| 2,000／1,000 | 23.384 | 10,062,480 | 480,472 | 4.596 | 0 | 0.851 | 1,827,008 |
| 20,000／0 | 134.758 | 98,927,880 | 4,364,472 | 116.735 | 0 | 45.717 | 22,331,144 |
| 20,000／10,000 | 159.760 | 102,047,880 | 4,864,472 | 161.093 | 0 | 42.386 | 22,331,144 |

Play準備はClone＋bind＋Startで、編集Sceneの構築とStopを含まない。割り当て量はスレッドの累積割り当て差分、保持量は別の非インライン測定フレーム内で実行用Sceneを生存させた `GC.GetTotalMemory(true)` 差分（1標本・キャッシュやGCの変動を含む概算）。YAML文字数を保持メモリとして扱わない。特に小さいケースの保持量差から、参照を増やすとメモリが減るとは結論しない。

- 定常アクセスは通常のC#メンバーアクセスで、ID検索・反射・再接続を挟まない。10,000参照のケースまで定常GC割り当て0 B。参照ありでは対象の値を読むゲーム処理も加わるため、時間差をID検索の費用と解釈しない。
- 編集側の直接代入／解除は1,000件で0.012／0.009μs毎件、10,000件で0.004／0.002μs毎件。共有参照先の削除は1,000件で1.609ms、10,000件で16.451ms。これはC#の編集データ操作の測定で、Avaloniaの描画・レイアウト時間を含まない。
- 全計測は完走。実画面のD&D・別マシン／GPU・CIは未確認。通常のC#からMissingのnull要素を移動した場合は自動追跡できず、保持パスの明示操作が必要。ローカル変数やInspector外参照の自動無効化も保証しない。

### StuffsのUI作成メニュー（2026-09-23）

「UI → Image／Button／Text」を現在のComponent・Project所有権・親子ツリーへ統合。Transform・UiElementを含む必要な構成を揃え、選択中の親の子として追加する。旧ブランチのSpritePath／Text等の別仕様は導入しない。操作は[README](../README.md)を参照。

- 名前の重複回避・Scene所有権・保存往復、実際のメニューイベントによる作成・ツリー選択・Play保護・アタッチ失敗時の取り消しをチェックに追加。
- ローカルの `./tools/code-quality.ps1 -Check` は通過（提案レベルの解析・警告／エラー0のビルド・Core/Editor Checks）。実画面・実GPU・CIは未確認。

**描画順の共通基盤（`RendererComponent.Order`）とGame表示・Button操作は実装・自動検証済み。** Button実装に先立つOrder基盤に続き、Game表示（実行用Sceneの描画）とButton本体（`core.button`・`IUiButtonHandler`・Game入力・更新境界ディスパッチ）を接続した。詳細は[検証状況](#game表示とbutton操作2026-09-23)を参照する。Textは[別工程](#text-component2026-09-24)で追加した。SpriteRenderer本体・SortingLayer・Zによる奥行き制御、ObjectRef・InputField・サイズ変更・回転Gizmo・単体Player配布は対象外として区別する。実画面・実GPU・CIは各工程の検証記録で区別する。

**指定されたV4前半「Scene Viewのグリッド・パン／ズーム・選択・移動Gizmo」は実装・レビュー修正済み。** 背景だけでは位置や縮尺を把握できず、Inspectorの数値入力だけでは配置しづらいため、画像をマウスで選択・移動できる編集面を作った。2026-09-23に下表の5項目の自動検証と実GPUチェックを通過した。実画面は表示を確認済み。一連の手動操作とCIは未確認として区別し、詳細は[検証状況](#v4前半のscene-view編集操作2026-09-23)を参照する。後続の実装候補はサイズ変更・回転Gizmo。

**座標方針：データ・配置計算はXYZを維持し、今回の編集GizmoはXY移動のみとする。** TransformのVector3／Quaternion、UiLayoutのMatrix4x4を維持する。操作でZを0へ戻さず、保存・再Open・Cloneでも保持する。表示はXYへの正投影とし、Zによる描画順の変更は行わない。

| 順番 | 作業 | 完了条件 |
| --- | --- | --- |
| 1 | **グリッド・原点・座標変換** | 暗い背景に薄いグリッドとX／Y軸を表示。描画・ポインター・選択枠・Gizmoが共通のビュー変換を使い、パン／ズームしても位置がずれない |
| 2 | **パン／ズーム** | 中ボタンドラッグでパン、ホイールでカーソル位置を中心にズーム。有限の倍率範囲を設ける。視点操作ではTransform・UiElement・保存値・未保存状態を変えない |
| 3 | **選択・枠・Pivot** | 表示中の画像をクリックして手前から選択し、Stuffs／Inspectorと双方向に連動。選択対象の変形後の矩形とPivotを描く。空白クリックで選択解除 |
| 4 | **移動Gizmo** | 選択中の有効なUI対象にX／Y矢印と中央ハンドルを表示。Transformのみのグループ親を選んだときもワールド原点にGizmoを出し、その変換は子の配置に受け渡す。XまたはYの軸移動／XY平面内の自由移動でTransform.LocalPositionのX・Yだけを更新し、Zを保持してInspectorへ即時反映。親の回転・拡縮があっても正しく動く |
| 5 | **Fキーで選択対象を表示** | Scene Viewにフォーカスがあるとき、選択対象を余白付きで中央に収める。未選択・0サイズ・無効な配置でも例外や異常倍率にならない |

移動は左ボタンのドラッグで開始し、離した時に確定する。実際に位置が変わった場合だけ未保存化し、Esc・フォーカス喪失・キャプチャ喪失は元の位置へ戻す。Scene差し替え・対象削除・Play開始・コード再読み込み時に旧オブジェクトへの操作を残さない。詳細な責務と座標・中断規則は[設計書](EngineArchitecture.md#v4前半scene-viewの編集操作)を正本とする。

**完成目標：画像を表示 → グリッドを基準にパン／ズーム → 画像クリックで選択 → Gizmoで移動 → Inspectorと表示が一致 → 保存・再Openで位置を再現。Escで中断すると開始位置へ戻り、パン／ズームだけでは未保存にならない。**

今回は単一選択とXY移動まで。Zハンドル・視点の3D回転・透視投影・奥行き判定は含めない。サイズ変更ハンドル・回転Gizmoは次の工程とし、複数選択・グリッドスナップ・汎用Undo／Redo・Button操作・InputField等は含めない。専用RectTransform、Camera Component、新しい親子構造は作らない。

自動チェックでは座標往復・カーソル中心ズーム・重なり順・親変換付きの移動・確定／キャンセル・既存の未保存状態保持・入力の分離・Scene差し替えを確認する。非ゼロZを持つ対象で移動・キャンセル・保存往復・Clone後もZが保持されることを検証する。実Editorではグリッドの視認性、パン／ズーム、Stuffs連動、Gizmoの操作とInspector反映、Esc、F、リサイズを確認し、保存往復まで行う。`./tools/code-quality.ps1 -Check`と実GPUチェックを実行し、結果・操作手順・画面証跡を記録する。未確認のDPI／GPU／CIをPASSと記載しない。

### 前工程：Inspectorから画像を配置・保存する

2026-09-22にユーザー指定した以下の5項目は、2026-09-23に実装・自動検証済み。実画面の目視とCIは未確認として区別する。初回レビューで見つかった不具合は修正し、実際の追加ダイアログ経由の回帰テストを追加した。詳細は[レビュー修正](#ui実装レビューの修正)と[初回検証状況](#ui5項目の検証2026-09-23)を参照。

| 順番 | 作業 | 状態 |
| --- | --- | --- |
| 1 | **InspectorのComponent検索・追加** | 実装・自動検証済み。InspectorのAdd Componentから当該Projectの登録型を検索し、既存のアタッチ処理・factoryで付ける。重複防止・削除・未保存・Play禁止を維持 |
| 2 | **必要な組み合わせの確認** | 実装・自動検証済み。Image不足のTransform／UiElementを通知し、揃うと解除する。自動追加はしない |
| 3 | **画像素材の取り込み・Sprite選択** | 実装・自動検証済み。`Assets/` へ取り込み、Sprite欄で選択・None解除。正式IDで参照し、欠落はID保持で診断する |
| 4 | **編集中Sceneの描画とInspector反映** | 実装・自動検証済み。Scene Viewを編集用Sceneへ接続し、追加・削除・配置・Sprite・色を反映する。Start／Updateは呼ばない |
| 5 | **保存・再Open・Clone** | 実装・自動検証済み。Sprite・値・親子・兄弟順を共通保存経路（`version: 2`）で保持し、Clone先を分離する |

**一連の完成目標：Emptyを作る → Componentを検索して付ける → Spriteを選ぶ → Inspectorで配置・色を変える → 保存 → 開き直して同じ表示になる。** 親を含む例も保存往復とClone・描画一致まで自動検証済み。

次は上記V4前半のScene View編集操作へ進む。Button操作・InputField等は今回の対象外。V3／V4全体の完了とは区別する。

検証は既存Core／Editor Checksへ追加し、`./tools/code-quality.ps1 -Check` がPASS。描画反映の実画面目視とCI実行は未確認。

## 機能追加前のアーキテクチャ改善（完了）

**[アーキテクチャ改善5項目](ArchitectureImprovements.md)は5／5項目完了。2026-09-22にA1〜A5を接続した状態で検証した。**

プロジェクト側のサービス登録、MainWindowの責務分離、非同期コンパイル、プロジェクト単位の型登録、UI非依存のRuntimeを統合済み。各項目の完了条件・実装・検証結果はリンク先で管理する。新機能は今回追加していない。

## 現在の到達点

2026-09-22、診断対策と再発チェックを追加（コミット `d653219`）。namespaceを維持したコード整理、提案レベルを含む一括修正・検査、CI設定、ゲーム用のライフサイクルIDE0051抑制を実装済み。詳細は下記の検証状況を参照。

**LauncherからProjectを作成・再開し、シーンのオブジェクトにC#クラスを付けて値とPriorityを編集し、YAMLで保存・復元できる。**

制作データを編集する基盤に加え、Coreで独立した実行用Sceneを作り、画面なしでStart／Update／DestroyをPriority順に実行できる。EditorのツールバーにあるPlay／Stopで開始・停止でき、実行中の編集・切替は無効化する。Scene Viewには編集中SceneをImage／Text／Sprite／UiLayoutで描く（Start／Updateなし）。描画順は`RendererComponent.Order`の昇順（同値は親→子・兄弟順）で、ヒット判定も同じ並べ替えを手前から使う。GameタブはPlay中の実行用Sceneを描き、Buttonのクリック・キーボード操作を `IUiButtonHandler.OnClick` へ届ける（下記）。単体実行・配布はV6。

## 実装済み

| 領域 | できること | 主な実装 |
| --- | --- | --- |
| Launcher | Project新規作成、既存Projectを開く、最近開いたProject、Editorからの復帰 | [LauncherWindow](../src/PureEngine.Editor/Windows/LauncherWindow.axaml.cs)、[ProjectSession](../src/PureEngine.Editor/Projects/ProjectSession.cs) |
| Project | manifest、複数シーン、起動シーン指定、相対パス、Projectフォルダの移動 | [ProjectFile](../src/PureEngine.Editor/Projects/ProjectFile.cs)、[ProjectDocument](../src/PureEngine.Core/Projects/ProjectDocument.cs) |
| Project Explorer | フォルダツリーとファイル一覧、シーンを開く、作成・改名・削除・更新、名前指定で空のsealedクラスを作るCreate C#。組み込みComponents一覧は表示しない | [MainWindow.ProjectExplorer](../src/PureEngine.Editor/Windows/MainWindow.ProjectExplorer.cs) |
| Editorの配置 | 左がScene View／Game、中央がStuffs、右がInspector、下部がProject／Console。ペインのサイズ変更 | [MainWindow.axaml](../src/PureEngine.Editor/Windows/MainWindow.axaml) |
| シーンとオブジェクト | ID・名前、追加・選択・名前変更・削除。Stuffsは親子のツリー表示、ドラッグ＆ドロップの子付け・前後並べ替え・ルート化、選択中への子追加 | [Scenes](../src/PureEngine.Core/Scenes/Scene.cs)、[StuffsHierarchy](../src/PureEngine.Editor/Editing/StuffsHierarchy.cs)、[MainWindow.Hierarchy](../src/PureEngine.Editor/Windows/MainWindow.Hierarchy.cs) |
| クラスのアタッチ | 普通のC#インスタンスをAttach／GetComponentで扱う。同じ型の重複を拒否 | [SceneObject](../src/PureEngine.Core/Scenes/SceneObject.cs) |
| コンポーネントの取り外し | InspectorカードのRemove、編集用Detach、Priority除去、Disposeの単発実行。他カードの入力と選択を保持し、Play中の取り外しを拒否 | [SceneObject](../src/PureEngine.Core/Scenes/SceneObject.cs)、[MainWindow](../src/PureEngine.Editor/Windows/MainWindow.axaml.cs) |
| ドラッグ＆ドロップ | Projectの自作C#ファイルからStuffsの行、または選択中オブジェクトのInspectorへアタッチ。Stuffs行同士の親子付け・並べ替えはツリー上で行う | [MainWindow](../src/PureEngine.Editor/Windows/MainWindow.axaml.cs)、[MainWindow.Hierarchy](../src/PureEngine.Editor/Windows/MainWindow.Hierarchy.cs) |
| 属性 | Inspector・Start・Update・Destroyの定義、Inspectorメンバーとライフサイクルメソッドの検出 | [ComponentSchema](../src/PureEngine.Core/Components/ComponentSchema.cs) |
| Coreの実行 | 実行用Sceneの複製、開始・明示的な更新・停止、追加・削除予約、例外の報告と後片付け、Priority順の実行 | [SceneRuntime](../src/PureEngine.Core/Scenes/SceneRuntime.cs) |
| EditorのPlay／Stop | ツールバーのPlay／Stop、独立Sceneでの開始・一定間隔の更新・停止、編集中Sceneの分離、実行中の編集・切替の無効化、入力エラー時の開始拒否、失敗表示と後片付け | [MainWindow.Play](../src/PureEngine.Editor/Windows/MainWindow.Play.cs)、[MainWindow.axaml](../src/PureEngine.Editor/Windows/MainWindow.axaml) |
| Inspector | string・int・float・double・bool・enum（Flags含む）・Vector2／3／4・Quaternion・Color・Transform・Sprite・自作クラス・配列・List・Dictionary（stringキー）の表示と編集、数値の無効表示・エラー数、Escで復元、非有限数の拒否、存在するライフサイクルのPriority表示と編集。対応範囲の正本は [EngineArchitecture.md](EngineArchitecture.md) | [MainWindow](../src/PureEngine.Editor/Windows/MainWindow.axaml.cs)、[Inspector](../src/PureEngine.Editor/Windows/MainWindow.Inspector.cs)、[InspectorValueTypes](../src/PureEngine.Core/Values/InspectorValueTypes.cs) |
| UI部品の追加 | InspectorのAdd Componentから当該Projectの登録型を検索し、既存のアタッチ処理・factoryで追加。重複防止・削除・未保存・Play禁止を維持。`Transform`・`UiElement`・`Image`・`Button`・`Text` は組み込み登録 | [ComponentAssets](../src/PureEngine.Editor/Components/ComponentAssets.cs)、[MainWindow.ComponentAdd](../src/PureEngine.Editor/Windows/MainWindow.ComponentAdd.cs) |
| UI組み合わせ診断 | `Image`／`Button`／`Text` に必要な `Transform`／`UiElement` の不足を通知し、揃うと解除する。自動追加はしない | [UiComponentRequirements](../src/PureEngine.Core/Components/UiComponentRequirements.cs)、[MainWindow.UiDiagnostics](../src/PureEngine.Editor/Windows/MainWindow.UiDiagnostics.cs) |
| 画像素材 | `Assets/` への取り込み、隣接登録情報、Project Open・Refreshでの索引再走査、Sprite欄の選択・None解除、欠落IDの保持と診断 | [ProjectAssets](../src/PureEngine.Editor/Assets/ProjectAssets.cs)、[Sprite](../src/PureEngine.Core/Assets/Sprite.cs) |
| 編集Sceneの描画 | Scene Viewを編集用Sceneへ接続し、親子配置を済ませてから`Order`昇順へ並べ替えて追加・削除・配置・Sprite・文字・色・Orderを反映する。ヒット判定も同じ並べ替えで手前から行う。`UiLayout`／`UiImageRenderer`／`UiTextRenderer` を再利用し、Start／Updateは呼ばない | [EditSceneRenderer](../src/PureEngine.Rendering/EditSceneRenderer.cs)、[VulkanViewport](../src/PureEngine.Rendering.Avalonia/VulkanViewport.cs)、[MainWindow.Preview](../src/PureEngine.Editor/Windows/MainWindow.Preview.cs) |
| 描画順の共通基盤 | ImageとTextと将来のSpriteRendererの共通基底として抽象クラス`RendererComponent`を追加し、`[Inspector] public int Order { get; set; }`（既定値0）を持たせる。`Image`・`Text`を派生させ、`Sprite`は素材データのまま維持する。Order昇順で描画し、大きい値を手前にする。負数も許可し、同値は親→子・兄弟順を維持する。配置計算後に並べ替え、親から継承せず各対象の値を使う。同じオブジェクトのImage＋Textは一単位として大きい方のOrderで並べ替え、Image→Textの順に描く。ライフサイクルのPriorityとは独立させる。SpriteRenderer本体・SortingLayer・Zによる奥行き制御は対象外 | [RendererComponent](../src/PureEngine.Core/Components/RendererComponent.cs)、[Image](../src/PureEngine.Core/Components/Image.cs)、[Text](../src/PureEngine.Core/Components/Text.cs)、[SceneViewMath](../src/PureEngine.Core/Scenes/SceneViewMath.cs)、[UiImageRenderer](../src/PureEngine.Rendering/UiImageRenderer.cs)、[UiTextRenderer](../src/PureEngine.Rendering/UiTextRenderer.cs)、[EditSceneRenderer](../src/PureEngine.Rendering/EditSceneRenderer.cs) |
| Game表示 | GameタブをPlaySessionの実行用Sceneへ接続し、親子配置を済ませてから`Order`昇順へ並べ替えて描く。Scene Viewは編集用Sceneのまま維持し、Play中の編集禁止を守る。実行中のTransform・Image変更を次のフレームに反映し、描画からStart／Updateを呼ばない。非表示時は描画・入力を止めて進行は維持する。読み取りと更新はUIスレッドで直列化する | [GameSceneRenderer](../src/PureEngine.Rendering/GameSceneRenderer.cs)、[MainWindow.Game](../src/PureEngine.Editor/Windows/MainWindow.Game.cs)、[MainWindow.Play](../src/PureEngine.Editor/Windows/MainWindow.Play.cs) |
| Button操作 | `Button`（`core.button`・`Interactable`）を普通のComponentとしてTransform＋UiElementの領域で判定し、見た目は同じオブジェクトのImageを使う。通常・ホバー・押下・無効・キーボードフォーカスを重ね表示で区別し、保存済み`Image.Color`を書き換えない。一時状態は保存・Cloneしない。重なり前面のみ・押上一致の単発・外し取消・キャプチャ・Tab／Shift+Tab・Enter／Space（リピート抑止）・無効／0サイズ／不能変換の除外・親無効の非波及・Imageなし可・非Button非遮蔽。`IUiButtonHandler`を実装するButton自身から`Clicked`へ更新境界で通知し（購読なしは無反応・複数購読可）、例外・削除・停止をRuntime規則へ接続する | [Button](../src/PureEngine.Core/Components/Button.cs)、[IUiButtonHandler](../src/PureEngine.Core/Components/IUiButtonHandler.cs)、[UiButtonVisuals](../src/PureEngine.Core/Components/UiButtonVisuals.cs)、[SceneRuntime](../src/PureEngine.Core/Scenes/SceneRuntime.cs)、[GameSceneRenderer](../src/PureEngine.Rendering/GameSceneRenderer.cs)、[MainWindow.Game](../src/PureEngine.Editor/Windows/MainWindow.Game.cs) |
| Text表示 | `Text`（`core.text`・`Content`・`Color`・`FontSize`・`LineSpacing`）を普通のComponentとしてTransform＋UiElementの領域を起点に同梱フォントで描く。左寄せ・上起点で幅折り返し、空文字は描画なし。高さクリップは未実装。保存・Clone・欠落メンバーの既定値読み込み、Scene View／Gameの描画とScene Viewの矩形選択に対応。同じオブジェクトのImage＋Textは一単位として大きい方のOrderで並べ替え、Image→Textの順に描く。寄せ・フォント素材の指定は後続 | [Text](../src/PureEngine.Core/Components/Text.cs)、[UiTextRenderer](../src/PureEngine.Rendering/UiTextRenderer.cs)、[EditSceneRenderer](../src/PureEngine.Rendering/EditSceneRenderer.cs)、[GameSceneRenderer](../src/PureEngine.Rendering/GameSceneRenderer.cs) |
| シーン保存 | YAML version 2、ID・名前・parentId・siblingIndex・typeId・Inspector値・Priorityの保存と復元、固定IDのクラス登録表。`Image.Order`もInspector値として保存・Cloneし、旧データは`Order = 0`として読み込む。`version: 1` は読み込みのみ | [SceneSerializer](../src/PureEngine.Core/Scenes/SceneSerializer.cs)、[ComponentRegistry](../src/PureEngine.Core/Components/ComponentRegistry.cs) |
| Inspectorメンバー改名 | 属性なしで改名・削除可能。新名は初期値、同名の値は維持し、保存時に古いYAML項目を削除。値を引き継ぐ旧名属性は任意。仕様は [EngineArchitecture.md](EngineArchitecture.md) のInspector節 | [SceneSerializer](../src/PureEngine.Core/Scenes/SceneSerializer.cs)、[ComponentSchema](../src/PureEngine.Core/Components/ComponentSchema.cs) |
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

- Start／Update／DestroyはCoreでPriority順に実行できる。EditorのPlay／Stopで開始・停止できる。Game表示とButton操作は実装・自動検証済み（下記）。単体実行・配布は未実装。
- 親子関係・兄弟順・Sprite参照・描画順（`Order`）・Buttonの`Interactable`・Textの内容と色・サイズの保存は実装済み。Stuffsのツリー表示とドラッグ＆ドロップの子付け・前後並べ替え・ルート化、`Scene.SetRootSiblingIndex` によるルート並べ替えも実装・自動検証済み。オブジェクト参照（ObjectRef）・フォント素材・SpriteRenderer本体・SortingLayer・Zによる奥行き制御は未実装。
- Projectの自作C#を自動コンパイル・登録する。独自csproj設定、外部NuGet依存の復元、Play中の実行状態を維持した差し替えは未対応。コンパイルはバックグラウンドで行い、Scene移行と採用はUIスレッドで行う。
- ゲーム用IDE0051抑制は生成csprojのAnalyzer参照で提供する。既存Projectは更新したEditorで再Openする。手動csprojへの参照追加は利用者が行う。CA1822など他の診断の自動抑制や、リポジトリの品質設定一式のゲームへの配布は対象外。
- Inspectorと保存の対応型は [EngineArchitecture.md](EngineArchitecture.md) のInspector節の範囲。自作クラスは単体・配列・リスト要素・辞書値・入れ子で対応する。`Sprite` のコレクション要素の編集UI、配列・リスト要素や辞書値への `Transform`・コレクションの入れ子、string以外の辞書キー、サービス参照は未対応。サービス参照に `[Inspector]` を付けない。
- YAMLのコメント保持・汎用の自動マイグレーションは未実装。Inspectorメンバーの改名は初期値へリセットして読み込み、保存時に旧項目を削除する。値の引き継ぎは任意の `FormerlySerializedAs` に対応。型変更・enum定数の改名を自動移行するものではない。
- ゲーム内UIのInputField等の追加、ゲーム実行ファイル、ゲーム進行のセーブ、通信・Steamは未実装。Scene Viewのドラッグ操作・ハンドルはV4前半の範囲（グリッド・パン／ズーム・単一選択・XY移動Gizmo・F表示）まで実装済み。描画順は`Order`基盤まで、Game表示とButton操作はV5前半の範囲まで、Text表示は内容・色・UiElement配置まで実装済みで、SpriteRenderer本体・SortingLayer・Zによる奥行き制御は未実装。サイズ変更・回転ハンドル、複数選択、スナップ、汎用Undo／Redoは未実装。
- ペイン配置などのEditor設定の永続化は未実装。最近開いたProjectの履歴は保存済み。

## 仕様整理と次の実装順

直近の着手順は冒頭の[次に着手する作業](#次に着手する作業)を優先する。以下は既存工程の進捗と全体の段階を示す。

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

### 6. C#15・Vulkan描画とScene View（V0〜V2実装）

2026-09-22、[Vulkan描画の実装計画](VulkanRenderingPlan.md) を作成した。設計方針の正本は [EngineArchitecture.md](EngineArchitecture.md#ui描画プレビュー)。最初に既存AvaloniaのScene ViewへVulkan描画を表示し、リサイズ・タブ切替・資源解放が実機で成立することを確認する。

| ID | 段階 | 状態 |
| --- | --- | --- |
| V0 | 依存関係・シェーダー・GPU共有経路の選定 | 実装・ローカル確認済み |
| V1 | Scene View埋め込みと単体ウィンドウの表示検証 | 実装・実機確認済み（DPI 1.0） |
| V2 | 2D画像・日本語の文字・クリップ・GPU資源管理 | 実装・ローカル／実機確認済み |
| V3 | 親子・素材参照・UIデータの保存 | Image向けの素材・Inspector・version 2保存／Clone、Text／Buttonを実装。フォント素材・ObjectRef等は後続 |
| V4 | Scene Viewでの配置・Inspector連動 | 編集SceneとInspectorの反映に加え、V4前半（グリッド・パン／ズーム・単一選択・XY移動Gizmo・F表示）を実装・自動検証済み。サイズ変更・回転ハンドル等は未実装 |
| V5 | Game表示・入力・Play／Stop接続 | Game表示とButton操作まで実装・自動検証済み（下記）。単体実行・配布は未着手 |
| V6 | 同じプロジェクトの単体実行・配布確認 | 未着手 |

V1が成立する前にUI本実装へ進まない。最終目標は、カード画像・日本語の得点表示・ボタンを編集保存し、Playと単体実行の両方で操作できること。各段階の完了条件と対象外は上記計画に集約する。

### その後の候補（順番は未決定）

| 候補 | 着手前に決めること |
| --- | --- |
| Parent・参照の追加用途 | UIに必要な範囲はV3で扱う。それを超える用途は必要時に検討する |
| 描画・UIの拡張 | 初期範囲を超えるアニメーション編集、World Space Canvasなど |
| Undo／Redo・Editor設定 | 対象操作、履歴の単位、設定の保存場所 |
| ローカル複数実行・通信 | ホストとクライアント、状態同期、テスト用起動方式 |
| Steam | ロビー・招待・参加、接続検証 |

Steamなど設計書で保留している内容は、ここに載せたことをもって着手しない。描画はV2基盤に加え、Image／Sprite／UiLayoutの検証用Sceneまで接続済み。実Projectの編集・保存・Play接続は後続。

## 検証状況

### Text Component（2026-09-24）

内容・文字色・配置（UiElementの領域）をText Component（`core.text`）として実装した。文字サイズ・行間調整も実装済み。今回は左寄せ・上起点の幅折り返し表示とし、高さクリップ・寄せ切り替えは未実装。Scene Viewでの選択はUiElement矩形が対象で、Gameは描画のみ（Text選択なし）。確定した仕様は[設計書](EngineArchitecture.md#uiコンポーネント)を正本とする。

- Core追加（`UiTextChecks`）：既定値（`Content = "New Text"`・白・24・1.2・`Order = 0`）、保存往復・Clone分離、旧データの既定値読み込み、UI組み合わせ要件（`Transform`＋`UiElement`）、Image／Text共有の描画順（単体・同オブジェクトの大きい方・負数・並べ替え一致）を確認。
- Editor追加（`UiTextEditorChecks`）：編集／Game描画の色・配置反映、空・null内容の非描画、無効なフォントサイズの診断、同オブジェクトのImage→Text順を確認。`UiMenuChecks`のUI作成メニューへText（配置要件・選択・Inspector表示・名前重複回避・保存往復）を追加し、`ComponentSearchChecks`へ`core.text`の登録・検索を追加した。
- landingレビューでInspectorのText要件警告を接続し、Image失敗後のText／フォーカス枠継続と画像tint抑止を統一。警告の表示／更新／解除、失敗後の描画、幅折り返し・行間・高さ非クリップの回帰チェックを追加。欠落メンバーのYAML fixtureはColorの子キーも含むノード単位の削除へ修正した。
- ローカル品質：`./tools/code-quality.ps1 -Check` は終了コード0でPASS。提案レベル診断・警告をエラー扱いにしたビルド・Core／Editorチェックがすべて通過した。
- 実画面・実GPU・CIは未確認として区別する。

### ソースコード内の英語化（PR #6）

- コメント・XMLドキュメント・例外／ログ／UI・診断メッセージを英語化し、対応するテストの期待メッセージを更新した。言語規約の正本は [AGENTS.md](../AGENTS.md#言語)。
- レビュー修正：XMLドキュメントの重複した閉じタグと型名の不正なXML表記、英文の前置詞抜けを修正した。変更対象のC#ファイル内のXMLドキュメントブロックがすべて整形式であることをパース検証した。
- 日本語オブジェクト名の保存往復と描画サンプルのCJK・全角数字／句読点・折り返しは検証用データとして元の実行時文字列を維持した。ソース上はUnicodeエスケープと英語コメントで表現し、英語化による検証範囲の縮小を避けた。
- ローカル品質：`./tools/code-quality.ps1 -Check` は終了コード0でPASS。提案レベル診断・警告をエラー扱いにしたビルド・Core／Editorチェックがすべて通過した。ソース／設定内の日本語・CJK・全角文字の残存チェックと `git diff --check` も通過した。
- CI：修正前のPRチェックは成功を確認済み。レビュー修正後の結果は [PR #6](https://github.com/tekewo000/PureEngine/pull/6) のチェックを参照。
- 実画面・実GPUでの英語ラベルの表示確認は未実施。

### Game表示とButton操作（2026-09-23）

Editorで画像とButtonを配置・保存し、Play中のGameで押すと自作C#が呼ばれ、Consoleにログが出る一連を接続した。今回はGame表示とButton操作までとし、Text・ObjectRef・InputField・サイズ変更・回転Gizmo・単体Player配布は追加しない。この達成だけでV3〜V5全体を完了扱いにしない。確定した仕様は[設計書](EngineArchitecture.md#v5前半game表示とbutton操作)を正本とする。`Button`・`Image`は`PureEngine.Core`に統一し（typeIdは`core.button`／`core.image`のまま）、Editor・テスト側のAvaloniaとの衝突は完全修飾と`using`エイリアスで明示する。

- ローカル品質：`./tools/code-quality.ps1 -Check` は終了コード0でPASS。提案レベル診断・警告をエラー扱いにしたビルドの警告／エラー0、Core／Editorチェックがすべて通過した。
- Core追加（`UiButtonChecks`）：Buttonの保存往復・Clone分離・既定値、UI組み合わせ要件、購読の登録・解除・複数通知・保存／Clone時の購読分離、tintの相違と`Image.Color`不変・解決優先度、重なり順・親変換・押下キャンセル・無効化・Imageなし・遮らない画像のヒット規則、更新境界ディスパッチ（0無反応・単発・実行Sceneのcontext・再通知なし・編集不変・例外停止・削除／停止／無効時のスキップ・Start前捨て）を確認。
- Editor追加（`GameButtonChecks`）：Add Componentの検索・factory追加・重複防止、Inspectorの`Interactable`編集・未保存化・要件警告の表示／解除、保存往復・Clone分離、Game描画の状態別頂点と`Image.Color`不変・描画順・Imageなしのフォーカス枠、Play中の押下／離上一致の単発・外しキャンセル・フォーカス喪失取消・実ポインター経路、重なり前面のみ・親変換追従・無効フォールスルー・Imageなし・非遮蔽、Tab／Shift+TabのOrder移動・無効スキップ・Enter／Space単発・リピート抑止（実キー経路）、削除／停止／例外の終了接続・再Playの状態初期化・20回Play／Stopの単発維持を確認。
- 自作C#の最小例はREADMEのClickCounter（クリック回数をConsoleへ出す）を正本とし、チェック内のGameClickCounterも同じ形で検証する。
- レビュー修正：左押下中の右ボタン操作、マウス／キー同時操作、別キー解放・Tab移動によるキーリピート再発火、無効化→再有効化での古い押下復活、削除後のキャプチャ残存、領域外でのホバー残存、画面外・退化変換のTab候補を回帰チェックで確認。ButtonのClickedへの明示登録へ移行し、別handlerの自動検索・Start待ちは廃止した。常に成功していた2つの検証条件も修正した。Button関連のみの再実行は `dotnet run --project tests/PureEngine.Editor.Checks -- --game-buttons`。
- 実画面確認：未実施。Headlessでの配置→保存→再Open相当→Play→クリック→Stop→再Playは自動検証済みとして区別する。実Editorでの配置→保存→再Open→Play→クリック→Stop→再Play、実行中変更の編集漏れなし・再Play初期化、エラー停止を含む20回Play／Stopの重複なし・資源増加なしの目視は未確認。
- 実GPU：Validation Layerなしの `dotnet run --project tests/PureEngine.Editor.Checks --no-build -- --vulkan` はNVIDIA GeForce RTX 4070、DPI 1.0でPASS。既存のリサイズ・タブ切替・20回再生成（ハンドル数1139→1141で安定）・画像更新を確認した。Gameのクリックから描画までの手動確認やGPU上の20回Play／Stopを検証したものではない。
- GPU検証の制限：`./tools/vulkan-check.ps1` は `ErrorLayerNotPresent`（Validation Layer不足）で失敗。失敗しても終了コード0を返していた検証アプリを修正し、スクリプトも成功メッセージを必須にした。Validation Layer付き検証・別GPU・高DPIは未確認。
- CI：未実行。GitHub Actionsの結果確認は未実施。

### 描画順の共通基盤（Order）（2026-09-23）

レビューで、同じオブジェクトに別のRendererComponent派生型を先に付けると、そのOrderがImageの描画・選択順へ混入する問題をCoreチェックで再現し、実際の描画対象のOrderを読むよう修正した。画像描画の重複を`DrawEntry`へ統合し、配置失敗時に同じ計算を繰り返す処理も削除した。適用範囲は[設計書](EngineArchitecture.md#image-componentから描画への接続)を正本とする。

Button実装に先立ち、Imageと将来のSpriteRendererの共通基底として抽象クラス`RendererComponent`を追加し、`[Inspector] public int Order { get; set; }`（既定値0）を持たせた。`Image`を派生させ、`Sprite`は素材データのまま維持する。`Order`昇順で描画し、大きい値を手前にする。負数も許可し、同値は親→子・兄弟順を維持する。親子の配置計算を済ませてから描画対象を並べ替え、`Order`は親から継承せず各対象の値を使う。描画順とヒット判定で同じ`SceneViewMath.SortForRender`を使い、クリック時は手前から判定する。Inspector編集・保存・読み込み・Cloneに接続し、旧データは`Order = 0`として従来の表示を維持する。ライフサイクルのPriorityとは独立させる。Button・SpriteRenderer本体・SortingLayer・Zによる奥行き制御は今回の対象外とする。namespaceの名前・有無・宣言形式は変更していない。

- ローカル品質：レビュー修正後の`./tools/code-quality.ps1 -Check` は終了コード0でPASS。提案レベル診断の検証、警告をエラー扱いにしたビルド（警告／エラー0）、Core／Editorチェックがすべて通過した。
- Core追加（`SceneViewChecks.RenderOrder`）：同値時の兄弟順維持、大小・負数での前後関係、`SortForRender`の安定並べ替え、配置値不変、親からの非継承（子・兄弟・親の順序）、最前面クリック選択、Priority独立（Start Priority 100とOrder 3の分離、描画部品なしは0）を確認。
- Core追加（`UiComponentChecks.OrderRoundTrip`）：`Image`のInspectorメンバーに`Order`が含まれること、既定値0、負数を含むYAML往復・Clone分離・編集後の漏れなし、旧YAML（`Order:`なし）は`Order = 0`・`membersChanged`・再保存で`Order:`接続を確認。
- Editor追加（`ImageRenderingChecks`）：既定値0、単体`Order`変更で自レイアウト不変を確認。
- Editor追加（`EditPreviewChecks.OrderDraws`）：同値時の兄弟順、大小での描画順入替、親子配置不変（子頂点 `(15, 25)` 維持）と子→親の描画順を確認。
- レビュー回帰チェック：別の派生Componentを先に付けてもImage自身のOrderで描画・選択されること、`Append`が背景を維持して`Build`と同順に描くこと、親の画像欠落時の子配置維持、親配置が不正な場合の診断と子の描画・選択の一致を確認。親子の重なり判定も両方の矩形内の座標で検証する。
- Editor追加（`UiImageEditorChecks`）：`Image.Order`欄の初期値0、正数・負数の編集到達、無効値の非反映とEsc復元を確認。
- Editor追加（`SceneViewEditorChecks.SaveClonePreserveZ`）：親子・Z保持に加え、`Order`（親2・子-4）の保存往復・安定性・Clone保持、復元後の整列（子→親）と最前面クリック（親）を確認。実ファイル経路（Project作成・取込・保存・再Open）でも`Order`（1・-2）とZ・パン／ズーム描画一致を確認。
- Editor追加（`UiEndToEndChecks`）：実Projectの作成・取込・配置で`Order`（3・-1）の保存・再Open・Clone保持と描画一致を確認。
- 実画面確認：未実施。HeadlessでのInspector編集・描画一致・保存往復は自動検証済みとして区別する。
- 実GPU：未実施。CPUのDrawList頂点順・配置一致で検証し、GPU表示の目視は行っていない。
- CI：未実行。GitHub Actionsの結果確認は未実施。

### Stuffsの親子ツリーとドラッグ＆ドロップ（2026-09-23）

Inspector参照D&Dの選択競合を修正。Stuffs行の左クリック選択をreleaseまで遅らせ、ドラッグ開始時はInspector対象を維持する。ドラッグ元はMove（階層操作）とCopy（参照代入）の両方を許可する。`HierarchySelectionChecks`でHeadlessのMouseDown／MouseMove／MouseUpを通した参照代入と選択維持、通常クリックでの選択を確認し、`./tools/code-quality.ps1 -Check`を通過した。今回の修正の実ウィンドウ操作確認は未実施。

Stuffsをフラットな一覧から親子のツリー表示へ変え、行のドラッグ＆ドロップで子付け・前後並べ替え・ルート化ができるようにした。Coreの `Parent`／`Children`／`SetParent`／`SetSiblingIndex` はそのまま使い、ルート同士の並べ替えだけ `Scene.SetRootSiblingIndex` を追加した。Editor側に `HierarchyNode`＋`HierarchyDrop` を置き、D&D実行本体をUI非依存で検証できるようにした。Single選択・Undoなしは維持し、保存形式（version 2）の変更はなし。namespaceは変更していない。

- ローカル品質：`./tools/code-quality.ps1 -Check` は終了コード0でPASS。提案レベル診断・警告をエラー扱いにしたビルドの警告／エラー0、Core／Editorチェック通過。
- Core追加（`ParentChecks`）：ルートの先頭・末尾・中間移動とno-op、範囲外・非ルート・未所属の拒否、子リンク不変を確認。`HierarchyDrop` の子付け・ルート化・兄弟前後、自分・子孫への拒否、同一親内移動の補正、`StuffsHierarchy.Build` のルート・子対応を確認。
- Editor追加（`SceneViewEditorChecks.HierarchyTreeAndDrop`）：ルートだけの表示、子の入れ子、子選択時の祖先展開、ID基準の選択維持、ルート化・前後並べ替えのツリー反映、改名追従を確認。既存のEditorチェックはTreeView経路（`SelectSceneObjectForTest`／`GetSelectedSceneObject`／`SyncHierarchyForTest`）へ移し、全件通過。
- レビュー修正：Component用ハンドラーによるMoveの上書き、展開状態のUIバインディング漏れ、展開済み親の高さによる位置誤判定、DragOverごとのホバー計時リセット、ノードの改名イベント購読蓄積を修正。青線はレイアウトを変えない見出し内の描画とし、選択イベントの重複登録も除去した。
- 回帰検証（`SceneViewEditorChecks.HierarchyRoutedDrag`）：実際のRoutedEvent経路で子付け・前後移動・ペイン外周余白へのルート化・自己／子孫拒否・Play拒否を確認。ComponentのCopy／Attach、実際の行の双方向展開、継続DragOver中の500ms展開、離脱時のタイマー／表示解除、青線の方向と行高不変、破棄ノードのGC回収も確認。改名はモデル値だけでなく表示TextBlockを検査する。修正後の`./tools/code-quality.ps1 -Check`も終了コード0でPASS。
- 実画面確認：Windowsの一時シーンを使い、Computer Useのマウスドラッグで親の中央への子付け、展開中の親の上端／下端への前後移動、余白へのルート化を確認。折りたたみとInspectorでの日本語改名の即時反映も確認。青線の描画プロパティ・500ms継続ホバーはHeadless検証で、ドラッグ途中の青線・待機時間の目視と実画面での保存往復は未確認。
- CI：今回の未コミット差分に対しては未実行。ローカルでGitHub Actionsと同じ`-Check`を通過したことと区別する。

### V4前半のScene View編集操作（2026-09-23）

画像を表示 → グリッドを基準にパン／ズーム → 画像クリックで選択 → Gizmoで移動 → Inspectorと表示が一致 → 保存・再Openで位置を再現する一連を、既存のTransform＋UiElement＋UiLayoutと描画・保存処理の再利用で接続した。データ・計算はXYZを維持し、GizmoはX・Y・XYの移動だけとする。LocalPosition.Zを変更・初期化せず、Zハンドル・3D視点回転・透視投影・Zによる描画順変更は実装しない。専用RectTransform、Camera Component、別の親子構造は作っていない。サイズ変更・回転Gizmo、複数選択、スナップ、汎用Undo／Redo、Button操作、InputFieldは今回含めない。namespaceは変更していない。

- ローカル品質：レビュー修正後の`./tools/code-quality.ps1 -Check` は終了コード0でPASS。提案レベル診断・警告をエラー扱いにしたビルドの警告／エラー0、Core／Editorチェック通過。
- Core追加（`SceneViewChecks`）：座標往復、View行列のZ保持、カーソル中心ズームと上下限クランプ・不正値拒否、グリッド間隔の画面48〜96px保持、重なり順（Zではなく兄弟順）・回転・Pivot中心・空白・Spriteなし・0サイズのヒット判定、親の回転・拡縮付き移動（X軸／Y軸／XYとZ保持、非可逆の拒否）、Gizmoの中央優先ヒットと画面固定サイズ、余白付きF表示と0サイズ・極小表示・不正値のno-opを確認。
- Editor追加（`SceneViewEditorChecks`）：パン／ズームの未保存維持、Gizmoの確定だけ未保存化・クリック無移動の非未保存化・X／Y単軸とZ保持、キャンセル時の開始値復元と開始時未保存状態の維持（既存編集の保存済み化なし）、ドラッグ中のInspector即時反映と未保存化なし、Scene差し替え・Play開始・削除時の操作破棄と旧Sceneへの書き戻しなし、Play中の配置編集禁止、Inspector文字入力からのF分離（実KeyDown経路）と未選択Fのno-op、実Projectの作成・取込・配置・保存・再Openでパン／ズーム付き描画一致と非ゼロZ保持（親子・Clone分離を含む）を確認。
- 実GPU：レビュー修正後の`./tools/vulkan-check.ps1 -ValidationLayerPath tools/.cache/validation` は終了コード0でPASS。Validation Layers＋同期検証エラー0。20回再作成のプロセスハンドル数は1143〜1145（最後1145）。同じ画像IDのキャッシュ更新後の赤→青の差し替えを描画フレーム完了で確認。GPU名はRTX 4070、scale=1。ドライバー版数・DPI間移動・別GPU／別OS・CIは今回未確認。
- 実画面確認：可視ウィンドウでグリッド・画像・選択枠・XYハンドルとInspectorの非ゼロZ表示を確認した。検証中に利用者の操作が入ったため、その編集状態を保持し、一連のドラッグ・Esc・F・保存往復を制御して確認する作業は中断した。新しい画面証跡ファイルは保存していない。Headless Editorでの選択→移動→Inspector→保存→再Openの流れは自動検証済みとして区別する。
- CI：未実行。GitHub Actionsの結果確認は未実施。

レビューで修正した点：

- GPU描画面の透明背景を設定し、空白部分でも入力を受け取れるようにした。CaptureLostのイベント配線と捕捉解除を修正し、Esc・フォーカス喪失・終了後にマウス捕捉を残さない。開始ボタン以外の解放では移動を確定しない。
- 親・UiElement・表示領域等の変更を移動／描画時に検出し、古い配置での移動を中断する。ドラッグ中のFを無効化し、0サイズ・潰れたXY変換では選択枠／Gizmo／F表示を行わない。矢印先端の見えている範囲全体をクリックできるようにした。描画に失敗した画像を選択対象から除いた。
- `SceneViewEditorChecks`に実際のマウス／キー入力を追加し、ヒットテスト、移動、F抑止、Esc、複数ボタン、確定、捕捉／フォーカス喪失、Anchor／親変更、ドラッグ中の保存とZ保持、パン中断を確認。移動のテスト専用実装を本番処理への呼び出しへ統合した。
- Coreチェックに矢印先端と退化した矩形／親軸を追加。Headlessの描画タイミングを揃え、Consoleチェックでは利用できないGPU面を外して遅延したinteropエラーの混入を防いだ。Consoleの既存アサーションと診断設定は維持した。

### Transformのみの親のGizmo修正（2026-09-23）

レビューでは、無効なUiElement付きの親までTransformのみとして扱い、子の選択／ヒット判定と描画位置がずれる経路を修正。正常なグループ親と無効なUI親について、編集描画・Game描画・選択の一致を回帰チェックへ追加した。

StuffsでTransformのみの親（子にTransform＋UiElement＋Image）を選んでもGizmoが出ない不具合を修正した。原因は、配置列挙・描画走査・Gizmo表示／ドラッグ開始のすべてがTransform＋UiElementの組み合わせを前提にし、Transformのみのノードを配置もGizmoも持たない扱いにしていたこと。`SceneViewMath.TryPropagateBareTransform`／`TryGetTransformFrame`を追加し、UiLayoutと同じlocal * parent順で素のTransformを子の配置へ受け渡す。描画走査（編集／Game）も同じ規則に揃え、正当なグループ親は描画診断を出さない。Scene ViewはTransformのみの選択にワールド原点のPivotとGizmoを出し（選択矩形なし）、XY移動・Z保持・確定／キャンセルの規則はUI対象と同じ。潰れたUI配置のGizmo抑止は維持する。

- ローカル品質：`./tools/code-quality.ps1 -Check` は終了コード0でPASS（警告／エラー0、Core／Editorチェック通過）。
- Core追加（`SceneViewChecks.BareTransformParent`）：子の配置が素の親へ追従すること、親フレームのワールド位置、Transformなし・Scene外の拒否、入れ子の累積を確認。
- Editor追加（`SceneViewEditorChecks.BareParentGizmo`）：Stuffs選択したTransformのみの親がGizmo枠（矩形なし・Pivotあり）を出すこと、Gizmoドラッグの確定でZ保持・未保存化・子配置の追従、ドラッグ中の回転編集でキャンセルし開始値へ戻ることを確認。
- 実画面・実GPU・CIは未確認として区別する。

### UI実装レビューの修正

初回レビューで再現した5件を修正した。以前の自動チェック通過だけでは追加ダイアログの操作経路を検証できていなかったため、回帰チェックを補った。

| 問題 | 修正・確認 |
| --- | --- |
| Add Componentの選択変更で再帰してクラッシュ | 候補一覧の再構築とAddボタンの有効状態更新を分離。実ダイアログを開き、非先頭行の選択・検索・追加・重複防止・結果なし・CloseをHeadless Editorで確認 |
| Empty／最後のComponent削除後に追加ボタンが消える | 選択中なら追加入口を表示。祖先パネルを含むIsEffectivelyVisibleと、最後のカードのRemove後に再追加できることを確認 |
| 削除予約後の親変更でSceneから消えた対象がUpdateされる | Scene所属とRuntime予約／終了状態を変更前に検証し、Runtimeは予約集合だけを単体除去。子の救出・予約親への新規追加・兄弟並べ替え・Destroy／Stop後の変更を拒否し、Destroy／Dispose一度だけを確認 |
| AssetsのjunctionからProject外へ読書きする | 共通パス検証を取込・走査・読込へ適用。Assets自体のjunction、索引作成後のリンク差し替え、取込途中失敗のロールバックを確認。外部フォルダへ書き込まない |
| 同じ画像IDをRefreshしても旧画像が残る | 次の直列フレームでアトラスを再構築しRevisionを更新。CPU画素の変更に加え、実GPUの完了フレームをまたいだ赤→青の差し替えを確認 |

- `./tools/code-quality.ps1 -Check`：終了コード0。提案レベルの解析、警告／エラー0のビルド、Core／EditorチェックPASS。
- `./tools/vulkan-check.ps1 -ValidationLayerPath tools/.cache/validation`：終了コード0。Validation Layers＋同期検証エラー0。20回再作成のプロセスハンドル数は1135〜1137、最後1137。同じ画像IDのキャッシュ更新後にも描画フレーム完了を確認。
- 実GPUテストは従来と同じRTX 4070／Windows構成。画素比較はCPUアトラスで行い、GPUの画面読み戻し比較とは区別する。
- 新しい回帰テストはHierarchyLifetimeChecks、UiImageEditorChecks、ProjectAssetChecks、EditPreviewChecks、VulkanCheckAppへ追加。既存のDispose検査・例外系テスト・namespace・診断設定は維持。
- CIと、一連の制作操作を人が実ウィンドウで行う目視確認は未実施。今回の検証は自動チェックと実GPUテストとして記録する。

### UI5項目の検証（2026-09-23）

Emptyを作る → Componentを検索して付ける → Spriteを選ぶ → Inspectorで配置・色を変える → 保存 → 開き直して同じ表示になる一連を、共通保存経路（`version: 2`）と編集Scene描画へ接続した。別のRectTransform、専用基底クラス、第二の保存経路は作っていない。namespaceは変更していない。

- ローカル品質：`./tools/code-quality.ps1 -Check` は終了コード0でPASS。提案レベル診断・警告をエラー扱いにしたビルドの警告／エラー0、Core／Editorチェック通過。
- Core追加（`UiComponentChecks`）：SpriteのYAML往復・Clone分離・拒否、UI組み合わせの不足と解除、親子・兄弟順の保存往復・Clone分離・子孫削除、 sibling並べ替え、不正構造の拒否を確認。
- Editor追加：`ComponentSearchChecks`（組み込み登録・検索・factory追加・重複防止）、`ProjectAssetChecks`（取り込み・索引・重複／欠落／破損の診断・バイト列読み込み）、`EditPreviewChecks`（追加・削除・配置・Sprite・色の反映、不足診断、Start／Update不呼び出し）、`UiImageEditorChecks`（Add入口・不足警告の表示／解除・Sprite選択／None／欠落・Play中禁止）、`UiEndToEndChecks`（実Projectの作成・取り込み・追加・配置・保存・再Open・Cloneで表示一致）を確認。
- 実画面確認：未実施。Headless EditorでのInspector・Scene View相当の描画一致は自動検証済みだが、可視ウィンドウでの操作・GPU表示の目視は今回行っていない。
- CI：未実行。GitHub Actionsの結果確認は未実施。

### Image・Sprite・UiLayoutの描画接続（2026-09-22）

既存Image Componentを変更せず、RenderingへCore参照とUiImageRendererを追加した。Spriteの切り出しを個別のアトラス領域へ登録し、UiLayoutの結果とImage.Colorで描画する。Scene View／試作Playerは、普通のComponentを付けた検証用Sceneを表示する。実Projectの編集Sceneや保存シーンの描画ではない。

- ローカル品質：`./tools/code-quality.ps1 -Check`は終了コード0、警告・エラー0でPASS。Componentからの頂点生成、切り出しUV、Color／Alpha、null／0Scale、素材欠落／不正領域、リサイズ後のアトラス再利用を検査した。
- 実GPU：`./tools/vulkan-check.ps1 -ValidationLayerPath tools/.cache/validation`は終了コード0でPASS。既存の20回再作成・連続リサイズ・0寸法・タブ切替・最小化／復元・終了処理を新サンプルで再確認した。Validation Layers 1.4.341.1＋同期検証でエラー0件。
- 環境：Windows 11／RTX 4070／ドライバー616.92／Avalonia 12.1.2 ANGLE、DPI 1.0。20回再作成時のハンドル数は1172〜1177で推移（最後1175）、退役後のrenderer数は0。
- 目視：全体と部分切り出し、色と半透明の重なり、親の回転と子の配置を確認。単体ウィンドウの拡大／復元で横Stretchと右下Anchorの追従も確認。[通常表示](evidence/image-component-player.png)、[拡大後](evidence/image-component-resized.png)、[Editor内の表示](evidence/image-component-editor.png)。

CI・別GPU・高DPI・性能測定は今回未実施。素材の正式なID解決、SpriteのInspector値変換、Imageの組み込み登録、実ProjectのScene描画と保存は未実装。Namespaceは既存宣言を維持し、Vulkan側のImage型はエイリアスで区別した。

### Spriteの素材データ（2026-09-22）

Core/AssetsのSpriteへ元画像IDと省略可能な切り出し矩形を追加。変更不可のデータとし、画像全体の指定とデコード後の寸法による範囲検証を扱う。仕様は[設計書](EngineArchitecture.md#spriteの素材データ)、使用例は[README](../README.md#spriteの素材データ)を参照。

Core.Checksへ、画像全体・部分領域・端の1ピクセル・解決時の不変性・空ID・負値／0・画像外・整数オーバーフローを回避する境界検証を追加。`./tools/code-quality.ps1 -Check`は終了コード0でPASS（提案レベル診断、警告／エラー0のビルド、Core／Editorチェック）。Imageへの組み込み・素材のID解決・Inspector／保存・描画は未接続で、CI／GPU実画面確認は今回未実施。

### UiLayoutの単体配置計算（2026-09-22）

`UiLayout.Calculate(parentSize, parentWorld, transform, uiElement)`を実装。既存Transform.LocalMatrixを使い、実際のサイズと左上基準の配置行列を返す。Componentの値は変更しない。数値検証の仕様は[設計書](EngineArchitecture.md#配置計算の置き場所と入出力)を参照。

Core.Checksへ、中央／右下固定、横／全面Stretch、Pivot中心の回転と拡縮、親のUiLayout結果を使う子の追従、Z位置保持、入力不変、範囲外Anchor／Pivot、0サイズ／0Scale、不正値とオーバーフロー拒否のチェックを追加。`./tools/code-quality.ps1 -Check`は終了コード0でPASS（提案レベル診断・警告をエラー扱いにしたビルドの警告／エラー0、Core／Editorチェック通過）。Scene走査・登録・描画／入力接続・CI／実画面確認はこの変更の対象外。

### V3のUI構成の更新（2026-09-22）

会話で合意したTransform＋UiElementの構成へ[設計書](EngineArchitecture.md#uiコンポーネント)を更新した。位置・回転・拡縮は既存Transform、SizeDelta・AnchorMin／AnchorMax・PivotはUiElementが持つ。計算はCoreの`Components/UiLayout.cs`へ置き、Componentではない共通の計算用クラスとする。以前の専用RectTransform・必須UiCanvasの案は取り下げた。

設計更新時点では作業ツリーにUiElementのデータ定義と作成途中のUiLayout.csがあった（現在の実装・検証状況は上の「UiLayoutの単体配置計算」を参照）。UiLayout計算の完成・検証済みや、組み込み登録・依存検証・描画／入力接続の実装済みとは扱わない。次は配置計算→中央／右下固定・Stretch・Pivot・親子追従のGPU不要チェック→Imageでの表示確認。その後、保存・参照・素材・Text／Buttonへ進む。Z軸／X/Y軸回転、非UI親の扱い、画面拡縮や非表示等の詳細は未確定と明記した。

今回の変更はMarkdownのみ。既存のC#作業ファイルは変更せず、code-quality.ps1 -Checkは文書のみの変更のため未実行。

### V3の設計（2026-09-22）

[設計案の正本](EngineArchitecture.md#v3親子素材参照ui保存の設計案)を追加。既存Parent／Transform／SceneSerializer／SceneRuntime／素材パス検証を調べ、親子保存の不足を確認した。親削除時に子孫も削除する方針はユーザー確認済み。参照値型、素材サイドカー、UI配置、ボタンの型付き契約、version 2への移行とV3-a〜eの実装単位は提案として記載した。UI構成と直近の着手順は上記の更新で見直している。

この変更は文書のみ。V3のコード実装・自動チェック・GPU確認は行っておらず、code-quality.ps1 -CheckはAGENTS.mdに従い未実行。V0〜V2の過去の検証記録とは区別する。

### ゲーム用C#15対応（2026-09-22）

組み込みRoslynを4.12.0から5.9.0へ更新し、生成ゲーム用csprojのLangVersionを13.0から15.0へ変更。`./tools/code-quality.ps1 -Check` がPASS（提案レベルの解析・警告をエラー扱いにしたDebugビルド警告・エラー0件・Core/Editorチェック全項目、終了コード0）。安定版NuGet（5.9.0）にLanguageVersion.CSharp15がまだ無いため、Editor内コンパイルはPreview設定でC#15相当として扱う。生成csprojのLangVersion 15.0は.NET 11 SDKでのビルドを確認済み。Zed実画面での補完確認とCI実行結果の確認は未実施。

### Vulkan V0〜V2の検証（2026-09-22）

実装は今回の作業ツリーにあり、コミットは未作成。設計・依存・資源の所有権は[描画設計](EngineArchitecture.md#v0v2の描画経路と資源所有)、操作は[README](../README.md#vulkan描画の確認windows-x64)を正本とする。

- 実機：Windows 11 10.0.26200 x64、NVIDIA GeForce RTX 4070、ドライバー616.92（32.0.16.1692）、Vulkan loader 1.4.341／GPU API 1.4.351。Avalonia 12.1.2のANGLE/D3D11、表示倍率1.0。
- ローカル品質：`./tools/code-quality.ps1 -Check`は終了コード0（警告・エラー0）でPASS。[最終チェック記録](evidence/vulkan-v2-checks.txt)。提案レベル診断・警告をエラー扱いにしたビルド・既存Core／Editorチェックに、変換・透明順・クリップ・日本語改行／折り返し・欠落文字・キャッシュ再利用・不正値・頂点上限のCPUチェックを追加。
- 実GPU：`tools/vulkan-check.ps1`の経路で、Scene View実ウィンドウの連続サイズ変更、サイズ0、最小化／復元、Scene View／Game切替、20回の取り外し／再作成、Inspectorヒットテスト、終了時解放をPASS。Validation Layers 1.4.341.1＋同期検証でエラー0件。
- 20回の再作成のプロセスハンドル数：1159, 1161, 1159, 1159, 1160, 1160, 1160, 1160, 1160, 1160, 1162, 1159, 1159, 1161, 1161, 1161, 1163, 1159, 1159, 1159。各退役後のrenderer数は0、表示中は1。プロセス全体のハンドル数をGPUメモリ量そのものとは扱わない。
- 目視：Editor内とEditor非依存の単体ウィンドウで、同じ画像・日本語・回転・半透明順・クリップを表示。通常のAvalonia BorderをGPU画像の上へ重ねられることを確認。[Scene View画面証跡](evidence/vulkan-v2-editor.png)、[単体ウィンドウ画面証跡](evidence/vulkan-v2-player.png)。
- 初回の純Vulkan→Avalonia Vulkan経路では、Avalonia側swapchainの同期／セマフォ再利用診断を検出したため採用しなかった。最終経路はVulkan描画→共有D3D11→既存ANGLE。CPU読み戻しによる代替ではない。
- 再作成検証で見つけたCOMとDLLの寿命不一致、D3D11テクスチャの遅延破棄、デバイス再生成によるハンドル増加を修正／回避。デバイスはアプリ単位、画像・バッファ等はペイン単位とし、共有テクスチャの解放後にFlushする。

CIは未実行。別GPU・別OS・物理モニターのDPI変更／高DPI間移動、実際のdevice lost、1280×720の100画像＋20テキスト性能測定（CPU／GPU時間・割り当て量）は未検証。今回の結果から60fps性能を保証しない。V3以降のシーンデータ・UI編集・ゲーム入力・ゲーム配布も未実装。

### Inspectorメンバー改名（2026-09-22）

`./tools/code-quality.ps1 -Check` がPASS。提案レベルの解析、警告をエラー扱いにしたDebugビルド（警告・エラー0件）、Core／Editorチェックをローカルで確認。

- Core：旧名付きのフィールド・プロパティ・継承メンバーを復元し、複数の旧名、null、新名での再保存・再読込・Clone・実行中ではないSceneの移行を確認。空白旧名、別メンバーとの名前衝突、保存データ内の新旧名や複数旧名の競合を拒否することを確認。
- Editor Headless：自作C#のフィールドを旧名属性付きプロパティへ改名して保存し、自動コンパイル後も未保存値・ID・Priority・選択・未保存状態を保持し、新しいInspector名を表示することを確認。旧シーンの読み込み、Play／Stop、新名で保存後のProject再Openを確認。
- 属性なしの改名・削除：scalar・ベクトルの古い保存名を読み飛ばして新メンバーを初期化し、他の値を保持することを確認。Editorでは保存済みSceneのC#を改名・削除し、自動反映後の未保存表示、ID・Priority・選択の保持、Playを確認。
- YAML整理：改名後のSaveで実際のファイルから旧項目が消えること、古いProject／SceneのOpenでも整理対象を未保存にすること、保存・再Openで新名と初期値だけが残ることを確認。
- 同名メンバーの互換性のない型変更と旧名属性の衝突では旧インスタンスと値を保持することを確認。変更後に `./tools/code-quality.ps1 -Check` を再実行し全項目PASS。CIと実画面での操作確認は今回未実施。

### B1コンポーネント削除の統合（2026-09-22）

B1のコンポーネント削除差分を現行mainのプロジェクト単位Registry・Inspectorへ統合。Coreでは同一参照のみのDetach、繰り返しの無操作、Priority除去、null／実行用Sceneの拒否を確認。Editor HeadlessではPlay中の拒否、削除時の単発DisposeとDestroyなし、兄弟カードの入力途中の値・エラー・選択保持、YAMLからの除去、再アタッチ、解放失敗時の報告、最後のカード削除後の空表示を確認。

ローカルの `./tools/code-quality.ps1 -Check` がPASS。提案レベルの解析、警告をエラー扱いにしたDebugビルド（警告・エラー0件）、Core／Editorチェックを確認。GitHub Actionsと実画面での右クリック操作はこの記録時点では未確認。

### Inspector拡張（2026-09-22）

enumレビューで見つかった3件（C#再読み込み時の型判定、複合Flagsの表示同期による値消失、ulong最上位ビットのOverflow）を修正後、`./tools/code-quality.ps1 -Check` がPASS（提案レベルの解析・警告をエラー扱いにしたDebugビルド・Core/Editorチェック、警告・エラー0件）。対応型と再読み込み互換性の正本は [EngineArchitecture.md](EngineArchitecture.md) のInspector節とYAML節。

```powershell
dotnet run --project tests/PureEngine.Core.Checks -c Release
dotnet run --project tests/PureEngine.Editor.Checks -c Release
```

- 追加分（Core・InspectorValueChecks）：double・Vector2／3／4・Quaternion・Transform・enum（Flags含む）・配列・List・Dictionary（stringキー）・NullableのYAML往復とClone分離（編集後の元変更が複製へ漏れない）、fr-FRでの不変書式、null・空の保持、非有限数・未知／欠落／余分キー・型違い・未対応型（独自クラス・入れ子・intキー）の拒否を確認。旧形式（scalarのみ）の読み込み互換を既存チェックで再確認。
- 追加分（Editor・InspectorValueEditorChecks）：拡張値のUnsupported表示なし、ベクトル・double・enum（ドロップダウン・Flags・Nullable）・リスト要素・辞書値・Transform入れ子（Create／Null）の編集到達と未保存化、数値の無効表示・保存拒否・Esc復元、リスト・辞書のAdd／Remove、Transformコンポーネント（`core.transform`）のLocal編集をHeadlessで確認。
- enum回帰（Editor・UserCodeChecks）：実際に別アセンブリへ再コンパイルし、enum・Nullable・配列・List・辞書の値保持と定数追加を確認。enum型名・基底型・定数名／値・Flags属性・メンバー型の非互換変更では移行を拒否し、旧Sceneの保存内容が変わらないことを確認。
- Flags回帰（Editor Headless）：部分的な複合値の初期表示で値を変更しないこと、ReadWriteからWriteだけを外すとReadを保持すること、複合値の選択・None、ulong最上位ビットと符号付き負値の編集、Nullable・List・辞書の共通編集経路を確認。
- 既存分：前回までの全項目を再確認。実画面の見た目、ネイティブファイルダイアログ、Project Explorerの全操作、タイマーの実測間隔は自動チェックの対象外。

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

### Buttonのイベント接続への置換（2026-09-23）

- Button自身に `IUiButtonHandler` と `Clicked` を実装し、同一オブジェクトのhandler検索と個数検証を削除した。接続仕様は[設計書](EngineArchitecture.md#v5前半game表示とbutton操作)、移行・登録例は[README](../README.md#game表示とbutton操作)を参照する。
- Coreチェックを購読・解除・複数購読・Clone／保存分離・再Play・例外／削除／停止の回帰確認へ更新。Editorチェックは実行用Buttonへ明示登録して、既存の入力経路を確認する。
- ローカルの `./tools/code-quality.ps1 -Check`（提案レベル解析・警告をエラー扱いにしたビルド・Core／Editorチェック）を通常の出力先で通過。実画面・実GPU・CIは未確認。

### 自作クラスのInspector対応（2026-09-23）

- `public AClass Foo { get; set; }` のような自作クラスをInspector値として扱う。条件は参照型のclass（`string`・配列・`List`・`Dictionary`・`Nullable`・enum・`Transform`・`Sprite`を除く）、抽象・ジェネリック・struct・`object`自体を除き、publicな引数なしコンストラクタを持ち、すべての `[Inspector]` メンバーが対応型であること。再帰（自分を直接・間接に含む）は未対応。宣言型と実行時型の一致を要求し、派生型の代入は保存時に拒否する。
- 単体・`T[]`・`List<T>`・`Dictionary<string, TValue>`・入れ子の自作クラスで同じ変換を使う。YAMLではメンバー名のマッピング、nullは `null`。欠けた項目はクラスの初期値を維持し、未知の項目は読み飛ばす（ベクトル・Transform・Sprite内部は従来どおり厳格）。`Clone` では深く複製する。対応型の正本は[設計書](EngineArchitecture.md)のInspector節とYAML節。
- Editorは入れ子カード（Null表示＋Create／Set Null＋折りたたみ＋`親.子`のAutomation名）で編集する。単体・配列／リスト要素・辞書値に対応し、無効表示・保存拒否・Esc復元・未保存化は既存の仕組みに合わせる。
- 追加分（Core・InspectorValueChecks）：自作クラスの単体・二重入れ子・配列・リスト・辞書のYAML往復とClone分離、null・初期値維持・未知項目の読み飛ばし、再帰・抽象・ジェネリック・struct・引数なしコンストラクタなし・派生型混入の拒否を確認。
- 追加分（Editor・InspectorValueEditorChecks）：入れ子エディタのUnsupported表示なし、Create→入れ子編集→Set Null、二重入れ子・折りたたみ、リスト・辞書のAdd／Removeと値編集、無効表示・Esc復元・未保存化をHeadlessで確認。
- PR修正：自作クラスの再コンパイル後の型互換性判定と、入れ子の `FormerlySerializedAs` の名前解決を追加。単体・配列・List・Dictionaryの値保持、入れ子の旧名復元、新旧名の重複拒否、非互換な内部メンバー型／クラス名変更時の元Scene保持を回帰チェックで確認した。仕様は[設計書](EngineArchitecture.md)のInspector節を参照。
- ローカルの `./tools/code-quality.ps1 -Check`（提案レベル解析・警告をエラー扱いにしたビルド・Core／Editorチェック全件）を通過。以前中断した `ConsoleChecks.CloseReopen` も今回の実行では通過。実画面・実GPUは未確認。CIはPRのChecksで別途確認する。
