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

    /// <summary>子プロセスを起動する。wait が true なら完了を待ち終了コードを返す。</summary>
    public static int SpawnReport(string[] args, bool wait)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath,
            UseShellExecute = false,
        };
        foreach (var a in args) { psi.ArgumentList.Add(a); }

        using var proc = Process.Start(psi);
        if (proc == null) { return 2; }
        if (!wait) { return 0; }
        proc.WaitForExit();
        return proc.ExitCode;
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
