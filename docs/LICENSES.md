# 使用 OSS ライセンス一覧

JsCoverageReporter が依存しているオープンソースソフトウェアとそのライセンスの一覧です。

---

## 本体（JsCoverageReporter）

### Microsoft.Playwright 1.58.0

- **用途**: Chromium ブラウザの制御・操作、CDP (Chrome DevTools Protocol) 経由でのカバレッジ収集
- **ライセンス**: Apache License 2.0
- **著作権**: Copyright (c) Microsoft Corporation
- **ソース**: https://github.com/microsoft/playwright-dotnet

```
Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0
```

---

### .NET 8 ランタイム / SDK

- **用途**: アプリケーションの実行基盤（C# コンパイラ・BCL・ランタイム）
- **ライセンス**: MIT License
- **著作権**: Copyright (c) .NET Foundation and Contributors
- **ソース**: https://github.com/dotnet/runtime

```
The MIT License (MIT)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.
```

---

## テスト用（JsCoverageReporter.Tests）

テストプロジェクトはアプリケーション本体の実行には不要です。

### xunit 2.5.3

- **用途**: .NET 向け単体テストフレームワーク
- **ライセンス**: Apache License 2.0
- **著作権**: Copyright (c) .NET Foundation and Contributors
- **ソース**: https://github.com/xunit/xunit

```
Licensed under the Apache License, Version 2.0
http://www.apache.org/licenses/LICENSE-2.0
```

---

### xunit.runner.visualstudio 2.5.3

- **用途**: Visual Studio / `dotnet test` での xUnit テスト実行アダプター
- **ライセンス**: Apache License 2.0
- **著作権**: Copyright (c) .NET Foundation and Contributors
- **ソース**: https://github.com/xunit/visualstudio.xunit

```
Licensed under the Apache License, Version 2.0
http://www.apache.org/licenses/LICENSE-2.0
```

---

### Microsoft.NET.Test.Sdk 17.8.0

- **用途**: `dotnet test` コマンドによるテスト実行インフラ
- **ライセンス**: MIT License
- **著作権**: Copyright (c) Microsoft Corporation
- **ソース**: https://github.com/microsoft/vstest

```
The MIT License (MIT)
https://opensource.org/licenses/MIT
```

---

### NSubstitute 5.1.0

- **用途**: テスト用モックオブジェクト生成ライブラリ
- **ライセンス**: BSD 3-Clause License
- **著作権**: Copyright (c) 2009 Anthony Egerton (nsubstitute@delfish.com) and David Tchepak (dave@davesquared.net)
- **ソース**: https://github.com/nsubstitute/NSubstitute

```
Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice,
   this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.
3. Neither the name of the copyright holder nor the names of its contributors
   may be used to endorse or promote products derived from this software without
   specific prior written permission.
```

---

### coverlet.collector 6.0.0

- **用途**: `dotnet test` 実行中のコードカバレッジ収集
- **ライセンス**: MIT License
- **著作権**: Copyright (c) 2018 Toni Solarin-Sodara
- **ソース**: https://github.com/coverlet-coverage/coverlet

```
The MIT License (MIT)
https://opensource.org/licenses/MIT
```

---

## まとめ

| パッケージ | バージョン | ライセンス | 用途 |
|-----------|-----------|-----------|------|
| Microsoft.Playwright | 1.58.0 | Apache 2.0 | ブラウザ制御・CDP カバレッジ収集 |
| .NET 8 Runtime/SDK | 8.0.x | MIT | 実行基盤 |
| xunit | 2.5.3 | Apache 2.0 | テストフレームワーク |
| xunit.runner.visualstudio | 2.5.3 | Apache 2.0 | テスト実行アダプター |
| Microsoft.NET.Test.Sdk | 17.8.0 | MIT | テスト実行インフラ |
| NSubstitute | 5.1.0 | BSD 3-Clause | テスト用モック |
| coverlet.collector | 6.0.0 | MIT | テストカバレッジ収集 |

---

*このリストは `JsCoverageReporter.csproj` および `JsCoverageReporter.Tests.csproj` に記載された依存関係を元に作成しています。*
