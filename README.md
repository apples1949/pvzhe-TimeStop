# 时停（TimeStop）Mod

在《植物大战僵尸杂交版》游戏内**「加速」按钮的正下方**新增「时停」两个字，
带圆角外边框。点击切换：

- **未触发** → 绿色
- **触发中** → 红色

效果 = **和游戏自带"自动暂停"一样冻结整个世界**（僵尸 / 子弹 / 植物 / 飘落阳光全部静止），
但**仍能种植植物、铲除植物、收集阳光金币**。

- Mod ID：`timestop`
- 版本：1.0.6
- 入口类：`TimeStopEntry`

---

## v1.0.11 修正（2026-09-28 晚七）—— 探针定位真因，改为「自行命中 + 直调」

### 探针数据（v1.0.10，决定性）
```
PROBE[种子包本体] TowerDefenseInGamePacketShow(...,Always) visible=True mf=Ignore rect=(2,59 96x60)
PROBE[道具类]     ShovelButton(TextureButton,Always)  visible=True mf=Stop rect=(400,0 70x72)
PROBE[道具类]     ShovelManager(ShovelManager,Always) visible=True mf=Stop rect=(400,0 70x80)
PROBE[鼠标] 屏幕位置=(1048.29, 132.37)
```
**控件状态全部正常**（`Always` / `visible` / `mf` 有效 / `rect` 非零），**但依然点不动**。

### ★★★ 真因：保活 ProcessMode 恢复不了 GUI 派发
Godot 的 GUI 派发链是 `Viewport::_gui_input_event` 做「鼠标窗口坐标 → canvas 空间转换 → 命中」，
**这一整段在 `SceneTree.Paused` 时被门控**。保活只能恢复节点自己的
`_Process`/`_Input` 回调，**恢复不了 Viewport 的 GUI 派发**。
⇒ **v1.0.7 / v1.0.9 的"保活 `BankUILayer` 整枝（324 节点）"根本性错误，彻底放弃。**

### 两个"暂停冻结的防抖位"（必须清零）
| 类 | 字段 | 冻结原因 |
|---|---|---|
| `TowerDefenseInGamePacketShow` | `_pressDelayTimer` | 只在 `_PhysicsProcess`(L1383) 递减 |
| `ShovelManager` | `shovelPressedAwait` | debounce 用 `CreateTimer(0.1, processAlways:false)` ⇒ 暂停时 timer 不走 |

表现都是**"点过一次之后全废"**。

### v1.0.11 修法：唯一路径 —— 自行命中 + 直调 public 接口
`DriveUiPicks()`（relay `_PhysicsProcess` 最前）：
1. **每帧无条件**清零上述两个防抖位；
2. 鼠标左键按下（`GetFrame()` 去重）时：
   - 铲子：`ShovelButton` 命中 → `ShovelManager.ShovelButtonPressed()`（public）
   - 种子包：`TowerDefenseInGamePacketShow.button` 命中 → `.Pressed()`（public）
3. 命中判定：`control.GetGlobalRect().HasPoint(viewport.GetMousePosition())`
   （两者**同坐标系，直接比即可**；`CanvasLayer` 无 `GetCanvasTransform()`，不要手动转换）
4. **撤掉 `BankUILayer` 保活**（与直调二选一，否则双击切换 = 无变化）
5. 加 `PICK[N] 鼠标=… rect=… 命中=…` 诊断（前 20 次点击）

### 待实测
- ③ 种子包**能选中、能种下**；④ 铲子**能选中、能铲、再点一次能取消**。
- 若仍失败，日志 `PICK[N]` 会显示鼠标与 rect 的实际数值，可直接定位。

---

## v1.0.10 诊断版（2026-09-28 晚六）

### 症状
v1.0.9 装机后用户反馈：**「234都不行」**（收不了 / 选不了种子包种不下 / 选不了铲子），
并明确 **「可以考虑暂时不解决不能收」** ⇒ 优先级调整为 **③④（选卡 / 铲子）优先**。

### ★★★ 关键证伪：保活 `BankUILayer` 无效
v1.0.9 日志实证 `保活完成：共 325 个节点（… 顶部UI枝=324）` —— **保活确实生效**，
但用户实测**依然选不了**。v1.0.7 同样（保活 324 个，用户报"还是一样"）。
⇒ **保活到 324 个节点仍点不动 ⇒ 问题不在 ProcessMode。**

⇒ **停止盲改，改用运行时探针实测。**

### v1.0.10 内容：加探针（不改逻辑）
新增 `ProbePacketInteractivity()` / `ProbeChain()`，每轮开时停时逐层打印
种子包本体及 14 层祖先的：
- `visible`（`IsVisibleInTree()`）
- `mouseFilter`（`Stop` / `Pass` / `Ignore`）
- `globalRect`（位置 + 尺寸，判断是否飘走 / 为 0）
- `ProcessMode`

以及：
- `PROBE[鼠标] 屏幕位置=…`（对照 rect）
- `PROBE[道具类节点]`：按名含 `Prop` / `Tool` / `Shovel` / `Rake` 模糊搜索，
  解决 `DIAG[道具/铲子栏] 未找到 UITopPropContainer`（手机布局容器名不同）的问题。

