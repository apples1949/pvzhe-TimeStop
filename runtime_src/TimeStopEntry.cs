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
	private bool _started;
	private long _frame;
	private bool _faultReported;

	/// <summary>时停是否处于激活状态（用户点开）。</summary>
	private bool _timeStopOn;

	/// <summary>自建的按钮 Label（找到齿轮后创建；齿轮重建则重新创建）。</summary>
	private Label _button;

	/// <summary>承载按钮的 PanelContainer（画外边框用）。</summary>
	private PanelContainer _buttonHost;

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
			_connected = true;
			_started = true;
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
			if (_connected && _tree != null && GodotObject.IsInstanceValid(_tree))
			{
				_tree.Disconnect("process_frame", _tickCallable);
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
			if (!Input.IsMouseButtonPressed(MouseButton.Left))
			{
				return;
			}
			Node charNode = FindCharacterNode();
			if (charNode == null)
			{
				return;
			}
			// ⚠️ 坐标必须用 **Node2D 的世界坐标**（`Node2D.GetGlobalMousePosition()` = 视口鼠标位置
			//   经 canvas transform 逆变换，含相机偏移）。用 `GetViewport().GetMousePosition()`
			//   （纯视口坐标）在相机移动/`CharacterLayer.follow_viewport_enabled=true` 时会偏，
			//   导致 `IsPointInCircle` 永远判不中 ⇒ 收不到阳光。
			Node2D ref2d = charNode as Node2D;
			Vector2 mouse = (ref2d != null)
				? ref2d.GetGlobalMousePosition()
				: (charNode.GetViewport()?.GetMousePosition() ?? Vector2.Zero);
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
			// 每帧无条件清零两个防抖位（见上：它们是"暂停冻结"的）
			ClearDebounceStates();

			if (!Input.IsMouseButtonPressed(MouseButton.Left))
			{
				return;
			}
			long f = _tree.GetFrame();
			if (_lastPickClickFrame == f)
			{
				return;
			}
			Viewport vp = _tree.Root.GetViewport();
			if (vp == null)
			{
				return;
			}
			Vector2 vpMouse = vp.GetMousePosition();
			bool diag = _pickClickDiag++ < 20;

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
					bool hit = HitControlInCanvas(sb, vpMouse);
					if (diag)
					{
						Info("PICK[" + _pickClickDiag + "] 鼠标=" + vpMouse + " 铲子rect=" + sb.GetGlobalRect() + " 命中=" + hit);
					}
					if (hit)
					{
						_lastPickClickFrame = f;
						SetMember(shovelMgr, "shovelPressedAwait", false);
						InvokeMethod(shovelMgr, "ShovelButtonPressed");
						Info("★ 旁路：命中铲子按钮，已调用 ShovelButtonPressed()。");
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
					bool hit = HitControlInCanvas(target, vpMouse);
					if (diag)
					{
						Info("PICK[" + _pickClickDiag + "] 鼠标=" + vpMouse + " 种子包(" + p.Name
							+ ") rect=" + target.GetGlobalRect() + " 命中=" + hit);
					}
					if (!hit)
					{
						continue;
					}
					_lastPickClickFrame = f;
					SetMember(p, "_pressDelayTimer", 0.0);
					SetMember(p, "pressDelayTimer", 0.0);
					InvokeMethod(p, "Pressed");
					Info("★ 旁路：命中种子包，已调用 Pressed()。select=" + GetBoolMember(p, "select"));
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
	/// ★ 命中判定。
	///
	/// Godot 4 里 `Control.GetGlobalRect()` 返回的坐标**已经过祖先 transform 与所在
	/// CanvasLayer 的 canvas transform**，与 `Viewport.GetMousePosition()`（窗口/视口坐标）
	/// **在同一坐标系**，可直接比较。（`CanvasLayer` 自身没有 `GetCanvasTransform()` 方法，
	/// canvas 变换由 viewport 内部应用在 `CanvasItem` 的 global transform 上。）
	/// </summary>
	private static bool HitControlInCanvas(Control ctl, Vector2 viewportMouse)
	{
		try
		{
			if (ctl == null || !GodotObject.IsInstanceValid(ctl))
			{
				return false;
			}
			return ctl.GetGlobalRect().HasPoint(viewportMouse);
		}
		catch
		{
			return false;
		}
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
				Info("收集诊断：节点 " + SafePath(node) + " 的 sprite 字段取不到（"
					+ (IsSubclassNamed(t, "TowerDefenseSunBase") ? "_sprite" : "spriteNode") + "）。");
			}
			return;
		}
		float dist = mouse.DistanceTo(sprite.GlobalPosition);
		// ★ 诊断：把每次判定的数值打出来（只在前若干次），定位"为什么收不到"
		if (_collectDiagCount < 40 && Input.IsMouseButtonPressed(MouseButton.Left))
		{
			_collectDiagCount++;
			Info("收集诊断[" + _collectDiagCount + "] 类型=" + t.Name
				+ " 鼠标=" + mouse + " 精灵=" + sprite.GlobalPosition
				+ " 距离=" + dist.ToString("F1") + " 半径=" + radius.ToString("F1")
				+ " 命中=" + (dist <= radius));
		}
		if (!Geometry2D.IsPointInCircle(mouse, sprite.GlobalPosition, radius))
		{
			return;
		}
		// ★ 调 public Collection()
		InvokeMethod(node, "Collection");
		Info("★ 已主动收集：" + t.Name + " 距离=" + dist.ToString("F1"));
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
			FieldInfo f = target.GetType().GetField(fieldName,
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			if (f == null)
			{
				return false;
			}
			f.SetValue(target, value);
			return true;
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
			Type t = target.GetType();
			FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			if (f != null)
			{
				return f.GetValue(target);
			}
			PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			if (p != null)
			{
				return p.GetValue(target);
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
		}
		catch { }
	}

	/// <summary>鼠标左键是否处于按下（用于轮询去重）。</summary>
	private bool _pollDown;

	/// <summary>最近一次触发点击的帧号（`OnButtonInput` 与 `PollButtonClick` 去重用）。</summary>
	private long _lastClickFrame = long.MinValue;

	/// <summary>找到定位基准（加速按钮，回退齿轮）并把「时停」放到它下方。</summary>
	private void EnsureButton(Node root)
	{
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
		CreateButtonNear(anchorBtn);
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
			if (_button != null && GodotObject.IsInstanceValid(_button))
			{
				_button.AddThemeColorOverride("font_color", fg);
			}
			if (_buttonHost != null && GodotObject.IsInstanceValid(_buttonHost))
			{
				StyleBoxFlat sb = _buttonHost.GetThemeStylebox("panel") as StyleBoxFlat;
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
		if (_timeStopOn)
		{
			Disengage("用户点击关闭");
		}
		else
		{
			Engage();
		}
		UpdateButtonVisual();
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
}
