# dirsizer

[English](README.md) | **日本語**

`dirsizer` は、標準的なファイルシステム走査（再帰的な列挙）を行わず、NTFSの MFT（Master File Table）メタデータを直接解析してフォルダーサイズを高速計算する、 Windows CLI ツールです。読み取りのみ行い、書き込みは行いません。

## ライセンス

DirSizer は MIT ライセンスの下で公開されています。詳細は [LICENSE](LICENSE) を参照してください。

## ステータス

`dirsizer.exe` が通常の入口です。パスを渡すと、そのファイルシステムに対して利用可能かつ検証済みの最良のスキャン方式を自動的に選びます。ユーザーはどの方式が動いたかを意識する必要はありません。管理者権限のある NTFS ドライブルートでは MFT（Master File Table）を直接読み、それ以外（非昇格、非 NTFS、ドライブルート以外のパス）では `FindFirstFileExW` によるディレクトリ走査になります。詳細は下の「dirsizer.exe（統合エントリポイント）」を参照してください。

同じ実装は、方式をあらかじめ知っている利用者向け、または診断・ベンチマーク・開発向けに、5 つの専用実行ファイルとしても提供されています: `dirsizer-fs.exe`（任意のファイルシステム、管理者権限不要）、`dirsizer-mft.exe`（生の `$MFT`、実験的機能）、`dirsizer-fsctl.exe`（`FSCTL_GET_NTFS_FILE_RECORD`、NTFS のリファレンス実装）、`dirsizer-inspect.exe`（`$MFT` 内部の調査、サイズ集計機能なし）、`dirsizer-index.exe`（実験的機能: ボリュームごとに保存したインデックスを USN ジャーナルで最新に保ち、繰り返し分析に使う）。NTFS 向けの 3 つのスキャナーは NTFS ボリュームの MFT を直接読み込む設計になっており、`FindFirstFile` や `Directory.EnumerateFiles` によるパス走査、および USN ジャーナルは使用していません。

## 構成ツール

DirSizer は、同じ実装から構築された 6 つの独立した実行ファイルで構成されています。

| ツール | 用途 | 備考 |
| --- | --- | --- |
| `dirsizer.exe` | **通常はこちらを使用。** パスに最適な方式を自動選択 | オプションに実装方式を指定するものはありません。下の「dirsizer.exe（統合エントリポイント）」と [docs/superpowers/specs/2026-09-23-unified-scan-strategy-design.md](docs/superpowers/specs/2026-09-23-unified-scan-strategy-design.md)（英語）を参照してください。 |
| `dirsizer-fs.exe` | **任意の**ファイルシステムのフォルダーサイズスキャン（管理者権限不要） | `FindFirstFileExW` で複数スレッドを使ってディレクトリ一覧を読み取り、個々のファイルは開きません。NTFS では `dirsizer-mft` より遅く、合計値も NTFS 向けツールとは仕様上異なります。下の「dirsizer-fs.exe」節と [docs/design_fs.md](docs/design_fs.md)（英語）を参照してください。 |
| `dirsizer-mft.exe` | 高速フォルダーサイズスキャン | **【実験的機能】** `$MFT` を大容量ブロック単位で直接読み込みます。検証環境では `dirsizer-fsctl` より約 1.4 倍高速に動作しました。`dirsizer-bulk.exe` から改称（実装は同一）。 |
| `dirsizer-fsctl.exe` | 同上のフォルダーサイズスキャン | `FSCTL_GET_NTFS_FILE_RECORD` を使用するリファレンス実装です。`dirsizer-mft` の動作検証、ベンチマーク、または安全性を重視する環境で使用します。 |
| `dirsizer-inspect.exe` | NTFS `$MFT` 内部構造の解析 | 開発・調査用ツール。単一レコードの属性解析、`$MFT` エクステント、スロット数の確認、Raw 読み込みと FSCTL の比較などを行います（サイズ集計機能はありません）。 |
| `dirsizer-index.exe` | **【実験的機能】** ボリュームごとに保存したインデックスによる繰り返し分析 | 初回は `$MFT` 全体をスキャンし、以降は USN ジャーナルに記録されたレコードだけを読み直します。ドライブ上の任意のディレクトリを指定できます。`--changes` で前回の実行から縮小・増加したディレクトリを一覧表示します。[docs/design_index.md](docs/design_index.md)（英語）を参照してください。 |

