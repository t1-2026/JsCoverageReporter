#nullable disable

using System.Diagnostics;

namespace JsCoverageReporter.Report;

/// <summary>
/// レポート生成を別プロセスで実行するためのホスト層ユーティリティ。
/// 引数組み立て（純関数）と、プロセス起動・ブラウザ起動（実行環境依存）を提供する。
/// </summary>
internal static class ReportProcess
{
    /// <summary>子プロセス（--report-from モード）へ渡す引数列を組み立てる純関数。</summary>
    public static string[] BuildReportArgs(
        string dataFile, string outputDir,
        bool writeLcov, bool writeJson, bool open, string targetUrl)
    {
        var args = new List<string>
        {
            "--report-from", dataFile,
            "--output", outputDir,
        };
        if (writeLcov) { args.Add("--lcov"); }
        if (writeJson) { args.Add("--json"); }
        if (open) { args.Add("--open"); }
        if (!string.IsNullOrEmpty(targetUrl))
        {
            args.Add("--target-url");
            args.Add(targetUrl);
        }
        return args.ToArray();
    }

    /// <summary>
    /// --progress-window の指定・--wait 併用・OS から、レポート生成子プロセスを
    /// 新しいコンソールウィンドウで起動するか、および --wait 併用の警告を出すかを決める純関数。
    /// 新ウィンドウ表示は Windows でのみ有効。--wait 併用時はウィンドウを出さず警告する。
    /// 非 Windows では progressWindow を黙って無視する（警告なし）。
    /// </summary>
    public static (bool UseNewWindow, bool WarnWaitConflict) ResolveProgressWindow(
        bool progressWindow, bool wait, bool isWindows)
    {
        if (!progressWindow) { return (false, false); }
        if (!isWindows) { return (false, false); }  // 非 Windows は無視（警告なし）
        if (wait) { return (false, true); }          // wait 併用は不可 → 警告して無視
        return (true, false);                        // Windows + デタッチ → 新ウィンドウ
    }

    /// <summary>
    /// 子プロセスを起動する。wait が true なら完了を待ち終了コードを返す。
    /// newWindow が true のときは新しいコンソールウィンドウで起動する（Windows）。
    /// 起動できなかった場合は 2 を返す。
    /// </summary>
    public static int SpawnReport(string[] args, bool wait, bool newWindow = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath,
            // newWindow 時は UseShellExecute=true で新コンソールウィンドウを得る。
            // ウィンドウを閉じると OS が既定でプロセスを終了する（中止）。
            UseShellExecute = newWindow,
        };
        foreach (var a in args) { psi.ArgumentList.Add(a); }

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) { return 2; }
            if (!wait) { return 0; }
            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            // UseShellExecute=true では起動失敗時に例外が飛び得る。従来の null 同様 2 を返す。
            Console.Error.WriteLine($"[Warning] レポート生成プロセスを起動できませんでした: {ex.Message}");
            return 2;
        }
    }

    /// <summary>既定ブラウザ（OS 関連付け）で HTML を開く。失敗しても致命的にはしない。</summary>
    public static void OpenInBrowser(string htmlPath)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = htmlPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Warning] レポートを開けませんでした: {ex.Message}");
        }
    }
}