### 下一步（等探针数据）
`PROBE[...]` 行会直接暴露真因：是 `visible=false`、`globalRect` 飘走、
`mouseFilter=Stop` 中间层遮挡，还是鼠标坐标不匹配。据此定点修。

---

## v1.0.9 修正（2026-09-28 晚五，「能停 / 不能收 / 无法选中和种下 / 无法选中」）

### 症状
v1.0.8 装机后用户反馈：**「1.能停 2.不能收 3.无法选中和种下 4.无法选中」**。

### ★★★ 决定性证据：种子包的真实宿主是 `CoexistHud`（运行时日志）
```
DIAG[种子包父链] / TowerDefenseInGamePacketShow / MobilePacketContainer / MobileISeedContanin
  / VBoxContainer / MobileSeedContainer / MobileItemContainer / MobileControl / Packet
  / TowerDefenseInGameSeedBank / @Control@100 / TowerDefenseCardScroll / CoexistHud
DIAG[道具/铲子栏] 未找到 UITopPropContainer。      ← 手机布局
保活完成：... 顶部UI枝=0                          ← v1.0.8 撤了保活
（无任何"旁路：命中"日志）                         ← 主动点击也没命中
```
源码 `TowerDefenseControlNew.cs` L198/L219 证实：
```csharp
uITopBankContainer = GetNode<HBoxContainer>("%UITopBankContainer");  // BankUILayer/UITopContainer/…
uITopBankContainer.AddChild(_coexistHud);    // ★ 种子包 HUD 挂在 CoexistHud 下
```
⇒ 种子包**确实**在 `BankUILayer` 下（经由 `CoexistHud`）。**静态 `.tscn` 里看不到**，
必须 dump 运行时父链才知道。

### v1.0.8 的两处误判
1. **撤掉 `BankUILayer` 保活 → 错**：不保活则按钮收不到 GUI 点击（暂停门控）。
2. **"旁路主动调 `Pressed()`" → 未命中**：`CoexistHud` 会动态改写种子栏
   `TopLevel`/`Position`（L303-306）⇒ 暂停态 `GetGlobalRect()` 失真，矩形命中失败。

### v1.0.9 修法：三条正交职责，互不冲突
| 职责 | 手段 |
|---|---|
| 按钮"收点击"（选中） | **恢复保活 `BankUILayer` 整枝**（唯一路径，让 Button 自己发 `Pressed`） |
| 落点处理 | relay 调 `mapFeature.ProcessInput()` |
| **撤销暂停副作用** | relay 每帧 `ClearPacketPressDelay()` ← **本版新增** |
| 收掉落物 | relay `CollectDroppablesAtMouse()` |

**`ClearPacketPressDelay()` 解决的是"点过一次后全废"**：
`TowerDefenseInGamePacketShow.Pressed()`（L1509）守卫 `!(pressDelayTimer > 0.0)`，
而该计时器**只在 `_PhysicsProcess`(L1383) 递减** ⇒ 时停时卡在 0.2。
清零**不是**"替游戏点按钮"，只是**撤销暂停导致的计时器冻结**，与保活职责不重叠。

**并删除了 v1.0.8 的 `DrivePacketAndToolClick()`**（避免"切换两次=没切换"）。

### 待实测
- ① 僵尸/植物/子弹**逻辑与动画都停**；② 阳光/金币**静止且能点收**；
- ③ 种子包**能选中、能种下**；④ 铲子**能选中、能铲、再点一次能取消**。
- 若②仍失败，日志里会有 `收集诊断[N] 鼠标=… 精灵=… 距离=… 半径=… 命中=…`，凭此定位。

---

## v1.0.8 修正（2026-09-28 晚四，「还是一样」—— 找到真正根因）

> ⚠️ **本版两处判断已被 v1.0.9 推翻**（撤保活、改用主动点击均错）。保留作过程留痕。

### 症状
v1.0.7 装机后用户反馈**「还是一样」**：4 条症状（收不了 / 选不了种子包 / 选不了铲子）全未改善。

### ★★★ 真正根因：前三版一直在错的地方使劲

**根因 A（决定性）：种子包"选中"完全不走 `mapFeature.ProcessInput()`。**
真实链路（解包实证）：
```
TowerDefenseInGamePacketShow.button (Button)   // L1356: button.Pressed += Pressed;
  → Godot GUI 输入派发 → Button.Pressed 信号
  → TowerDefenseInGamePacketShow.Pressed()     // L1499，public
  → OnPressed → PacketPickControl.PickPacket() // L1081 → packetPick = 该包
```
**暂停时 Godot GUI 派发被门控**（`_gui_input` 不调用）⇒ 只驱动 `ProcessInput()` **永远选不中卡**。

**根因 B（死锁）：`ProcessInput()` 首行就 `return`。**
```csharp
// TowerDefenseBattleFeatureMap.ProcessInput() L1242
bool flag3 = ...packetPickControl.NeedsInputProcessing();
if (!flag2 && !flag3) return;    // ← 未选中任何东西就早退
// PacketPickControl.NeedsInputProcessing() L591
if (!IsPicking() && !_wasPicking) return _toolActivateGrace > 0;   // 初始 0 ⇒ false
// 而 _toolActivateGrace 只在 ProcessReleaseInput()(L1269) 递减，
// 后者只在 ProcessInput() 内部被调 ⇒ 死锁
```

