# VRCAvatarChanger

VRChat の外部から(ゲームを開かなくても・ゲーム起動中でも)アバターを着替えられる Windows 用デスクトップアプリです。
VRChat 公式 API の `PUT /avatars/{id}/select` を叩いて着替えるので、VRChat 起動中ならゲーム内にも即座に反映されます。

## 機能

- VRChat アカウントでログイン(2FA: 認証アプリ / メール / リカバリーコード対応)
- **Discord / Google / Apple / Steam などの連携ログイン**(アプリ内ブラウザで vrchat.com にログイン → セッションを取り込み)
- 自分がアップロードしたアバター一覧・お気に入りアバター一覧の表示(サムネイル付き)
- **パブリック**: アプリ独自のアバターリスト。VRChat のお気に入り上限とは無関係に、パブリックアバターをいくつでも登録できる
  (ID / URL で追加、他タブから右クリックで追加、右クリックで削除。「追加日」順はリストに追加した日時)
- **グループ化**: タイルを別のタイルに重ねる(ドラッグ&ドロップ)と、衣装違いなどを 1 つのグループにまとめられる。
  グループは重なったタイルとして表示され、ダブルクリックで開く。右クリックで名前変更 / 解除。どのタブでも使え、保存先は `%AppData%\VRCAvatarChanger\groups.json`
- リスト表示 / ボックス(グリッド)表示の切り替え。ボックス表示は 1 行 3〜10 体をスライダーで調整(設定は保存)
- 並び順の切り替え: 追加日 / 更新日 / 名前(昇順・降順、既定は追加日が新しい順。設定は保存)
- フィルタ: お気に入りタブは VRChat のお気に入りグループ、「自分のアバター」と「パブリック」は自分で付けたタグで絞り込み
  (右クリック →「タグを編集...」。グループは「全員のタグを編集...」で全メンバーに適用。保存先 `tags.json`)
- 名前・作者名・ID で絞り込み
- 一覧から選択(またはダブルクリック)して着替え
- セッションクッキーを保存し、次回起動時は自動ログイン(パスワードは保存しません)
- VRChat の OSC 出力(`/avatar/change`)を受信して「現在のアバター」表示をリアルタイム更新(ゲーム内で着替えた場合も反映)
- OSC 連携中はローカル(`127.0.0.1:9000`)への `/avatar/change` 送信で即座に着替え(サーバー経由の反映待ちなし)
- VRChat 連動(ツールバーでオン/オフ): Windows 起動時にタスクトレイで待機し、VRChat の起動を検知して自動でウィンドウを開く

## OSC について

着替えは 2 経路のハイブリッドです。VRChat が起動していて OSC が有効なら、ローカルの OSC 入力
(`/avatar/change` → `127.0.0.1:9000`)でゲームに直接切り替えさせます(即時反映。サーバー → ゲームの
イベント経路を通らないため、まれにある「切り替わらない」現象も起きません)。
ゲームからの確認が取れない場合(VRChat 未起動・OSC 無効など)は従来どおり Web API で切り替えます。
ゲーム未起動時に API で着替えると、次回起動時のアバターとして反映されます。
現在のアバター表示は OSC 受信(既定 `127.0.0.1:9001` を待ち受け)で追従します。
OSC 連携には VRChat 側で OSC を有効にしてください(アクションメニュー → Options → OSC → Enabled)。
VRCX など他のツールが 9001 番を使っている場合は同時受信できないことがあるので、その場合は OSC ルーター(VOR 等)で分配してください。

## 必要環境

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)(ビルド用)

## フォルダ構成と使い方

```
VRCAvatarChanger\
  VRCAvatarChanger.exe   起動用(tools\build.ps1 が作る単一 exe。普段はこれをダブルクリック)
  README.md              この文書
  src\                   ソース一式(csproj, .cs, .xaml, Themes, アイコン)。ビルド出力もこの下(src\bin, src\obj)
  tools\                 build.ps1(普段用 exe を作る)/ publish.ps1(配布 zip を作る)
  docs\                  README-配布用.txt(配布 zip に同梱される利用者向け説明)
  dist\                  publish.ps1 の出力先(配布 zip)
```

普段使う exe を作る(ソースを変えたあとに作り直す):

```bash
powershell -ExecutionPolicy Bypass -File .\tools\build.ps1
```

直下の `VRCAvatarChanger.exe` は .NET 10 デスクトップランタイムがある PC 用の単一ファイル(約 1.5MB)です。
開発中に素早くビルドするだけなら `src` フォルダで `dotnet build` でもかまいません(出力は `src\bin` に入るので直下は汚れません)。

## Discord / Google などでのログインについて

VRChat の外部連携ログインは Web サイト上の OAuth としてのみ提供されており、外部アプリ向けの API はありません。
そのため「Discord / Google などでログイン (ブラウザ)」ボタンでは、アプリ内に埋め込んだブラウザ(WebView2)で
vrchat.com のログイン画面を開き、そこでログインしてもらったあと、ブラウザが持つセッションクッキー(`auth` / `twoFactorAuth`)を
アプリ側に取り込んでいます。2 段階認証もブラウザ内でそのまま入力できます。

