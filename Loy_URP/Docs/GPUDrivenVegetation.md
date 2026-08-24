# 地形 GPU-Driven 植被系统设计方案（Unity Terrain 为主）

> 适用范围：Loy_URP（Unity 6000.3.15f1 / URP / RenderGraph 模式）
> 目标：在 Unity Terrain 上刷植被（草 + 花，多类型），渲染侧完全 GPU-Driven；自定义 mesh 地形为可选替代。
> 关联模块：`Assets/RenderFeature/Hiz`、`SSR`、`SSGI`、`HBAO`。

---

## 目录

1. [目标与范围](#1-目标与范围)
2. [现状与约束](#2-现状与约束)
3. [总体架构](#3-总体架构)
4. [数据模型](#4-数据模型)
5. [GPU 剔除管线](#5-gpu-剔除管线)
6. [间接绘制](#6-间接绘制)
7. [多类型植被（草 + 花）](#7-多类型植被草--花)
8. [地形（Unity Terrain）与植被贴合](#8-地形unity-terrain与植被贴合)
   - 8.1 [Terrain 制作步骤（小场景）](#81-terrain-制作步骤小场景)
   - 8.2 [植被与 Terrain 的贴合](#82-植被与-terrain-的贴合)
   - 8.3 [备选：mesh / Blender / 高度图](#83-备选mesh--blender--高度图)
9. [着色与风](#9-着色与风)
10. [LOD 与距离](#10-lod-与距离)
11. [编辑器刷植被工作流](#11-编辑器刷植被工作流)
12. [模块与文件规划](#12-模块与文件规划)
13. [分阶段实施计划](#13-分阶段实施计划)
14. [性能与内存预算](#14-性能与内存预算)
15. [备选方案对比](#15-备选方案对比)
16. [风险与对策](#16-风险与对策)
17. [附录 A：Compute 剔除骨架](#17-附录-acompute-剔除骨架)
18. [附录 B：植被 Shader 骨架](#18-附录-b植被-shader-骨架)
19. [附录 C：关键 API 速查](#19-附录-c关键-api-速查)

---

## 1. 目标与范围

**目标**

- 在 **Unity Terrain（主）或自定义 mesh 地形**上刷植被，支持多种草、多种花。
- 运行时渲染链路**完全 GPU-Driven**：CPU 侧不逐帧遍历实例，剔除在 GPU 完成，绘制用间接实例化。
- 支持每株精确放置（可增删改），也支持大面积「喷洒填充」。
- 能与本仓库已有屏幕空间特性（HiZ / SSR / SSGI / HBAO）正确共存，并**复用 HiZ 做遮挡剔除**。

**范围外（本期不做，预留接口）**

- 植被物理交互（踩踏、燃烧）。
- 大规模开放世界的流式加载（chunk 化可预留）。
- 运行时地形形变后的植被重吸附（预留高度采样接口即可）。

---

## 2. 现状与约束

已确认的工程事实：

| 项 | 结论 | 影响 |
|---|---|---|
| Unity 版本 | 6000.3.15f1（Unity 6） | 可用 `GraphicsBuffer` / `Graphics.RenderMeshIndirect` / RenderGraph API |
| 管线 | URP，RenderGraph 模式（含 `URP_COMPATIBILITY_MODE` 兼容分支） | 植被 pass 优先走 `RecordRenderGraph`，兼容分支走 `Execute` |
| HiZ | `HizRenderFeature` 已实现，`HiZFrameData : ContextItem` 暴露 `TextureHandle[] mips` + `mipCount` | 植被剔除直接消费 `HiZFrameData`，RenderGraph 自动排序 |
| 深度源 | RG 模式下读 `resourcesData.cameraDepth` | 植被需要写深度时走 depth attachment |
| 命名约定 | shader `Loy_*.shader`，profiler `Loy_*`，目录 `Assets/RenderFeature/<Name>/` | 新模块遵循同一约定 |
| 现有屏幕特效 | SSR / SSGI / HBAO 均依赖 HiZ / 深度 | 植被必须写深度，否则屏幕特效里植被「不存在」 |

---

## 3. 总体架构

```
┌──────────────────────── 编辑时（CPU） ────────────────────────┐
│  笔刷(SceneView 射线) ──► 写入/删除实例 ──► VegetationData 资产 │
│  喷洒填充(密度散点) ──────► 显式实例 Buffer（持久化）           │
└──────────────────────────────┬───────────────────────────────┘
                               │ 上传(增量/全量)
                               ▼
┌──────────────────────── 运行时（GPU） ────────────────────────┐
│  StructuredBuffer<VegetationInstance> 全部实例                 │
│          │                                                    │
│          ▼  Compute: KCull（1 个 pass / 每帧）                  │
│  ① 距离剔除  ② 视锥剔除  ③ HiZ 遮挡剔除                         │
│          │ AppendStructuredBuffer 紧凑化                       │
│          ▼                                                    │
│  StructuredBuffer<VegetationVisible> 可见实例                  │
│  GraphicsBuffer(IndirectArguments) args（instanceCount）       │
│          │                                                    │
│          ▼  间接绘制                                           │
│  DrawMeshInstancedIndirect / RenderMeshIndirect（按类型）      │
│  顶点 shader：SV_InstanceID → 读可见 Buffer → 变换 + 风         │
└────────────────────────────────────────────────────────────────┘
```

一句话：**「编辑时把笔刷写进 Buffer，运行时只做一次 Compute 剔除 + 一次间接绘制」。**

---

## 4. 数据模型

### 4.1 实例结构

```hlsl
// 持久化实例（编辑时写入，运行时只读）
struct VegetationInstance
{
    float3 position;   // 世界坐标（已吸附到地形表面）
    float  scale;      // 统一缩放（或 float2 scaleXZ + float scaleY）
    float  rotationY;  // 绕 Y 轴旋转（弧度）
    uint   typeIndex;  // 指向 VegetationPrototype（草 A / 花 B ...）
    uint   seed;       // 逐株随机种子：决定 tint / 摆动相位 / 大小抖动
    float  radius;     // 包围球半径，剔除用（也可运行时按 type 查表）
};
// 对齐后约 32 字节/株
```

**可见集（剔除后的紧凑输出）**可以精简，例如只保留 shader 需要的字段：

```hlsl
struct VegetationVisible
{
    float4 positionScale;  // xyz=pos, w=scale
    float4 rotSeedType;    // x=rotY, y=seed, z=typeIndex, w=reserved
};
```

### 4.2 Prototype（种类配置）

每种草/花一个 `VegetationPrototype`：

```csharp
[Serializable]
public class VegetationPrototype
{
    public Mesh mesh;                 // 十字片 / 多面片 / 简模
    public Material material;         // 独立材质，或指向图集的材质
    public int    atlasIndex = -1;    // -1 = 独立绘制；>=0 = 图集槽位
    public float  maxDistance = 80f;  // 最远显示距离
    public float  minScale = 0.8f, maxScale = 1.2f;
    public float  windStrength = 1f;  // 风力权重
    public Color  tintMin = Color.white, tintMax = Color.white; // 逐株随机色
    public LOD[]  lods;               // 可选多级 LOD
    // 风、法线贴图、alphaTest 阈值等
}
```

### 4.3 Buffer 管理与持久化

- 用 `GraphicsBuffer.Target.Structured` 承载实例；为减少 CPU-GPU 往返，编辑时维护一份 CPU 侧 `NativeArray<VegetationInstance>`，脏区间按「增量」上传（`SetData` 支持偏移）。
- 容量策略：**固定容量 + 计数**，例如一个类型块 256K 株，满了扩容（翻倍重建）。稀疏/开放世界用「chunk（地块）分块」，每 chunk 一块 Buffer + 一个 AABB，CPU 先按 chunk 包围盒做粗剔除，再进 GPU。
- 持久化：`ScriptableObject` 内嵌 `byte[]`（二进制 blob）或按 chunk 拆成多个资产，避免 `VegetationInstance[]` 直接序列化（百万级会拖慢导入器）。v1 可先单资产 + 二进制，后续再 chunk 化。

---

## 5. GPU 剔除管线

每帧一个 Compute pass（`VegetationCull.compute`），对全量实例做三级剔除：

1. **距离剔除**：`distance(pos, camPos) > prototype[typeIndex].maxDistance → kill`。
2. **视锥剔除**：实例包围球对 6 个视锥平面做保守测试（CPU 每帧算好平面，`SetVectorArray` 传入）。
3. **HiZ 遮挡剔除**（本方案核心优势）：
   - 把实例投影到屏幕，取 AABB，选合适 mip（使 mip 纹素约等于屏幕 AABB 大小），采样该 mip 的最近深度。
   - 若实例包围球近平面深度 > HiZ 中该处深度 + 偏置 → 被遮挡，kill。
   - **HiZ 来源**：`HizRenderFeature.HiZFrameData`（`mips` 为 RFloat 深度金字塔，`mip0` 为场景最远/最近深度，与仓库现有一致）。

命中实例用 `AppendStructuredBuffer<VegetationVisible>` 紧凑化写出（无顺序要求，opaque 植被无需排序）。

**args 的 instanceCount**：使用一块 `GraphicsBuffer(IndirectArguments)` 当 `RWStructuredBuffer<uint>`，每帧在 Compute 开头由单线程清零 `args[1]`，命中时 `InterlockedAdd` 累加。这样 instanceCount 完全在 GPU 侧生成，CPU 不回读。

> 说明：如果追求极致，可把「三级剔除 + 紧凑」拆成两个 pass（先标记可见、再 compact/统计），但 v1 用 `Append` 单 pass 足够，先跑通再优化。

---

## 6. 间接绘制

### 6.1 两种 API 选择

| API | 位置 | 说明 |
|---|---|---|
| `CommandBuffer.DrawMeshInstancedIndirect` | RenderGraph pass 的 `SetRenderFunc` 内 | **主推**：CommandBuffer 版本，能干净嵌入 RenderGraph，支持 `shaderPass`、`MaterialPropertyBlock` |
| `Graphics.RenderMeshIndirect(RenderParams, mesh, argsBuf)` | 自定义 pass（非 RG）或 BRG 场景 | 2022.2+ 新 RenderParams API，更现代，但嵌入 RenderGraph 需注意时机 |

本仓库是 RenderGraph 模式，**v1 用 `DrawMeshInstancedIndirect`**。

### 6.2 绘制参数

```csharp
// args 布局（非索引 mesh 用顶点数语义，索引 mesh 用 indexCount）
// { indexCountPerInstance, instanceCount, startIndex, baseVertex, startInstance }
uint[] args = new uint[5];
args[0] = mesh.GetIndexCount(0);
args[1] = 0; // GPU 侧 InterlockedAdd 填充
args[2] = mesh.GetIndexStart(0);
args[3] = mesh.GetBaseVertex(0);
args[4] = 0;
```

材质侧绑定可见集：`material.SetBuffer("_Visible", visibleBuffer)`（或 `MaterialPropertyBlock`）。

### 6.3 RenderGraph 集成（消费 HiZ，自动排序）

```csharp
class VegetationPass : ScriptableRenderPass
{
    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        HiZFrameData hiZ = frameData.Get<HiZFrameData>(); // 未构建时可能为 null

        using (var builder = renderGraph.AddRasterRenderPass<PassData>(
                   "Loy_Vegetation", out var passData, profiler))
        {
            // 关键：声明读取 HiZ mips → RenderGraph 自动保证本 pass 排在 HiZ Build 之后，
            //       且 HiZ 无消费者时不会被误裁剪。
            if (hiZ != null)
                for (int i = 0; i < hiZ.mipCount; i++)
                    builder.UseTexture(hiZ.mips[i], AccessFlags.Read);

            builder.SetRenderAttachment(color, 0, AccessFlags.Write);     // 写颜色
            builder.SetRenderAttachment(depth, AccessFlags.ReadWrite);    // 写深度（alpha test）
            builder.AllowPassCulling(false);

            builder.SetRenderFunc((data, ctx) =>
            {
                ctx.cmd.DispatchCompute(cullCS, cullKernel, ...);         // 剔除
                ctx.cmd.DrawMeshInstancedIndirect(mesh, 0, material, 0,
                                                  argsBuffer, 0, mpb);    // 绘制
            });
        }
    }
}
```

> 代码为骨架，签名以 6000.3 实际 API 为准（尤其 `SetRenderAttachment` 的重载与 `AccessFlags` 组合）。

### 6.4 渲染顺序与深度（重要）

存在一个「先有鸡还是先有蛋」：HiZ 由**不透明几何的深度**构建，而植被本身也是不透明（alpha test）。

**推荐 v1（简单、够用）**

- 植被在 **HiZ Build 之后**的独立 pass 绘制，剔除用「当帧不透明场景的 HiZ」。
- 植被照常**写深度**（alpha test），保证 SSR / HBAO / SSGI 能从 `_CameraDepthTexture` 看到植被。
- 代价：植被之间不做 HiZ 自遮挡（靠距离剔除 + LOD 控制密度）。

**进阶（后续可选）**

- 植被在 opaque 阶段内绘制、写入深度并参与 HiZ，剔除用「上一帧 HiZ」实现植被互相遮挡，配合异步/运动矢量抗闪烁。成本高，仅当植被密度极大且自遮挡严重时考虑。

---

## 7. 多类型植被（草 + 花）

两种组织方式，按种类数量选择：

| 方式 | 说明 | 优点 | 缺点 |
|---|---|---|---|
| **每类型一次 Draw**（v1 主推） | 每种草/花 = 自己的 Mesh + Material + Args | 各自独立 LOD/风/颜色，最易维护 | drawcall 随类型数线性增长 |
| **图集合批** | 所有草+花打进一张 atlas + 共享十字片 mesh，实例带 `typeIndex` 算 atlas UV 区域 | 1~2 次 draw 画完全部 groundcover | shader 复杂、无法逐种独立 LOD |

**推荐组合**

- groundcover（草 + 小花）：1~2 张图集合批（atlas），实例里 `atlasIndex` 选槽位。
- 高花 / 灌木 / 需要独立 LOD 的植物：每类型独立 draw。

这样 drawcall 数量可控，又能对重点植物精细控制。

---

## 8. 地形（Unity Terrain）与植被贴合

### 8.1 Terrain 制作步骤（小场景，主路线）

结论：小场景直接用 Unity 内置 Terrain，**不需要转 mesh、不需要会雕刻**。

#### 创建与尺寸

1. `GameObject → 3D Object → Terrain` 创建（自动带 `Terrain` + `TerrainData` + `TerrainCollider`）。
2. Terrain Settings 里定尺寸与分辨率：

| 参数 | 小场景建议 | 说明 |
|---|---|---|
| Terrain Width / Length | 200 × 200 左右 | 场景实际占地 |
| Terrain Height | 30 ~ 50 | 最高海拔，够起丘陵即可 |
| Heightmap Resolution | 513（或 257 / 1025） | 高度图精度，513 足够 |
| Base Map Distance 等 | 默认 | 小场景不用动 |

3. 用内置刷子塑造地形（本质是「刷高度图」，不需要雕刻技能）：
   - **Raise / Lower Terrain**：抬升/下沉，刷起伏；
   - **Paint Height / Set Height**：刷到指定高度，做平地或山顶；
   - **Smooth Height**：平滑，修掉尖锐；
   - **Stamp Terrain**：套现成噪声/山形。

#### 地表贴图

- Terrain Layers 里加 1~2 张贴图（草、裸土），用 **Paint Texture** 刷过渡。小场景 1~2 层就够。
- 不需要自己写 splatmap——Terrain 内置就是按层权重混合的。

#### 植被密度/类型信息（可选）

想让「某些区域长草、某些区域长花」，小场景最省事的做法是**读地表贴图层的权重**：

- `TerrainData.GetAlphamaps()` 读各层权重，例如「草地层权重高 → 草多、花少」；
- 或额外刷一张 mask 贴图 / 顶点色做精确控制。

### 8.2 植被与 Terrain 的贴合

和自定义 mesh 地形最大的区别：**不用烘焙高度图**，直接读 `TerrainData`。

- **取高度/法线（编辑时）**：`TerrainData.GetInterpolatedHeight(x, z)` / `GetInterpolatedNormal(x, z)`，或 `Terrain.SampleHeight(worldPos)`，直接给喷洒散点/笔刷落点用，无需额外 height map。
- **笔刷落点（编辑时）**：SceneView 射线打 `TerrainCollider`（`Physics.Raycast`），取 `hit.point` + `hit.normal` 作为实例 `position` 与朝向。
- **运行时是否要采样地形**：实例已存**世界坐标**，运行时绘制不需要再采样；只有编辑器放点、或未来动态地形才读 TerrainData。
- **地形本身渲染**：Terrain 是不透明几何，正常写深度、进 HiZ——SSR / HBAO / SSGI 与植被的 HiZ 遮挡都照常。

### 8.3 备选：mesh / Blender / 高度图

一般用不到。只有要接 mesh 专用工具、特定 shader，或做运行时地形变形时才需要：

- **Terrain → mesh**：`TerrainData.GetHeights()` 读高度 → 写成高度图 → 喂给下面的脚本生成 mesh（Unity 没有一键导出，脚本或资产均可）。
- **Blender 雕刻**：美术需要更有机的细节时（Plane 细分 → Sculpt → 导出 FBX，略）。
- **高度图生成网格**：`HeightmapToMesh.cs`（也是「Terrain 转 mesh」的落地方式）：

```csharp
// HeightmapToMesh.cs —— 放 Assets/Editor/ 下（最小实现：灰度图 → 地形 mesh）
// 需 using UnityEngine; using UnityEditor; 且高度图勾选 Read/Write Enabled
[MenuItem("Tools/Terrain/Heightmap To Mesh")]
static void Build()
{
    Texture2D h = Selection.activeObject as Texture2D;
    int res = 256; float size = 100f; float maxH = 10f;   // 分辨率 / 占地 / 最高海拔
    var verts = new Vector3[(res + 1) * (res + 1)];
    var uv = new Vector2[verts.Length];
    var tris = new int[res * res * 6];
    for (int z = 0; z <= res; z++)
    for (int x = 0; x <= res; x++)
    {
        int i = z * (res + 1) + x;
        float hgt = h.GetPixelBilinear(x / (float)res, z / (float)res).r;
        verts[i] = new Vector3(x / (float)res * size, hgt * maxH, z / (float)res * size);
        uv[i] = new Vector2(x / (float)res, z / (float)res);
    }
    int t = 0;
    for (int z = 0; z < res; z++)
    for (int x = 0; x < res; x++)
    {
        int a = z * (res + 1) + x, b = a + 1, c = a + res + 1, d = c + 1;
        tris[t++] = a; tris[t++] = c; tris[t++] = b;
        tris[t++] = b; tris[t++] = c; tris[t++] = d;
    }
    var mesh = new Mesh { name = h.name + "_Terrain" };
    mesh.vertices = verts; mesh.uv = uv; mesh.triangles = tris;
    mesh.RecalculateNormals();                       // 若法线朝下，交换三角形绕序
    AssetDatabase.CreateAsset(mesh, "Assets/Terrain_" + h.name + ".asset");
}
```

> mesh 路线才需要额外处理 UV / Collider / 贴图；Terrain 路线以上全免。

---

## 9. 着色与风

- **顶点风**：顶点 shader 内，用 `worldPos + _Time + seed` 生成噪声（程序化噪声或采样一张风向量场贴图），按 `prototype.windStrength` 与「距根部距离」加权位移。草类弯折大、花类较小。
- **逐株差异**：`seed` 决定 tint（在 `tintMin/tintMax` 间插值）、摆动相位、缩放抖动，避免「一片一模一样的草」。
- **交互预留**：风场贴图接口上预留一块 `R32` 交互 RT（玩家踩过的地方写入弯折），后续做踩踏。

**实现路径建议**：基于 URP Lit 手写 shader（`Loy_Vegetation.shader`）或在 Shader Graph 中用 Custom Function 节点接实例读取 + 风。手写更可控，推荐手写。

---

## 10. LOD 与距离

- 每种 `VegetationPrototype` 可配 2 级 LOD：完整 mesh + 简化 mesh（或十字片 / billboard）。
- 距离阈值在 Compute 剔除里按 `distance` 选 LOD，把结果写进可见集（或按 LOD 分 args）。
- 过渡用 **dither（棋盘/噪声 alpha）** 做 cross-fade，避免 pop。
- 对大片 grass，常见做法是「近处 mesh、远处 alpha 密度衰减到 0」，配合 `maxDistance`。

---

## 11. 编辑器刷植被工作流

自定义 `EditorWindow` + `SceneView.duringSceneGui`：

- 原型选择器：选草 A / 花 B，调笔刷半径、密度、强度。
- 操作：**画（增）**、**擦（删）**、**清除**、**喷洒填充**（半径内按密度随机散点，结果落成显式实例）。
- 落点：射线打地形 collider → 高度/法线吸附。
- **Undo/Redo**：记录「本次 stroke 新增/删除的实例区间」而非整块 Buffer 快照。
- 持久化：写回 `VegetationData` 资产（二进制 blob + 增量上传）。

---

## 12. 模块与文件规划

与现有约定对齐，新建：

```
Assets/RenderFeature/Vegetation/
  VegetationRenderFeature.cs        # ScriptableRendererFeature 入口
  VegetationPass.cs                 # RenderGraph pass：剔除 + 间接绘制
  VegetationData.cs                 # 资产：实例 blob + Prototype 列表
  VegetationPrototype.cs            # 种类配置
  VegetationInstance.cs             # 结构体 + GraphicsBuffer 管理
  VegetationCull.compute            # 距离/视锥/HiZ 剔除 + Append 紧凑
  VegetationWind.cginc              # 风场/噪声公共函数
  Editor/
    VegetationBrushEditor.cs        # SceneView 笔刷 + 喷洒 + Undo
    VegetationDataEditor.cs         # Prototype 编辑面板
Shaders/
  Loy_Vegetation.shader             # URP Lit 变体：实例读取 + 风 + 图集/tint
```

依赖改动：`HizRenderFeature` 无需改动，直接消费其 `HiZFrameData`（若需暴露更多信息再扩展）。

---

## 13. 分阶段实施计划

| 里程碑 | 内容 | 验收标准 |
|---|---|---|
| **M0 最小闭环** | 单类型（一种草）、无 HiZ：视锥+距离剔除 + `DrawMeshInstancedIndirect` + 简单风 | 屏幕出现一片能随风摆的草，CPU 无逐帧遍历 |
| **M1 多类型** | Prototype 化、每类型独立 draw、`typeIndex`、逐株 tint/scale | 草+多种花同屏，各自独立材质/LOD |
| **M2 HiZ 遮挡** | 消费 `HiZFrameData` 做遮挡剔除，接 RenderGraph 排序 | 被地形/建筑挡住的植被被剔除，帧率提升可测 |
| **M3 编辑工具** | 笔刷增删擦、喷洒、高度/法线吸附、Undo、持久化 | 美术能直接刷草刷花，重启工程数据不丢 |
| **M4 打磨** | 图集合批、LOD cross-fade、风场/交互预留、性能预算达标 | 百万株级别稳定帧率，与 SSR/HBAO/SSGI 共存无异常 |

---

## 14. 性能与内存预算

以 100 万株、屏幕可见约 10 万株估算（32B/实例）：

| 项 | 估算 | 说明 |
|---|---|---|
| 实例 Buffer | ~32 MB | 100 万 × 32B |
| 可见集 Buffer | ~1.6 MB | 10 万 × 16B |
| args / 高度图 | < 20 MB | 高度图 2K R16 ≈ 8MB |
| CPU 每帧开销 | ~0（除 1 次 Dispatch + 1 次 Draw 提交） | 无逐株遍历 |
| GPU 剔除 | 1 个 Compute pass，百万级 ~0.1~0.3ms | 取决于线程分组与带宽 |
| drawcall | = 植被类型数（或图集批次数） | 与实例数无关 |

优化要点：`Append` 单 pass 先跑通；瓶颈若在带宽，再把实例按类型分 Buffer、按 chunk 分块、压缩字段（半精度/量化）。

---

## 15. 备选方案对比

| 方案 | 适配 mesh 地形 | 多种类 | 与 HiZ/SSR 集成 | 工作量 | 结论 |
|---|---|---|---|---|---|
| **自研（本文）** | ✅ 完全可控 | ✅ | ✅ 直接复用 HiZ | 高 | 契合本仓库定位，推荐 |
| GPU Instancer（资产） | ✅ | ✅ | ⚠️ 需适配 | 低 | 求快可选，但深度定制受限 |
| Nature Renderer + The Vegetation Engine | ✅（主打 mesh 地形） | ✅ | ⚠️ 独立体系 | 低 | 美术优先、不碰代码时首选 |
| Unity 6 内置（GPU Resident Drawer / BRG） | ❌ 非刷植被工作流 | 有限 | — | — | 不适用 |

**决策线**：要「可控、能定制风/遮挡/交互、和现有渲染框架深集成」→ 自研；要「美术立刻铺草铺花、不写代码」→ Nature Renderer + TVE。

---

## 16. 风险与对策

| 风险 | 对策 |
|---|---|
| RenderGraph API 签名细节（6000.3 与文档差异） | 以编译错误为准调整，先跑 M0 最小闭环验证 API |
| HiZ 深度语义（最近/最远）与植被 AABB 采样方向不一致 | 复用仓库现有 HiZ 语义，先小场景对拍验证，偏置量可调 |
| 植被写深度但不在 HiZ 中，屏幕特效可能边缘异常 | v1 接受（SSR/HBAO 主要依赖 `_CameraDepthTexture`，植被已写深度）；异常再评估 |
| Append/Consume 跨平台（Metal/Vulkan）计数器行为差异 | 用 `InterlockedAdd` + 固定 args 的方案，兼容性更好 |
| 百万级持久化序列化慢 | 用二进制 blob，避免 `ScriptableObject` 直接存数组；后续 chunk 化 |
| 无 HiZ 时植被照常显示 | 代码里 `hiZ == null` 时跳过遮挡剔除（退化到视锥+距离） |

---

## 17. 附录 A：Compute 剔除骨架

```hlsl
// VegetationCull.compute
#pragma kernel KCull

StructuredBuffer<VegetationInstance> _Instances;
RWStructuredBuffer<uint>            _Args;            // {indexCount, instanceCount, startIndex, baseVertex, startInstance}
AppendStructuredBuffer<VegetationVisible> _Visible;
StructuredBuffer<float4>            _FrustumPlanes;   // 6 planes
float4                             _CamPos;           // xyz=pos, w=1/(far-near) 之类
Texture2D<float>                   _HiZMip0;         // ..._HiZMipN
float4                             _HiZSize;         // xy=尺寸, z=mipCount
uint                               _InstanceCount;

[numthreads(64,1,1)]
void KCull(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i == 0) _Args[1] = 0;            // 清零 instanceCount（需 DeviceMemoryBarrier 或单独 pass）
    if (i >= _InstanceCount) return;

    VegetationInstance inst = _Instances[i];
    float dist = distance(inst.position, _CamPos.xyz);

    // ① 距离剔除（按类型 maxDistance）
    if (dist > _MaxDistance[inst.typeIndex]) return;

    // ② 视锥剔除（包围球 vs 6 平面）
    if (!SphereInFrustum(inst.position, inst.radius, _FrustumPlanes)) return;

    // ③ HiZ 遮挡剔除
    if (_HiZSize.z > 0 && IsOccludedByHiZ(inst.position, inst.radius, dist)) return;

    // 命中 → 紧凑写出
    VegetationVisible v;
    v.positionScale = float4(inst.position, inst.scale);
    v.rotSeedType   = float4(inst.rotationY, asfloat(inst.seed), asfloat(inst.typeIndex), 0);
    _Visible.Append(v);

    InterlockedAdd(_Args[1], 1);         // 累加 instanceCount
}

bool IsOccludedByHiZ(float3 pos, float radius, float dist)
{
    // 投影到屏幕 → AABB → 选 mip → 采样最近深度 → 与自身深度比较
    // （具体采样方向与偏置需与本仓库 HiZ 深度语义对齐后确定）
    return false; // 骨架占位
}
```

---

## 18. 附录 B：植被 Shader 骨架

```hlsl
// Loy_Vegetation.shader（URP Lit 变体，节选关键部分）
StructuredBuffer<VegetationVisible> _Visible;
float  _WindStrength;
float4 _WindParams;            // 时间、风向、强度、噪声缩放

// 顶点阶段：跳过 Unity 常规 MVP，用实例数据 + 风
Varyings vert(Attributes IN, uint instanceID : SV_InstanceID)
{
    VegetationVisible v = _Visible[instanceID];
    float3 worldPos = v.positionScale.xyz;

    // 对象空间偏移（十字片顶点）→ 旋转/缩放 → 世界
    float3 local = IN.positionOS.xyz;
    local = RotateY(local, v.rotSeedType.x) * v.positionScale.w;
    worldPos += local;

    // 顶点风：根部不动，梢部弯折
    float wind = WindDisplacement(worldPos, v.rotSeedType.y, _WindParams, _WindStrength);
    worldPos += wind * (IN.uv.y);          // 十字片 v 轴 = 高度

    float4 clipPos = TransformWorldToHClip(worldPos);
    // 逐株 tint / 图集 UV 计算（v.rotSeedType.z = typeIndex）
    ...
    return OUT;
}
```

---

## 19. 附录 C：关键 API 速查

| 用途 | API |
|---|---|
| 实例 Buffer | `GraphicsBuffer(Target.Structured, count, stride)` |
| 间接参数 | `GraphicsBuffer(Target.IndirectArguments, ...)` |
| 上传实例 | `buffer.SetData(array, 0, 0, count)`（支持偏移=增量上传） |
| 绑定可见集到材质 | `material.SetBuffer("_Visible", buf)` 或 `MaterialPropertyBlock.SetBuffer` |
| 间接绘制（RG pass 内） | `cmd.DrawMeshInstancedIndirect(mesh, submesh, mat, pass, argsBuf, offset, mpb)` |
| 间接绘制（RenderParams） | `Graphics.RenderMeshIndirect(in RenderParams, mesh, argsBuf, count, start)` |
| 视锥平面 | `GeometryUtility.CalculateFrustumPlanes(camera)` → 传入 Compute |
| HiZ 消费 | `frameData.Get<HiZFrameData>()` → `builder.UseTexture(mips[i], AccessFlags.Read)` |

---

*文档结束。实施顺序建议严格按 M0→M4，先跑通 M0 最小闭环再叠加 HiZ 与多类型。*