**根因 C：`pressDelayTimer` 卡死。**
`Pressed()` L1509 守卫 `!(pressDelayTimer > 0.0)`，而该计时器**只在 `_PhysicsProcess`(L1383) 递减**
⇒ 时停时卡在 0.2 ⇒ 第二次点击必被挡。

### v1.0.8 修法：旁路节点主动点按钮（唯一路径）
1. 新增 `DrivePacketAndToolClick()`，挂在 relay `_PhysicsProcess` **最前**（顺序：选卡 → 落点 → 收掉落物）：
   - 鼠标左键按下（边沿去重）时，用 `Control.GetGlobalRect().HasPoint(viewport.GetMousePosition())` 命中；
   - 种子包：命中 `TowerDefenseInGamePacketShow` → 先 `pressDelayTimer=0` 破根因 C → `InvokeMethod(p,"Pressed")`
     （其 `.button`/`.select`/`.alive`/`Pressed()` **全 public**；内部 `select = !select` 天然支持再点取消）；
   - 铲子：命中 `UITopPropContainer` 子项里的 `PacketPickTool` → `PickTool(!toolPick)`（全 public）。
2. **撤掉 v1.0.7 的 `BankUILayer` 整枝保活** —— 它与"主动点击"**双路径冲突**
   （切换类操作被触发两次 = 没切换，这正是"还是一样"的表象）。
3. 动画仍停 —— 顶部栏动画由 `UITopAnimationPlayer`（主控直属、不在保活集合内）驱动，不保活即停。

### 待实测
- ① 僵尸/植物/子弹**逻辑与动画都停**；② 阳光/金币**静止且能点收**；
- ③ 种子包**能选中、能种下**；④ 铲子**能选中、能铲、再点一次能取消**。

---

## v1.0.7 修正（2026-09-28 晚三，「动画停住了 / 停住但收不了 / 选不了种子包 / 选不了铲子」）

> ⚠️ **本版结论已被 v1.0.8 推翻**：用户实测「还是一样」。下面「保活 `BankUILayer` 整枝」的做法
> 被证明是错的（与主动点击双路径冲突、且 GUI 派发逐层检查靠 ProcessMode 不可靠）。
> **保留此段仅作过程留痕**，正确做法见上方 v1.0.8。

### 症状（用户逐条实测 v1.0.6）
> 1. 动画停住了 2. 停住 但收不了 3. 现在直接无法选择种子包了 4. 现在无法选择铲子

逐条对照：
| # | 结论 |
|---|---|
| 1 动画停住 | ✅ v1.0.6 的「收缩保活面 + 排除角色分支」生效，**保持** |
| 2 停住但收不了 | ❌ 两处 bug，本版修 |
| 3 选不了种子包 | ❌ v1.0.6 收缩保活面的副作用，本版修 |
| 4 选不了铲子 | ❌ 同 #3（铲子在 `UITopPropContainer`） |

### 两个真根因

**根因 1（→ 症状 2）：`TryCollectOne` 里多了游戏源码没有的 `over` 守卫。**
v1.0.6 写了 `if (GetBoolMember(node, "over")) return;`，但游戏 `TowerDefenseSunBase._Input` 的原文是：

```csharp
if (!isCollect && Geometry2D.IsPointInCircle(GetGlobalMousePosition(), _sprite.GlobalPosition, 40f*Scale.X))
    Collection();
```

**根本没有 `over` 判断** —— `over` 只表示"已落地停住"，而时停期间阳光恰好全部处于落地停住状态 ⇒ 被这个多出来的守卫一票否决，**永远收不到**。

**根因 2（→ 症状 2）：鼠标坐标空间不一致。**
v1.0.6 用 `charNode.GetViewport().GetMousePosition()`（**视口坐标**），拿去和 `sprite.GlobalPosition`（**世界坐标**）做 `IsPointInCircle`。而 `CharacterLayer` 是 `CanvasLayer` 且 `follow_viewport_enabled=true`，相机偏移下两者差一个 canvas transform ⇒ 判不中。
✅ 改用 `(charNode as Node2D).GetGlobalMousePosition()` —— 它正是游戏源码用的同一 API，自带 canvas transform 逆变换。

**根因 3（→ 症状 3、4）：`BankUILayer` 整枝仍受暂停门控。**
v1.0.6 为了止血"动画还在播"，把保活面收缩到只剩 `TowerDefenseMapControl` 一个节点，`BankUILayer`（种子包/铲子/道具所在）随之失去 `Always` ⇒ 其 `_GuiInput`/`_Input` 在暂停时**不被调用**，点击被吞。

