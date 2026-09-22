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
- **ゲーム実行部分（設計方針）**：C#15製のVulkan描画・入力と既存Runtimeを接続し、EditorのGameと単体実行で描画処理を共有する。実装・実機検証は未着手。
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
現在のオブジェクトIDは `Guid`。親子関係の具体的なAPI、循環の扱い、親削除時の子の扱いは未決定。

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

以前の会話で使った `[End]` は、現在の方針では `[Destroy]` に置き換わっている。
Destroyは削除時の処理であり、Stop時も実行用Sceneの破棄に伴って呼ぶ。編集用Sceneは破棄しない。

現在の4属性の定義は `AllowMultiple = false`、`Inherited = true`。
同じ属性を同じメンバーに重複して付けることはできない。
継承・オーバーライドされたメソッドは同じライフサイクルを二重に呼ばないよう列挙する。

### Inspectorと保存

- `[Inspector]` の付いたフィールド・プロパティだけを表示・編集・シーン保存する。
- 属性のないメンバーはInspectorにも制作データの保存対象にも含めない。
- 別の `[Serialize]` 属性は設けない。
- 実行中に変化した値を、制作データへ自動で書き戻さない。
- 非publicメンバー、readonly、読み取り専用プロパティは対象外。対応する値の型は下記の範囲とする。

対応する値の型（Coreの `InspectorValueTypes` で検証・変換し、Editorは同じ範囲を表示する）：

- scalar：string・int・float・double・bool、対応する `Nullable<T>`（空欄＝null）
- enum：通常のenumと `[Flags]` enum、対応する `Nullable<T>`。通常はドロップダウン、`[Flags]` はチェックボックス群とNoneクリアで編集する
- `[Flags]` は複合値・符号付きの負値・`ulong` の最上位ビットにも対応する。チェック状態の同期は表示のみを更新し、ユーザー操作として値へ書き戻さない。
- ベクトル：`Vector2`・`Vector3`・`Vector4`・`Quaternion`（各成分は有限のfloat、対応する `Nullable<T>` を含む）
- `Transform`：null可の参照型。`LocalPosition`・`LocalRotation`・`LocalScale` を入れ子で編集する
- 配列・リスト：`T[]`・`List<T>`（`T` はstring・int・float・double・bool・enum・ベクトル4種と `Nullable<int/float/double/bool/enum>`、null可）
- 辞書：`Dictionary<string, TValue>`（`TValue` は配列・リストの要素と同じ範囲、キーはstringのみ、null可）

`Transform` 自体も `[Inspector]` 付きの組み込みコンポーネント（typeId `core.transform`）として保存・編集する。配列・リスト要素や辞書値に `Transform`・コレクションの入れ子・`Dictionary` のキーにstring以外は含めない。詳細なYAML形式は下記のYAML節を参照。

ライフサイクルのPriorityはエンジン側のアタッチ設定として表示・保存するもので、ゲーム側メンバーの `[Inspector]` 指定とは別に扱う。

Inspectorメンバーの改名・削除は追加の属性なしで許可する。保存データのvaluesにだけ存在する項目は無視し、新しい名前のメンバーはコンストラクター／フィールド初期化子の値を使う。同名の対応項目は保持する。読み込み・C#再読み込みで項目の追加・改名・削除を検出したEditorはSceneを未保存にする。次のシーン保存では現在のメンバーだけを書き出し、古い項目をYAMLから削除する。未読込の他シーンは一括で書き換えない。

`[FormerlySerializedAs]` は改名前の値も保持したい場合の任意指定。Coreの共通名解決を通じ、保存シーンの復元とC#再読み込みの両方で旧名を現在のメンバーへ対応付ける。Inspectorの表示と新しい保存データは現在の宣言名を使い、YAMLのversionは1を維持する。使い方はREADMEの「Inspectorメンバーを改名する」を参照。

