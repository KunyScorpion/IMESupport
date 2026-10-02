# AI日本語IME支援アプリ（IMESupport） 実装計画書

## 1. 概要
本プロジェクトは、Windows上で動作する常駐型AI文章推敲・入力支援ツールです。
キーボードの「変換キー2回押し（ダブルタップ）」をトリガーとして、選択されたテキスト（または入力直後の文）の誤変換・同音異義語ミス・文脈不整合をGemini API（高速・低遅延なFlash-Lite系モデル等）により自動推敲し、スムーズに置換します。

---

## 2. システム構成・アーキテクチャ

```mermaid
flowchart TD
    subgraph Windows OS
        User[ユーザー操作] -->|変換キー2回押し| Hook[低レベルキーボードフック (WH_KEYBOARD_LL)]
        Hook -->|トリガー検知| MainApp[IMESupport アプリ (WPF / .NET 8)]
        MainApp -->|Ctrl+C 模擬| TargetApp[入力対象アプリ (メモ帳/ブラウザ/Word等)]
        TargetApp -->|テキスト取得| Clip[クリップボード退避・取得]
    end

    subgraph AI推敲エンジン
        Clip -->|文章送信| Gemini[Gemini API (REST Client)]
        Gemini -->|修正テキスト返却| MainApp
    end

    subgraph 自動置換 & 復元
        MainApp -->|Ctrl+V 模擬| TargetApp
        MainApp -->|クリップボード復元| Clip
        MainApp -->|完了通知 / インジケータ表示| UI[トレイアイコン / オーバーレイ]
    end
```

---

## 3. 主要コンポーネント設計

1. **キーボード監視モジュール (`KeyHookService`)**:
   - `SetWindowsHookEx` (`WH_KEYBOARD_LL`) を利用。
   - `VK_CONVERT` (0x1C / 変換キー) の連続押下（デフォルト: 350ms以内の2連打）を検出。
   - 変換キー2回目検知時、IMEの再変換ポップアップ等の不要な動作を抑制するオプション（キーイベントの破棄/通過制御）。
   - 代替キー（無変換キー、Ctrl2回、Alt2回、カスタムショートカット）への設定切り替えにも対応。

2. **テキスト取得・置換モジュール (`TextReplacementService`)**:
   - 直前のクリップボード内容をメモリへ安全に退避。
   - `SendInput` API による `Ctrl + C` の模擬送信とテキスト取得。
   - テキスト未選択時の処理（直前文字の自動選択フォールバック、または通知）。
   - AI修正完了後、`Ctrl + V` を送信して瞬時に置換。
   - クリップボードを元の内容に安全に復元（クリップボード履歴ツールへの影響を最小化）。

3. **Gemini API クライアント (`GeminiApiClient`)**:
   - Google Gemini REST API (`https://generativelanguage.googleapis.com/v1beta/models/...:generateContent`) を直接呼び出し。
   - モデル名設定（`gemini-2.5-flash-lite`, `gemini-2.0-flash`, `gemini-1.5-flash`, `gemini-3.5-flash-lite` 等を自由に変更可能）。
   - 日本語誤変換・文脈修正に特化したシステムプロンプトの適用。
   - タイムアウト処理、エラーハンドリング。

4. **UI & 常駐管理 (`TrayIcon & SettingsWindow`)**:
   - WPFアプリケーション（起動時はメインウィンドウ非表示、タスクトレイのみ表示）。
   - タスクトレイアイコン（動作中アイコン、通信中アニメーション/ステータス）。
   - 設定画面（WPF）：
     - Gemini APIキー入力
     - モデル選択・カスタムモデル名指定
     - プロンプトのカスタマイズ
     - ダブルタップ判定速度（ミリ秒）調整
     - Windows起動時の自動起動（スタートアップ登録）
   - トレイメニュー：設定、動作一時停止/再開、ログ確認、終了。

---

## 4. 実装フェーズ

- **フェーズ 1: プロジェクト基本構造の作成**
  - .NET 8 WPF プロジェクトのセットアップ
  - 設定管理クラス（`AppSettings`、JSON読み書き）の実装
- **フェーズ 2: Gemini API クライアントの実装**
  - Gemini API との非同期通信処理
  - 日本語誤変換・文脈修正用のプロンプト設計とテスト
- **フェーズ 3: キーフック＆テキスト取得・置換の実装**
  - 低レベルキーボードフックによる「変換キー2回」検知
  - `SendInput` による選択範囲取得、自動置換、クリップボード復元
- **フェーズ 4: UI・タスクトレイ・設定画面の実装**
  - システムトレイアイコンおよびコンテキストメニュー
  - 設定ダイアログ（APIキー設定、モデル切り替え、動作テスト機能）
- **フェーズ 5: ビルド・動作確認と調整**
  - アプリのビルド、単体テスト・動作テスト
  - 単一EXE（Self-contained / PublishSingleFile）の発行準備