### v1.0.7 修法
1. `TryCollectOne`：**删除** `over` 守卫（回归游戏源码语义）。
2. `CollectDroppablesAtMouse`：鼠标坐标改 `Node2D.GetGlobalMousePosition()`。
3. `ApplyKeepAlive` **新增精准保活 `BankUILayer` 整枝**（`KeepAliveTarget(bankLayer, true)`）。
   - ✅ 安全性已用 awk 验证 `TowerDefenseControlNew.tscn`：`BankUILayer` 子树只有
     `HFlowContainer` / `HBoxContainer` / `Control`，**不含任何 Animation/AnimatedSprite 节点**
     ⇒ 保活它不会让动画继续播（这正是 v1.0.5 翻车的原因，本版避开）。
   - ✅ 它是 `CharacterLayer` 的**兄弟分支**，不在 `CharacterNode` 父链上 ⇒ 不破坏冻结。
   - 保活面：**1 个地图控件 + 顶部 UI 枝（约 6~10 个容器）**，仍远小于 v1.0.5 的 570 个。

### 待实测
- 时停后：僵尸/植物/子弹**逻辑与动画都停**（应仍 ✓）；
- 阳光/金币**静止在空中**且**能点收**；
- 种子包**能选中、能种下**；铲子**能选中、能铲、再点一次能取消**。

---

## v1.0.6 修正（2026-09-28 晚二，「停机了但动画在播 / 收不了 / 种不下」）

### 症状（用户逐条实测 v1.0.5）
1. 植物僵尸停止 **但动画还在播放**；子弹成功暂停
2. **无法收集**阳光，会**按惯性继续往下掉落**
3. **能选**种子包，**但种不下去**
4. 铲子**能选取但铲除不了**，也**无法再点铲子位置取消选择**
5. 能取消时停 ✓

### 三个真根因（解包实证）

**根因 A（致命 bug）：`IsDroppable` 把植物/僵尸当成了掉落物。**
日志实证：`掉落物=95`（且越掉越多，最后 110）；而 `DIAG[阳光节点扫描] 找到 0 个`。
解包真相：
```csharp
public partial class TowerDefenseGroundItemBase : Node2D { }        // Prefab/TowerDefense/Base/
public partial class TowerDefenseCharacter : TowerDefenseGroundItemBase { }  // ★ 植物/僵尸！
public partial class TowerDefenseCoinBase  : TowerDefenseGroundItemBase { }  // 金币
```
旧判据 `IsSubclassNamed(t,"TowerDefenseGroundItemBase")` ⇒ **95 个植物/僵尸被设成 `Always`**
⇒ 角色逻辑解冻。
**但为什么"停止"了却"动画还在播"？** 因为角色的移动/攻击走 `_PhysicsProcess`（被我改动的
父链/自身状态影响），而**动画走 `Tween`/`AnimationPlayer`（引擎级，不受节点 ProcessMode 门控）**
⇒ 出现"逻辑停、动画播"的诡异组合。

**根因 B：把掉落物设 `Always` 反而让它们继续下落。**
`TowerDefenseSunBase._PhysicsProcess`：
```csharp
else if (!over && (double)_sprite.Position.Y > height && _moveComponent.velocity.Y > 0f)
{ _sprite.Position = new Vector2(_sprite.Position.X, (float)height); over = true;
  _moveComponent.MoveClear(); SetPhysicsProcess(enable: false); }
```
它靠 `_PhysicsProcess` 判定"落地停住"。一旦设 `Always`，`_PhysicsProcess` 活了 ⇒
`MoveComponent` 驱动它**继续下落**（用户实测"按惯性继续往下掉落"）。
⇒ **掉落物必须保持冻结**（这才是"时停"该有的样子）。

**根因 C：泛撒网保活 570 个 UI 节点。**
v1.0.5 的 `KeepAliveRecursive` 把树里 570 个 UI 节点（含 `AnimationPlayer`/`AnimatedSprite`）
一并设成 `Always` ⇒ 动画继续播放、UI 状态机继续跑。
⇒ **保活面必须收敛到最小。**

### v1.0.6 修法

**① 保活面收缩到只有 `TowerDefenseMapControl` 一个节点。**
删除并停用 `KeepAliveRecursive`（已从代码中移除并留注释警示）。

**② 掉落物一律不保活**，由旁路节点**主动收集**：
新增 `CollectDroppablesAtMouse()`（在 relay 的 `_PhysicsProcess` 里每帧跑）：
- 鼠标左键按下时，遍历 `CharacterNode` 下的阳光/金币实例
- 做**与游戏源码逐字一致**的圆形命中判定：
  - 阳光：`IsPointInCircle(mouse, _sprite.GlobalPosition, 40f*Scale.X)`
  - 金币：`IsPointInCircle(mouse, spriteNode.GlobalPosition, 30f*Scale.X)`
  （两者都用 **sprite 节点**的 `GlobalPosition`，不是节点自身位置 ⇒ 反射取 `_sprite`/`spriteNode`）
- 命中就调 **public `Collection()`**（`TowerDefenseSunBase.Collection()` 与
  `TowerDefenseCoinBase.Collection()` 都是 public ✓）

**③ `IsDroppable` 修正**：先排除 `TowerDefenseCharacter`，只认 `TowerDefenseSunBase` +
`TowerDefenseCoinBase`。`MaintainDroppableKeepAlive` 改为**纯统计日志**（阳光=N，金币=M）。

