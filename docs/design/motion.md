# TKCode 动效设计（Motion）

> 目标：所有“出现 / 消失 / 展开 / 收起 / 开合 / 变形”都有连续的过渡，让界面看起来顺滑。
> 已确定的方向：混合方案（弹层用 AtomUI，其余用 TKCode 自己的一层薄 Motion 工具）、偏丰富灵动的风格、
> 面板开合用宽度动画并在动画期间降载。

## 1. 现状

全项目目前 **没有任何 Transition / Animation**，所有状态切换都是 `IsVisible` 硬切或代码里直接改列宽：

| 类别 | 位置 | 现在的实现 |
|---|---|---|
| 弹层 | ProjectPicker / BranchPicker / ModelPicker / PermissionPicker / “+”添加面板 | Avalonia 原生 `Popup`，没有动画 |
| 弹层内部状态 | 模型选择器参数子菜单（`ExpandedParameter`）、权限“完全授权”二次确认、分支切换确认 | `IsVisible` 切换，尺寸跳变 |
| 浮层 | ⌘K 命令面板（`PaletteOverlay`）、添加厂商模板选择（`IsPickingTemplate`） | `IsVisible` 切换 |
| 页面 | 设置页 ↔ 主界面、设置导航分区、工作台标签切换 | `IsVisible` / `ContentControl` 硬切 |
| 展开收起 | 思考块、活动组、工具步骤、组内思考步骤、侧栏会话列表、文件树 | `IsVisible` + 两个箭头图标互换；文件树是扁平 `VisibleNodes` 插入删除 |
| 面板 | 侧栏（`IsSidebarOpen`）、编辑区、文件树、编辑区最大化 | `MainWindow.UpdateColumns` 直接设 `GridLength` |
| 输入框 | 紧凑单行胶囊 → `multiline` 两段布局；`TextArea.IsAutoSize` 逐行增高 | 样式类切换，Grid 行列重排，瞬间跳变 |
| 出现消失 | 审批卡、排队卡、回到底部按钮、附件缩略图、状态提示、发送 ↔ 停止 | `IsVisible` 切换 |
| 微交互 | `TKCode.RowButton` 系列的悬停 / 按下 / 选中、`IconTab` 下划线、分段选择 | 样式 Setter 瞬间换色 |
| 会话内容 | 新的用户消息、流式追加的回复块、切换会话 | 直接出现 |

## 2. AtomUI 能直接用的部分（NuGet 公开 API）

- `atom:Popup`（`AtomUI.Desktop.Controls.Popup`）：公开 `OpenMotion` / `CloseMotion`（`AbstractMotion`）、`MotionDuration`、`IsMotionEnabled`，关闭会等动画播完。
- 公开的动效：`ZoomBig*`、`Zoom*`、`SlideUp/Down/Left/Right*`、`Move*`、`Fade*`、`Expand/CollapseMotion`，都可以继承 `AbstractMotion` 自定义。
- 时长 token：`MotionDurationFast/Mid/Slow/VerySlow` = 100 / 200 / 300 / 800ms；`EnableMotion=false` 时全部归零。读取方式：`{atom:SharedTokenResource MotionDurationMid}`，或在代码里用 `SharedTokenKind`。
- 用不了的部分：Collapse / Expander 用的 `ContentExpansionAnimator` 是 internal；没有缓动 token；没有通用的“带动画的显示隐藏”控件。
- AOT：MotionScene 本身没有反射。`atom:Popup` 内部对 Avalonia 私有成员有反射（`PopupReflectionExtensions`、`PopupLightDismissRegistration`），ILLink 能分析到，但和 Avalonia 版本绑得比较紧。AtomUI 的 Select / Tooltip 已经在用它，所以不算新增风险。**不使用** `AnimatableReflectionExtensions`。

## 3. 动效规范（丰富灵动）

`Motion/MotionTokens.cs` 统一定义，所有动效只从这里取值：

