# MyOcrDiff Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 2つのディレクトリの画像を OCR でテキスト抽出・差分比較し、行単位＋単語単位のカラー HTML レポートを生成する C# CLI ツールを作る。

**Architecture:** C# (.NET 8) CLI が NDLOCR-Lite（Python Embedded 経由でサブプロセス呼び出し）で OCR し、DiffPlex で行単位＋単語単位の差分を計算して HTML を生成する。テストは xUnit、全コンポーネントを TDD で実装する。

**Tech Stack:** C# .NET 8, xUnit, DiffPlex NuGet (Apache 2.0), NDLOCR-Lite (Python, CC BY 4.0), Python Embedded

---

## 前提知識

### DiffPlex の主要 API

```csharp
// 行単位・左右2カラム diff
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
var model = SideBySideDiffBuilder.Diff(leftText, rightText);
// model.OldText.Lines[i] と model.NewText.Lines[i] は常に同じ行番号で揃っている
// DiffPiece.Type: ChangeType.Unchanged / Inserted / Deleted / Modified / Imaginary
// ChangeType.Imaginary = 片側に行がない（パディング用）

// 単語単位 diff（Modified 行内のハイライト用）
using DiffPlex;
var differ = new Differ();
var wordDiff = differ.CreateWordDiffs(oldLine, newLine, ignoreWhiteSpace: false,
    wordSeparators: new[] { ' ', '\t', '、', '。', '，', '．', '\r', '\n' });
// wordDiff.PiecesOld[i] = 旧テキストを単語分割したもの
// wordDiff.PiecesNew[i] = 新テキストを単語分割したもの
// wordDiff.DiffBlocks = 変更ブロック（DeleteStartA, DeleteCountA, InsertStartB, InsertCountB）
```

### NDLOCR-Lite の呼び出し方

```bash
cd ndlocr-lite/src
python/python.exe ocr.py --sourceimg "C:\path\to\img.png" --output "C:\tmp\out"
# → C:\tmp\out\<stem>.txt にテキスト結果が書き出される
```

### プロジェクト構成（新規ディレクトリ）

```
C:\work\MyOcrDiff\
├── MyOcrDiff\                ← C# 本体
│   ├── MyOcrDiff.csproj
│   ├── Program.cs
│   ├── FilePairer.cs
│   ├── OcrRunner.cs
│   └── Report\
│       └── HtmlReportGenerator.cs
└── MyOcrDiff.Tests\          ← xUnit テストプロジェクト
    ├── MyOcrDiff.Tests.csproj
    ├── FilePairerTests.cs
    ├── DiffCalculatorTests.cs
    └── Report\
        └── HtmlReportGeneratorTests.cs
```

---

## Task 1: プロジェクトセットアップ

**Files:**
- Create: `C:\work\MyOcrDiff\` （新規ディレクトリ）

**Step 1: ソリューション・プロジェクト作成**

```bash
mkdir C:\work\MyOcrDiff
cd C:\work\MyOcrDiff

dotnet new sln -n MyOcrDiff
dotnet new console -n MyOcrDiff --framework net8.0
dotnet new xunit -n MyOcrDiff.Tests --framework net8.0

dotnet sln add MyOcrDiff
dotnet sln add MyOcrDiff.Tests
dotnet add MyOcrDiff.Tests reference MyOcrDiff
```

**Step 2: NuGet パッケージ追加**

```bash
dotnet add MyOcrDiff package DiffPlex
dotnet add MyOcrDiff.Tests package DiffPlex
```

**Step 3: ビルド確認**

```bash
dotnet build
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

**Step 4: デフォルトテスト削除**

`MyOcrDiff.Tests/UnitTest1.cs` を削除する。

**Step 5: git 初期化とコミット**

```bash
cd C:\work\MyOcrDiff
git init
git add .
git commit -m "chore: initial project setup"
```

---

## Task 2: FilePairer — ファイルペアリング

**Files:**
- Create: `MyOcrDiff/FilePairer.cs`
- Test: `MyOcrDiff.Tests/FilePairerTests.cs`

**Step 1: テストを書く**

`MyOcrDiff.Tests/FilePairerTests.cs` を作成する:

```csharp
using System.IO;
using Xunit;

namespace MyOcrDiff.Tests;

public class FilePairerTests : IDisposable
{
    // テスト用の一時ディレクトリを作成するヘルパー
    private readonly List<string> _tempDirs = [];

    private string MakeTempDir(params string[] fileNames)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        foreach (var name in fileNames)
        {
            File.WriteAllText(Path.Combine(dir, name), "");
        }
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // 両ディレクトリに同じファイル名があればペアになること
    [Fact]
    public void Pair_BothDirsHaveSameFile_ReturnsPair()
    {
        var left = MakeTempDir("a.png");
        var right = MakeTempDir("a.png");

        var (pairs, unmatched) = FilePairer.Pair(left, right);

        Assert.Single(pairs);
        Assert.Equal("a.png", pairs[0].Name);
        Assert.Empty(unmatched);
    }

    // 左にしかないファイルは unmatched の Side = "left" で記録されること
    [Fact]
    public void Pair_OnlyInLeft_ReturnsUnmatchedLeft()
    {
        var left = MakeTempDir("a.png", "b.png");
        var right = MakeTempDir("a.png");

        var (pairs, unmatched) = FilePairer.Pair(left, right);

        Assert.Single(pairs);
        Assert.Single(unmatched);
        Assert.Equal("b.png", unmatched[0].Name);
        Assert.Equal("left", unmatched[0].Side);
    }

    // 右にしかないファイルは unmatched の Side = "right" で記録されること
    [Fact]
    public void Pair_OnlyInRight_ReturnsUnmatchedRight()
    {
        var left = MakeTempDir("a.png");
        var right = MakeTempDir("a.png", "b.png");

        var (pairs, unmatched) = FilePairer.Pair(left, right);

        Assert.Single(pairs);
        Assert.Single(unmatched);
        Assert.Equal("b.png", unmatched[0].Name);
        Assert.Equal("right", unmatched[0].Side);
    }

    // 非対応拡張子（.txt など）はペアリング対象外であること
    [Fact]
    public void Pair_NonImageExtension_Ignored()
    {
        var left = MakeTempDir("a.png", "note.txt");
        var right = MakeTempDir("a.png", "note.txt");

        var (pairs, unmatched) = FilePairer.Pair(left, right);

        Assert.Single(pairs);
        Assert.Equal("a.png", pairs[0].Name);
        Assert.Empty(unmatched);
    }

    // 大文字小文字を区別せずペアリングできること（拡張子の大文字対応）
    [Fact]
    public void Pair_ExtensionCaseInsensitive_Matched()
    {
        var left = MakeTempDir("a.PNG");
        var right = MakeTempDir("a.PNG");

        var (pairs, _) = FilePairer.Pair(left, right);

        Assert.Single(pairs);
    }

    // 両ディレクトリが空の場合、ペアも unmatched も空であること
    [Fact]
    public void Pair_BothEmpty_ReturnsEmpty()
    {
        var left = MakeTempDir();
        var right = MakeTempDir();

        var (pairs, unmatched) = FilePairer.Pair(left, right);

        Assert.Empty(pairs);
        Assert.Empty(unmatched);
    }

    // 対応拡張子（png/jpg/jpeg/tiff/bmp）がすべて認識されること
    [Fact]
    public void Pair_AllSupportedExtensions_AllMatched()
    {
        var files = new[] { "a.png", "b.jpg", "c.jpeg", "d.tiff", "e.bmp" };
        var left = MakeTempDir(files);
        var right = MakeTempDir(files);

        var (pairs, unmatched) = FilePairer.Pair(left, right);

        Assert.Equal(5, pairs.Count);
        Assert.Empty(unmatched);
    }
}
```

