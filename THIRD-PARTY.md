# 第三方许可声明

## S5A9GmTool

服务端内嵌 GM 网页的功能范围与接口设计迁移自开发组朋友的 S5A9GmTool（作者 rewio）。迁移采用重写适配：页面与代码在本仓库重新实现，数据访问复用服务端自己的模型、PvfLib 与开发 GM 命令；原项目以 MIT 许可发布，其许可全文保留如下。逐接口迁移状态见 [GM迁移对照](Docs/开发/GM迁移对照.md)。

另外，`Server/DfoServer/Network/Management/ImagePack/*`（NPK 读取、名称解密、图像解码、PNG 编码）和 `GmItemStatSpecs.cs`（物品属性映射表）移植自同一项目（MIT），已改命名空间和可见性，文件头保留来源说明。

---

MIT License

Copyright (c) 2026 rewio

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the
following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO
EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE
USE OR OTHER DEALINGS IN THE SOFTWARE.