`dirsizer-mft` と `dirsizer-fsctl` は同じコマンドラインオプションを受け付け、同一の結果を出力します。`dirsizer-fs.exe` は独自のオプション（`--workers`、`--strict`、`--enumerator`）を持ちます。`dirsizer.exe` はどのツールよりも小さいオプション集合です（実装方式を指定するオプションはありません）。詳細なオプションは各コマンドの `--help` で確認できます（`dirsizer-inspect --help` が最も詳細です）。

### 管理者権限（昇格）について

NTFS 向けの 3 つのツールは NTFS のローメタデータへアクセスするため、実行ファイルには管理者権限を要求するマニフェスト（`requireAdministrator`）が埋め込まれています。非昇格環境で実行すると Windows の UAC ダイアログが表示されます（アプリ独自に `runas` 制御を行っているわけではありません）。すでに昇格済みのターミナルであれば、そのまま実行されます。なお、`dirsizer-mft` と `dirsizer-inspect` はプロセス内で `SeBackupPrivilege` を有効化します。アカウントに同権限が付与されていない場合はエラーを出力します。

**【Windows の挙動に関する注意点】**
Windows の仕様上、非昇格コンソールから昇格が必要なプロセスを*同一コンソール内（In-place）*で起動することはできません。PowerShell では昇格要求エラーとなり、その他のランチャーでは別ウィンドウで管理者コンソールが開くため、標準出力をリダイレクト（`> result.json`）したりパイプで渡したりできなくなります。出力のリダイレクトやパイプ処理を行いたい場合は、**あらかじめ管理者権限で開いたターミナル**から実行してください。

また、昇格ウィンドウはプロセス終了時に自動で閉じるため、ツール側で「単独のコンソールで開かれている」と判断した場合は、終了直前に Enter キーの入力待ちが発生します（`ConsolePause.cs`）。通常のシェルを共有しているターミナル内ではこの待ち時間は発生しません。なお、`src\Shared\app.manifest` 内の `level` を `asInvoker` に変更することで、これら管理者権限の要求を外してビルドすることも可能です。

`dirsizer.exe` と `dirsizer-fs.exe` にはここで述べたことは当てはまりません。それぞれ専用のマニフェスト（`asInvoker`）を持ち、管理者権限は不要で、通常のターミナルから実行でき、`> file` やパイプも使えます。ただし `asInvoker` は「昇格して実行できない」という意味ではありません。すでに昇格済みのコンソールから `dirsizer.exe` を起動すればそのトークンを引き継ぎ、`$MFT`/FSCTL 方式が利用可能になります。通常のコンソールからではそれらは利用できず、`dirsizer-fs.exe` と同じディレクトリ走査に黙って切り替わります。どちらの場合も UAC を要求することはありません。

## ビルド

.NET 8 SDK が必要です。各ツールは独立したプロジェクトとして管理されています。`DirSizer.Fs.Core` と `DirSizer.Core` は複数のツールが共有するライブラリで、実行ファイルではありません。

| プロジェクト | 生成される実行ファイル |
| --- | --- |
| `src\DirSizer\DirSizer.csproj` | `dirsizer`（統合エントリポイント） |
| `src\DirSizer.Fs\DirSizer.Fs.csproj` | `dirsizer-fs` |
| `src\DirSizer.Bulk\DirSizer.Bulk.csproj` | `dirsizer-mft` |
| `src\DirSizer.Fsctl\DirSizer.Fsctl.csproj` | `dirsizer-fsctl` |
| `src\DirSizer.Inspect\DirSizer.Inspect.csproj` | `dirsizer-inspect` |
| `src\DirSizer.Index\DirSizer.Index.csproj` | `dirsizer-index`（実験的機能） |

依存関係のない単一の NativeAOT 実行ファイルを生成する場合（要 Visual Studio C++ ビルドツール）:

```powershell
dotnet publish src\DirSizer\DirSizer.csproj -c Release -r win-x64

```

成果物は `artifacts\publish\DirSizer\release_win-x64\` に出力されます。NativeAOT 化によりランタイム非依存となり、コードトリミングが適用されます。全プロジェクトを一貫してパブリッシュする場合は `scripts\release.ps1` を使用してください。

ローカル開発での素早いコンパイルには、ソリューション（`DirSizer.sln`。全ツールと開発者向けツールを含みます）または個別のプロジェクトをビルドします。各プロジェクトは専用のフォルダー `artifacts\bin\<project>\release_win-x64\` に出力されます:

```powershell
dotnet build -c Release
dotnet build src\DirSizer.Bulk\DirSizer.Bulk.csproj -c Release
dotnet artifacts\bin\DirSizer.Bulk\release_win-x64\dirsizer-mft.dll T:

