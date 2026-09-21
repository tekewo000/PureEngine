# PureEngine

C#で作る、UI中心の2Dマルチプレイゲーム向けエディター。
現在はペインを備えたエディターで、空のオブジェクトの追加・選択・名前変更・削除ができます。

設計方針・Attribute・Priority・保存データ・将来の構想は [EngineArchitecture.md](EngineArchitecture.md) にまとめています。

## 現在できること

- 灰色のダークテーマで、タブの切り替えとペインのサイズ変更ができる。
- Assetsにコンパイル済みのサンプルクラス `PlayerStats` と `RoundSettings` を表示する。クリックや上下キーで選択でき、クラス名と名前空間を確認できる。
- 一覧は `ComponentAssets.cs` の `Types` に `typeof(...)` で明示登録する。自動探索や外部C#ファイルの取り込みは未実装。
- Scene View／Gameは左、Stuffsは中央、Inspectorは右に配置する。
- AssetsのクラスをStuffsのオブジェクト行、または選択中オブジェクトのInspectorへドラッグ＆ドロップしてアタッチする。追加したクラス名はInspectorのComponentsに表示する。同じ型の重複、Stuffsの余白、未選択のInspectorへのドロップは受け付けない。
- Stuffsの右クリックメニュー「Add Empty」でオブジェクトを追加し、Inspectorの「Name」で名前を編集する。
- オブジェクトを右クリックして「Delete」、またはStuffsで選択してDeleteキーで削除する。余白を右クリックすると選択が解除され、削除は無効になる。
- 現在のデータはメモリ内のみ。保存・読み込みは未実装で、終了すると失われる。
- .NET 11 RC1とAvaloniaでビルドし、Windows上で表示を確認済み。

## 技術

- .NET SDK 11.0.100-rc.1.26425.128（global.jsonで固定）
- Avalonia 12.1.2 / Fluent ダークテーマ

## 起動

PowerShellでリポジトリのフォルダを開き、次を実行します。

```powershell
./run-editor.ps1
```

起動スクリプトは `%LOCALAPPDATA%/PureEngine/dotnet/dotnet.exe` があれば使用し、
なければPATH上のdotnetを使用します。

別のPCでは、指定の.NET 11 SDKをインストールしてください。
公式のdotnet-install.ps1で上記のユーザーフォルダへ導入することもできます。

通常のdotnetコマンドが指定SDKを認識する環境では、以下でも起動できます。

```powershell
dotnet run --project PureEngine.Editor
```

## 構成

- `PureEngine.Core/`：シーン・オブジェクト・属性の定義。
- `PureEngine.Editor/`：Avaloniaによる編集画面。
- `PureEngine.Core.Checks/`：Coreの動作チェック。
- [EngineArchitecture.md](EngineArchitecture.md)：設計仕様と未決定事項。

Coreのクラスのアタッチ・取得と属性検出、Editorからのアタッチは実装済み。クラスの値の編集、ゲーム実行、Steam連携、UI配置、シーン保存はまだ実装していません。

## コアの動作確認

追加・名前変更・空名の拒否・IDの維持・変更通知・削除を確認します。SDKが使える環境で実行してください。

```powershell
dotnet run --project PureEngine.Core.Checks
```