| Token | 值 | 用途 |
|---|---|---|
| `Micro` | 140ms | 悬停 / 按下换色、箭头旋转 |
| `Short` | 220ms | 出现消失、弹层、展开收起 |
| `Medium` | 300ms | 面板开合、浮层、输入框变形 |
| `Long` | 360ms | 页面切换（设置页） |
| 退出 | 进入时长 × 0.7 | 消失总是比出现快 |
| `EaseEmphasized` | cubic-bezier(0.2, 0, 0, 1) | 进入、展开 |
| `EaseExit` | cubic-bezier(0.3, 0, 1, 1) | 退出、收起 |
| `Spring` | 自定义 `Easing`（阻尼比约 0.72，轻微过冲） | 弹层缩放、面板、卡片入场 |
| `Stagger` | 每项 24ms，最多 8 项 | 列表逐项入场 |

**统一开关**：设置里的“减少动态效果”（settings.json 的 `reduceMotion`），或 macOS 的“减少动态效果”（`NSWorkspace.accessibilityDisplayShouldReduceMotion`，通过 ObjC P/Invoke 读取，AOT 安全）其中任一生效时，所有时长归零，只保留状态结果。

**可打断**：所有动画都从当前进度反向，不从头重播。连续快速点击展开 / 收起不会跳变。

## 4. TKCode Motion 工具层（`src/TKCode.Desktop/Motion/`）

全部基于 Avalonia 的 `Transitions` / `Animation` 加自定义 `Easing`，不用反射，NativeAOT 安全。

1. **`Reveal`（Decorator）**：展开收起。替代 `IsVisible="{Binding IsExpanded}"`：
   ```xml
   <motion:Reveal IsOpen="{Binding IsExpanded}">…</motion:Reveal>
   ```
   - 内部有一个 0→1 的 `Progress` 属性。`MeasureOverride` 返回子元素期望高度 × Progress，并裁剪越界部分，所以周围布局会跟着平滑重排（效果和 AtomUI Collapse 一样）。
   - 同时做透明度 0→1 和 Y 方向 −6px→0 的位移。
   - 收起播完才设 `IsVisible=false`，可打断。
   - 带 `Orientation` 属性，也能做横向展开。
2. **`Presence`（附加属性）**：出现消失，不占位变化的元素用它。
   - `motion:Presence.IsPresent="{Binding …}"`，并可选 `Presence.Effect="Fade|Scale|SlideUp|SlideDown|Pop"`。
   - 只用 Opacity 和 RenderTransform 合成，不触发布局。退出动画播完才隐藏。
3. **`Crossfade`（Panel）**：同一位置的内容互换，比如发送 ↔ 停止、Eye ↔ EyeOff、最大化 ↔ 还原、标签页内容、设置分区。
   - 旧内容淡出并缩小到 0.9，新内容淡入并从 0.9 弹回 1，可选横向位移。
4. **`ItemsMotion`（附加属性，挂在 ItemsControl 上）**：
   - 容器 `ContainerPrepared` 时，如果这一项是**初次加载之后**新增的，就淡入、上移 8px 并错开入场；初次加载、切换数据源时不播。侧栏“展开更多”追加的行同样播放入场（只限于当时已创建的行）。
   - 删除项可选折叠退出（延迟真正移除）。
5. **`Rotate`（附加属性）**：箭头只保留一个 `ChevronRight`，`IsExpanded` 时 `RotateTransform` 从 0 转到 90°。替代现在的两个图标互换。
6. **`Morph`（FLIP 工具）**：给输入框这类会“重新排版”的控件用。
   - 切换前记录各子元素相对容器的位置，切换并完成布局后，给它们加反向的 `TranslateTransform`，再动画到 0。
   - 容器高度和圆角同时过渡。
7. **`ColumnAnimator`**：`GridLength` 不能直接做 Transition，所以按帧推进列宽（`TopLevel.RequestAnimationFrame`，用 Spring 缓动），每帧调用现有的 `UpdateColumns` 计算目标值。

## 5. 逐处方案

### 5.1 弹层 / 浮层
- 5 个 `Popup` 改成 `atom:Popup`：
  - `OpenMotion` 用自定义的 `PopoverInMotion`：缩放 0.94→1，透明度 0→1，Y 方向 −6→0，Spring 缓动，220ms。
  - `CloseMotion` 反向，150ms。
  - 缩放原点放在触发按钮一侧，`Placement=BottomEdgeAlignedLeft` 时原点在左上角。
  - 先确认 `atom:Popup` 的轻点关闭（light dismiss）和 `Opened` / `Closed` 事件与现在一致（`ProjectPickerView` 和 `BranchPickerView` 打开时要聚焦搜索框）。
