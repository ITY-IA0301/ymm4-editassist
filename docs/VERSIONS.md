# 保存済みプラグインの全バージョン

2026-10-03時点で手元に残っていた7版と、EditAssist 0.4.0のGitHub登録機能付きソースを保管しています。過去版のZIPと導入ファイルは、再ビルドせず元のバイト列を保存しています。

## 最新版のソース

0.5.1で語尾・話し方の手動指定と参考台本の特徴抽出を追加。163項目の検証とクロスビルド成功。Windows実機と実AI接続は未検証です。

0.5.0のChatGPT支援・字幕調整を作業ブランチに追加。公式YMM4 4.56.1.0を参照したクロスビルド版で、Windows実機と実AI接続は未検証です。

- リポジトリのルート：EditAssist 0.5.1。ソース管理用のGitHub登録処理も含みます。
- [plugins/PreviewLite](../plugins/PreviewLite)：PreviewLite 0.3.0の独立したソース。EditAssistに組み込んだり、同じDLLへまとめたりしていません。

このページは`docs`内にあるため、PreviewLiteのリンクは [リポジトリ上のフォルダー](https://github.com/ITY-IA0301/ymm4-editassist/tree/main/plugins/PreviewLite)も利用できます。

## 過去版・導入ファイル

| プラグイン | 版 | 保存フォルダー |
| --- | --- | --- |
| EditAssist | 0.1.0 | [archive/EditAssist/0.1.0](../archive/EditAssist/0.1.0) |
| EditAssist | 0.2.0 | [archive/EditAssist/0.2.0](../archive/EditAssist/0.2.0) |
| EditAssist | 0.3.0 | [archive/EditAssist/0.3.0](../archive/EditAssist/0.3.0) |
| EditAssist | 0.4.0 | [archive/EditAssist/0.4.0](../archive/EditAssist/0.4.0) |
| EditAssist | 0.5.0（試用版） | [archive/EditAssist/0.5.0](../archive/EditAssist/0.5.0) |
| EditAssist | 0.5.1（試用版） | [archive/EditAssist/0.5.1](../archive/EditAssist/0.5.1) |
| PreviewLite | 0.1.0 | [archive/PreviewLite/0.1.0](../archive/PreviewLite/0.1.0) |
| PreviewLite | 0.2.0 | [archive/PreviewLite/0.2.0](../archive/PreviewLite/0.2.0) |
| PreviewLite | 0.3.0 | [archive/PreviewLite/0.3.0](../archive/PreviewLite/0.3.0) |

各フォルダーには元の`*-source.zip`と`*-net10.ymme`を保存しています。EditAssist 0.4.0には、最初のソースZIPに加えて`*-source-github.zip`もあります。同じプラグイン版の開発環境追加であり、0.5.0という新しい機能版ではありません。

`archive/manifest.json`には従来15個の元ファイルと0.5.0の2個のサイズとSHA-256を記録します。ファイル名だけでなく、バイト列を照合するための一覧です。登録後のGitHubファイルはGitのblob SHAでも照合しています。

## 注意

- 旧版は比較・復元用です。最新版と同時にYMM4へ導入しないでください。
- PreviewLite 0.2.0には過去の動画参照機能が含まれています。現在はEditAssist 0.3.0以降へ移し、PreviewLite 0.3.0は軽量プレビューのみの設計です。
- 各版の説明・確認済み項目は、その版のZIP内のREADMEと検証記録を参照してください。実際のYMM4で未確認の内容を、GitHubへの保存だけで確認済みに変更していません。
- 動画、YMMP、プロジェクトのバックアップ、個人設定、チャット履歴、YMM4本体は保管対象外です。PreviewLiteのHarmonyは元のMITライセンス文とともに保存します。
- この登録は既存の非公開リポジトリへの保管です。一般公開・GitHub Releasesの公開・新しいライセンスの選択は行っていません。