```

Bulk リーダーの内部構造（エクステント、USA フィックスアップ、レコード順序、ライブボリュームの検証機構）、コンポーネント構成、ベンチマーク測定結果の詳細は [docs/design_mft.md](docs/design_mft.md) (英語) を参照してください。

静止状態のテスト用ボリューム（`NTFSTEST` ラベル付き）を利用した相互検証およびベンチマークスクリプト:

```powershell
.\scripts\New-AbFixture.ps1 -Volume T:         # シンボリックリンク、ADS、スパース/圧縮/削除済みファイルを含むテスト環境を作成
.\scripts\Compare-Readers.ps1 -Volume T:       # dirsizer-fsctl と dirsizer-mft の出力を比較（一致すれば終了コード 0）
.\scripts\Compare-Benchmark.ps1 -Volume C: -Runs 5  # 交互実行によるフェーズ別（Min/Median/Max）パフォーマンス計測
.\scripts\Test-BulkInstability.ps1 -Volume T:  # テスト用ボリュームの MFT を拡張させて挙動を検証（詳細はスクリプトヘッダー参照）
.\scripts\Compare-Fs.ps1 -Path .\src -Oracle   # dirsizer-fs.exe と独立した集計の比較（更新のないツリーで実行）
.\scripts\Compare-Fs.ps1 -Path T:\ -Bulk       # dirsizer-fs.exe と dirsizer-mft の比較（想定される差異を一覧表示。docs\design_fs.md 参照）
.\scripts\Compare-Fs.ps1 -Path C:\ -Sweep -Runs 3  # ワーカー数 1/2/4/8 での走査時間

```

リファクタリング時のデグレ（回帰）チェックには `scripts\Get-ReferenceSnapshot.ps1` を使用します。変更前後のスナップショットを出力・比較することで、実行時間等の変動要素を除いた同一性を保証できます。レコードレベルでの詳細比較を行う場合は、以下の比較ツールをビルドして実行します:

```powershell
dotnet build src\DirSizer.Compare\DirSizer.Compare.csproj -c Release
dotnet artifacts\bin\DirSizer.Compare\release_win-x64\DirSizer.Compare.exe T:

```

実ボリュームを必要としないセルフテスト機能も各ツールに組み込まれています:

```powershell
dotnet .\artifacts\bin\DirSizer.Fsctl\release_win-x64\dirsizer-fsctl.dll --self-test
dotnet .\artifacts\bin\DirSizer.Bulk\release_win-x64\dirsizer-mft.dll --self-test
dotnet .\artifacts\bin\DirSizer.Inspect\release_win-x64\dirsizer-inspect.dll --self-test
dotnet .\artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer-fs.dll --self-test    # %TEMP% にテスト用ツリーを作成。管理者権限は不要
dotnet .\artifacts\bin\DirSizer\release_win-x64\dirsizer.dll --self-test         # 統合エントリポイント自身のセレクタ/CLI テスト

```

## リポジトリの構成

```
DirSizer.sln                 すべてのプロジェクト
Directory.Build.props        バージョン（1 か所）と、共通のビルド出力の設定
src\DirSizer\                dirsizer.exe: 統合エントリポイント（IScanStrategy、ScanStrategySelector）
src\DirSizer.Fsctl\          dirsizer-fsctl
src\DirSizer.Bulk\           dirsizer-mft（フォルダー名は変更なし。実行ファイル名のみ改称）
src\DirSizer.Inspect\        dirsizer-inspect
src\DirSizer.Fs\             dirsizer-fs.exe: 薄いホスト（ディレクトリ列挙、任意のファイルシステム、管理者権限不要）
src\DirSizer.Fs.Core\        ディレクトリ列挙エンジン。dirsizer-fs.exe と dirsizer.exe が共有
src\DirSizer.Compare\        開発者向けツール: 2 つのリーダーのレコード単位の比較
src\DirSizer.Core\           リーダーに依存しないパイプライン（マージ、関係解決、集計）
src\Shared\                  複数のツールにコンパイルされるファイル（オプションと出力、ConsolePause、app.manifest など）
src\Shared\BulkReader\       生の $MFT リーダー。dirsizer-mft、dirsizer-inspect、dirsizer.exe、開発者向けツールが共有
scripts\                     ベンチマーク、比較、リリースのスクリプト
docs\                        設計メモとロードマップ
artifacts\                   ビルド出力（git 管理外）
dist\                        リリースパッケージ（git 管理外）
```

## リリリースプロセス

バージョン情報は `Directory.Build.props` で一元管理されています。現行バージョンのパッケージ（ZIP）を作成するには以下を実行します:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\release.ps1

```