**④ 种植/铲除**：仍由 relay 的 `DrivePlanting()` 直调 `mapFeature.ProcessInput()`
（v1.0.5 已实现；本次未改，待实测确认是否生效）。

### 待实测
- 时停后：僵尸/植物/子弹**逻辑与动画都停**；阳光/金币**静止在空中**且**能点收**；
  种子包**能种下**；铲子**能铲且能取消**。

---

## v1.0.5 修正（2026-09-28 晚，「还是除了时停外什么都动不了」）

### 症状
v1.0.4 装机后用户反馈：**「还是除了时停外什么都动不了」**（种植、铲除、收阳光全部失效）。

### 三个真实根因（全部来自解包源码实证）

**根因 1：`KeepAliveRecursive` 从禁区节点起步 → 整棵子树被剪断。**
v1.0.4 的调用是 `KeepAliveRecursive(control, 0)`，而 `control`（= `TowerDefenseControlNew`）
本身就在 `_protected` 里 ⇒ 函数第一行 `if (_protected.Contains(node)) return;` **直接返回**，
整棵 UI 子树（`BankUILayer` 的种子包、`GUITop` 按钮）**一个都没保活**。
日志实证：「保活完成：共 **3** 个节点」。
⇒ 修复：禁区节点**只跳过自身，仍要递归子节点**；只有角色分支才整枝剪断。

**根因 2：阳光/金币根本不在 `ObjectManager` 下！**
v1.0.4 保活 `ObjectManager` 无效。解包实证：
`TowerDefenseManager.cs:1282` `ObjectManager.PoolPop(id, GetCharacterNode())`、
`:3096` `PoolPop(poolKey, GetCharacterNode())` —— **掉落物直接挂在 `CharacterNode` 下**。
⇒ 修复：新增 `KeepAliveDroppables()`，**逐个保活 `CharacterNode` 直属子节点里的掉落物实例自己**
（`TowerDefenseSunBase` / `TowerDefenseGroundItemBase`），**绝不碰 `CharacterNode`**；
并用 `MaintainDroppableKeepAlive()` 每轮补保活新掉落的阳光（阳光持续掉落）。
`objectManager` 单例字段改名 `sunHost` 语义（日志 `阳光宿主=`）。

**根因 3（核心）：种植链路被暂停门控，且 v1.0.4 认错了入口。**
- ❌ v1.0.4 以为种植走 `TowerDefenseMapControl._PhysicsProcess()`。
  实际那条路的条件是 `!isGameRunning`，**游戏运行中根本不调**。
- ✅ 真·链路（解包实证）：
  `TowerDefenseControlNew._Process()`（受 `ShouldDispatch` 门控）
  → `GameRunningProcessing(delta)` ［`TowerDefenseControlNew.cs:1562`］
  → 遍历 `featureDictionary` 调 `feature.Process(delta)`
  → `TowerDefenseBattleFeatureMap.Process(delta)` ［:905］
  → **`ProcessInput()`** ［:1226，**public**］→ `packetPickControl.ProcessPacketPick(...)` 种植

### 修法：旁路驱动节点（挂在 `SceneTree.Root` 下，`ProcessMode = Always`）
自建 `InputRelayNode : Node`，**不在 `CharacterNode` 父链上，绝对安全**：
1. `_Input(InputEvent)` → 转调 `control.process.InputProcess(event)`：
   覆盖**收阳光 / 金币 / 工具点击**（`TowerDefenseSunBase._Input` 需要 InputEvent）。
   只转发鼠标/触摸事件，不转发键盘（避免干扰游戏快捷键）。
2. `_PhysicsProcess(delta)` → **每帧直调 `mapFeature.ProcessInput()`**：
   覆盖**种植 / 铲除 / 选卡落点**。
   `ProcessInput()` 只轮询 `GetViewport().GetMousePosition()`，**不看 delta、不看事件**，
   天然免疫暂停。
   ⚠️ 它内部用 `Engine.GetPhysicsFrames()` 做去重（`_lastInputPhysicsFrame`）——
   暂停时物理帧不推进 ⇒ 帧号恒同 ⇒ 只处理一次。
   **所以每帧先用反射把 `_lastInputPhysicsFrame` 置回 `ulong.MaxValue` 强制放行**
   （与游戏 `NotifyMapTransformChanged()` 同法）。
3. 只在时停期间挂载（`EnsureRelay`），`RestoreKeepAlive()` 里 `RemoveRelay()` 立即移除。

### 新增诊断
`DumpDiagnostics()`：dump `SceneTree.Root` 直属子节点、所有 `TowerDefenseSunBase` 的父链、
`TowerDefenseMapControl` 真实路径、`ObjectManager` 实例数（只 dump 一次）。

---

## v1.0.4 修正（2026-09-28，「能时停但不能种植/收阳光」）

### 症状
v1.0.3 装机后用户反馈：**「现在能解除时停了，但植物种不了，掉落的阳光也点不了」**。
根因：v1.0.2/1.0.3 把保活压到 **0 个节点**，世界冻得干净，但**交互也全废**。