**Step 2: テストが失敗することを確認**

```bash
dotnet test MyOcrDiff.Tests
```

Expected: FAIL（`FilePairer` が存在しないため）

**Step 3: 実装を書く**

`MyOcrDiff/FilePairer.cs` を作成する:

```csharp
namespace MyOcrDiff;

// 一致したペア（左右のファイルパスと名前）
internal record ImagePair(string Name, string LeftPath, string RightPath);

// どちらかにしか存在しないファイル
// Side: "left" = 左だけ存在、"right" = 右だけ存在
internal record UnmatchedFile(string Name, string Path, string Side);

// 2つのディレクトリのファイルをファイル名でペアリングするクラス
internal static class FilePairer
{
    // 対応する画像拡張子の一覧（大文字小文字を区別しない）
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".tiff", ".bmp" };

    // leftDir と rightDir の画像ファイルをファイル名でペアリングして返す
    internal static (List<ImagePair> Pairs, List<UnmatchedFile> Unmatched) Pair(
        string leftDir, string rightDir)
    {
        // 各ディレクトリの画像ファイルを「ファイル名 → フルパス」辞書として取得する
        var leftFiles = GetImageFiles(leftDir);
        var rightFiles = GetImageFiles(rightDir);

        var pairs = new List<ImagePair>();
        var unmatched = new List<UnmatchedFile>();

        // 左ファイルを走査してペアを探す
        foreach (var (name, leftPath) in leftFiles)
        {
            if (rightFiles.TryGetValue(name, out var rightPath))
            {
                // 右にも同名ファイルがあればペアにする
                pairs.Add(new ImagePair(name, leftPath, rightPath));
            }
            else
            {
                // 右にない場合は unmatched（左だけ存在）として記録する
                unmatched.Add(new UnmatchedFile(name, leftPath, "left"));
            }
        }

        // 右ファイルのうち左に存在しないものを unmatched（右だけ存在）として記録する
        foreach (var (name, rightPath) in rightFiles)
        {
            if (!leftFiles.ContainsKey(name))
            {
                unmatched.Add(new UnmatchedFile(name, rightPath, "right"));
            }
        }

        return (pairs, unmatched);
    }

    // 指定ディレクトリの画像ファイルを「ファイル名 → フルパス」辞書で返す
    private static Dictionary<string, string> GetImageFiles(string dir)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(dir))
        {
            var ext = Path.GetExtension(file);
            if (SupportedExtensions.Contains(ext))
            {
                result[Path.GetFileName(file)] = file;
            }
        }
        return result;
    }
}
```

**Step 4: テストが通ることを確認**

```bash
dotnet test MyOcrDiff.Tests
```

Expected: `Passed: 7, Failed: 0`

**Step 5: コミット**

```bash
git add MyOcrDiff/FilePairer.cs MyOcrDiff.Tests/FilePairerTests.cs
git commit -m "feat: add FilePairer with image extension filtering and pairing logic"
```

---

## Task 3: DiffCalculator — 差分計算

**Files:**
- Create: `MyOcrDiff/DiffCalculator.cs`
- Test: `MyOcrDiff.Tests/DiffCalculatorTests.cs`

**Step 1: テストを書く**

`MyOcrDiff.Tests/DiffCalculatorTests.cs` を作成する:

```csharp
using DiffPlex.DiffBuilder.Model;
using Xunit;

namespace MyOcrDiff.Tests;

public class DiffCalculatorTests
{
    // 同一テキスト → 全行 Unchanged であること
    [Fact]
    public void Calculate_IdenticalText_AllUnchanged()
    {
        var result = DiffCalculator.Calculate("hello\nworld", "hello\nworld");

        var allLines = result.OldText.Lines.Concat(result.NewText.Lines);
        Assert.All(allLines, l => Assert.True(
            l.Type == ChangeType.Unchanged || l.Type == ChangeType.Imaginary));
    }

    // 右に行が追加された → NewText に Inserted が含まれること
    [Fact]
    public void Calculate_LineAddedInRight_InsertedInNew()
    {
        var result = DiffCalculator.Calculate("hello", "hello\nworld");

        Assert.Contains(result.NewText.Lines, l => l.Type == ChangeType.Inserted);
    }

    // 左にあった行が右でなくなった → OldText に Deleted が含まれること
    [Fact]
    public void Calculate_LineDeletedFromRight_DeletedInOld()
    {
        var result = DiffCalculator.Calculate("hello\nworld", "hello");

        Assert.Contains(result.OldText.Lines, l => l.Type == ChangeType.Deleted);
    }

    // 行が変更された → OldText / NewText に Modified が含まれること
    [Fact]
    public void Calculate_LineModified_ModifiedInBoth()
    {
        var result = DiffCalculator.Calculate("hello world", "hello Japan");

        Assert.Contains(result.OldText.Lines, l => l.Type == ChangeType.Modified);
        Assert.Contains(result.NewText.Lines, l => l.Type == ChangeType.Modified);
    }

    // 両方空 → Lines が空またはすべて Imaginary であること
    [Fact]
    public void Calculate_BothEmpty_NoLines()
    {
        var result = DiffCalculator.Calculate("", "");

        var meaningful = result.OldText.Lines.Where(l => l.Type != ChangeType.Imaginary);
        Assert.Empty(meaningful);
    }

    // BuildWordDiffHtml — 変更のない行はエスケープされたテキストのみ返ること
    [Fact]
    public void BuildWordDiffHtml_NoChange_ReturnsEncodedText()
    {
        // "<b>" はHTMLエンコードされるべき（XSS防止）
        var (oldHtml, newHtml) = DiffCalculator.BuildWordDiffHtml("<b>hello</b>", "<b>hello</b>");

        Assert.Equal("&lt;b&gt;hello&lt;/b&gt;", oldHtml);
        Assert.Equal("&lt;b&gt;hello&lt;/b&gt;", newHtml);
    }

    // BuildWordDiffHtml — 変更した単語に <mark> タグが付くこと
    [Fact]
    public void BuildWordDiffHtml_ChangedWord_WrappedInMark()
    {
        var (oldHtml, newHtml) = DiffCalculator.BuildWordDiffHtml(
            "hello world", "hello Japan");

        Assert.Contains("<mark", oldHtml);
        Assert.Contains("<mark", newHtml);
        Assert.Contains("world", oldHtml);
        Assert.Contains("Japan", newHtml);
    }
}
```

**Step 2: テストが失敗することを確認**

```bash
dotnet test MyOcrDiff.Tests
```

Expected: FAIL（`DiffCalculator` が存在しないため）

**Step 3: 実装を書く**

`MyOcrDiff/DiffCalculator.cs` を作成する:

```csharp
using System.Text;
using System.Web;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace MyOcrDiff;

// テキスト差分を計算するクラス
internal static class DiffCalculator
{
    // 単語区切り文字（日本語句読点を含む）
    private static readonly char[] WordSeparators =
        { ' ', '\t', '、', '。', '，', '．', '・', '：', '；', '！', '？', '\r', '\n' };

    // 2つのテキストを行単位で比較し、左右のパネルが揃った SideBySideDiffModel を返す
    internal static SideBySideDiffModel Calculate(string leftText, string rightText)
    {
        return SideBySideDiffBuilder.Diff(leftText, rightText);
    }

    // 変更行の oldLine と newLine を単語単位で比較し、
    // 変更された単語を <mark> タグで囲んだ HTML を（旧, 新）のタプルで返す
    internal static (string OldHtml, string NewHtml) BuildWordDiffHtml(
        string oldLine, string newLine)
    {
        var differ = new Differ();
        var wordDiff = differ.CreateWordDiffs(
            oldLine, newLine, ignoreWhiteSpace: false, separators: WordSeparators);

        // 旧テキストのどのピースが削除されたかを記録するセット
        var deletedIndices = new HashSet<int>();
        // 新テキストのどのピースが挿入されたかを記録するセット
        var insertedIndices = new HashSet<int>();

        foreach (var block in wordDiff.DiffBlocks)
        {
            for (int i = block.DeleteStartA; i < block.DeleteStartA + block.DeleteCountA; i++)
            {
                deletedIndices.Add(i);
            }
            for (int i = block.InsertStartB; i < block.InsertStartB + block.InsertCountB; i++)
            {
                insertedIndices.Add(i);
            }
        }

        // 旧テキスト HTML を構築する
        var oldSb = new StringBuilder();
        for (int i = 0; i < wordDiff.PiecesOld.Length; i++)
        {
            var word = HttpUtility.HtmlEncode(wordDiff.PiecesOld[i]);
            if (deletedIndices.Contains(i))
            {
                oldSb.Append($"<mark class=\"w-del\">{word}</mark>");
            }
            else
            {
                oldSb.Append(word);
            }
        }

        // 新テキスト HTML を構築する
        var newSb = new StringBuilder();
        for (int i = 0; i < wordDiff.PiecesNew.Length; i++)
        {
            var word = HttpUtility.HtmlEncode(wordDiff.PiecesNew[i]);
            if (insertedIndices.Contains(i))
            {
                newSb.Append($"<mark class=\"w-ins\">{word}</mark>");
            }
            else
            {
                newSb.Append(word);
            }
        }

        return (oldSb.ToString(), newSb.ToString());
    }
}
```

> **注意:** `System.Web.HttpUtility` は .NET 8 では `System.Web` 名前空間にある。`MyOcrDiff.csproj` に `<UseWindowsForms>false</UseWindowsForms>` がなくても使えるが、念のため `dotnet add MyOcrDiff package System.Web.HttpUtility` を試すこと。もしパッケージが不要なら手動で HtmlEncode を実装する（後述）。

**Step 4: `System.Web` の利用可否を確認し、不要なら独自 HtmlEncode に差し替える**

.NET 8 の console app は `System.Web.HttpUtility` が使えないケースがある。その場合は `DiffCalculator.cs` の先頭に以下を追加する:

```csharp
// HttpUtility を使わない HtmlEncode（System.Web が使えない場合）
private static string HtmlEncode(string text) =>
    text.Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");
```

そして `HttpUtility.HtmlEncode(...)` を `HtmlEncode(...)` に置き換える。

**Step 5: テストが通ることを確認**

```bash
dotnet test MyOcrDiff.Tests
```

Expected: `Passed: 12+, Failed: 0`

**Step 6: コミット**

```bash
git add MyOcrDiff/DiffCalculator.cs MyOcrDiff.Tests/DiffCalculatorTests.cs
git commit -m "feat: add DiffCalculator with line-level and word-level diff"
```

---

## Task 4: HtmlReportGenerator — BuildDetailsPage

**Files:**
- Create: `MyOcrDiff/Report/HtmlReportGenerator.cs`
- Test: `MyOcrDiff.Tests/Report/HtmlReportGeneratorTests.cs`

**Step 1: テストを書く**