`dist\DirSizer-v<version>-win-x64.zip` が生成され、6 つの NativeAOT 実行ファイル（dirsizer.exe、dirsizer-fs.exe、dirsizer-mft.exe、dirsizer-fsctl.exe、dirsizer-inspect.exe、dirsizer-index.exe）、README、ライセンスが同梱されます。GitHub CLI（`gh`）を使用して GitHub Release へ自動公開を行う場合は、事前にサインイン（`gh auth login`）を完了させた上で以下を実行します:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\release.ps1 `
  -Publish -NotesFile release-notes.md

```

※ パブリッシュ実行には、クリーンな `master` ブランチの作業状態と未インストールのバージョンタグが必要です。スクリプトによって `master` および `v<version>` タグが push され、ビルドされた ZIP が GitHub Release にアタッチされます。

## 使い方

通常は `dirsizer.exe` を実行するだけです。管理者権限は不要で、オプションに実装方式を指定するものはありません:

```powershell
.\dirsizer.exe C:\Users
.\dirsizer.exe D:\ --top=50 --files
.\dirsizer.exe C:\ --top=100 --json > result.json

```

特定の方式を使いたい場合は、NTFS 向けツールを管理者権限のターミナルから実行してください（詳細は「管理者権限（昇格）について」を参照）。`dirsizer-mft` と `dirsizer-fsctl` は同じオプションを共有しています。`dirsizer-fs.exe` は下の専用の節を参照してください。

```powershell
.\dirsizer-mft.exe C:\
.\dirsizer-mft.exe D:\ --top=50 --files
.\dirsizer-mft.exe C:\ --top=100 --json > result.json
.\dirsizer-fsctl.exe C:\ --top=1 --json --benchmark
.\dirsizer-inspect.exe C: --record 5

```

デフォルトの出力数は `--top=25` です。テーブル表示では指定した上位件数がディレクトリ／ファイル別にそれぞれ出力されます。`--files` を指定すると両方のセクションが出力され、省略した場合は上位ディレクトリのみが表示されます。

`$MFT` や `$Bitmap` などの著名な NTFS メタデータレコードは `[NTFS metadata]` として識別表示されます。パスが `[unresolved record ...]` と表示される場合、MFT 上にデータが存在するものの、親ディレクトリとの親子関係やパスが再構築できず、かつ既知のメタデータレコードにも該当しなかったものを意味します。

スキャン中の進捗状況は `stderr`（標準エラー出力）に `current/estimated (percent%)` の形式で 1 行更新表示されます。そのため、`stdout`（標準出力）を汚すことなく、リダイレクトによる JSON 保存やパイプ処理を安全に行うことができます。

## dirsizer.exe（統合エントリポイント）

`dirsizer.exe` はパスに対して利用可能かつ検証済みの最良のスキャン方式を自動的に選びます。ユーザーはどの方式が動いたかを意識する必要はありません。設計の詳細: [docs/superpowers/specs/2026-09-23-unified-scan-strategy-design.md](docs/superpowers/specs/2026-09-23-unified-scan-strategy-design.md)（英語）。

```powershell
.\dirsizer.exe C:\
.\dirsizer.exe D:\some\directory --top=50 --files
.\dirsizer.exe C:\ --json --benchmark
```

選択方針: NTFS のドライブルートで管理者トークンが利用可能な場合（昇格済みのコンソール）は `$MFT` を直接読み（`strategy=mft`、最速）、それが使えず `FSCTL_GET_NTFS_FILE_RECORD` は使える場合はそちらを使い（`strategy=fsctl`）、それ以外（非昇格コンソール、非 NTFS のファイルシステム、ドライブルート以外のパス。後者 2 つの方式は常にボリューム全体を読むため）はディレクトリ走査を行います（`strategy=filesystem`、`dirsizer-fs.exe` と同一）。この順序は現時点では未計測の既定方針であり（MFT と FSCTL は大きな NTFS ツリーでディレクトリ走査より速いと予想されますが、両者を直接比較した実測はまだありません）、保証ではありません。詳細は設計仕様の "Selection policy is a prior, not a measurement" を参照してください。

オプション: `--top=N`、`--files`、`--dirs`、`--json`、`--strict`、`--benchmark`、`--workers N`（ファイルシステム方式のときのみ意味を持ちます）、`--self-test`、`-h` — どのオプションも実装方式を指定しないため、専用ツールのオプション集合より意図的に小さくなっています。特定の方式を使いたい場合は `dirsizer-fs.exe`、`dirsizer-mft.exe`、`dirsizer-fsctl.exe` を直接使ってください。`--json` は `statistics.performance.strategy`（`"mft"`、`"fsctl"`、`"filesystem"` のいずれか）と、方式間のフォールバックが発生した場合は `strategy_fallback`/`strategy_fallback_reason` を報告します。終了コード: 0 = 結果を出力、1 = エラー、3 = `--strict` 指定時にスキャンの一部が完了できなかった。

