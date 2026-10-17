# 第三方声明

本项目自有部分采用 [AGPL-3.0-only](LICENSE)。下列第三方材料继续适用其原许可证，项目整体许可不替代其版权或许可声明。

## 源码与参考项目

| 材料 | 本项目中的用途 | 版权与许可 |
|---|---|---|
| [Boost.Random](https://github.com/boostorg/random/blob/develop/include/boost/random/binomial_distribution.hpp) | [BoosterBatchRandom.cs](Server/DfoServer/Game/Inventory/Services/BoosterBatchRandom.cs) 中二项分布 BTRD 拒绝采样的改写来源 | Copyright Steven Watanabe 2010；[Boost Software License 1.0](LICENSES/BSL-1.0.txt) |
| [ServerS4A21](https://gitgud.io/rewio/ServerS4A21) | 项目架构参考 | Copyright (c) 2026 rewio；[MIT](LICENSES/ServerS4A21-MIT.txt) |
| [S5A9GmTool](THIRD-PARTY.md) | 内嵌 GM 网页的功能与接口设计参考；NPK 图像处理及物品属性映射代码移植 | Copyright (c) 2026 rewio；原始 MIT 全文与移植范围见 [THIRD-PARTY.md](THIRD-PARTY.md) |

`BoosterBatchRandom.cs` 保留其原版权与完整 Boost 许可。架构参考不表示本项目拥有参考项目的著作权；实际复用部分应继续保留其原声明。

## NuGet 与 SDK 组件

版本以项目文件及还原后的实际依赖为准。当前直接依赖见 [DfoServer.csproj](Server/DfoServer/DfoServer.csproj)，编译期引用见 [ArchitectureCheck.csproj](Tools/ArchitectureCheck/ArchitectureCheck.csproj)。

| 组件 | 用途与版本 | 版权与许可 |
|---|---|---|
| [Microsoft.Data.Sqlite / Microsoft.Data.Sqlite.Core](https://github.com/dotnet/efcore) | SQLite ADO.NET 接口，10.0.9 | Copyright (c) .NET Foundation and Contributors；包声明 © Microsoft Corporation. All rights reserved.；[MIT](LICENSES/dotnet-MIT.txt) |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) | `bundle_e_sqlite3`、`config.e_sqlite3`、`core`、`provider.e_sqlite3`，3.0.3 | Copyright 2014-2025 SourceGear, LLC；[Apache License 2.0](LICENSES/Apache-2.0.txt) |
| [SourceGear.sqlite3](https://www.nuget.org/packages/SourceGear.sqlite3/3.50.4.5) | 原生 SQLite，3.50.4.5 | 包内许可声明为 SQLite 公共领域，见下文 |
| [.NET Runtime](https://github.com/dotnet/runtime) / [.NET WindowsDesktop](https://github.com/dotnet/windowsdesktop) | 自包含发布的运行时与 Windows Forms；所附原始声明取自 10.0.11 | Copyright (c) .NET Foundation and Contributors；[MIT](LICENSES/dotnet-MIT.txt) 及运行时自带的第三方许可 |
| [ASP.NET Core](https://github.com/dotnet/aspnetcore) | 内嵌 GM HTTP 服务使用的运行时，10.0.11 | Copyright (c) .NET Foundation and Contributors；包声明 © Microsoft Corporation. All rights reserved.；[MIT](LICENSES/dotnet-MIT.txt) 及[原始第三方声明](LICENSES/aspnetcore-THIRD-PARTY-NOTICES.txt) |
| [Roslyn](https://github.com/dotnet/roslyn) | SDK 提供的 `Microsoft.CodeAnalysis` / `Microsoft.CodeAnalysis.CSharp`，用于编译期分层检查 | Copyright (c) .NET Foundation and Contributors；[MIT](LICENSES/dotnet-MIT.txt) |
| [Microsoft.NET.ILLink.Tasks](https://github.com/dotnet/runtime/tree/main/src/tools/illink) | 单文件发布使用的 SDK 构建工具，10.0.11 | © Microsoft Corporation. All rights reserved.；[MIT](LICENSES/dotnet-MIT.txt) |

SQLitePCLRaw 的 Apache-2.0 正文与版权声明适用于上述绑定组件；SQLite 原生库的公共领域声明不替代绑定组件的许可证。

SourceGear.sqlite3 包内的原始声明为：

```text
SQLite is Public Domain

https://sqlite.org/copyright.html
```

SQLite 的说明见[官方版权页面](https://sqlite.org/copyright.html)。

## 随附许可正文

- [Apache-2.0.txt](LICENSES/Apache-2.0.txt)：Apache Software Foundation 发布的完整 Apache License 2.0。
- [BSL-1.0.txt](LICENSES/BSL-1.0.txt)：Boost 发布的完整 Boost Software License 1.0。
- [ServerS4A21-MIT.txt](LICENSES/ServerS4A21-MIT.txt)：ServerS4A21 的原始 MIT 版权及许可声明。
- [dotnet-MIT.txt](LICENSES/dotnet-MIT.txt)：Microsoft.Data.Sqlite、.NET Runtime、WindowsDesktop 与 ASP.NET Core 使用的 MIT 正文及 .NET Foundation 声明。
- [dotnet-THIRD-PARTY-NOTICES.txt](LICENSES/dotnet-THIRD-PARTY-NOTICES.txt)：.NET Runtime 10.0.11 包内完整原始第三方声明，保留其中各组件的独立版权与许可。

- [aspnetcore-THIRD-PARTY-NOTICES.txt](LICENSES/aspnetcore-THIRD-PARTY-NOTICES.txt)：ASP.NET Core Runtime 10.0.11 包内完整原始第三方声明。

更新依赖、SDK 或目标平台时，应按实际发布组件同步本清单及原始声明。分发源码或程序时保留相应版权、许可与 NOTICE；第三方声明不改变 [LICENSE](LICENSE) 对本项目整体的要求。

客户端、PVF、游戏商标和玩家数据不由本仓库提供，也不由本项目许可证授权。独立的客户端补丁仓库按其自身许可证处理。
