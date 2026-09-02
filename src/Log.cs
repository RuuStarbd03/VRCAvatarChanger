using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace VRCAvatarChanger;

/// <summary>
/// 動作の記録 (%AppData%\VRCAvatarChanger\app.log)。問い合わせ対応と、黙って失敗している箇所を追うためのもの。
///
/// ・Error は常に書く (予期しない例外、保存の失敗など)。
/// ・Warn / Info / Debug は設定「ログの記録」がオンのときだけ書く (普段は何も書かない)。
/// ・2MB を超えたら app.log.1 に退避して書き直す (肥大化しない。残るのは最大 2 世代)。
/// ・書くのはアバターの ID と名前、HTTP のステータス、例外の内容まで。
///   ログイン情報 (パスワード・トークン・クッキー) は呼び出し側が渡さないこと。
/// </summary>
public static class Log
{
    public static string FilePath => AppPaths.In("app.log");

    /// <summary>Warn 以下も書くか。設定「ログの記録」。</summary>
    public static bool Enabled { get; set; }

    private const long MaxBytes = 2L * 1024 * 1024;
    private static readonly object Gate = new();

    public static void Error(string what, Exception? ex = null) => Write("ERROR", what, ex, always: true);
    public static void Warn(string what, Exception? ex = null) => Write("WARN ", what, ex, always: false);
    public static void Info(string what) => Write("INFO ", what, null, always: false);
    public static void Debug(string what, Exception? ex = null) => Write("DEBUG", what, ex, always: false);

    /// <summary>
    /// 投げっぱなしにするタスクの失敗を記録する。`_ = FooAsync();` の代わりに `FooAsync().Forget();` と書く。
    /// 何のタスクかは呼び出し側の式から自動で取る。
    /// </summary>
    public static void Forget(this Task task, [CallerArgumentExpression(nameof(task))] string what = "")
    {
        if (task.IsCompleted)
        {
            if (task.IsFaulted) Error("裏で動かした処理が失敗: " + what, task.Exception?.GetBaseException());
            return;
        }
        task.ContinueWith(
            t => Error("裏で動かした処理が失敗: " + what, t.Exception?.GetBaseException()),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    /// <summary>「ログを開く」用。まだ無ければ見出しだけのファイルを作る。</summary>
    public static void EnsureFile()
    {
        lock (Gate)
        {
            if (File.Exists(FilePath)) return;
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.WriteAllText(FilePath, Header());
            }
            catch { /* 作れない場所なら開けないだけ */ }
        }
    }

    private static string Header()
        => $"# VRCAvatarChanger {Updater.CurrentVersion.ToString(3)} / {Environment.OSVersion} / .NET {Environment.Version}\r\n";

    private static void Write(string level, string what, Exception? ex, bool always)
    {
        if (!always && !Enabled) return;
        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" [").Append(level).Append("] ").Append(what);
        if (ex is not null)
        {
            // 予期しないものは追えるように全文、想定内の失敗は種類とメッセージだけ
            sb.Append(" — ");
            if (always) sb.Append(ex);
            else
            {
                sb.Append(ex.GetType().Name).Append(": ").Append(ex.Message);
                for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
                    sb.Append(" <- ").Append(inner.GetType().Name).Append(": ").Append(inner.Message);
            }
        }
        sb.Append("\r\n");
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                var info = new FileInfo(FilePath);
                var fresh = !info.Exists;
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                    fresh = true;
                }
                File.AppendAllText(FilePath, fresh ? Header() + sb : sb.ToString());
            }
            catch { /* ログが書けないことを、ログに書くわけにはいかない */ }
        }
    }
}