## dirsizer-fs.exe（任意のファイルシステム、管理者権限不要）

`dirsizer-fs.exe` は、`FindFirstFileExW` でディレクトリ一覧を読み取り、**任意の** Windows ファイルシステム（NTFS、ReFS、exFAT、FAT、ネットワーク共有）でフォルダーサイズを計算します。個々のファイルは開かず、複数のスレッドでディレクトリを並列に読み取り、管理者権限なしで動作します。読み取れないディレクトリはスキップされ、件数として報告されます。`dirsizer.exe` が NTFS 方式を使えないときにフォールバックする先も、この同じエンジンです。

```powershell
.\dirsizer-fs.exe C:\Users
.\dirsizer-fs.exe \\server\share --top=50 --files
.\dirsizer-fs.exe D:\ --json > result.json
.\dirsizer-fs.exe C:\ --strict --workers 8 --benchmark
```

オプションは NTFS 向けツールと同じもの（`--top`、`--files`、`--dirs`、`--json`、`--benchmark`、`--self-test`）に加え、`--workers N`（既定はプロセッサ数、最大 8）と `--strict`（読み取れないディレクトリがあれば終了コード 3。結果は出力されます）があります。パスはドライブのルートに限らず、任意のディレクトリを指定できます。`--enumerator=NAME[:CLASS[:KiB]]` は比較用の上級者向けオプションで、ディレクトリを読む API を選びます（`find` が既定で `FindFirstFileExW`、`handle` は `GetFileInformationByHandleEx`、`nt` は `NtQueryDirectoryFileEx`（実験的）、`auto` は `handle:full:64` を使い、対応していないファイルシステムでは自動的に `find` に切り替わります）。測定結果は [docs/design_fs.md](docs/design_fs.md)（英語）の "Enumerator comparison (P4)" と "Default enumerator with a fallback" にあります。`idextd` クラスは exFAT では使えません。終了コード: 0 = 結果を出力、1 = エラー、3 = `--strict` 指定時に読み取れないディレクトリがあった。進捗は stderr に 1 行で更新表示され（`Scanning: N directories, M files`）、stderr がターミナルのときだけ表示されます。JSON 出力は ASCII のみ（パス中の非 ASCII 文字はエスケープされます）なので、コンソールのコードページに関係なく正確です。表形式の出力はコンソールのコードページに従うため、パスを正確に得たいときは `--json` を使ってください。JSON の最上位の構成は NTFS 向けツールと同じですが、`statistics` と `performance` のキーは異なります（[docs/design_fs.md](docs/design_fs.md)（英語）を参照）。

結果は NTFS 向けツールと**同一にはなりません**（仕様です。詳細と実測した差異は [docs/design_fs.md](docs/design_fs.md)（英語）を参照）:

| ケース | `dirsizer-fs.exe` | NTFS 向けツール |
| --- | --- | --- |
| ハードリンクされたファイル | 名前を持つすべてのディレクトリで加算 | 1 回だけ加算 |
| NTFS メタデータ（`$MFT`、`$Bitmap` など） | 列挙されず、加算されない | 加算される |
| 読み取り権限のないディレクトリ | スキップし `directories_denied` に計上 | 加算される |
| ジャンクション、シンボリックリンク、マウントポイントのディレクトリ | 辿らず `reparse_skipped` に計上 | 空のディレクトリとして一覧に出る |

サイズは論理サイズ（ディレクトリ一覧が報告するサイズ）で、代替データストリームは含みません。NTFS では `dirsizer-fs.exe` は `dirsizer-mft` より遅い想定です。速度が必要で管理者権限で実行できる場合は NTFS 向けツールを使ってください。検証済みなのはローカルの NTFS のみです。他のファイルシステムやネットワーク共有は設計上は対応していますが、実行して確認するまでは未検証です。

## dirsizer-mft（実験的実装）

