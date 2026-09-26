# PureEngine アーキテクチャ

この文書は、エンジンの設計方針と会話で合意した仕様をまとめる。
「決定した仕様」と「実装済み」は区別する。起動方法と利用手順は [README.md](../README.md) を参照。

## 目的と責務

UI中心のカードゲーム・ボードゲーム・政治や経営の対戦ゲームを、普通のC#で作りやすくする2Dゲームエンジンを目指す。
「Pure C#」は専用基底クラスに縛られないことを指し、描画やSteam連携のライブラリまですべてC#で自作するという意味ではない。

- **Core**：シーン、オブジェクト、アタッチしたクラスとその実行を扱う。AvaloniaやSteamには依存させない。
- **Runtime**：GameSession・PlaySessionによるサービス生成と実行接続。CoreとMicrosoft.Extensions.DependencyInjectionに依存し、Editor・Avaloniaには依存しない。
- **Editor**：Coreのデータを編集する。制作画面には.NET 11とAvaloniaを使う。
- **Analyzers**：ゲームの外部エディター向け診断を調整する。Roslynに依存する独立DLLをEditorに同梱する。Core・Runtimeからは参照しない。
- **ゲーム実行部分（設計方針）**：C#15製のVulkan描画・入力と既存Runtimeを接続し、EditorのGameと単体実行で描画処理を共有する。固定描画サンプルはV2まで実装。ゲームデータ・入力・Runtimeとの接続は後続。
- **ゲーム側のコード**：配札・投票・勝敗判定などのルールを、描画やSteamから独立したC#で記述する。

独自のカード表示や投票結果なども、標準部品と同じ仕組みで追加できることを目指す。
最初はC#で部品を追加して再ビルドする形とし、必要に応じて拡張する。

## オブジェクト

`SceneObject` の基本構成は次の範囲とする。

| 要素 | 用途 |
| --- | --- |
| ID | オブジェクトを識別・参照する |
| Name | Editorで扱う表示名 |
| Parent | 親オブジェクトとの関係 |
| アタッチしたクラス | データや処理を持つ普通のC#クラスのインスタンス |

