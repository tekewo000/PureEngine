# PureEngine

C#で作る、UI中心の2Dマルチプレイゲーム向けエディター。
現在はLauncherからProjectを開き、オブジェクト・アタッチしたクラスの値を編集し、YAMLで保存・復元できます。

設計方針・Attribute・Priority・保存データ・将来の構想は [EngineArchitecture.md](docs/EngineArchitecture.md) にまとめています。
実装済みの範囲・制限・次の作業・検証状況は [実装計画・進捗](docs/ImplementationPlan.md) にまとめています。

C#15＋VulkanのV0〜V2を実装しました。Scene Viewには編集中のSceneをImage・Sprite・UiLayoutで描きます（Start／Updateは呼びません）。GameにはPlay中の実行用Sceneを表示し、Button操作を接続しています。保存プロジェクトの単体実行は後続です。[描画の実装計画](docs/VulkanRenderingPlan.md)と[検証記録](docs/ImplementationPlan.md#vulkan-v0v2の検証2026-09-22)を参照してください。

## 現在できること

- 灰色のダークテーマで、タブの切り替えとペインのサイズ変更ができる。
- 起動時にLauncherを表示し、Projectの新規作成・既存Projectの選択・最近開いたProjectからの再開ができる。
- 下部のProject Explorerは左にフォルダツリー、右にファイル一覧を表示する。プロジェクトに存在するフォルダとファイルだけを表示し、組み込みサンプルの一覧は追加しない。素材の登録情報（`.pureasset.yaml`）は表示しない。
- OSのファイル／フォルダをProject Explorerへドロップするとコピーする。フォルダ上はその中へ、余白は表示中のフォルダへ取り込む（右ペインでフォルダを選択中ならそのフォルダを優先）。同名は連番化し、同じ親フォルダへのドロップはスキップする。Play中は受け付けない。途中で失敗した場合、コピー済みの項目は残る。画像のSprite登録には引き続き「Import Image…」を使う。
- 自作C#はProject内の任意フォルダから読み込み、元のフォルダ内のファイルをドラッグしてアタッチできる。エンジン側への手動登録は不要。組み込みサンプルのComponents一覧は表示しない。
- Scene View／Gameは左、Stuffsは中央、Inspectorは右に配置する。
- Project Explorerの自作C#ファイルをStuffsのオブジェクト行、または選択中オブジェクトのInspectorへドラッグ＆ドロップしてアタッチする。追加したクラス名はInspectorのComponentsに表示する。同じ型の重複、Stuffsの余白、未選択のInspectorへのドロップは受け付けない。
- Inspectorの「Add Component」で選択中オブジェクトへEngineのComponentを検索して追加できる。候補は当該Projectの登録型（`Transform`・`UiElement`・`Image`・`Button`・`Text`・自作型）から探し、既存のアタッチ処理・factoryを使う。重複追加は付けない。Play中は追加できない。
- Inspectorのコンポーネントカードを右クリックして「Remove」で取り外す。削除は未保存の変更になり、保存後のシーンからも除かれる。Play中は取り外せない。親の削除はその時点の子孫ごと削除する。
- Stuffsは親子をツリー表示する。子は親の下にインデントして並び、折りたたみ・展開ができる。名前の変更はツリーへ即時反映する。
- Stuffsの行をドラッグ＆ドロップして親子付けと並べ替えができる。見出し行の中央50%へ落とすとその子に、上端／下端の25%へ落とすと青線の示す前後に移動する。展開済みの親も見出し行を基準に判定し、青線で行の高さは変わらない。折りたたまれた親の中央に500ms留まると自動展開する。空の余白へ落とすとルートに戻る。自分自身・自分の子孫へのドロップとPlay中の付け替えは受け付けない。
- Stuffsの右クリックメニュー「Add Empty」は選択中があればその子として追加し、なければルートに追加する。追加後は親を展開して追加分を選択する。
- 同じメニューの「UI → Image」はTransform・UiElement・Image、「UI → Button」はさらにButton、「UI → Text」はTransform・UiElement・Textを付けて作成する。親への追加と選択はAdd Emptyと同じで、名前は連番で重複を避ける。Spriteは作成後にInspectorで指定する。Play中は作成できない。
- Inspectorの「Name」で名前を編集する。ツリー表示へ即時反映し、未保存になる。
- オブジェクトを右クリックして「Delete」、またはStuffsで選択してDeleteキーで削除する。余白を右クリックすると選択が解除され、削除は無効になる。削除後は兄弟内の次の対象へ選択を移す。
- Inspectorで `[Inspector]` 付きの値を編集し、YAMLで保存・読み込みできる。対応型はstring・int・float・double・bool・enum・`Vector2`・`Vector3`・`Vector4`・`Quaternion`・`Color`・`Transform`・`Sprite`・配列・`List<T>`・`Dictionary<string, TValue>`（対応範囲の詳細は [EngineArchitecture.md](docs/EngineArchitecture.md) のInspector節を参照）。`Color` はR・G・B・Aの数値とプレビューで編集する。
- `Image`だけを付けても表示されない。`Transform`・`UiElement`が不足しているとInspectorに「Requires: …」と表示し、揃うと消える（`Button`・`Text` も同じ）。`Sprite`がNoneのときは描かない。`Text`は内容が空のときは描かない。素材IDが見つからないときはIDを保持したまま「Missing image …」と表示する。
- ProjectへPNG／JPEGを取り込み、`Image`の`Sprite`欄で選択・None解除ができる。Project Explorerの画像ファイルを`Sprite`欄へドラッグ＆ドロップしても同じ割り当てになる。取り込みはProject Explorerの「Import Image…」から行い、`Assets/`へコピーして新規IDの登録情報を作る。開き直し・Refreshで索引を作り直し、重複・欠落・壊れた登録はConsoleに理由を表示する。
- Scene Viewは編集中のSceneの親子配置を済ませてから`Order`昇順へ並べ替えて描く。同値は親→子・兄弟順を維持する。追加・削除、位置・サイズ・Anchor・Pivot・回転・拡縮・色・Sprite・文字・Orderの変更を反映する。暗い背景に薄いグリッドと原点・X／Y軸を表示し、中ボタンドラッグでパン、ホイールでカーソル中心にズーム（0.25〜8倍）できる。パン／ズームだけでは未保存にならない。
- Scene Viewの表示中の画像や文字を左クリックで選択すると、Stuffs／Inspectorと連動して選択枠とPivotを表示する。重なりは`Order`の大きい値を手前として同じ並べ替えで判定し、手前から選ぶ。空白クリックで選択を解除する。選択中の有効なUI対象にはX／Y矢印と中央ハンドルが出て、ドラッグでTransform.LocalPositionのX・Yだけを移動する（Zは保持）。ドラッグ中はInspectorへ即時反映し、左ボタンを離したときに変わっていた場合だけ未保存になる。Esc・フォーカス喪失・キャプチャ喪失や、保存・Scene切替・Play開始・コード採用の前には開始位置へ戻し、マウスの捕捉も解除する。ドラッグ中に親・Anchor・サイズ等の配置条件が変わった場合も中断する。Fキーで選択対象を余白付きで中央に表示する（ドラッグ中やInspectorの入力中は無効）。0サイズやXY変換が潰れた対象のGizmoは無効。Play中は配置編集できない。詳細な座標・中断規則は[設計書](docs/EngineArchitecture.md#v4前半scene-viewの編集操作)、検証状況は[実装計画](docs/ImplementationPlan.md#v4前半のscene-view編集操作2026-09-23)を参照する。
- enumはドロップダウン、`[Flags]` はチェックボックスとNoneボタンで編集する。自作enumを含むC#も保存後に自動反映する。互換性のない定義変更はConsoleに理由を表示し、編集中の値を保持する。
- ゲームのクラスは普通のC#コンストラクタでサービスを受け取れる。保存データは `[Inspector]` に置き、保存値を使う初期化は `Start` に書く。編集時の追加・読み込みと Play 時の複製は、Game側の一箇所の登録から作った独立したサービス群で生成する。
- ライフサイクルのあるクラスにはアタッチ設定としてStart／Update／Destroy Priorityを表示・編集できる。存在しないライフサイクルは表示しない。
- ツールバーのPlay／Stopで編集中シーンの複製を開始・停止できる。Play中は約60Hzで更新し、Stopで終了する。実行中の編集・切替は無効化する。
- GameタブはPlay中の実行用Sceneを描く。実行用Buttonの `Clicked` へ登録した処理を、クリック・Tab移動後のEnter／Spaceで呼ぶ。`Interactable` は保存され、押下・ホバー・フォーカスは保存しない。`Text` の内容・色・サイズも描く。
- .NET 11 RC1とAvaloniaでビルドし、Windows上で表示を確認済み。

## 技術

- .NET SDK 11.0.100-rc.1.26425.128（global.jsonで固定）
- Avalonia 12.1.2 / Fluent ダークテーマ
- YamlDotNet 18.1.0

| 対象 | ターゲット | C# |
| --- | --- | --- |
| Engine・Runtime・Editor・チェック | net11.0 | 15（SDK既定） |
| 生成するゲーム用csproj | net11.0 | 15（組み込みRoslyn 5.xのコンパイル設定に合わせる） |
| PureEngine.Analyzers | netstandard2.0 | 15（外部エディターの実行環境との互換性を維持） |

## シーンの保存・読み込み

- File → Open Scene（Ctrl+O）：シーンを読み込む。
- File → Save Scene（Ctrl+S）：上書き保存。初回は保存先を選ぶ。
- File → Save Scene As（Ctrl+Shift+S）：別名で保存。
- 拡張子は `.pure.scene.yaml`。例として [Examples/Main.pure.scene.yaml](Examples/Main.pure.scene.yaml) を開ける。
- 未保存の変更はタイトルの `*` で示す。別シーンを開くときや終了時にSave／Discard／Cancelを選ぶ。
- Inspectorに入力エラーがある間は保存しない。成否とエラー詳細は画面下部に表示する。

保存対象はオブジェクトのID・名前・親子関係・兄弟順、登録済みクラスの固定ID、`[Inspector]` 付きの値、アタッチごとのPriority、ComponentごとのインスタンスIDと参照ID。組み込みは `core.transform`・`core.ui-element`・`core.image`・`core.button`・`core.text` で保存する。`Sprite` は画像IDと切り出し矩形で保存し、欠落した素材IDも失わず保持する。`Image` の `Order`（`RendererComponent` の共通基底）もInspector値として保存・Cloneし、旧データは `Order: 0` として従来の表示を維持する。ライフサイクルのPriorityとは独立させる。
読み込みは別のSceneへ復元し、成功してから現在のSceneと入れ替える。保存は同じフォルダの一時ファイルへ書き終えてから置き換える。
現在の形式は `version: 3`。旧形式（`version: 1` は全てルート・配列順の兄弟、`version: 2` は親子・兄弟順あり）として読み込み、次の明示保存で3へ更新する。YAMLのコメントは再保存で失われる。Editorのペイン配置は現在の保存対象に含めない。旧形式（prioritiesなし）はすべて0として読み込む。旧形式（string・int・float・boolのみのシーン）はそのまま読み込む。

## SceneObject・Componentの参照

ゲーム側は通常のC#参照を宣言し、Inspectorで同じSceneの対象を選ぶ。`ObjectRef<T>` や毎回の `Resolve` は不要で、実行中は解決済みの実物を直接触る。

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

- 参照できるのは同じSceneのSceneObjectと、Projectに登録されたComponent型（`Transform`・`UiElement`・`Image`・`Button`・自作型）。未登録の対応する自作クラスは埋め込み値になる。登録Component型の欄に `new Button()`／`new Transform()` を直接入れても埋め込み値にはならず、別Scene・未アタッチの参照先の保存は拒否する。自作のpublic具象クラスは自動登録されるため、従来埋め込みに使った型も登録対象なら参照欄になる。
- Inspectorの参照欄は対象名・ID・None／Missing・選択／解除を表示する。SceneObject／登録Componentの欄にはStuffsの行または型が一致するPrefabファイル、DataAsset型の欄には型が一致するDataAssetファイルをドラッグ＆ドロップできる（Componentアタッチとは別形式）。Prefabを落とした場合は選択中オブジェクトの子として複製し、Rootまたは一致するComponentを割り当てる。対象がない、または候補が複数の場合は選ばない。Play中は編集できない。変更したときだけ未保存になる。
- 参照を設定したいオブジェクトを選択し、参照先のStuffs行をInspectorの参照欄へドラッグする。行を押した時点では選択を切り替えず、ドラッグ中もInspectorを維持する。ドラッグせずに離すと、その行を選択する。
- 対象を削除・取り外すとC#はnullになり、IDはMissingとして保持される。Missingのまま保存・開き直しができ、同じIDが戻れば再接続する。別の対象を選び直すと実物の値を優先する。Missingを消すときは欄のClearを使う。単なるnull代入ではMissingは消えない。
- 旧シーン（v1／v2）はSceneObject IDを保持し、Component IDを新規発行して未保存化する。登録Component型の旧インライン値は保持して診断し、欄の再割り当て／Clearまで上書き保存・Play用Cloneを拒否する。例えば旧Button値は実際にアタッチしたButtonを選び直す。復元に失敗したときは元Scene・元ファイルを置き換えない。
- 参照を含む配列・List・stringキーDictionaryは、埋め込みクラスの中でも選択・解除・行の追加／削除ができる。Inspectorで行削除や辞書キー変更を行うとMissingのIDも移動する。Missingを含むコレクションを通常のC#から構造変更する場合は `Scene.References` の明示操作で保持パスも更新する（null同士の移動は通常のC#参照から判別できない）。

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
ProjectごとのC#読み込み・自動反映に対応。画像素材は `Assets/` へ取り込んで使う。

```text
MyGame/
  Project.pure.project.yaml
  Assets/
    Cards/ace.png
    Cards/ace.png.pureasset.yaml
  Scenes/
    Main.pure.scene.yaml
```

- 「Import Image…」でPNG／JPEGを `Assets/` へコピーし、新規IDの登録情報（`version: 1`・`id`・`kind: image`）を作る。同じ素材の移動・改名は登録情報ごと行えばIDを維持する。
- Projectを開くときとRefreshで索引を作り直す。同じ画像IDのファイルを変更した場合も、Refresh後の次の描画で画像キャッシュを更新する。重複ID・欠落・壊れた登録・種別違いはConsoleに理由を表示し、該当IDを解決不能にする。索引の再走査だけではファイルを作らない。
- Assets以下のリンク／junctionは使用できない。取込先・登録情報・画像読込の直前に検証し、索引作成後にリンクへ差し替わった場合も外部ファイルを読まない。
- 素材のライセンス確認と保持は取り込む側の責任。エンジンは推定しない。


## 自作C#と自動反映

Project内に、例えば `Gameplay/Actors/Player.cs` を作成します。専用のComponentsフォルダは不要です。

Project欄のフォルダまたはファイル一覧を右クリックして **Create C#** を選ぶと、ファイル名を指定してC#を作成できます（.csは省略可）。ひな形は `public sealed class ファイル名` と空の本体です。同名ファイルは上書きしません。作成後は通常の自動コンパイルでアタッチ可能になります。

```csharp
using PureEngine.Core;

namespace MyGame;

public class Player
{
    [Inspector] public int Health = 100;
    [Start] public void Start() => Log.Info($"Health: {Health}");
}
```

- プロジェクトを開くとC#をコンパイルし、Project欄の元のフォルダに表示します。ファイルをStuffsのオブジェクト行、または選択中オブジェクトのInspectorへドラッグするとアタッチできます。
- アタッチ対象は外部から見えるpublicの具象クラス（record classを含む）。internal・abstract・static・未確定の型引数を持つクラスは補助コードとして扱います。属性や専用の基底クラスは必須ではありません。生成には呼び出し可能なコンストラクタと、必要なら既存Gameサービスへの依存登録が必要です。
- 1ファイルに複数の対象クラスがあれば、未アタッチのクラスをすべて追加します。partial classは宣言のある各ファイルから同じ型をアタッチできます。
- 保存・追加・削除・ファイルやフォルダの移動を検知し、約600msの待機で連続通知をまとめます。停止中に反映し、Play中はStop後、Inspectorの入力エラー中は修正後まで保留します。
- 未保存のオブジェクト・同名のInspector値・Priority・選択を引き継ぎます。Inspectorメンバーの改名・削除はそのまま反映し、新しい名前のメンバーは初期値を使います。
- コンパイル失敗、生成失敗、アタッチ済みクラスの削除、同名メンバーの互換性のない型変更、設定済みPriorityの移行不能時は反映を中止し、直前の正常なコードと編集データを保持します。理由をConsoleに表示し、修正して保存すると再試行します。
- 保存用の型IDは初回に割り当て、`.pureengine/types.json` に保持します。同じファイル内でのクラス名・名前空間の変更でも、アタッチ・Inspector値・Priorityを引き継ぎます。再起動後も同じIDで読み込みます。この管理ファイルもプロジェクトと一緒に保存・移動してください。
- 複数クラスがあるファイルでは、同名クラスを先に照合し、残った旧・新クラスが1対1の場合に改名として引き継ぎます。複数クラスの同時改名など対応が曖昧な場合は反映を止めます。一つずつ改名して保存してください。ファイル移動とクラス改名も別々に反映してください。
- 旧バージョンの保存シーンは、完全名または一意なクラス名で初回のID管理へ移行します。ID管理導入前にクラス名自体も変わっていた場合は、元の名前で一度読み込んでから改名してください。
- 起動時からコンパイルに失敗し、起動シーンがその自作型を参照している場合は、ソースを修正してから開き直してください。自作型を使わない起動シーンなら、エラーをConsoleに残して開けます。

現在はProject内のソースをまとめてコンパイルします。`bin`・`obj`・隠しフォルダ・リンク先フォルダは対象外です。独自csprojの設定、外部NuGet依存の復元、Playの実行状態を保ったコード差し替えは未対応です。起動時・再読み込み時のコンパイルはバックグラウンドで行います。Componentの生成・Scene移行・採用はUIスレッドで行い、採用時点の編集内容を引き継ぎます。Play・ファイル操作・入力エラー中は採用を保留し、連続変更や終了で古くなった結果は破棄します。詳細は[アーキテクチャ改善](docs/ArchitectureImprovements.md)を参照してください。

### Inspectorメンバーを改名する

フィールドやプロパティは、そのまま改名・削除してC#を保存できます。例えば `Health` を次の宣言へ変えると、古いHealthの保存値を無視し、HitPointsは100で始まります。

```csharp
[Inspector]
public int HitPoints { get; set; } = 100;
```

YAMLにしか残っていない項目があっても、読み込み・C#自動反映を止めません。項目を整理したシーンはタイトルに `*` が付きます。シーンを保存すると古い項目をYAMLから削除し、現在の項目だけを書き込みます。まだ開いていない他のシーンは、それぞれ開いて保存したときに整理されます。

改名前の値も残したい場合だけ、任意で `[FormerlySerializedAs("Health")]` を付けられます。複数回の改名は属性を追加して過去の旧名を残します。通常の改名には不要です。対応範囲・重複時の扱いは[設計書のInspector節](docs/EngineArchitecture.md#inspectorと保存)を参照してください。

### データアセットを作る

UnityのScriptableObjectに当たる共有データを、継承なしの普通のクラスで作れます。`[DataAsset]` を付けるとProject欄の **Create Data Asset** メニューに出ます。引数でメニューの場所を指定できます（省略時は型名）。

```csharp
using PureEngine.Core;

namespace MyGame;

[DataAsset("Items/Weapon")]
public class WeaponData
{
    [Inspector] public string Name { get; set; } = "";
    [Inspector] public int Attack { get; set; }
}
```

- 作成先はメニューを開いたフォルダで、ファイルは `.pure.asset.yaml` です。ID・型・値の版・データ本体を持ち、アセットIDやパスはクラスに書きません。
- 値の規則はシーンと同じで、`[Inspector]` の付いた対応型だけを保存します。`SceneObject` やComponentへの参照は保存できません。
- メニューに使えない型がある場合は理由を表示します。同じメニュー場所の重複も報告します。
- Project欄でアセットを選ぶとInspectorに読み込み、そのまま編集して **Save Data Asset**（Ctrl+S）で保存します。編集中はファイル名に `*` が付きます。シーンとは別に未保存を管理し、切り替え・終了時は保存確認が出ます。
- Componentの `[Inspector]` メンバーにはアセットのクラス型をそのまま指定できます。ドロップダウンから選ぶか、Project欄の `.pure.asset.yaml` ファイルを欄へD&Dしてください。`get; init;` と `get; set;` の両方に対応します。押している間はInspectorを切り替えず、ドラッグせず離したときだけアセット編集を開きます。

```csharp
public class Fighter
{
    [Inspector] public WeaponData? Weapon { get; init; }

    [Start] public void Start()
    {
        if (Weapon is { } weapon) Log.Info($"Attack: {weapon.Attack}");
    }
}
```

- 保存するのはIDだけです。ファイルを移動・改名しても同じIDを読み込みます。欠落時は `null` とMissing表示になり、Clearしない限り保存にIDを残します。ファイルを戻してシーンを開き直すと復旧します。`DataAssetRef<T>` や明示的なResolveは不要です。
- 一括取得が必要な場合は、従来どおり `DataAssetStore` をコンストラクタで受け取ってIDや型で引けます。

```csharp
public class Shop
{
    private readonly DataAssetStore _assets;
    public Shop(DataAssetStore assets) => _assets = assets;

    [Start] public void Start() => Log.Info($"Shop has {_assets.GetAll<WeaponData>().Count} weapons.");
}
```

- ストアはスナップショットです。プロジェクトを開く・C#を反映する・Playするたびに作り直し、実行中の変更はファイルや他の実行に漏れません。仕様は[設計書のData Assets節](docs/EngineArchitecture.md#data-assets)を参照してください。
- Playで使うのは保存済みの値です。Inspectorの変更をゲームへ反映する前にアセットを保存してください。ストア内のインスタンス自体は通常の可変classで、同じ実行内の読み込み先では共有されます。

### プレハブを使う

SceneObjectを1つ選び、子孫ごとファイル化して何度でも複製できます。配置後は普通のSceneObjectになり、元の編集は配置済みへ反映されません。リンク・個別Override・入れ子はありません。

- Stuffsで対象を選んで右クリック→ **Save as Prefab…** で保存します。保存先はProject欄に表示中のフォルダで、ファイルは `.pure.prefab.yaml` です。既存ファイルの上書きはしません。
- 配置はProject欄のファイルを右クリック→ **Place in Scene**、ダブルクリック／Enter、またはStuffsへのD&Dです。行の上ならその子、余白ならルートの末尾に置きます（メニューとダブルクリックはStuffsの選択を親にします）。Prefab内の兄弟順を保ちます。配置後はシーンが未保存になります。
- PrefabファイルはSceneObject／登録ComponentのInspector参照欄にもD&Dできます。選択中オブジェクトの子としてPrefabを複製し、Rootまたは型が一致して一つだけあるComponentを割り当てます。複数候補や型不一致は割り当てず、元のPrefabとのリンクは保存しません。
- Prefab内部の参照（子・Component・コレクションや入れ子値の中も）は複製先へつなぎ直し、範囲外・画像・データアセットへの参照はそのまま残します。対象不在はMissingとしてIDを保持し、同じIDが戻れば再接続します。メンバーの追加・改名・削除には耐え、非互換な型変更などの壊れたPrefabは配置を拒否してシーンを変えません。
- ゲーム実行中の生成は `PrefabSpawner` をコンストラクターで受け、`Spawn` で行います。PrefabのIDはファイルの `id` です。`Start` 以降に呼び、コンストラクターでは使わないでください。追加分は次のフレームから開始します。

```csharp
public class EnemySpawner(PrefabSpawner prefabs)
{
    [Start] public void Start()
    {
        // The prefab ID is the "id" in the .pure.prefab.yaml file.
        prefabs.Spawn(Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"));
    }
}
```

- Play開始時にPrefab一覧を作り直し、実行中の配置はファイルや他の実行に漏れません。Play中の保存・配置はできません。仕様は[設計書のPrefabs節](docs/EngineArchitecture.md#prefabs)を参照してください。

## ZedなどでC#を編集する

新規Projectの作成時、および既存Projectを開くときに、編集用の `PureEngine.Game.csproj`・`PureEngine.Game.slnx` と不足している `global.json` を自動生成します。ゲームのターゲットは **.NET 11（net11.0）**。PureEngine.Core・DIライブラリへの参照、ライフサイクル診断用のAnalyzer参照、エンジンが使用するSDKの指定を含みます。

ZedではC#拡張を導入し、Projectのルートフォルダ（csprojがあるフォルダ）を開いてください。Roslynが補完・診断・using追加に必要な型情報を読み込めます。必要な.NET 11 SDKがインストールされ、Zedからdotnetを実行できることが前提です。既にフォルダを開いていた場合は言語サーバーを再起動するか、フォルダを開き直してください。

生成したcsprojの参照先はProjectを開くたびに更新します。手動作成のcsprojが既にある場合は自動生成を避け、既存のソリューション・global.jsonやZedの設定も上書きしません。自動生成ファイルを自分で管理する場合は先頭の生成コメントを外してください。このcsprojは外部エディター向けです。独自のPackageReferenceやビルド設定をエンジン内のコンパイルへ取り込む機能は含みません。

### privateライフサイクルメソッドの未使用診断

PureEngineの `[Start]`・`[Update]`・`[Destroy]` が付いたメソッドは、エンジンがリフレクションで呼び出します。
同梱の [LifecycleUsageSuppressor](src/PureEngine.Analyzers/LifecycleUsageSuppressor.cs) が、これらのメソッドのIDE0051（未使用privateメソッド）だけを抑制します。ゲームコードにpragmaを追加する必要はありません。

- 通常の未使用メソッドや、別の名前空間にある同名属性のメソッドにはIDE0051が残ります。
- 属性の別名・完全修飾名にも対応し、属性を外すとIDE0051の対象に戻ります。
- CA1822（static化）など他の診断や、実行前のライフサイクル宣言の検証は対象外です。

既存の生成済みプロジェクトは、更新したEditorで開き直すとAnalyzer参照が更新されます。Zedに古い診断が残る場合は言語サーバーを再起動してください。
手動管理のcsprojは上書きしないため、Editorに同梱された `PureEngine.Analyzers.dll` を `<Analyzer Include="DLLのパス" />` としてItemGroupに追加してください。

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
- `src/PureEngine.Runtime/`：UI非依存のサービス生成とPlay実行接続。
- `src/PureEngine.Editor/`：Avaloniaによる編集画面。
- `src/PureEngine.Analyzers/`：ゲーム用のライフサイクル診断Suppressor。Editorから配布する。
- `tests/PureEngine.Core.Checks/`：Coreの動作チェック。
- `tests/PureEngine.Editor.Checks/`：画面を表示しないLauncher・Editor遷移の動作チェック。
- `tools/code-quality.ps1`：一括修正・提案診断・ビルド・Core/Editorチェック。
- `.github/workflows/code-quality.yml`：push/PR時に同じ品質チェックを実行。
- [EngineArchitecture.md](docs/EngineArchitecture.md)：設計仕様と未決定事項。

Coreのクラスのアタッチ・取得と属性検出、Editorからのアタッチ・値とPriorityの編集、YAMLシーン保存、Coreのライフサイクル実行（Priority順）とEditorのPlay／Stopによる開始・停止は実装済み。Game描画とImage／Textの配置、Button操作も実装済みです。単体配布、Steam連携は後続です。

## Coreのライフサイクル実行

既存のSceneとComponentRegistryから、編集データと独立した実行用Sceneを作れます。ツールバーのPlay／Stopで開始・停止できます。

```csharp
using var runtime = new SceneRuntime(scene, registry);
runtime.Start();
if (runtime.IsRunning) runtime.Step(1f / 60f); // 呼び出し元が経過秒を渡す
runtime.Stop();
foreach (var error in runtime.Errors)
    Console.WriteLine($"{error.ObjectName}/{error.ComponentType.Name}.{error.MethodName}: {error.Exception}");
```

実行中の追加・アタッチ・削除には `runtime.Scene.AddEmpty()`、`Attach()`、`runtime.Scene.Remove()` を使います。追加分は次のStepでStartし、削除予約後はStart／Updateを呼ばず、フレーム末にDestroy＋Disposeします。各ライフサイクルはPriorityの小さい順に実行し、同値は順序を保証しません。動的追加分は最初のStartより前に `SetStartPriority` などで設定できます。全Componentの生成・復元・検証が成功してからStartし、準備失敗時はStart／Destroyせず生成済み `IDisposable` のみ解放します。Start途中失敗でも受入済み全対象をDestroy＋Disposeし、一つの終了処理の例外でも残りを続けて `Errors` に報告します。`Stop()`／`Dispose()` の重複はno-opで二重終了しません。再実行は新しいSceneRuntimeを作ります。詳細な制約と例外時の動作は設計書を参照してください。

ツールバーのPlayは編集中Sceneの複製で `PlaySession` を作り、約60Hzのタイマーで実測の経過秒を渡して更新します。Inspectorに入力エラーがある間は開始せず、画面下部に理由を表示します。実行中はシーン編集・切替とシーン操作メニューを無効化し、Stopで終了します。開始・更新・終了の失敗とSceneRuntimeのErrorsは画面下部に表示し、失敗後も操作可能な状態へ戻します。ウィンドウを閉じる際も実行中なら終了・解放します。再Playは新しいSceneRuntimeで開始し、Gameタブに実行用Sceneを描画します。

Playの実行・終了エラーは画面下部に表示し、ツールチップで全件の発生箇所と例外詳細を確認できます。ウィンドウ終了時にPlayの後片付けでエラーが発生した場合は、その回の終了を取り消して表示を残します。内容を確認してもう一度閉じると終了できます。

## 共通ログとConsole

普通のC#クラスから `using PureEngine.Core;` だけで呼べます。Loggerの生成・DI登録・継承は不要です。

```csharp
using PureEngine.Core;

Log.Info("開始します");
Log.Warning("残り時間が少ないです");
Log.Error("保存に失敗しました");
Log.Error(exception);
Log.Error("読み込みに失敗しました", exception);

// エンジン側の通知・診断。ゲーム側と同じキューへ、出力元を区別して記録します。
Log.Engine.Info("Playの開始処理が完了しました。");
Log.Engine.Warning("エンジン側の警告");
Log.Engine.Error("エンジン側の処理に失敗しました", exception);
```

- レベルはInfo／Warning／Errorの3種類。`Log.Error` は記録だけで例外送出やPlay停止をしません。
- 出力元はGame／Engine。従来の `Log.Info / Warning / Error` はGame、`Log.Engine.Info / Warning / Error` はEngineです。Engineにも例外のみ・本文と例外のErrorオーバーロードがあり、呼び出し元のファイル・行・メソッド情報を保持します。
- Console一覧は出力元とレベルを別々に表示し、詳細・コピーには `[Engine][Info]` のように含めます。Engineチェック（初期ON）を外すとエンジンの全レベルを非表示にします。履歴と件数は保持され、再チェックで戻ります。検索・レベル別フィルターと併用できます。
- 本文・レベル・時刻・呼び出し元のファイル／行／メソッドを保持します。呼び出し元は自動取得し、通常ログではスタックトレースを取りません。
- 例外を渡すと内部例外とスタックトレースを含む詳細を保持します。Runtimeエラーの発生箇所は例外側の情報を使います。
- 別スレッドからも呼べます。UIを直接操作せず、Editorが約200msごとにキューからまとめて取り込みます。Consoleタブが非表示でも受け取り、後で確認できます。
- キュー上限は1000件（`Log.MaxQueuedEntries`）、表示履歴上限は1000件（`MainWindow.ConsoleMaxHistory`）。上限で破棄した件数はConsoleに表示します。受け手がなくても失敗せず、無制限に増えません。

下部ペインのConsoleタブの使い方：

- 時刻・種類・本文の先頭を一覧表示します。行を選ぶと全文・記録箇所・例外詳細を下の詳細欄で確認し、Copyでコピーできます。
- Info／Warning／Errorのチェックで表示を切り替え、件数は各チェック横に表示します。検索欄は本文（例外詳細を含む）に部分一致します。フィルターは表示だけで、切り替えると保持中のログを再表示します。
- Clearで履歴と表示を消去します。Clear on Play（初期ON）はStart前に消去し、そのPlayの開始ログは消しません。
- 一覧が最下部にある間は最新ログへ自動スクロールします。上へスクロールして過去ログを読んでいる間は、行を選択していなくても位置を保ちます。
- Play開始・停止・準備失敗・更新失敗・終了失敗と `SceneRuntime.Errors` の全件をConsoleで確認できます。同じエラーはStep／Stop／Disposeで重複しません。Stop後も読めます。
- 実行中のオブジェクト削除でDestroy／Disposeが失敗しても、停止を待たず、そのStep後にエラーを転送します。

## ゲームのサービス登録とコンストラクタ注入

エンジンを変更せず、Project内の自作C#に次の登録を1つだけ書きます。組み込み登録（`src/PureEngine.Editor/Game/GameServices.cs`）に加えて適用されます。登録がない既存Projectは従来どおり組み込み登録だけで動きます。

```csharp
using Microsoft.Extensions.DependencyInjection;

public static class GameSetup
{
    public static void ConfigureGameServices(IServiceCollection services)
    {
        services.AddScoped<QuestLog>();
    }
}

public sealed class QuestBoard(QuestLog log)
{
    [Inspector] public int Score { get; set; } = 10;
    [Start] private void OnStart() { /* 保存値を使う初期化はここ */ }
}
```

形式は同期の `public static void ConfigureGameServices(IServiceCollection services)` の1つのみです（async voidは不可）。同名が複数ある場合や形式が違う場合は、理由を表示して新しい登録を採用しません。Component 自体の DI 登録や専用基底クラスは不要です。Core は `Func<Type, object>` の生成関数だけを受け、MS DI を参照しません。編集と各 Play は同じ登録から独立したサービス群（provider＋Scope）で動き、Singleton も共有しません。UIなしの実行側はPureEngine.Runtimeを参照し、次の形で呼び出します。EditorのPlayボタンも同じ `PlaySession` を使います。

```csharp
using PureEngine.Runtime;

// sceneの型を登録済みのComponentRegistryと、ゲーム側の登録処理を渡す。
using var play = PlaySession.Prepare(scene, registry, GameSetup.ConfigureGameServices);
play.Start();
if (play.Runtime.IsRunning) play.Step(1f / 60f);
```

終了時は Runtime の Destroy＋Dispose を完了してから Scope・provider を終了します。注入されたサービスを Component 側で Dispose しないでください。編集用シーン切替は同一ウィンドウ内で編集用 Scope を共有します。

`PlaySession.Stop()` と `Dispose()` はどちらもサービスまで終了します。コールバック中の停止は、そのコールバックと Component の終了処理が完了してからサービスを解放します。ライフサイクル例外による自動停止も同じ順序です。停止後も `Runtime.Errors` を確認できます。
編集用 Component はシーン切替・オブジェクト削除・読み込みキャンセル・ウィンドウ終了で Dispose し、ゲーム用の Destroy は呼びません。ウィンドウ終了では Component を先に、サービスを後に解放します。

## コード品質と一括チェック

コードの提案をまとめて修正し、残りの診断・ビルド・Core/Editorチェックまで実行します。

```powershell
./tools/code-quality.ps1
```

変更せずに検査する場合は `./tools/code-quality.ps1 -Check`。GitHub Actionsもpush/PRで同じ検査を実行します。
対象は `PureEngine.slnx`。リポジトリの `.editorconfig`、SDKの `global.json`、エージェント向けの [AGENTS.md](AGENTS.md) を共通の基準にします。生成したゲーム用プロジェクトへ、この品質設定一式を自動コピーする機能ではありません。
namespaceの名前・有無・宣言形式は修正対象外です。
static化・未使用引数の削除・引数順序の変更は自動適用せず、呼び出し元とリフレクション利用を確認して修正します。
`[Start]`・`[Update]`・`[Destroy]` はインスタンスメソッドのまま維持し、必要なCA1822の例外はその宣言に理由付きで記載します。
コールバックで使わない引数は削除せず `_` と命名します。画面バインディングや異常系テストも必要な例外だけ局所的に抑制します。

検査は `dotnet format style/analyzers --severity info` 相当、警告をエラー扱いにしたビルド、Core/Editorチェックの順です。修正できない診断は残件として失敗するため、内容を確認して手動対応してください。
スクリプトはSDK 11 RC1の `dotnet format` 起動パスの問題を避けるため、選択されたSDK内のformatter DLLを直接実行します。

## 個別の動作確認

追加・名前変更・検証・ID・削除・アタッチ・属性検出に加えて、YAMLの保存と復元、文字列の保持、不正データの拒否、保存失敗時の元ファイル保護を確認します。SDKが使える環境で実行してください。

```powershell
dotnet run --project tests/PureEngine.Core.Checks
```

Launcherからの作成・履歴からの再開・未保存確認・Launcherへの復帰・終了は、実画面を操作せず確認できる。

```powershell
dotnet run --project tests/PureEngine.Editor.Checks
```

Coreのチェックにはライフサイクル・編集データの分離・追加削除・例外時の後片付け・Priorityの保持と順序・保存互換も含みます。実行機構単体の性能測定は次で再実行できます。測定条件と結果は実装計画・進捗に記載しています。

```powershell
dotnet run --project tests/PureEngine.Core.Checks -c Release -- --runtime-benchmark
```


## Vulkan描画の確認（Windows x64）

通常のEditorを起動してProjectを開くと、Scene Viewに編集中のSceneを描きます。対応しないGPU・表示バックエンドでは描画領域に理由を表示し、Consoleへ記録します。

Editorに依存しない描画確認用ウィンドウ：

```powershell
dotnet run --project src/PureEngine.Player
```

これはImage／Sprite／UiLayoutの描画試作であり、保存したゲームのPlayerではありません。ウィンドウを横に広げると、下の帯がStretchし、右側の小さい画像が親領域の右下へ追従します。開発SDKはglobal.jsonの指定版、実行時はVulkan対応GPUドライバーが必要です。シェーダー・フォントは同梱するので実行時のVulkan SDK／シェーダーコンパイラは不要です。

シェーダーの変更後は次を実行します。固定版glslang 16.6.0をtools/.cacheへ取得してGLSLをSPIR-Vへコンパイルします。生成物とHashes.propsも変更に含めてください。通常ビルドは欠落・ソースと生成物のハッシュ不一致を拒否します。

```powershell
./tools/build-shaders.ps1
./tools/code-quality.ps1 -Check
```

GPUの実ウィンドウ検証は通常CIと分離しています。Khronos Validation Layersを用意して実行してください（未導入をPASSにはしません）。

```powershell
./tools/vulkan-check.ps1 -ValidationLayerPath 'C:/VulkanSDK/1.4.341.1/Bin'
```

Scene Viewのリサイズ・サイズ0・最小化／復元・Gameタブ切替・20回の取り外し／再作成・ハンドル数・Inspectorのヒットテストを検査します。目視確認時は `$env:PUREENGINE_VISUAL_CHECK='1'` を設定すると最後にウィンドウを残します。使用後は環境変数を削除してください。描画規約・資源の所有権・依存ライセンスは[設計書](docs/EngineArchitecture.md#v0v2の描画経路と資源所有)を参照してください。


## UI配置の単体計算

Coreの`UiLayout.Calculate(parentSize, parentWorld, transform, uiElement)`で、拡縮前の矩形サイズと配置行列を取得できます。親のサイズ・行列には親のUiLayout結果を渡し、ルートには呼び出し側で決めた表示領域と基準行列を渡します。TransformやUiElementの値は変更しません。

```csharp
var (size, world) = UiLayout.Calculate(
    new Vector2(400, 200), Matrix4x4.Identity,
    new Transform(),
    new UiElement
    {
        AnchorMin = new(0.5f), AnchorMax = new(0.5f),
        Pivot = new(0.5f), SizeDelta = new(100, 40),
    });
// sizeは(100, 40)、ローカル原点をworldで変換した左上は(150, 80, 0)。
```

`System.Numerics`と`PureEngine.Core`を使用します。これはGPU不要の配置計算APIです。編集中Sceneの描画・Buttonの前段階の選択枠と共通の計算です。配置・入力検証の仕様は[設計書](docs/EngineArchitecture.md#配置計算の置き場所と入出力)を参照してください。


## Spriteの素材データ

`PureEngine.Core.Sprite`は元画像のIDと切り出し領域を表します。Componentではなく、GPUや画像ファイルを所有しない変更不可のデータです。

```csharp
// imageIdには、参照したい元画像のIDを指定する。
var whole = new Sprite(imageId);
var cropped = new Sprite(imageId, (16, 8, 32, 24));
var region = cropped.ResolveSourceRect(imageWidth: 128, imageHeight: 64);
// region = (X: 16, Y: 8, Width: 32, Height: 24)
```

切り出しは左上原点の整数ピクセルです。省略すると画像全体を使い、`ResolveSourceRect`にはデコード後の実寸を渡します。画像外の切り出しは例外になります。`Image.Sprite`のInspector選択・YAML保存・Clone・編集中Sceneの描画に接続済みです。素材IDの解決はProjectの `Assets/` 索引を使い、検証用の固定画像辞書は制作データに使いません。


## Image Componentの描画

`Image.Color` は `PureEngine.Core.Color` 型です。InspectorではRGBA数値と色見本を表示します。旧シーンのVector4形式は読み込めますが、C#で `Vector4` を代入していた箇所は `new Color(r, g, b, a)` または `Color.White` へ変更してください。保存互換性の詳細は [設計書のYAML節](docs/EngineArchitecture.md#inspector拡張値のyaml形式実装済み) を参照してください。

Scene Viewの前後関係はInspectorの`Image.Order`で変更します。手前にしたい画像へ大きい値を設定してください。描画順の適用範囲は[設計書](docs/EngineArchitecture.md#image-componentから描画への接続)、確認済みの項目は[検証記録](docs/ImplementationPlan.md#描画順の共通基盤order2026-09-23)を参照してください。

Imageは`RendererComponent`から派生し、`Sprite`・`Color`・`Order = 0`を持つ。位置・回転・拡縮をTransform、領域をUiElementから取得します。Spriteがnullなら表示しません。`Order`は昇順で描き、大きい値を手前にする。負数も許可し、親からは継承せず各対象の値を使う。実行は上記のEditorまたは試作Playerを使います。[単体表示](docs/evidence/image-component-player.png)と[リサイズ後](docs/evidence/image-component-resized.png)は検証用サンプルの画面証跡として残しています。

コードから使う入口は`PureEngine.Rendering.UiImageRenderer.Draw`です。対象SceneObject、親のサイズ／UI配置行列、画像IDからPNG等のバイト列を取得する辞書、表示先のクリップ矩形を渡します。戻り値のサイズ・行列を子へ渡せます。画像辞書の内容は描画リストの寿命中不変としてください。

編集中Sceneの一括走査は`PureEngine.Rendering.EditSceneRenderer.Build`です。Scene・画像辞書・表示領域を渡すと、親子配置を済ませてから`Order`昇順へ並べ替えて描き、描けなかった対象の診断を返します。ヒット判定は`PureEngine.Core.SceneViewMath.HitTest`で同じ並べ替えを使い、手前から判定します。Start／Updateは呼びません。Button操作は下記のGame表示へ接続しています。SpriteRenderer本体・SortingLayer・Zによる奥行き制御は後続です。

完成目標の操作：StuffsでEmptyを作る → InspectorのAdd Componentで `Transform`・`UiElement`・`Image` を検索して付ける → Projectへ画像を取り込み `Sprite` 欄で選ぶ → Inspectorで配置・色・`Order`を変える → 保存 → 開き直して同じ表示になる。親を含む例も保存往復とCloneで確認する。

## Text Componentの表示

`Text`（`core.text`）は内容・文字色・フォントサイズ・行間を持ち、同じオブジェクトの `Transform`・`UiElement` が解決した領域へ同梱フォントで描きます。`Content = "New Text"`・`Color = White`・`FontSize = 24`・`LineSpacing = 1.2`・`Order = 0` が既定値です。左寄せ・上起点で領域幅で折り返し、内容が空のときは描きません。同じオブジェクトのImage＋Textは一単位として大きい方の `Order` で並べ替え、Imageの後にTextを描きます。独立した順序が必要なら別オブジェクトにします。

操作：Stuffsの右クリックメニュー「UI → Text」で作る（選択中があればその子）→ Inspectorで内容・色・サイズ・行間を変える → 保存 → 開き直す。Scene View／Gameの描画、Scene ViewでのUiElement矩形による選択、保存・Clone・欠落メンバーの既定値読み込みに対応します。GameにはText選択機能はありません。高さ方向はUiElement矩形でクリップせず、ビューポートのみで切ります。領域外へあふれた文字は選択範囲を広げません。寄せ・フォント素材の指定、Inspectorでの複数行編集は後続です。確定した仕様は[設計書](docs/EngineArchitecture.md#uiコンポーネント)、検証状況は[実装計画](docs/ImplementationPlan.md#text-component2026-09-24)を参照してください。

## Game表示とButton操作

Play中のGameタブに実行用Sceneを描き、Buttonを押すと自作C#が呼ばれてConsoleにログが出る。Textは上記の別工程で追加済み。ObjectRef・InputField・サイズ変更・回転Gizmo・単体Player配布は対象外。確定した仕様は[設計書](docs/EngineArchitecture.md#v5前半game表示とbutton操作)、検証状況は[実装計画](docs/ImplementationPlan.md#game表示とbutton操作2026-09-23)を参照する。

Stuffsで作ったオブジェクトへ `Transform`・`UiElement`・`Image`・`Button` を付け、Inspectorで配置と `Interactable` を設定する。クリック処理はButton自身の `Clicked` イベントへコードから登録する。専用のHandlerコンポーネントは不要。

実行用Sceneを持つ呼び出し側での接続例（`source` は編集用Scene、`registry` はComponent登録、`buttonId` は対象ID）：

```csharp
using var runtime = new SceneRuntime(source, registry);
var button = runtime.Scene.Objects.Single(item => item.Id == buttonId)
    .GetComponent<PureEngine.Core.Button>()!;
var count = 0;
button.Clicked += context => Log.Info($"Clicked {context.ButtonObject.Name} x{++count}");
runtime.Start();
// 入力側で runtime.EnqueueButtonClick(buttonId)、更新側で runtime.Step(dt) を呼ぶ。
```

`PlaySession` を使う場合も `Prepare` 後の `session.Runtime.Scene` に対して登録する。購読は保存・Cloneされないので、Playごとに実行用インスタンスへ登録し直す。解除は `button.Clicked -= callback`。EditorのInspectorからメソッドを選ぶ機能や、ゲームComponentへ実行用Sceneを自動注入する機能は未実装であり、上の例は実行用Sceneを取得できる呼び出し側向け。以前の `IUiButtonHandler` 実装Componentは自動では呼ばれないため、`Clicked` への明示登録へ移行する。

- `Button`（`core.button`）は同じオブジェクトの `Transform`・`UiElement` で領域を決め、見た目は同じオブジェクトの `Image` を使う。`Interactable` だけを保存し、押下・ホバー・フォーカスは保存しない。
- 通常・ホバー・押下・無効・キーボードフォーカスを重ね表示で区別する。保存済みの `Image.Color` は書き換えない。
- 重なったButtonは手前の1つだけが反応する。左ボタンで押したButton上で左ボタンを離したときだけ1回通知し、外で離すとキャンセルする。`Interactable=false`・0サイズ・判定不能な変換は対象外。親の無効化は子へ波及しない。
- 購読なしは無反応、複数の購読は登録順に呼ばれる。クリックは更新境界で届き、例外はConsoleへ報告して安全に停止する。
