---
name: mcp-first
description: Unity 操作前先检查 MCP 是否联通；联通则用 MCP 工具，不通才回退文件操作。涉及 Shader/Material/场景/渲染器 Feature/GameObject 等 Unity 资产操作前应调用。
---

# MCP First

做任何 Unity 编辑器相关操作（Shader / Material / 场景 / Renderer Feature / GameObject / 控制台 / 截图）前，**先确认 MCP 联通**，联通就优先用 MCP 工具，不要直接手改文件。

## 1. 检查 MCP 联通

```bash
npx --no-install unity-mcp-cli run-tool ping --input '{}'
# 或查编辑器状态（顺便确认不在编译中）：
npx --no-install unity-mcp-cli run-tool editor-application-get-state --input '{}'
```

- 返回 `SUCCESS` → MCP 联通，用 MCP 工具。
- 返回 HTTP 错误 / 超时 / 连接拒绝 → MCP 不通，回退到文件操作（见第 3 节）。
- 注意：`run-tool ping` 若报 `HTTP 500 ... Tool with Name 'ping' not found`，不代表整个 MCP 不通——那是该工具名被禁用；改用 `editor-application-get-state` 再确认。

## 2. 联通时：用 MCP 工具

用对应的 skill 或直接 `unity-mcp-cli run-tool <tool> --input '<json>'`：

| 操作 | 工具 |
|---|---|
| 找资产 / 材质 / shader | `assets-find`（`t:Shader` / `t:Material`）|
| 读/改资产 | `assets-get-data` / `assets-modify` |
| 建材质 / 建 shader 数据 | `assets-material-create` / `assets-shader-get-data` |
| 编译检查 | `assets-refresh`（会触发编译）→ `editor-application-get-state` 看 `IsCompiling` → `console-get-logs` 看错误 |
| 场景 / GameObject | `scene-*` / `gameobject-*` |
| 跑自定义 C#（注册 Feature、删子资产等）| `script-execute`（走 Unity 序列化，比手改 YAML 可靠）|
| 截图验证 | `screenshot-camera` 等 |

关键原则：
- **序列化资产（.asset / .mat / 场景 / Renderer Feature）绝不要手改 YAML** —— 用 `assets-modify` / `script-execute` 走 Unity API。
- 手改 `.asset` 添加 Feature 子资产尤其危险（missing-script 子资产、`m_RendererFeatureMap` 不同步），一律用 `script-execute` 的 `AssetDatabase.AddObjectToAsset` + `rendererFeatures.Add`。
- shader 文件内容可以用 Write/Edit（Unity 会重新导入），但**改完必须 `assets-refresh` + 查编译状态 + `console-get-logs` 验证**。
- 场景里的 GameObject 引用不能序列化进项目资产（会变 `{fileID: 0}` 导致 Feature 变 NULL），改用 LayerMask/LightMode 筛选或 `script-execute` 动态处理。

## 3. 不通时：回退文件操作

MCP 完全不可用（连接拒绝 / 编辑器没开 / 持续超时）才用文件工具：
- 直接 Read/Edit/Write 文件，但要**手动核对序列化格式**（YAML 缩进、fileID、GUID、map 一致性）。
- 改完无法用 MCP 验证编译/渲染，只能靠仔细复查。
- 告知用户当前 MCP 不可用，操作是回退模式。

## 4. 收尾检查（联通时必做）

每次改动后：
1. `assets-refresh`（触发 Unity 重新导入/编译）。
2. 等编译结束（`editor-application-get-state` 的 `IsCompiling: false`）。
3. `console-get-logs`（`logType: Error`）确认无新增错误 —— 注意过滤已知噪音（MCP 自己的 `IOException`/`Clear Logs`/截图 `cameraRef` 报错）。
4. 需要看效果用 `screenshot-*` + 像素分析。
