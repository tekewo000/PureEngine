# ゲーム用コンストラクタ注入 提案書

最終更新: 2026-09-22
状態: 実装済み（本書の方針どおりに実装し、検証済み。詳細は EngineArchitecture と ImplementationPlan を参照）

## 1. 採用する方針

ゲームの Component は、普通の C# コンストラクタでサービスを受け取る。
ゲーム開発者が書くのは、Game 側のサービス登録と Component のコンストラクタだけとする。

- サービス登録・解決には Microsoft.Extensions.DependencyInjection（以下 MS DI）を利用する。
- Component は MS DI を参照しない。`new PlayerController(random)` でも使える普通の C# クラスとする。
- `[Inject]`、独自コンテナ、サービスロケーター、注入用の継承やインターフェースは導入しない。
- Core は `Func<Type, object>` の生成関数だけを受け取り、MS DI を知らない。
- DI の対象は、この Engine で作るゲームの Component とサービス。Engine・Editor 全体の DI 化は対象外。

編集時にも Component の実体が必要なため、Engine は編集時の生成にも Game 側の登録を利用する。
ゲーム開発者に Editor 用の別登録や、生成箇所ごとの factory 記述を要求しない。

## 2. ゲーム開発者の書き方

### 2.1 Game 側で一度、登録処理を書く

以下は利用イメージ。`GameServices.Configure` は仮の名前であり、既存のゲーム起動処理に合わせて呼び出し口を決める。
このメソッドを手動で各所から呼ぶ必要はなく、Engine のゲーム起動・編集用の接続部分が呼ぶ。

```csharp
using Microsoft.Extensions.DependencyInjection;

public static class GameServices
{
    public static void Configure(IServiceCollection services)
    {
        services.AddScoped<IRandomService, RandomService>();
        services.AddScoped<BattleSession>();
    }
}
```

「一度書く」は登録コードを一箇所にまとめるという意味。同じ登録処理から、編集用と実行用の独立したサービス群を作る。
Component 自体を MS DI に登録する必要はない。既存の ComponentRegistry への型登録は別の役割として残す。

### 2.2 Component は普通のコンストラクタで受け取る

```csharp
public sealed class PlayerController
{
    private readonly IRandomService _random;
    private readonly BattleSession _session;

    public PlayerController(IRandomService random, BattleSession session)
    {
        _random = random;
        _session = session;
    }

    [Inspector] public int Hp { get; set; } = 100;

    [Start] private void OnStart() { }
    [Update] private void Tick(float dt) { }
}
```

注入のための属性や初期化メソッドは不要。依存は生成時点で揃い、`readonly` に保持できる。
ゲームの保存データは従来どおり `[Inspector]` に置く。

## 3. 背景と解決範囲

元の提案時点では、以下の生成経路がパラメータレスコンストラクタを前提としている。
進行中の実装とは独立した提案であり、実装着手時に最新コードと照合する。

- `ComponentRegistry.Register<T>(string id) where T : class, new()`
- `SceneSerializer.Restore()` の `Activator.CreateInstance(type)`
- `ComponentAssets.TryAttach()` の `Activator.CreateInstance(type)`
- `SceneRuntime` が Play 時に `Clone()` で Component を作り直し、`[Inspector]` 値をコピーする経路

作り済みのインスタンスを `Attach(object)` するだけでは、Clone 後のインスタンスに依存を引き継げない。
Engine が Component を生成する経路に共通の生成関数を通し、Clone 先でもコンストラクタで依存を受け取れるようにする。

対象外:

- フィールド・プロパティ・メソッドへの `[Inject]`。
- 階層コンテナ、SceneObject ごとの Scope、コード生成。
- 毎フレームの自動サービス解決。
- Component 間の自動参照注入や、SceneObject の自動注入。
- Editor 本体・描画・通信などの既存システムを DI 化する改修。

## 4. 責任の分担

| 場所 | 責任 |
| --- | --- |
| Game の登録処理 | サービスの型とライフタイムを登録する |
| Game の Component | 普通のコンストラクタで依存を受け取り、ゲーム処理を書く |
| Engine と Game の接続部分 | 登録処理を呼び、provider・Scope・factory を作成し、寿命を管理する |
| Core | factory を使って Component を生成し、データ復元とライフサイクル処理を行う |

MS DI の参照は Game の登録処理と接続部分に限定する。
接続部分は既存の起動処理に組み込み、DI 専用の汎用ホストやプラグイン機構は新設しない。

Core に渡す factory の中身は以下だけでよい。

```csharp
Func<Type, object> factory = type =>
    ActivatorUtilities.CreateInstance(scope.ServiceProvider, type);
```

ゲーム開発者が Component ごとにこのコードを書く必要はない。

## 5. Core の変更

### 5.1 型登録

`ComponentRegistry.Register<T>()` の `new()` 制約を外す。
型登録時にはインスタンスを生成しない。サービス解決の成否は実際の生成時に判定する。

