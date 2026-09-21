# PureEngine

C#で作る、UI中心の2Dマルチプレイゲーム向けエディター。
現在は空のウィンドウを表示する最小構成です。

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

## ファイル

- `Program.cs`: 起動処理
- `App.axaml`: アプリ共通のテーマ
- `App.axaml.cs`: メインウィンドウの生成
- `MainWindow.axaml`: ウィンドウの見た目
- `MainWindow.axaml.cs`: ウィンドウの初期化

上記のソースは `PureEngine.Editor/` 内にあります。
ゲーム実行部分・Steam連携・編集機能はまだ実装していません。
