# PureEngine

C#で作る、UI中心の2Dマルチプレイゲーム向けエディター。
現在はLauncherからProjectを開き、オブジェクト・アタッチしたクラスの値を編集し、YAMLで保存・復元できます。

設計方針・Attribute・Priority・保存データ・将来の構想は [EngineArchitecture.md](docs/EngineArchitecture.md) にまとめています。
実装済みの範囲・制限・次の作業・検証状況は [実装計画・進捗](docs/ImplementationPlan.md) にまとめています。

## 現在できること

- 灰色のダークテーマで、タブの切り替えとペインのサイズ変更ができる。
- 起動時にLauncherを表示し、Projectの新規作成・既存Projectの選択・最近開いたProjectからの再開ができる。
- 下部のProject Explorerは左にフォルダツリー、右にファイル一覧を表示する。プロジェクトに存在するフォルダとファイルだけを表示し、組み込みサンプルの一覧は追加しない。
- 自作C#はProject内の任意フォルダから読み込み、元のフォルダ内のファイルをドラッグしてアタッチできる。エンジン側への手動登録は不要。組み込みサンプルのComponents一覧は表示しない。
- Scene View／Gameは左、Stuffsは中央、Inspectorは右に配置する。
- Project Explorerの自作C#ファイルをStuffsのオブジェクト行、または選択中オブジェクトのInspectorへドラッグ＆ドロップしてアタッチする。追加したクラス名はInspectorのComponentsに表示する。同じ型の重複、Stuffsの余白、未選択のInspectorへのドロップは受け付けない。
- Stuffsの右クリックメニュー「Add Empty」でオブジェクトを追加し、Inspectorの「Name」で名前を編集する。
- オブジェクトを右クリックして「Delete」、またはStuffsで選択してDeleteキーで削除する。余白を右クリックすると選択が解除され、削除は無効になる。
- Inspectorで `[Inspector]` 付きのstring・int・float・boolを編集し、YAMLで保存・読み込みできる。
- ゲームのクラスは普通のC#コンストラクタでサービスを受け取れる。保存データは `[Inspector]` に置き、保存値を使う初期化は `Start` に書く。編集時の追加・読み込みと Play 時の複製は、Game側の一箇所の登録から作った独立したサービス群で生成する。
- ライフサイクルのあるクラスにはアタッチ設定としてStart／Update／Destroy Priorityを表示・編集できる。存在しないライフサイクルは表示しない。
- ツールバーのPlay／Stopで編集中シーンの複製を開始・停止できる。Play中は約60Hzで更新し、Stopで終了する。実行中の編集・切替は無効化する。
- .NET 11 RC1とAvaloniaでビルドし、Windows上で表示を確認済み。

## 技術

- .NET SDK 11.0.100-rc.1.26425.128（global.jsonで固定）
- Avalonia 12.1.2 / Fluent ダークテーマ
- YamlDotNet 18.1.0

| 対象 | ターゲット | C# |
| --- | --- | --- |
| Engine・Runtime・Editor・チェック | net11.0 | 15（SDK既定） |
| 生成するゲーム用csproj | net11.0 | 13（組み込みRoslyn 4.12のコンパイル設定に合わせる） |
| PureEngine.Analyzers | netstandard2.0 | 15（外部エディターの実行環境との互換性を維持） |

## シーンの保存・読み込み

- File → Open Scene（Ctrl+O）：シーンを読み込む。
- File → Save Scene（Ctrl+S）：上書き保存。初回は保存先を選ぶ。
- File → Save Scene As（Ctrl+Shift+S）：別名で保存。
- 拡張子は `.pure.scene.yaml`。例として [Examples/Main.pure.scene.yaml](Examples/Main.pure.scene.yaml) を開ける。
- 未保存の変更はタイトルの `*` で示す。別シーンを開くときや終了時にSave／Discard／Cancelを選ぶ。
- Inspectorに入力エラーがある間は保存しない。成否とエラー詳細は画面下部に表示する。

保存対象はオブジェクトのID・名前、登録済みクラスの固定ID、`[Inspector]` 付きの値、アタッチごとのPriority。
読み込みは別のSceneへ復元し、成功してから現在のSceneと入れ替える。保存は同じフォルダの一時ファイルへ書き終えてから置き換える。
YAMLのコメントは再保存で失われる。Parent、オブジェクト参照、Editorのペイン配置は現在の保存対象に含めない。旧形式（prioritiesなし）はすべて0として読み込む。

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
ProjectごとのC#読み込み・自動反映に対応。素材の取り込みは未実装。


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
- 未保存のオブジェクト・Inspector値・Priority・選択を引き継ぎます。新しいInspectorメンバーは初期値を使います。
- コンパイル失敗、生成失敗、アタッチ済みクラスの削除、保存対象メンバーの削除・型変更、設定済みPriorityの移行不能時は反映を中止し、直前の正常なコードと編集データを保持します。理由をConsoleに表示し、修正して保存すると再試行します。
- 保存用の型IDは初回に割り当て、`.pureengine/types.json` に保持します。同じファイル内でのクラス名・名前空間の変更でも、アタッチ・Inspector値・Priorityを引き継ぎます。再起動後も同じIDで読み込みます。この管理ファイルもプロジェクトと一緒に保存・移動してください。
- 複数クラスがあるファイルでは、同名クラスを先に照合し、残った旧・新クラスが1対1の場合に改名として引き継ぎます。複数クラスの同時改名など対応が曖昧な場合は反映を止めます。一つずつ改名して保存してください。ファイル移動とクラス改名も別々に反映してください。
- 旧バージョンの保存シーンは、完全名または一意なクラス名で初回のID管理へ移行します。ID管理導入前にクラス名自体も変わっていた場合は、元の名前で一度読み込んでから改名してください。
- 起動時からコンパイルに失敗し、起動シーンがその自作型を参照している場合は、ソースを修正してから開き直してください。自作型を使わない起動シーンなら、エラーをConsoleに残して開けます。

現在はProject内のソースをまとめてコンパイルします。`bin`・`obj`・隠しフォルダ・リンク先フォルダは対象外です。独自csprojの設定、外部NuGet依存の復元、Playの実行状態を保ったコード差し替えは未対応です。起動時・再読み込み時のコンパイルはバックグラウンドで行います。Componentの生成・Scene移行・採用はUIスレッドで行い、採用時点の編集内容を引き継ぎます。Play・ファイル操作・入力エラー中は採用を保留し、連続変更や終了で古くなった結果は破棄します。詳細は[アーキテクチャ改善](docs/ArchitectureImprovements.md)を参照してください。

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

Coreのクラスのアタッチ・取得と属性検出、Editorからのアタッチ・値とPriorityの編集、YAMLシーン保存、Coreのライフサイクル実行（Priority順）とEditorのPlay／Stopによる開始・停止は実装済み。ゲーム画面の描画、Steam連携、ゲーム内UI配置はまだ実装していません。

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

ツールバーのPlayは編集中Sceneの複製で `PlaySession` を作り、約60Hzのタイマーで実測の経過秒を渡して更新します。Inspectorに入力エラーがある間は開始せず、画面下部に理由を表示します。実行中はシーン編集・切替とシーン操作メニューを無効化し、Stopで終了します。開始・更新・終了の失敗とSceneRuntimeのErrorsは画面下部に表示し、失敗後も操作可能な状態へ戻します。ウィンドウを閉じる際も実行中なら終了・解放します。再Playは新しいSceneRuntimeで開始します。描画はまだ行いません。

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
