# AI日本語IME支援アプリ（IMESupport） 実装完了レポート

## 1. 成果物の概要
Web版Geminiで検討されていた構想をベースに、Windows環境で常駐動作するAI文章推敲・IME補助アプリケーション **「IMESupport」** を制作しました。

- **トリガー**: **変換キー2回押し（ダブルタップ）**（低レベルキーボードフックで検出、IME再変換を自動抑制）
- **AIエンジン**: **Gemini API**（`gemini-2.5-flash`, `gemini-2.0-flash`, `gemini-3.5-flash-lite` などの高速・軽量モデルに柔軟対応）
- **推敲機能**:
  - 同音異義語の誤変換修正（例: 「変改」→「変換」、「機構」→「気候」）
  - 文脈不整合の自然な修正
  - 脱字・送り仮名の補正
  - **タイピングミス（誤打鍵・母音/子音抜け・隣接キー誤打）による不自然な文字列の予測修正**
- **UX・動作**:
  - メモ帳、ブラウザ、チャット、WordなどあらゆるWindowsアプリ上で動作
  - 選択範囲のテキスト取得・置換はもちろん、**未選択時でも直前の一行・段落を自動検出して推敲**
  - **マウスのトリプルクリック相当のスマート段落選択**: 自動折り返し（ワードラップ）を遡り、入力欄全体を巻き込まずに直前の改行（Enter）までの「1段落」のみを正確に選択
  - **推敲履歴の差分色分けハイライト**: 修正前（赤色背景・取消線）と修正後（緑色背景・太字）の比較プレビューをリアルタイム表示
  - タスクトレイ（画面右下）に常駐し、タスクバーやコンソール画面を残さない設計
  - デスクトップ通知の完全ON/OFF切り替え（デフォルトは通知なしで静かに直接置換）

---

## 2. 成果物ファイル構成

- **実行ファイル**: `c:\AI\Antigravity\IMESupport\publish\IMESupport.exe` (約212KBの単一EXE)
- **ソースコード**:
  - [`App.xaml`](file:///c:/AI/Antigravity/IMESupport/App.xaml) / [`App.xaml.cs`](file:///c:/AI/Antigravity/IMESupport/App.xaml.cs): ライフサイクル・多重起動防止・タスクトレイ常駐
  - [`Models/AppSettings.cs`](file:///c:/AI/Antigravity/IMESupport/Models/AppSettings.cs): 設定保持・JSON保存
  - [`Models/CorrectionHistory.cs`](file:///c:/AI/Antigravity/IMESupport/Models/CorrectionHistory.cs): 推敲履歴管理
  - [`Services/GeminiService.cs`](file:///c:/AI/Antigravity/IMESupport/Services/GeminiService.cs): Gemini REST API通信・パース・クレンジング
  - [`Services/KeyboardHookService.cs`](file:///c:/AI/Antigravity/IMESupport/Services/KeyboardHookService.cs): `WH_KEYBOARD_LL` による変換キー2回検知
  - [`Services/TextReplacementService.cs`](file:///c:/AI/Antigravity/IMESupport/Services/TextReplacementService.cs): `SendInput` によるテキスト取得・自動置換・クリップボード復元
  - [`Services/StartupManager.cs`](file:///c:/AI/Antigravity/IMESupport/Services/StartupManager.cs): Windows起動時のスタートアップ登録
  - [`UI/SettingsWindow.xaml`](file:///c:/AI/Antigravity/IMESupport/UI/SettingsWindow.xaml) / [`UI/SettingsWindow.xaml.cs`](file:///c:/AI/Antigravity/IMESupport/UI/SettingsWindow.xaml.cs): 設定・接続テスト・プレビューUI
  - [`UI/HistoryWindow.xaml`](file:///c:/AI/Antigravity/IMESupport/UI/HistoryWindow.xaml) / [`UI/HistoryWindow.xaml.cs`](file:///c:/AI/Antigravity/IMESupport/UI/HistoryWindow.xaml.cs): 推敲履歴一覧・コピーUI
  - [`UI/TrayIconManager.cs`](file:///c:/AI/Antigravity/IMESupport/UI/TrayIconManager.cs): タスクトレイアイコン・右クリックメニュー制御
  - [`README.md`](file:///c:/AI/Antigravity/IMESupport/README.md): 取扱説明書

---

## 3. テスト・検証結果

- **単体テスト**: `dotnet test` により全3件のテスト（設定読み込み、変更検知、レスポンスクレンジング処理）が合格。
- **ビルド・発行**: `dotnet publish` によりエラー・警告0で単一バイナリ `publish\IMESupport.exe` の生成が完了。

---

## 4. 利用手順

1. `c:\AI\Antigravity\IMESupport\publish\IMESupport.exe` を実行します。
2. 初回起動時に設定画面が表示されるので、ご自身の **Gemini API キー** を入力し、「API接続テスト」で通信成功を確認後、「保存して閉じる」をクリックします。
3. 任意のアプリ（メモ帳、ブラウザ、Discord、Slack等）で文字を入力し、**変換キーを素早く2回（トントン）** 押してください。
4. 自動的にGeminiが文章を推敲し、タイピングミスや誤変換が正しい文章へと即座に置き換わります。