- 名前の比較は大文字小文字を区別する。空名・前後に空白のある旧名、別メンバーの現在名または旧名との衝突は拒否する。
- 一つの保存データに新旧両方の名前、または同一メンバーを指す複数の旧名がある場合は、優先順位で上書きせず復元を拒否する。
- C#再読み込みでは対応する名前がある場合のみ従来の型互換性検査を行い、失敗時は旧コードとSceneを保持する。属性なしの改名は新メンバーとして初期化する。これはメンバー名の移行であり、enum型名・enum定数の改名には適用しない。
- 旧名の属性は、未変換のシーンやバックアップを読み込むために保持する。`[Inspector]` 対象外のメンバーに付けても表示・保存対象にはならない。

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

シーンは1シーン1ファイルのYAML（`.pure.scene.yaml`）で保存する。現在の形式は `version: 1`。
制作データをそのままゲーム進行のセーブデータとして扱わない。

### YAML保存の構成（実装済み）

- `SceneDocument` はversionとobjects、各オブジェクトはid・name・componentsを持つ保存用データ。
- 各componentはtypeIdとvaluesを持つ。valuesは `[Inspector]` が付いた対応型（上記のInspector節）のみ。string・コレクション・`Transform`・`Nullable` のnullにも対応する。有限でないfloat／double（NaN・Infinity）は保存・読み込みとも拒否する。
- `ComponentRegistry` で固定文字列IDとC#型を明示登録する。Assetsと読み込みは同じ登録表を使う。C#のクラス名・名前空間を変更しても固定IDは維持する。組み込みの `Transform` は `core.transform` で登録する。
- `SceneSerializer` はCoreに置き、Sceneと保存用データの変換・検証・YamlDotNetによるYAML処理を行う。Avaloniaに依存しない。`Clone`（Play時の複製を含む）では配列・リスト・辞書・`Transform` を深く複製し、編集用と実行用の共有を残さない。
- ファイル選択、保存先、未保存状態、確認・エラー表示、ファイルの置き換えはEditorが担当する。
- 読み込みでは保存時のオブジェクトIDを復元する。全体の復元に成功してから編集中のSceneを入れ替え、ライフサイクルは実行しない。
- 未対応のversion、未知のtypeId、重複キー・ID・同型component、既存メンバーの不正な値を拒否する。values内の存在しないInspectorメンバーは読み飛ばし、次の保存時に削除する。文書構造やベクトル・Transform内部の未知キーは引き続き拒否する。
- 保存値がない新しいメンバーはクラスの初期値を維持する。メンバー名の変更にはデータ移行が必要で、自動移行は未実装。
- オブジェクト・componentの順番を維持し、valuesはメンバー名順で出力する。必要な文字列は引用し、独自タグ・アンカーは生成しない。コメントの保持は行わない。
- 保存は同じフォルダの一時ファイルに書き込み、完了後に元ファイルと置き換える。
- Parent・参照・Editor設定はそれぞれの機能を実装するときに追加する。

#### Inspector拡張値のYAML形式（実装済み）

- ベクトルはマッピング：`Vector2` は `{x, y}`、`Vector3` は `{x, y, z}`、`Vector4`・`Quaternion` は `{x, y, z, w}`。例：

```yaml
values:
  Position: {x: 1, y: 2, z: 3}
  Rotation: {x: 0, y: 0, z: 0, w: 1}
```

- `Transform` 型のメンバーは `LocalPosition`・`LocalRotation`・`LocalScale` のマッピング。`Transform` コンポーネント自体は同じ3メンバーをvaluesに持つ。例：

```yaml
values:
  Target:
    LocalPosition: {x: 7, y: 8, z: 9}
    LocalRotation: {x: 0, y: 0, z: 0, w: 1}
    LocalScale: {x: 1, y: 1, z: 1}
```

- 配列・`List<T>` はシーケンス、辞書はマッピング。nullは `null`、空は `[]`・`{}`。例：

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

- `version` は引き続き `1`。旧形式（string・int・float・boolのみのシーン）はそのまま読み込む。未知のキー・欠落・余分なキー・シーケンスとマッピングの取り違え・非有限数は拒否する。

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
- `version` は引き続き `1`。Priorityの有無でversionは変えない。

## Project（実装済み）