- WebView2 ランタイムが必要です(Windows 11 には標準搭載。無い場合は起動時に案内が出ます)。
- 埋め込みブラウザのデータは `%AppData%\VRCAvatarChanger\WebView2` に保存されます。

## UI の設計方針

日常的に使う「道具」として、Windows 11 の Fluent に寄せた落ち着いた見た目にしています。

- **テーマ**: Windows の「アプリのモード」(ダーク / ライト)に自動で追従。タイトルバーも同色。環境変数 `VRCAC_THEME=light|dark` で強制できます。
- **色**: ニュートラル基調 + アクセント 1 色(青)。意味色は成功(緑)/ エラー(赤)のみ。
- **形**: 操作できるもの(ボタン・入力・行)は角丸 6px、面(カード)は 8px に統一。
- **状態**: すべての操作にホバー / 押下 / フォーカス / 無効の状態を用意。一覧は「読み込み中(スケルトン)」「空(理由と次の一手を提示)」「エラー」を明示。
- **文字**: Segoe UI Variable、ID はモノスペース。ラベルは入力欄の上に置く(プレースホルダーをラベル代わりにしない)。
- **動き**: 自動アニメーションなし。状態変化のみ。
- トークンは `src\Themes\Dark.xaml` / `src\Themes\Light.xaml`、共通スタイルは `src\App.xaml` に集約。

Debug ビルドでは `VRCAC_UI_PREVIEW=1` を付けて起動すると、API に接続せずダミーデータでメイン画面を確認できます
(`VRCAC_UI_PREVIEW_STATE=loading|empty` で各状態)。

## 配布(第三者に渡す)

1. `src\VRChatApi.cs` の `Contact` を配布者の連絡先(メールアドレス、または実在する配布ページ URL)に変える。
   VRChat API の規約で必須。ダミー(example.com 等)は VRChat 側で拒否される。`publish.ps1` は既定値のままだと止まる。
2. `src\VRCAvatarChanger.csproj` の `<Version>` を上げる(アプリ内「このアプリについて」とファイル名に反映される)。
3. 次を実行する。

```bash
powershell -ExecutionPolicy Bypass -File .\tools\publish.ps1
```

`dist\VRCAvatarChanger-vX.Y.Z-win-x64.zip` ができる。中身は .NET ランタイム同梱の単一 `VRCAvatarChanger.exe`(約 60MB)と
利用者向けの `はじめにお読みください.txt`(元: `docs\README-配布用.txt`)。利用者側のインストール作業は不要(解凍して exe を実行するだけ)。
直下の普段用 exe と違い、配布版は .NET が入っていない PC でも動く。

### 利用者向けの導線(アプリ内)

- 右上「使い方」(F1)から、使い方 / よくある質問 / このアプリについて(バージョン・保存フォルダを開く)
- ログイン画面にも「使い方とよくある質問を見る」
- 一覧が空のときは、その理由と次にやることを画面内で案内
- エラーは日本語で原因と対処を表示(通信不可、セッション切れ、非公開アバター、レート制限など)。セッション切れは自動でログイン画面に戻る
- 二重起動すると既存ウィンドウを前面に出す。ウィンドウの位置とサイズは記憶
- 予期しない例外は `%AppData%\VRCAvatarChanger\error.log` に記録(問い合わせ時に送ってもらう)

### 自動アップデート (GitHub Releases)

利用者のアプリは起動時に GitHub Releases の最新バージョンを確認し、新しければツールバーに「vX.Y.Z に更新」ボタンを出します。
押すと確認ダイアログ → zip をダウンロード → SHA-256 検証 → 自分自身を差し替えて再起動、まで自動で行います(勝手に更新はしません)。

有効にする手順:

1. GitHub にリポジトリを作り、`src\Updater.cs` の `GitHubRepo` を `"ユーザー名/リポジトリ名"` に設定する(空のままなら更新確認は無効)。
2. バージョンを上げて `tools\publish.ps1` を実行すると、`dist` に zip と `SHA256SUMS.txt` ができ、リリース用のコマンドが表示される。
3. タグ `vX.Y.Z` でリリースを作り、**zip と SHA256SUMS.txt の両方**を添付する(gh CLI なら表示されたコマンドをそのまま実行)。

備考: 更新チェックは github.com への HTTPS アクセスのみ。確認に失敗しても何も表示せず通常起動します。
Debug ビルド限定の環境変数 `VRCAC_UPDATE_REPO` / `VRCAC_UPDATE_API` でテスト先を差し替えられます(Release には含まれません)。

### 配布時の注意

- 初回起動時に SmartScreen の警告が出ることがある(署名なしのため)。`はじめにお読みください.txt` に回避手順を記載済み。
  コード署名証明書があれば `signtool` で exe に署名すると警告が出なくなる。
- Debug ビルド限定の環境変数: `VRCAC_UI_PREVIEW=1`(ダミーデータ表示)、`VRCAC_ALLOW_MULTI=1`(二重起動許可)。Release には含まれない / 効かない。

## 隠し機能