### 5.2 生成関数を通す

| 経路 | 変更 |
| --- | --- |
| `SceneSerializer.Restore` / `Clone` | factory を受け取り、Component 生成に使用する |
| `SceneRuntime` の Clone 経路 | 受け取った factory を serializer に渡す |
| `ComponentAssets.TryAttach` | factory を受け取れる呼び出し形を追加する |

API の形状は既存コードに合わせる。基本形は省略可能な `Func<Type, object>? factory` とする。

- factory 未指定なら、従来のパラメータレス生成を使う。
- factory 指定時に生成が失敗したら、そのエラーを報告する。パラメータレス生成で再試行して隠さない。
- factory の戻り値は null でなく、要求された Component の exact type であることを確認する。
- factory は呼び出しごとに新しい Component を返す契約とする。共有したいものは注入するサービス側に置く。
- 作り済みインスタンスの `Attach(object)` は変更しない。
- 同じ SceneObject に同一 exact type を複数 Attach できない既存の制約も変更しない。

## 6. 編集と実行で同じ登録を使う

ゲーム開発者向けの登録口は一つにする。一方、編集と実行でゲーム状態を共有しないよう、サービスの実体は分離する。

| 利用場面 | provider と Scope の寿命 |
| --- | --- |
| 編集 | 編集対象のゲームを開いたときに専用 provider と Scope を作り、閉じる・再読み込みするときに終了する |
| Editor の Play | Play ごとに専用 provider と Scope を作り、Stop で終了する |
| 単体のゲーム実行 | ゲーム起動時に provider と Scope を作り、ゲーム終了時に終了する |

いずれも同じ Game の登録処理を使う。編集用と Play 用の provider は独立しており、Singleton も共有しない。
これにより、編集時にサービスが生成されても、Play の対戦状態や乱数状態には持ち越さない。
Play をやり直すと、サービスも新しく生成される。

編集時の Component 追加とシーン読み込みには編集用 factory を使い、Play の Clone には Play 用 factory を使う。
Root provider から Scoped サービスを直接解決せず、必ず各 Scope を通す。
登録がない既存ゲームは従来のパラメータレス生成で動作する。

### コンストラクタの責務

Component とサービスのコンストラクタは、依存の保持と内部状態の初期化に留める。
通信接続、ゲーム進行、バックグラウンド処理の開始などは、実行開始後の明示的な処理に置く。
編集時にも生成されるため、コンストラクタでゲームを開始しない。

この方針はサービスを「生成できる」ことを前提とする。生成そのものに実行環境が必要なサービスが現れた場合は、まず実処理の開始を生成から分離する。
任意のサービスを編集時にも安全に生成できると保証する仕組みではなく、必要になる前から Editor 専用の代替登録機構は作らない。

## 7. Play の生成順序とデータ復元

1. Game の登録処理から Play 用 provider を作り、Scope を作る。
2. Scope を使う factory を `SceneRuntime` に渡す。
3. 全 Component をコンストラクタ経由で生成し、`[Inspector]` データを復元する。
4. 全体の生成・データ復元・既存の実行前検証に成功してから `Start` を呼ぶ。
5. 実行中の `Update` は従来どおり束縛済みの処理を呼ぶ。
6. Stop では Runtime の終了処理を完了してから Scope、provider の順に終了する。

コンストラクタには YAML の値を渡さない。コンストラクタ実行時点では `[Inspector]` の保存値はまだ復元されていないため、その値を必要とする初期化は `Start` に置く。
注入されたサービス参照は保存・コピーせず、Clone 先のサービス群から新しく解決する。

生成コストは編集時の追加・読み込みや Play 準備時に発生する。毎フレームの注入処理は追加しない。
将来、実行中に Component を生成する機能へ対応する場合は、その生成時にも同じ契約を適用する。

## 8. ライフタイムと後始末

### 8.1 サービス

| 登録 | 意味 |
| --- | --- |
| `Scoped` | 一つの編集期間または一回のゲーム実行の中で共有する。対戦状態などの基本的な選択肢 |
| `Singleton` | その provider の中で共有する。編集と Play、異なる Play の間では共有しない |
| `Transient` | 解決ごとに生成する。注入後に毎フレーム作り直すことはない |

初期構成は provider ごとに Scope 一つなので、Scoped と Singleton の共有範囲は実質的に近い。
ゲーム実行単位の状態は Scoped を基本とし、Play を跨ぐ保存は別の保存処理で扱う。

DI が生成・所有する disposable サービスは DI 側で後始末する。Component は注入されたサービスを勝手に Dispose しない。
外から作り済みのインスタンスを登録する場合は、その所有者が後始末を行う。Scope 終了を「全オブジェクトの自動破棄」とは扱わない。

### 8.2 Component

`ActivatorUtilities.CreateInstance` で生成する Component 自体は DI コンテナの所有物ではない。Engine が後始末を担当する。