`dirsizer-mft` は、ボリュームから生の `$MFT` データブロックをダイレクトに一括読み込み（オフセットは `$MFT` のエクステントマップから取得）した上で、`dirsizer-fsctl` と完全に共通の解析、マージ、パス解決、集計、出力パイプラインへ渡します。検証機の実用 C: ボリュームにおける計測では、約 1.4 倍の高速化（5 回のベンチマーク中央値で 1.37〜1.52 倍、個別比較で 1.36〜1.59 倍）を記録しました。短縮された時間のすべては MFT データの取得フェーズによるものであり、その後の解析・集計処理のコストは同等です。なお、この数値は特定環境（単一マシン、単一ボリューム、ウォーム状態のファイルキャッシュ）に基づく参考値です。（`dirsizer-bulk` から改称。実装とプロジェクトフォルダー `src\DirSizer.Bulk\` は同一です。）

* **自動フォールバックなし**: ロー読み込みでエラーが発生した場合、即座にエラーを出力して終了（Exit Code 1）します。自動的に `dirsizer-fsctl` に切り替わることはありません。
* **同一の解析結果**: 静止状態の NTFS ボリュームにおいて、`dirsizer-mft` の出力結果（ルートサイズ、各パス、サイズ、ソート順、直下のエントリ、統計カウンター、未解決レコード）は、JIT/NativeAOT のビルド形式を問わず `dirsizer-fsctl` と完全に一致します（`scripts\Compare-Readers.ps1` により検証可能）。
* **進捗表示**: `dirsizer-fsctl` 同様、`stderr` にリアルタイムでスキャン進捗（`Scanning MFT: n/N (p%)` → `Calculating folder sizes...`）を出力します。標準出力へのリダイレクト（`> result.json`）やパイプ処理を阻害しません。
* **ライブボリュームの変更検知**: 読み込みの前後で MFT のレイアウト情報（ボリュームシリアル、ジオメトリ、有効データ長、エクステントマップ）を照合し、差分を検知した場合は 1 度だけ再スキャンを行います。これによりスキャン中の MFT 拡張や再配置を検出可能です。ただし、スキャン実行中に既存レコード内で発生したファイルの作成・更新・削除自体は検出**しません**（これは動作中のボリュームに対する全ツールの仕様です）。

`dirsizer-mft` の終了コード（`dirsizer-fsctl` は 0 と 1 のみ使用）:

| コード | 状態 | 詳細 |
| --- | --- | --- |
| 0 | 成功 | スキャンが完了し、処理中に MFT レイアウトの変更は検出されなかった。 |
| 1 | エラー | 引数エラー、無効なボリューム、非 NTFS、アクセス拒否、I/O 障害などの致命的エラー（出力なし）。 |
| 2/3 | 警告付き完了 | スキャン結果は出力されたが、再スキャン後も MFT レイアウトの変更が継続したため、アトミックなスナップショットとしては扱えない状態。警告が `stderr` に出力される（データ出力自体は完全ですが、一貫性は保証されません）。 |

`--json` オプション使用時、両ツール共通で `reader` フィールド（`fsctl` または `bulk`）が出力されます。`dirsizer-mft` では追加で `bulk` オブジェクト（安定性ステータス `stable`/`unstable`、試行回数、検出されたレイアウト変更、フェーズ別所要時間、スロット数）が含まれます。`records_scanned` は走査した MFT スロット数、`records_skipped` は読み込み・解析不能だったスロット数、`statistics.performance.query_ms` は取得フェーズ全般の所要時間を表します。

## dirsizer-inspect

NTFS マスターファイルテーブル（$MFT）の内部構造を解析する読み取り専用のデバッグ・調査ツールです。詳細は `dirsizer-inspect --help` を参照してください。

主要な使用例:

```powershell
dirsizer-inspect C:                         # ボリュームジオメトリ、MFT サイズ、スロット数、エクステント数の表示
dirsizer-inspect C: --mft-extents           # $MFT の全エクステントマップを表示
dirsizer-inspect C: --slots [--diagnose]    # スロット状態（使用中/削除済み/未使用/破損）の集計
dirsizer-inspect C: --record 5 [--dump] [--raw]   # 指定レコードのヘッダー、USA（Update Sequence Array）、全属性のダンプ
dirsizer-inspect C: --compare 12345         # 単一レコードに対する Direct Read と FSCTL の取得結果比較