- **10 体ごとの色分け**: メイン画面で `Ctrl+Shift+C`。一覧の下から上に向かって数え、10 体ごとにアバターの背景色が変わる(6 色ループ)。
  グループタイルは 1 とカウント。有効中は右クリックに「カウントから除外」が出て、除外したものは数に入らず色も付かない。
  状態と除外リストは `settings.json` に保存(`StripeColors` / `StripeExcluded`)。

## 保存されるデータ

- `%AppData%\VRCAvatarChanger\session.json` — VRChat のセッションクッキー(`auth` / `twoFactorAuth`)。
  **Windows の DPAPI(CurrentUser)で暗号化**されており、同じ Windows ユーザーアカウントでしか復号できません。「ログアウト」で削除されます。
- `%AppData%\VRCAvatarChanger\WebView2\` — ブラウザログイン用の埋め込みブラウザのプロファイル。
  ログイン成功時にクッキー・閲覧データを消去し、「ログアウト」でフォルダごと削除します。
- `%AppData%\VRCAvatarChanger\settings.json` — 表示形式・列数・並び順などの表示設定(機密情報は含みません)。
- `%AppData%\VRCAvatarChanger\groups.json` — グループ(名前とアバター ID の一覧)。
- `%AppData%\VRCAvatarChanger\public_avatars.json` — パブリックリスト(アバター ID・名前・作者・サムネ URL・追加日時)。
  起動時は API を叩かずこのキャッシュを表示し、「再読み込み」で情報を取り直します。

## セキュリティ上の設計

- **パスワードは保存しない**。通常ログインではメモリ上で API に渡した後すぐ破棄します。
- **セッションは DPAPI で暗号化保存**。ファイルをコピーされても他の PC / 他ユーザーでは復号できません。
- **通信は HTTPS のみ**、証明書検証は既定(無効化していません)。クッキーは `api.vrchat.cloud` にのみ送信されます。
- **画像取得は VRChat のホスト(https)に限定**。API 応答に含まれる URL でも他ホストへはリクエストしません。
- **アバター ID は `avtr_` + UUID の形式を厳密に検証**してから URL に埋め込みます(パス操作の防止)。
- **埋め込みブラウザは最小権限**: https 以外への遷移拒否、ポップアップは同一ウィンドウ内、パスワード保存 / 自動入力 / DevTools / ダウンロード / 権限要求(カメラ等)をすべて無効化。
- **ログ・テレメトリなし**。外部に送るのは VRChat API へのリクエストだけです。
- 依存パッケージは Microsoft 製の `Microsoft.Web.WebView2` のみ。

### 知っておくべき制約

- VRChat の API にはアバター切り替えだけに権限を絞ったトークンが存在しません。このアプリが保持するセッションは
  **VRChat アカウント全体を操作できる権限**を持ちます(公式サイトにログインしているのと同じ)。
  そのため `%AppData%\VRCAvatarChanger` を他人と共有したり、配信画面に映したりしないでください。
- 使い終わって長期間使わない場合は「ログアウト」でセッションを破棄しておくのが安全です。
- 非公式 API のため、VRChat の利用規約・仕様変更の影響を受ける可能性があります。

## 注意

- VRChat API は連絡先入りの `User-Agent` を要求します。配布する場合は `src\VRChatApi.cs` の `Contact` を自分の連絡先に書き換えてください。
- 非公式 API 利用のため、VRChat 側の仕様変更で動かなくなる可能性があります。
- 短時間に大量のリクエストを送るとレート制限(HTTP 429)を受けます。

## ファイル構成

| ファイル | 内容 |
| --- | --- |
| `src\VRChatApi.cs` | VRChat API クライアント(ログイン / 2FA / アバター取得 / 着替え / セッション保存) |
| `src\OscListener.cs` | OSC 送受信(`/avatar/change` の検知と送信、依存ライブラリなし) |
| `src\BrowserLoginWindow.xaml(.cs)` | 埋め込みブラウザによる連携ログイン(Discord / Google 等) |
| `src\MainWindow.xaml(.cs)` | UI とロジック |
| `src\Settings.cs` | 表示設定の読み書き |
| `src\PublicAvatarStore.cs` | パブリックリストの保存・読み込み |
| `src\GroupStore.cs` | グループ(衣装違いのまとめ)の保存・読み込み |
| `src\GroupPickerWindow.xaml(.cs)` | グループの選択 / 作成 / 名前変更ダイアログ |
| `src\HelpWindow.xaml(.cs)` | 使い方 / よくある質問 / このアプリについて |
| `src\Updater.cs` | GitHub Releases からの自動アップデート |
| `src\App.xaml(.cs)` | 共通スタイル、テーマ切り替え、タイトルバー配色、グローバル例外処理 |
| `src\Themes\Dark.xaml`, `src\Themes\Light.xaml` | 色トークン |
| `src\app.ico`, `src\app.manifest` | アプリアイコン、DPI 設定 |
| `tools\build.ps1`, `tools\publish.ps1` | 普段用 exe の生成、配布 zip の生成 |
| `docs\README-配布用.txt` | 配布 zip に同梱する利用者向け説明 |