### 当时的判断（部分正确）
`Paused = true` 时，`_input` / `_unhandled_input` / `_gui_input` / `_physics_process`
**全部不调用**（除非节点 `ProcessMode = Always`）。
- **阳光/金币**：`TowerDefenseSunBase._Input(InputEvent)` L346（判断正确，但宿主判断错误）
- **种植/铲除**：当时以为是 `TowerDefenseMapControl._PhysicsProcess()`（**判断错误**，见 v1.0.5）

### 修法（v1.0.4，后被 v1.0.5 取代）
保留"禁区"（`CharacterNode` 祖先链 + 根，绝不改），额外保活
`ObjectManager`（以为阳光在它下面，**错**）与 `TowerDefenseMapControl`（**入口也错**）。
实际效果：`保活完成：共 3 个节点（阳光宿主=2，种植控件=1）`，用户反馈"什么都动不了"。

---

## v1.0.3 修正（2026-09-28 下午，「能开不能关」）

### 症状
v1.0.2 装机后用户反馈：**「现在的确时停了，但恢复不了了」**。
真·当前日志（`logs/godot.log`）：
```
[TimeStop] 已找到定位基准（加速按钮 checkBox2X）：类型=CheckBox 全局位置=(973, 64) 尺寸=(102, 64)
[TimeStop] 已创建「时停」按钮，位置=(973, 134)      ← 位置正确（加速正下方）
[TimeStop] 保活完成：改动 0 个 UI 节点（禁区 4 个：CharacterNode 祖先链 + 根，绝不改）。
[TimeStop] 时停已开启：PhyFrame 冻结。保活节点 0 个。
[TimeStop] 时停已解除（Shutdown）。                 ← ★ 全程只有 Shutdown 时解除，没有"用户点击关闭"！
```
⇒ 冻结本身**完全正确**（禁区生效、保活 0 个），但**关闭点击从未被接收**。

### 根因
Godot 的 GUI 输入派发（`Viewport::_gui_input_event`）会检查 Control 的 `Node::can_process()`。
`Paused = true` 时，v1.0.2 的按钮继承自根（`Pausable`）⇒ **`_gui_input` 根本不会被调用**
⇒ 点击丢失 ⇒ **只能开、关不掉**。
（对比：`SceneTree.process_frame` 信号在暂停时**照样发** —— `SceneTree::process()` 无条件
emit `"process_frame"` —— 所以按钮能被建出来、`MaintainTimeStop` 也照跑，唯独交互被吞。）

### 修法（v1.0.3）
1. **给「时停」按钮自身（`PanelContainer pc` 与 `Label lb`）设 `ProcessMode = Always`**。
   只动这两个新建节点，**不碰根、不碰 `CharacterNode` 祖先链** ⇒ 不会重蹈 v1.0.0/1.0.1 的覆辙。
2. **加一层兜底点击通道 `PollButtonClick()`**：在 `OnProcessFrame` 里（`process_frame` 暂停时也发）
   直接轮询鼠标左键 + 按钮全局矩形做命中判定，永远有效。
   与 `OnButtonInput` 用 `_lastClickFrame`（`SceneTree.GetFrame()`，类型是 **`long`** 不是 `ulong`）
   去重，避免一次点击被两条通道各触发一次。

---

## 〇、v1.0.2 关键修正（2026-09-28）

### 症状
v1.0.1 装机后用户反馈**「还是完全没效果」**。日志（`PVZHE_Logs/godot_startup_*.log`）显示：

```
[TimeStop] 已创建「时停」按钮，位置=(147, 544)
[TimeStop] 时停已开启：PhyFrame 冻结。保活节点 719 个。
[TimeStop] 时停已解除（用户点击关闭）。
```

⇒ mod **已加载、按钮已建、点击已生效**（开/关日志都有），**纯粹是冻结无效**；
且「保活节点 719 个」异常巨大，说明保活动作改了大量非 UI 节点。

### 根因（逐行复核解包源码，非猜测）
游戏判定角色该不该跑，用的是 **沿父链向上解析 ProcessMode**：

```
TowerDefenseProcessModeDispatch.ShouldDispatchInherited(parent, treePaused)
  → ResolveEffectiveProcessModeCached(parent)
    → ResolveEffectiveProcessMode(node)：while 循环沿 GetParent() 往上走，
        遇到第一个 != Inherit 的 ProcessMode 就返回它
```

而角色批处理的 parent = `TowerDefenseManager.GetCharacterNode()`
= `TowerDefenseControlNew/CharacterLayer/CharacterNode`，解析链是：

```
CharacterNode(Inherit) → CharacterLayer(Inherit) → TowerDefenseControlNew
```

v1.0.1 的 `ApplyKeepAlive()` **从 `TowerDefenseControlNew` 开始递归**，把**根自己**
也设成了 `Always` → 父链解析一路往上落到根 = `Always` → `ShouldDispatchMode` 返回 true
→ **角色继续跑，时停彻底失效**。

⚠️ v1.0.1 加的 `IsCharacterBranch()`（跳过 `CharacterLayer` 子树）**完全无效**：
父链解析会"绕过"被跳过的子树继续往上，正好落到被改过的根。