- `[Destroy]` の対象・呼び出し条件は既存のライフサイクル契約を維持する。
- factory 経由で生成した Component が `IDisposable` を実装する場合、Engine が一度だけ Dispose する。
- 正常終了では、既存の終了コールバックを完了し、Component を Dispose してからサービスの Scope を終了する。
- 生成や復元の途中で失敗した場合、既に生成済みの Component を生成の逆順で Dispose し、Scope と provider も終了する。どの `Start` も呼ばない。
- 一つの終了処理が例外になっても残りの後始末を続け、失敗内容を報告する。
- コンストラクタが完了せず返ってこなかった Component の内部資源は、コンストラクタ側で失敗時に解放する。

`[Destroy]` はゲームの終了処理、`Dispose` は所有資源の解放として責務を分け、同じ資源を二重解放しない。
手動 `Attach(object)` の既存の所有権はこの提案で変更せず、factory が生成したものを識別して管理する。
非同期の後始末が必要な型を採用する場合は、利用前にホストの非同期終了経路を整える。同期終了だけで対応済みとは扱わない。

## 9. 検証とエラー報告

- 未登録の必須依存、コンストラクタ選択の曖昧さ、循環依存などの解決規則は MS DI に任せる。
- Component は public コンストラクタ一つを基本とする。Core に独自のコンストラクタ選択処理を実装しない。
- provider 作成時には `ValidateScopes` と `ValidateOnBuild` を有効にする。
- Component 自体は DI 登録しないため、provider の検証だけでは十分ではない。実際の全 Component の生成・復元成功を Start の前提にする。
- Engine は対象 SceneObject、Component の `typeId` と型、処理段階を添えてエラーを報告し、元の例外を保持する。
- 引数名などの解決詳細は元例外を保持して伝え、任意の factory から取得できない情報まで必須の報告契約にしない。
- 編集時の追加・読み込みで失敗した場合は、その操作のエラーとして報告し、不完全な Component を正常なものとして残さない。

`ctor = サービス`、`[Inspector] = 保存データ` は設計上の規約とする。
`string/int/float/bool` の一律禁止や、型から「サービスらしさ」を推測する独自検証は追加しない。
サービス参照に `[Inspector]` を付けず、保存可能なデータの判定は既存 serializer の契約に従う。保存対象を黙って除外する仕様は追加しない。

## 10. テストと性能確認

実装時には以下を確認する。

- パラメータレスの既存 Component は、factory なしで従来どおり生成・保存・再生できる。
- 引数付き Component を編集時に追加・保存・再読込し、Play では新しいサービスを使って生成できる。
- 同じ Play 内では Scoped サービスを共有し、編集と Play、異なる Play の間では共有しない。
- 依存不足や復元失敗で、一つも Start せず、生成済み Component と DI 所有サービスの後始末を行う。
- 正常終了時に Component が生きているサービスを使って終了処理でき、二重解放しない。

Component 単体テストは `new PlayerController(mockRandom, session)` でよい。
Clone を通る Runtime テストでは、モックを渡して新しい Component を作る factory を指定する。
作り済み Component の Attach だけで Clone 先にモックが引き継がれるとは扱わない。

性能は既存の `RuntimeBenchmarks` で準備時間と一ステップの時間・割り当て量を分けて測る。
`ActivatorUtilities` が常に従来の生成より速いとは仮定しない。最初から独自キャッシュや生成コードは追加せず、測定で必要になった場合に検討する。

## 11. 実装順序

1. 最新の生成経路と既存の終了・失敗時契約を確認する。
2. `new()` 制約を外し、生成経路に省略可能な factory を通す。
3. factory 生成 Component の所有権と、正常・失敗時の後始末を整える。
4. 既存のゲーム起動処理にサービス登録の呼び出し口を一つ設ける。
5. 編集・Play・単体実行の接続部分で、同じ登録処理から独立した provider と Scope を作る。
6. 互換性、編集から再生までの一連の操作、ライフタイム、失敗時の動作を確認する。
7. ベンチマークを測り、実装済みになった内容を関連する仕様書へ反映する。

本書の更新段階では、実装コードと他の仕様書は変更しない。

## 12. この方式を選ぶ理由

最優先は、ゲームのクラスを普通の C# として書けること。
コンストラクタ注入なら、必要な依存が型の API に現れ、生成直後から依存が揃い、Engine 外でも同じ書き方で利用できる。

`[Inject]` は編集時のパラメータレス生成に適しているが、「生成後に Engine が注入する」という独自の準備段階を必要とする。
今回はその仕組みを Component に持ち込まず、Engine の生成経路を通常のコンストラクタに対応させる。
MS DI にサービス管理を任せることで、VContainer 相当の独自コンテナを作る必要もない。

参考:

- [Microsoft: Dependency injection guidelines](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection-guidelines)
- [Microsoft: Dependency injection in .NET](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection)