`MyOcrDiff.Tests/Report/HtmlReportGeneratorTests.cs` を作成する:

```csharp
using DiffPlex.DiffBuilder.Model;
using Xunit;

namespace MyOcrDiff.Tests.Report;

public class HtmlReportGeneratorTests
{
    // BuildDetailsPage — 同一テキストの場合、白背景（line-equal）の行が含まれること
    [Fact]
    public void BuildDetailsPage_IdenticalText_ContainsEqualLines()
    {
        var diff = DiffCalculator.Calculate("hello\nworld", "hello\nworld");
        var html = HtmlReportGenerator.BuildDetailsPage("test.png", diff);

        Assert.Contains("line-equal", html);
    }

    // BuildDetailsPage — 追加行は right カラムに line-insert クラスが付くこと
    [Fact]
    public void BuildDetailsPage_InsertedLine_ContainsInsertClass()
    {
        var diff = DiffCalculator.Calculate("hello", "hello\nworld");
        var html = HtmlReportGenerator.BuildDetailsPage("test.png", diff);

        Assert.Contains("line-insert", html);
    }

    // BuildDetailsPage — 削除行は left カラムに line-delete クラスが付くこと
    [Fact]
    public void BuildDetailsPage_DeletedLine_ContainsDeleteClass()
    {
        var diff = DiffCalculator.Calculate("hello\nworld", "hello");
        var html = HtmlReportGenerator.BuildDetailsPage("test.png", diff);

        Assert.Contains("line-delete", html);
    }

    // BuildDetailsPage — 変更行は line-modify クラスが付き、単語ハイライト mark タグが含まれること
    [Fact]
    public void BuildDetailsPage_ModifiedLine_ContainsModifyClassAndMark()
    {
        var diff = DiffCalculator.Calculate("hello world", "hello Japan");
        var html = HtmlReportGenerator.BuildDetailsPage("test.png", diff);

        Assert.Contains("line-modify", html);
        Assert.Contains("<mark", html);
    }

    // BuildDetailsPage — ファイル名がヘッダーに表示されること
    [Fact]
    public void BuildDetailsPage_FileName_InHeader()
    {
        var diff = DiffCalculator.Calculate("a", "b");
        var html = HtmlReportGenerator.BuildDetailsPage("myfile.png", diff);

        Assert.Contains("myfile.png", html);
    }

    // BuildDetailsPage — 「一覧に戻る」リンクが含まれること
    [Fact]
    public void BuildDetailsPage_ContainsBackLink()
    {
        var diff = DiffCalculator.Calculate("a", "a");
        var html = HtmlReportGenerator.BuildDetailsPage("x.png", diff);

        Assert.Contains("../index.html", html);
    }

    // BuildDetailsPage — テキスト内の HTML 特殊文字がエスケープされること（XSS防止）
    [Fact]
    public void BuildDetailsPage_HtmlInText_IsEscaped()
    {
        var diff = DiffCalculator.Calculate("<script>alert(1)</script>", "<b>safe</b>");
        var html = HtmlReportGenerator.BuildDetailsPage("x.png", diff);

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }
}
```

**Step 2: テストが失敗することを確認**

```bash
dotnet test MyOcrDiff.Tests
```

Expected: FAIL

**Step 3: 実装を書く**

`MyOcrDiff/Report/` ディレクトリを作成し、`HtmlReportGenerator.cs` を作成する:

```csharp
using System.Text;
using DiffPlex.DiffBuilder.Model;

namespace MyOcrDiff.Report;

// HTML レポートを生成するクラス
// BuildDetailsPage: 1ペアの差分詳細ページ
// BuildIndexPage:   全ペアのサマリーページ（Task 5 で追加）
internal static class HtmlReportGenerator
{
    // HTML 特殊文字をエスケープする（XSS防止）
    private static string Enc(string? text)
    {
        if (text is null)
        {
            return "";
        }
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }

    // 1ペアの差分詳細 HTML を生成して返す
    // name: ファイル名（ヘッダー表示用）
    // diff: DiffCalculator.Calculate の戻り値
    internal static string BuildDetailsPage(string name, SideBySideDiffModel diff)
    {
        var sb = new StringBuilder();

        // HTML ヘッダーとスタイルを出力する
        sb.AppendLine($$"""
            <!DOCTYPE html><html><head><meta charset="utf-8">
            <title>差分: {{Enc(name)}}</title>
            <style>
            body{font-family:sans-serif;font-size:13px;margin:0;background:#fff}
            h1{padding:8px 12px;background:#2d2d2d;color:#fff;margin:0;font-size:14px}
            .back{display:block;padding:8px 12px;background:#f5f5f5;
                  border-bottom:1px solid #ddd;font-size:12px;color:#1a7a4a;text-decoration:none}
            .back:hover{text-decoration:underline}
            .diff-table{width:100%;border-collapse:collapse;font-family:monospace;font-size:13px}
            .diff-table td{padding:2px 8px;vertical-align:top;border:none}
            .lineno{width:40px;text-align:right;color:#aaa;user-select:none;
                    border-right:2px solid #e0e0e0;padding-right:6px}
            .line-equal td{background:#fff}
            .line-delete td.left-cell{background:#ffeef0}
            .line-insert td.right-cell{background:#e6ffed}
            .line-modify td.left-cell{background:#fff3cd}
            .line-modify td.right-cell{background:#e6ffed}
            .divider{width:4px;background:#e8e8e8;border:none;padding:0}
            mark.w-del{background:#f0c800;border-radius:2px;padding:0 1px}
            mark.w-ins{background:#8fc98f;border-radius:2px;padding:0 1px}
            </style></head><body>
            """);

        sb.AppendLine($"<h1>差分: {Enc(name)}</h1>");
        sb.AppendLine("<a class=\"back\" href=\"../index.html\">← 一覧に戻る</a>");
        sb.AppendLine("<table class=\"diff-table\">");
        sb.AppendLine("<colgroup><col class=\"lineno\"><col><col class=\"divider\"><col class=\"lineno\"><col></colgroup>");

        var leftLines = diff.OldText.Lines;
        var rightLines = diff.NewText.Lines;
        int count = Math.Max(leftLines.Count, rightLines.Count);

        // 行数分のループで左右を同時に出力する（DiffPlex は常に同数に揃えてくれる）
        for (int i = 0; i < count; i++)
        {
            DiffPiece? left  = i < leftLines.Count  ? leftLines[i]  : null;
            DiffPiece? right = i < rightLines.Count ? rightLines[i] : null;

            // 行の状態を CSS クラス名に変換する
            string rowClass = DetermineRowClass(left, right);

            // 行番号（Imaginary はパディング行なので番号なし）
            string leftNo  = (left?.Position  is int lp) ? lp.ToString() : "";
            string rightNo = (right?.Position is int rp) ? rp.ToString() : "";

            // セルの HTML コンテンツを決定する
            string leftContent;
            string rightContent;

            if (rowClass == "line-modify")
            {
                // 変更行は単語単位のハイライトを行う
                var (oldHtml, newHtml) = DiffCalculator.BuildWordDiffHtml(
                    left?.Text ?? "", right?.Text ?? "");
                leftContent  = oldHtml;
                rightContent = newHtml;
            }
            else
            {
                // それ以外は単純にエスケープしたテキスト
                leftContent  = Enc(left?.Text);
                rightContent = Enc(right?.Text);
            }

            sb.AppendLine($"""
                <tr class="{rowClass}">
                  <td class="lineno">{leftNo}</td>
                  <td class="left-cell">{leftContent}</td>
                  <td class="divider"></td>
                  <td class="lineno">{rightNo}</td>
                  <td class="right-cell">{rightContent}</td>
                </tr>
                """);
        }

        sb.AppendLine("</table></body></html>");
        return sb.ToString();
    }

    // 左右の行状態から行の CSS クラス名を返す
    private static string DetermineRowClass(DiffPiece? left, DiffPiece? right)
    {
        var leftType  = left?.Type  ?? ChangeType.Imaginary;
        var rightType = right?.Type ?? ChangeType.Imaginary;

        if (leftType == ChangeType.Deleted || rightType == ChangeType.Imaginary && leftType != ChangeType.Imaginary)
        {
            return "line-delete";
        }
        if (rightType == ChangeType.Inserted || leftType == ChangeType.Imaginary && rightType != ChangeType.Imaginary)
        {
            return "line-insert";
        }
        if (leftType == ChangeType.Modified || rightType == ChangeType.Modified)
        {
            return "line-modify";
        }
        return "line-equal";
    }
}
```