- 模型选择器的参数子菜单：在弹层内部横向用 `Presence`（SlideLeft + Fade）。切换不同参数时用 `Crossfade`。
- 权限“完全授权”二次确认、分支切换确认：用 `Crossfade`，并让弹层尺寸跟着变化。弹层外框用 `Reveal` 的尺寸平滑能力，宽高同时过渡。
- ⌘K 命令面板 / 添加厂商浮层：
  - 遮罩透明度 0→1。
  - 卡片缩放 0.96→1，Y 方向 −12→0，Spring 缓动，260ms。
  - 关闭时反向，180ms。
  - 子页（面包屑 ←）进入时内容从右侧滑入，返回时从左侧滑入。
  - 结果列表数量变化时，`ScrollViewer` 高度平滑过渡。
  - 高亮项改成一条滑动的高亮底板，用 ↑ ↓ 移动时会滑过去。

### 5.2 展开 / 收起
- 思考块、活动组、工具步骤、组内思考步骤：`IsVisible` 换成 `Reveal`，箭头改用 `Rotate`。
  - 这些都在 StreamMark `TranscriptView` 的虚拟化条目里，高度变化会触发重新测量。需要确认 TranscriptView 在条目高度连续变化时锚点保持正确（展开时视口不跳）。必要时给 StreamMark 加一个“条目尺寸动画中”的提示 API。
- 侧栏会话列表折叠：`Reveal`，箭头旋转。
- 文件树（扁平列表）：
  - 展开目录时，新插入的子节点用 `ItemsMotion` 错开入场。
  - 收起时子节点折叠退出，先播动画再从 `VisibleNodes` 移除，需要在 `FileExplorerViewModel` 和视图之间加一个延迟移除的钩子。

### 5.3 面板开合（宽度动画 + 降载）
- 侧栏：外层宽度 0↔256 按帧动画，内容固定 256 宽并裁剪，同时 X 方向 −16→0、透明度过渡，所以侧栏自身不会重新排版。
  - 标题栏左边距（红绿灯让位，`UpdateHeaderInsets`）跟着同步插值。
- 编辑区 / 文件树 / 最大化：用 `ColumnAnimator` 推进 `MainGrid` 的列宽。面板内部内容固定成目标宽度并裁剪。
- 降载：
  - 动画期间把会话区的 `TranscriptView` 固定在起始宽度（左对齐并裁剪），只有容器在变宽或变窄。
  - 动画结束后释放，只重排一次，避免长会话每帧重新排版 Markdown。
  - 实施前先用 `HistoryBenchTests` 测一次宽度变化的重排耗时。如果单次重排足够便宜（< 4ms），就跳过降载，直接逐帧重排，效果最自然。
- 分割条拖拽时不做动画。

### 5.4 输入框：单行 → 多行
- 紧凑胶囊切到 `multiline` 时用 `Morph`：
  - 外框高度平滑增高，圆角 22 → `RadiusLarge`（`CornerRadiusTransition`）。
  - 附件、模型选择器、发送按钮从单行里的位置滑到底部工具栏的新位置。
  - 输入框宽度变化时文字不重排闪烁，切换瞬间就按新宽度排版，只做位置过渡。
  - 回到单行时反向。
- `TextArea` 自动增高（多一行或少一行）：外框高度用 Spring 过渡，不再逐行跳。
- 空状态的居中输入框在发送第一条消息后变成底部停靠输入框：两个是不同实例。
  - 方案 A：用共享元素过渡，记录空状态输入框的位置，在覆盖层里放一个快照并让它飞到底部位置。
  - 方案 B：空状态整体淡出、上移，会话视图淡入。
  - 先做 B，A 作为后续增强。

### 5.5 微交互与入场
- 按钮主题（`RowButton` / `MenuItem` / `TabButton` / `ChipButton` / `PillButton` / `TileButton`）：
  - `Background`、`BorderBrush`、`Foreground` 加上 `BrushTransition`（Micro）。
  - `:pressed` 时缩放到 0.97。
  - 磁贴悬停时 Y 方向上浮 −2px，并加深阴影。