### 修法（v1.0.2）
1. **先算"禁区"**：从 `CharacterNode` 一路向上到 `SceneTree.Root` 的所有祖先
   （**根必然在其中**）+ `SceneTree.Root` 链，全部加入 `_protected`，递归时一律跳过。
2. 只把**纯 UI** 节点设 `Always`，且必须不在禁区、不属角色分支。
3. 关键前提：Godot 的 `_Input`/`_UnhandledInput`/`_GuiInput` **不受 `Paused` 影响**
   （只有 `_Process`/`_PhysicsProcess` 被暂停）→ 种植/铲除/收集这些**输入型**操作
   即便不保活根节点也照常工作。

### 按钮定位修正
`CheckBox2X`（加速）在 tscn 里是 `visible = false`（进关卡后才显形），
v1.0.1 要求 `IsVisibleInTree()` 才采用 → 早期扫描误判缺失、回落到齿轮（左下 4,544）。
v1.0.2 改为**只要求节点有效**，可见性交给 `EnsureButton` 的 `Size.Y` 检查兜底。

---

## 一、技术原理（全部来自解包源码实测）

### 1. 游戏怎么暂停
`DialogBoxBase._Ready()`（`Core/DialogManager/Base/DialogBoxBase.cs` L57-66）：

```csharp
saveTimeScale = Engine.TimeScale;
Engine.TimeScale = 1.0;
if (pasue)
{
    savePauseState = GetTree().Paused;
    base.ProcessMode = ProcessModeEnum.Always;
    GetTree().Paused = true;     // ← 暂停的真正机制
}
```

`CloseDialog()`（L111-120）恢复：`Engine.TimeScale = saveTimeScale; GetTree().Paused = savePauseState;`

⇒ **暂停靠 `GetTree().Paused`，不是 TimeScale**。

### 2. 暂停时谁还活着
`TowerDefenseProcessModeDispatch.ShouldDispatchMode()`（L143-158）：

```csharp
if (!treePaused) return mode != WhenPaused;
if (mode != Always) return mode == WhenPaused;
return true;
```

⇒ **`ProcessMode = Always` 的节点在暂停时照常处理**。这就是"保活"的钥匙。

### 3. ⚠️ 为什么不能用 `Engine.TimeScale = 0` 做时停
`DialogBoxBase._Ready()` 会把它**强制设成 1.0**，`CloseDialog()` 再写回。
只要玩家打开/关闭任何对话框，时停就会被覆盖。**必须用 `Paused`。**

### 4. 种植 / 铲除的输入入口
`TowerDefenseControlNew._Input()`（L1071）里 `process.InputProcess(event_)`——
鼠标落点判定从这里进。所以时停时必须保活 `TowerDefenseControlNew` 及其 UI 子树。

### 5. 齿轮按钮怎么定位
`TowerDefenseControl.optionButton`（`Scene/TowerDefesne/TowerDefenseControl.cs` L13、L42-46），
`OptionButtonPressed()` 就是它的回调。
⚠️ **它是 `SpriteBrightButton`，节点名未在解包中确定**，所以本 Mod 用**反射读
`TowerDefenseControl` 实例的 public 字段 `optionButton`** 来定位，不硬编码节点名。

---

## 二、实现要点

| 动作 | 做法 |
|---|---|
| 建「时停」按钮 | 在齿轮的**父节点**下建 `PanelContainer`（画边框）+ 内嵌 `Label`；位置贴齿轮右侧 `+8px` |
| 边框 | `StyleBoxFlat`：圆角 6、描边 2px、半透明黑底 |
| 颜色 | 文字 + 边框同时变色：绿 `#4DFF59` / 红 `#FF5147` |
| 防穿透 | `GuiInput` 里 `SetInputAsHandled()`，避免点按钮时顺手种下一棵植物 |
| 开时停 | `ApplyKeepAlive()`（把 `TowerDefenseControlNew` 子树设 Always）→ `_tree.Paused = true` |
| 关时停 | 逐个还原 `ProcessMode` → `_tree.Paused = _savedTreePaused` |
| 维持 | 每 6 帧校正一次 `Paused = true`（防游戏因失焦等自行改动） |
| 退出保护 | `Shutdown()` 里强制 `Disengage()`，避免卸载 Mod 时把游戏卡死 |

---

## 三、编译 / 打包 / 装机

```bash
# 编译（★必须加 -p:UseSharedCompilation=false，否则本机会卡死）
export DOTNET_ROOT="C:/Users/txgcs/WorkBuddy/zjb/tools/dotnet9"
export DOTNET_CLI_HOME="C:/Users/txgcs/WorkBuddy/zjb/tools/dotnethome"
cd runtime_src
"$DOTNET_ROOT/dotnet.exe" build TimeStop.csproj -c Release -p:UseSharedCompilation=false -v:minimal

# 若构建进程卡在收尾不退出（产物其实已生成）：
cp obj/Release/JTYTimeStop.dll bin/Release/JTYTimeStop.dll

# 打包
python build_pmod.py     # 产物 -> mod/dist/TimeStop.pmod

# 装机
cp mod/dist/TimeStop.pmod "%APPDATA%\Godot\app_userdata\植物大战僵尸杂交版\Mods\"
# 并确保 enabled_mods.json 含 "timestop"
# 最后让 ModsCache/TimeStop 改名失效（游戏下次启动重新解包）
```