位置・サイズなどのUI情報を基本構成に追加するかは、今は決めない。
オブジェクトや素材の参照はID方式とし、名前やファイルパスだけには依存させない。
現在のオブジェクトIDは `Guid`。Parent／Children／SetParentと循環拒否は実装済み。保存・兄弟順・子孫削除を含む拡張は[V3設計案](#v3親子素材参照ui保存の設計案)を参照。

## クラスのアタッチ

- 専用基底クラスの継承や、インターフェースの実装を要求しない。
- `Attach(object)` でインスタンスを預け、`GetComponent<T>()` で取り出す方針。
- Editorではクラスをドラッグ＆ドロップしてアタッチできる形を目指す。
- 最初はプロジェクト内でコンパイル済みのクラスを一覧からドラッグする。外部の `.cs` ファイルを取り込むコンパイル機構は後続の範囲。
- 属性のないデータだけのクラスもアタッチできる。アタッチすることと毎フレーム実行することは別。
- 編集用Sceneの `Detach(object)` は同一インスタンスだけを外し、そのアタッチのPriorityも削除する。未アタッチはfalse、nullと実行用Sceneからの呼び出しは拒否する。Disposeは呼び出し側の責務とし、EditorのRemove操作は取り外し後に一度だけ解放する。編集時のDestroyは呼ばない。
- Removeは対象カードだけを取り除き、他のコンポーネントの入力途中の値・入力エラーとオブジェクト選択を保持する。対象の入力エラーだけを解除し、コンポーネント数・空状態・未保存表示を更新する。解放失敗は報告し、削除済みインスタンスを再度解放しない。

クラスの種類を識別するIDと、アタッチされたインスタンスを識別するIDを分ける案がある。
その登録方法、IDの生成・保持方法、同じ型を複数付けた場合の `GetComponent<T>()` の挙動は未決定。

## Attribute

属性の名前空間は `PureEngine.Core`。属性クラス自体は中身を持たないマーカーとする。

| 属性 | 対象 | 意味 |
| --- | --- | --- |
| `[Inspector]` | フィールド・プロパティ | Inspectorの表示・編集と、制作データの保存対象 |
| `[FormerlySerializedAs("旧名")]` | フィールド・プロパティ | Inspectorメンバー改名時の旧保存名。複数指定可 |
| `[Start]` | メソッド | 開始時に1回実行 |
| `[Update]` | メソッド | 毎フレーム実行 |
| `[Destroy]` | メソッド | 削除時に実行 |
| `[DataAsset("Menu/Path")]` | クラス | 作成メニューに出すデータアセット。継承なしの普通のクラスに付ける |

以前の会話で使った `[End]` は、現在の方針では `[Destroy]` に置き換わっている。
Destroyは削除時の処理であり、Stop時も実行用Sceneの破棄に伴って呼ぶ。編集用Sceneは破棄しない。

ライフサイクルとInspectorの4属性の定義は `AllowMultiple = false`、`Inherited = true`。
同じ属性を同じメンバーに重複して付けることはできない。
`[DataAsset]` だけは `Inherited = false` で、付けたクラス自体だけが対象になる。引数のメニューパスは省略できる。
継承・オーバーライドされたメソッドは同じライフサイクルを二重に呼ばないよう列挙する。

### Inspectorと保存

- `[Inspector]` の付いたフィールド・プロパティだけを表示・編集・シーン保存する。
- 属性のないメンバーはInspectorにも制作データの保存対象にも含めない。
- 別の `[Serialize]` 属性は設けない。
- 実行中に変化した値を、制作データへ自動で書き戻さない。
- 非publicメンバー、readonly、読み取り専用プロパティは対象外。対応する値の型は下記の範囲とする。
- Inspectorの表示順は基底→派生の順とする。同じクラス内では従来の `MetadataToken` 順を維持する（フィールドがプロパティより先で、両者を混ぜたソース宣言順ではない）。`Image`・`Text` はどちらも共通基底の `Order` が先頭になる。
- 参照欄は名前だけの1行選択＋右端の×（Clear）とし、ID・フルパスはツールチップへ移す。2行目の補足はMissing・旧インライン値の注意だけに使う。コレクション見出しは枠なしアイコンのAdd・Set Null・一括Clearにし、要素の削除は行の×で行う。

対応する値の型（Coreの `InspectorValueTypes` で検証・変換し、Editorは同じ範囲を表示する）：

- scalar：string・int・float・double・bool、対応する `Nullable<T>`（空欄＝null）
- enum：通常のenumと `[Flags]` enum、対応する `Nullable<T>`。通常はドロップダウン、`[Flags]` はチェックボックス群とNoneクリアで編集する
- `[Flags]` は複合値・符号付きの負値・`ulong` の最上位ビットにも対応する。チェック状態の同期は表示のみを更新し、ユーザー操作として値へ書き戻さない。
- ベクトル：`Vector2`・`Vector3`・`Vector4`・`Quaternion`（各成分は有限のfloat、対応する `Nullable<T>` を含む）
- 色：`Color`（RGBAの有限float、単体の `Nullable<T>` を含む。単体とNullableは色見本とピッカー、コレクション要素は数値で編集する）。保存値は範囲外でもクランプせず、ピッカー入力は0〜1に制限する。新規要素とCreateは白。色見本を選ぶとSpectrum／Palette／Sliders（hex・alpha付き）のピッカーが開く。
- `Transform`：null可の参照型。`LocalPosition`・`LocalRotation`・`LocalScale` を入れ子で編集する
- `Sprite`：null可の参照型。画像IDと切り出し矩形を持ち、Inspectorでは選択・None解除で編集する。2行目の補足はMissing・切り出し時のみ表示する
- 配列・リスト：ゼロ下限の `T[]`・多次元配列 `T[,]` 等・`List<T>`。対応値型、参照、コンテナを任意に入れ子にでき、nullと空を区別する
- 辞書：`Dictionary<string, TValue>`（値は他の対応型と同じ範囲、キーはstringのみ、null可）。見出しは枠なしアイコンのAdd・Set Null・一括Clear
- 自作struct：`[Inspector]` メンバーを持つ値として扱い、Nullableにも対応する。深い入れ子・配列要素・辞書値の編集はすべての親へ書き戻す。struct自体のComponentアタッチは対象外
- 自作クラス：既存互換としてpublicな引数なしコンストラクタを持つclassで、すべての `[Inspector]` メンバーが対応型であるもの。参照型のためnull可。抽象クラス・ジェネリック・`object` 自体・再帰（自分を直接・間接に含む）は対象外。宣言型と実行時型の一致を要求し、派生型の代入は保存時に拒否する。新しい埋め込み指定は追加せず、共有設定はDataAssetを使う

`Transform` 自体も `[Inspector]` 付きの組み込みコンポーネント（typeId `core.transform`）として保存・編集する。`UiElement`（`core.ui-element`）・`Image`（`core.image`）・`Button`（`core.button`）・`Text`（`core.text`）も同じ組み込み登録で検索・追加・保存する。Component／DataAsset参照の意味は入れ子の深さによって変えない。非string辞書キー・非ゼロ下限配列・任意ポリモーフィズムは対象外。詳細なYAML形式は下記のYAML節を参照。

Inspectorの複合値は、元の文書所有者と値の読書きを保持する共通バインディングで編集する。structは毎回現在の親を読み、boxed copyをすべての親へ書き戻すため、兄弟欄の編集を上書きしない。コレクションは32要素ずつ表示し、コレクション内の子コレクションは初期折りたたみで展開時に生成する。入れ子の子コレクションやオブジェクトカードは親行の中列に収めず全幅で積み、子自身の見出しに `[n]` と行削除×を統合して1行にする（例: `[0] ▼ 2 items ＋ Null Clear ×`）。多次元配列は座標でセルを表示し、各次元の長さを入力してResizeする。重なる座標の値とMissing情報を保持し、外れた座標を除去する。Inspectorからの配列拡大は誤入力による巨大確保を避けるため1,048,576要素を操作上限とする（メモリ量の保証ではなくUI操作の制限）。既存の大きな配列の表示・セル編集はページ経由で可能。

ライフサイクルのPriorityはエンジン側のアタッチ設定として表示・保存するもので、ゲーム側メンバーの `[Inspector]` 指定とは別に扱う。

Inspectorメンバーの改名・削除は追加の属性なしで許可する。保存データのvaluesにだけ存在する項目は無視し、新しい名前のメンバーはコンストラクター／フィールド初期化子の値を使う。同名の対応項目は保持する。読み込み・C#再読み込みで項目の追加・改名・削除を検出したEditorはSceneを未保存にする。次のシーン保存では現在のメンバーだけを書き出し、古い項目をYAMLから削除する。未読込の他シーンは一括で書き換えない。

`[FormerlySerializedAs]` は改名前の値も保持したい場合の任意指定。Coreの共通名解決を通じ、保存シーンの復元とC#再読み込みの両方で旧名を現在のメンバーへ対応付ける。Inspectorの表示と新しい保存データは現在の宣言名を使い、YAMLのversionは上げない。使い方はREADMEの「Inspectorメンバーを改名する」を参照。

自作struct・既存埋め込みクラスの入れ子にも同じ旧名解決を適用し、新旧の保存名が同じメンバーへ重複して対応するデータは拒否する。C#再読み込みでは完全名と対応するメンバーの型、配列のrankを再帰的に比較し、再コンパイルによるTypeの違いを許容する。メンバーの追加・削除・旧名付き改名は許容するが、既存メンバーの非互換な型変更は採用せず、元の編集Sceneを保持する。

- 名前の比較は大文字小文字を区別する。空名・前後に空白のある旧名、別メンバーの現在名または旧名との衝突は拒否する。
- 一つの保存データに新旧両方の名前、または同一メンバーを指す複数の旧名がある場合は、優先順位で上書きせず復元を拒否する。
- C#再読み込みでは対応する名前がある場合のみ従来の型互換性検査を行い、失敗時は旧コードとSceneを保持する。属性なしの改名は新メンバーとして初期化する。これはメンバー名の移行であり、enum型名・enum定数の改名には適用しない。
- 旧名の属性は、未変換のシーンやバックアップを読み込むために保持する。`[Inspector]` 対象外のメンバーに付けても表示・保存対象にはならない。

### Data Assets

UnityのScriptableObjectに当たる、継承なしの普通のクラスで作る共有データ。使い方はREADMEの「データアセットを作る」を参照。

- `[DataAsset]` を付けたpublic・非abstract・非ジェネリックのクラスが対象。作成メニューに出すためpublicな引数なしコンストラクターが必要。`[DataAsset("Items/Weapon")]` の引数でメニューの場所を指定でき、省略時は型名になる。判定とメニュー表記の正本はCoreの `DataAssetDescriptor`。
- 保存形式は `.pure.asset.yaml`（version: 1、ID、typeId、values）。アセットIDとファイルパスはエンジン側の管理情報とし、ユーザーのクラスに持たせない。読み書きの正本はCoreの `DataAssetSerializer`。
- 値の変換はシーンと同じ `[Inspector]` 規則（`InspectorValueTypes`）を使い、旧名解決・不明項目の無視・ membersChanged 報告も同じにする。アセットが所有する値の判定は `SceneReferenceTypes.ContainsAssetExternalReference` が正本で、`SceneObject` とデータアセット型への参照だけを拒否する。登録済みの普通のクラスや `Transform` はシーンでは参照だが、アセット内では入れ子の値として保存する（YAML形式はシーンの入れ子と同じ写像で版は上げない）。
- 作成メニューはProjectペインの Create Data Asset（型のメニューパス順、フォルダ階層付き）。使えない型は黙って隠さず理由を表示する。新規アセットの初期値はコンストラクターと初期化子の値を使う。実行中は読み取り用として扱い、メモリ上の変更をファイルへ自動で書き戻さない。
- Inspector編集はシーンと同じ行エディターを流用する。編集の dirty 振り分けは `MarkEdited` がインスタンスの所有（アセット到達集合か）で決め、シーン側の操作は従来の `MarkSceneChanged` のままにする。参照系エディターは所有IDを持たないため非対応表示になり、保存時にも拒否する。未保存はシーンと別管理で、選択切替・シーン切替・終了時に保存確認を出す。C#再反映時は反映前のYAMLを新旧の型IDで付け替える。
- 実行中の読み込みはCoreの `DataAssetStore`（ID・型引き、フォルダ走査と診断）。プロジェクトを開く・C#を反映する・Playするたびにスナップショットを作り直し、編集用と各Play実行で共有しない。ゲーム側はコンストラクタ注入で受け取る。

- Component内の属性付きメンバーが `[DataAsset]` 型なら、入れ子の値でもシーンComponent参照でもなく、プロジェクトのアセット参照として扱う。専用の公開ラッパーは要求しない。単体・配列・List・stringキーDictionary・入れ子メンバーは既存の参照Codec経由で `{ref: asset-id}` を保存し、Sceneのスナップショットから通常のC#インスタンスを解決する。同じスナップショット内では共有し、Clone・Playでは分離する。Missingは既存のSceneReferenceStoreでIDを保持する。
- Editorは型の一致するアセットを候補に出し、同じ型のシーン上のインスタンスを候補に混ぜない。ファイルD&Dは専用のIDペイロードを使い、押下ではなくクリック完了時にアセットのInspectorへ移る。候補更新では保存済みアセットを再走査する。重複IDはどちらも解決せず診断する。SceneObject／登録Componentの参照欄はStuffs行、型一致Prefab、DataAssetの専用payloadを同じ行Drop経路で受け付け、Prefabは非実行テンプレートのRootまたは一意なComponentを割り当て、Hierarchyには追加しない。
- 今回の接続範囲は **Component → DataAsset**。DataAsset内から別のDataAssetやSceneを参照する保存は未対応のまま。既存のDataAsset編集と同様に、Playで使う値は明示的に保存した値であり、実行中の変更は書き戻さない。
- 一覧編集（実装済み）：`ViewportTabs` のGame横に `DataAssetEditor` タブを置き、型セレクターで同型の全アセットを行に並べる。行=1ファイル、列=`[Inspector]` 順。セルは単体Inspectorの行エディターを流用し、スカラーは直接、`List`・`Dictionary`・入れ子単体は箱＋内スクロールで編集する（箱内のAdd／削除・Create／Set Nullは単体と同じ）。行高は既定固定＋下辺ドラッグの一時伸縮で、ファイルには保存しない。表全体で1つのdirtyとして一括保存し、入力エラー・Play中は全体を止める。型切替・終了時は保存確認を出す。C#再反映は行インスタンスを新旧の型IDで付け替え、列は新Typeで組み直す。

### Prefabs

UnityのPrefabに当たる、SceneObjectの単一ルート＋子孫をファイル化して複製する仕組み。コピーのみで、配置後の自動更新・Override・Variantは作らない。保存元・配置したルートは表示用の配置元IDを持ち、StuffsではPrefabアイコンになる（子や通常オブジェクトはSceneObjectアイコン）。使い方はREADMEの「プレハブを使う」を参照。

- 保存形式は `.pure.prefab.yaml`（version: 1、PrefabのID、objects）。objectsの1件分の形はシーンの `version: 3` と同じ（id・name・parentId・siblingIndex・components、Componentの `id`・typeId・values・priorities）に、表示用の配置元 `prefabId`（省略可）を加えた形。Prefab内の最上位のparentIdはなし。読み書きと配置の正本はCoreの `PrefabSerializer`。シーンの `SceneObject`／`SceneObjectDocument` も同じ `prefabId` を持ち、保存往復とCloneで維持する。
- 値の変換・旧名解決・不明項目の無視・membersChanged報告はシーンの `[Inspector]` 規則を再利用する。メンバーの追加・改名（`FormerlySerializedAs`）・削除には耐え、非互換な型変更・未知のtypeId・ライフサイクルのないPriorityは配置を拒否して配置先を変えない。Prefabだけの別仕様は作らない。
- 配置時は複製範囲の内部参照だけ新しいIDへ付け替え、範囲外・画像・データアセット参照は維持する。対象不在はシーン参照と同じくC#はnull＋ID保持（Missing）とし、同じIDが戻れば再接続する。配置は末尾への追加（ルートまたは指定親の末子）とし、Prefab内の兄弟順を保つ。失敗時は作りかけを除去し、実行中は予約削除に任せて二重解放しない。
- Editor配置とゲーム実行中の動的生成は同じ配置経路を使う。EditorはStuffsの右クリック保存・Stuffs→ProjectへのD&D保存（SceneObject ID単位のペイロード、落としたフォルダへ自動連番で作成）・Projectペインの管理（作成相当の一覧・改名・削除）・明示的な配置メニュー・Project→StuffsへのD&D配置（ファイルパス単位のペイロード、行上はその子・余白はルート、行のハイライト付き）・Play中の保存・配置・D&D禁止に対応する。新規保存先はProject内の `.pure.prefab.yaml` で、Save as PrefabとD&D保存はいずれも既存ファイルの上書きをしない。保存元のルートには新規PrefabのIDを付けてシーンを未保存にし、配置したルートには配置元PrefabのIDを付ける。Stuffsは `HierarchyNode.IsPrefab` でPrefab／SceneObjectアイコンを切り替え、Prefab Editorのルートは旧ファイル救済のため編集中はPrefab表示にする。InspectorのSceneObject／登録Component参照欄へのD&Dは後述の非実行テンプレート参照であり、配置はしない。
- Projectのダブルクリック／Enter／Openは配置ではなく、中央の単一Prefab Editorタブを開く。既存のペイン位置を変えず、StuffsとInspectorはアクティブな編集コンテキストを共有する。メインシーンの文書とPrefabの編集用Sceneを分離し、タブ切替では両方の編集状態を保持する。Prefab編集はライフサイクルを実行せず、Ctrl+Sによる明示保存で元ファイルを更新し、Prefab・既存Object・ComponentのIDを維持する。閉じる／別Prefabへの置換では未保存を保存・破棄・キャンセルで確認する。Explorerから対象ファイルまたは祖先フォルダを改名・削除するときも、ディスク変更前にPrefabを閉じる確認を行う。
- Sceneのパス・Startup・Explorerの改名／削除保護とPlayはアクティブなPrefabではなくメインシーンを基準にする。Prefabを開いている間はコードのホットリロードを保留し、閉じて反映する案内をステータスに出す。Prefabの保存による配置済みコピーの自動更新やOverride管理は行わない。
- InspectorのPrefab参照は`{ prefab: <asset id>, target: <object/component id> }`として保存する。型はSceneObject／登録Componentのまま。`PrefabReferenceStore`が別Sceneにテンプレートを復元し、同じPrefab内の参照は同じインスタンスへ解決する。通常のHierarchy・描画・ライフサイクルには登録しない。編集／Play／コード再読み込みごとに分離し、終了時にテンプレートのIDisposableも解放する。ファイル不在はnull＋両ID保持でClear可能。循環するPrefabアセット参照は拒否する。
- 実行中の生成は `PrefabSpawner` をゲーム側のコンストラクター注入で受け、`Instantiate<T>(参照, parent?)`で対応する同じ型のコピーを返す。親省略時はルートへ生成する。既存の`Spawn(prefabId, parent?)`も利用できる。Prefabの一覧は `PrefabCatalog`（フォルダ走査と診断、重複IDはどちらも解決せず除外）。Play準備時に実行用Scene・factory・カタログを束縛し、Start以降に使う。コンストラクターでは使わない。編集用と各Play実行で共有せず、2回目のPlayに前回の配置を持ち越さない。

### ライフサイクルのメソッド

`[Update]` は次の2形式に対応する。

```csharp
[Update]
private void Tick() { }

// 引数を使う場合の別例。同じクラスに両方必要という意味ではない。
[Update]
private void Tick(float dt) { }
```

`dt` は前フレームからの経過秒。見た目やアニメーションに使う。
ホストとクライアントで一致しないため、勝敗・配札などのゲームルールを `dt` に依存させない。

以下は2026-09-21に合意した仕様。Coreの `SceneRuntime` で実装済み。EditorのPlay／Stop接続も実装済み。

- 各クラスでStart・Update・Destroyはそれぞれ最大1つ。継承を含めて判定し、overrideは1つの実行対象として扱う。
- 非public（基底クラスのprivateを含む）も対象とし、非static・非generic・同期のvoidメソッドに限定する。async voidも認めない。abstractは具象overrideを実行対象とする。
- Start・Destroyは引数なし。Updateは引数なし、またはfloat引数1つ。
- 不正な宣言は実行開始前の検証で拒否し、対象の型・メソッドと理由を報告する。

### 開始・更新・追加・削除・停止（Coreで実装済み）

- 全Componentの生成・`[Inspector]`復元・実行前検証がすべて成功してからStartする。準備に失敗した場合はコンストラクターが例外を投げ、どのStart／Update／Destroyも呼ばない。復元途中で生成済みの `IDisposable` は生成の逆順でDisposeしてから投げる。コンストラクター自体が完了しなかった資源は生成側で解放する。
- 開始対象すべてのStartを終えてからUpdateに進む。Startは実行用インスタンスにつき1回。
- 実行中に追加された対象は次フレームから参加し、そのフレームのUpdateより先にStartを呼ぶ。
- 削除は予約し、予約後はその対象のStart・Updateを呼ばない。フレーム末にDestroyを呼んで削除し、その後に `IDisposable` はDisposeする。既に実行した処理は巻き戻さない。既に削除で終了した対象はStop時に繰り返さない。
- Stop時は以後の更新を止め、実行用Sceneを破棄してDestroyを呼ぶ。同じ対象のDestroy／Disposeを重複して呼ばない。`Stop()`／`Dispose()` の重複呼び出しはno-opとする。
- 終了処理の確定ルール：受け入れ済みの全対象に `[Destroy]` をDestroy優先度順に一度だけ呼ぶ。Start済み・Start中に失敗したもの・未開始を問わず対象とし、既に削除でDestroy済みのものと、コンストラクターや不正Attachで受け入れなかったものは呼ばない。Destroyの後に `IDisposable` は一度だけDisposeする（Destroyなし・Destroy例外でも行う）。
- 再度Playした場合は新しい実行用インスタンスを作り、Startから始める。前回の実行状態（回数・破棄済みインスタンス・Dispose済み資源）は持ち越さない。一時停止・途中再開は今回の範囲に含めない。
- Start・Updateの例外では実行全体を停止し、対象オブジェクト・型・メソッド・例外を報告する。Destroy／Disposeの例外も報告するが、残りの対象の後片付けは続ける。

例：A・Bが開始対象なら、両方のStartが終わってからUpdateを実行する。AのUpdateが、まだUpdateしていないBの削除とCの追加を予約した場合、そのフレームのBのUpdateは呼ばず、末尾でBのDestroyを呼ぶ。Cは次フレームにStartしてからUpdateに参加する。この例はAがBより先に実行される場合であり、同じPriorityの順序を保証するものではない。

#### APIと境界条件

- `new SceneRuntime(source, registry)` は宣言検証と制作データの複製を行う。コンストラクターではStartを呼ばない。初期の制作データには既存の登録表・保存可能な型の制約が適用される。準備失敗時は例外を投げ、Start／Destroyを呼ばず、生成済み `IDisposable` のみ解放する。解放中にも例外が出た場合は残りの解放を続け、元の準備エラーを先頭、解放エラーを発生順に保持した `AggregateException` を投げる。解放が成功した場合は元の例外をそのまま再送出する。
- `Start()` は初期対象を開始する。`Step(float dt)` は追加対象のStart、Update、予約削除の順に1フレームを実行する。dtは有限の0以上の秒数。
- `Scene` は実行用Scene。既存の `Scene.AddEmpty()`、`SceneObject.Attach()`、`Scene.Remove()` が実行機構へ通知される。追加はSceneに即時現れるが、現在の開始バッチ／フレームには参加しない。削除対象はフレーム末までSceneに残る。
- 各開始バッチの対象を開始時点で固定する。Start中に追加した対象も次のStepまで待つ。追加直後に削除された対象や、Start前に削除された対象はStartを呼ばずDestroy（＋Dispose）する。
- 実行側が受け入れた全インスタンスを破棄対象とする。Start途中の例外でも、Start済み・失敗したもの・未Startの対象を含めてDestroy＋Disposeで後片付けする。コンストラクターで拒否されたSceneや、不正なAttachで受け入れなかった対象にはライフサイクルを呼ばない。作り済みインスタンスのAttach失敗時の解放は呼び出し側が担う。
- `Stop()`／`Dispose()` は繰り返し呼べる。Start前でも受け入れ済みの対象をDestroy＋Disposeする。DestroyとDisposeは対象ごとに一度だけ行い、二重実行しない。`[Destroy]` が `IDisposable.Dispose` の実装メソッドそのものである場合はDestroyの順序で一度だけ呼び、例外時もDisposeとして再試行しない。別のDestroyメソッドから利用者が明示的にDisposeを呼ぶ場合、その二重解放防止は利用者の責任とする。コールバック中のStopは現在のコールバックが戻ってから後片付けし、後続のStart／Updateを呼ばない。
- 同一runtimeでStartの再呼び出し、Stepの再入、開始前・停止後のStepは拒否する。再実行は新しい `SceneRuntime` を作る。
- Destroy中とStop要求後の追加・Attach・削除、削除予約済み対象へのAttachは拒否する。同一runtimeで同じcomponentインスタンスの重複利用・破棄後の再利用も拒否する。
- ライフサイクルの例外は `Errors` にオブジェクトID・名前・型・メソッド名・元の例外を記録する（Dispose失敗はメソッド名 `Dispose`）。Start／Updateの例外は実行全体を停止する。通常の削除でのDestroy／Dispose例外は記録して削除を完了し、他の対象の実行は続ける。Stop時もDestroy／Dispose例外にかかわらず後片付けを続ける。
- 実行・構造変更は単一スレッドで使う。Priorityは各ライフサイクルで小さい順に適用し、同値の順序は保証しない。component単独のDetachは今回の範囲に含めない。
- Componentの生成箇所は `SceneSerializer.Restore`（`Clone`／`Deserialize` の `CreateComponent`）、編集時の `ComponentAssets.TryAttach`、実行中の呼び出し側 `new`＋`Attach` に限る。各生成箇所は省略可能な `Func<Type, object>? factory` を受け、未指定時は従来のパラメータレス生成を使う。指定時はその結果を使い、失敗したら報告する。パラメータレス生成で再試行して隠さない。factory の戻り値は null でなく要求どおりの exact type であることを確認する。呼び出しごとに新しいインスタンスを返す契約とし、共有は注入するサービス側に置く。資源解放箇所は `SceneRuntime` の削除時（`DestroyRemoved`）と停止・失敗時（`DestroyRemaining`）のDestroy後Dispose、および復元・準備失敗時の生成逆順Disposeに限る。解放箇所は維持する。
- ゲームの Component は普通のC#コンストラクタでサービスを受け取る。`ctor = 依存`、`[Inspector] = 保存データ` とする。保存値はコンストラクタ実行後に復元するため、保存値を使う初期化は `Start` に置く。編集時にも生成するため、Component とサービスのコンストラクタで通信やゲーム進行を開始しない。`[Inject]`、独自コンテナ、サービスロケーター、階層Scope、コード生成は追加しない。プリミティブ引数の一律禁止や、型からサービスかどうかを推測する独自検証も追加しない。
- サービス登録・解決には Microsoft.Extensions.DependencyInjection を Game 側の登録処理と接続部分でのみ利用する。Core は `Func<Type, object>` の生成関数だけを受け、MS DI を参照しない。Component 自体の DI 登録は不要で、既存の ComponentRegistry への型登録は別の役割として残す。`ComponentRegistry.Register<T>` に `new()` 制約はない。型登録時にインスタンスは生成せず、解決の成否は実際の生成時に判定する。
- プロジェクト側の登録口は自作C#内の `public static void ConfigureGameServices(IServiceCollection services)` 1つのみとする（`ProjectGameServices` が検出・適用）。組み込みの `GameServices.Configure` に加えて適用し、登録がない既存プロジェクトは組み込みのみで従来どおり動く。同名が複数ある場合や形式が違う場合（非public・非static・async void・戻り値・引数の不一致など）は理由を報告し、新しい登録を採用しない。登録処理自体の例外も報告する。
- 編集時・Play時・再Play時は同じ登録処理（組み込み＋採用中のプロジェクト登録）から、編集用と各 Play 用の独立したサービス群（provider＋明示 Scope）を作る。編集と Play、異なる Play の間では Singleton も含めて共有しない。Root provider から Scoped を直接解決せず、必ず各 Scope を通す。provider 作成時は `ValidateScopes` と `ValidateOnBuild` を有効にする。
- サービス登録を含む自作C#の再読み込み・Project読み込みでは、新コードの登録・サービス生成・Scene移行がすべて成功してから採用する。準備失敗時は直前の正常なコード・編集Scene・サービス群を維持し、生成済みの移行先 Component・候補 Scope・provider を解放してから報告する。未保存の Inspector 値・オブジェクトID・Priority・選択状態・未保存状態を維持し、Play中・ファイル操作中・入力エラー中の反映保留を守る。
- Play の順序は、Play 用 provider・Scope → factory → `SceneRuntime`（生成・復元・検証）→ `Start` とする。Stop では Runtime の終了処理（Destroy＋Dispose）を完了してから Scope、provider の順に終了する。準備失敗時も生成済みの所有資源を逆順で解放し、Scope・provider も終了する。どの `Start` も呼ばない。元の例外と後始末中の例外を保持する。登録処理やコンストラクタの外部副作用まで巻き戻せるとは扱わない。
- DI が生成・所有する disposable サービスは DI 側（Scope・provider 終了）で後始末し、Component は注入されたサービスを Dispose しない。factory 経由で生成した Component 自体は DI の所有物ではなく Engine が一度だけ Dispose する。手動 `Attach(object)` の所有権は変更しない。

### Play時の制作データの分離（Coreで実装済み）

- `SceneRuntime.Stopped` は全 Component の終了処理が完了してから一度だけ通知する。`PlaySession` はこの通知で Scope・provider を解放する。コールバック内の Stop／Dispose、Runtime の直接停止、Start／Update 例外による自動停止も同じ経路を通る。停止後の Runtime と Errors は参照できる。
- 編集用 Component は Editor が所有し、シーン切替・削除・一時読み込みの破棄・終了時に Dispose する。編集用の Destroy は呼ばない。終了時は全 Component の Dispose を試みてから編集用 Scope・provider を解放し、失敗を集約する。

- 現在の編集内容から別の実行用Sceneとクラスのインスタンスを作る。未保存の編集内容も対象にする。
- オブジェクトのID・名前・アタッチ構成と、対応済みの制作データ、Priorityを引き継ぐ。クラスのメンバーは `[Inspector]` の値を引き継ぎ、それ以外は新しいインスタンスの初期値を使う。
- 実行中の変更を編集用Sceneに自動で書き戻さない。コピーはPlay時に行い、毎フレームは行わない。実行用SceneでのPriority変更も編集用Sceneに漏れない。
- この分離はSceneとそのインスタンスが対象。ゲームコードのstatic変数や外部への副作用の巻き戻しを意味しない。
- 複製には `SceneSerializer.Clone` を使い、既存の制作データ変換・検証を共有する。YAML文字列やファイルを経由しない。Play 時の Clone には Play 用 factory を渡し、注入されたサービス参照は保存・コピーせず Clone 先のサービス群から新しく解決する。Editor の Play／Stop ボタンは `PlaySession` を使う。

### 実行コスト（実装・測定済み）

- 属性の探索とメソッド検証は型ごとに結果を再利用し、毎フレーム行わない。
- 呼び出し先は開始・追加時に型付きデリゲートへ結び付け、Updateの反復呼び出しで `MethodInfo.Invoke` や `DynamicInvoke` を使わない。
- Updateのある対象だけを更新リストに持つ。追加・削除時にリストを更新し、変更のないフレームでは再構築しない。Priorityの整列は対象や設定が変わったタイミングで行い、定常フレームでは並べ替えも割り当ても行わない。
- 制作データ変換でもInspectorメンバーの検出結果を型ごとに再利用し、復元時のID重複はHashSetで調べる。
- 変更のないフレームでは実行機構が一時配列やリストを生成しない。空Updateの測定では定常Stepの割り当て量は0 Bだった（Priority適用後も0 Bを維持）。ゲームコード側の割り当ては別。
- 開始時の複製・結び付け・Startと、Update対象数ごとの1ステップ時間・割り当て量を分けて測定済み。条件と結果は [ImplementationPlan.md](ImplementationPlan.md) に記載。実際のゲームのFPSや対応個数は保証しない。

### Priority（Core・Editorで実装済み。Play／Stop接続を含む）

- Priorityはアタッチごと、かつライフサイクルごとのエンジン側設定とする。ゲーム側クラスに基底クラス・インターフェース・Priority用メンバーを要求しない。
- Start／Update／Destroyそれぞれに独立した整数値を持ち、初期値は `0`、負の値も許可する。同じ型でも別オブジェクトへのアタッチなら別の値を設定できる。
- 各ライフサイクルの実行時に、Priorityの**小さい順**に、同じSceneの全対象で実行する。オブジェクトごとの並べ替えだけで済ませない。
- 同じPriorityの実行順は保証しない。登録順やオブジェクトの並び順に依存しない。チェックでも同値の順序を固定しない。
- Inspectorには、そのクラスに存在するライフサイクルのPriorityだけを「Start Priority」「Update Priority」「Destroy Priority」として表示する。ライフサイクルがなければPriority欄を表示しない。通常のInspectorメンバーとは区別してアタッチ設定として扱い、数値検証・無効表示・Esc復元・未保存状態・入力エラー中の保存拒否は既存の仕組みに合わせる。
- Priorityはアタッチ設定としてシーンに保存し、Inspectorのvaluesには混ぜない。保存形式と互換性は下記のYAML節を参照。
- 全Start後にUpdate、実行中追加分は次フレームからStart、削除予約後のStart／Update抑止とフレーム末Destroy、Stop時の全対象Destroy、例外時の停止と残りDestroyの継続・二重Destroy防止は維持する。通常削除のDestroyはそのフレームの削除対象全体で、Stop時のDestroyは残っている全対象で並べる。追加された対象を現在の開始バッチへPriorityを理由に割り込ませない。
- 実行中のPriority変更は広げず、開始前の設定を基本とする。動的に追加したcomponentには最初のStartより前に設定できる。Start／Updateは最初のStartより前だけ、DestroyはDestroy実行より前だけ受け付け、それ以外は明示的に拒否する。値だけ変わって順序に反映されない状態を作らない。

例えば `[Start]` と `[Update]` だけを持つクラスには、次の2項目だけを表示する。

```text
Start Priority     0
Update Priority    0
```

`[Start]` だけならStart Priorityだけを表示する。属性がなければPriority項目も表示しない。
同じ属性が付いたメソッド数と実行途中の追加・削除は上記のライフサイクル仕様に従う。複数シーンにまたがる実行順は未決定。

## 保存データの区別

| 種類 | 対象 | 現時点の扱い |
| --- | --- | --- |
| 制作データ | シーン、ID、名前、親子関係、アタッチ情報、Inspector対象の値、Priority、素材参照 | 次の実装範囲。形式は未確定 |
| ゲームのセーブデータ | プレイヤーの進行、所持品、対戦途中の状態など | 制作データとは別。今は保留 |
| Editorの設定 | ペイン配置、テーマ、最近開いたプロジェクトなど | シーンとは別。永続化は未実装 |

シーンは1シーン1ファイルのYAML（`.pure.scene.yaml`）で保存する。現在の形式は `version: 2`（`version: 1` は読み込みのみ）。
制作データをそのままゲーム進行のセーブデータとして扱わない。

### YAML保存の構成（実装済み）

- `SceneDocument` はversionとobjects、各オブジェクトはid・name・parentId・siblingIndex・componentsを持つ保存用データ。`version: 2` で保存し、`version: 1` は全てルート・配列順の兄弟として読み込む。次の明示保存で2へ更新し、読み込み時にファイルを書き換えない。
- 親子は同じScene内だけで結び、欠落Parent・自己参照・循環、欠落・重複・負数・範囲外のsiblingIndexを拒否する。ルートの兄弟順は文書順に従う。親削除はその時点の子孫ごと削除する。実行中は対象子孫を全て削除予約する。
- 各componentはtypeIdとvaluesを持つ。valuesは `[Inspector]` が付いた対応型（上記のInspector節）のみ。string・コレクション・`Transform`・`Sprite`・`Nullable` のnullにも対応する。有限でないfloat／double（NaN・Infinity）は保存・読み込みとも拒否する。
- `ComponentRegistry` で固定文字列IDとC#型を明示登録する。Assetsと読み込みは同じ登録表を使う。C#のクラス名・名前空間を変更しても固定IDは維持する。組み込みは `core.transform`・`core.ui-element`・`core.image`・`core.button`・`core.text` で登録する。
- `SceneSerializer` はCoreに置き、Sceneと保存用データの変換・検証・YamlDotNetによるYAML処理を行う。Avaloniaに依存しない。`Clone`（Play時の複製を含む）では配列・リスト・辞書・`Transform`・`Sprite` を深く複製し、親子はClone先へ解決して編集用と実行用・編集用とClone先の共有を残さない。
- ファイル選択、保存先、未保存状態、確認・エラー表示、ファイルの置き換えはEditorが担当する。
- 読み込みでは保存時のオブジェクトIDを復元する。全体の復元に成功してから編集中のSceneを入れ替え、ライフサイクルは実行しない。素材欠落はIDを保持したまま警告とし、構造エラーと同じ理由で開けなくしない。
- 未対応のversion、未知のtypeId、重複キー・ID・同型component、既存メンバーの不正な値を拒否する。values内の存在しないInspectorメンバーは読み飛ばし、次の保存時に削除する。文書構造やベクトル・Transform・Sprite内部の未知キーは引き続き拒否する。自作クラス内部の未知キーは読み飛ばし（クラス側の追加・削除があっても旧シーンを開ける）、欠けた項目はクラスの初期値を維持する。
- 保存値がない新しいメンバーはクラスの初期値を維持する。メンバー名の変更にはデータ移行が必要で、自動移行は未実装。
- オブジェクト・componentの順番を維持し、valuesはメンバー名順で出力する。必要な文字列は引用し、独自タグ・アンカーは生成しない。コメントの保持は行わない。
- 保存は同じフォルダの一時ファイルに書き込み、完了後に元ファイルと置き換える。
- オブジェクト参照・Editor設定はそれぞれの機能を実装するときに追加する。

#### Inspector拡張値のYAML形式（実装済み）

- ベクトルはマッピング：`Vector2` は `{x, y}`、`Vector3` は `{x, y, z}`、`Vector4`・`Quaternion` は `{x, y, z, w}`。`Color` は `{r, g, b, a}`。例：

```yaml
values:
  Position: {x: 1, y: 2, z: 3}
  Rotation: {x: 0, y: 0, z: 0, w: 1}
  Tint: {r: 1, g: 0.5, b: 0.25, a: 1}
```

`Color` の読み込みは旧Vector4の完全な `{x, y, z, w}` もRGBA順で受け付ける。旧 `Image.Color` の値を維持し、次回保存時に `{r, g, b, a}` へ統一する。両形式の混在・欠落・余分なキー・非有限値は拒否する。Vector4側の読み込み形式は変更しない。C#ソースのVector4代入はColor構築へ変更が必要で、コード再読み込み時の任意の型変更を許可する機能ではない。

- `Transform` 型のメンバーは `LocalPosition`・`LocalRotation`・`LocalScale` のマッピング。`Transform` コンポーネント自体は同じ3メンバーをvaluesに持つ。例：

```yaml
values:
  Target:
    LocalPosition: {x: 7, y: 8, z: 9}
    LocalRotation: {x: 0, y: 0, z: 0, w: 1}
    LocalScale: {x: 1, y: 1, z: 1}
```

- 一次元配列・`List<T>` は従来互換のシーケンス、辞書はマッピング。多次元配列は `{dimensions: [各次元の長さ], items: [行優先順の要素]}` とし、`[0, 3]` のような空次元も形状を保持する。rank・長さ・要素数・下限を検証してから確保する。nullは `null`、一次元の空は `[]`・`{}`。例：

```yaml
values:
  Scores: [10, -20, 30]
  Tags: [a, b]
  Counts:
    alice: 3
```

- enumは名前で保存する（`[Flags]` の組み合わせは `Read, Write` の形式）。読み込みでは名前（大文字小文字を区別しない）と数値の両方を受け付ける。未知の名前は拒否する。例：

```yaml
values:
  Level: Normal
  Access: Read, Write
```

- C#再読み込みでは、enumの完全名・基底整数型・Flags属性と既存の全定数名／値が一致すれば、新しいアセンブリの型へ値を移行する。定数の追加は許可する。改名・削除・数値変更・基底型変更・Flags属性変更は拒否し、旧Sceneを保持する。対応するNullable・配列・List・辞書内のenumにも同じ判定を適用し、その他の型変更の拒否は維持する。

- `version` は `2`。旧形式（string・int・float・boolのみのシーン）はそのまま読み込む。未知のキー・欠落・余分なキー・シーケンスとマッピングの取り違え・非有限数は拒否する。
- `Sprite` は `{imageId, sourceRect?}` のマッピング。`imageId` は画像IDのD形式文字列（空ID不可）、`sourceRect` は省略・nullで全体、`{x, y, width, height}` で部分領域を表す。例：

```yaml
values:
  Sprite:
    imageId: ba6104ce-7234-4673-8bd0-cee380b505de
  Cropped:
    imageId: ba6104ce-7234-4673-8bd0-cee380b505de
    sourceRect: {x: 16, y: 8, width: 32, height: 24}
```

- 自作クラスは `[Inspector]` メンバー名のマッピング。nullは `null`。例：

```yaml
values:
  Boss:
    Hp: 30
    Name: Rex
  Party:
  - Hp: 1
    Name: A
  Ranks:
    leader: {Hp: 9, Name: Z}
```

#### Priorityの保存形式と互換性（実装済み）

- 各componentは `typeId` と `values` に加え、任意の `priorities` を持つ。`values` とは分離し、アタッチ設定として扱う。例：

```yaml
components:
- typeId: sample.player-stats
  values:
    Hp: 100
  priorities:
    start: -5
    update: 10
```

- `priorities` のキーは `start`／`update`／`destroy` のみ。値は整数（負の値を含む）。キーは省略可能で、省略時は `0`。
- すべて `0` の場合は `priorities` 自体を省略する。旧形式（`priorities` なし）はすべて `0` として読み込む。新形式の全ゼロも省略されるため、旧ファイルとの差分は生じない。
- 未知のキー、不正な値（非整数・オーバーフロー・コレクション・null値など）、存在しないライフサイクルへの指定は拒否し、黙って捨てない。重複キー・ID・同型componentの扱いは既存の検証に従う。
- `SceneSerializer.Clone` でもPriorityを引き継ぎ、実行用Sceneと編集用Sceneの分離に含める。YAML文字列やファイルを経由しない。
- `version` は `2`。Priorityの有無でversionは変えない。

## Project（実装済み）

- Projectはフォルダ単位で、ルートの `Project.pure.project.yaml` と `Scenes/` 内の複数シーンで構成する。
- `ProjectDocument` はCoreの保存用データ。version（現在1）、name、startupScene（相対パス）を持つ。
- Editorの `ProjectFile` がProjectの作成・読み込み・シーンの列挙・起動シーンの変更を扱う。シーン一覧自体は保存せず、Scenesフォルダから取得する。
- Launcherで新規Projectを作り、空のMainシーンで開始する。作成途中は一時フォルダに書き込み、完成後に新しいProjectフォルダとして配置する。既存の同名フォルダは上書きしない。
- Projectを開くと起動シーンを編集対象にする。Project Explorerの右ペインでシーンファイルをダブルクリックまたはEnterで切り替える。C#ファイルのダブルクリック／Enter／OpenはProjectルートとそのファイルをZedで開く（ルートをワークスペースとして開き、ファイルを表示する）。Play中も開ける。`zed` コマンドが見つからない場合はステータスとConsoleに理由を表示する。
- 底ペインのProject Explorerは左にフォルダTree、右に中身を出す。Assets／Scenesタブは廃止し、Projectルートの下にはエディタが扱う種類（シーン／C#／画像／データアセット／Prefab／Localization）とそのフォルダだけを表示する。隠し・`bin`／`obj`・`.pureengine` のフォルダ、Project管理情報・生成ワークスペース・登録情報（`.pureasset.yaml`）・その他の一般ファイルは表示しない。組み込みサンプルの仮想Components一覧は表示しない。自作C#は元のフォルダ内のファイルからアタッチする。
- 右クリック（またはF2・Delete・Enter）でフォルダ作成・シーン作成・改名・削除・起動シーン設定・更新ができる。シーン作成はScenes配下のみ。Scenesフォルダ自体の改名・削除は不可。
- 改名・削除では編集中シーンと起動シーンの参照を付け替える。起動シーンと編集中シーン（を含むフォルダ）は削除できず、改名時はScenes外への脱出を拒否する。
- シーンの保存先はProjectのScenesフォルダ内とする。絶対パスによる参照、フォルダ外への参照、リンクによる外部参照を認めない。
- Project／シーンを切り替える前に未保存の変更を確認し、読み込み検証が成功するまで現在の編集対象を維持する。
- 起動シーン指定は編集時に最初に開くシーンとして使う。ProjectごとのC#コンパイル・自動登録・変更監視に対応。画像素材は `Assets/` へ取り込み、索引はProject Open時と明示Refreshで作り直す。詳細は [README](../README.md) のProject節を参照。

## Launcher（実装済み）

- アプリ起動時は `LauncherWindow` を表示し、新規作成・Projectファイルの選択・最近開いたProjectからの再開を行う。
- `ProjectSession` がProjectと起動シーンを読み込んで検証し、成功したデータを `MainWindow` に渡す。読み込み失敗時はLauncherにエラーを表示する。
- Editorを開いている間はLauncherを非表示にする。Editorを閉じると既存の未保存確認を経てLauncherへ戻る。Launcherを閉じるとアプリを終了する。
- 履歴はProject名とmanifestのローカルパスを最大12件保存する。保存先は `%LOCALAPPDATA%/PureEngine/recent-projects.json`。履歴が破損・保存不可でもProjectの作成・読み込みは妨げない。各エントリは一覧のRemoveボタンまたはDelete／Backspaceで削除でき、削除は履歴ファイルのみに反映しProjectフォルダには触れない。
- Launcherから新規作成するProjectは空のMainシーンを持つ。Editor内でのProject作成・切り替え操作はLauncherへの復帰に統一する。

## UI・描画・プレビュー

2026-09-22、固定サンプルの描画基盤をV2まで実装した。以下の制作・ゲーム実行機能はV3以降の目標であり、実装済み基盤は次節に区別する。作業順・候補技術・完了条件は [Vulkan描画の実装計画](VulkanRenderingPlan.md) を参照する。

- 制作者はScene Viewで画像・文字・ボタンを配置し、Inspectorで調整してシーンへ保存する。UnityのCanvas・RectTransformに近い親子・矩形配置の制作体験を目指す。具体的な保存形式・APIは実装前に確定する。
- 描画本体と資源管理はリポジトリのC#15で実装し、既存バインディングからVulkanを呼ぶ。GPUドライバー等のネイティブ依存は残る。シェーダーはGLSL 450を固定版glslangでSPIR-Vへ事前コンパイルする。シェーダーまでC#15で書けるという意味ではない。
- AvaloniaはEditorの操作画面を担当する。ゲーム内UIはゲーム用の描画処理で表示し、Avaloniaの標準UI部品への変換は行わない。
- Vulkanで描いたGPU画像をAvaloniaの表示領域へ渡す経路を第一候補とする。既存Avalonia版・GPU・ドライバーでの埋め込み、同期、リサイズ、表示領域の破棄・再作成を最初に実機検証する。
- Scene Viewは編集用Sceneを編集用の視点で表示し、選択枠・配置ハンドルを加える。ゲームのライフサイクルは実行しない。Gameは既存PlaySessionの実行用Sceneを表示し、ゲーム操作を受け付ける。Play中の編集禁止とStop後の編集データ保持を維持する。
- 単体実行とEditorで描画・入力規則・Runtimeを共有し、ウィンドウと表示先への接続を分ける。Core／RuntimeはAvalonia・Vulkanに依存させず、GPU資源を保存データに含めない。
- 最初は画面上の2D UIを対象とし、画像・日本語の文字・ボタン・矩形クリップを実装する。UIの矩形配置とゲーム空間のTransformは役割を区別する。World Space Canvas・3D・高度な演出編集は後続の範囲とする。

Windows x64の検証構成・残る制限は[実装状況](ImplementationPlan.md#vulkan-v0v2の検証2026-09-22)に記録する。

### V0〜V2の描画経路と資源所有

`PureEngine.Rendering`はCoreの配置・Sprite・Imageデータを参照し、Editor／Avalonia／Runtimeには依存しない。`DrawList`が投入順に矩形・画像・文字を三角形へ展開し、`VulkanRenderer`が単一のアトラスと1回のDrawで描く。テクスチャで並べ替えないため、半透明の前後関係を維持する。V2の`RenderingSample`は固定命令の検証用として残す。試作Playerの表示は`ImageRenderingSample`の検証専用Sceneを使い、EditorのScene Viewは編集中Sceneを `EditSceneRenderer` で描く。PlayのSceneの描画は後続。`PureEngine.Rendering.Avalonia`はGPU画像の取り込みとウィンドウ寿命だけを担当し、PlayerからEditorへの参照はない。

WindowsのAvalonia 12.1.2標準ANGLE/D3D11バックエンドを維持する。LUIDで同じ物理GPUを選び、D3D11が確保したRGBA8 UNORMテクスチャをNT handleでVulkanへ専用割り当てとしてimportする。図形の描画はすべてVulkan。CPUへの毎フレーム読み戻しは行わず、Avaloniaの`CompositionDrawingSurface.UpdateWithKeyedMutexAsync`で表示する。Vulkan 1.1、`VK_KHR_external_memory_win32`、`VK_KHR_win32_keyed_mutex`と互換D3D11共有テクスチャを必要とする。

描画はKeyed Mutexのkey 0を取得しkey 1で返す。Avaloniaはkey 1を取得してGPUコピー後にkey 0で返す。画像は外部キュー所有からgraphics queueへ取得し、COLOR_ATTACHMENT_OPTIMALで描画後、GENERALへ遷移して外部へ所有を戻す。NT handleはimport完了後にCloseHandleする。CPU側は5秒のfence待機で自分のGPU処理完了を確認し、importの破棄完了後に画像／メモリを解放する。D3D11の遅延破棄をFlushして資源を退役させる。

`VulkanDevice`はアプリケーションが所有し、Vulkan／D3D11デバイスをアプリ終了まで保持する。ペインごとの`VulkanRenderer`はターゲット・バッファ・pipeline・GPUアトラスを、`DrawList`はCPUアトラス・フォント・文字キャッシュを所有する。取り外し時に更新を止め、進行中のimport／presentを待ってペイン資源を解放する。ウィンドウ終了も同じ処理を待ち、全ウィンドウ終了後にデバイスを解放する。単一UIスレッド上で使用する。複数の物理GPUや並列描画は対象外。初期化途中の失敗も生成済み資源を解放し、device lostや描画失敗は領域を停止して通知する。自動再初期化しない。

座標は左上原点・X右・Y下の論理座標。`Matrix3x2`で位置／回転／拡縮を与え、クリップは変換後のターゲット論理座標の矩形とする。物理ターゲット寸法はBounds×RenderScalingを切り上げ、ポインターの物理座標も同じ倍率で変換する。Avaloniaのimport側の原点に合わせ、最終頂点シェーダーでYを反転する。色はsRGB符号化値のRGBA8 UNORM、アルファはpremultiplied、合成はONE／ONE_MINUS_SRC_ALPHA。リニアライト合成やHDRは行わない。入力色は0〜1に制限し、色乗算時にアルファも乗じる。

文字は同梱Noto Sans CJK JP RegularをSkiaSharpのCPUフォント機能でラスタライズする。日本語／英数字／句読点を初期対象とし、Unicode text element単位の幅折り返し、改行、文字サイズ、行間、欠落文字の「□」を扱う。複雑な双方向文字・結合スクリプトのシェーピング、禁則処理、IMEは対象外。HarfBuzzを必要とするスクリプトを追加する時にシェーピングを導入する。

文字列行と画像を同じ2048×2048・16 MiBのアトラスに追加し、変更時だけGPUへ転送する。キャッシュは最大4096項目、1テキスト16384 UTF-16単位、1バッチ60000頂点。容量超過は明示的に失敗する。自動退避・無制限拡張をせず、破棄時に全項目を解放する。画像キーの内容が変わった場合はResetAtlasで明示的に無効化する。動的な文字の高頻度更新には部分転送や字形単位のキャッシュが今後の改善候補になる。

| 依存／配布物 | 固定版・ライセンス |
| --- | --- |
| Silk.NET.Vulkan／Extensions.KHR／Direct3D11／DXGI | 2.23.0、MIT。Vulkanバインディングと共有メモリ確保用 |
| Avalonia.Desktop | 12.1.2、MIT。Editorと描画確認用ウィンドウで共用 |
| SkiaSharp／libSkiaSharp | 3.119.4、MIT＋Skiaの第三者ライセンス。画像デコードと文字ラスタライズのみ |
| glslang | 16.6.0、BSD系の複合ライセンス。開発時だけ使用し実行物へ同梱しない |
| NotoSansCJKjp-Regular.otf | SIL OFL 1.1。noto-cjk commit `165c01b46ea533872e002e0785ff17e44f6d97d8`、SHA256 `68A3FC98800B2A27B371F2FB79991DAF3633BD89309D4FFAA6946FD587F375B5` |
| チェック柄のテスト画像 | リポジトリ内のコードで生成。外部画像素材なし |

フォント・SPIR-VはRendering DLLへ埋め込み、ライセンス本文は出力の`licenses/`へコピーする。Microsoft提供のD3D11／DXGIとGPUドライバーのVulkanローダーを利用し、独自C++層は追加しない。[Avalonia 12.1.2公式interop実装](https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Windows/Avalonia.Win32/OpenGl/Angle/AngleExternalObjectsFeature.cs)、[KhronosのWin32共有メモリ規約](https://docs.vulkan.org/refpages/latest/refpages/source/VK_KHR_external_memory_win32.html)を採用パッケージと照合した。

### V3：親子・素材参照・UI保存の設計案

2026-09-22作成。**設計と実装途中の状態を区別する**。親削除時の子孫削除、および既存Transform＋UiElementを組み合わせ、CoreのUiLayoutで配置を計算する方針は合意済み。作業ツリーにはUiElementのデータ定義があり、UiLayout.Calculateの単体配置計算を実装した。組み込み登録・Scene走査・描画／入力接続は未実装で、V3全体の完了を意味しない。参照・素材管理・保存形式など、それ以外の詳細は引き続き設計案。V3の成果はGPU不要で保存・復元・配置計算を検証できる状態。Scene Viewの制作操作はV4、実入力からのクリック発火はV5、保存ゲームのPlayer起動はV6で行う。

#### 既存実装を使う範囲

`SceneObject.Parent`／`Children`／`SetParent`と循環拒否、`Transform.LocalMatrix`／`SceneObject.WorldMatrix`は実装済み。一方、現状のSceneSerializerは親子をCaptureせず、Clone・コード再読み込みで失う。Scene所属の照合、兄弟順、親削除時の子の解放も未対応。新しい保存シーンや第二のライフサイクルは作らず、保存接続時にここを補う。UiLayoutの単体配置計算とGPU不要チェックを追加済み。Spriteの素材データも追加済み。ImageからSpriteを参照する描画経路を検証用Sceneで接続済み。編集Scene・素材索引・保存への接続は後続とする。

UI用の値・コンポーネント・配置計算・クリック契約は`PureEngine.Core`に置く。Core／RuntimeへVulkan・Avalonia・Skia依存は足さない。組み込みUI型はCoreで共通登録できる入口を設け、既存Editorの`ComponentAssets.RegisterBuiltins`から利用する。ゲームのコンパイル参照は既にCoreを含むため、UIを使うだけのためにRendering DLLをゲーム用csprojへ追加しない。

#### 親子と削除

| 項目 | V3の規則 |
| --- | --- |
| 所属 | Scene内のオブジェクトは同じScene内だけで親子にできる。現行テストで使う未所属同士のSetParentは維持し、所属あり／なしの混在は拒否する |
| 操作 | 既存`SetParent(parent)`を維持し、ローカル値を変えず新しい兄弟の末尾へ移す。同じ親は従来どおりno-op。`SetSiblingIndex(index)`で子同士を並べ替え、`Scene.SetRootSiblingIndex(index)`でルート同士を並べ替える。範囲外・自己参照・循環・別Sceneを変更前に拒否する |
| 列挙 | `Scene.Objects`のフラットな作成順は維持。`Scene.RootObjects`と`Children`が表示用の兄弟順を持ち、SiblingIndexはその位置。ライフサイクルの走査順と表示順を兼用しない |
| 付け替え | Transform／UiElementのローカル値を保持。見た目の位置を保つ付け替えはV4の編集操作として別途座標を計算し、V3 APIへ曖昧な既定値を入れない |
| 親削除 | 指定オブジェクトと、その時点の子孫をまとめて削除。残したい子は削除前に親を付け替える |
| 実行中 | Remove受付時に対象子孫を全て削除予約し、以後のStart／Updateから除外。実体の除去は既存のフレーム末尾。予約済みの子を付け替えて救出すること、予約済みの親へ追加することは拒否 |
| 実行中の付け替え | 単一スレッド上で検証して即時反映する。更新の対象リスト・Priorityは変わらない。Destroy中・Stop要求後・削除予約済みなら拒否し、別の予約キューは作らない |
| 後始末 | Runtimeは削除対象全体の既存Destroy Priority→Disposeを維持し、各componentを一度だけ終了する。親→子等の新しい終了順は導入しない。Editorは削除前の子孫componentを回収し、編集用Disposeだけを一度実行する |

構造の切断・ID索引の削除は単一オブジェクト用の内部操作RemoveObjectImmediatelyに分ける。Runtimeが予約済み集合を後始末するときに、公開の再帰削除をもう一度呼ばない。SetParent／SetSiblingIndexはScene所属とRuntimeの削除予約・終了状態を検査してから変更する。失敗時は元の構造を維持する。IDの照合と検索には既存のID管理を辞書へ拡張し、重複する別索引を増やさない。

#### ID参照と素材ファイル

**2026-09-23更新：SceneObject／Component参照には、下記の[直接参照の合意](#sceneobjectcomponentの直接参照2026-09-23合意実装済み)を優先する。** 以下のObjectRefを公開値型として扱う記述、および後段のV3工程表・InspectorValueTypesへのObjectRef追加案は旧案。画像・フォントの計画はこの変更で実装・変更したことにはならない。

提案する保存用の参照は`ObjectRef`・`ImageRef`・`FontRef`の3つの小さな不変値型。保持する値はGuidだけで、実オブジェクト・Type・デリゲート・GPUハンドルを持たない。YAMLではGuidのD形式文字列、未指定はnull（内部ではGuid.Empty）とする。汎用の`AssetRef<T>`や任意オブジェクトグラフのシリアライズは導入しない。

- ObjectRefは渡されたScene内でのみ解決する。CloneではIDを保ち、解決先は必ずClone先のSceneとする。欠落した対象は解決結果null＋診断とし、保存済みIDは消さない。構造を壊す欠落Parent IDとは区別する。
- ImageRefの未指定は画像なし。存在しない非空IDや不正な画像は、描画接続後に欠落表示＋診断。FontRefの未指定はV2同梱Noto、非空IDの欠落はIDを残して同梱フォントへ代替する。
- 参照はプロジェクトローカル。同じIDが別Projectにあっても別の索引を使う。異なるProjectのオブジェクト／素材を暗黙に検索しない。

##### SceneObject・Componentの直接参照（2026-09-23合意・実装済み）

ゲーム側は `[Inspector] public Button? TestButton { get; set; }` のような通常のC#参照を宣言する。保存上の識別はID、実行中の操作対象は接続済みの実物とする。`ObjectRef<T>`／毎回の `Resolve`、専用基底クラス、IDメンバーの手書きは要求しない。工程・検証・性能測定は[実装計画](ImplementationPlan.md#sceneobjectcomponentのid参照とinspector接続2026-09-23合意実装済み)で管理する。

- **IDの所有：** SceneObjectの既存Guidを維持し、全Componentにも参照の有無によらずインスタンスGuidを付ける。普通のC#クラスを維持するためIDはエンジンのアタッチ管理情報に保持し、生成／Attachの共通境界でSceneへの公開前に確定する。Scene内ではObjectとComponentを跨いで一意。型識別の既存 `typeId` とは別。Inspectorの各欄や数値・文字列などの値自体には固有IDを付けない。同じ実物の多重所有は拒否する。
- **値と参照：** SceneObject型とProjectのRegistryに登録されたComponent型のInspectorメンバーは対象への参照として扱う。未登録の対応する自作クラスは既存の埋め込み値として扱う。Componentカードは自身のInspector値を編集し、他Componentの欄に現れる同型は参照スロットとする。登録型は現在値のアタッチ有無で値／参照の意味を切り替えず、未アタッチなら参照先不正として保存を拒否する。InspectorはDetached表示から再設定でき、埋め込み値のCreateへ戻さない。Spriteなどの素材値は今回変更しない。自動登録されるpublic具象ヘルパーも登録型なら参照になる。埋め込み値に参照が含まれていても、他のメンバーの未対応型・埋め込み再帰は拒否する。対応コンテナの入れ子はInspector節と同じ再帰規則を使う。
- **保存：** 新形式は `version: 3`。各Componentに `id` を保存し、参照は `{ ref: <GuidのD形式> }`、未指定はnullとする。参照先の値はそのComponent本体にだけ保存する。既存の親・兄弟順・Priority等は維持する。旧版（v1／v2）はSceneObject IDを保持し、不足するComponent IDを新規発行して未保存化する。登録Component型の旧インライン値はScene所有へ保持し、再割り当て／明示破棄まで保存・Cloneを拒否する。v3の参照欄でインライン値を受け付けない。旧版の参照先推測やデータの黙示破棄は行わない。
- **解決：** SceneごとにID→実物の索引を持ち、全対象の生成・登録後に宣言型との互換性と所有Sceneを検査して接続する。異なるProject／Sceneへの暗黙の検索はしない。全Startより前に接続を完了する。ID参照の前方・自己・相互循環は許可し、親子や埋め込み値の循環拒否とは区別する。不正ID・重複ID・存在する対象との型不一致は明示的なエラーとする。未アタッチ・別Scene対象の保存は拒否する。
- **欠落：** 対象不在はC#側をnullにして、保存IDをScene所有の参照管理情報へ保持し、InspectorはMissing表示とする。Missingのまま保存可能。明示Clearで初めてそのIDを除去する。元IDが復帰すれば再接続する。通常のC#代入・コレクション変更と管理情報の整合は編集／保存／構造変更の境界で処理し、毎フレーム反射監視しない。ゲームが非nullの別対象を代入した場合は実物の値を優先する。Missingで既にnullの欄への単なるnull再代入は観測できないので、Missing IDの破棄はInspectorのClearまたは対応するエンジンの明示操作で行う。配列／Listは位置、辞書はキー、埋め込み値はドット区切りで保持し、Inspectorの構造変更では付け替える。直接のC#並べ替えでずれたMissingの復元は保証しない。
- **複製と寿命：** Play用Clone・コード再読み込みはIDを保持して新Sceneの実物へ接続し直す。同一Scene内への複製経路は存在しないため、新ID発行・範囲内付け替えの分岐は未使用とする。削除・取り外し・交換時は管理対象の参照欄を変更境界で更新する。任意のローカル変数、外部サービス、Inspector対象外のメンバーへコピーしたC#参照の自動失効は保証しない。イベント購読は保存・Cloneせず、実行側で行う。
- **性能：** ID生成・索引構築・参照接続は準備／変更時へ寄せ、定常実行は通常のC#参照を使う。プロパティアクセスごとのID検索・反射、全Sceneの毎フレーム再接続を導入しない。準備時間・保持メモリ・保存サイズと、定常Updateの時間・割り当て量を分けて実装前後で測定する。速度の改善や無視できる負担を未測定のまま保証しない。Inspectorは折りたたみとページ表示で生成量を制限する。

##### 素材ファイルの既存計画

画像・フォントは`Assets/`以下へ明示的に取り込む。初期の対象はPNG／JPEGとTTF／OTF。素材ごとに隣接ファイル（例：`Assets/Cards/ace.png.pureasset.yaml`）を置き、`version: 1`・`id: <Guid>`・`kind: image|font`を保存する。素材の相対パスはサイドカーに重複保存せず、Project所有の索引が走査結果からID→パスを作る。既存の`.pureengine/types.json`はC#型ID専用のままとする。

素材とサイドカーを一緒に移動・改名すればIDを維持する。外部ツールで素材だけを移動した場合は、孤立メタデータ／未登録素材として知らせ、内容ハッシュで勝手に対応を推測しない。コピーによるID重複は曖昧なIDを解決不能にして報告し、自動でどちらかのIDを変更しない。明示的な新規取込／複製では新IDを発行する。索引の再走査だけではファイルを作成・書換しない。

Project内の素材サービスはEditorが所有する。初期実装はProject Open時と明示Refreshで再走査し、自動ファイル監視はV3の必須範囲にしない。プレイヤー向けパッケージ索引の生成はV6で扱う。取込APIと索引検証はV3、ドラッグ＆ドロップ等の制作UIはV4とする。

ProjectFile.ValidateProjectPathをシーン・フォルダ・素材で共有し、正規化後の相対パスで対象が指定ルート内かを検査する。Assets自身も検査対象とし、取込・メタデータ読込・索引からの画像読込の直前にリンク／junctionを再検証する。絶対パス・`..`による脱出・リンク／junction経由の脱出を拒否し、読み込み／コピーの直前にも検査する。外部素材は明示的にコピーして取り込み、外部パスそのものは保存しない。取込は一時領域へ素材とメタデータを準備してから公開し、失敗時は既存素材を上書きしない。ID重複・壊れたメタデータ・種別違いは診断し、任意のパスへのフォールバックはしない。

素材のライセンスをエンジンが推定することはできない。配布許可の確認と素材のライセンス文書の保持は取り込む側の責任。V3のチェックにはV2の同梱フォントと生成画像を使う。

#### UIコンポーネント

最初に用意するのは**UiElement・Text・Image・Button**。全て同じSceneObjectへ付けて組み合わせる普通のComponentとし、描画順以外の専用の基底クラスや別のRectTransformは作らない。位置・回転・拡縮は既存Transformを使い、UiElementに二重に持たせない。現在のコード上の表記は`UiElement`に揃える。Imageは既存の`PureEngine.Core`名前空間のクラスを使い、`RendererComponent`から派生させる（namespaceの宣言形式は変更しない）。`UiElement`（`core.ui-element`）・`Image`（`core.image`）・`Button`（`core.button`）・`Text`（`core.text`）は組み込み登録済み。将来のSpriteRendererも同じ基底を使うが、本体・SortingLayer・Zによる奥行き制御は今回の対象外とする。

| Component | 責任・データ |
| --- | --- |
| `Transform`（既存） | `LocalPosition`・`LocalRotation`・`LocalScale`。既存のLocalMatrixの意味・計算は変更しない |
| `UiElement`（データ定義あり） | `SizeDelta = (100,100)`、`AnchorMin = AnchorMax = (0,0)`、`Pivot = (0.5,0.5)` |
| `Text` | UiElementの領域へ同梱フォントで描く。`Content = "New Text"`・`Color = White`・`FontSize = 24`・`LineSpacing = 1.2`・`RendererComponent.Order = 0`。左寄せ・上起点で領域幅で折り返し、空文字は描画なし。Imageと同じオブジェクトではImageの後に描く |
| `Image`（描画確認用の接続済み） | `Sprite? Sprite`と`Color Color = Color.White`、`RendererComponent.Order = 0`。nullは描画なし、ColorはRGBA乗算。UiElementの領域へStretchする |
| `Button` | 領域内のクリック判定と処理の通知。見た目はImage／Textとの組み合わせで作る |

UiElementはTransformと組み合わせる。登録とInspectorの依存警告は実装済み。構築途中のAttach順序は強制せず、不足するTransform／UiElementをInspectorで通知する。Text／Image／Buttonは同じオブジェクトのUiElementが解決した領域を使う。Textは幅で折り返すが高さではクリップせず、ビューポートのクリップだけを適用する。Scene Viewの選択はUiElement矩形を使い、あふれた文字は選択範囲に含めない。描画診断はオブジェクト単位の選択／Game入力除外に使うため、一部が描画できても診断があるオブジェクトは操作対象から外れる。Image失敗時もTextとフォーカス枠の描画は継続し、画像用tintは重ねない。

InputField・DropDown・Slider、Toggle／Checkbox・ScrollView・ProgressBarは後続候補。最初の4Componentを作るために専用Canvas Componentを必須にしない。以前のUiCanvas必須・TransformとRectTransformの併用禁止という案は採用しない。

Visible・Opacity・ClipChildrenや画面全体の解像度設定は引き続き設計対象だが、現在のUiElementのメンバーとして存在するものではない。どこへ持たせるかは使用する機能の実装時に確定する。Imageの色はCoreのColorを使い、描画境界でVector4へ変換する。

#### Localization（Text・Voiceの下地・実装済み）

文言はプロジェクト直下の `Localization.pure.loc.yaml` に1表で持つ。言語列はテーブル全体で統一し、行ごとの言語差は持たせない。行は不変ID・表示キー名・言語ごとの本文・言語ごとのボイス欄からなり、言語キーは小文字のBCP47式（`ja`・`en`・`ko`等）。空欄は未翻訳扱いで空白は描かない。Game横のLocalizationタブで行の追加・改名・削除と言語列の追加・削除、各セルの本文・ボイス編集を行い、重複・空キーがある間は保存と終了を止める。`Text` は直書き `Content` に加えて任意の `LocalizedTextId` 参照を1本持ち、参照ありなら解決文を描く。Inspectorの参照欄はキー一覧からの選択・Clear・Missing表示だけで、本文・改名の編集は置かない。

解決の正本はCoreの `LocalizationService` で、現在言語→既定言語（`ja`）→表順の最初の可用言語→呼び出し側代替の順に代替する。言語コードは小文字へ正規化し、不正値は拒否する。保存するのは行の不変ID（`{loc: id}`）だけで、表示キー名の改名では壊れない。欠落はID保持＋診断で、Clone・Playはスナップショット分離する。編集・各Playは独立した `LocalizationStore` スナップショットを持ち、ゲームコードはサービスとストアのコンストラクタ注入で受け、実行中の言語切替はその実行だけに効く。未保存の表がある間はPlayを開始しない。プレビュー言語はEditorが所有し、ツールバーの選択でScene View／Gameの解決に使い、保存しない。ボイス欄は音声基盤までの予約で、文字列のまま保存・編集し、`ResolveVoice`（空は無音）までを提供する。

#### Spriteの素材データ

`PureEngine.Core/Assets/Sprite.cs`の`Sprite`はComponentではなく、Imageから参照する変更不可の素材データ。元画像のProjectローカルID（`Guid ImageId`）と、任意の切り出し矩形（`SourceRect`）を持つ。元の画像データ・ファイルパス・GPUハンドル・表示位置・UIのPivotは保持しない。

`new Sprite(imageId)`は画像全体、`new Sprite(imageId, (x, y, width, height))`は部分領域を表す。矩形は左上原点・X右・Y下の整数ピクセルで、範囲は右端／下端を含まない。空ID、負の原点、0以下の幅・高さは生成時に拒否する。画像をデコードした側が`ResolveSourceRect(imageWidth, imageHeight)`を呼ぶと、全体指定を実寸へ解決し、画像外の切り出しや不正な画像寸法を拒否する。切り詰めやサイズのキャッシュはせず、同じSpriteを複数のImageから共有できる。

切り出しサイズは元画像のピクセル数であり、UIの表示サイズはUiElement／UiLayoutが決める。Image ComponentからSpriteを読む経路と、切り出しをGPUアトラスへ転送する経路は接続済み。画像IDをファイルへ解決する素材索引、Sprite自身の素材ID、Inspector／YAML保存は未実装。前節のImageRefは引き続き保存形式の案で、現在のSpriteが保持するのはGuidのImageIdのみ。Spriteの追加に合わせて汎用参照型やGPU資源管理を新設しない。

#### Image Componentから描画への接続

`RendererComponent`はOrderを保持する共通基底であり、継承しただけで新しい描画形式が登録されるわけではない。現在のScene View描画対象はImageとTextのため、整列時はそのオブジェクトが持つ描画対象のOrderのうち大きい方を取得する。同じSceneObjectにImageとTextがある場合は一単位として扱い、Image→Textの順に描く。独立した順序が必要なら別オブジェクトにする。SpriteRenderer等の描画接続を追加するときは、その描画対象ごとにOrderを扱う。

`PureEngine.Rendering.UiImageRenderer.Draw`は、同じSceneObjectのTransformとUiElementを読み、UiLayoutでサイズと配置行列を計算する。ImageがありSpriteが非nullなら、呼び出し側のID→画像バイト列の辞書から素材を取得し、DrawListへ渡す。Imageなし／Sprite=nullのオブジェクトも配置結果を返すので、画像のない親グループに使える。子にはこの戻り値を渡す。走査順・親子の所属・素材データの所有は呼び出し側の責任で、描画アダプターはSceneを書き換えたりライフサイクルを呼んだりしない。単体描画ではOrderを使わず、配置も変えない。

配置計算済みの描画は`UiImageRenderer.DrawEntry`に集約し、単体の`Draw`と一括走査の両方から使う。一括走査は配置の失敗原因を診断へ残し、失敗した対象の子には親領域を受け渡す。素材解決だけが失敗した場合は、その対象の有効な配置を子へ引き継ぐ。

現在の2D描画は配置行列のXYへの正投影。Quaternionによる変換はUiLayoutで適用した後にXYを取り出し、Z値での奥行き並べ替えはしない。透視変換は明示的に拒否する。編集中Sceneの一括走査（`EditSceneRenderer`）では親子の配置計算を済ませてから、`RendererComponent.Order`の昇順へ安定並べ替えして描く。大きい値を手前にし、負数も許可する。同値は親→子・兄弟順を維持し、Orderは親から継承せず各描画対象の値を使う。ImageとTextが同じオブジェクトにある場合は大きい方のOrderで並べ替える。画像の表示は矩形いっぱいへのStretch。0サイズ・退化したXY変換は描画を省略する。Transform／UiElement不足、画像ID欠落、壊れた画像、切り出し範囲外は例外とし、現在のViewportは既存の停止・診断経路で通知する。ライフサイクルのPriorityとは独立させる。

DrawListのSprite用Imageオーバーロードは、元画像を初回だけデコードし、Spriteの領域を独立したアトラス領域へコピーする。キーは画像ID＋切り出し矩形（全体指定は別キー）なので、同じ画像の異なる切り出しを混同せず、線形補間でも隣のSprite領域を直接参照しない。画像IDのバイト列を変更する呼び出し側はキャッシュを無効化する。EditorのRefreshはVulkanViewportへ無効化を予約し、前回の表示処理を待った次の直列フレーム内でDrawList.ResetAtlasを適用する。CPUアトラスの項目と配置をクリアしRevisionを進めることで、同じIDでも新しい画素をGPUへ再転送する。明示Refreshではアトラス全体を再構築し、GPUデバイスや描画先を作り直さない。元画像サイズはV2と同じく各軸2046ピクセル以下、アトラスは2048×2048に制限する。

`ImageRenderingSample`は検証専用Sceneに通常のTransform・UiElement・Imageを付けて親子を作る。Viewportは表示寸法で毎フレーム配置を再計算する。全体画像・部分切り出し・Color／Alpha・親子回転・右下固定・横Stretch・null Spriteを確認する。背景と説明文字は既存DrawListで描き、Text Component実装とは区別する。

Imageの `Sprite`・`Color`・`Order` はInspector・YAML・Cloneで扱う。`Text` の `Content`・`Color`・`FontSize`・`LineSpacing`・`Order` もInspector・YAML・Cloneで扱い、旧データは既定値（`Content = "New Text"` ほか）として読み込む。`Order` は `RendererComponent` の共通基底に `[Inspector] public int Order { get; set; }` として持ち、既定値は0。旧データは `Order = 0` として従来の表示を維持する。`Sprite` の選択肢はProjectの `Assets/` 索引から作り、None解除と欠落IDの保持に対応する。検証用の画像辞書を制作データの保存先にしない。`Sprite` は素材データのまま維持し、描画順を持たせない。

#### 配置計算の置き場所と入出力

計算は`PureEngine.Core/Components/UiLayout.cs`にまとめる。**UiLayoutはComponentではなく、通常の計算用クラス**。描画API・Avalonia・GPU資源には依存しない。TransformとUiElementはデータを持ち、UiLayoutが両方と親の配置から結果を求める。

最初は1要素の計算を入口にする。入力は「親の未変形の矩形サイズ・親のUI配置行列・自分のTransform・自分のUiElement」、出力は「実際の矩形サイズ・UI配置行列」。Scene全体の走査、素材解決、イベント処理を最初から同じメソッドへ詰め込まない。描画・Buttonのクリック判定・Editorの選択枠は、共通の計算結果を使う。

UiElementは左上原点、X右・Y下の矩形を定義する。親サイズをPとし、各軸で次を計算する。

```text
a = P * UiElement.AnchorMin
b = P * UiElement.AnchorMax
size = (b - a) + UiElement.SizeDelta
anchorPoint = a + (b - a) * UiElement.Pivot
local = Translate(-UiElement.Pivot * size, z=0)
        * Transform.LocalMatrix
        * Translate(anchorPoint, z=0)
world = local * parentUiWorld
```

System.Numericsの行ベクトル規約で合成する。Transform.LocalPositionはAnchorの基準点からのオフセットとなり、Pivotを中心にTransformの回転・拡縮を適用する。Transform.LocalMatrix自体を書き換えたり、計算後の位置をLocalPositionへ書き戻したりしない。既存のSceneObject.WorldMatrixはUIのAnchor／Pivotを含まないので、そのままUIの完成行列として使わず、親の変換の二重適用もしない。

AnchorMin=AnchorMaxなら固定サイズ、異なれば親サイズに合わせてStretchする。例えば親(400,200)、両Anchor=(0.5,0.5)、Pivot=(0.5,0.5)、SizeDelta=(100,40)、Transformが単位変換なら左上は(150,80)。Anchor=(0,0)〜(1,1)、SizeDelta=(-20,-20)、Pivot=(0.5,0.5)なら左上(10,10)、size=(380,180)。親の回転・拡縮は子のAnchor計算後に適用する。

UiLayout.Calculateはnull、非有限値、負の親サイズ、逆転したAnchor、負の解決後サイズ、計算のオーバーフローを例外で拒否する。Anchor／Pivotは0〜1の外側も許し、クランプしない。SizeDeltaは負値可。0サイズ・0Scaleは計算結果として返し、描画／ヒット対象から除く判断は呼び出し側で行う。負Scaleもそのまま合成する。行列にはTransformのZ位置とQuaternion全体を保持し、2D描画での投影・制限は後続の接続時に決める。

#### 表示領域と直近の実装順

親にUiElementがある場合は、親の解決済みサイズとUI配置行列を使う。親にUiElementがない場合は表示領域を基準にする案から始める。この場合の非UI祖先Transformの扱いは、Scene走査を接続するときに確定する。UiLayout単体は呼び出し側から渡された親領域を計算に使い、画面サイズを自分で取得しない。

まずは表示領域を論理座標で渡す。物理ピクセルとの変換・ポインター座標の変換ではDPI倍率を一度だけ適用する。基準解像度へのFit／レターボックスは将来の画面設定の候補であり、現段階の必須Componentや確定済みの画面拡縮方式にしない。幅または高さ0の表示領域では描画／入力対象を作らない。

次に着手する順番は[実装計画](ImplementationPlan.md#次に着手する作業)を参照する。Inspector／素材選択／編集Scene描画／保存の接続に続き、V4前半のグリッド・視点操作・選択・移動Gizmoへ進む。以下は配置計算と描画試作までの進め方を記録したもの。

1. UiLayoutでTransform＋UiElementから矩形サイズと配置行列を求める。
2. GPU不要のチェックで、中央固定・右下固定・横Stretch・全面Stretch・Pivot中心の回転／拡縮・親子追従を確認する。
3. 最小のSpriteデータをImageから参照し、結果をScene Viewで目視する（検証用Sceneで接続済み）。これは配置確認の小さな接続で、素材管理・保存・V4全体の完了とは区別する。
4. 親子保存・ID参照・素材・共通Inspectorを揃え、TextとButtonを接続する。

自動整列、内容に合わせたサイズ変更、スクロール等は別の機能として後続で扱う。UiElementのデータだけでUnityのUI全機能が揃うとは扱わない。

#### 重なり・クリップ・入力との境界

以下は後続機能の設計案。Visible／Opacity／ClipChildrenの格納先やAPIは未確定で、最初のUiLayout計算には持ち込まない。

親子の配置を済ませてから、`Order`昇順へ安定並べ替えして描く。大きい値を手前にし、同値は親→子・兄弟順（深さ優先の走査順）を維持する。後の兄弟の部分木が手前になるのは、同値の場合に限る。同じオブジェクトにImageとTextがあればImage→Textの順。Buttonは独立した絵を持たず、V5で入力を受ける。Visible=falseの部分木は全体を非表示にし、Opacityは祖先との積。これらは描画／入力の設定であり、非表示を理由に既存のUpdateを停止しない。透明でもVisible=true・Interactable=trueならButtonの入力対象になり得る。

ClipChildrenは親のローカル矩形で子孫を切る。Imageは自身の矩形に収まり、Textは自身の矩形でもクリップする。回転／拡縮した親のクリップは変換された矩形のまま扱い、画面上のAABBへの拡大で代用しない。入れ子は全祖先のクリップの共通部分になる。

**V2との接続上の注意：** 現在のDrawListは画面軸に平行なクリップ矩形のみを扱う。V3ではCoreに変換付き矩形のクリップ列を持たせ、点の包含をGPUなしで検証する。V4の描画接続では、描画三角形をこれらの凸矩形でCPUクリップし、UVを補間して既存の頂点バッチへ渡す処理を追加する。軸平行の場合は従来の矩形クリップを利用できる。Stencil／Render Graph／別バックエンドは不要。正確な親クリップが通るまでUI描画の接続完了とはしない。 またV2のDrawListはフォントが同梱Notoに固定されているため、FontRefの保存だけで外部フォントが表示可能になったとは扱わない。V4では素材索引から画像・フォントを解決する接続、フォントID／サイズを含むキャッシュキー、素材変更時のキャッシュ退役も必要になる。V3ではこれらをGPU非依存の参照検証までとする。

V3は共通の座標変換・矩形包含・クリップ包含を用意し、実際の選択と逆順ヒット検索はV4／V5が同じ結果を使う。描画とヒット判定で同じ `SceneViewMath.SortForRender` を使い、クリック時は手前（`Order`降順、同値は後方が手前）から判定する。非可逆変換、表示領域外、Visible=false、クリップ外はヒットしない。ButtonのInteractable=falseはそのButtonを対象外にし、親のButtonを無効にしても別の子Buttonを自動で無効化しない。

#### ボタンとゲームコードの接続

Button自身が `IUiButtonHandler` を実装し、更新境界で受けたクリックを `Clicked` イベントへ渡す。接続と実行の仕様は[実装済みのV5前半](#v5前半game表示とbutton操作)、登録例と現在の接続範囲は[README](../README.md#game表示とbutton操作)を参照する。Text表示は実装済みだがクリック接続には要求しない。ObjectRefによる対象参照は後続の計画。

#### 保存形式・移行・Inspector

シーンの新規保存形式は**version: 2**。オブジェクトに`parentId`（ルートはnull）と`siblingIndex`を追加し、componentsの形式は既存どおり。フラットなobjects配列の順序も保存し、兄弟表示順とは独立に維持する。同じ親のsiblingIndexは0〜件数-1の一意な連続値とし、欠落・重複・負数・範囲外を拒否する。

version: 1は読み続け、全てルート・配列順の兄弟として復元する。次の明示保存で2へ更新し、読み込み時にファイルを書き換えない。旧エンジンへ2の読み込み互換は約束しない。Project manifestは新しい必須項目を足さないためversion: 1を維持する。

復元は、文書構造／ID／親／順序の検証→全オブジェクトの作成→親子接続→既存factoryでcomponent生成／値復元→UIの完成Scene検証→公開、の順。欠落Parent、循環、重複ID、未知typeIdはScene全体を不採用。生成途中の失敗は既存Serializerの逆順Disposeを維持し、編集中のSceneは置き換えない。保存時も同じ構造・UI検証を通す。素材欠落や通常ObjectRefの解決失敗は警告としてIDを保持し、構造エラーと同じ理由で編集データを開けなくしない。

ObjectRef／ImageRef／FontRefはInspectorValueTypesの共通の対応型へ追加し、単体・Nullable・既存の一次元配列／List／stringキーDictionaryの葉で同じ変換を使う。参照型の解決を任意class・任意structの反射シリアライズには広げない（値の入れ子は上記のInspector節の自作クラス範囲で対応し、IDによる参照解決とは分ける）。Inspectorでは対象名＋ID＋欠落状態と選択／解除を用意する。候補は現在のSceneまたはProjectの期待種別のみ。SceneSerializer.CloneとSceneCodeMigratorは同じ保存経路で親子・順序・参照を運び、旧Scene／旧ユーザー型への参照を残さない。

#### 実装順と受入チェック

V3-a〜eは作業単位の識別子。直近はV3-dの配置計算部分を先行し、Imageで確認してからV3-a〜cの保存基盤へ接続する。V3-d全体が完成したという意味ではない。

| 順序 | 実装単位 | GPU不要の確認 |
| --- | --- | --- |
| V3-a | Scene所属・ルート／兄弟順・子孫削除 | 別Scene／混在／循環拒否、失敗時不変、予約削除の重複と付け替え拒否、既存Priority順と単一Dispose |
| V3-b | version 2と参照値型 | v1→v2往復、親が後ろにある文書、同じIDでもClone先へ解決、順序保持、不正構造拒否、コード再読み込み失敗時の旧Scene保持 |
| V3-c | 素材取込・サイドカー・索引 | ファイル対の移動、Project移動、重複／欠落／種別違い、取込失敗、パス脱出／リンク拒否、読取だけで書換しないこと |
| V3-d | Transform＋UiElementとUiLayout、Image／Text／Button | 固定／Stretch／Pivotの数値例、親の回転と拡縮、表示領域／DPI、0寸法。後続の表示・クリップ規則は仕様確定後に追加 |
| V3-e | 共通Inspector・クリック契約 | 参照の設定／解除と保存、組み込み型の再登録、Clickedの複数購読・解除・Clone時の購読分離、Cloneしたボタンの対象が編集用ラベルへ解決されないこと |

Core.Checksへ既存機能の境界を跨ぐチェックを追加し、Editor.Checksは参照編集・未保存表示・再読み込みを確認する。C#実装時は既存`tools/code-quality.ps1 -Check`を実行する。設計文書を追加しただけでは、上記の実装・検証を完了扱いにしない。

### V4前半：Scene Viewの編集操作

**合意した方針：データ・計算はXYZ、今回のGizmo操作はXY。** TransformのLocalPosition／LocalScaleはVector3、LocalRotationはQuaternion、UiLayoutの結果はMatrix4x4のまま扱う。2D編集のためにComponentをVector2へ置き換えたり、Zを消したりしない。

工程と完了条件は[実装計画](ImplementationPlan.md#次に着手する作業)に集約する。2026-09-23にV4前半の5項目を実装・レビュー修正し、自動検証と実GPUチェックを通過した。実画面は表示を確認し、一連の手動操作とCIは未確認として区別する。検証の証跡は[実装計画](ImplementationPlan.md#v4前半のscene-view編集操作2026-09-23)を参照する。

- 現在の表示はXYへの正投影で、Z単独の変更は画面位置や描画順を変えない。Zハンドル・3D視点回転・透視投影・奥行き判定は今回実装しない。これらが必要になった段階で、カメラ・投影・選択判定を一緒に設計する。
- Editorがパン位置とズーム倍率を所有する。Scene・Component・シーンYAMLへ保存せず、実行用SceneやGameの視点にも流用しない。初期値はパン0・ズーム1とし、現在の左上原点／X右・Y下を維持する。
- UiLayoutで計算した配置へ最後にEditorのビュー変換を合成する。Scene座標をs、論理表示座標をpとして`p = s * zoom + pan`。ポインターは逆変換する。DPIは論理座標とGPUターゲット寸法の境界で一度だけ適用し、二重に掛けない。
- Anchor計算用のルート領域はパン／ズームで変更しない。UiLayoutへ渡すサイズをzoomで割ってStretchのサイズを変える実装は避ける。実際の表示領域リサイズ時の配置更新と、Editorだけの視点移動を区別する。
- グリッドと原点はEditorの表示要素。ゲームのComponentや保存データにはしない。倍率に応じて線の間隔／本数を調整し、極端な拡大縮小で過密描画や無制限の列挙を起こさない。グリッドは今回表示のみで、スナップは行わない。
- 中ボタンドラッグはパン、ホイールはポインター直下のScene座標を保持してズームする。倍率には有限の上下限を設ける。サイズ0・非有限値・変換不能では操作を開始しない。
- 画像の選択は描画と同じUiLayout結果と`Order`並べ替えを使い、描画順の逆順（手前から）で矩形をヒット判定する。回転・拡縮・Pivot・表示領域のクリップを反映する。初期は矩形判定とし、画像の透明ピクセル単位の判定はしない。Spriteなし／描画対象外の画像を空白クリックで拾わない。Stuffsからは画像のない有効なUIグループも選択できる。
- 選択状態は既存Stuffs／Inspectorへ接続し、第二の独立した選択リストを作らない。選択枠は変形後の四隅、PivotはUiLayoutの同じ行列から算出する。Transformのみのグループ親は矩形の代わりにワールド原点をPivotとしてGizmoを出し、その変換は子の配置へ受け渡す。UiElement付きで矩形が潰れた配置や非可逆なXY変換では、選択を維持してもGizmo操作を無効にする。
- 移動Gizmoは選択対象のPivotに表示し、画像より先にヒット判定する。矢印・中央ハンドルの見かけのサイズと操作判定の幅は画面の論理ピクセル基準で保ち、zoomと一緒に小さくしない。X／YはLocalPositionを書き換える軸、すなわち親のUI配置で変換されたローカルXY軸として表示する。自身の回転によって移動軸をさらに回さない。
- ドラッグ開始時のLocalPositionと親のUI配置を保持し、マウスのScene座標差を親のXY変換の逆行列でローカル差分へ戻す。X／Yハンドルでは対応軸だけ、中央ではXY両方を変更し、LocalPosition.Z・Anchor・Pivot・SizeDeltaは維持する。非ゼロZの対象でも同じ規則とし、確定・キャンセル・保存・CloneでZを失わない。小刻みな加算の積み重ねではなく、開始値から算出する。
- ドラッグ中は見た目とInspectorへ反映するが、確定した変更だけを未保存化する。クリックだけで位置が変わらなければ未保存化しない。キャンセルでは開始値と開始時の未保存状態を維持し、既にあった他の編集を保存済み扱いにしない。
- Pointer captureを使い、開始したポインターだけを領域外でも追跡する。他のボタンを離しても確定しない。Esc・Scene View内のフォーカス喪失・ウィンドウ非アクティブ化・キャプチャ喪失で位置ドラッグをキャンセルする。確定／中断時は操作状態を先に消してから捕捉を解除し、CaptureLostの再入を防ぐ。パンの終了／中断では制作データを変更しない。Composition描画面のホストには透明な背景を設定してAvaloniaのヒットテスト対象にする。
- 保存・シーン切替・Play開始・コード再読み込みの採用前とEditor終了時には、進行中の位置ドラッグをキャンセルしてから既存操作へ渡す。対象が削除された場合は操作参照を破棄し、削除済み／旧Sceneのインスタンスを後から書き戻さない。開始時のComponent参照・親配置行列／サイズ・Anchor／Pivot／SizeDelta・回転／拡縮・表示領域サイズを移動時と描画時に照合し、条件が変わった場合も古い逆行列で続行しない。
- Fは有効な選択対象の変形後の矩形を余白付きで表示領域へ収める。Scene Viewにキーボードフォーカスがあり、ドラッグ中でない時だけ扱い、Inspectorの文字入力や数値入力から奪わない。0サイズ・非可逆なXY変換・未選択では安全にno-opとする。
- Scene View内の編集入力だけを処理し、Inspector・Project・Gameへドラッグやショートカットを漏らさない。Play中の配置編集禁止を維持し、編集中にゲームのStart／UpdateやButtonイベントを呼ばない。

描画・入力・Gizmoで別々の座標式を持たず、UiLayoutの配置結果と共通のビュー変換を使う。既存のRendering／Avalonia interop・未保存管理・保存／Cloneを再利用する。複数選択、汎用Undo／Redoは後続とする。

実装はCoreの`Scenes/SceneViewMath.cs`（ビュー変換・配置列挙・ヒット判定・親逆変換・F表示の共通計算）、Renderingの`SceneViewOverlay.cs`（グリッド・選択枠・Gizmoの表示要素）と`EditSceneRenderer`／`UiImageRenderer`のビュー合成、Editorの`Windows/MainWindow.SceneView.cs`（パン／ズーム・選択・Gizmo・F・中断と未保存管理）に置く。Headlessの移動操作口は本番と同じ開始・更新・確定処理を呼ぶ。加えてHeadlessのマウス・キー入力から実際のヒットテストとイベント経路を検証し、計算のみのテストと区別する。

### V4後半：サイズ変更・回転ハンドル

**合意した方針：サイズ変更は `UiElement.SizeDelta`、回転は `Transform.LocalRotation` のみを書き換える。** 移動Gizmoと同じ単一ドラッグ・確定管理を使い、対象外の値は維持する。`Transform` のみのグループ親には出さず、移動のみのまま残す。

- 有効な `UiElement` 枠（変形後の四隅）を持つ選択にだけ出す。枠の四隅と辺中央の8個の正方形（画面の論理ピクセル基準）と、上辺中央から28px上の回転ハンドル（接続線＋ひし形）を描く。押下時のヒット優先度は回転→サイズ変更→移動Gizmo→選択とする。
- サイズ変更はAnchor・Pivot・回転／拡縮・位置を維持し、`SizeDelta` だけを変える。マウスのScene座標差を配置全体のXY変換の逆行列で戻し、Pivot相対のゲイン（ドラッグ隅側 `1 - Pivot`、固定側 `-Pivot`）で除算する。四隅は両軸、辺は対応軸だけを変え、使わない軸は0に置く。Pivotちょうどに重なる辺は固定され、軸合わせのドラッグでは反対軸だけが動く。解決サイズは軸ごとに0以上へクランプし、0に潰れてもドラッグは継続できる。自身と親の回転・拡縮があってもドラッグ隅はポインターに追従する。
- 回転は位置・サイズを維持し、`LocalRotation` だけを変える。開始PivotのScene位置を基準に、開始ポインターと現在ポインターの角度差Δを求め、`Normalize(Qz(Δ) * 開始回転)` を合成する。既存のX／Y傾きはリセットせず残す。Pivot自体が回転で動くため、ハンドルへの完全な追従ではなく開始値基準の近似になる。特異なワールド・非有限値・極小半径では開始しない。
- 未保存・中断・禁止の規則は移動ドラッグと共通にする。ドラッグ中は見た目とInspector（位置・回転・サイズ欄）へ反映するが、確定した変更だけを未保存化する。Esc・フォーカス喪失・キャプチャ喪失・保存・シーン切替・Play開始・コード採用の前には位置・サイズ・回転の開始値を戻す。対象削除時は書き戻さない。サイズ変更ではAnchor・Pivot・回転／拡縮・親配置の変化を、回転ではAnchor・Pivot・サイズ・拡縮・親配置の変化を検出して中断する。Play中の配置編集禁止を維持する。
- スナップ・複数選択・汎用Undo／Redo・Zハンドルは含めない。`Transform` のみの親の回転ハンドル、サイズ変更中の反対隅の固定も対象外とする。

実装はV4前半と同じ3ファイルに置く（`SceneViewMath` のハンドル配置・ヒット判定・サイズ／回転の共通計算、`SceneViewOverlay` のハンドル表示、`MainWindow.SceneView` の開始・更新・確定・中断とInspector同期）。Headlessのサイズ変更・回転の操作口は本番と同じ処理を呼び、実際のマウス入力からのハンドル経路も検証する。

### V5前半：Game表示とButton操作

**合意した範囲：Game表示とButton操作まで。** Text・ObjectRef・InputField・サイズ変更・回転Gizmo・単体Player配布は追加しない。この達成だけでV3〜V5全体を完了扱いにしない。検証の証跡は[実装計画](ImplementationPlan.md#game表示とbutton操作2026-09-23)を参照する。

- GameはPlaySessionの実行用Sceneを描く。Scene Viewは編集用Sceneの表示を維持し、Play中の編集禁止を守る。実行中のTransformやImageの変更は次のフレームの配置再計算でGameへ反映する。描画は配置と画像投入だけを行い、Start／Updateを呼ばないため更新の二重実行は起きない。Gameタブ非表示時は描画・入力を止めるが、Playタイマーは独立しているためゲームの進行は維持する。Sceneの読み取りと更新はどちらもUIスレッドで直列化し、GPU側へは確定したDrawListを渡す。
- Buttonは普通のComponent（`PureEngine.Core.Button`、typeId `core.button`）とし、TransformとUiElementで領域を決める。見た目は同じオブジェクトのImageを使う。`Image` も `PureEngine.Core` に置き、Avalonia.Controls.Button／Imageとの衝突はEditor・テスト側で完全修飾と `using` エイリアスにより明示する。`Interactable = true` を既定とし、Inspector編集・保存・復元・Cloneへ接続する。一時的な押下・ホバー・フォーカス状態はComponentに持たず、保存やCloneで引き継がない。Add Componentの検索（"button"で一致）・既存factory・重複防止を使い、Transform／UiElementの不足は既存の `UiComponentRequirements`／Inspector警告で知らせる。Imageは必須にしない。
- 状態表示は保存済みの `Image.Color` を書き換えず、実効色の読み取りと重ね描きで行う。通常はそのまま、ホバー・押下・無効はそれぞれ白・黒・灰色の半透明矩形を重ね、キーボードフォーカスは紫の枠を描く。ImageのないButtonもヒット対象とし、フォーカス時は枠だけを描く。
- 入力は配置計算・座標変換・Orderによる順序を描画と共有する（`SceneViewMath.SortForRender`／`HitTest`、`UiLayout`）。Gameにパン／ズームはなく、Scene Viewの視点は影響しない。親の回転・拡縮は逆行列で戻し、DPIは論理座標とGPUターゲット寸法の境界で一度だけ適用する。表示領域のクリップ（領域外は対象外）を考慮し、見た目と判定を一致させる。重なったButtonは手前（Order降順、同値は後方が手前）の1つだけが入力を受ける。左ボタンで押し始めたButton上で離したときだけ1回通知し、外で離した場合はキャンセルする。ポインターキャプチャを使い、フォーカス喪失・キャプチャ喪失・タブ切替・無効化・削除・Stopで押下状態を解除する。Tab／Shift+TabはOrder昇順（描画順の背→手前）で移動し、Enter／Spaceでも操作できる。キーボードの長押しリピートは抑止する。他の起動キーの解放やTab移動では再発火させず、ポインター押下中のキー起動は受け付けない。Tab候補から画面外・退化変換・描画失敗のButtonを除き、無効化・削除時の押下とフォーカスは更新後に解除する。`Interactable=false`・0サイズ・判定不能な変換は対象外にする。親Buttonの無効化だけで子Buttonを無効化しない。ImageのないButtonも操作でき、ButtonでないImageはGame入力を遮らない。未実装のVisible／ClipChildren等は考慮せず、実装済みとして扱わない。
- Button自身が `IUiButtonHandler.OnClick(UiClickContext context)` を実装し、`event Action<UiClickContext>? Clicked` を発火する。Runtimeは同じSceneObjectから別のhandlerを検索せず、Button自身へ入力を届ける。購読なしは何もしない。複数の購読は通常のC#イベントとして登録順に呼び、解除は `-=` を使う。イベントの購読・メソッド名・デリゲートは保存・Cloneせず、Playごとに実行用Buttonへ登録する。入力は `SceneRuntime.Step` 内のStart済みバッチの後・Updateの前で処理し、Start前や停止後のEnqueueは捨てる。購読先の初期化と解除は登録側が管理し、別オブジェクトの購読先が削除される場合はDestroy等で解除する。購読先のStart待ちや自動検出は行わない。クリック中のButton削除・Stopは残りの入力を捨てるが、実行中のイベント通知は通常どおり完了する。購読処理が例外を投げると後続の購読は呼ばず、`Button.OnClick` のエラーとしてConsoleへ報告して安全に停止する。contextには実行用SceneとButtonのSceneObjectを渡す。

## 単体実行とゲーム配布

- 初回の配布対象はWindows x64のself-containedフォルダ。Playerと.NETランタイム、描画のネイティブ依存、ゲームDLL、データ、ライセンス表記をまとめる。単一exe化・Native AOT・トリミングは行わない。操作・制作側の前提は[README](../README.md#ゲームのbuildと配布)を正本とする。
- PlayerはEditorアセンブリに依存せず、配布データとコンパイル済みゲームDLLから起動シーンを復元する。実行時コンパイルや元プロジェクトへの参照は行わない。作業ディレクトリではなく、配布物の位置を基準にデータを解決する。
- Coreの保存形式・型ID・ライフサイクルを維持し、Runtimeのサービス寿命とPlaySession、RenderingのGame描画を再利用する。組み込み型・ゲーム側のサービス登録・画像IDの解決規則をEditorとPlayerで共有し、既存のサンプル型も互換性を保つ。
- 配布用manifestはゲーム名、起動シーン、ゲームDLL、安定した型IDから型名への対応を保持する。画像の登録情報、Prefab・DataAsset・LocalizationのIDも保持する。ゲームDLLと対応表は同じコンパイル結果から生成する。
- Buildは保存した制作データのスナップショットから行う。ソースコードや制作キャッシュは配布せず、対応するゲームデータを明示的に収集する。初回は参照到達性による未使用データの除去を行わない。コンパイル・データ検証・publishが失敗した場合は成功扱いにしない。
- 出力は専用の一時フォルダで完成させてから確定する。出力先と入力側の重複やリンクを拒否し、前回の正常な配布物と無関係な既存フォルダを保護する。Build & Runもこの配布経路の成果物を起動する。
- 実装・自動検証・実GPU・.NET未導入環境での確認は区別し、結果を[実装計画](ImplementationPlan.md)に記録する。

## マルチプレイ・Steam：将来の構想

### 複数インスタンスでの検証

- Editorで人数を選び、ホスト1つと複数クライアントをまとめて起動・停止する。
- 各ゲームは別プロセスで動かし、役割・接続先・テスト用プレイヤーIDを起動時に渡す。
- ログをホスト・各プレイヤーごとに識別する。
- ローカル接続とSteam接続の検証を切り替え、同じゲームルールを使う。
- 最初の目標は、2つのゲーム画面を同時起動し、一方のボタン操作の結果がもう一方に反映されること。

ローカルの複数起動は、Steamの複数アカウント接続の検証とは分けて扱う。

### Steam連携

- ロビー作成・招待・参加・退出を扱う。
- 参加者一覧、準備完了、ゲーム開始の流れを支援する。
- 通信するのはUIそのものではなく、プレイヤーの操作と確定したゲーム状態。
- ホストが操作を検証し、全員に公開する情報と、本人だけに見せる手札などを分けて配信する。

## Editorの見た目と操作

- Godot風の濃い青灰ダークを基調とし、独立したペインの間に隙間を設ける。色トークンは `App.axaml` の `Application.Resources` に集約し、各ウィンドウは参照する（ハードコードしない）。
- ベース `#1A1C20`、ペイン面 `#23262C`、カード `#2E333B`、入力欄 `#333842`、枠線 `#3A3F47`、文字は `#E8EAED` / `#B8BDC5` / `#7D848F` の3段。
- 構造アクセントは紫 `#8B7CF6` に1色化する。Playボタン・Startupピル・フォーカス枠・選択背景（ウォッシュ `#2E2A4A`）に使う。Info／Warning／Errorは青 `#7AB5F0`／黄 `#E0B45A`／赤 `#E06A5A`。
- タブ文字は左揃え。すべてのペインで同じタブ表現を使う。
- ペインの枠線は1pxで常に枠線色とし、フォーカス中もアクセント紫に変えない。
- タブにはホバーによる色変化を付けない。
- 操作部品の角丸は4、カード・タイル・ピルは6に統一する。ダークでは効かないドロップシャドウは付けない。
- アイコンは `src/PureEngine.Editor/Resources/Icons.axaml` に集約し、各画面は `PathIcon` + `StaticResource` で参照する（インラインの `Path Data` や文字グリフ `▶■✕🗑` を置かない）。図形は Fluent UI System Icons（MIT）の Filled・20px 系に統一し、由来とライセンスは同ファイルのコメントに残す（C# 文書のみ 16px）。色は構造アクセント紫と文字色の範囲に収め、アイコンごとの多色化はしない。一括 Clear には `Icon.Delete`、List・Dictionary の追加には `Icon.AddSquare`、Set Null（null化）には `Icon.BorderNone`、nullからの生成（Create）には `Icon.Compose` を使う。折りたたみトグルは `Icon.TriangleDown`（展開時）／`Icon.TriangleRight`（折りたたみ時）、ツリー（Stuff・Project）の展開矢印は `Icon.ChevronRight`（折りたたみ時）／`Icon.ChevronDown`（展開時）を使う。
- トップバーはFileメニュー＋未実装のEdit／View（無効表示）の正規メニューとし、ダミーのテキスト表示は置かない。Playはアクセント塗りのプライマリボタン、Stopは通常ボタンで幅を揃える。
- 下端はペイン面のステータス帯とし、シーン状態の文言を表示する。エラー時はエラー文字色にする。
- InspectorのComponentカードはヘッダー（折りたたみ・名前・Priority）＋内容の構成とし、折りたたみ状態は選択切替をまたいで保持する。カードの枠線はホバー・フォーカスで変えず、入力欄自体のフォーカス表示だけ残す。
- PriorityのS／U／D表示はライフサイクルごとの地色を持つ角丸チップ＋色文字とし、ヘッダー右にCompactに並べる。
- TransformのPosition／Rotation（QuaternionはX／Y／Z／W）／Scaleの各軸値にも同じ角丸チップの軸バッジを付ける。X赤・Y緑・Z青、Wは中性色。
- Vector2／3／4（Nullable含む）の各成分も同じ軸バッジ付きとし、軸行の入力欄は幅を等分してペイン幅に収める。QuaternionのWがはみ出さないようにする。
- 折りたたみ操作子は通常のButtonとし、ToggleButtonのchecked時テーマ塗りが残らないようにする。
- カード内はメンバー行ごとに区切り線を入れ、Transform・List・Dictionaryなどの背の高いエディタの境界を明確にする。
- List・Dictionaryの要素一覧も折りたたみ可能とし、件数と操作ボタンを持つヘッダーは残す。折りたたみ状態はメンバー単位で保持する。
- カード内の件数メタ行は出さない。編集項目もPriorityもない場合だけ最小の案内を残す。
- Stuffsの見出し行は最小高さ24px、右クリックメニュー項目は26px。メニューの外側に左右の余白を設ける。
- Stuffsは親子をツリー表示する。子は親の下に並び、折りたたみ・展開と名前の即時反映を行う。行の中央へのドロップは子付け、上下端へのドロップは前後への並べ替え、余白へのドロップはルート化とする。自分自身・子孫へのドロップとPlay中の付け替えは拒否する。詳しい操作手順は [README](../README.md) を正本とする。
- 展開状態は`HierarchyNode`と実際の`TreeViewItem`で双方向同期し、名前は`SceneObject.Name`へ直接バインドする。階層用とComponent用のD&Dはデータ形式で分離する。挿入表示と位置判定は子の表示領域を含まない見出し行に限定し、ホバー計時は同じ対象・位置の間は継続する。キャンセル・Scene再構築・終了時は表示とタイマーを解除する。
- オブジェクト操作はStuffsの右クリックメニューから行い、追加・削除ボタンを常設しない。Add Emptyは選択中があればその子、なければルートに追加する。
- UI作成メニューは既存のTransform・UiElement・Image・Buttonを組み合わせ、当該Projectの登録表と編集用factoryで生成する。別のUI型や保存形式は追加しない。必要なComponentの生成に失敗した場合は新規オブジェクトを取り消し、選択・未保存状態を維持する。
- 空のペインに操作案内文を置かない。
- ペインの幅・高さをドラッグで変更できる。起動時はタイトルバーのある最大化ウィンドウ。

## 共通ログとConsole（2026-09-23に合意、実装済み）

- `PureEngine.Core` のstaticクラス `Log` で `Info`／`Warning`／`Error` の3種を扱う。呼び出し側にLogger生成・DI登録・継承を要求しない。`using PureEngine.Core;` だけで呼べる。
- `Log.Error` は記録だけを行い、例外送出やPlay停止をしない。Critical／Debug／Trace、Errorでの一時停止は今回の範囲に含めない。
- 本文・レベル・時刻・呼び出し元のファイル・行番号・メソッド名を保持する。呼び出し元はCaller属性で自動取得し、オーバーロード間の転送でLog内部に置き換わらない。通常ログではスタックトレースを毎回取得しない。
- 例外を渡した場合は内部例外とスタックトレースを含む詳細を保持する。Runtimeエラーの発生箇所は例外側の情報を使い、ログ転送処理の行を原因箇所として扱わない。
- CoreはEditorやAvaloniaに依存しない。別スレッドからも安全に呼べる。ログ出力側はUIを直接操作せず、Editorが有界キュー（1000件）から定期的にまとめて取り込む。受け手がなくても失敗せず無制限に増えない。外部ログライブラリや汎用プロバイダー機構は追加しない。
- Editorの下部ペインにConsoleタブを置き、既存のデザインとレイアウトに合わせる。時刻・種類・本文の先頭を一覧し、Info／Warning／Errorの表示切り替えと件数、本文検索、選択時の全文・発生箇所・例外詳細とコピー、Clear、Clear on Play（初期ON・Start前実施）を備える。フィルターは表示だけに適用し、自動スクロールは過去ログ読み中は位置を奪わない。表示履歴にも上限（1000件）を設け、キューと履歴の破棄件数を表示する。Collapse・ファイル保存・外部エディターへのジャンプは今回の範囲に含めない。
- Play開始・停止・準備失敗・更新失敗・終了失敗をConsoleで確認できる。`SceneRuntime.Errors` は全件取り込み、同じエラーをStep／Stop／Disposeで重複出力しない。Object名・Component型・ライフサイクル名を残す。既存のステータス表示と終了エラー時の残留、Stop後の可読、閉鎖時の購読解除を維持する。

## 実装状況と次の範囲

進捗・未実装の一覧・次の作業・検証結果は [実装計画・進捗](ImplementationPlan.md) に集約する。
この文書は設計仕様を扱う。仕様が書かれていることと、その機能が実装・検証されていることは区別する。


## ProjectのC#再読み込み

RoslynでProject内のソースを一括コンパイルし、collectible AssemblyLoadContextへ読み込む。型IDの初期値は `user.`＋FullNameだが、以後は `.pureengine/types.json` に永続化し、完全名と相対ソースパスの対応を更新する。publicの具象・非genericクラスを対象にし、1ファイルの複数対象は未アタッチ分をまとめて追加する。補助クラスはinternal等で区別する。

監視通知は250msまとめ、UIスレッドで適用する。Play中・ファイル操作中・Inspector入力エラー中は保留する。旧Registryで編集データを保存形式へ取り出し、新Registryで別Sceneへ復元する。ID・名前・対応するInspector値・Priorityと選択を維持する。メンバーの追加・改名・削除時は上記Inspector節の規則で初期化／移行し、シーンを未保存にする。それ以外は旧未保存状態を維持する。アタッチ済みの型の削除、対応するメンバーの互換性のない型変更、Priorityの移行不能では切替自体を拒否し、旧コードとデータを保持する。生成途中の失敗は既存Serializerの逆順解放を使う。サービス登録を含む変更では、候補登録からの候補サービス群で移行し、成功後に編集Sceneと編集用サービス群を入れ替える。登録の曖昧・不正・登録中の失敗、依存解決の失敗も移行失敗と同様に旧状態を維持し、候補の Component・Scope・provider を解放する。旧状態の採用後は旧 Component を先に、旧 Scope・provider を後に解放する。

プロジェクト単位の増分状態（`UserCodeIncrementalCompiler`）は起動時のコンパイルから保存監視へ引き継ぐ。未変更の構文木と参照DLLの `MetadataReference` を再利用し、前回の `CSharpCompilation` へ追加・削除・置換だけを適用する。ファイル内容・参照集合・設定が前回と同じ場合はコンパイル・読み込み・移行を省略し、保留中の反映待ちを維持する。実行は最大1件、待機は最新1件に集約し、古い実行へキャンセルを通知する。DLLは引き続き全体を生成して差し替え、ファイル間の型検査の省略やHot Reloadは行わない。

Project開始時も候補Registryで起動シーンの読み込みが成功してから公開する。候補登録からの編集用サービス群で復元し、成功したサービス群を Editor へ渡す。失敗時は候補の Component・Scope・provider を解放し、既存の登録を維持する。終了時は監視とインスタンスを解放し、ユーザー型の登録を外してALCのUnloadを要求する。Coreの型キャッシュはConditionalWeakTableを使い、古いユーザー型を静的キャッシュで保持し続けない。ユーザー自身のstaticイベント購読等の解除はDispose側の責任。

外部NuGet・独自csproj設定、実行状態を維持したPlay中の差し替えは対象外。起動時とコード再読み込みのコンパイルはバックグラウンドで行う。採用時はUIスレッドで世代と所有者を確認し、その時点のSceneを新コード用のサービス群へ移行する。Play・ファイル操作・入力エラー中は採用を保留し、古い結果・終了後の結果は解放する。無変更の保存では診断の再記録や再移行を行わない。


## 外部エディターのC# Workspace

ProjectSessionのCreate/Open/OpenAsyncでProjectCodeWorkspace.Ensureを呼び、net11.0のPureEngine.Game.csprojを生成する。起動中EngineのPureEngine.Core.dllとDIライブラリを参照し、PureEngine.Analyzers.dllをAnalyzerとして登録する。再Openで参照パスを更新する。生成コメントがない既存csprojは上書きしない。他名のcsprojがある場合も重複生成しない。

global.jsonはEngineビルド時に埋め込んだSDK設定を不足時のみコピーする。ゲームも.NET 11を使用し、Zed等の言語サーバーから同じSDKを解決する。Zed固有のユーザー設定は変更しない。これは編集用メタデータであり、RuntimeのRoslynコンパイルは引き続き独立している。

ゲーム用csprojのLangVersionは、組み込みRoslyn 5.xに合わせて15.0とする。安定版NuGet（5.9.0）にLanguageVersion.CSharp15がまだ無いため、Editor内コンパイルはPreview設定でC#15相当として扱う。診断DLLは同じSDKとC# 15でビルドし、外部エディターのRoslynが.NET 10等で動作する場合にも読めるようnetstandard2.0を対象とする。

既存のsln/slnxがなければPureEngine.Game.slnxを生成し、Roslynの自動読み込みの入口にする。TestProjectでcsproj単体よりもソリューション経由の読み込みが必要だったため、両方を用意する。

### ライフサイクルのIDE0051抑制

[LifecycleUsageSuppressor](../src/PureEngine.Analyzers/LifecycleUsageSuppressor.cs) は、メソッドに付いたStartAttribute・UpdateAttribute・DestroyAttributeを型シンボルで識別し、IDE0051だけを抑制する。短い属性名の文字列比較ではないため、別の名前空間の同名属性は対象外で、別名や完全修飾名は同じ属性として扱える。

通常の未使用メソッドの診断は維持する。ゲームコードへの呼び出し追加・書き換え・pragma挿入は行わない。CA1822等の他の診断やComponentSchemaによる不正なライフサイクル宣言の拒否は変更しない。手動管理のcsprojでは利用者がAnalyzer参照を追加する。

### リポジトリのコード品質

規約の正本は [.editorconfig](../.editorconfig)、作業手順は [AGENTS.md](../AGENTS.md)。namespaceの名前・有無・宣言形式は維持する。採用した診断はビルドでも検査し、[code-quality.ps1](../tools/code-quality.ps1) で提案レベルの診断・ビルド・既存チェックを一括実行する。static化や引数変更はリフレクション利用を確認して個別対応する。ゲーム用IDE0051抑制とリポジトリ全体の品質設定は別の適用範囲を持つ。


## クラスの改名と永続ID

候補コンパイルで旧IDとの対応を解決し、Sceneの復元成功後、型登録の公開前にID管理ファイルを一時ファイル経由で保存する。移行・生成失敗時はID管理も旧状態に残す。完全名一致を先に予約し、同じソースファイルの一意な短いクラス名、残った旧新1対1の順で解決する。多対多の場合は推測しない。型が消えた場合もID記録を残して再利用を避ける。

初回導入時は保存シーンのuser.* IDを集め、完全名または一意な短いクラス名が一致すれば引き継ぐ。型IDの管理ファイルもプロジェクトの保存対象とする。ファイルとクラスを同時に改名する場合や多対多の改名は自動推測せず、変更を分ける。メンバー改名は上記Inspector節の初期化／任意の旧名属性で扱い、型変更に対する既存の保護は維持する。

## Editorの所有と実行接続（A1〜A5統合済み）

EditorはMVVM構成とし、`EditorViewModel` が各ペイン、Play、コード反映を接続する。`EditorDocuments` が編集文書を所有し、`UserCodeReloadCoordinator` がScene・単体DataAsset・表編集の候補移行を接続する。MainWindowにはControl生成、ネイティブ入力、ダイアログ、描画ホストとモデルへの接続を残す。モジュールの責務と検証は [EditorMvvmMigration.md](EditorMvvmMigration.md) を参照。

依存方向はEditor → Runtime → Core。ProjectComponentsがプロジェクト単位の型登録・ソース対応・採用中コードを持つ。ComponentAssetsは状態を持たない共通処理のみを提供する。ProjectSessionは起動Scene・編集用サービス・ProjectComponentsと増分コンパイル状態を所有し、EditorViewModelへ引き渡す。

編集状態と編集用サービスはEditSceneStore、保存・Play・再読み込みの可否判定はEditorOperationGate、コード採用と旧資源の解放はUserCodeReloadCoordinatorが担当する。PlayViewModelは実行Session、CompilationViewModelは要求世代・候補を所有する。再読み込みは候補Registry・サービス・移行先Sceneを準備してから採用し、旧Component → 旧サービス → 旧コードの順に解放する。終了時もこの所有順に従い、Viewの購読とタイマーを解除する。

RuntimeのGameSession.CreateとPlaySession.PrepareはAction<IServiceCollection>を受け取る。EditorはGameServices.ForProjectから組み込み＋当該プロジェクトの登録を渡し、UIなしの実行側はゲーム側の登録処理を直接渡せる。完了条件と検証は[ArchitectureImprovements.md](ArchitectureImprovements.md)を参照。
