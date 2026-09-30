using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// 「时停」Mod 的托管运行时入口。
///
/// ── 需求（2026-09-28 用户）─────────────────────────────────────
/// 在游戏内**左下角齿轮按钮（optionButton，即 CD 开关入口）的右侧**新增「时停」两个字，
/// 带外边框。点击切换：未触发=绿色，触发中=红色。
///
/// 效果 = **和游戏自带"自动暂停"一样冻结整个世界**（僵尸/子弹/植物/飘落阳光全部静止），
///        但**仍能种植、铲除植物、收集阳光金币**。
///
/// ── 游戏机制（全部来自解包源码，不是猜的）──────────────────────
/// 1. **暂停**：游戏用 `GetTree().Paused = true` 实现（`DialogBoxBase._Ready()` L61-66，
///    `pasue=true` 时；`BattleOption.tscn` 的 pasue 就是 true）。
/// 2. **暂停时保活**：靠节点 `ProcessMode = Always`，判定在
///    `TowerDefenseProcessModeDispatch.ShouldDispatchMode()` L143-158：
///    `if (treePaused) return mode == Always || mode == WhenPaused;`
/// 3. **种植/铲除入口**：`TowerDefenseControlNew._Input()` L1071 里
///    `process.InputProcess(event_)` —— 鼠标落点判定都从这里进。
/// 4. **齿轮按钮**：`TowerDefenseControl.optionButton`（`OptionButtonPressed()` L94）。
///    ⚠️ **它是 `SpriteBrightButton`，节点名未知**，靠"找 TowerDefenseControl 的
///    optionButton 字段 + 取它的 NodePath/GlobalPosition"来定位，不能硬编码名字。
/// 5. ⚠️ **对话框会改 Engine.TimeScale**：`DialogBoxBase._Ready()` 把它设成 1.0、
///    `CloseDialog()` 再写回 saveTimeScale。所以**不能用 TimeScale 做时停**（会被对话框覆盖），
///    必须用 `GetTree().Paused`。
///
/// ── 铁律（技能文档 §1）─────────────────────────────────────────
/// Initialize / OnAllModsLoaded / Shutdown **一律不许抛**：
/// 抛出去 → 整包无条件回滚（不受 runtimeAssemblyPolicy 保护）。三个回调全部 try/catch。
/// </summary>
public sealed class TimeStopEntry : IXWModRuntimeEntry
{
	private const string LogPrefix = "[TimeStop] ";

	/// <summary>自建「时停」按钮的节点名（幂等判断用）。</summary>
	private const string ButtonName = "ModTimeStopButton";

	/// <summary>
	/// 诊断日志总开关。false 时 Info 全部静默（Warn 不受影响）。
	/// ★ v1.0.12（2026-09-28）：按用户要求**关闭日志输出**（此前 v1.0.8~v1.0.11 的
	/// `DIAG`/`PROBE`/`PICK`/`CLICK` 诊断已完成使命，定位到真因：暂停时 GUI 派发被门控）。
	/// 需要再排查时把它改回 true 即可，所有诊断代码都保留。
	/// </summary>
	private static readonly bool EnableInfoLog = false;

	/// <summary>★ v1.0.13 诊断开关：只输出"选卡 / 铲子 命中判定"相关的少量行
	/// （每次点击最多 3 行，不刷屏）。与 <see cref="EnableInfoLog"/> 相互独立，
	/// 便于在日志总开关关闭的情况下精确排障。验证通过后置 false。</summary>
	private static readonly bool EnablePickDiag = false;

	/// <summary>
	/// ★ v1.0.25 起的 **UI 形态内部开关**（v1.0.27 用户改回按钮）。
	///   · `false` ⇒ 勾选框样式（`CheckBox`，与游戏「加速」同款）；
	///   · `true`（**当前默认**）⇒ **自绘圆角按钮**（未触发绿字 / 触发中红字）。
	///
	/// ── 为什么最终选按钮 ────────────────────────────────────────────
	/// 勾选框形态实测有"长按/连点抖动"：`CheckBox` 自身的 GUI 派发**与**
	/// 我给自绘按钮做的兜底轮询（`PollButtonClick`）会**各触发一次**切换
	/// （v1.0.26 已用 `if (!UseButtonStyle) return;` 关掉轮询来解，但按钮形态本就
	/// 只有一条路径，最省心）。功能上两者完全等价。
	/// 改这个值需要重新编译打包。
	/// </summary>
	private static readonly bool UseButtonStyle = true;

	/// <summary>扫描节流：每 N 帧扫一次全树（找齿轮按钮 + 维持时停状态）。</summary>
	private const int ScanStride = 6;

	// ---- 颜色 ----
	private static readonly Color GreenColor = new Color(0.30f, 1f, 0.35f, 1f);   // 未触发
	private static readonly Color RedColor = new Color(1f, 0.32f, 0.28f, 1f);     // 触发中
	private static readonly Color BorderGreen = new Color(0.25f, 0.85f, 0.30f, 1f);
	private static readonly Color BorderRed = new Color(0.90f, 0.25f, 0.22f, 1f);

	// ---- 运行状态 ----
	private XWModRuntimeContext _context;
	private SceneTree _tree;
	private Callable _tickCallable;
	private bool _connected;

	/// <summary>★ v1.0.14：`physics_frame` 信号的回调。
	///
	/// ── 为什么不再依赖 relay 节点 ─────────────────────────────────
	/// Mod 程序集是**手写 csproj**（没有 `Godot.NET.Sdk` 的源码生成器）⇒
	/// 自定义 `Node` 子类的 `_Input` / `_PhysicsProcess` / `_Process`
	/// **引擎根本不会调用**（Godot 4 的 C# 脚本必须由源码生成器注册虚方法表；
	/// 没有生成器时这些方法对引擎"不存在"）。
	/// 而 `Callable.From(Action)` + `SceneTree.Connect("physics_frame", …)`
	/// 走的是**信号**通道，不需要生成器，且**暂停时照样发**
	/// （`SceneTree::physics_process()` 先 `emit_signal("physics_frame")`，
	/// 之后才做受 `paused` 门控的 `_process(true)`）。
	/// ⇒ v1.0.14 起，全部每帧驱动逻辑改由本回调承担；relay 只作兼容保留。
	/// </summary>
	private Callable _physCallable;

	/// <summary>心跳诊断：确认物理帧回调真的在跑（只打一次）。</summary>
	private bool _physHeartbeatLogged;

	/// <summary>物理帧回调异常只报一次。</summary>
	private bool _physFaultReported;
	private bool _started;
	private long _frame;
	private bool _faultReported;

	/// <summary>时停是否处于激活状态（用户点开）。</summary>
	private bool _timeStopOn;

	/// <summary>自建的按钮 Label（找到齿轮后创建；齿轮重建则重新创建）。</summary>
	/// <summary>★ v1.0.25：类型放宽为 `Control` —— 按钮形态下是 `Label`，勾选框形态下是 `CheckBox`。</summary>
	private Control _button;

	/// <summary>承载按钮的 PanelContainer（画外边框用）。</summary>
	/// <summary>★ v1.0.25：类型放宽为 `Control` —— 按钮形态下是 `PanelContainer`，勾选框形态下是 `CheckBox`。</summary>
	private Control _buttonHost;

	/// <summary>把哪些节点改成了 Always —— 恢复时逐个还原。</summary>
	private readonly List<(Node node, Node.ProcessModeEnum oldMode)> _keptAlive
		= new List<(Node, Node.ProcessModeEnum)>();

	/// <summary>时停前 SceneTree.Paused 的原值（保护：时停期间用户又点了游戏暂停）。</summary>
	private bool _savedTreePaused;

	/// <summary>已报告过"找到齿轮"（避免刷屏）。</summary>
	private bool _gearFoundReported;

	// ================================================================ 入口三回调