- Projectはフォルダ単位で、ルートの `Project.pure.project.yaml` と `Scenes/` 内の複数シーンで構成する。
- `ProjectDocument` はCoreの保存用データ。version（現在1）、name、startupScene（相対パス）を持つ。
- Editorの `ProjectFile` がProjectの作成・読み込み・シーンの列挙・起動シーンの変更を扱う。シーン一覧自体は保存せず、Scenesフォルダから取得する。
- Launcherで新規Projectを作り、空のMainシーンで開始する。作成途中は一時フォルダに書き込み、完成後に新しいProjectフォルダとして配置する。既存の同名フォルダは上書きしない。
- Projectを開くと起動シーンを編集対象にする。Project Explorerの右ペインでシーンファイルをダブルクリックまたはEnterで切り替える。
- 底ペインのProject Explorerは左にフォルダTree、右に中身を出す。Assets／Scenesタブは廃止し、Projectルートの下には実在するフォルダとファイルだけを表示する。組み込みサンプルの仮想Components一覧は表示しない。自作C#は元のフォルダ内のファイルからアタッチする。
- 右クリック（またはF2・Delete・Enter）でフォルダ作成・シーン作成・改名・削除・起動シーン設定・更新ができる。シーン作成はScenes配下のみ。Scenesフォルダ自体の改名・削除は不可。
- 改名・削除では編集中シーンと起動シーンの参照を付け替える。起動シーンと編集中シーン（を含むフォルダ）は削除できず、改名時はScenes外への脱出を拒否する。
- シーンの保存先はProjectのScenesフォルダ内とする。絶対パスによる参照、フォルダ外への参照、リンクによる外部参照を認めない。
- Project／シーンを切り替える前に未保存の変更を確認し、読み込み検証が成功するまで現在の編集対象を維持する。
- 起動シーン指定は編集時に最初に開くシーンとして使う。ProjectごとのC#コンパイル・自動登録・変更監視に対応。素材管理は未実装。

## Launcher（実装済み）

- アプリ起動時は `LauncherWindow` を表示し、新規作成・Projectファイルの選択・最近開いたProjectからの再開を行う。
- `ProjectSession` がProjectと起動シーンを読み込んで検証し、成功したデータを `MainWindow` に渡す。読み込み失敗時はLauncherにエラーを表示する。
- Editorを開いている間はLauncherを非表示にする。Editorを閉じると既存の未保存確認を経てLauncherへ戻る。Launcherを閉じるとアプリを終了する。
- 履歴はProject名とmanifestのローカルパスを最大12件保存する。保存先は `%LOCALAPPDATA%/PureEngine/recent-projects.json`。履歴が破損・保存不可でもProjectの作成・読み込みは妨げない。
- Launcherから新規作成するProjectは空のMainシーンを持つ。Editor内でのProject作成・切り替え操作はLauncherへの復帰に統一する。

## UI・描画・プレビュー：設計方針（実装は未着手）

2026-09-22、描画の土台をC#15とVulkanで作る方針を実装計画へ反映した。以下は目標と責務であり、動作確認済みの機能ではない。作業順・候補技術・完了条件は [Vulkan描画の実装計画](VulkanRenderingPlan.md) を参照する。

- 制作者はScene Viewで画像・文字・ボタンを配置し、Inspectorで調整してシーンへ保存する。UnityのCanvas・RectTransformに近い親子・矩形配置の制作体験を目指す。具体的な保存形式・APIは実装前に確定する。
- 描画本体と資源管理はリポジトリのC#15で実装し、既存バインディングからVulkanを呼ぶ。GPUドライバー等のネイティブ依存は残る。シェーダーはSPIR-Vを使い、HLSL等のソース言語とコンパイラは別途選定する。シェーダーまでC#15で書けるという意味ではない。
- AvaloniaはEditorの操作画面を担当する。ゲーム内UIはゲーム用の描画処理で表示し、Avaloniaの標準UI部品への変換は行わない。
- Vulkanで描いたGPU画像をAvaloniaの表示領域へ渡す経路を第一候補とする。既存Avalonia版・GPU・ドライバーでの埋め込み、同期、リサイズ、表示領域の破棄・再作成を最初に実機検証する。
- Scene Viewは編集用Sceneを編集用の視点で表示し、選択枠・配置ハンドルを加える。ゲームのライフサイクルは実行しない。Gameは既存PlaySessionの実行用Sceneを表示し、ゲーム操作を受け付ける。Play中の編集禁止とStop後の編集データ保持を維持する。
- 単体実行とEditorで描画・入力規則・Runtimeを共有し、ウィンドウと表示先への接続を分ける。Core／RuntimeはAvalonia・Vulkanに依存させず、GPU資源を保存データに含めない。
- 最初は画面上の2D UIを対象とし、画像・日本語の文字・ボタン・矩形クリップを実装する。UIの矩形配置とゲーム空間のTransformは役割を区別する。World Space Canvas・3D・高度な演出編集は後続の範囲とする。