**Step 4: テストが通ることを確認**

```bash
dotnet test MyOcrDiff.Tests
```

Expected: `Passed: 19+, Failed: 0`

**Step 5: コミット**

```bash
git add MyOcrDiff/Report/HtmlReportGenerator.cs MyOcrDiff.Tests/Report/HtmlReportGeneratorTests.cs
git commit -m "feat: add HtmlReportGenerator.BuildDetailsPage with 2-column diff view"
```

---

## Task 5: HtmlReportGenerator — BuildIndexPage

**Files:**
- Modify: `MyOcrDiff/Report/HtmlReportGenerator.cs`
- Modify: `MyOcrDiff.Tests/Report/HtmlReportGeneratorTests.cs`

### データ型

```csharp
// 1ペアの処理結果（サマリー表の1行分）
internal record PairResult(
    string Name,        // ファイル名
    int Added,          // 追加行数
    int Deleted,        // 削除行数
    int Modified,       // 変更行数
    bool IsError,       // OCR などでエラーが発生したか
    string DetailFile   // 詳細ページのファイル名（details/xxx.html）
);

// 片側にしかないファイル
internal record UnmatchedReport(string Name, string Side); // Side: "left" or "right"
```

**Step 1: テストを追加する**

`HtmlReportGeneratorTests.cs` に追加:

```csharp
// BuildIndexPage — 差分ありのペアはファイル名がリンクになること
[Fact]
public void BuildIndexPage_PairWithDiff_HasLink()
{
    var pairs = new List<PairResult>
    {
        new("img001.png", Added: 2, Deleted: 0, Modified: 1,
            IsError: false, DetailFile: "details/img001.html"),
    };
    var html = HtmlReportGenerator.BuildIndexPage(
        pairs, unmatched: [], leftDir: "before", rightDir: "after");

    Assert.Contains("details/img001.html", html);
    Assert.Contains("img001.png", html);
}

// BuildIndexPage — 差分なしのペアは「同一」と表示されること
[Fact]
public void BuildIndexPage_PairNoDiff_ShowsSame()
{
    var pairs = new List<PairResult>
    {
        new("img001.png", 0, 0, 0, false, "details/img001.html"),
    };
    var html = HtmlReportGenerator.BuildIndexPage(
        pairs, [], "before", "after");

    Assert.Contains("同一", html);
}

// BuildIndexPage — unmatched の left ファイルは「左のみ」と表示されること
[Fact]
public void BuildIndexPage_UnmatchedLeft_ShowsLeftOnly()
{
    var html = HtmlReportGenerator.BuildIndexPage(
        pairs: [],
        unmatched: [new UnmatchedReport("orphan.png", "left")],
        leftDir: "before", rightDir: "after");

    Assert.Contains("orphan.png", html);
    Assert.Contains("左のみ", html);
}

// BuildIndexPage — unmatched の right ファイルは「右のみ」と表示されること
[Fact]
public void BuildIndexPage_UnmatchedRight_ShowsRightOnly()
{
    var html = HtmlReportGenerator.BuildIndexPage(
        pairs: [],
        unmatched: [new UnmatchedReport("new.png", "right")],
        leftDir: "before", rightDir: "after");

    Assert.Contains("new.png", html);
    Assert.Contains("右のみ", html);
}

// BuildIndexPage — エラーペアは「エラー」と表示されること
[Fact]
public void BuildIndexPage_ErrorPair_ShowsError()
{
    var pairs = new List<PairResult>
    {
        new("bad.png", 0, 0, 0, IsError: true, ""),
    };
    var html = HtmlReportGenerator.BuildIndexPage(pairs, [], "before", "after");

    Assert.Contains("エラー", html);
}

// BuildIndexPage — ヘッダーに leftDir と rightDir が表示されること
[Fact]
public void BuildIndexPage_Header_ContainsDirNames()
{
    var html = HtmlReportGenerator.BuildIndexPage([], [], "before/", "after/");

    Assert.Contains("before/", html);
    Assert.Contains("after/", html);
}
```