```

`--record` 指定時、対象レコードの `$FILE_NAME`（親ディレクトリ情報・名前空間含む）、`$DATA` ストリーム、`$ATTRIBUTE_LIST` エントリが表示されます。ハードリンクを多数持つファイルなどの拡張レコード（Extension Record）も自動追跡され、共有パーサーが生成した内部モデルが出力されます。

レコード番号は 10 進数または `0x` プレフィックスの 16 進数で指定できます（既存ファイルのレコード番号は `fsutil file queryFileID` の下位 48 ビットから取得可能）。
終了コード: `0` (正常完了), `1` (エラー), `2` (`--compare` で差分検出)。

## 仕様および制限事項

* **論理サイズ（Logical Size）**: ファイルのデータサイズ（データ長）を基準とします。
* 報告されるサイズは、プライマリ（無名）の NTFS `$DATA` 属性の論理バイト数です。
* ディレクトリ自体のサイズは 0 バイトとして扱い、代替データストリーム（ADS）は集計から除外されます。
* 削除済みレコード、およびリパースポイント（ジャンクション/シンボリックリンク等）の参照先は除外されます。
* ハードリンクされたファイルは、検出された最初の親/ファイル名ペアに対して 1 回のみカウントされます。
* MFT レコードは降順でクエリされ、共有出力バッファーから直接解析されます。
* 拡張レコードは、パスおよびサイズの解決処理前にベースレコードへマージされます。
* 破損または不正な形式のレコードはスキップされ、カウントが JSON 統計に出力されます。
* 入力パスは `C:\` のようなローカル NTFS ドライブのルートパスのみサポートします。
* `--self-test`: 実ボリュームを開かずにパーサーのユニットテストを実行します。
* `--benchmark`: フェーズ別の詳細な所要時間およびメモリ消費量を JSON と `stderr` に出力します。

※ 現バージョンでは `--allocated`（割り当てサイズ）、`--deleted`（削除済みファイル）、`--ads`（副ストリーム）、および非 NTFS ドライブへのフォールバックスキャンは未実装です。物理割り当て容量（常駐データ、スパースファイル、圧縮ファイル、ハードリンク等）の正確な集計は、専用のテストスイート構築後に対応予定です。

## JSON 出力仕様

JSON には、指定した `--top` 数、サイズモード、バイト数値、パス、およびスキャン統計が含まれます:

```json
{
  "volume": "C:",
  "top": 25,
  "size_mode": "logical",
  "root": { "path": "C:\\", "size": 123456789 },
  "root_children": [{ "path": "C:\\Users", "size": 987654321 }],
  "directories": [{ "path": "C:\\Users", "size": 123 }],
  "files": [],
  "statistics": {
    "records_scanned": 100,
    "records_accepted": 90,
    "records_skipped": 10,
    "files": 70,
    "directories": 20
  }
}