- 模型选择器 Provider 页签：下划线改成一条共享的指示条，在页签之间滑动并改变宽度。分段选择的选中底板同样滑动。
- 审批卡、排队卡：`Presence`（SlideUp + Fade + 轻微 Spring）。卡片之间的高度变化用 `Reveal` 平滑过渡，输入框被往上推时也是顺滑的。
- 回到底部按钮：用 `Pop`，从底部弹出并缩放。附件缩略图：用 `ItemsMotion` 入场，删除时缩小退出。状态提示：用 `Reveal`。
- 发送 ↔ 停止：用 `Crossfade`，带缩放与轻微旋转。
- 会话内容：
  - 新的用户消息气泡：从右下方 Y 方向 +12 滑入并淡入。
  - 流式期间新出现的回复块：淡入，Y 方向 +6。
  - 切换会话时整个会话视图交叉淡入。加载历史或分页时都不播。
  - 前提是 StreamMark 的 `TranscriptView` 能告诉调用方某条目是“刚追加的”，比如提供条目实现事件或 `IsNew` 标记。StreamMark 是我们自己的库，可以补这个 API。
- 侧栏新会话：在列表顶部入场。
- 设置页 ↔ 主界面：
  - 主界面淡出并缩放到 0.98，设置页从 1.02 缩回 1 并淡入，用时 Long。
  - 设置导航切换时，内容交叉淡入，同时 Y 方向 +8 位移。
- `ContextRing`：数值变化时有过渡。

## 6. 验证

- 在 `tests/TKCode.Desktop.Tests` 的 Headless 环境里默认关闭动效，保证现有快照稳定。
- 新增动效测试：手动推进渲染计时器，截取 0% / 50% / 100% 三帧 PNG 检查，覆盖 Reveal、Presence、弹层、输入框变形、面板。还要断言“动画结束后 `IsVisible` / 布局与无动效时一致”，以及“中途反向不跳变”。
- 每个阶段都执行 AOT 发布（0 个 IL 警告），并运行发布出来的二进制。
- 帧率：在长会话（`HistoryBenchTests` 的数据）下开合面板、展开活动组，确认没有掉帧（每帧 < 16ms）。

## 7. 分阶段实施

| 阶段 | 内容 |
|---|---|
| P0 基础 | `MotionTokens`、Spring 缓动、减少动态效果开关（设置项 + macOS 系统设置）、`Reveal` / `Presence` / `Crossfade` / `Rotate` / `ItemsMotion`，以及测试用的动画时间推进工具 |
| P1 弹层浮层 | 5 个 `atom:Popup` + 自定义弹层动效、弹层内部子状态、⌘K 与添加厂商浮层、设置页切换 |
| P2 展开收起 | 会话转录中的 4 类折叠块（含 StreamMark 锚点确认）、会话列表、文件树 |
| P3 输入框 | 单行 ↔ 多行变形、自动增高、空状态 → 停靠的过渡 |
| P4 面板 | 侧栏、编辑区、文件树、最大化的宽度动画与降载 |
| P5 微交互与入场 | 按钮颜色过渡与按下反馈、滑动指示条、卡片 / 按钮 / 附件的出现消失、会话内容入场（需要 StreamMark API） |

## 8. 实现状态（2026-10-16）

代码在 `src/TKCode.Desktop/Motion/`，测试在 `tests/TKCode.Desktop.Tests/MotionTests.cs`、`PanelMotionTests.cs`。

| 件 | 文件 | 说明 |
|---|---|---|
| 帧时钟 / 动画值 | `MotionValue.cs` | `MotionValue`（弹簧或补间，反向时保留速度）；`MotionTicker` 按 TopLevel 合并 `RequestAnimationFrame`；手动时钟 `IsManual` / `Advance` / `Delay` 供测试精确推进 |
| 曲线与规范 | `MotionCurve.cs`、`MotionTokens.cs` | `SpringCurve(response, dampingRatio)`、`TweenCurve`、归一化 `SpringEase`（给 AtomUI 弹层与 Transitions 用） |
| 开关 | `MotionSettings.cs` | `reduceMotion`、macOS 系统设置（窗口激活时刷新）、测试的 `ForceDisabled` |
| 是否该播 | `MotionGate.cs` | 未加载、所在区域不可见、刚换 DataContext（虚拟化回收、切换会话）时直接到终态 |
| 展开收起 | `Reveal.cs` | 尺寸随进度变化，内容淡入 + 位移；`Orientation=Horizontal` 用于侧栏 |
| 出现消失 | `Presence.cs` | `Fade/Pop/Slide*/Zoom`，`AnimateExit=False` 用于同一 StackPanel 内互斥的分区，`PlayEnter` 代码触发 |
| 尺寸平滑 | `SmoothSize.cs` | 输入框增高（`Anchor=Bottom`）、弹层子状态、命令面板结果数 |
| 箭头旋转 | `Turn.cs` | 替代两个图标互换 |
| 列表入场 | `ItemsMotion.cs` | 只对加载之后的新增项播放；Clear 后紧跟的重建（搜索、换根目录）不播；每轮最多 24 项 |
| FLIP | `Morph.cs` | 输入框单行 ↔ 两段时工具栏部件滑到新位置 |
| 弹层 | `Popover.cs` | `atom:Popup` 子类 + `PopoverIn/OutMotion`，缩放原点随 Placement / 翻转 |
| 微交互过渡 | `MotionTransitions.cs` | 颜色、数值、圆角、变换过渡，动效关闭时立即切换 |