Windows x64から検証する計画。最低Vulkanバージョン・対応GPU・使用拡張・描画性能は試作後に記録し、現時点で保証しない。

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
- 枠線は1px。フォーカス中だけアクセント紫、それ以外は枠線色。
- タブにはホバーによる色変化を付けない。
- 操作部品の角丸は4、カード・タイル・ピルは6に統一する。ダークでは効かないドロップシャドウは付けない。
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
- Stuffsの一覧行は高さ24px、右クリックメニュー項目は26px。メニューの外側に左右の余白を設ける。
- オブジェクト操作はStuffsの右クリックメニューから行い、追加・削除ボタンを常設しない。
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

監視通知は600msまとめ、UIスレッドで適用する。Play中・ファイル操作中・Inspector入力エラー中は保留する。旧Registryで編集データを保存形式へ取り出し、新Registryで別Sceneへ復元する。ID・名前・対応するInspector値・Priorityと選択を維持する。メンバーの追加・改名・削除時は上記Inspector節の規則で初期化／移行し、シーンを未保存にする。それ以外は旧未保存状態を維持する。アタッチ済みの型の削除、対応するメンバーの互換性のない型変更、Priorityの移行不能では切替自体を拒否し、旧コードとデータを保持する。生成途中の失敗は既存Serializerの逆順解放を使う。サービス登録を含む変更では、候補登録からの候補サービス群で移行し、成功後に編集Sceneと編集用サービス群を入れ替える。登録の曖昧・不正・登録中の失敗、依存解決の失敗も移行失敗と同様に旧状態を維持し、候補の Component・Scope・provider を解放する。旧状態の採用後は旧 Component を先に、旧 Scope・provider を後に解放する。

Project開始時も候補Registryで起動シーンの読み込みが成功してから公開する。候補登録からの編集用サービス群で復元し、成功したサービス群を Editor へ渡す。失敗時は候補の Component・Scope・provider を解放し、既存の登録を維持する。終了時は監視とインスタンスを解放し、ユーザー型の登録を外してALCのUnloadを要求する。Coreの型キャッシュはConditionalWeakTableを使い、古いユーザー型を静的キャッシュで保持し続けない。ユーザー自身のstaticイベント購読等の解除はDispose側の責任。

外部NuGet・独自csproj設定、実行状態を維持したPlay中の差し替えは対象外。起動時とコード再読み込みのコンパイルはバックグラウンドで行う。採用時はUIスレッドで世代と所有者を確認し、その時点のSceneを新コード用のサービス群へ移行する。Play・ファイル操作・入力エラー中は採用を保留し、古い結果・終了後の結果は解放する。


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

依存方向はEditor → Runtime → Core。ProjectComponentsがプロジェクト単位の型登録・ソース対応・採用中コードを持つ。ComponentAssetsは状態を持たない共通処理のみを提供する。ProjectSessionは起動Scene・編集用サービス・ProjectComponentsを所有し、MainWindowへ引き渡す。

編集状態と編集用サービスはEditSceneStore、保存・Play・再読み込みの可否判定はEditorOperationGate、コード採用と旧資源の解放はUserCodeReloadCoordinatorが担当する。再読み込みは候補Registry・サービス・移行先Sceneを準備してから採用し、旧Component → 旧サービス → 旧コードの順に解放する。終了時もこの所有順に従う。

RuntimeのGameSession.CreateとPlaySession.PrepareはAction<IServiceCollection>を受け取る。EditorはGameServices.ForProjectから組み込み＋当該プロジェクトの登録を渡し、UIなしの実行側はゲーム側の登録処理を直接渡せる。完了条件と検証は[ArchitectureImprovements.md](ArchitectureImprovements.md)を参照。
