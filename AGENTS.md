# AGENTS.md

## C# の書き方

- このリポジトリは `global.json` で .NET SDK 11 RC1 固定、`net11.0` / C# 15 既定が前提。
- 常にそのSDKで使える最新のC#の書き方を使う。古い等価な書き方は選ばない。
  - 例: `ToArray()` よりコレクション式 `[.. xs]`、`new Dictionary<,>(comparer)` より `[with(comparer)]`。
- 新しい構文に確信がない場合は、Roslyn の提案・Microsoft Learn の最新ドキュメントを確認してから書き、必ずビルドで検証する。