面板开合在 `MainWindow.UpdateColumns`：一个进度值同时插值编辑区与文件树列宽；动画期间编辑区 / 文件树内容与会话视图固定为终值宽度（降载），结束后各自只重排一次。侧栏动画期间同样冻结会话视图宽度。

补充实现（第二轮）：

| 件 | 位置 | 说明 |
|---|---|---|
| 会话内容入场 | `Motion/Entrance.cs`、`Controls/TranscriptAdapter.cs` | Adapter 在会话进行中追加条目时标记，模板根元素加载时播入场（用户消息上移、思考 / 活动组 / 底栏 / 等待指示）；初次挂载、加载更早历史、虚拟化再实现不播。流式正文由 StreamMark 自带渐显，不需要 StreamMark 新 API |
| 共享滑动指示条 | `Motion/IndicatorPanel.cs` | 命令面板高亮、设置页与厂商页的分段选择、模型选择器 Provider 页签（下划线模式）；子元素用 `IndicatorPanel.IsCurrent` 报告当前项，底板弹簧滑过去 |
| 文件树折叠退出 | `FileExplorerViewModel.CollapseAnimation` + `MainWindow.AnimateTreeCollapse` | 子节点收起高度并淡出后再移除；动画中途再次展开时恢复，不重复插入 |
| 空状态输入框 → 底部输入框 | `Morph.Fly` | 发送第一条消息时底部输入框从居中输入框的位置落到底部 |
| 执行中展开、完成后收起 | `ViewModels/TranscriptItems.cs`、`Controls/TranscriptAdapter.cs`、`Reveal.ContentTemplate` | 进行中的活动组只显示正在进行的步骤（`IsLive`：流式输出中的思考、执行中的工具），步骤完成后整行带动画收起、计入摘要，没有进行中的步骤时整组收起（`ShowSteps`）；点击摘要展开全部步骤。步骤行用 `Reveal` 包一层（`IsLive` 或组已展开时打开），内容用 `Reveal.ContentTemplate` 按需创建，收起播完才释放。进行中的思考 / 工具自身也自动展开（read 除外），完成后收起；用户手动展开 / 收起过的不再自动改变。一轮结束时沿用这一段最后一个活动组就地收成“已工作 …”（`Sync` 保留前后都有的条目，不删了重插；组内容不显示时 `Steps` 不同步，显示时才同步），组外并入的正文、单独思考直接消失 |

注意：
- 透明度绝不能过冲：渲染时按 0–255 换算，1.04 会溢出成接近 0。弹簧缓动只用于缩放 / 位移；AtomUI 弹层动效（`PopoverInMotion`）的透明度过渡单独换成不过冲的缓动。此前“打开下拉闪几下”就是这个原因（真实窗口逐帧取证：透明度 1.04→1.01 的几帧画面几乎全透明）。`PopoverPlacementTests` 断言透明度单调且 ≤ 1。
- AtomUI 弹层按触发按钮当时的变换后边界定位（打开后不随按钮回弹移动）：按下缩放会让弹层固定偏几个像素。所以弹层触发按钮（`TKCode.ChipButton`）不做按下缩放，启动页退场后主界面入场只淡入。
- 画刷过渡必须预乘 alpha（`MotionBrushTransition.Mix`）：`Transparent` 是透明的白色，直接插值会在悬停时闪一层浅灰。