```

`size_mode` は現在 `logical` 固定です。
`root` は集計されたボリュームルートの合計、`root_children` は Top-N フィルタリング適用前のルート直下要素の数値を保持します。`directories` と `files` 配列はプライオリティキューによって抽出され、要求された Top-N 範囲内でのみサイズ降順ソートされます。

統計カウンター（診断用情報）:

* `records_scanned`: 発行された MFT レコードクエリの総数。
* `records_accepted`: 正常に解析・受理された有効レコード数。
* `records_skipped`: 読み込みまたは解析に失敗したスキップ数。
* `files` / `directories`: 受理された種別ごとのカウント。

※ `records_skipped` が 1 以上の場合、一部のレコードが計算から漏れている可能性があることを示します。

## パフォーマンス・ベースライン

管理者ターミナルから `C:\ --top=1 --json --benchmark` を実行して計測します。以下は 2026 年 9 月 20 日にローカル C: ドライブ（NTFS）上で測定されたベースラインデータです。

| 指標 | 特性 | 測定結果 |
| --- | --- | --- |
| MFT クエリ数 | 有効レコードごとに降順で 1 回発行 | 949,287 〜 949,292 |
| スキップレコード数 | 正常ボリュームでは 0 | 0 |
| クエリ処理時間 | FSCTL リーダーにおける最大ボトルネック | 5,032 〜 5,288 ms |
| パーサー処理時間 | 共有バッファーを使用する独立した CPU 処理 | 1,527 〜 1,537 ms |
| パス/親子関係の解決 | 保持レコード数に対して線形（O(N)） | 485 〜 567 ms |
| サイズ集計 | ボトムアップ（葉→根）の単一パス処理 | 281 〜 302 ms |
| **スキャン合計時間** | セットアップ・出力処理を含む全体時間 | **8,369 〜 8,772 ms** |
| マネージドヒープ割り当て | バッファーコピーを回避した設計 | 約 736.5 MB |
| ワーキングセットピーク | 保持する内部モデルおよびバッファー上限 | 約 460 MB |

メモリ割り当ての大部分は、`DeviceIoControl` の I/O バッファーではなく、保持される内部モデル（`FileRecord`、パス文字列、リスト、辞書構造）によるものです。そのため、現段階（P1）では `ArrayPool` 等による最適化は見送られています。さらなる軽量化を行う場合は、ドメインモデルのデータ構造自体のスリム化が対象となります。

## 開発フェーズ（P0 / P1 / P2）比較

稼働中の `C:` ドライブを対象としたスキャン比較です。ライブ環境のためバックグラウンド I/O により数値は若干変動しますが、設計構造とパフォーマンス特性の検証を目的としています。

| ステージ | 期待される動作 | 実際の出力・結果 |
| --- | --- | --- |
| **P0（正確性）** | 正常な JSON 出力、ルート集計、スキップ数 0 | ルートサイズ `203,707,362,244` バイト、受理 `916,568` 件、スキップ `0` 件（最大ファイル: `pagefile.sys`） |
| **P1（性能）** | フェーズ別ベンチマーク、共有バッファーによるコピーレス走査 | クエリ `949,287〜949,292` 回、処理時間 `8,369〜8,772 ms`、マネージド割り当て約 `736.5 MB` |
| **P2（出力）** | ルート/直下要素に加え、上限付き Top-N の正確なソート出力 | ルートサイズ `203,710,737,592` バイト、`root_children` 取得成功、`top=3` にて上位 3 件のディレクトリ/ファイルが降順取得完了 |

直近の詳細スキャンテストでは、クエリ `949,503` 回、解析成功 `949,503` 件、拡張レコード `32,692` 件、論理レコード `916,811` 件、スキップ `0` 件、総処理時間 `10,177 ms`（スループット: 161,585 クエリ/秒）を記録しました。

ベンチマーク出力には `open_ms`、`volume_metadata_ms`、`other_ms`、`phase_sum_ms` などの詳細フィールドが含まれており、`phase_sum_ms` と `total_ms` の一致（アサート検証）が行われます。

シーケンス番号を含む完全な 64 ビットの File Reference（FRN）はモデル内部に保持されています。ただし、ライブボリュームにおける親子関係の解決では、メタデータ書き換えの競合を考慮し、検索キーとして 48 ビットのレコード番号を使用します（厳密なシーケンス検証を行うとツリー断絶が発生するため）。完全な 64 ビット参照は、将来の一貫性チェックやスナップショットリーダー機能用に保持されています。

## シーケンス識別・検証（Diagnostics）

スキャナーは MFT レコードヘッダー内のシーケンス番号 (`B`) を保持し、`$FILE_NAME` 属性が持つ親ディレクトリのシーケンス番号 (`A`) と比較検証します。FSCTL 経由で取得したレコード番号の上位シーケンスビット (`C`) は、仕様上の不透明性があるため整合性チェックには使用していません。

テスト用 `T:` ボリュームでの検証結果:

| チェック項目 | 結果 |
| --- | --- |
| 親子シーケンス（A/B）の完全一致 | 32 件 |
| シーケンス（A/B）の不一致 | 0 件 |
| シーケンス A=0 のフォールバック発生 | 0 件 |
| 未解決の親子関係 | 4 件 |

※ Windows 仕様上、FSCTL が返す識別子の上位ビットがヘッダーのシーケンス番号と一致する保証はないため、`C` の値は整合性判定から除外しています。また、シーケンス 0 は無効値として扱い、レコードヘッダー情報から再構築を行います。

製品コードおよびセルフテストから LINQ 依存は完全に排除されています。
実 C: ドライブにおけるスキャンでは、A/B 完全一致が `917,442` 件、未解決関係が `4` 件という結果を得ています。

`C:` スキャンで未解決となった 4 件のレコード詳細は以下の通りです:

| レコード番号 | ディレクトリ判定 | 論理サイズ | `$FILE_NAME` 数 | 親要素 |
| --- | --- | --- | --- | --- |
| 12 (`$Quota`) | false | 0 | 0 | なし |
| 13 (`$ObjId`) | false | 0 | 0 | なし |
| 14 (`$Reparse`) | false | 0 | 0 | なし |
| 15 (`$UsnJrnl`) | false | 0 | 0 | なし |

これらはすべてサイズ 0 の NTFS システムメタデータファイルであり、未解決であっても集計結果の容量計算に影響を与えません。

## 今後の展望

* **パーサーの強化**: ロングパス、Unicode パス名、リパースポイント、スパース/圧縮ファイル、および ADS（代替データストリーム）の除外テストの拡充。
* **メモリ・パフォーマンスの最適化**: 並列化やメモリプーリング（`ArrayPool`）を導入する前に、内部データモデル自体の構造見直しによるメモリ割り当ての低減。