---

## 四、已知风险 / 待实测项

1. **保活范围可能不全**：目前是"把 `TowerDefenseControlNew` 整棵子树设为 Always"的粗粒度做法。
   若实测发现种植能点但落点不判定、或阳光点不动，需要**按实测缩小/扩大保活范围**。
2. **`_PhysicsProcess` 副作用**：`TowerDefenseControlNew._PhysicsProcess()` 会累加
   `runGameTime`（游戏内计时）并检查失焦自动暂停。被设为 Always 后，时停期间它仍会跑。
   若发现游戏内计时异常，需单独处理。
3. **与游戏暂停按钮冲突**：时停期间若玩家点了游戏的暂停按钮，`Paused` 状态会互相干扰；
   已加"每轮校正"缓解，但**极端情况仍可能异常**。
4. **多人模式未适配**：本 Mod 未考虑多人联机（联机下 `Paused` 语义不同）。

---

## 五、诊断日志

`TimeStopEntry.EnableInfoLog = true`（默认开）。确认稳定后可改为 `false` 静默。

关注日志前缀 `[TimeStop] `：
- `已找到齿轮按钮：类型=.. 节点名=.. 位置=..` ← 定位成功
- `已创建「时停」按钮，位置=..`
- `时停已开启：PhyFrame 冻结。保活节点 N 个。` ← N 就是保活规模
- `未找到 TowerDefenseControlNew，种植/收集可能无法在时停时操作。` ← 需排查


---

## v1.0.1（2026-09-28）修复时停失效 + 按钮移到加速下方

### 用户反馈
1. 「时停没效果 植物会继续倒计时 僵尸还是会动」
2. 「按钮还是改到加速下方吧」

### 根因（源码复核后确认，是我上一版的实现错误）
角色的逻辑/动画由四个**批处理节点**驱动：
`TowerDefenseZombieBatch` / `TowerDefenseCharacterBatch` /
`TowerDefenseCharacterMotionBatch` / `TowerDefenseShieldImpactBatch`。

它们在 `_Ready()` 里**硬编码 `base.ProcessMode = ProcessModeEnum.Always`** ——
游戏的本意是让角色在暂停时仍能播完「受击闪白 / 死亡消散」这类收尾动画。

关键在 `TowerDefenseCharacterBatch._Process()`（L127-144）与
`TowerDefenseZombieBatch._PhysicsProcess()`（L180+）：

```csharp
bool treePaused = GetTree()?.Paused ?? false;
...
if (!(character.BatchUsesInheritedProcessMode
      ? TowerDefenseProcessModeDispatch.ShouldDispatchInherited(character.BatchProcessModeParent, treePaused)
      : TowerDefenseProcessModeDispatch.ShouldDispatch(character, treePaused)))
{
    num2++; continue;                  // ← 按 Paused 跳过该角色
}
character.BatchProcessUpdate(delta);
```

⇒ **批处理器自己会读 `Paused` 并跳过角色**，所以 `Paused = true` 本该能冻住角色。

**但 `ShouldDispatch()` 会沿父链解析 ProcessMode**：
`ResolveEffectiveProcessMode()` 从节点往上找第一个非 `Inherit` 的 ProcessMode。
v1.0.0 的 `ApplyKeepAlive()` 把 `TowerDefenseControlNew` **整棵子树**（含
`CharacterLayer/CharacterNode`…）都设成了 `Always` ⇒ 角色所在分支解析出 `Always`
⇒ `ShouldDispatchMode(Always, treePaused=true)` 返回 **true** ⇒ **角色继续跑，时停失效**。

**结论：保活范围过大反噬。我自己把冻结搞坏了。**

### 修复
1. **保活改为精确白名单 + 硬跳过角色分支**：`IsCharacterBranch()` 判定，
   节点名 == `CharacterLayer`/`CharacterNode`/`ZombieCheckArea`/`CharacterCanvasModulate`，
   或类名含 `Batch`，或类名含 `Character`（且不含 `Control`）⇒
   **整棵子树一律不碰**（连子孙都跳过）。这样 `Paused = true` 才能真正冻住角色。
2. **按钮位置**：基准从「齿轮 `optionButton`」改为「**加速按钮 `checkBox2X`**」，
   放在它**正下方**（竖直 +6px 间隙，宽度与加速对齐）。
   `optionButton` 保留为回退基准。
3. 新增 `ResyncPosition()`：每轮校正按钮位置（基准按钮进关卡后才完成 layout，
   且分辨率/UI 缩放变化时要跟随）。
4. 基准按钮尺寸未就绪（`Size.Y < 1`）时**延后创建**，避免位置算错。

### 产物
DLL 13824 字节 / md5 `e5a69c12b103816fd4be78da1561fc68`；
包 7407 字节，已装机 + ModsCache 失效。

### 仍待实测
- 保活后 `GUITop`（加速/齿轮所在层）在暂停时能否正常响应点击
- 阳光手动点击收集（若阳光对象在 `CharacterLayer` 内会被冻结；
  用户主要用游戏自带「自动收集」，影响可能不大）