	public void Initialize(XWModRuntimeContext context)
	{
		try
		{
			_context = context;
			_frame = 0;
			_faultReported = false;
			string root = (context == null) ? "<null>" : context.PackageRoot;
			Info("初始化完成；PackageRoot=" + root + "。将在「加速」按钮正下方创建「时停」开关。");
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(LogPrefix + "Initialize 异常（已吞）：" + ex.Message); } catch { }
		}
	}

	public void OnAllModsLoaded()
	{
		try
		{
			if (_started)
			{
				return;
			}
			_tree = Engine.GetMainLoop() as SceneTree;
			if (_tree == null)
			{
				Warn("拿不到 SceneTree，时停不会生效（游戏其余部分不受影响）。");
				return;
			}
			_tickCallable = Callable.From(new Action(OnProcessFrame));
			_tree.Connect("process_frame", _tickCallable);
			// ★ v1.0.14：物理帧信号（暂停时也发），替代 relay 的 `_PhysicsProcess`
			_physCallable = Callable.From(new Action(OnPhysicsFrame));
			_tree.Connect("physics_frame", _physCallable);
			_connected = true;
			_started = true;
			// ★ v1.0.30：窗口级输入中继 —— 时停期间把"点/拖/动"转发给角色的
			//   `MousePressComponent`（加农炮瞄准/开火走的就是它）。
			HookWindowInput();
			Info("已挂载 process_frame（每 " + ScanStride + " 帧扫一次）。");
		}
		catch (Exception ex)
		{
			Warn("OnAllModsLoaded 异常（已吞）：" + ex.Message);
		}
	}

	public void Shutdown()
	{
		try
		{
			// 退出前必须先解除时停，否则会把整个游戏卡住
			Disengage("Shutdown");
			UnhookWindowInput();
			if (_connected && _tree != null && GodotObject.IsInstanceValid(_tree))
			{
				_tree.Disconnect("process_frame", _tickCallable);
				_tree.Disconnect("physics_frame", _physCallable);
			}
		}
		catch (Exception ex)
		{
			Warn("Shutdown 异常（已吞）：" + ex.Message);
		}
		finally
		{
			_connected = false;
			_started = false;
		}
	}

	// ================================================================ 每帧

	/// <summary>★ v1.0.14：物理帧驱动（**替代** relay 节点的 `_PhysicsProcess`）。
	///
	/// 暂停期间照样被调用：`SceneTree::physics_process()` 先 `emit_signal("physics_frame")`，
	/// 之后才做受 `paused` 门控的 `_process(true)`；而 `Main::iteration()` 无条件调
	/// `physics_process()`。⇒ 这是"暂停期间仍能跑 Mod 逻辑"的**唯一可靠通道**。
	/// </summary>
	private void OnPhysicsFrame()
	{
		try
		{
			if (!_timeStopOn)
			{
				return;
			}
			if (!_physHeartbeatLogged)
			{
				_physHeartbeatLogged = true;
				PickDiag("PHYS 心跳：物理帧回调已生效（v1.0.14 信号通道）。");
			}
			DriveUiPicks();
			DrivePlanting();
			CollectDroppablesAtMouse();
			RefreshPacketCooldownUi();
		}
		catch (Exception ex)
		{
			if (!_physFaultReported)
			{
				_physFaultReported = true;
				PickDiag("PHYS 异常（只报一次）：" + ex.Message);
			}
		}
	}

	/// <summary>
	/// ★★ v1.0.19：**时停期间手动刷新卡片的运行时可用性**（修"无视 CD 直接种"）。
	///
	/// 为什么必须手动刷：`TowerDefenseInGamePacketShow` 的 `alive`
	/// （= `_cachedRuntimeAvailability && (_cachedUpgradePacket || !coldDownOpen)`，
	/// 见 `ApplyCachedRuntimeAvailability()`）由**每帧的 `_PhysicsProcess`** 驱动。
	/// 暂停时该回调不跑 ⇒ 种下后 `coldDownOpen` 虽然被置 true，但 `alive` **不再被重算**、
	/// 停在 true ⇒ **冷却中的卡仍能被选中并种下**（用户实测：
	/// "时停状态下可以无视 cd 消耗阳光直接种"）。
	/// ⇒ 这里每轮对**卡池 + 卡槽**里的卡调一次 public 的 `RefreshRuntimeState()`，
	///   让 CD / 可用性重新算准（不改变任何游戏规则，只是"把该刷的刷了"）。
	/// </summary>
	private void RefreshPacketCooldownUi()
	{
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return;
			}
			TowerDefenseBattleFeaturePacketBank feature = mgr.GetPacketBankFeature();
			if (feature == null || !GodotObject.IsInstanceValid(feature))
			{
				return;
			}
			// ★★ v1.0.20：**三个列表都要刷** ——
			//   ① `feature.packetBank.packetList`（`TowerDefenseInGamePacketBank.packetList`）：
			//      **玩家在卡池/卡槽里看到并操作的那批卡**。这是最关键的 ——
			//      `TowerDefenseBattleFeaturePacketBank` **自己也有一个 `packetList`**，
			//      两者不是同一份；v1.0.19 刷的是 feature 那份 ⇒ **完全没刷到真正在用的卡**
			//      ⇒ `alive` 不重算 ⇒ 冷却中的卡照样能被选中并种下。
			//   ② `feature.packetBank.seedBank.packetList`：开局卡槽。
			//   ③ `feature.packetList`：保险（可能有别的路径往里塞卡）。
			// ★★ v1.0.23：**不再猜容器，直接全树搜 `TowerDefenseInGamePacketShow`**。
			//   实测各条"正规路径"都不靠谱：
			//     `CD 刷新：packetList=0 seedBank=0 feature=0 packetContainer=1`
			//   —— 战斗中有 6+ 张卡，却只找到 1 个。卡的宿主容器随"卡池页签 / 卡槽 / 场景"
			//   而变（`TowerDefenseInGamePacketBank.packetContainer`、
			//   `TowerDefenseInGameSeedBank.packetContainer`、选卡界面各有各的），
			//   与其逐个试，不如**一次全树搜干净**（数量就几十个，开销可忽略）。
			int n = RefreshAllPackets();
			_diagCdTotal = n;
			if (EnablePickDiag && _cdDiag < 12)
			{
				_cdDiag++;
				PickDiag("CD 刷新（全树）：共 " + n + " 张卡");
			}
		}
		catch { }
	}

	/// <summary>对一组 `TowerDefenseInGamePacketShow` 逐个调 `RefreshRuntimeState()`，返回处理了几张。</summary>
	private static int RefreshPacketList(object listObj)
	{
		int n = 0;
		try
		{
			if (!(listObj is Godot.Collections.Array arr))
			{
				return 0;
			}
			foreach (Variant v in arr)
			{
				if (v.AsGodotObject() is TowerDefenseInGamePacketShow p && GodotObject.IsInstanceValid(p))
				{
					p.RefreshRuntimeState();
					n++;
				}
			}
		}
		catch { }
		return n;
	}

	/// <summary>
	/// ★★ v1.0.22：遍历 `packetContainer` 的**子节点**刷新 ——
	/// 实测 `packetList` 一直是空的，而 `TowerDefenseInGamePacketBank.packetContainer`
	/// （`public Control packetContainer`）才是**卡片 UI 的实际父节点**。
	/// </summary>
	private static int RefreshPacketContainer(object pkObj)
	{
		int n = 0;
		try
		{
			Node container = GetMember(pkObj, "packetContainer") as Node;
			if (container == null || !GodotObject.IsInstanceValid(container))
			{
				return 0;
			}
			foreach (Node child in container.GetChildren())
			{
				if (child is TowerDefenseInGamePacketShow p && GodotObject.IsInstanceValid(p))
				{
					p.RefreshRuntimeState();
					n++;
				}
			}
		}
		catch { }
		return n;
	}

	/// <summary>
	/// ★★ v1.0.23：**全树搜索所有 `TowerDefenseInGamePacketShow` 并逐个 `RefreshRuntimeState()`**。
	///
	/// 为什么不再走"正规路径"：实测
	///   · `packetList`（feature / packetBank / seedBank 三处）**全是 0**；
	///   · `packetBank.packetContainer` 只有 1 个（战斗中有 6+ 张卡）；
	///   · `packetBank.seedBank` 字段本身常常还是 null。
	/// 卡的宿主容器随"卡池页签 / 卡槽 / 选卡界面 / 场景切换"而变，逐个猜代价太高
	/// ⇒ 直接全树搜（`CollectByClassName`），一网打尽。
	/// </summary>
	private static int RefreshAllPackets()
	{
		int n = 0;
		try
		{
			SceneTree tree = Engine.GetMainLoop() as SceneTree;
			if (tree == null || !GodotObject.IsInstanceValid(tree) || tree.Root == null)
			{
				return 0;
			}
			var found = new List<Node>();
			CollectByClassName(tree.Root, 0, "TowerDefenseInGamePacketShow", found, 300);
			foreach (Node node in found)
			{
				if (node is TowerDefenseInGamePacketShow p && GodotObject.IsInstanceValid(p))
				{
					p.RefreshRuntimeState();
					n++;
				}
			}
		}
		catch { }
		return n;
	}

	/// <summary>CD 刷新诊断计数 / 上次刷到的卡数。</summary>
	private int _cdDiag;
	private int _diagCdTotal;

	private void OnProcessFrame()
	{
		try
		{
			_frame++;
			if (_frame % ScanStride != 0)
			{
				return;
			}
			if (_tree == null || !GodotObject.IsInstanceValid(_tree))
			{
				return;
			}
			Node root = _tree.Root;
			if (root == null || !GodotObject.IsInstanceValid(root))
			{
				return;
			}

			// 1) 保证按钮存在且贴在加速按钮正下方（每次进关卡会重建，所以每轮都查一次）
			EnsureButton(root);

			// 2) ★ 兜底点击：process_frame 信号在 Paused 时**照样发**（SceneTree::process()
			//    无条件 emit "process_frame"），所以这里直接轮询鼠标左键，作为 GUI 输入的
			//    后备通道 —— 万一某些 Godot 版本/节点状态下 `_gui_input` 被暂停吞掉，
			//    也能保证"点得开、点得关"。（每 ScanStride 帧查一次，开销可忽略）
			PollButtonClick();

			// 3) 维持时停状态（游戏自己可能改 Paused，例如失焦自动暂停；这里每轮校正）
			MaintainTimeStop();

			// 4) 时停期间：不断补充保活"新掉落的阳光/金币"（它们挂在 CharacterNode 下，
			//    掉落物自己的 _Input 必须 Always 才收得到点击）
			MaintainDroppableKeepAlive();
		}
		catch (Exception ex)
		{
			if (!_faultReported)
			{
				_faultReported = true;
				Warn("扫描异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	// ================================================================ 旁路驱动节点（v1.0.6 核心）

	/// <summary>
	/// 时停期间替游戏接管「种植 / 铲除 / 选卡 / 收阳光」。
	///
	/// ── v1.0.6 设计总纲（对 v1.0.5 的三处修正）────────────────────
	/// **① 掉落物「不保活」，改为「主动收集」。**
	///    v1.0.5 把掉落物设成 `Always` ⇒ 它的 `_Process`/`_PhysicsProcess` 也活了 ⇒
	///    `MoveComponent` 驱动它**继续下落**（用户实测"按惯性继续往下掉落"），
	///    且"时停"语义被破坏。**正确做法：掉落物保持冻结**（静止在空中），
	///    由本旁路节点每帧做"鼠标命中检测 → 调 `Collection()`"来收。
	///    `TowerDefenseSunBase.Collection()` / `TowerDefenseCoinBase.Collection()` **都是 public**。
	///
	/// **② 绝不泛撒网保活 UI。**
	///    v1.0.5 递归保活了 570 个 UI 节点 ⇒ 其中包含 `AnimationPlayer`/`AnimatedSprite` 等，
	///    导致「植物僵尸停止但**动画还在播放**」。**正确做法：只保活必需的最小集合**
	///    （`TowerDefenseMapControl` 一个），其余交给旁路驱动。
	///
	/// **③ `IsDroppable` 必须排除 `TowerDefenseCharacter`（v1.0.5 致命 bug）。**
	///    解包实证：`TowerDefenseCharacter : TowerDefenseGroundItemBase`
	///    ⇒ 旧判据 `IsSubclassNamed(t,"TowerDefenseGroundItemBase")` 把 **95 个植物/僵尸**
	///    当"掉落物"保活了 ⇒ 僵尸/植物逻辑解冻（用户实测"植物僵尸停止但动画在播"）。
	///    正确的掉落物族：`TowerDefenseSunBase`、`TowerDefenseCoinBase`（金币族）。
	///
	/// ── 为什么不能直接把 `TowerDefenseControlNew` 设成 Always ────
	/// 它是 `CharacterNode` 的**祖先**。`TowerDefenseProcessModeDispatch.ResolveEffectiveProcessMode()`
	/// 沿父链向上找第一个非 `Inherit` 的 ProcessMode —— 一旦它变成 `Always`，角色链解析出 `Always`
	/// ⇒ 僵尸/植物/子弹全部照跑 ⇒ **时停彻底失效**（v1.0.0/1.0.1 的坑）。
	///
	/// ── 旁路节点 ─────────────────────────────────────────────────
	/// `ProcessMode = Always`、挂 `SceneTree.Root` 下（不在 `CharacterNode` 父链上，绝对安全）：
	///   · `_PhysicsProcess(delta)` 每帧做三件事：
	///       (a) `mapFeature.ProcessInput()` —— 种植/铲除/工具（直调，见 `DrivePlanting`）
	///       (b) `CollectDroppablesAtMouse()` —— 主动收阳光/金币
	///       (c) 无。
	///   · `_Input(event)` 转发给 `control.process.InputProcess(event)` 作为兜底（工具/视图）。
	///
	/// 只在时停期间挂载，解除时立即移除，绝不影响正常游戏。
	/// </summary>
	private sealed class InputRelayNode : Node
	{
		/// <summary>回指 Mod 入口（注意：不能叫 Owner —— 会和 `Node.Owner` 冲突，
		/// 而且 `Node.Owner` 是场景序列化归属，乱设会让本节点被当作场景一部分）。</summary>
		public TimeStopEntry Entry;

		public override void _Input(InputEvent event_)
		{
			Entry?.RelayInput(event_);
		}

		public override void _PhysicsProcess(double delta)
		{
			// ★ v1.0.11 顺序：
			//   ① `DriveUiPicks()` —— 清零防抖位 + 命中判定 + 直调 public 接口（选卡/铲子）
			//   ② `DrivePlanting()` —— `mapFeature.ProcessInput()` 落点处理
			//   ③ `CollectDroppablesAtMouse()` —— 收阳光/金币
			Entry?.DriveUiPicks();
			Entry?.ProbeClickMoment();
			Entry?.DrivePlanting();
			Entry?.CollectDroppablesAtMouse();
		}
	}

	/// <summary>旁路输入转发节点实例（时停期间存在）。</summary>
	private InputRelayNode _relay;

	/// <summary>把暂停期间收到的输入转发给游戏的战斗输入处理链（覆盖收阳光/工具点击）。</summary>
	private void RelayInput(InputEvent event_)
	{
		try
		{
			if (event_ == null)
			{
				return;
			}
			// ★ v1.0.13：捕获"左键刚按下"的边沿。
			//   `_Input` 收到的事件天然是边沿事件（Pressed 只在按下那一刻为 true），
			//   不依赖 `Input.IsMouseButtonPressed` 的轮询语义 ⇒ 与轮询边沿构成双路冗余。
			if (event_ is InputEventMouseButton mb
				&& mb.ButtonIndex == MouseButton.Left
				&& mb.Pressed
				&& mb.Device >= 0)
			{
				_pendingClick = true;
				// ★ v1.0.13：在**输入事件当下**立刻驱动一次落点。
				//   `PacketPickControl` 判定"确认种植"用的是 `mapControl.IsConfirmInput()`
				//   → `Input.IsActionJustPressed("Press")`。该 API 在**物理帧**里比较
				//   `pressed_physics_frame == Engine.get_physics_frames()`，放到下一物理帧再调
				//   可能因帧号已推进而判 false（表现为"选中了但种不下去"）；
				//   而在 `_Input` 回调里，`Input` 状态刚被本事件更新 ⇒ 一定为 true。
				DrivePlanting();
			}
			// 只转发"战斗操作"相关事件：鼠标（含滚轮）与触摸。
			// 键盘事件不转发 —— 避免干扰游戏自身的快捷键（暂停键、加速键等）。
			bool relevant = event_ is InputEventMouse
				|| event_ is InputEventScreenTouch
				|| event_ is InputEventScreenDrag;
			if (!relevant)
			{
				return;
			}
			Node control = FindNodeByClassName(_tree.Root, "TowerDefenseControlNew")
				?? FindNodeByClassName(_tree.Root, "TowerDefenseControl");
			if (control == null)
			{
				return;
			}
			// 主路径：TowerDefenseControlNew._Input() 里原封不动做的事
			//   `if (process != null) process.InputProcess(event_);`
			object proc = GetMember(control, "process");
			if (proc is GodotObject go && GodotObject.IsInstanceValid(go))
			{
				// TowerDefenseBattleProcess.InputProcess(InputEvent) 是 public virtual
				InvokeMethod(go, "InputProcess", event_);
			}
		}
		catch (Exception ex)
		{
			if (!_relayFaultReported)
			{
				_relayFaultReported = true;
				Warn("输入转发异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	/// <summary>
	/// 每物理帧驱动"地图交互"（种植 / 铲除 / 选卡落点 / 推车）。
	///
	/// 直调 `TowerDefenseBattleFeatureMap.ProcessInput()`：
	///   · 它是 **public** 方法；
	///   · 内部**只**用 `GetMousePosition()` + `Engine.GetPhysicsFrames()`，**不看 delta、不看事件**；
	///   · 入口条件 `packetPickControl.NeedsInputProcessing()` 决定"是否正持有卡/工具"。
	///
	/// ⚠️ 去重坑：`ProcessInput()` 用 `Engine.GetPhysicsFrames()` 做帧去重（`_lastInputPhysicsFrame`）。
	/// 暂停时物理帧**冻结不推进** ⇒ 每帧的物理帧号相同 ⇒ 只在第一帧处理一次。
	/// 所以每帧先把该字段重置为 `ulong.MaxValue` 强制放行（与游戏 `NotifyMapTransformChanged()` 同法）。
	/// </summary>
	private void DrivePlanting()
	{
		try
		{
			// `TowerDefenseBattleFeatureMap` 不是 Node（是 Resource/GodotObject），
			// 只能通过 `TowerDefenseManager.GetMapFeature()` 拿。
			object mapFeature = TryGetMapFeatureViaManager();
			if (mapFeature == null)
			{
				return;
			}
			// ★ 重置去重缓存，强制本帧放行（暂停时物理帧号不推进）
			SetMember(mapFeature, "_lastInputPhysicsFrame", ulong.MaxValue);

			// ★ v1.0.13 诊断：把"落点"能否成功的前置条件逐项打出来（前 60 次）
			if (EnablePickDiag && _plantDiag < 60)
			{
				_plantDiag++;
				object ppc = GetMember(mapFeature, "packetPickControl");
				object picked = GetMember(ppc, "packetPick");
				object mc = GetMember(mapFeature, "mapControl");
				string pickedName = "null";
				if (picked is GodotObject pgo && GodotObject.IsInstanceValid(pgo))
				{
					pickedName = pgo.GetType().Name;
				}
				PickDiag("PLANT#" + _plantDiag
					+ " ppc=" + (ppc != null)
					+ " picked=" + pickedName
					+ " needs=" + GetBoolByMethod(ppc, "NeedsInputProcessing")
					+ " confirm=" + GetBoolByMethod(mc, "IsConfirmInput")
					+ " phys=" + Engine.GetPhysicsFrames()
					+ " paused=" + _tree.Paused);
			}

			InvokeMethod(mapFeature, "ProcessInput");
		}
		catch (Exception ex)
		{
			if (!_driveFaultReported)
			{
				_driveFaultReported = true;
				Warn("驱动种植异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	private bool _driveFaultReported;

	/// <summary>v1.0.13 落点诊断计数（只打前若干条，避免刷屏）。</summary>
	private int _plantDiag;


	/// <summary>
	/// 每物理帧做"主动收集"：鼠标左键按下时，遍历 `CharacterNode` 下的
	/// 阳光 / 金币实例，做与游戏完全一致的**圆形命中判定**，命中就调它的 public `Collection()`。
	///
	/// ── 为什么不用"把掉落物设 `Always` 让它自己收 `_Input`" ────────
	/// v1.0.5 试过：把掉落物设 `Always` ⇒ 它的 `_PhysicsProcess` 也活了 ⇒
	/// `MoveComponent` 驱动它**继续下落**（用户实测"按惯性继续往下掉落"），
	/// 而且一旦落到 ground 就会 `over=true` 停下甚至消失 —— 完全违背"时停"。
	/// ⇒ **掉落物必须保持冻结**（静止在空中），收集由本方法代劳。
	///
	/// ── 命中判据（与游戏源码逐字对齐）────────────────────────────
	/// 阳光 `TowerDefenseSunBase._Input`：
	///     `!isCollect &amp;&amp; IsPointInCircle(GetGlobalMousePosition(), _sprite.GlobalPosition, 40f*Scale.X)`
	/// 金币 `TowerDefenseCoinBase._Input`：
	///     `IsPointInCircle(GetGlobalMousePosition(), spriteNode.GlobalPosition, 30f*Scale.X)`
	/// 二者都用 **`_sprite`/`spriteNode` 的 GlobalPosition** 而**不是**节点自身位置
	/// （阳光的 `_sprite` 会有动画偏移）⇒ 必须反射取 sprite 节点。
	///
	/// 只在"鼠标左键按下"时才判定（与 `_Input` 的触发时机一致：点击即收，不悬停自动收）。
	/// </summary>
	private void CollectDroppablesAtMouse()
	{
		try
		{
			// ★★ v1.0.24 —— **回退 v1.0.22 的"边沿触发"**。
			//   用户确认："游戏设定本来就是鼠标滑过就会拾取"，**这不是 bug**。
			//   ⇒ 恢复成"按住左键期间持续判定"（按住并划过阳光即拾取），
			//     与游戏原生 `TowerDefenseSunBase._Input()` 的行为一致。
			//   （v1.0.22 我把它误判成缺陷改掉了，属于画蛇添足，特此还原。）
			if (!Input.IsMouseButtonPressed(MouseButton.Left))
			{
				return;
			}
			Node charNode = FindCharacterNode();
			if (charNode == null)
			{
				if (EnablePickDiag && _collectRunDiag < 3)
				{
					_collectRunDiag++;
					PickDiag("COLLECT 找不到 CharacterNode（掉落物收集无法进行）。");
				}
				return;
			}
			if (EnablePickDiag && _collectRunDiag < 3)
			{
				_collectRunDiag++;
				PickDiag("COLLECT 开始遍历 CharacterNode，子节点=" + charNode.GetChildCount() + "。");
			}
			// ★★ v1.0.19 修正坐标空间（实测凭据见下）：
			//   旧写法用 `charNode.GetGlobalMousePosition()` —— 那是 **`CharacterNode` 自身局部
			//   坐标系**下的鼠标（`get_global_transform_with_canvas().affine_inverse() * viewport_mouse`），
			//   而 `sprite.GlobalPosition` 是**世界坐标**；两者差一个 `CharacterNode` 的完整变换
			//   （含 `mapControl` 缩放与相机偏移）⇒ 距离恒偏大。
			//   实测日志：`鼠标=(421.45, 46.49) 精灵=(300, 125) 距离=144.6 半径=40 命中=False`
			//   （用户明明点在阳光上）；而偶尔对上时距离只有 8.7
			//   ⇒ **判定逻辑没错，是两边不同坐标系**。
			//   ⇒ 统一改用**窗口坐标**（= 玩家在屏幕上看到的那个位置），
			//     与 `sprite.GetGlobalTransformWithCanvas()` 变换后的点直接比。
			Viewport vp = charNode.GetViewport();
			Vector2 mouse = (vp != null) ? vp.GetMousePosition() : Vector2.Zero;
			int n = charNode.GetChildCount();
			for (int i = 0; i < n; i++)
			{
				Node child = charNode.GetChild(i);
				if (child == null || !GodotObject.IsInstanceValid(child))
				{
					continue;
				}
				try
				{
					TryCollectOne(child, mouse);
				}
				catch { }
			}
		}
		catch (Exception ex)
		{
			if (!_collectFaultReported)
			{
				_collectFaultReported = true;
				Warn("主动收集异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	private bool _collectFaultReported;

	/// <summary>v1.0.17 收集流程诊断计数（只打前几次）。</summary>
	private int _collectRunDiag;

	/// <summary>
	/// ★ v1.0.11 点击瞬态探针：仅在"鼠标左键按下"的那一帧打印
	/// 鼠标位置 + 种子包/铲子的 rect + Viewport 关键信息，
	/// 用来判断"用户点的位置"与"控件 rect"是否在同一坐标系。
	/// </summary>
	private void ProbeClickMoment()
	{
		try
		{
			if (!_timeStopOn || !Input.IsMouseButtonPressed(MouseButton.Left))
			{
				return;
			}
			long f = _tree.GetFrame();
			if (_lastProbeClickFrame == f)
			{
				return;
			}
			_lastProbeClickFrame = f;
			if (_probeClickCount++ > 25)
			{
				return;
			}
			Viewport vp = _tree.Root.GetViewport();
			Vector2 vpMouse = (vp != null) ? vp.GetMousePosition() : Vector2.Zero;
			string line = "CLICK[" + _probeClickCount + "] vpMouse=" + vpMouse;
			if (vp != null)
			{
				line += " vpSize=" + vp.GetVisibleRect().Size;
			}
			Info(line);

			var packets = new List<Node>();
			CollectByClassName(_tree.Root, 0, "TowerDefenseInGamePacketShow", packets, 4);
			int i = 0;
			foreach (Node p in packets)
			{
				Control c = p as Control;
				if (c == null)
				{
					continue;
				}
				Rect2 r = c.GetGlobalRect();
				bool hit = r.HasPoint(vpMouse);
				// 再试它的 button 子节点
				bool hitBtn = false;
				Rect2 br = default;
				var btns = new List<Node>();
				CollectByNameContains(p, 0, new string[] { "Button" }, btns, 1);
				if (btns.Count > 0 && btns[0] is Control bc)
				{
					br = bc.GetGlobalRect();
					hitBtn = br.HasPoint(vpMouse);
				}
				Info("CLICK[" + _probeClickCount + "] 种子包" + (i++) + " rect=" + r
					+ " 命中=" + hit + " | Button rect=" + br + " 命中=" + hitBtn);
			}
			// 铲子
			var shovels = new List<Node>();
			CollectByNameContains(_tree.Root, 0, new string[] { "ShovelButton" }, shovels, 1);
			foreach (Node s in shovels)
			{
				if (s is Control sc)
				{
					Rect2 sr = sc.GetGlobalRect();
					Info("CLICK[" + _probeClickCount + "] 铲子 rect=" + sr
						+ " 命中=" + sr.HasPoint(vpMouse));
				}
			}
		}
		catch { }
	}

	private long _lastProbeClickFrame = -1;
	private int _probeClickCount;

	/// <summary>
	/// ★★★ v1.0.11 核心：**命中判定 + 直调 public 接口**，统一处理「选种子包 / 选铲子」。
	///
	/// ── 为什么保活 324 个节点仍点不动（v1.0.9 探针实证）──────────
	/// 探针显示保活后所有相关节点都已是 `Always`、`visible=True`、`mf=Stop/Pass`：
	/// ```
	/// PROBE[种子包本体] TowerDefenseInGamePacketShow(...,Always) visible=True mf=Ignore rect=(2,59 96x60)
	/// PROBE[道具类]     ShovelButton(TextureButton,Always)      visible=True mf=Stop  rect=(400,0 70x72)
	/// PROBE[鼠标] 屏幕位置=(1048.3, 132.4)                ← ★ 鼠标 x=1048，控件 rect 都在 0~470
	/// ```
	/// ⇒ **控件状态全都正常，但控件 rect 与鼠标坐标"不在同一坐标系"**。
	///   原因：这些 Control 在 `BankUILayer`（**CanvasLayer**）下，而
	///   `Control.GetGlobalRect()` 对 CanvasLayer 子节点返回的是 **canvas 空间**坐标，
	///   **不含 CanvasLayer 自身的 transform**；鼠标位置是**窗口空间**。
	///   Godot 正常的 GUI 派发由 `Viewport` 负责做这套转换 + 命中，但**暂停时被门控**。
	///   ⇒ 保活 ProcessMode **无法**让 GUI 派发恢复 ⇒ v1.0.7/v1.0.9 的"保活整枝"必然失败。
	///
	/// ── 本方法的做法 ─────────────────────────────────────────────
	/// 自己做正确坐标转换（`CanvasLayer.GetCanvasTransform()` 的逆变换），
	/// 命中就**直调游戏的 public 接口**：
	///   · 种子包 → `TowerDefenseInGamePacketShow.Pressed()`（先清 `_pressDelayTimer`）
	///   · 铲子   → `ShovelManager.ShovelButtonPressed()`（先清 `shovelPressedAwait`）
	///
	/// ── 两个"暂停冻结的防抖开关"必须一并清零（同类坑）──────────
	/// 它们是**在暂停时不会归零的状态位**，不清就"点过一次后全废"：
	///   · `TowerDefenseInGamePacketShow._pressDelayTimer`（只在 `_PhysicsProcess` 递减）
	///   · `ShovelManager.shovelPressedAwait`（其 debounce 用 `CreateTimer(0.1, processAlways:false)`
	///     ⇒ **暂停时 timer 不走**，`OnPressDebounceTimeout` 永不触发 ⇒ 永久卡 true）
	/// </summary>
	private void DriveUiPicks()
	{
		try
		{
			if (!_timeStopOn)
			{
				return;
			}
			// ★★ v1.0.13 核心修复：**边沿触发**（只认"按下的那一瞬间"）
			//
			// v1.0.11 用的是 `Input.IsMouseButtonPressed(MouseButton.Left)` —— 这是**按住**语义：
			// 鼠标按住 0.2 秒 ≈ 12 帧，就会调 12 次 `TowerDefenseInGamePacketShow.Pressed()`；
			// 而 `Pressed()` 内部是 `select = !select`（**切换**）⇒ 翻转偶数次**回到原样**，
			// 表现就是"点了完全没反应"。更糟的是我还在每帧 `ClearDebounceStates()` 里
			// 把游戏自带的 `pressDelayTimer = 0.2` 防抖清零了，等于亲手拆掉唯一的安全网。
			// ⇒ 改为"按下瞬间处理一次"：`edge` 走本地轮询边沿，`fromRelay` 走 `_Input` 事件边沿
			//    （双路冗余：任一路活着就不断，且同帧只消费一次）。
			bool down = Input.IsMouseButtonPressed(MouseButton.Left);
			bool edge = down && !_prevMouseDown;
			_prevMouseDown = down;
			bool fromRelay = _pendingClick;
			_pendingClick = false;
			if (!edge && !fromRelay)
			{
				return;
			}
			long f = _tree.GetFrame();
			if (_lastPickClickFrame == f)
			{
				return;
			}
			_lastPickClickFrame = f;

			Viewport vp = _tree.Root.GetViewport();
			Vector2 vpMouse = (vp != null) ? vp.GetMousePosition() : Vector2.Zero;
			bool diag = EnablePickDiag && _pickClickDiag++ < 40;
			if (diag)
			{
				PickDiag("CLICK#" + _pickClickDiag + " 窗口鼠标=" + vpMouse
					+ " edge=" + edge + " relay=" + fromRelay);
			}

			// ── (1) 铲子：ShovelManager.ShovelButtonPressed() ────────
			Node shovelMgr = null;
			{
				var found = new List<Node>();
				CollectByNameContains(_tree.Root, 0, new string[] { "ShovelManager" }, found, 1);
				if (found.Count > 0)
				{
					shovelMgr = found[0];
				}
			}
			if (shovelMgr != null)
			{
				var btns = new List<Node>();
				CollectByNameContains(shovelMgr, 0, new string[] { "ShovelButton" }, btns, 1);
				if (btns.Count > 0 && btns[0] is Control sb)
				{
					bool hit = HitControlInCanvas(sb);
					if (diag)
					{
						PickDiag("  铲子 hit=" + hit + " rectWin=" + GetWindowRect(sb)
							+ " localMouse=" + sb.GetLocalMousePosition());
					}
					if (hit)
					{
						SetMember(shovelMgr, "shovelPressedAwait", false);
						// ★★ v1.0.16：必须**先切换按钮的按下状态**再调 `ShovelButtonPressed()`！
						//   正常路径是 `ShovelManager._Input()`：
						//       `shovelButton.ButtonPressed = !shovelButton.ButtonPressed;`
						//       `ShovelButtonPressed();`
						//   而 `ShovelButtonPressed()` 内部读的正是这个状态：
						//       `PickShovel(shovelButton.ButtonPressed)`
						//   ⇒ 直调时不切它，读到的就是 `false` ⇒ `PickShovel(false)` = **取消铲子**，
						//     表现就是"能种植物但点不了铲子"（v1.0.15 实测）。
						if (sb is BaseButton bb)
						{
							bb.ButtonPressed = !bb.ButtonPressed;
						}
						InvokeMethod(shovelMgr, "ShovelButtonPressed");
						PickDiag("  ★ 命中铲子 → ShovelButtonPressed() 已调用。");
						return;
					}
				}
			}

			// ── (2) 种子包：TowerDefenseInGamePacketShow.Pressed() ──
			var packets = new List<Node>();
			CollectByClassName(_tree.Root, 0, "TowerDefenseInGamePacketShow", packets, 24);
			foreach (Node p in packets)
			{
				try
				{
					// 命中判定优先用其 Button 子节点（玩家点的是按钮）
					Control target = null;
					var btns = new List<Node>();
					CollectByNameContains(p, 0, new string[] { "Button" }, btns, 1);
					if (btns.Count > 0 && btns[0] is Control bc)
					{
						target = bc;
					}
					if (target == null)
					{
						target = p as Control;
					}
					if (target == null)
					{
						continue;
					}
					bool hit = HitControlInCanvas(target);
					if (diag)
					{
						PickDiag("  种子包(" + p.Name + "/" + target.Name + ") hit=" + hit
							+ " rectWin=" + GetWindowRect(target)
							+ " localMouse=" + target.GetLocalMousePosition()
							+ " alive=" + GetBoolMember(p, "alive")
							+ " lock=" + GetBoolMember(p, "lock")
							+ " onlyDraw=" + GetBoolMember(p, "onlyDraw")
							+ " select=" + GetBoolMember(p, "select"));
					}
					if (!hit)
					{
						continue;
					}
					// 只清"本次触发"用得上的那一个防抖位（不再每帧清，保住游戏自带防抖）
					SetMember(p, "_pressDelayTimer", 0.0);
					SetMember(p, "pressDelayTimer", 0.0);
					bool before = GetBoolMember(p, "select");
					InvokeMethod(p, "Pressed");
					bool after = GetBoolMember(p, "select");
					PickDiag("  ★ 命中种子包(" + p.Name + ") → Pressed() select " + before + " → " + after);
					return;
				}
				catch { }
			}
		}
		catch (Exception ex)
		{
			if (!_pickFaultReported)
			{
				_pickFaultReported = true;
				Warn("旁路选卡/铲子异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	private long _lastPickClickFrame = -1;
	private bool _pickFaultReported;
	private int _pickClickDiag;

	/// <summary>v1.0.13 边沿检测：上一帧左键是否处于按下状态。</summary>
	private bool _prevMouseDown;

	/// <summary>v1.0.13：`_Input` 侧捕获到"左键刚按下"（与轮询边沿构成双路冗余）。</summary>
	private bool _pendingClick;

	/// <summary>选卡/铲子诊断输出（受 <see cref="EnablePickDiag"/> 门控；
	/// 用 GD.Print 直出，不被日志总开关吞掉）。</summary>
	private void PickDiag(string msg)
	{
		if (!EnablePickDiag)
		{
			return;
		}
		GD.Print("[TimeStop] " + msg);
	}

	/// <summary>每帧清零"被暂停冻结"的两个防抖状态位。</summary>
	private void ClearDebounceStates()
	{
		try
		{
			var found = new List<Node>();
			CollectByNameContains(_tree.Root, 0, new string[] { "ShovelManager" }, found, 1);
			if (found.Count > 0)
			{
				SetMember(found[0], "shovelPressedAwait", false);
			}
		}
		catch { }
	}

	/// <summary>
	/// ★ v1.0.13 命中判定（<b>已修正坐标系</b>）。
	///
	/// ── v1.0.11 为什么必然失败 ────────────────────────────────────
	/// 旧写法是 `ctl.GetGlobalRect().HasPoint(viewport.GetMousePosition())`，**两个量不同坐标系**：
	///   · `Control.GetGlobalRect()` = `Rect2(GetGlobalPosition(), Size)`，其
	///     `GetGlobalPosition() = get_global_transform().xform(Vector2.Zero)` ——
	///     **只含 Node2D / Control 祖先的 transform，不含 viewport 的 canvas transform**。
	///   · `Viewport.GetMousePosition()` = **窗口（视口）像素坐标**。
	///   种子包挂在 `BankUILayer`（**CanvasLayer**）下，`BankUILayer` 有自己的 scale/offset
	///   ⇒ 两者相差整个 canvas 变换（日志实证：控件 rect 卡在 0~100，鼠标却报 1196）。
	///   ⇒ 命中恒为 false ⇒ **永远不会触发选卡/铲子**。
	///
	/// ── 正确姿势（Godot 官方为此提供的 API）──────────────────────
	/// `Control.GetLocalMousePosition()` 的内部实现就是：
	///     `get_global_transform_with_canvas().affine_inverse().xform(viewport.get_mouse_position())`
	/// 它**已经把 canvas transform 与全部祖先 transform 一起算进去了**，与控件自身
	/// 的局部坐标（`GetRect()`）**天然同系** ⇒ 直接 `HasPoint` 即可，零手工换算。
	///
	/// ⚠️ 千万不要再改成 `CanvasLayer.GetCanvasTransform()` —— `CanvasLayer` **没有**这个方法
	///    （只有 `CanvasItem.GetCanvasTransform()`），写了会 CS1061 编译失败。
	/// </summary>
	private static bool HitControlInCanvas(Control ctl)
	{
		try
		{
			if (ctl == null || !GodotObject.IsInstanceValid(ctl))
			{
				return false;
			}
			if (!ctl.IsInsideTree() || !ctl.IsVisibleInTree())
			{
				return false;
			}
			// ★★ v1.0.15 修正：**不能**用 `ctl.GetRect()` 配 `GetLocalMousePosition()`！
			//
			// Godot 4 的 `Control::get_rect()` 实现是 `Rect2(get_position(), get_size())`
			// —— 它**含控件在父容器里的 position**；而 `Control::get_local_mouse_position()`
			// 是 `get_global_transform_with_canvas().affine_inverse() * viewport_mouse`，
			// 是**控件自身坐标系**（原点在控件左上角）。两者原点不同，直接 `HasPoint`
			// 会把每张卡都判成"没点中"。
			//
			// 实测证据（v1.0.14 日志）：
			//   `@Control@144` rectWin=(100,121) 94x60、localMouse=(50.72815, 50.991257)
			//   —— 局部坐标明明落在 94x60 内，hit 却是 False；
			//   因为 `GetRect().Position` 是它在 VFlowContainer 里的槽位偏移（y≈62），
			//   而鼠标局部 y=51 < 62 ⇒ 落在 rect 之外。
			// ⇒ 正确写法：`new Rect2(Vector2.Zero, ctl.Size)`。
			bool hit = new Rect2(Vector2.Zero, ctl.Size).HasPoint(ctl.GetLocalMousePosition());
			if (!hit)
			{
				return false;
			}
			return true;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>取控件在**窗口坐标**下的矩形（用于诊断打印，方便与鼠标位置直接比对）。</summary>
	private static Rect2 GetWindowRect(Control ctl)
	{
		try
		{
			if (ctl == null || !GodotObject.IsInstanceValid(ctl) || !ctl.IsInsideTree())
			{
				return default(Rect2);
			}
			Transform2D xf = ctl.GetGlobalTransformWithCanvas();
			// Godot 4 的 C# 绑定没有 `Transform2D.Xform()`（那是 Godot 3 的名字），
			// 用 `Transform2D * Vector2` 运算符（等价，编译器直接内联）。
			Vector2 p0 = xf * Vector2.Zero;
			Vector2 p1 = xf * ctl.Size;
			return new Rect2(p0, p1 - p0).Abs();
		}
		catch
		{
			return default(Rect2);
		}
	}

	/// <summary>整体命中自检：Godot GUI 派发语义 = 取"最深的可点控件"。
	/// 本方法在候选集合里挑出命中的那个（按树深度最大者优先）。</summary>
	private static Control PickDeepestHit(List<Control> candidates)
	{
		Control best = null;
		int bestDepth = -1;
		foreach (Control c in candidates)
		{
			if (c == null || !GodotObject.IsInstanceValid(c))
			{
				continue;
			}
			if (!HitControlInCanvas(c))
			{
				continue;
			}
			int d = 0;
			Node p = c.GetParent();
			while (p != null)
			{
				d++;
				p = p.GetParent();
			}
			if (d > bestDepth)
			{
				bestDepth = d;
				best = c;
			}
		}
		return best;
	}

	/// <summary>对单个候选节点做命中判定并收集（内部消化所有异常）。</summary>
	private void TryCollectOne(Node node, Vector2 mouse)
	{
		Type t = node.GetType();
		Node2D n2d = node as Node2D;
		if (n2d == null)
		{
			return;
		}
		// 已收集 / 已消失的跳过（对应源码里的 isCollect / die / !over 等守卫）
		if (GetBoolMember(node, "isCollect") || GetBoolMember(node, "die"))
		{
			return;
		}

		float radius = 0f;
		Node2D sprite = null;
		if (IsSubclassNamed(t, "TowerDefenseSunBase") || t.Name == "TowerDefenseSunBase")
		{
			// ⚠️ v1.0.6 曾在这里误加 `if (over) return;` —— `over` 只是"已落地停住"，
			//   游戏源码 `_Input` 里**没有**这个守卫，落地的阳光照样能收。
			//   加了它 ⇒ 时停期间停住的阳光永远收不到（用户反馈"停住但收不了"）。已删除。
			sprite = GetMember(node, "_sprite") as Node2D;
			radius = 40f * n2d.Scale.X;
		}
		else if (IsSubclassNamed(t, "TowerDefenseCoinBase") || t.Name == "TowerDefenseCoinBase")
		{
			sprite = GetMember(node, "spriteNode") as Node2D;
			radius = 30f * n2d.Scale.X;
		}
		else
		{
			return;
		}
		if (sprite == null || !GodotObject.IsInstanceValid(sprite))
		{
			if (!_collectDiagReported)
			{
				_collectDiagReported = true;
				PickDiag("收集诊断：节点 " + SafePath(node) + " 的 sprite 字段取不到（"
					+ (IsSubclassNamed(t, "TowerDefenseSunBase") ? "_sprite" : "spriteNode") + "）。");
			}
			return;
		}
		// ★ v1.0.19：统一到**窗口坐标**（与玩家屏幕上看到的一致）。
		//   `GetGlobalTransformWithCanvas()` 已含祖先 transform + canvas transform，
		//   乘原点即得该精灵在窗口上的位置；半径按同一变换缩放（`Scale.X`）。
		Transform2D xf = sprite.GetGlobalTransformWithCanvas();
		Vector2 spriteWin = xf * Vector2.Zero;
		float radiusWin = radius * Mathf.Max(0.01f, xf.Scale.X);
		float dist = mouse.DistanceTo(spriteWin);
		if (_collectDiagCount < 200 && Input.IsMouseButtonPressed(MouseButton.Left))
		{
			_collectDiagCount++;
			PickDiag("收集诊断[" + _collectDiagCount + "] 类型=" + t.Name
				+ " 鼠标(窗口)=" + mouse + " 精灵(窗口)=" + spriteWin
				+ " 距离=" + dist.ToString("F1") + " 半径=" + radiusWin.ToString("F1")
				+ " 命中=" + (dist <= radiusWin));
		}
		if (dist > radiusWin)
		{
			return;
		}
		// ★ 调 public Collection()
		// ★★ v1.0.21 真正的修复：`Collection()` 只是"开始收集"，**真正的结算在 Tween 回调里**
		//
		// `TowerDefenseSunBase.Collection()`（`TowerDefenseSunBase.cs:382`）：
		//     if (!die && !isCollect) {
		//         ...
		//         isCollect = true;                       // ★ 先置位
		//         _collectionTween = CreateTween();       // ★ 建 Tween
		//         _collectionTween.TweenProperty(_sprite, "global_position", 相机+偏移, 1.0);
		//         _collectionTween.TweenCallback(Callable.From(FinishCollectionFlightCallback));  // ★ 1 秒后结算
		//     }
		// ⇒ **`SceneTree.Paused = true` 时 Tween 不走**：
		//   回调永不触发 ⇒ **不加阳光、飘飞动画不动、也永不销毁**；
		//   而 `isCollect` 已经被置 true ⇒ **之后再点它会被 `!isCollect` 直接挡掉**（永久卡死）。
		//   实测日志：`★ 已尝试收集：距离=18.8 调用前在树=True 调用后在树=True`
		//   —— 调用了、但对象没消失，正是这个原因。
		// ⇒ 修法：调用 `Collection()` 后，**手动补一次它本该由 Tween 触发的结算回调**。
		//   （暂停期间本来也看不到那 1 秒的飞行动画，直接结算即可。）
		bool wasInTree = GodotObject.IsInstanceValid(node) && node.IsInsideTree();
		InvokeMethod(node, "Collection");
		// `Collection()` 里会把三个参数存进字段，这里原样取出再喂给回调
		object leaseVersion = GetMember(node, "_collectionLeaseVersion");
		object collectCtx = GetMember(node, "_collectionContext");
		object collectValue = GetMember(node, "_collectionValue");
		if (leaseVersion != null)
		{
			InvokeMethod(node, "FinishCollectionFlight", leaseVersion, collectCtx, collectValue);
		}
		bool nowInTree = GodotObject.IsInstanceValid(node) && node.IsInsideTree();
		PickDiag("★ 已尝试收集：" + t.Name + " 距离=" + dist.ToString("F1")
			+ " 调用前在树=" + wasInTree + " 调用后在树=" + nowInTree
			+ " isCollect=" + GetBoolMember(node, "isCollect"));
	}

	private int _collectDiagCount;
	private bool _collectDiagReported;

	/// <summary>反射读 bool 字段/属性（读不到返回 false）。</summary>
	private static bool GetBoolMember(object target, string name)
	{
		object v = GetMember(target, name);
		return v is bool b && b;
	}

	/// <summary>定位战场 `CharacterNode`（掉落物的实际父节点）。</summary>
	private Node FindCharacterNode()
	{
		try
		{
			Node control = FindNodeByClassName(_tree.Root, "TowerDefenseControlNew")
				?? FindNodeByClassName(_tree.Root, "TowerDefenseControl");
			if (control == null)
			{
				return null;
			}
			Node cn = control.GetNodeOrNull<Node2D>("CharacterLayer/CharacterNode");
			if (cn != null)
			{
				return cn;
			}
		}
		catch { }
		return null;
	}

	/// <summary>通过 `TowerDefenseManager.GetMapFeature()`（反射）拿 map feature 对象。</summary>
	private object TryGetMapFeatureViaManager()
	{
		try
		{
			object mgr = GetSingletonByClassName("TowerDefenseManager");
			if (mgr == null)
			{
				return null;
			}
			MethodInfo mi = mgr.GetType().GetMethod("GetMapFeature",
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
			object r = mi?.Invoke(mi.IsStatic ? null : mgr, null);
			if (r is GodotObject veto && GodotObject.IsInstanceValid(veto))
			{
				return r;
			}
			// GodotObject 派生但非 Node 也正常（feature 就是这种）
			return (r is GodotObject) ? r : null;
		}
		catch { return null; }
	}

	/// <summary>按类名找 autoload / 单例（含静态 `Instance` 属性）。</summary>
	private object GetSingletonByClassName(string className)
	{
		try
		{
			if (_tree == null || !GodotObject.IsInstanceValid(_tree.Root))
			{
				return null;
			}
			Node n = FindNodeByClassName(_tree.Root, className);
			if (n != null)
			{
				return n;
			}
		}
		catch { }
		return null;
	}

	/// <summary>用反射写公开/私有字段。</summary>
	private static bool SetMember(object target, string fieldName, object value)
	{
		if (target == null)
		{
			return false;
		}
		try
		{
			// ★★ v1.0.18：同样**逐级遍历基类** —— 基类声明的 private 字段用
			//   `GetField` 直接查是拿不到的（详见 `GetMember` 上的说明）。
			for (Type t = target.GetType(); t != null; t = t.BaseType)
			{
				FieldInfo f = t.GetField(fieldName,
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
				if (f != null)
				{
					f.SetValue(target, value);
					return true;
				}
			}
			return false;
		}
		catch { return false; }
	}

	/// <summary>反射调用无参方法并取 bool 返回值（取不到一律 false）。</summary>
	private static bool GetBoolByMethod(object target, string methodName)
	{
		if (target == null)
		{
			return false;
		}
		try
		{
			MethodInfo mi = target.GetType().GetMethod(methodName,
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			if (mi == null)
			{
				return false;
			}
			object r = mi.Invoke(target, null);
			return r is bool b && b;
		}
		catch { return false; }
	}

	private bool _relayFaultReported;

	/// <summary>确保输入转发节点存在（时停期间调用）。</summary>
	private void EnsureRelay()
	{
		try
		{
			if (_relay != null && GodotObject.IsInstanceValid(_relay))
			{
				return;
			}
			if (_tree == null || !GodotObject.IsInstanceValid(_tree.Root))
			{
				return;
			}
			_relay = new InputRelayNode
			{
				Name = "ModTimeStopInputRelay",
				Entry = this,
				// ★ 关键：Always ⇒ 暂停时 `_Input` 照常被调用
				ProcessMode = Node.ProcessModeEnum.Always,
			};
			_tree.Root.AddChild(_relay, forceReadableName: false, @internal: Node.InternalMode.Disabled);
			Info("已挂载输入转发节点（Always），时停期间代为接收种植/铲除/选卡输入。");
		}
		catch (Exception ex)
		{
			Warn("挂载输入转发节点失败（已吞）：" + ex.Message);
		}
	}

	/// <summary>移除输入转发节点（解除时停时调用）。</summary>
	private void RemoveRelay()
	{
		try
		{
			if (_relay != null && GodotObject.IsInstanceValid(_relay))
			{
				_relay.QueueFree();
				Info("已移除输入转发节点。");
			}
		}
		catch { }
		_relay = null;
	}

	/// <summary>用反射读公开字段/属性（兼容不同版本的字段名）。</summary>
	private static object GetMember(object target, string name)
	{
		if (target == null)
		{
			return null;
		}
		try
		{
			// ★★ v1.0.18：**必须逐级遍历基类**。
			//   C# 的 `Type.GetField(name, NonPublic|Instance)` **只查当前类型自己声明的
			//   私有字段，不查基类的**（`FlattenHierarchy` 也仅对 public/protected static 生效）。
			//   实测踩坑：阳光节点是子类 `TowerDefenseSun`，而 `_sprite` 声明在基类
			//   `TowerDefenseSunBase`（`private AdobeAnimateSprite _sprite;` +
			//   只读属性 `public AdobeAnimateSprite sprite => _sprite;`）⇒
			//   直接 `GetField("_sprite")` 返回 null ⇒ 收集时"取不到 sprite"、**永远收不了阳光**。
			for (Type t = target.GetType(); t != null; t = t.BaseType)
			{
				FieldInfo f = t.GetField(name,
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
				if (f != null)
				{
					return f.GetValue(target);
				}
			}
			for (Type t = target.GetType(); t != null; t = t.BaseType)
			{
				PropertyInfo p = t.GetProperty(name,
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
				if (p != null && p.CanRead)
				{
					return p.GetValue(target);
				}
			}
		}
		catch { }
		return null;
	}

	/// <summary>用反射调用公开方法（参数类型宽松匹配）。</summary>
	private static void InvokeMethod(object target, string name, params object[] args)
	{
		if (target == null)
		{
			return;
		}
		try
		{
			MethodInfo m = target.GetType().GetMethod(
				name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			m?.Invoke(target, args);
		}
		catch { }
	}

	// ================================================================ 按钮创建与定位

	/// <summary>
	/// ★ v1.0.3 兜底点击通道：直接轮询鼠标左键是否落在「时停」按钮矩形内。
	///
	/// 为什么要它：Godot 的 GUI 输入派发（`Viewport::_gui_input_event`）会检查 Control 的
	/// `Node::can_process()`。`Paused = true` 时，若按钮不是 `Always`，`_gui_input` 根本不会被调用
	/// ⇒ **"只能开、关不掉"**（用户实测 v1.0.2：日志只有 Shutdown 时解除，没有"用户点击关闭"）。
	/// 已给按钮设 `ProcessMode = Always` 修主路径；这里再加一层保险 ——
	/// `SceneTree.process_frame` 信号在暂停时**照样发**（`SceneTree::process()` 无条件 emit），
	/// 所以用 `Input.IsMouseButtonPressed` + 按钮全局矩形做命中判定，永远有效。
	/// 与 `OnButtonInput` 用 `_lastClickFrame` 去重，避免同一次点击被两条通道各触发一次。
	/// </summary>
	private void PollButtonClick()
	{
		try
		{
			// ★★ v1.0.26（用户："改成复选框后怎么还有点击的问题，长点会出现问题，
			//   原版的复选框就没这个问题"）：
			//   **勾选框形态下必须停掉轮询**。
			//   `CheckBox` 自己走 Godot 的 GUI 派发（我已给它设 `ProcessMode = Always`，
			//   暂停时同样能收到 `_gui_input`）⇒ 点一下就会自己切 `ButtonPressed`
			//   并发 `Toggled`；此时**再叠加我这层轮询**，就会**一次点击触发两次 Toggle**
			//   （长按 / 连点时尤其明显：开→关→开 抖动）。
			//   原版复选框之所以没这问题，正是因为没有这层轮询。
			//   轮询只保留给"自绘按钮"形态（那个不是原生按钮，暂停时收不到 GUI）。
			if (!UseButtonStyle)
			{
				_pollDown = false;
				return;
			}
			if (_buttonHost == null || !GodotObject.IsInstanceValid(_buttonHost))
			{
				return;
			}
			if (!Input.IsMouseButtonPressed(MouseButton.Left))
			{
				_pollDown = false;   // 松开后复位
				return;
			}
			if (_pollDown)
			{
				return;              // 按住不放，不重复触发
			}
			_pollDown = true;

			// 鼠标位置（考虑拉伸/缩放，用 Viewport 的鼠标位置）
			Viewport vp = (_tree != null && GodotObject.IsInstanceValid(_tree)) ? _tree.Root.GetViewport() : null;
			if (vp == null)
			{
				return;
			}
			Vector2 mp = vp.GetMousePosition();
			// 命中矩形：按钮的全局矩形（GlobalPosition + Size，含外框）
			Vector2 gp = _buttonHost.GlobalPosition;
			Vector2 gs = _buttonHost.Size;
			// ★★ v1.0.24（用户反馈"有的时候时停按钮点了没反应"）：
			//   布局尚未完成时 `Size` 会是 0 ⇒ `rect` 退化成空矩形 ⇒ **永远点不中**
			//   （按钮明明画出来了，但那一两秒内点击无效）。给一个保底尺寸兜住。
			if (gs.X < 1f || gs.Y < 1f)
			{
				gs = new Vector2(80f, 32f);
			}
			Rect2 rect = new Rect2(gp, gs);
			// 竖直方向允许 6px 容差（贴太紧时好点）
			rect = rect.Grow(3f);
			if (rect.HasPoint(mp))
			{
				// 与 OnButtonInput 去重：同一帧内 GUI 已处理过就不再重复
				long f = _tree.GetFrame();
				if (_lastClickFrame == f - 1 || _lastClickFrame == f)
				{
					return;
				}
				_lastClickFrame = f;
				Toggle();
			}
			else if (EnablePickDiag && _btnMissDiag < 20)
			{
				_btnMissDiag++;
				PickDiag("按钮未命中：鼠标=" + mp + " 按钮矩形=" + rect + " size=" + gs
					+ " 可见=" + _buttonHost.Visible);
			}
		}
		catch { }
	}

	/// <summary>按钮未命中诊断计数。</summary>
	private int _btnMissDiag;

	/// <summary>鼠标左键是否处于按下（用于轮询去重）。</summary>
	private bool _pollDown;

	/// <summary>最近一次触发点击的帧号（`OnButtonInput` 与 `PollButtonClick` 去重用）。</summary>
	private long _lastClickFrame = long.MinValue;

	/// <summary>找到定位基准（加速按钮，回退齿轮）并把「时停」放到它下方。</summary>
	/// <summary>
	/// ★ v1.0.17：判断"是否已在关卡战斗中"。
	/// 用来把「时停」按钮限制在**关卡内**显示（主菜单 / 地图界面不出现它）。
	/// 判据：存在 `TowerDefenseControlNew` 且其 `isGameRunning == true`
	/// （该字段由 `GameRunningEntered()` 置 true，正是"游戏开始"那一刻）。
	/// </summary>
	private bool IsInBattle(Node root)
	{
		try
		{
			if (root == null || !GodotObject.IsInstanceValid(root))
			{
				return false;
			}
			Node ctl = FindNodeByClassName(root, "TowerDefenseControlNew");
			if (ctl == null || !GodotObject.IsInstanceValid(ctl))
			{
				return false;
			}
			return GetBoolMember(ctl, "isGameRunning");
		}
		catch
		{
			return false;
		}
	}

	private void EnsureButton(Node root)
	{
		// ★ v1.0.17（用户要求）：**只在关卡内显示按钮** —— 主菜单 / 地图界面不要出现它。
		//   判据 = 关卡控制器存在且 `isGameRunning`（战斗已开始）。
		if (!IsInBattle(root))
		{
			if (_buttonHost != null && GodotObject.IsInstanceValid(_buttonHost))
			{
				_buttonHost.Visible = false;
			}
			return;
		}
		if (_buttonHost != null && GodotObject.IsInstanceValid(_buttonHost))
		{
			_buttonHost.Visible = true;
		}
		// 已存在且仍然有效 → 更新颜色 + 校正位置（进关卡时按钮尺寸可能后layout 才有）
		if (_button != null && GodotObject.IsInstanceValid(_button))
		{
			UpdateButtonVisual();
			ResyncPosition(root);
			return;
		}
		_button = null;
		_buttonHost = null;
		_diagDumped = false;   // 新关卡/按钮重建 → 允许再 dump 一次诊断

		// 优先：加速按钮（时停放它正下方）；回退：齿轮
		Control anchorBtn = FindAnchorButton(root) ?? FindGearFallback(root);
		if (anchorBtn == null)
		{
			ResetGearReport();
			return;
		}
		// 基准按钮还没布局好（尺寸为 0）时先不建，等下一轮
		if (anchorBtn.Size.Y < 1f)
		{
			return;
		}
		if (UseButtonStyle)
		{
			CreateButtonNear(anchorBtn);
		}
		else
		{
			CreateCheckBoxNear(anchorBtn);
		}
	}

	/// <summary>
	/// ★ v1.0.25：创建**勾选框**形态（默认）。
	///
	/// 直接照抄游戏自带的「加速」：同一种节点（`CheckBox`）、挂在同一个父容器
	/// （`TowerDefenseControlNew/GUITop`，`CheckBox2X` 就在那儿）下 ⇒
	/// **样式完全由游戏 Theme 提供，外观与「加速」一致**，不需要手绘。
	///
	/// ⚠️ 必须 `ProcessMode = Always`：暂停时继承自根（Pausable）的 `Control`
	///    收不到 `_gui_input`（这是 v1.0.3 踩过的坑）。
	/// </summary>
	private void CreateCheckBoxNear(Control anchorBtn)
	{
		try
		{
			Node parent = anchorBtn.GetParent();
			if (parent == null)
			{
				return;
			}
			CheckBox cb = new CheckBox
			{
				Name = ButtonName,
				Text = "时停",
				ButtonPressed = _timeStopOn,
				ProcessMode = Node.ProcessModeEnum.Always,
				MouseDefaultCursorShape = Control.CursorShape.PointingHand,
			};
			cb.Toggled += OnTimeStopToggled;
			parent.AddChild(cb, false, Node.InternalMode.Disabled);

			// 贴在基准按钮（加速）正下方
			cb.GlobalPosition = new Vector2(anchorBtn.GlobalPosition.X,
				anchorBtn.GlobalPosition.Y + anchorBtn.Size.Y + 6f);
			cb.CustomMinimumSize = new Vector2(Mathf.Max(anchorBtn.Size.X, 72f), 0f);

			_button = cb;
			_buttonHost = cb;    // 复用"宿主"引用：轮询命中判定 / 位置校正都靠它
			Info("已创建「时停」勾选框（CheckBox 样式，与「加速」同款）。");
		}
		catch (Exception ex)
		{
			Warn("创建「时停」勾选框失败（已吞）：" + ex.Message);
		}
	}

	/// <summary>勾选框切换回调（`Toggled` 信号）。</summary>
	private void OnTimeStopToggled(bool pressed)
	{
		try
		{
			// ★ v1.0.28：同样过防抖闸门（两条输入路径共用同一道闸）
			if (!PassToggleGate())
			{
				return;
			}
			if (pressed == _timeStopOn)
			{
				return;
			}
			if (pressed)
			{
				Engage();
			}
			else
			{
				Disengage("用户取消勾选");
			}
		}
		catch (Exception ex)
		{
			Warn("勾选框切换异常（已吞）：" + ex.Message);
		}
	}

	/// <summary>基准按钮位置/尺寸变了（分辨率、UI 缩放、进关卡）时，把按钮重新对齐到它正下方。</summary>
	private void ResyncPosition(Node root)
	{
		try
		{
			if (_buttonHost == null || !GodotObject.IsInstanceValid(_buttonHost))
			{
				return;
			}
			Control anchorBtn = FindAnchorButton(root) ?? FindGearFallback(root);
			if (anchorBtn == null || anchorBtn.Size.Y < 1f)
			{
				return;
			}
			Vector2 want = new Vector2(anchorBtn.GlobalPosition.X,
				anchorBtn.GlobalPosition.Y + anchorBtn.Size.Y + 6f);
			if (_buttonHost.GlobalPosition.DistanceTo(want) > 2f)
			{
				_buttonHost.GlobalPosition = want;
				_buttonHost.CustomMinimumSize = new Vector2(Mathf.Max(anchorBtn.Size.X, 72f), 0f);
			}
		}
		catch { }
	}

	/// <summary>回退用：反射读 `optionButton`（齿轮）。</summary>
	private Control FindGearFallback(Node root)
	{
		return WalkForGearField(root, 0);
	}

	private Control WalkForGearField(Node node, int depth)
	{
		if (node == null || depth > 64)
		{
			return null;
		}
		try
		{
			Type t = node.GetType();
			if (t.Name == "TowerDefenseControl" || t.Name == "TowerDefenseControlNew"
				|| IsSubclassNamed(t, "TowerDefenseControl"))
			{
				return ReadControlField(node, t, "optionButton");
			}
		}
		catch { }
		int n = node.GetChildCount();
		for (int i = 0; i < n; i++)
		{
			Control r = WalkForGearField(node.GetChild(i), depth + 1);
			if (r != null)
			{
				return r;
			}
		}
		return null;
	}

	private void ResetGearReport()
	{
		// 关卡未开始/已结束时会找不到齿轮，这是正常的，不重复报告
		_gearFoundReported = false;
	}

	/// <summary>
	/// 找"定位基准"按钮：**优先 `checkBox2X`（加速按钮）**——用户要求「时停」放在加速下方；
	/// 找不到加速时退回 `optionButton`（齿轮）。
	/// 两者都是 `TowerDefenseControl` 的 **public 字段**，节点名虽已知
	/// （`GUITop/CheckBox2X` / `GUITop/OptionButton`）但类型未定，故用反射读字段最稳。
	/// </summary>
	private Control FindAnchorButton(Node root)
	{
		return WalkForAnchor(root, 0);
	}

	private Control WalkForAnchor(Node node, int depth)
	{
		if (node == null || depth > 64)
		{
			return null;
		}
		try
		{
			Type t = node.GetType();
			// 按类名匹配（避免直接引用游戏类型，减小耦合）
			if (t.Name == "TowerDefenseControl" || t.Name == "TowerDefenseControlNew"
				|| IsSubclassNamed(t, "TowerDefenseControl"))
			{
				// 1) 先要加速按钮（时停放它下面）
				//    ⚠️ 不能要求 IsVisibleInTree()：tscn 里 CheckBox2X 是 visible=false，
				//    进关卡后才显形；早期扫描会因不可见而误判缺失、回落到齿轮。
				//    这里只要求"节点有效"，布局尺寸由 EnsureButton 的 Size.Y 检查兜底。
				Control c2 = ReadControlField(node, t, "checkBox2X");
				if (c2 != null)
				{
					ReportAnchor(c2, "加速按钮 checkBox2X");
					return c2;
				}
			}
		}
		catch { }

		int n = node.GetChildCount();
		for (int i = 0; i < n; i++)
		{
			Control r = WalkForAnchor(node.GetChild(i), depth + 1);
			if (r != null)
			{
				return r;
			}
		}
		return null;
	}

	/// <summary>读 `TowerDefenseControl` 上的一个 Control 类字段。</summary>
	private static Control ReadControlField(Node node, Type t, string fieldName)
	{
		try
		{
			FieldInfo fi = FindFieldAlong(t, fieldName);
			if (fi == null)
			{
				return null;
			}
			object v = null;
			try { v = fi.GetValue(node); } catch { }
			if (v is Control c && GodotObject.IsInstanceValid(c))
			{
				return c;
			}
		}
		catch { }
		return null;
	}

	private void ReportAnchor(Control c, string desc)
	{
		if (!_gearFoundReported)
		{
			_gearFoundReported = true;
			Info("已找到定位基准（" + desc + "）：类型=" + c.GetType().Name
				+ " 节点名=" + c.Name + " 全局位置=" + c.GlobalPosition
				+ " 尺寸=" + c.Size);
		}
	}

	/// <summary>在基准按钮（加速按钮）**正下方**创建「时停」按钮（带圆角描边）。</summary>
	private void CreateButtonNear(Control anchorBtn)
	{
		try
		{
			// 父节点：基准按钮的父（保证同一坐标系，位置好算）
			Node parent = anchorBtn.GetParent();
			if (parent == null)
			{
				return;
			}

			Label lb = new Label();
			lb.Name = ButtonName;
			lb.Text = "时停";
			lb.MouseFilter = Control.MouseFilterEnum.Stop;      // 要接收点击
			lb.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
			lb.HorizontalAlignment = HorizontalAlignment.Center;
			lb.VerticalAlignment = VerticalAlignment.Center;
			lb.AddThemeColorOverride("font_color", GreenColor);
			lb.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 1));
			lb.AddThemeConstantOverride("outline_size", 5);
			lb.AddThemeFontSizeOverride("font_size", 22);

			// 圆角描边外框（给 PanelContainer 画）
			StyleBoxFlat sb = new StyleBoxFlat();
			sb.BgColor = new Color(0f, 0f, 0f, 0.35f);
			sb.BorderColor = BorderGreen;
			sb.SetBorderWidthAll(2);
			sb.SetCornerRadiusAll(6);
			sb.ContentMarginLeft = 10f;
			sb.ContentMarginRight = 10f;
			sb.ContentMarginTop = 3f;
			sb.ContentMarginBottom = 3f;

			PanelContainer pc = new PanelContainer();
			pc.Name = ButtonName + "Panel";
			pc.AddThemeStyleboxOverride("panel", sb);
			pc.MouseFilter = Control.MouseFilterEnum.Stop;
			// ★★ v1.0.3 关键：按钮自身必须 ProcessMode = Always，否则暂停后收不到点击！
			//   Godot 的 GUI 输入派发（Viewport::_gui_input_event）会检查 Control 的
			//   Node::can_process()：`Paused = true` 时，继承自根（Pausable）的 Control
			//   一律不再处理 _gui_input ⇒ **关闭点击丢失 → 只能开不能关**。
			//   给按钮自身设 Always 即可（只动这一个节点，**不碰根、不碰 CharacterNode 祖先链**，
			//   所以不会像 v1.0.0/v1.0.1 那样把角色的父链解析带成 Always）。
			pc.ProcessMode = Node.ProcessModeEnum.Always;

			parent.AddChild(pc, false, Node.InternalMode.Disabled);
			pc.AddChild(lb, false, Node.InternalMode.Disabled);
			// Label 只是显示层，同样设 Always，保证暂停时仍能刷新颜色/文本
			lb.ProcessMode = Node.ProcessModeEnum.Always;

			// ★ 位置：贴在基准按钮（加速）的**正下方**。
			//   `CheckBox2X` 是右上角锚定的（anchor_left=1），tscn 里它的框是
			//   offset(-107, 64) ~ (-5, 128)、scale 0.78。这里用"运行时实测的全局位置+尺寸"
			//   推算，比硬编码 offset 更稳（换分辨率/UI 缩放也不会跑偏）。
			Vector2 aPos = anchorBtn.GlobalPosition;
			Vector2 aSize = anchorBtn.Size;
			// 竖直方向留 6px 间隙；水平与基准按钮居中对齐
			pc.GlobalPosition = new Vector2(aPos.X, aPos.Y + aSize.Y + 6f);
			pc.CustomMinimumSize = new Vector2(Mathf.Max(aSize.X, 72f), 0f);
			pc.ZIndex = 200;

			// 点击
			pc.GuiInput += OnButtonInput;
			lb.GuiInput += OnButtonInput;

			_button = lb;
			_buttonHost = pc;
			UpdateButtonVisual();
			Info("已创建「时停」按钮，位置=" + pc.GlobalPosition);
		}
		catch (Exception ex)
		{
			Warn("创建时停按钮异常（已吞）：" + ex.Message);
		}
	}

	/// <summary>按当前状态刷新颜色（绿=未触发 / 红=触发中）。</summary>
	private void UpdateButtonVisual()
	{
		try
		{
			Color fg = _timeStopOn ? RedColor : GreenColor;
			Color bd = _timeStopOn ? BorderRed : BorderGreen;
			// ★ v1.0.25：两种 UI 形态都能安全跑 ——
			//   勾选框（`CheckBox`）**刻意不改色**，保持游戏原生样式（与「加速」一致）；
			//   自绘按钮（`Label` + `PanelContainer`）才做绿/红着色。
			if (_button is Label lb && GodotObject.IsInstanceValid(lb))
			{
				lb.AddThemeColorOverride("font_color", fg);
			}
			if (_buttonHost is PanelContainer pc && GodotObject.IsInstanceValid(pc))
			{
				StyleBoxFlat sb = pc.GetThemeStylebox("panel") as StyleBoxFlat;
				if (sb != null)
				{
					sb.BorderColor = bd;
				}
			}
		}
		catch { }
	}

	private void OnButtonInput(InputEvent ev)
	{
		try
		{
			if (ev is InputEventMouseButton mb && mb.Pressed
				&& mb.ButtonIndex == MouseButton.Left)
			{
				// 吃完事件，避免穿透到战场（否则点按钮会顺手种下一棵植物）
				Viewport vp = (_tree != null && GodotObject.IsInstanceValid(_tree))
					? _tree.Root.GetViewport() : null;
				if (vp != null)
				{
					vp.SetInputAsHandled();
				}
				// ★ 记录帧号：与 PollButtonClick 兜底通道去重，避免一次点击触发两次开关
				if (_tree != null && GodotObject.IsInstanceValid(_tree))
				{
					_lastClickFrame = _tree.GetFrame();
				}
				Toggle();
			}
		}
		catch (Exception ex)
		{
			Warn("按钮点击异常（已吞）：" + ex.Message);
		}
	}

	// ================================================================ 时停开关

	private void Toggle()
	{
		// ★★ v1.0.28（用户建议）：**成功切换后 1 秒内不接受新的切换**。
		//   彻底根治"长按 / 连点抖动"—— 不依赖去重帧号、也不去猜是哪条输入路径触发的，
		//   直接给状态加一个时间闸门（原版加速复选框体感上也没法连点刷）。
		if (!PassToggleGate())
		{
			return;
		}
		if (_timeStopOn)
		{
			Disengage("用户点击关闭");
		}
		else
		{
			Engage();
		}
		UpdateButtonVisual();
		SyncCheckBoxState();
	}

	/// <summary>切换防抖闸门：距上次成功切换不足 <see cref="ToggleDebounceMs"/> 就直接拒掉。</summary>
	private bool PassToggleGate()
	{
		ulong now = Time.GetTicksMsec();
		if (_lastToggleMsec != 0 && now - _lastToggleMsec < ToggleDebounceMs)
		{
			// 被闸门拦下时，把勾选框视觉与真实状态同步回去（否则勾选框会"骗人"）
			SyncCheckBoxState();
			return false;
		}
		_lastToggleMsec = now;
		return true;
	}

	/// <summary>最近一次成功切换时停的时刻（`Time.GetTicksMsec()`）。</summary>
	private ulong _lastToggleMsec;

	/// <summary>切换防抖窗口（毫秒）。用户要求"成功触发后 1 秒内不让改时停状态"。</summary>
	private const ulong ToggleDebounceMs = 1000;

	/// <summary>
	/// ★ v1.0.25：把 `_timeStopOn` 同步到勾选框的视觉状态。
	/// 走"轮询命中"那条路（`PollButtonClick` → `Toggle()`）时，勾选框自己不会变，
	/// 必须手动同步；用 `SetPressedNoSignal` 避免反过来再触发一次 `Toggled`。
	/// </summary>
	private void SyncCheckBoxState()
	{
		try
		{
			if (_button is CheckBox cb && GodotObject.IsInstanceValid(cb) && cb.ButtonPressed != _timeStopOn)
			{
				cb.SetPressedNoSignal(_timeStopOn);
			}
		}
		catch { }
	}

	/// <summary>开启时停：冻结世界 + 保活交互层。</summary>
	private void Engage()
	{
		try
		{
			if (_tree == null || !GodotObject.IsInstanceValid(_tree))
			{
				return;
			}
			_savedTreePaused = _tree.Paused;
			_timeStopOn = true;

			// 保活：把交互相关节点设为 Always（暂停后仍响应输入）
			ApplyKeepAlive();

			// 冻结
			_tree.Paused = true;
			Info("时停已开启：PhyFrame 冻结。保活节点 " + _keptAlive.Count + " 个。");
		}
		catch (Exception ex)
		{
			_timeStopOn = false;
			Warn("开启时停异常（已吞）：" + ex.Message);
		}
	}

	/// <summary>关闭时停：解除冻结 + 还原所有保活节点。</summary>
	private void Disengage(string reason)
	{
		try
		{
			_timeStopOn = false;
			RestoreKeepAlive();
			if (_tree != null && GodotObject.IsInstanceValid(_tree))
			{
				_tree.Paused = _savedTreePaused;
			}
			Info("时停已解除（" + reason + "）。");
		}
		catch (Exception ex)
		{
			Warn("关闭时停异常（已吞）：" + ex.Message);
		}
	}

	/// <summary>维持时停：每轮校正 Paused（游戏可能因失焦等自行改动）。</summary>
	private void MaintainTimeStop()
	{
		if (!_timeStopOn)
		{
			return;
		}
		if (_tree == null || !GodotObject.IsInstanceValid(_tree))
		{
			return;
		}
		if (!_tree.Paused)
		{
			_tree.Paused = true;
		}
	}

	/// <summary>
	/// 保活：把"玩家操作相关"的节点改成 Always，让它们在暂停时仍能收输入 / 跑 `_Process`。
	///
	/// ════════════════════════════════════════════════════════════════════
	/// ★ v1.0.6 保活策略（2026-09-28 晚，彻底精简）★
	/// ════════════════════════════════════════════════════════════════════
	/// 教训链：
	///   v1.0.2/1.0.3 保活 0 个 → 世界冻得干净，但交互全废。
	///   v1.0.4 保活 3 个（ObjectManager+MapControl）→ 无效（找错了宿主与入口）。
	///   v1.0.5 保活 657~683 个（含泛撒网 UI 570 + 「掉落物」95）→
	///     · 95 个其实是**植物/僵尸**（`TowerDefenseCharacter : TowerDefenseGroundItemBase`）
	///       ⇒ 僵尸/植物逻辑解冻 ⇒「停止但动画在播」；
	///     · 掉落物被设 Always ⇒ `_PhysicsProcess` 活了 ⇒「按惯性继续往下掉落」；
	///     · 570 个 UI 泛撒网 ⇒ 动画播放器也活了。
	///
	/// **v1.0.6 策略：保活面越小越好 —— 只保 `TowerDefenseMapControl` 一个节点。**
	///   · 种植/铲除/工具：由旁路节点每帧直调 `mapFeature.ProcessInput()`（不靠保活）。
	///   · 阳光/金币收集：由旁路节点每帧做命中判定 + 调 public `Collection()`（不靠保活）。
	///   · **掉落物一律保持冻结**（不下落、不消失）—— 这才是"时停"该有的样子。
	///   · 角色/植物/僵尸/子弹：绝不碰 ⇒ 冻结生效。
	/// 禁区（`CharacterNode` 祖先链 + 根）依旧一律不改。
	/// </summary>
	private void ApplyKeepAlive()
	{
		try
		{
			Node control = FindNodeByClassName(_tree.Root, "TowerDefenseControlNew")
				?? FindNodeByClassName(_tree.Root, "TowerDefenseControl");
			if (control == null)
			{
				Warn("未找到 TowerDefenseControlNew，保活只做全局交互节点。");
			}

			// ★ 1) 构建"禁区"：CharacterNode 的全部祖先（含自身）+ SceneTree.Root 链
			_protected.Clear();
			Node charNode = (control != null) ? control.GetNodeOrNull<Node2D>("CharacterLayer/CharacterNode") : null;
			Node cursor = (charNode != null) ? charNode : control;
			for (Node p = cursor; p != null; p = p.GetParent())
			{
				_protected.Add(p);
			}
			// 兜底：无论 CharacterLayer 在不在，根节点一律禁改
			if (_tree != null && GodotObject.IsInstanceValid(_tree.Root))
			{
				for (Node p = _tree.Root; p != null; p = p.GetParent())
				{
					_protected.Add(p);
				}
			}

			// ★ 2) 唯一保活：`TowerDefenseMapControl`（只它自己，不递归）
			//   它挂在 `TowerDefenseControlNew/TowerDefenseInGameLevelControl/…` 下，
			//   与 `CharacterLayer` 是**兄弟分支**，**不是** `CharacterNode` 的祖先 ⇒ 安全。
			//   ⚠️ 它自己的 `_PhysicsProcess` 在暂停时仍不跑（受门控），
			//   所以真正干活的是旁路节点 §3 —— 保活它只是"顺手让它的状态机别卡"。若无效可再删。
			int nMap = 0;
			if (control != null)
			{
				Node mapControl = FindNodeByClassName(control, "TowerDefenseMapControl");
				nMap = KeepAliveTarget(mapControl, false);
			}

			// ★ 2b) v1.0.11：**不再保活 `BankUILayer`**（v1.0.7/v1.0.9 保活均被证伪）。
			//   探针实证：保活 324 个节点后，种子包/铲子节点确实全变 `Always`、`visible=True`、
			//   `mf=Stop/Pass` —— **控件状态完全正常，但依然点不动**。
			//   真因是**坐标系**：它们在 `BankUILayer`(CanvasLayer) 下，
			//   `Control.GetGlobalRect()` 返回 canvas 空间坐标，与鼠标的窗口坐标不匹配；
			//   Godot 正常的 GUI 派发（Viewport 做转换+命中）**暂停时被门控，保活 ProcessMode 恢复不了它**。
			//   ⇒ **唯一可行路径：旁路节点自己做坐标转换 + 命中判定 + 直调 public 接口**
			//     （见 `DriveUiPicks()`）。
			//   ⇒ 与"保活让按钮自己收点击"相比，**必须二选一**（否则双击切换 = 无变化），
			//     本版选"直调"，故保活归零。
			int nBank = 0;

			// ★ 3) 挂载"旁路驱动节点"（Always，挂在 SceneTree.Root 下）：
			//   让「种植 / 铲除 / 选卡 / 收阳光」在暂停期间仍能工作的**真正关键**。
			EnsureRelay();

			Info("保活完成：共 " + _keptAlive.Count + " 个节点（禁区 " + _protected.Count
				+ " 个：CharacterNode 祖先链 + 根，绝不改）。地图控件=" + nMap
				+ "，顶部UI枝=" + nBank
				+ "（掉落物不保活，改由旁路节点主动收取）。");

			// ★ 4) 诊断：把真实树结构 dump 出来（定位"为什么交互仍不可用"）
			DumpDiagnostics(control);
			// ★ 4b) v1.0.8：每轮都把种子包/铲子按钮的完整 ProcessMode 父链打出来
			DumpUiInteractiveNodes(_tree != null ? _tree.Root : null);
			// ★ 4c) v1.0.10：可交互性探针（visible/mouseFilter/globalRect 逐层）
			ProbePacketInteractivity();
		}
		catch (Exception ex)
		{
			Warn("保活处理异常（已吞）：" + ex.Message);
		}
	}

	/// <summary>
	/// 统计 `CharacterNode` 下当前有多少"真掉落物"（阳光/金币）—— **仅用于日志诊断，不再保活**。
	///
	/// ⚠️ v1.0.5 的致命 bug 在此修正：旧 `IsDroppable` 认 `TowerDefenseGroundItemBase`，
	/// 而 **`TowerDefenseCharacter : TowerDefenseGroundItemBase`** ⇒ 把植物/僵尸当掉落物保活了。
	/// v1.0.6 起掉落物**一律不保活**（保持冻结），收集由 `CollectDroppablesAtMouse()` 代劳。
	/// </summary>
	private static bool IsDroppable(Node node)
	{
		try
		{
			Type t = node.GetType();
			// ★ 首先排除角色（植物/僵尸）—— `TowerDefenseCharacter : TowerDefenseGroundItemBase`！
			if (IsSubclassNamed(t, "TowerDefenseCharacter") || t.Name == "TowerDefenseCharacter")
			{
				return false;
			}
			if (IsSubclassNamed(t, "TowerDefenseSunBase") || t.Name == "TowerDefenseSunBase")
			{
				return true;   // 阳光
			}
			if (IsSubclassNamed(t, "TowerDefenseCoinBase") || t.Name == "TowerDefenseCoinBase")
			{
				return true;   // 金币族（CoinGold/Silver/TQ/YB/GoldShard/LuckyBag…）
			}
		}
		catch { }
		return false;
	}

	/// <summary>已保活节点集合（防重复加入 `_keptAlive`）。</summary>
	private readonly HashSet<Node> _keptAliveSet = new HashSet<Node>();

	/// <summary>每轮扫描时汇报场上掉落物数量（诊断用；**不**做任何保活）。</summary>
	private void MaintainDroppableKeepAlive()
	{
		if (!_timeStopOn)
		{
			return;
		}
		try
		{
			Node charNode = FindCharacterNode();
			if (charNode == null)
			{
				return;
			}
			int sun = 0, coin = 0, other = 0;
			int n = charNode.GetChildCount();
			for (int i = 0; i < n; i++)
			{
				Node child = charNode.GetChild(i);
				if (child == null || !GodotObject.IsInstanceValid(child))
				{
					continue;
				}
				Type t = child.GetType();
				if (IsSubclassNamed(t, "TowerDefenseCharacter") || t.Name == "TowerDefenseCharacter")
				{
					other++;
				}
				else if (IsSubclassNamed(t, "TowerDefenseSunBase") || t.Name == "TowerDefenseSunBase")
				{
					sun++;
				}
				else if (IsSubclassNamed(t, "TowerDefenseCoinBase") || t.Name == "TowerDefenseCoinBase")
				{
					coin++;
				}
			}
			// 只在数量变化时打日志，避免刷屏
			if (sun != _lastSunCount || coin != _lastCoinCount)
			{
				_lastSunCount = sun;
				_lastCoinCount = coin;
				Info("场上掉落物：阳光=" + sun + "，金币=" + coin + "（角色/其它=" + other + "，均不保活）。");
			}
		}
		catch { }
	}

	private int _lastSunCount = -1;
	private int _lastCoinCount = -1;

	/// <summary>
	/// 诊断：把关键节点的真实路径/类型/ProcessMode 打出来，用于定位交互不可用的原因。
	/// 只打一次（进关卡首轮），避免刷屏。
	/// </summary>
	private void DumpDiagnostics(Node control)
	{
		if (_diagDumped)
		{
			return;
		}
		_diagDumped = true;
		try
		{
			// (a) SceneTree.Root 的直属子节点
			Node root = (_tree != null && GodotObject.IsInstanceValid(_tree.Root)) ? _tree.Root : null;
			if (root != null)
			{
				int n = root.GetChildCount();
				for (int i = 0; i < n; i++)
				{
					Node c = root.GetChild(i);
					Info("DIAG[Root子" + i + "] name=" + c.Name + " type=" + c.GetType().Name
						+ " mode=" + c.ProcessMode + " childCount=" + c.GetChildCount());
				}
			}
			// (b) 扫描所有 TowerDefenseSunBase 实例，打父链
			if (root != null)
			{
				int found = 0;
				DumpSunAncestors(root, 0, ref found);
				Info("DIAG[阳光节点扫描] 找到 " + found + " 个 TowerDefenseSunBase 实例。");
			}
			// (c) mapControl 的真实路径
			if (control != null)
			{
				Node mc = FindNodeByClassName(control, "TowerDefenseMapControl");
				Info("DIAG[MapControl] " + (mc == null ? "<null>"
					: ("path=" + SafePath(mc) + " type=" + mc.GetType().Name + " mode=" + mc.ProcessMode)));
			}
			// (d) ObjectManager 的所有同名实例路径
			if (root != null)
			{
				int om = 0;
				DumpNodesByClassName(root, 0, "ObjectManager", ref om);
				Info("DIAG[ObjectManager 实例数] " + om);
			}
		}
		catch (Exception ex)
		{
			Warn("DIAG 异常（已吞）：" + ex.Message);
		}
	}

	/// <summary>
	/// ★ v1.0.8 诊断：把「种子包 / 铲子」按钮及其**全部祖先**的 ProcessMode 打出来。
	/// 目的：验证"保活是否真的覆盖到了按钮本身"。
	/// Godot 的 GUI 输入派发是**逐层**的：从 Viewport 起，任何一层的 `Control.CanProcess()`
	/// 为 false（即 ProcessMode 解析为 `Pausable` 且 tree 已暂停）都会**中断派发**
	/// ⇒ 按钮收不到 `_gui_input` ⇒ `Pressed` 信号不发。
	/// 每轮开启时停都打（不设 once 标志），供用户复测核对。
	/// </summary>
	private void DumpUiInteractiveNodes(Node root)
	{
		try
		{
			if (root == null)
			{
				return;
			}
			// 找所有 TowerDefenseInGamePacketShow（种子包）
			var hits = new List<Node>();
			CollectByClassName(root, 0, "TowerDefenseInGamePacketShow", hits, 3);
			Info("DIAG[种子包按钮] 找到 " + hits.Count + " 个 TowerDefenseInGamePacketShow。");
			foreach (Node p in hits)
			{
				DumpProcessChain(p, "种子包");
			}
			// 找铲子：UITopPropContainer 的直属子节点
			Node prop = null;
			Node bank = FindNodeByClassName(root, "BankUILayer");
			if (bank != null)
			{
				prop = FindNodeByClassName(bank, "UITopPropContainer");
			}
			if (prop != null)
			{
				int n = prop.GetChildCount();
				Info("DIAG[道具/铲子栏] UITopPropContainer 子节点数=" + n
					+ " mode=" + prop.ProcessMode);
				for (int i = 0; i < n && i < 6; i++)
				{
					Node c = prop.GetChild(i);
					DumpProcessChain(c, "铲子/道具" + i);
				}
			}
			else
			{
				Info("DIAG[道具/铲子栏] 未找到 UITopPropContainer。");
			}
			// BankUILayer 本身状态
			if (bank != null)
			{
				DumpProcessChain(bank, "BankUILayer");
			}
		}
		catch (Exception ex)
		{
			Warn("DIAG[UI] 异常（已吞）：" + ex.Message);
		}
	}

	/// <summary>按类名（含子类）收集节点，最多 max 个。</summary>
	private static void CollectByClassName(Node node, int depth, string cls, List<Node> outList, int max)
	{
		if (node == null || depth > 40 || outList.Count >= max)
		{
			return;
		}
		try
		{
			Type t = node.GetType();
			string n = t.Name;
			if (n == cls || IsSubclassNamed(t, cls))
			{
				outList.Add(node);
			}
		}
		catch { }
		int c = node.GetChildCount();
		for (int i = 0; i < c; i++)
		{
			CollectByClassName(node.GetChild(i), depth + 1, cls, outList, max);
			if (outList.Count >= max)
			{
				return;
			}
		}
	}

	/// <summary>打印某节点自身 + 全部祖先的 `名称(类型,模式)` 链（最多 12 层）。</summary>
	private void DumpProcessChain(Node node, string tag)
	{
		try
		{
			string chain = "";
			int guard = 0;
			for (Node p = node; p != null && guard++ < 12; p = p.GetParent())
			{
				chain += " / " + p.Name + "(" + p.GetType().Name + "," + p.ProcessMode + ")";
			}
			Info("DIAG[" + tag + "父链] " + chain);
		}
		catch { }
	}

	private bool _diagDumped;

	/// <summary>
	/// ★ v1.0.10 探针：打印种子包本体 Control 及其**每一层祖先**的
	/// `visible / mouseFilter / clip / globalRect`，用来定位"点击到底被哪一层吃掉"。
	/// Godot GUI 命中要求：整条祖先链上不能有 `MouseFilter=Stop` 的中间层遮挡，
	/// 且目标必须在 `globalRect` 内。任何一层 rect 为 0 或位置飘走都会导致点不中。
	/// </summary>
	private void ProbePacketInteractivity()
	{
		try
		{
			var packets = new List<Node>();
			CollectByClassName(_tree.Root, 0, "TowerDefenseInGamePacketShow", packets, 3);
			Info("PROBE[种子包] 共 " + packets.Count + " 个。");
			foreach (Node p in packets)
			{
				ProbeChain(p, "种子包本体");
			}
			// 顺带探测鼠标当前屏幕位置（用于对照 rect）
			Viewport vp = _tree.Root.GetViewport();
			if (vp != null)
			{
				Info("PROBE[鼠标] 屏幕位置=" + vp.GetMousePosition());
			}
			// 探测铲子/道具栏：按名字模糊匹配 "Prop"/"Tool"/"Shovel"
			var props = new List<Node>();
			CollectByNameContains(_tree.Root, 0, new string[] { "Prop", "Tool", "Shovel", "Rake" }, props, 12);
			Info("PROBE[道具类节点] 共 " + props.Count + " 个。");
			foreach (Node n in props)
			{
				ProbeChain(n, "道具类");
			}
		}
		catch (Exception ex)
		{
			Info("PROBE 异常：" + ex.Message);
		}
	}

	/// <summary>按"名字含任一关键词"收集节点（深度优先，最多 max 个）。</summary>
	private static void CollectByNameContains(Node node, int depth, string[] keys, List<Node> outList, int max)
	{
		if (node == null || depth > 40 || outList.Count >= max)
		{
			return;
		}
		try
		{
			string nm = node.Name.ToString();
			string tn = node.GetType().Name;
			foreach (string k in keys)
			{
				if (nm.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0
					|| tn.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
				{
					outList.Add(node);
					break;
				}
			}
		}
		catch { }
		int c = node.GetChildCount();
		for (int i = 0; i < c; i++)
		{
			CollectByNameContains(node.GetChild(i), depth + 1, keys, outList, max);
			if (outList.Count >= max)
			{
				return;
			}
		}
	}

	private void ProbeChain(Node node, string tag)
	{		try
		{
			int guard = 0;
			for (Node p = node; p != null && guard++ < 14; p = p.GetParent())
			{
				string extra = "";
				Control c = p as Control;
				if (c != null && GodotObject.IsInstanceValid(c))
				{
					Rect2 r = c.GetGlobalRect();
					extra = " visible=" + c.IsVisibleInTree()
						+ " mf=" + c.MouseFilter
						+ " rect=(" + r.Position.X.ToString("F0") + "," + r.Position.Y.ToString("F0")
						+ " " + r.Size.X.ToString("F0") + "x" + r.Size.Y.ToString("F0") + ")";
				}
				Node2D nd = p as Node2D;
				if (nd != null && GodotObject.IsInstanceValid(nd))
				{
					extra += " pos2d=" + nd.GlobalPosition;
				}
				Info("PROBE[" + tag + "] " + p.Name + "(" + p.GetType().Name + "," + p.ProcessMode + ")" + extra);
			}
		}
		catch { }
	}

	private void DumpSunAncestors(Node node, int depth, ref int found)
	{
		if (node == null || depth > 40 || found > 6)
		{
			return;
		}
		try
		{
			if (IsSubclassNamed(node.GetType(), "TowerDefenseSunBase") || node.GetType().Name == "TowerDefenseSunBase")
			{
				found++;
				string chain = "";
				int guard = 0;
				for (Node p = node; p != null && guard++ < 12; p = p.GetParent())
				{
					chain += " / " + p.Name + "(" + p.GetType().Name + "," + p.ProcessMode + ")";
				}
				Info("DIAG[阳光父链] " + chain);
			}
		}
		catch { }
		int n = node.GetChildCount();
		for (int i = 0; i < n; i++)
		{
			DumpSunAncestors(node.GetChild(i), depth + 1, ref found);
		}
	}

	private void DumpNodesByClassName(Node node, int depth, string cls, ref int count)
	{
		if (node == null || depth > 40)
		{
			return;
		}
		try
		{
			if (node.GetType().Name == cls)
			{
				count++;
				Info("DIAG[" + cls + "] path=" + SafePath(node) + " mode=" + node.ProcessMode
					+ " childCount=" + node.GetChildCount());
			}
		}
		catch { }
		int n = node.GetChildCount();
		for (int i = 0; i < n; i++)
		{
			DumpNodesByClassName(node.GetChild(i), depth + 1, cls, ref count);
		}
	}

	private static string SafePath(Node node)
	{
		try { return node.GetPath().ToString(); } catch { return "<path?>"; }
	}

	/// <summary>
	/// 精准保活单个目标节点（可选是否递归子节点）。
	/// 会跳过禁区与角色分支；只把 `Inherit`/`Pausable` 改成 `Always`（记原值以便还原）。
	/// 返回改动的节点数。
	/// </summary>
	private int KeepAliveTarget(Node node, bool recurseChildren)
	{
		if (node == null || !GodotObject.IsInstanceValid(node))
		{
			return 0;
		}
		if (_protected.Contains(node) || IsCharacterBranch(node))
		{
			return 0;
		}
		int count = 0;
		try
		{
			Node.ProcessModeEnum cur = node.ProcessMode;
			if (cur == Node.ProcessModeEnum.Inherit || cur == Node.ProcessModeEnum.Pausable)
			{
				_keptAlive.Add((node, cur));
				node.ProcessMode = Node.ProcessModeEnum.Always;
				count++;
			}
		}
		catch { }
		if (recurseChildren)
		{
			int n = node.GetChildCount();
			for (int i = 0; i < n; i++)
			{
				count += KeepAliveTarget(node.GetChild(i), true);
			}
		}
		return count;
	}

	/// <summary>禁区节点集合：CharacterNode 的全部祖先（含自身）+ SceneTree.Root 链。绝不修改。</summary>
	private readonly HashSet<Node> _protected = new HashSet<Node>();

	/// <summary>该节点是否属于"角色/子弹"分支 → 绝不能保活（保活会让时停失效）。</summary>
	private static bool IsCharacterBranch(Node node)
	{
		try
		{
			string nm = node.Name.ToString();
			if (nm == "CharacterLayer" || nm == "CharacterNode"
				|| nm == "ZombieCheckArea" || nm == "CharacterCanvasModulate")
			{
				return true;
			}
			string cn = node.GetType().Name;
			if (cn.IndexOf("Batch", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return true;   // 批处理节点（角色/僵尸/运动/护盾）自带 Always，不能改也不能依赖
			}
			if (cn.IndexOf("Character", StringComparison.OrdinalIgnoreCase) >= 0
				&& cn.IndexOf("Control", StringComparison.OrdinalIgnoreCase) < 0)
			{
				return true;   // 其它 Character 相关节点（排除 TowerDefenseInGameLevelControl 这类）
			}
		}
		catch { }
		return false;
	}

	// ★ v1.0.6：`KeepAliveRecursive` 已删除。
	//   它曾是 v1.0.5 里"泛撒网保活 570 个 UI 节点"的元凶 —— 把 `AnimationPlayer`/
	//   `AnimatedSprite` 等一并设为 `Always`，导致「僵尸/植物停止但动画还在播放」。
	//   现在保活面收敛到只有 `TowerDefenseMapControl` 一个节点（见 `ApplyKeepAlive`），
	//   其余交互全部由旁路节点 `InputRelayNode` 每帧主动驱动。
	//   ⚠️ 若将来确实需要保活某个 UI 节点，请**显式单点**调用 `KeepAliveTarget(node,false)`，
	//     绝不要恢复"从主控根递归整棵树"的做法。

	private void RestoreKeepAlive()
	{
		// ★ 先摘掉旁路驱动节点（否则解除时停后它仍会驱动输入 → 双份输入）
		RemoveRelay();
		for (int i = _keptAlive.Count - 1; i >= 0; i--)
		{
			try
			{
				(Node node, Node.ProcessModeEnum oldMode) = _keptAlive[i];
				if (node != null && GodotObject.IsInstanceValid(node))
				{
					node.ProcessMode = oldMode;
				}
			}
			catch { }
		}
		_keptAlive.Clear();
		_keptAliveSet.Clear();
	}

	// ================================================================ 工具

	private static Node FindNodeByClassName(Node node, string className)
	{
		if (node == null)
		{
			return null;
		}
		try
		{
			Type t = node.GetType();
			if (t.Name == className || IsSubclassNamed(t, className))
			{
				return node;
			}
		}
		catch { }
		int n = node.GetChildCount();
		for (int i = 0; i < n; i++)
		{
			Node r = FindNodeByClassName(node.GetChild(i), className);
			if (r != null)
			{
				return r;
			}
		}
		return null;
	}

	/// <summary>t 是否（直接或间接）继承自名字叫 name 的类型。</summary>
	private static bool IsSubclassNamed(Type t, string name)
	{
		try
		{
			Type b = t.BaseType;
			int guard = 0;
			while (b != null && guard++ < 32)
			{
				if (b.Name == name)
				{
					return true;
				}
				b = b.BaseType;
			}
		}
		catch { }
		return false;
	}

	private static FieldInfo FindFieldAlong(Type t, string name)
	{
		try
		{
			Type cur = t;
			int guard = 0;
			while (cur != null && guard++ < 32)
			{
				FieldInfo fi = cur.GetField(name,
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (fi != null)
				{
					return fi;
				}
				cur = cur.BaseType;
			}
		}
		catch { }
		return null;
	}

	// ================================================================ 日志

	private void Info(string msg)
	{
		if (!EnableInfoLog)
		{
			return;
		}
		try { if (_context != null) { _context.Log(LogPrefix + msg); } else { GD.Print(LogPrefix + msg); } }
		catch { }
	}

	private void Warn(string msg)
	{
		try { if (_context != null) { _context.Warn(LogPrefix + msg); } else { GD.PrintErr(LogPrefix + msg); } }
		catch { }
	}

	// ================================================================ 时停期间：加农炮瞄准 / 开火（v1.0.30）
	//
	// ── 需求 ────────────────────────────────────────────────────────
	// 时停期间允许"点击加农炮 → 选择发射地点"的操作。
	//
	// ── 根因（读源码定案）───────────────────────────────────────────
	// 加农炮的点击链路是 `MousePressComponent`（Marker/Line 两种模式）：
	//   · 事件入口：`TowerDefenseCharacter._Input(e)` → `componentManager.DispatchRuntimeInput(e)`
	//     → 各组件 `ProcessInput(e)`（`MousePressComponent.ProcessInput` 是 internal）。
	//   · **暂停时 `TowerDefenseCharacter`（Pausable）收不到 `_Input`** ⇒ 炮点不了。
	//   · 组件侧的门控 `CanInteract()` 只要求 `IsGameRunning()`（= `currentControl.isGameRunning`，
	//     **暂停不影响它**）+ `_inputReady` + `inGame` + 组件 `Alive`（= `canFire`，装填好了才 true），
	//     **都不受暂停影响** ⇒ 只要有人把事件送进来，炮就能在时停里正常瞄准/开火。
	//   · Marker 模式（玉米加农炮这类）是 **ToggleMode 两段式**：第 1 次按下进入瞄准
	//     （`ToggleTarget` 瞄准标记跟着指针走，纯设坐标，暂停下也有效），第 2 次按下
	//     `FinishAim` → `OnFinishPressed` → `CannonComponent.FireAt(pos)`。
	//     ⚠️ `FireAt` 只置状态/发状态机事件，炮弹真正飞出要等状态机动画推进
	//        ⇒ **解除时停后炮弹才发射**（这是引擎的暂停语义，非本 Mod 能绕过）。
	//
	// ── 实现 ────────────────────────────────────────────────────────
	// 窗口级输入中继：`Window.WindowInput` 在 **暂停时照样发**（窗口层不受节点 ProcessMode 门控）。
	// 时停开启时，把"左键按下/抬起、触摸按下/抬起、拖动、鼠标移动"原样转发给所有
	// `MousePressComponent.ProcessInput`（internal ⇒ 反射调用；命中判定由组件自己做，
	// 与正常游玩完全同一条代码路径）。非时停状态直接返回（游戏自己的 `_Input` 在处理，避免双份）。
	//
	// ⚠️ 事件坐标：`WindowInput` 给的是**窗口像素**坐标，而组件在正常游玩时收到的是
	//    `Viewport`（内容缩放后）坐标 —— 用 `Root.GetFinalTransform().AffineInverse()` 转换
	//    （这正是 Godot `Viewport::push_input` 内部做的同一步变换）。
	//
	// ⚠️ 自家按钮防误触：点「时停」按钮时不能顺手指挥加农炮 —— 转发前先做矩形排除。

	private bool _windowInputHooked;

	/// <summary>`MousePressComponent.ProcessInput`（internal ⇒ 反射缓存）。</summary>
	private System.Reflection.MethodInfo _mpiMousePressInput;

	/// <summary>角色列表缓存（同一帧内复用；输入事件频率高于帧率）。</summary>
	private Godot.Collections.Array _cachedCharacters;
	private long _cachedCharFrame;

	private void HookWindowInput()
	{
		try
		{
			if (_windowInputHooked)
			{
				return;
			}
			Window root = (_tree != null && GodotObject.IsInstanceValid(_tree)) ? _tree.Root : null;
			if (root == null)
			{
				return;
			}
			root.WindowInput += OnWindowInputForComponents;
			_windowInputHooked = true;
		}
		catch (Exception ex)
		{
			Warn("挂载窗口输入中继异常（已吞）：" + ex.Message);
		}
	}

	private void UnhookWindowInput()
	{
		try
		{
			if (!_windowInputHooked)
			{
				return;
			}
			Window root = (_tree != null && GodotObject.IsInstanceValid(_tree)) ? _tree.Root : null;
			if (root != null)
			{
				root.WindowInput -= OnWindowInputForComponents;
			}
			_windowInputHooked = false;
		}
		catch (Exception ex)
		{
			Warn("卸载窗口输入中继异常（已吞）：" + ex.Message);
		}
	}

	/// <summary>窗口坐标 → 视口（内容缩放后）坐标。</summary>
	private Vector2 WindowToViewport(Vector2 windowPos)
	{
		try
		{
			Window root = (_tree != null && GodotObject.IsInstanceValid(_tree)) ? _tree.Root : null;
			if (root == null)
			{
				return windowPos;
			}
			return root.GetFinalTransform().AffineInverse() * windowPos;
		}
		catch
		{
			return windowPos;
		}
	}

	/// <summary>这次点击是否落在自家「时停」按钮上（是则不转发，避免顺手开炮）。</summary>
	private bool IsOverModButton(Vector2 viewportPos)
	{
		try
		{
			for (int i = 0; i < 2; i++)
			{
				Control c = (i == 0) ? _buttonHost : _button;
				if (c == null || !GodotObject.IsInstanceValid(c) || !c.Visible)
				{
					continue;
				}
				Rect2 r = new Rect2(c.GetGlobalPosition(), c.Size);
				if (r.HasPoint(viewportPos))
				{
					return true;
				}
			}
		}
		catch { }
		return false;
	}

	private void OnWindowInputForComponents(InputEvent ev)
	{
		try
		{
			if (!_timeStopOn || ev == null || ev.IsEcho())
			{
				return;                    // 非时停 ⇒ 游戏自己的 `_Input` 在处理
			}

			InputEvent forwarded = null;
			if (ev is InputEventScreenTouch t)
			{
				var e2 = new InputEventScreenTouch();
				e2.Index = t.Index;
				e2.Pressed = t.Pressed;
				e2.Position = WindowToViewport(t.Position);
				e2.DoubleTap = t.DoubleTap;
				forwarded = e2;
			}
			else if (ev is InputEventScreenDrag d)
			{
				var e2 = new InputEventScreenDrag();
				e2.Index = d.Index;
				e2.Position = WindowToViewport(d.Position);
				e2.Relative = d.Relative;      // 相对量不做平移变换（组件仅用于惯性参考）
				e2.Velocity = d.Velocity;
				forwarded = e2;
			}
			else if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
			{
				var e2 = new InputEventMouseButton();
				e2.ButtonIndex = mb.ButtonIndex;
				e2.Pressed = mb.Pressed;
				e2.Position = WindowToViewport(mb.Position);
				e2.GlobalPosition = e2.Position;
				e2.DoubleClick = false;
				forwarded = e2;
			}
			else if (ev is InputEventMouseMotion mm)
			{
				var e2 = new InputEventMouseMotion();
				e2.Position = WindowToViewport(mm.Position);
				e2.GlobalPosition = e2.Position;
				e2.Relative = mm.Relative;
				forwarded = e2;
			}
			if (forwarded == null)
			{
				return;
			}

			Vector2 vpPos = (forwarded is InputEventMouse me) ? me.Position
				: (forwarded is InputEventScreenTouch st) ? st.Position
				: (forwarded as InputEventScreenDrag)?.Position ?? Vector2.Zero;
			if (IsOverModButton(vpPos))
			{
				return;                    // 点的是自家按钮 ⇒ 不指挥加农炮
			}
			ForwardToMousePressComponents(forwarded);
		}
		catch (Exception ex)
		{
			if (_diagCannonRelay < 3)
			{
				_diagCannonRelay++;
				Warn("加农炮输入中继异常：" + ex.Message);
			}
		}
	}

	private int _diagCannonRelay;

	/// <summary>把事件转发给场上所有 `MousePressComponent`（命中判定组件自己做）。</summary>
	private void ForwardToMousePressComponents(InputEvent ev)
	{
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return;
			}
			if (_mpiMousePressInput == null)
			{
				_mpiMousePressInput = typeof(MousePressComponent).GetMethod(
					"ProcessInput",
					System.Reflection.BindingFlags.NonPublic
					| System.Reflection.BindingFlags.Instance
					| System.Reflection.BindingFlags.DeclaredOnly);
				if (_mpiMousePressInput == null)
				{
					Warn("反射拿不到 MousePressComponent.ProcessInput（游戏更新了？）");
					return;
				}
			}

			// 角色列表按帧缓存（鼠标移动事件频率高于帧率，避免每次都重新收集）
			// ⚠️ `SceneTree.GetFrame()` 在本版本返回 long，不是 ulong
			long frame = (_tree != null && GodotObject.IsInstanceValid(_tree)) ? _tree.GetFrame() : 0;
			if (_cachedCharacters == null || _cachedCharFrame != frame)
			{
				_cachedCharacters = mgr.GetCharacter();
				_cachedCharFrame = frame;
			}
			if (_cachedCharacters == null)
			{
				return;
			}

			object[] args = new object[] { ev };
			foreach (Variant item in _cachedCharacters)
			{
				if (!(item.AsGodotObject() is TowerDefenseCharacter ch)
					|| !GodotObject.IsInstanceValid(ch) || !ch.inGame)
				{
					continue;
				}
				ComponentManager cm = ch.componentManager;
				if (cm == null || !GodotObject.IsInstanceValid(cm))
				{
					continue;
				}
				MousePressComponent mpc = cm.GetRuntime<MousePressComponent>();
				// ⚠️ CharacterComponentRuntime 不是 GodotObject，只能用 IsReleased 判有效性
				if (mpc == null || mpc.IsReleased)
				{
					continue;
				}
				try
				{
					_mpiMousePressInput.Invoke(mpc, args);
				}
				catch (Exception ex)
				{
					if (_diagCannonRelay < 6)
					{
						_diagCannonRelay++;
						Warn("转发组件输入失败：" + ex.Message);
					}
				}
			}
		}
		catch (Exception ex)
		{
			if (_diagCannonRelay < 9)
			{
				_diagCannonRelay++;
				Warn("收集组件异常：" + ex.Message);
			}
		}
	}
}