**Step 2: テストが失敗することを確認**

```bash
dotnet test MyOcrDiff.Tests
```

Expected: FAIL（`BuildIndexPage` が存在しないため）

**Step 3: 実装を追加する**

`HtmlReportGenerator.cs` の末尾に `PairResult` レコード・`UnmatchedReport` レコードと `BuildIndexPage` メソッドを追加する:

```csharp
// 1ペアの処理結果（サマリー表の1行分）
internal record PairResult(
    string Name, int Added, int Deleted, int Modified, bool IsError, string DetailFile);

// 片側にしかないファイルの記録
internal record UnmatchedReport(string Name, string Side);
```

```csharp
// サマリー index.html を生成して返す
// pairs: 全ペアの処理結果リスト
// unmatched: 片側にしかないファイルリスト
// leftDir / rightDir: ヘッダー表示用のディレクトリ名
internal static string BuildIndexPage(
    List<PairResult> pairs,
    List<UnmatchedReport> unmatched,
    string leftDir,
    string rightDir)
{
    var sb = new StringBuilder();

    sb.AppendLine($$"""
        <!DOCTYPE html><html><head><meta charset="utf-8">
        <title>画像差分レポート</title>
        <style>
        body{font-family:sans-serif;padding:24px;color:#333}
        h1{font-size:20px;margin-bottom:4px}
        .subtitle{color:#666;font-size:14px;margin-bottom:24px}
        table{border-collapse:collapse;width:100%}
        th,td{border:1px solid #ddd;padding:8px 12px;text-align:left}
        th{background:#f5f5f5;font-weight:600}
        td.num{text-align:right;font-variant-numeric:tabular-nums}
        a{color:#1a7a4a;text-decoration:none}
        a:hover{text-decoration:underline}
        .tag-same{color:#666}
        .tag-diff{color:#c0392b;font-weight:600}
        .tag-err{color:#e67e22;font-weight:600}
        .tag-left{color:#2980b9}
        .tag-right{color:#27ae60}
        </style></head><body>
        <h1>画像差分レポート</h1>
        <p class="subtitle">{{Enc(leftDir)}} ↔ {{Enc(rightDir)}}　全 {{pairs.Count + unmatched.Count}} ファイル</p>
        <table>
        <tr><th>ファイル名</th><th class="num">追加行</th><th class="num">削除行</th><th class="num">変更行</th><th>状態</th></tr>
        """);

    // ペアの行を出力する
    foreach (var pair in pairs)
    {
        // 状態テキストと CSS クラスを決定する
        string statusTag;
        string nameCell;

        if (pair.IsError)
        {
            statusTag = "<span class=\"tag-err\">エラー</span>";
            nameCell  = Enc(pair.Name);
        }
        else if (pair.Added == 0 && pair.Deleted == 0 && pair.Modified == 0)
        {
            statusTag = "<span class=\"tag-same\">同一</span>";
            nameCell  = Enc(pair.Name);
        }
        else
        {
            statusTag = "<span class=\"tag-diff\">差分あり</span>";
            nameCell  = $"<a href=\"{Enc(pair.DetailFile)}\">{Enc(pair.Name)}</a>";
        }

        sb.AppendLine($"""
            <tr>
              <td>{nameCell}</td>
              <td class="num">{pair.Added}</td>
              <td class="num">{pair.Deleted}</td>
              <td class="num">{pair.Modified}</td>
              <td>{statusTag}</td>
            </tr>
            """);
    }

    // unmatched ファイルの行を出力する
    foreach (var u in unmatched)
    {
        string sideLabel;
        string sideClass;

        if (u.Side == "left")
        {
            sideLabel = "左のみ";
            sideClass = "tag-left";
        }
        else
        {
            sideLabel = "右のみ";
            sideClass = "tag-right";
        }

        sb.AppendLine($"""
            <tr>
              <td>{Enc(u.Name)}</td>
              <td class="num">—</td>
              <td class="num">—</td>
              <td class="num">—</td>
              <td><span class="{sideClass}">{sideLabel}</span></td>
            </tr>
            """);
    }

    sb.AppendLine("</table></body></html>");
    return sb.ToString();
}
```

**Step 4: テストが通ることを確認**

```bash
dotnet test MyOcrDiff.Tests
```

Expected: `Passed: 25+, Failed: 0`

**Step 5: コミット**

```bash
git add MyOcrDiff/Report/HtmlReportGenerator.cs MyOcrDiff.Tests/Report/HtmlReportGeneratorTests.cs
git commit -m "feat: add HtmlReportGenerator.BuildIndexPage with summary table"
```

---

## Task 6: OcrRunner — サブプロセス呼び出し

**Files:**
- Create: `MyOcrDiff/OcrRunner.cs`
- Test: `MyOcrDiff.Tests/OcrRunnerTests.cs`

> **テスト方針:** 本物の NDLOCR-Lite は重いためテストには使わない。代わりに、引数を受け取って `.txt` ファイルを書き出すスタブ Python スクリプトでテストする。

**Step 1: スタブ Python スクリプトを作成する**

`MyOcrDiff.Tests/stub_ocr.py` を作成する:

```python
# スタブ OCR スクリプト（テスト用）
# NDLOCR-Lite の ocr.py と同じ引数形式を受け取り、固定テキストを .txt に書き出す
import argparse
import os

parser = argparse.ArgumentParser()
parser.add_argument('--sourceimg', required=True)
parser.add_argument('--output', required=True)
args = parser.parse_args()

stem = os.path.splitext(os.path.basename(args.sourceimg))[0]
os.makedirs(args.output, exist_ok=True)

with open(os.path.join(args.output, stem + '.txt'), 'w', encoding='utf-8') as f:
    f.write('stub ocr result\n')
```

**Step 2: テストを書く**

`MyOcrDiff.Tests/OcrRunnerTests.cs` を作成する:

```csharp
using System.IO;
using System.Reflection;
using Xunit;

namespace MyOcrDiff.Tests;

public class OcrRunnerTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    // テスト用の空画像ファイルを作成するヘルパー
    private string MakeTempImage(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), name);
        File.WriteAllBytes(path, []);
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            if (File.Exists(f)) File.Delete(f);
        }
    }

    // スタブ OCR スクリプトのパス（テストアセンブリと同じディレクトリ）
    private static string StubOcrScript =>
        Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
            "stub_ocr.py");

    // RunAsync — スタブ OCR を呼び出してテキストが返ること
    [Fact]
    public async Task RunAsync_WithStubOcr_ReturnsText()
    {
        var img = MakeTempImage("test_ocr.png");
        var runner = new OcrRunner(pythonExe: "python", ocrScript: StubOcrScript);

        var result = await runner.RunAsync(img);

        Assert.Equal("stub ocr result\n", result);
    }

    // RunAsync — 存在しない Python を指定した場合は OcrException が発生すること
    [Fact]
    public async Task RunAsync_InvalidPython_ThrowsOcrException()
    {
        var img = MakeTempImage("test_ocr2.png");
        var runner = new OcrRunner(pythonExe: "nonexistent_python_xyz", ocrScript: StubOcrScript);

        await Assert.ThrowsAsync<OcrException>(() => runner.RunAsync(img));
    }
}
```

> **注意:** `stub_ocr.py` をテストの出力ディレクトリにコピーするため、`MyOcrDiff.Tests.csproj` に以下を追加する:
> ```xml
> <ItemGroup>
>   <Content Include="stub_ocr.py" CopyToOutputDirectory="Always" />
> </ItemGroup>
> ```

**Step 3: テストが失敗することを確認**

```bash
dotnet test MyOcrDiff.Tests
```

Expected: FAIL（`OcrRunner` が存在しないため）

**Step 4: 実装を書く**

`MyOcrDiff/OcrRunner.cs` を作成する:

```csharp
using System.Diagnostics;

namespace MyOcrDiff;

// OCR 実行時のエラーを表す例外クラス
internal class OcrException(string message) : Exception(message);

// NDLOCR-Lite を Python サブプロセスで呼び出して OCR テキストを取得するクラス
internal class OcrRunner(string pythonExe, string ocrScript)
{
    // 画像ファイルのパスを受け取り、OCR 結果テキストを返す
    // 一時フォルダに .txt を書き出してもらい、読み込んで返す
    internal async Task<string> RunAsync(string imagePath)
    {
        var stem = Path.GetFileNameWithoutExtension(imagePath);

        // 一時出力フォルダを作成する（GUID で衝突を防ぐ）
        var tmpOut = Path.Combine(Path.GetTempPath(), "myocrdiff_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpOut);

        try
        {
            // サブプロセスを起動して OCR を実行する
            var psi = new ProcessStartInfo
            {
                FileName               = pythonExe,
                Arguments              = $"\"{ocrScript}\" --sourceimg \"{imagePath}\" --output \"{tmpOut}\"",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };

            Process process;
            try
            {
                process = Process.Start(psi)
                    ?? throw new OcrException($"Python プロセスの起動に失敗しました: {pythonExe}");
            }
            catch (Exception ex) when (ex is not OcrException)
            {
                throw new OcrException($"Python プロセスの起動に失敗しました: {ex.Message}");
            }

            using (process)
            {
                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    var err = await process.StandardError.ReadToEndAsync();
                    throw new OcrException(
                        $"OCR が失敗しました (終了コード {process.ExitCode}): {err}");
                }
            }

            // 出力された .txt ファイルを読み込む
            var txtPath = Path.Combine(tmpOut, stem + ".txt");
            if (!File.Exists(txtPath))
            {
                throw new OcrException($"OCR 出力ファイルが見つかりません: {txtPath}");
            }

            return await File.ReadAllTextAsync(txtPath);
        }
        finally
        {
            // 一時フォルダを削除する（成功・失敗どちらでも）
            if (Directory.Exists(tmpOut))
            {
                Directory.Delete(tmpOut, recursive: true);
            }
        }
    }
}
```

**Step 5: テストが通ることを確認**

```bash
dotnet test MyOcrDiff.Tests
```

Expected: `Passed: 27+, Failed: 0`

> **もし `RunAsync_WithStubOcr_ReturnsText` が失敗する場合:** `python` コマンドが PATH に通っているか確認する。Windows では `python3` ではなく `python` になっている場合が多い。

**Step 6: コミット**

```bash
git add MyOcrDiff/OcrRunner.cs MyOcrDiff.Tests/OcrRunnerTests.cs MyOcrDiff.Tests/stub_ocr.py MyOcrDiff.Tests/MyOcrDiff.Tests.csproj
git commit -m "feat: add OcrRunner wrapping NDLOCR-Lite Python subprocess"
```

---

## Task 7: Program.cs — CLI 統合

**Files:**
- Modify: `MyOcrDiff/Program.cs`

> Program.cs は統合ロジックのため、ユニットテストではなく手動実行で確認する。

**Step 1: Program.cs を実装する**

```csharp
using System.Text.RegularExpressions;
using DiffPlex.DiffBuilder.Model;
using MyOcrDiff;
using MyOcrDiff.Report;

// ---- --help の処理 ----
if (Array.IndexOf(args, "--help") >= 0 || Array.IndexOf(args, "-h") >= 0)
{
    Console.WriteLine("""
        使い方:
          MyOcrDiff <before-dir> <after-dir> [オプション]

        引数:
          <before-dir>    比較元の画像ディレクトリ
          <after-dir>     比較先の画像ディレクトリ

        オプション:
          --output <dir>  レポート出力先（デフォルト: ./report）
          --help, -h      このヘルプを表示する

        対応拡張子: .png .jpg .jpeg .tiff .bmp
        """);
    return 0;
}

// ---- 引数の解析 ----
string? leftDir   = null;
string? rightDir  = null;
string outputDir  = "./report";

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--output" && i + 1 < args.Length)
    {
        outputDir = args[i + 1];
        i++;
    }
    else if (!args[i].StartsWith("--"))
    {
        if (leftDir is null)
        {
            leftDir = args[i];
        }
        else if (rightDir is null)
        {
            rightDir = args[i];
        }
    }
}

if (leftDir is null || rightDir is null)
{
    Console.Error.WriteLine("エラー: before-dir と after-dir の両方を指定してください。");
    Console.Error.WriteLine("使い方: MyOcrDiff --help");
    return 1;
}

if (!Directory.Exists(leftDir))
{
    Console.Error.WriteLine($"エラー: ディレクトリが存在しません: {leftDir}");
    return 1;
}

if (!Directory.Exists(rightDir))
{
    Console.Error.WriteLine($"エラー: ディレクトリが存在しません: {rightDir}");
    return 1;
}

// ---- ファイルペアリング ----
var (pairs, unmatched) = FilePairer.Pair(leftDir, rightDir);

if (pairs.Count == 0 && unmatched.Count == 0)
{
    Console.Error.WriteLine("エラー: 対象の画像ファイルが見つかりませんでした。");
    return 1;
}

Console.WriteLine($"ペア: {pairs.Count} 件、片側のみ: {unmatched.Count} 件");

// ---- Python / OCR スクリプトのパス解決 ----
// 実行ファイルと同じディレクトリの相対パスで解決する
var baseDir    = AppContext.BaseDirectory;
var pythonExe  = Path.Combine(baseDir, "python", "python.exe");
var ocrScript  = Path.Combine(baseDir, "ndlocr-lite", "src", "ocr.py");

// python.exe が存在しない場合はシステムの python を使う（開発環境向けフォールバック）
if (!File.Exists(pythonExe))
{
    pythonExe = "python";
}

var runner = new OcrRunner(pythonExe, ocrScript);

// ---- 出力ディレクトリの準備 ----
Directory.CreateDirectory(outputDir);
var detailsDir = Path.Combine(outputDir, "details");
Directory.CreateDirectory(detailsDir);

// ---- 各ペアを処理する ----
var pairResults   = new List<PairResult>();
var unmatchedReps = unmatched.Select(u => new UnmatchedReport(u.Name, u.Side)).ToList();
int errorCount    = 0;

for (int idx = 0; idx < pairs.Count; idx++)
{
    var pair = pairs[idx];
    Console.WriteLine($"  [{idx + 1}/{pairs.Count}] {pair.Name}");

    try
    {
        // 2枚の OCR を並列実行する
        var task1 = runner.RunAsync(pair.LeftPath);
        var task2 = runner.RunAsync(pair.RightPath);
        await Task.WhenAll(task1, task2);

        var leftText  = await task1;
        var rightText = await task2;

        // 差分を計算する
        var diff = DiffCalculator.Calculate(leftText, rightText);

        // 行数を集計する
        int added    = diff.NewText.Lines.Count(l => l.Type == ChangeType.Inserted);
        int deleted  = diff.OldText.Lines.Count(l => l.Type == ChangeType.Deleted);
        int modified = diff.OldText.Lines.Count(l => l.Type == ChangeType.Modified);

        // 詳細ページのファイル名を決定する（英数字以外を _ に置換してファイル名を安全にする）
        var safeName   = Regex.Replace(pair.Name, @"[^\w.]", "_");
        var detailFile = $"details/{safeName}.html";

        // 詳細ページを書き出す
        var detailHtml = HtmlReportGenerator.BuildDetailsPage(pair.Name, diff);
        await File.WriteAllTextAsync(Path.Combine(outputDir, detailFile), detailHtml);

        pairResults.Add(new PairResult(pair.Name, added, deleted, modified,
            IsError: false, DetailFile: detailFile));
    }
    catch (OcrException ex)
    {
        // OCR 失敗はそのペアをエラー扱いにして続行する
        Console.Error.WriteLine($"  [Warning] OCR 失敗 ({pair.Name}): {ex.Message}");
        pairResults.Add(new PairResult(pair.Name, 0, 0, 0, IsError: true, DetailFile: ""));
        errorCount++;
    }
}

// ---- index.html を生成する ----
var indexHtml = HtmlReportGenerator.BuildIndexPage(pairResults, unmatchedReps, leftDir, rightDir);
await File.WriteAllTextAsync(Path.Combine(outputDir, "index.html"), indexHtml);

Console.WriteLine($"レポート: {Path.Combine(outputDir, "index.html")}");

// 全ペアが失敗した場合は終了コード 2 を返す
if (errorCount > 0 && errorCount == pairs.Count)
{
    return 2;
}

return 0;
```

**Step 2: ビルド確認**

```bash
dotnet build MyOcrDiff
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

**Step 3: 全テスト確認**

```bash
dotnet test MyOcrDiff.Tests
```

Expected: 全件 PASS

**Step 4: コミット**

```bash
git add MyOcrDiff/Program.cs
git commit -m "feat: add CLI entry point with directory pairing and OCR orchestration"
```

---

## Task 8: 配布準備メモ（実装ではなく手順）

> このタスクはコードを書かず、配布パッケージの組み立て手順を記録する。

### self-contained exe のビルド

```bash
dotnet publish MyOcrDiff -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist/
```

### Python Embedded のセットアップ

1. [Python Embedded zip](https://www.python.org/downloads/windows/) をダウンロード（例: `python-3.11.x-embed-amd64.zip`）
2. `dist/python/` に展開する
3. `python311._pth` を編集して `import site` のコメントを外す（pip の有効化）
4. `get-pip.py` を実行して pip を有効化する
5. `pip install -r ndlocr-lite/requirements.txt --target dist/python/Lib/site-packages/` で依存ライブラリをインストールする

### NDLOCR-Lite の配置

```bash
cp -r ndlocr-lite dist/ndlocr-lite
```

### 最終的な配布フォルダ確認

```
dist/
├── MyOcrDiff.exe
├── ndlocr-lite/src/{ocr.py, model/*.onnx, ...}
├── python/{python.exe, Lib/site-packages/...}
└── NOTICE.txt
```

### NOTICE.txt の内容

```
This software uses NDLOCR-Lite.
  Copyright (c) 2023 National Diet Library, Japan
  Licensed under CC BY 4.0: https://creativecommons.org/licenses/by/4.0/

This software uses DEIMv2 (model weights).
  Licensed under Apache 2.0: https://www.apache.org/licenses/LICENSE-2.0

This software uses PARSeq (model weights).
  Licensed under Apache 2.0: https://www.apache.org/licenses/LICENSE-2.0

This software uses DiffPlex.
  Licensed under Apache 2.0: https://www.apache.org/licenses/LICENSE-2.0
```
