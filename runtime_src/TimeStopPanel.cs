using System;
using System.Collections.Generic;
using Godot;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// 「时停」Mod —— ★ v1.1.0 新增：**16 行卡槽触发面板**（本文件 = TimeStopEntry 的 partial 部分）。
///
/// ── 需求（2026-10-12 用户）────────────────────────────────────────
/// 1. 面板里固定 **16 行**，每行 = 一个卡槽：显示卡槽序号 + 该卡槽植物的名称；
///    每行**右侧一个复选框**。
/// 2. **勾选**的卡槽，其「种植 CD 结束」时**自动触发时停**。
/// 3. 解除方式不变：点「时停」按钮 或 按快捷键（复用既有 Toggle 通道）。
/// 4. 面板自己的**呼出快捷键**走**既有机制**：mod.json 的 enum 下拉 + 配置页
///    「录制快捷键」按钮（KeybindPanelManager，锚 = Reset_panelHotkey）。
/// 5. 若**没有**设面板快捷键，则在**战斗设置页**（`BattleOption`，即设置植物/僵尸血条那一页）
///    注入一个「时停 · 卡槽触发面板」按钮作为兜底入口。
///
/// ── 为什么"种植 CD 结束"要这么判定（判据全部来自解包源码）──────────
/// `TowerDefenseInGamePacketShow._PhysicsProcess`（L1599-1627）：
///     if (start | flag) { if (!flag &amp;&amp; coldDownOpen) {
///         coldDownTimer = Math.Max(0.0, coldDownTimer - delta);
///         if (coldDownTimer &lt;= 0.0) { coldDownOpen = false;
///             XWModCards.Notify(this, "CooldownCompleted"); } } }
/// ⇒ 冷却中 = `coldDownOpen`（等价于 `coldDownTimer &gt; 0`）；卡片被使用后
///   `coldDownTimer = coldDown`（L2445）重新拉满 ⇒ **"就绪→冷却"这个边沿 = 玩家真的种下了它**。
/// 因此本面板只用**两个边沿**，不采信"当前恰好就绪"这种静态判断：
///   · **就绪 → 冷却**：卡被使用 ⇒ 该槽进入"待命"（armed）；
///   · **冷却 → 就绪** 且已待命：CD 走完 ⇒ 触发时停。
/// 这样处理掉了两个坑：
///   ① 关卡开局自带 `startingCooldown`（卡片一进场就是冷却态）——没有"就绪→冷却"边沿，
///      不会在开局莫名触发；换关时把上次观测状态**预置为冷却**即可屏蔽。
///   ② 时停期间 CD 不推进（树被暂停）——解除时停后第一帧只做状态重同步、不做边沿判定。
///
/// ── 铁律遵守 ──────────────────────────────────────────────────────
/// · 本文件**不定义任何自己的 Node 子类**（无源码生成器 ⇒ `_Process`/`_Input` 不会被调用）。
///   全部逻辑由 TimeStopEntry 既有的 `process_frame` / `physics_frame` 信号回调驱动。
/// · 面板只用引擎内置控件；`CheckBox.Toggled` / `Button.Pressed` 是引擎信号，运行时连接有效。
/// · 所有回调一律 try/catch 吞异常（抛出 = 整包回滚）。
/// · ⚠️ 复选框**只走原生 GUI 派发**（ProcessMode=Always），**不再叠加鼠标轮询** ——
///   两条路径会各触发一次，表现为"点一下跳两下"（本 Mod 已在自绘按钮上踩过这个坑）。
/// </summary>
public sealed partial class TimeStopEntry
{
	// ================================================================ 常量

	/// <summary>面板行数（= 卡槽数上限；游戏 `LEVEL_SEEDBANK_METHOD.PRESET` 用满 16 槽）。</summary>
	private const int SlotCount = 16;

	/// <summary>面板快捷键的配置键（mod.json 里声明的 enum 项）。</summary>
	private const string KPanelHotkey = "panelHotkey";

	/// <summary>
	/// ★ v1.1.4（用户要求）：面板滑动条「自动解除时停」的上限秒数（60 → 15）。
	/// 面板与配置页（mod.json 的 autoResumeSec.max）必须保持一致。
	/// </summary>
	private const int AutoResumeMaxSec = 15;

	/// <summary>★ v1.1.4：配置键 —— 读取新关卡（含重打当前关卡）时是否清除所有植物的选择。</summary>
	private const string KClearOnNewLevel = "clearTriggersOnNewLevel";

	/// <summary>勾选状态持久化文件（Godot user:// = %APPDATA%\Godot\app_userdata\植物大战僵尸杂交版\）。</summary>
	private const string PanelCfgPath = "user://TimeStop.cfg";

	private const string PanelLayerName = "ModTimeStopPanelLayer";
	private const string PanelRootName = "ModTimeStopPanel";
	private const string OptBtnName = "ModTimeStopPanelButton";

	/// <summary>
	/// ★ 2026-10-27（用户要求）：本 Mod **独占**的「按钮位置」面板描述
	/// （时停与加速各一个面板、各一个入口按钮；面板节点名由 `Id` 派生）。
	/// </summary>
	private static readonly PosEditorRow.PanelSpec PosSpec =
		new PosEditorRow.PanelSpec("timestop", "时停按钮 · 位置与样式", "时停·按钮位置");

	// ================================================================ 状态

	private bool _panelBuilt;
	private bool _panelVisible;
	private CanvasLayer _panelLayer;
	private Control _panelRoot;
	private Label _panelStatus;
	private readonly List<Label> _rowNameLbl = new List<Label>(SlotCount);
	private readonly List<Label> _rowCdLbl = new List<Label>(SlotCount);
	private readonly List<CheckBox> _rowCb = new List<CheckBox>(SlotCount);

	/// <summary>每槽是否启用"CD 结束触发时停"（持久化到 user://TimeStop.cfg）。</summary>
	private readonly bool[] _trigOn = new bool[SlotCount];

	/// <summary>每槽是否"待命"（该卡已被用掉、CD 走完就触发时停）。</summary>
	private readonly bool[] _trigArmed = new bool[SlotCount];

	/// <summary>每槽的"开场保护"：换关后置真，直到该槽出现过"就绪"才允许 arm（吃掉 startingCooldown）。</summary>
	private readonly bool[] _armBlocked = new bool[SlotCount];

	/// <summary>上一帧观测到的"是否冷却中"（诊断/兜底用）。</summary>
	private readonly bool[] _prevCooling = new bool[SlotCount];

	/// <summary>每槽当前卡片实例 id（换卡即清 armed，避免旧卡的待命状态串到新卡）。</summary>
	private readonly ulong[] _slotCardId = new ulong[SlotCount];

	/// <summary>当前卡槽实例 id（换关/换卡槽实例 ⇒ 重置观测状态）。</summary>
	private ulong _watchBankId;

	private ulong _lastFireMs;
	private int _lastFireSlot = -1;
	private ulong _lastRowRefreshMs;
	private Theme _panelTheme;
	private bool _panelCfgLoaded;
	private bool _panelFaultReported;
	private bool _optBtnFaultReported;
	private string _optBtnHotkeySnapshot = "\u0001";   // 非空初值 = "还不知道"，首次一定同步一次

	// ================================================================ 生命周期钩子（由 TimeStopEntry 调用）

	/// <summary>加载勾选状态（Initialize / OnAllModsLoaded 各调一次，幂等）。</summary>
	private void LoadPanelCfg()
	{
		try
		{
			if (_panelCfgLoaded)
			{
				return;
			}
			_panelCfgLoaded = true;
			ConfigFile cf = new ConfigFile();
			if (cf.Load(PanelCfgPath) == Error.Ok)
			{
				for (int i = 0; i < SlotCount; i++)
				{
					object v = cf.GetValue("trigger", "slot" + i, false);
					_trigOn[i] = (v is bool b) && b;
				}
				Info("已读取卡槽触发配置：" + DescribeTriggerSlots() + "（" + PanelCfgPath + "）");
			}
			else
			{
				Info("无卡槽触发配置（首次运行），16 槽默认全部不勾选。");
			}
		}
		catch (Exception ex)
		{
			Warn("读取卡槽触发配置失败（用默认值）：" + ex.Message);
		}
	}

	/// <summary>保存勾选状态。</summary>
	private void SavePanelCfg()
	{
		try
		{
			ConfigFile cf = new ConfigFile();
			for (int i = 0; i < SlotCount; i++)
			{
				cf.SetValue("trigger", "slot" + i, _trigOn[i]);
			}
			Error e = cf.Save(PanelCfgPath);
			if (e != Error.Ok)
			{
				Warn("保存卡槽触发配置失败：" + e);
			}
		}
		catch (Exception ex)
		{
			Warn("保存卡槽触发配置异常：" + ex.Message);
		}
	}

	private string DescribeTriggerSlots()
	{
		var sb = new System.Text.StringBuilder();
		for (int i = 0; i < SlotCount; i++)
		{
			if (!_trigOn[i])
			{
				continue;
			}
			if (sb.Length > 0)
			{
				sb.Append(',');
			}
			sb.Append(i + 1);
		}
		return (sb.Length == 0) ? "（无勾选）" : ("槽 " + sb);
	}

	/// <summary>Shutdown 时收尾：销毁面板，避免残留控件。</summary>
	private void ShutdownPanel()
	{
		try
		{
			if (_panelLayer != null && GodotObject.IsInstanceValid(_panelLayer))
			{
				_panelLayer.QueueFree();
			}
		}
		catch { }
		_panelLayer = null;
		_panelRoot = null;
		_panelBuilt = false;
		_panelVisible = false;
		_rowNameLbl.Clear();
		_rowCdLbl.Clear();
		_rowCb.Clear();
	}

	// ================================================================ 每帧入口（由 OnProcessFrame / OnHotkeyTick 调用）

	/// <summary>
	/// 面板主逻辑（每帧；由 `OnProcessFrame` 调用）。
	/// 顺序：① 关卡内才允许存在 ② 保证控件 ③ 面板快捷键 ④ 卡槽触发看门狗。
	/// </summary>
	private void PanelTick(Node root)
	{
		try
		{
			if (root == null || !GodotObject.IsInstanceValid(root))
			{
				return;
			}
			ulong now = Time.GetTicksMsec();
			// ⚠️ 性能：`IsInBattle` 内部是**全树递归 + 逐节点反射类型名**（FindNodeByClassName），
			//   放在每帧路径上会明显加开销（本 Mod 的用途之一就是排查性能，自己更不能成为负担）。
			//   0.5s 判一次并缓存；面板显隐/建控件的延迟最多 0.5 秒，体感无差别。
			if (_panelBattleCheckMs == 0UL || now - _panelBattleCheckMs >= 500UL)
			{
				_panelBattleCheckMs = now;
				_panelBattleCached = IsInBattle(root);
			}
			if (!_panelBattleCached)
			{
				// 离开关卡：面板必须消失，否则会飘在主菜单/地图界面上
				if (_panelVisible)
				{
					SetPanelVisible(false);
				}
				return;
			}
			EnsurePanel(root);
			PanelHotkeyTick();
			WatchSlotTriggers();
			// 行内容刷新（0.25s 一次即可；16 行 × 每行几次字段读，开销可忽略）
			if (_panelVisible && (now - _lastRowRefreshMs >= 250UL || _lastRowRefreshMs == 0UL))
			{
				_lastRowRefreshMs = now;
				RefreshPanelRows();
			}
		}
		catch (Exception ex)
		{
			if (!_panelFaultReported)
			{
				_panelFaultReported = true;
				Warn("面板 tick 异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	/// <summary>"是否在关卡内"的缓存（0.5s 刷新一次；理由见 PanelTick）。</summary>
	private bool _panelBattleCached;
	private ulong _panelBattleCheckMs;

	/// <summary>战斗设置页（`BattleOption`）兜底按钮注入（每轮扫描调用一次，幂等）。</summary>
	private void TryInjectOptionButton(Node node)
	{
		try
		{
			if (node == null || !GodotObject.IsInstanceValid(node))
			{
				return;
			}
			if (!node.Name.ToString().StartsWith("BattleOption", StringComparison.Ordinal))
			{
				return;
			}
			// ★★ 2026-10-13（用户需求 3）：**不再塞进游戏原生那一列** ——
			//   原来 `ResolveOptionList` 优先返回 `ModCol1` 里的原生列表，于是本按钮被追加到
			//   「返回」下面，看起来像游戏自带的选项，还把原生列撑长了。
			//   现在改为在**最右侧新起一列**（共享组件负责建列/列序/多 Mod 兼容）。
			VBoxContainer list = ResolveTimeStopColumn(node);
			if (list == null || !GodotObject.IsInstanceValid(list))
			{
				return;   // 对话框还没初始化完，下轮扫描再试
			}
			// ★ 需求 4+6：顺手在设置页放一行「时停按钮 · 位置」编辑器
			//   （预设 + 绝对坐标 + 范围提示；改完立即写配置并当帧套用）
			EnsurePosEditor(node);
			// 需求：**没设面板快捷键**时才需要这个兜底入口；设了就隐藏它
			bool need = string.IsNullOrWhiteSpace(ReadPanelHotkey());
			Button exist = list.FindChild(OptBtnName, true, false) as Button;
			if (exist != null && GodotObject.IsInstanceValid(exist))
			{
				if (exist.Visible != need)
				{
					exist.Visible = need;
				}
				return;
			}
			if (!need)
			{
				return;
			}
			Button b = new Button();
			b.Name = OptBtnName;
			b.Text = "时停 · 卡槽触发面板";
			b.TooltipText = "打开「时停」的 16 卡槽触发面板（勾选卡槽：其种植 CD 结束自动时停）";
			b.AddThemeFontSizeOverride("font_size", 14);
			b.CustomMinimumSize = new Vector2(0f, 40f);   // 宽不设死：跟着列宽（不 Expand，不撑整页）
			b.ProcessMode = Node.ProcessModeEnum.Always;   // 对话框本身在暂停树下也能点
			BattleOptionColumns.ApplyDarkButtonStyle(b);   // ★ 用户要求：按钮颜色加深
			b.Pressed += () =>
			{
				try
				{
					bool willShow = !_panelVisible;
					SetPanelVisible(willShow);
				// ★ 2026-10-13（用户实测"时停面板关闭后还是没解除暂停"）：打开面板时顺手收起
				//   战斗设置页 —— 那一页自己也把 Paused 压着，不收起来的话关闭面板后游戏仍是暂停的。
				if (willShow) { try { DismissOptionDialog(node); } catch { } }
					// ★ v1.1.1（用户实测反馈）：从战斗设置页打开本面板时，**设置页必须自己关掉** ——
					//   否则那一页一直叠在战场上（它还带 `pasue = true`，游戏保持暂停），
					//   玩家看着像"点了没反应/面板没出来"。
					if (willShow)
					{
						DismissOptionDialog(list);
					}
				}
				catch { }
			};
			list.AddChild(b, false, Node.InternalMode.Disabled);
			Info("已在战斗设置页注入「时停 · 卡槽触发面板」按钮（因为未设置面板快捷键）。");
		}
		catch (Exception ex)
		{
			if (!_optBtnFaultReported)
			{
				_optBtnFaultReported = true;
				Warn("设置页按钮注入异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	/// <summary>
	/// ★ v1.1.1：关掉宿主那个设置对话框（`BattleOption`）。
	///
	/// 为什么：本 Mod 的入口按钮就注入在 `BattleOption` 里 —— 玩家点它打开时停面板后，
	/// 设置页仍留着（`BattleOption.tscn` 的 `pasue = true` 会让游戏一直暂停），
	/// 表现为"打开了面板但那一页不取消、战场也被挡着"。
	///
	/// 关闭路径全部来自解包源码（不猜信号名）：
	///   `BattleOption.cs:59`  `GetNode&lt;NinePatchButtonBase&gt;("CenterContainer/VBoxContainer/BackButton").OnPressed += BackButtonPressed;`
	///   `BattleOption.cs:133` `public void BackButtonPressed() { CloseDialog(); }`
	///   ⚠️ `NinePatchButtonBase.OnPressed` 是**自定义 C# 事件**（内部由 `node.Pressed += _Pressed` 触发），
	///      所以 `EmitSignal("pressed")` 对外层按钮**无效** ⇒ 直接调公开方法最可靠。
	/// 三级兜底：BackButtonPressed() → CloseDialog() → BackButton._Pressed()。
	/// </summary>
	private void DismissOptionDialog(Node from)
	{
		try
		{
			Node dlg = null;
			Node cur = from;
			for (int i = 0; i < 10 && cur != null && GodotObject.IsInstanceValid(cur); i++)
			{
				if (cur.Name.ToString().StartsWith("BattleOption", StringComparison.Ordinal))
				{
					dlg = cur;
					break;
				}
				cur = cur.GetParent();
			}
			if (dlg == null)
			{
				return;
			}
			try
			{
				if (dlg is BattleOption bo)
				{
					bo.BackButtonPressed();
					Info("时停面板已打开 ⇒ 自动关闭战斗设置页（等价于按它的「返回」）。");
					return;
				}
			}
			catch { }
			try
			{
				if (dlg is DialogBoxBase db)
				{
					db.CloseDialog();
					Info("时停面板已打开 ⇒ 自动关闭对话框（CloseDialog）。");
					return;
				}
			}
			catch { }
			try
			{
				Node back = dlg.FindChild("BackButton", true, false);
				if (back is NinePatchButtonBase npb)
				{
					npb._Pressed();
					Info("时停面板已打开 ⇒ 自动按「返回」（_Pressed 兜底）。");
				}
			}
			catch { }
		}
		catch { }
	}

	/// <summary>
	/// ★ 2026-10-13（需求 3）：取/建**本 Mod 独占的最右列**（`ModCol4`）。
	///
	/// 为什么不再用 `ResolveOptionList`：那个方法优先返回 `ModCol1` 里的**游戏原生列表**，
	/// 于是按钮被追加到「返回」下方、和游戏自带选项挤在同一列 —— 用户明确要求
	/// "不与游戏原生的设置一列，在右侧新起一列"。列本身由共享组件 `BattleOptionColumns`
	/// 创建并统一排序（多 Mod 兼容：谁先建谁建、后到者复用）。
	///
	/// **太宽就兜底**：给 4+ 列留出的总宽若超过视口 95%，就不再新起一列，
	/// 而是并到当前最右那一列（总好过把整页挤出屏幕）。
	/// </summary>
	/// <summary>
	/// ★ 需求 4+6：在战斗设置页放「时停按钮 · 位置」编辑器（共享组件 PosEditorRow）。
	/// 编辑的是**战斗 HUD 里那个「时停」开关按钮**的位置（setting: positionMode/customX/customY）。
	/// 幂等：同一个对话框重复调用只是刷新数值。
	/// </summary>
	private void EnsurePosEditor(Node optionRoot)
	{
		try
		{
			float bw = 72f, bh = 32f;
			try
			{
				if (_buttonHost != null && GodotObject.IsInstanceValid(_buttonHost))
				{
					if (_buttonHost.Size.X > 1f) { bw = _buttonHost.Size.X; }
					if (_buttonHost.Size.Y > 1f) { bh = _buttonHost.Size.Y; }
				}
			}
			catch { }
			// ★ 2026-10-27（用户要求）：时停**独占**一个面板 + 一个入口按钮（与加速互不干扰）
			PosEditorRow.EnsureStyleRow(optionRoot, PosSpec, "timestopstyle", "时停样式",
				new string[] { "1", "2" },
				new string[] { "1 · 绿框（默认）", "2 · 复选框" },
				ReadStyleSetting(),
				ApplyStyleFromEditor);
			PosEditorRow.Ensure(optionRoot, PosSpec, "timestop", "时停按钮 · 位置",
				new string[] { "auto", "custom" },
				new string[] { "自动（排在加速下方）", "自定义绝对坐标" },
				"custom", ReadPosPreset(), _posX, _posY, bw, bh, ApplyPosFromEditor);
		}
		catch { }
	}

	/// <summary>读当前 positionMode（兼容 string / JsonElement；取不到就按内存里的值推）。</summary>
	private string ReadPosPreset()
	{
		try
		{
			object v = (_context == null) ? null : _context.Settings.Get<object>(KPositionMode);
			string s = null;
			if (v is string str) { s = str; }
			else if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.String)
			{
				s = je.GetString();
			}
			if (!string.IsNullOrWhiteSpace(s)) { return s.Trim(); }
		}
		catch { }
		return _posCustom ? "custom" : "auto";
	}

	/// <summary>
	/// ★ 编辑器回调：写回配置 + **当帧套用**（需求 4「战斗设置页里的更改实时生效」）。
	/// 写配置是为了持久化；同时直接改内存字段 + 立刻 ResyncPosition，避免等下一轮轮询。
	/// </summary>
	private void ApplyPosFromEditor(string preset, int x, int y)
	{
		try
		{
			_posCustom = (preset == "custom");
			_posX = x;
			_posY = y;
			try
			{
				var changes = new System.Collections.Generic.Dictionary<string, object>();
				changes[KPositionMode] = preset;
				changes[KCustomX] = x;
				changes[KCustomY] = y;
				_context?.Settings?.TrySave(changes, out XWModSettingsSaveResult _);
			}
			catch { }
			try
			{
				Node root = (_tree != null && GodotObject.IsInstanceValid(_tree)) ? _tree.Root : null;
				if (root != null) { ResyncPosition(root); }
			}
			catch { }
		}
		catch { }
	}

	private static VBoxContainer ResolveTimeStopColumn(Node optionRoot)
	{
		try
		{
			float vw = 0f;
			try
			{
				Vector2 vp = optionRoot.GetViewport()?.GetVisibleRect().Size ?? Vector2.Zero;
				vw = vp.X;
			}
			catch { }
			float total = BattleOptionColumns.TotalMinWidth(optionRoot);
			if (vw > 100f && total > vw * 0.95f)
			{
				// 兜底：并到最右那一列（不新建列）
				HBoxContainer cols = optionRoot.FindChild(BattleOptionColumns.ColumnsName, true, false) as HBoxContainer;
				if (cols != null && GodotObject.IsInstanceValid(cols) && cols.GetChildCount() > 0)
				{
					for (int i = cols.GetChildCount() - 1; i >= 0; i--)
					{
						if (cols.GetChild(i) is VBoxContainer last && GodotObject.IsInstanceValid(last))
						{
							return last;
						}
					}
				}
			}
			return BattleOptionColumns.GetOrCreateColumn(optionRoot,
				BattleOptionColumns.TimeStopColName, BattleOptionColumns.TimeStopColMinWidth);
		}
		catch { return null; }
	}

	/// <summary>
	/// 找到"承载游戏选项列表"的那个 VBoxContainer。
	/// ⚠️ 必须兼容两种布局：装了 HealthCooldownLine（那个 Mod 会把原生
	/// `CenterContainer/VBoxContainer` 整个搬进 `CenterContainer/ModOptionColumns/ModCol1/`），
	/// 以及没装它时的原始布局。两条路径都试，再留一个"CenterContainer 下第一个 VBox"的兜底。
	/// </summary>
	private static VBoxContainer ResolveOptionList(Node optionRoot)
	{
		try
		{
			Node col1 = optionRoot.FindChild("ModCol1", true, false);
			if (col1 != null && GodotObject.IsInstanceValid(col1))
			{
				VBoxContainer inner = col1.FindChild("VBoxContainer", true, false) as VBoxContainer;
				if (inner != null && GodotObject.IsInstanceValid(inner))
				{
					return inner;
				}
			}
			VBoxContainer direct = optionRoot.GetNodeOrNull<VBoxContainer>("CenterContainer/VBoxContainer");
			if (direct != null && GodotObject.IsInstanceValid(direct))
			{
				return direct;
			}
			Node center = optionRoot.GetNodeOrNull("CenterContainer");
			if (center != null && GodotObject.IsInstanceValid(center))
			{
				foreach (Node child in center.GetChildren())
				{
					if (child is VBoxContainer vb && GodotObject.IsInstanceValid(vb))
					{
						return vb;
					}
					if (child is Node c2)
					{
						VBoxContainer deep = c2.FindChild("VBoxContainer", true, false) as VBoxContainer;
						if (deep != null && GodotObject.IsInstanceValid(deep))
						{
							return deep;
						}
					}
				}
			}
		}
		catch { }
		return null;
	}

	// ================================================================ 卡槽触发看门狗（★ 功能核心）

	/// <summary>
	/// 每帧观测 16 个卡槽的冷却状态，决定是否触发时停。
	/// 只在**战斗中且当前没有时停**时判定（时停期间 CD 本来就不走）。
	///
	/// ── 状态机（每条规则都对应一个真实场景，别再"简化"）──────────────
	///   · 冷却中 &amp;&amp; !armBlocked        ⇒ armed = true   （该卡已被用掉，CD 走完就该触发）
	///   · 就绪                            ⇒ armBlocked = false（开场保护解除）
	///                                        且若 armed ⇒ **触发时停**并清 armed
	///   · `armBlocked`：换关/换卡槽实例后置真 —— 用来吃掉**关卡开局自带的 startingCooldown**
	///     （卡片一进场就是冷却态，不屏蔽的话开局就会莫名时停一次）。
	/// 场景覆盖：
	///   ① 开局自带 CD：armBlocked=true ⇒ 不 arm；CD 走完只解保护、不触发 ✓
	///   ② 正常种下 → CD 结束：冷却中 arm ⇒ CD 结束触发 ✓
	///   ③ 勾选时该卡正在冷却：勾选回调里直接 arm（IsSlotCooling）⇒ CD 结束触发 ✓
	///   ④ 勾选时就绪：不 arm；下次种下（进入冷却）才 arm ⇒ CD 结束触发 ✓
	///   ⑤ **时停期间种下、解除后**：期间不判定；解除后第一帧看到"冷却中" ⇒ arm ⇒ CD 结束触发 ✓
	///      （v1.1.0 初版这里漏了 —— 用"就绪→冷却边沿"来 arm 会漏掉这种跨越暂停的种植。）
	///   ⑥ 卡槽里的卡被换掉：按卡片实例 id 变化清 armed，避免串味 ✓
	/// </summary>
	private void WatchSlotTriggers()
	{
		try
		{
			if (_timeStopOn)
			{
				return;   // 时停期间 CD 冻结，无需判定（解除后第一帧会自然重新对齐）
			}
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return;
			}
			TowerDefenseInGameSeedBank bank = mgr.GetSeedBank();
			if (bank == null || !GodotObject.IsInstanceValid(bank))
			{
				return;
			}
			// ★ v1.1.4：换关检测**放在 hasGameStarted 之前** —— 新关卡（含重打当前关卡）
			//   在读卡/选卡阶段 seedBank 实例就已经换了；若等开打后才检测，玩家在选卡阶段
			//   勾好的卡会在开打那一瞬间被"清除所有选择"清掉，与用户预期相反。
			ulong id = bank.GetInstanceId();
			if (id != _watchBankId)
			{
				bool firstWatch = (_watchBankId == 0UL);
				_watchBankId = id;
				ResetWatchState();
				// ★ v1.1.4（用户要求）：配置开关打开时，读取新关卡（含重打当前关卡）
				//   要清除所有植物的选择。首次建立观测（进第一关）不算"读取新关卡"，
				//   否则打开游戏后玩家刚勾好的卡会在进第一关时被清掉。
				if (!firstWatch && ClearTriggersOnNewLevel)
				{
					Info("读取新关卡：按设置清除所有卡槽的勾选。");
					ClearAllSlotTriggers(false);
				}
			}
			if (!bank.hasGameStarted)
			{
				return;   // 还没开打：CD 不走
			}
			Godot.Collections.Array<TowerDefenseInGamePacketShow> list = mgr.GetSeedBankList();
			for (int i = 0; i < SlotCount; i++)
			{
				TowerDefenseInGamePacketShow card = null;
				try
				{
					if (list != null && i < list.Count)
					{
						card = list[i];
					}
				}
				catch { }
				bool valid = card != null && GodotObject.IsInstanceValid(card);
				ulong cid = 0UL;
				bool cooling = false;
				if (valid)
				{
					try
					{
						cid = card.GetInstanceId();
						cooling = card.coldDownOpen || card.coldDownTimer > 0.0;
					}
					catch { valid = false; }
				}
				if (!valid || !_trigOn[i])
				{
					_trigArmed[i] = false;
					_slotCardId[i] = cid;
					_prevCooling[i] = cooling;
					continue;
				}
				if (cid != _slotCardId[i])
				{
					// 这个槽位换卡了 ⇒ 旧卡的待命状态必须清掉；并且把**新卡按"开会话"处理**：
					// 它可能一进来就是冷却态（选卡阶段换卡 / 新卡自带 startingCooldown），
					// 那种"本来就凉着"的冷却**不算玩家种下**，不该触发时停
					// —— 必须等它先出现过"就绪"，之后被真的用掉（就绪→冷却）才 arm。
					// （离线状态机测试 scenario 6 抓到：不置 armBlocked 会在换卡后误触发。）
					_slotCardId[i] = cid;
					_trigArmed[i] = false;
					_armBlocked[i] = true;
				}
				if (cooling)
				{
					if (!_armBlocked[i])
					{
						_trigArmed[i] = true;
					}
				}
				else
				{
					_armBlocked[i] = false;                    // 见到"就绪" ⇒ 开场保护解除
					if (_trigArmed[i])
					{
						_trigArmed[i] = false;
						_prevCooling[i] = false;
						FireSlotTrigger(i);
						return;                                // 一帧只触发一个槽
					}
				}
				_prevCooling[i] = cooling;
			}
		}
		catch (Exception ex)
		{
			if (!_panelFaultReported)
			{
				_panelFaultReported = true;
				Warn("卡槽看门狗异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	/// <summary>重置卡槽观测状态：装入"开场保护"以屏蔽关卡自带的 startingCooldown。</summary>
	private void ResetWatchState()
	{
		for (int i = 0; i < SlotCount; i++)
		{
			_trigArmed[i] = false;
			_armBlocked[i] = true;     // 开场保护：直到该槽出现过"就绪"才允许 arm
			_prevCooling[i] = true;
			_slotCardId[i] = 0UL;
		}
	}

	/// <summary>槽 i 的 CD 走完 ⇒ 触发时停（与手动切换共用 Engage 通道）。</summary>
	private void FireSlotTrigger(int slot)
	{
		try
		{
			ulong now = Time.GetTicksMsec();
			// 兜底防重：同一秒内不再连触发（正常路径下 armed 已清零，这里是双保险）
			if (_timeStopOn || (now - _lastFireMs < 1000UL && _lastFireSlot == slot))
			{
				return;
			}
			_lastFireMs = now;
			_lastFireSlot = slot;
			Info("卡槽触发：槽 " + (slot + 1) + "（" + SlotCardNameForLog(slot) + "）种植 CD 结束 → 自动时停。"
				+ "解除：点「时停」按钮、按快捷键，或在面板里点「解除时停」。");
			// ★ v1.1.2（用户新需求）：「自动解除时停」只作用于**自动触发**的时停，
			//   所以这里显式登记倒计时（armAutoResume = true）；手动触发不登记。
			Engage(true);
			RefreshPanelStatus();
			RefreshPanelRows();
		}
		catch (Exception ex)
		{
			Warn("触发时停异常：" + ex.Message);
		}
	}

	private string SlotCardNameForLog(int slot)
	{
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr != null && GodotObject.IsInstanceValid(mgr))
			{
				Godot.Collections.Array<TowerDefenseInGamePacketShow> list = mgr.GetSeedBankList();
				if (list != null && slot < list.Count)
				{
					return PacketDisplayName(list[slot]);
				}
			}
		}
		catch { }
		return "?";
	}

	/// <summary>卡片显示名（中文）。</summary>
	/// <remarks>
	/// `TowerDefensePacketConfig.name` 里存的**就是翻译键**（如 `TOWERDEFENSE_PLANT_BLOVER_NAME`，
	/// 见 `Asset/Anime/Character/Plant/Chapter0/Blover/Packet/PlantBlover.tres`），
	/// 所以过一遍 `TranslationServer.Translate` 即得中文；取不到再回落到角色键/保存键。
	/// </remarks>
	private static string PacketDisplayName(TowerDefenseInGamePacketShow card)
	{
		try
		{
			if (card == null || !GodotObject.IsInstanceValid(card))
			{
				return "（空）";
			}
			TowerDefensePacketConfig cfg = card.config;
			if (cfg == null || !GodotObject.IsInstanceValid(cfg))
			{
				return "（未就绪）";
			}
			string key = cfg.name;
			if (!string.IsNullOrWhiteSpace(key))
			{
				string tr = TranslationServer.Translate(key);
				if (!string.IsNullOrWhiteSpace(tr) && !string.Equals(tr, key, StringComparison.Ordinal))
				{
					return tr;
				}
			}
			try
			{
				TowerDefenseCharacterConfig cc = cfg.characterConfig;
				if (cc != null && GodotObject.IsInstanceValid(cc) && !string.IsNullOrWhiteSpace(cc.name))
				{
					string tr2 = TranslationServer.Translate(cc.name);
					if (!string.IsNullOrWhiteSpace(tr2) && !string.Equals(tr2, cc.name, StringComparison.Ordinal))
					{
						return tr2;
					}
					return cc.name;
				}
			}
			catch { }
			if (!string.IsNullOrWhiteSpace(cfg.saveKey))
			{
				return cfg.saveKey;
			}
			return string.IsNullOrWhiteSpace(key) ? "（未知）" : key;
		}
		catch
		{
			return "（未知）";
		}
	}

	// ================================================================ 面板控件

	/// <summary>保证面板存在（幂等；只在关卡内调用）。</summary>
	private void EnsurePanel(Node root)
	{
		try
		{
			if (_panelBuilt && _panelLayer != null && GodotObject.IsInstanceValid(_panelLayer)
				&& _panelRoot != null && GodotObject.IsInstanceValid(_panelRoot))
			{
				return;
			}
			ShutdownPanel();

			CanvasLayer layer = new CanvasLayer();
			layer.Name = PanelLayerName;
			layer.Layer = 150;                                 // ★ v1.1.1：抬到游戏对话框之上（键位录制面板 200 之下）
			layer.ProcessMode = Node.ProcessModeEnum.Always;   // ★ 时停（暂停树）期间仍要能点复选框
			root.AddChild(layer, false, Node.InternalMode.Disabled);

			CenterContainer center = new CenterContainer();
			center.Name = "Center";
			center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			center.MouseFilter = Control.MouseFilterEnum.Ignore;   // 空白区不挡战场点击
			layer.AddChild(center, false, Node.InternalMode.Disabled);

			PanelContainer panel = new PanelContainer();
			panel.Name = PanelRootName;
			panel.CustomMinimumSize = new Vector2(470f, 0f);
			panel.ProcessMode = Node.ProcessModeEnum.Always;
			panel.MouseFilter = Control.MouseFilterEnum.Stop;      // ★ 面板自身吃掉点击，不穿透到战场
			center.AddChild(panel, false, Node.InternalMode.Disabled);

			VBoxContainer box = new VBoxContainer();
			box.AddThemeConstantOverride("separation", 6);
			panel.AddChild(box, false, Node.InternalMode.Disabled);

			Label title = new Label();
			title.Text = "时停 · 卡槽触发面板";
			title.AddThemeFontSizeOverride("font_size", 20);
			title.HorizontalAlignment = HorizontalAlignment.Center;
			box.AddChild(title, false, Node.InternalMode.Disabled);

			_panelStatus = new Label();
			_panelStatus.AddThemeFontSizeOverride("font_size", 14);
			_panelStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
			_panelStatus.CustomMinimumSize = new Vector2(450f, 0f);
			box.AddChild(_panelStatus, false, Node.InternalMode.Disabled);

			Label hint = new Label();
			hint.Text = "勾选卡槽 ⇒ 该卡槽「种植 CD 结束」时自动时停。解除：点「时停」按钮 / 按快捷键 / 下面那个按钮。";
			hint.AddThemeFontSizeOverride("font_size", 13);
			hint.AddThemeColorOverride("font_color", new Color(0.72f, 0.78f, 0.86f));
			hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
			hint.CustomMinimumSize = new Vector2(450f, 0f);
			box.AddChild(hint, false, Node.InternalMode.Disabled);

			ScrollContainer scroll = new ScrollContainer();
			// 高度按视口自适应：手机竖屏/小分辨率下别把面板顶出屏幕（16 行 ≈ 420px）
			float vh = 720f;
			try
			{
				Viewport vp0 = root.GetViewport();
				if (vp0 != null && GodotObject.IsInstanceValid(vp0))
				{
					vh = vp0.GetVisibleRect().Size.Y;
				}
			}
			catch { }
			float scrollH = Mathf.Clamp(vh * 0.52f, 220f, 460f);
			scroll.CustomMinimumSize = new Vector2(450f, scrollH);
			scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
			box.AddChild(scroll, false, Node.InternalMode.Disabled);

			VBoxContainer rows = new VBoxContainer();
			rows.Name = "Rows";
			rows.AddThemeConstantOverride("separation", 2);
			rows.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			scroll.AddChild(rows, false, Node.InternalMode.Disabled);

			_rowNameLbl.Clear();
			_rowCdLbl.Clear();
			_rowCb.Clear();
			for (int i = 0; i < SlotCount; i++)
			{
				int slot = i;   // ★ 闭包捕获：必须用局部变量
				HBoxContainer row = new HBoxContainer();
				row.Name = "ModTsRow" + i;
				row.AddThemeConstantOverride("separation", 8);
				rows.AddChild(row, false, Node.InternalMode.Disabled);

				Label no = new Label();
				no.Text = (i + 1).ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
				no.AddThemeFontSizeOverride("font_size", 14);
				no.AddThemeColorOverride("font_color", new Color(0.62f, 0.68f, 0.76f));
				no.CustomMinimumSize = new Vector2(30f, 0f);
				row.AddChild(no, false, Node.InternalMode.Disabled);

				Label nm = new Label();
				nm.Text = "（空）";
				nm.AddThemeFontSizeOverride("font_size", 15);
				nm.CustomMinimumSize = new Vector2(190f, 0f);
				nm.ClipText = true;
				nm.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
				row.AddChild(nm, false, Node.InternalMode.Disabled);
				_rowNameLbl.Add(nm);

				Label cd = new Label();
				cd.Text = "";
				cd.AddThemeFontSizeOverride("font_size", 13);
				cd.AddThemeColorOverride("font_color", new Color(0.72f, 0.78f, 0.86f));
				cd.CustomMinimumSize = new Vector2(96f, 0f);
				cd.HorizontalAlignment = HorizontalAlignment.Right;
				row.AddChild(cd, false, Node.InternalMode.Disabled);
				_rowCdLbl.Add(cd);

				CheckBox cb = new CheckBox();
				cb.Name = "ModTsCb" + i;
				cb.ButtonPressed = _trigOn[i];
				cb.TooltipText = "勾选：槽 " + (i + 1) + " 的种植 CD 结束时自动触发时停";
				cb.CustomMinimumSize = new Vector2(38f, 0f);
				cb.ProcessMode = Node.ProcessModeEnum.Always;
				cb.Toggled += (bool pressed) =>
				{
					try
					{
						OnSlotCheckToggled(slot, pressed);
					}
					catch { }
				};
				row.AddChild(cb, false, Node.InternalMode.Disabled);
				_rowCb.Add(cb);
			}

			HBoxContainer footer = new HBoxContainer();
			footer.AddThemeConstantOverride("separation", 8);
			box.AddChild(footer, false, Node.InternalMode.Disabled);

			Button bindKey = new Button();
			bindKey.Text = "设置面板呼出键";
			bindKey.TooltipText = "打开既有快捷键录制面板，为「时停面板呼出键」录一个键";
			bindKey.CustomMinimumSize = new Vector2(0f, 38f);
			bindKey.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			bindKey.ProcessMode = Node.ProcessModeEnum.Always;
			bindKey.Pressed += () =>
			{
				try
				{
					// ★ 复用既有单键录制机制（KeybindPanelManager v2.2.1 的公开入口）
					KeybindPanelManager.OpenFor(_tree, "时停", "时停面板",
						SponsorQrRoot ?? (_context?.PackageRoot ?? ""),
						KPanelHotkey, ParseKeySetting(ReadPanelHotkey()), ReadPanelHotkey(),
						key => WritePanelHotkeySetting(key));
				}
				catch (Exception ex)
				{
					Warn("打开快捷键录制面板失败：" + ex.Message);
				}
			};
			footer.AddChild(bindKey, false, Node.InternalMode.Disabled);

			// ★ v1.1.4（用户要求）：一键清除所有植物的选择 ——
			//   按下后 16 个槽位全部取消勾选，所有植物在冷却完成后都不再触发自动时停。
			Button clearAll = new Button();
			clearAll.Name = "ClearAllTriggersButton";
			clearAll.Text = "清除所有选择";
			clearAll.TooltipText = "一键取消全部 16 个卡槽的勾选：之后所有植物在种植 CD 完成后都不会再触发自动时停";
			clearAll.CustomMinimumSize = new Vector2(0f, 38f);
			clearAll.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			clearAll.ProcessMode = Node.ProcessModeEnum.Always;
			clearAll.Pressed += () =>
			{
				try
				{
					ClearAllSlotTriggers(true);
				}
				catch { }
			};
			footer.AddChild(clearAll, false, Node.InternalMode.Disabled);

			Button release = new Button();
			release.Text = "解除时停";
			release.TooltipText = "立即结束时停（与点「时停」按钮、按快捷键等效）";
			release.CustomMinimumSize = new Vector2(0f, 38f);
			release.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			release.ProcessMode = Node.ProcessModeEnum.Always;
			// ★ 2026-10-13（用户要求：可以不要解除时停按钮了）：隐藏它（不删代码，避免牵连其空闲/还原逻辑）
			release.Visible = false;
			release.Disabled = true;
			release.Pressed += () =>
			{
				try
				{
					if (_timeStopOn)
					{
						Disengage("面板解除");
					}
					RefreshPanelStatus();
				}
				catch { }
			};
			footer.AddChild(release, false, Node.InternalMode.Disabled);

			// ★ 2026-10-13（用户要求：自动解除的设置也写在时停面板里）——
			//   一行：标签 + 滑动条(0~AutoResumeMaxSec 秒) + 数值；改动即写配置并立即生效（0 = 只能手动解除）
			//   ★ v1.1.4（用户要求）：上限从 60 秒降到 15 秒。
			try
			{
				HBoxContainer arRow = new HBoxContainer();
				arRow.Name = "AutoResumeRow";
				arRow.AddThemeConstantOverride("separation", 8);
				Label arTitle = new Label();
				// ★ v1.1.2：说明「只作用自动触发」——避免玩家以为手动时停也会自己解除
				arTitle.Text = "自动解除时停（仅限卡槽自动触发）";
				arTitle.AddThemeFontSizeOverride("font_size", 14);
				arTitle.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 1));
				arTitle.AddThemeConstantOverride("outline_size", 4);
				arRow.AddChild(arTitle);
				HSlider arSlider = new HSlider();
				arSlider.Name = "AutoResumeSlider";
				arSlider.MinValue = 0;
				arSlider.MaxValue = AutoResumeMaxSec;   // ★ v1.1.4：60 → 15
				arSlider.Step = 1;
				arSlider.Value = _autoResumeSec;
				arSlider.CustomMinimumSize = new Vector2(260f, 0f);
				arSlider.ProcessMode = Node.ProcessModeEnum.Always;
				arRow.AddChild(arSlider);
				Label arVal = new Label();
				arVal.Name = "AutoResumeValue";
				arVal.Text = (_autoResumeSec == 0) ? "关（只手动）" : (_autoResumeSec + " 秒");
				arVal.CustomMinimumSize = new Vector2(96f, 0f);
				arVal.AddThemeFontSizeOverride("font_size", 14);
				arVal.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 1));
				arVal.AddThemeConstantOverride("outline_size", 4);
				arRow.AddChild(arVal);
				HSlider arSliderRef = arSlider;
				Label arValRef = arVal;
				arSlider.ValueChanged += (double v) =>
				{
					try
					{
						if (_syncPanelCtl) { return; }
						int sec = (int)v;
						_autoResumeSec = sec;
						arValRef.Text = (sec == 0) ? "关（只手动）" : (sec + " 秒");
						try
						{
							var ch = new System.Collections.Generic.Dictionary<string, object>();
							ch[KAutoResume] = sec;
							_context?.Settings?.TrySave(ch, out XWModSettingsSaveResult _);
						}
						catch { }
					}
					catch { }
				};
				box.AddChild(arRow);
				_autoResumeSlider = arSliderRef;
				_autoResumeValue = arValRef;
				// 面板每次打开时按配置回填（用无信号赋值，避免把回填当成玩家操作）
				try
				{
					_syncPanelCtl = true;
					arSliderRef.SetValueNoSignal(_autoResumeSec);
					_syncPanelCtl = false;
				}
				catch { _syncPanelCtl = false; }
			}
			catch { }

			Button close = new Button();
			close.Text = "关闭";
			close.CustomMinimumSize = new Vector2(0f, 38f);
			close.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			close.ProcessMode = Node.ProcessModeEnum.Always;
			close.Pressed += () =>
			{
				try
				{
					SetPanelVisible(false);
				}
				catch { }
			};
			footer.AddChild(close, false, Node.InternalMode.Disabled);

			_applyPanelTheme(panel);
			layer.Visible = false;
			_panelLayer = layer;
			_panelRoot = panel;
			_panelBuilt = true;
			_panelVisible = false;
			Info("时停面板已创建（16 卡槽 + 复选框）。按面板快捷键或战斗设置页按钮呼出。");
		}
		catch (Exception ex)
		{
			Warn("创建时停面板异常：" + ex.Message);
		}
	}

	/// <summary>套用游戏主题（拿不到就保持默认主题，不影响功能）。</summary>
	private void _applyPanelTheme(Control panel)
	{
		try
		{
			if (_panelTheme == null || !GodotObject.IsInstanceValid(_panelTheme))
			{
				_panelTheme = ResourceLoader.Load<Theme>("res://Asset/Theme/MainTheme.tres");
			}
			if (_panelTheme != null && GodotObject.IsInstanceValid(_panelTheme))
			{
				panel.Theme = _panelTheme;
			}
		}
		catch { }
	}

	/// <summary>
	/// ★ v1.1.4（用户要求）：一键清除所有植物的选择。
	/// <para>把 16 个槽位的 `_trigOn` 全部置 false（并清掉待命状态），持久化后刷新面板 ——
	/// 之后所有植物在种植 CD 完成后都不再触发自动时停。</para>
	/// <para>`userAction` = true 表示玩家按了面板按钮（写日志）；内部自动清（换关）传 false。</para>
	/// </summary>
	private void ClearAllSlotTriggers(bool userAction)
	{
		try
		{
			bool any = false;
			for (int i = 0; i < SlotCount; i++)
			{
				if (_trigOn[i])
				{
					any = true;
				}
				_trigOn[i] = false;
				_trigArmed[i] = false;
				_slotCardId[i] = 0UL;
			}
			SavePanelCfg();
			// 复选框视觉同步（用无信号赋值，避免再触发一遍 Toggled）
			for (int i = 0; i < SlotCount && i < _rowCb.Count; i++)
			{
				CheckBox cb = _rowCb[i];
				if (cb != null && GodotObject.IsInstanceValid(cb) && cb.ButtonPressed)
				{
					cb.SetPressedNoSignal(false);
				}
			}
			if (userAction)
			{
				Info(any
					? "已一键清除所有卡槽的勾选：16 个槽位全部取消，CD 完成后不再自动时停。"
					: "一键清除：当前本来就没有勾选任何卡槽。");
			}
			RefreshPanelStatus();
			RefreshPanelRows();
		}
		catch (Exception ex)
		{
			Warn("一键清除卡槽勾选异常：" + ex.Message);
		}
	}

	private void OnSlotCheckToggled(int slot, bool pressed)
	{
		if (slot < 0 || slot >= SlotCount)
		{
			return;
		}
		_trigOn[slot] = pressed;
		if (pressed)
		{
			// 勾选时若该卡**正在冷却** ⇒ 立刻待命（否则等它被用掉后进入冷却再待命）
			bool cooling = IsSlotCooling(slot);
			_trigArmed[slot] = cooling;
			if (!cooling)
			{
				// ★ 勾选时该卡是**就绪**的 ⇒ 撤掉"开场保护"。
				//   保护的本意只是吃掉**关卡自带 startingCooldown**；玩家此刻明确勾选、
				//   而卡是就绪的，说明没有开场 CD 需要屏蔽。不撤的话会出现：
				//   "勾选后第一次种下 → CD 走完 → 不触发"（离线状态机测试 scenario 8 抓到的缺陷）。
				_armBlocked[slot] = false;
			}
			Info("槽 " + (slot + 1) + "（" + SlotCardNameForLog(slot) + "）勾选：CD 结束时触发时停"
				+ (cooling ? "（当前冷却中，已待命）" : "（当前就绪，将在下次种植后 CD 结束时触发）") + "。");
		}
		else
		{
			_trigArmed[slot] = false;
			Info("槽 " + (slot + 1) + " 取消勾选。");
		}
		SavePanelCfg();
		RefreshPanelStatus();
		RefreshPanelRows();
	}

	private bool IsSlotCooling(int slot)
	{
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr != null && GodotObject.IsInstanceValid(mgr))
			{
				Godot.Collections.Array<TowerDefenseInGamePacketShow> list = mgr.GetSeedBankList();
				if (list != null && slot < list.Count)
				{
					TowerDefenseInGamePacketShow c = list[slot];
					if (c != null && GodotObject.IsInstanceValid(c))
					{
						return c.coldDownOpen || c.coldDownTimer > 0.0;
					}
				}
			}
		}
		catch { }
		return false;
	}

	/// <summary>刷新 16 行的卡名 / 冷却 / 勾选状态。</summary>
	private void RefreshPanelRows()
	{
		try
		{
			Godot.Collections.Array<TowerDefenseInGamePacketShow> list = null;
			try
			{
				TowerDefenseManager mgr = TowerDefenseManager.Instance;
				if (mgr != null && GodotObject.IsInstanceValid(mgr))
				{
					list = mgr.GetSeedBankList();
				}
			}
			catch { }
			for (int i = 0; i < SlotCount && i < _rowNameLbl.Count; i++)
			{
				TowerDefenseInGamePacketShow card = null;
				try
				{
					if (list != null && i < list.Count)
					{
						card = list[i];
					}
				}
				catch { }
				bool valid = card != null && GodotObject.IsInstanceValid(card);
				string name = valid ? PacketDisplayName(card) : "（空）";
				string cdText = "（空）";
				if (valid)
				{
					bool cooling = false;
					double remain = 0.0;
					try
					{
						cooling = card.coldDownOpen || card.coldDownTimer > 0.0;
						remain = card.coldDownTimer;
					}
					catch { }
					if (cooling)
					{
						cdText = "CD " + remain.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "s";
					}
					else
					{
						cdText = _trigOn[i] ? "就绪" : "就绪";
					}
				}
				Label nm = _rowNameLbl[i];
				if (nm != null && GodotObject.IsInstanceValid(nm))
				{
					if (nm.Text != name)
					{
						nm.Text = name;
					}
					nm.Modulate = valid ? new Color(1f, 1f, 1f) : new Color(0.6f, 0.62f, 0.66f);
				}
				Label cl = _rowCdLbl[i];
				if (cl != null && GodotObject.IsInstanceValid(cl))
				{
					if (cl.Text != cdText)
					{
						cl.Text = cdText;
					}
					cl.AddThemeColorOverride("font_color", valid
						? (cdText.StartsWith("就绪", StringComparison.Ordinal)
							? new Color(0.55f, 0.92f, 0.60f)
							: new Color(0.95f, 0.82f, 0.45f))
						: new Color(0.55f, 0.58f, 0.62f));
				}
				CheckBox cb = _rowCb[i];
				if (cb != null && GodotObject.IsInstanceValid(cb) && cb.ButtonPressed != _trigOn[i])
				{
					cb.SetPressedNoSignal(_trigOn[i]);
				}
			}
			RefreshPanelStatus();
		}
		catch { }
	}

	/// <summary>刷新面板顶部状态行（时停开关 + 勾选概要）。</summary>
	private void RefreshPanelStatus()
	{
		try
		{
			if (_panelStatus == null || !GodotObject.IsInstanceValid(_panelStatus))
			{
				return;
			}
			_panelStatus.Text = "时停：" + (_timeStopOn ? "开（战场已冻结）" : "关")
				+ "　·　已勾选：" + DescribeTriggerSlots()
				+ "　·　面板呼出键：" + (string.IsNullOrWhiteSpace(ReadPanelHotkey()) ? "（未设置，可在配置页录制）" : ReadPanelHotkey());
		}
		catch { }
	}

	private void SetPanelVisible(bool visible)
	{
		try
		{
			if (!_panelBuilt || _panelLayer == null || !GodotObject.IsInstanceValid(_panelLayer))
			{
				if (visible && _tree != null && GodotObject.IsInstanceValid(_tree))
				{
					EnsurePanel(_tree.Root);
				}
			}
			if (_panelLayer == null || !GodotObject.IsInstanceValid(_panelLayer))
			{
				return;
			}
			_panelLayer.Visible = visible;
			_panelVisible = visible;
			if (visible)
			{
				RefreshPanelRows();
				// ★ 面板打开时把「自动解除时停」回填成当前配置值
				try
				{
					if (_autoResumeSlider != null && GodotObject.IsInstanceValid(_autoResumeSlider))
					{
						_syncPanelCtl = true;
						_autoResumeSlider.SetValueNoSignal(_autoResumeSec);
						_syncPanelCtl = false;
						if (_autoResumeValue != null && GodotObject.IsInstanceValid(_autoResumeValue))
						{
							_autoResumeValue.Text = (_autoResumeSec == 0) ? "关（只手动）" : (_autoResumeSec + " 秒");
						}
					}
				}
				catch { _syncPanelCtl = false; }
				PauseWhilePanel();      // ★ 2026-10-13：显示面板即暂停（不显示游戏暂停界面）
				Info("时停面板已打开。");
			}
			else
			{
				ResumeAfterPanel();     // ★ 关闭面板自动解除暂停，并还原到打开前的状态
				// ★★ 2026-10-13（用户实测"关闭后还是没解除暂停"）：设置页自己也压着 Paused；
				//   它已不在树里且时停没开 ⇒ 没有别的理由继续暂停，硬解除。
				try
				{
					Node rt = (_tree != null && GodotObject.IsInstanceValid(_tree)) ? _tree.Root : null;
					bool dialogOpen = rt != null && rt.FindChild("BattleOption", true, false) != null;
					if (!dialogOpen && !_timeStopOn && _tree != null && GodotObject.IsInstanceValid(_tree))
					{
						_tree.Paused = false;
						Engine.TimeScale = 1.0;
						Info("已解除暂停（设置页已收起且时停未开）。");
					}
				}
				catch { }
			}
		}
		catch { }
	}

	// ─────────────────────────────────────────────────────────────────────────────
	// ★★ 2026-10-13（用户确认的规格 A）：
	//   **显示面板时游戏暂停，但不显示游戏的暂停界面**（= 与呼出战斗设置页同样的效果：
	//   画面还在、战斗不推进）；**关闭面板时自动解除**并还原到打开前的状态 ——
	//   连"打开前本来就在时停/暂停"这种情况也照原样还原。
	//   做法照 `DialogBoxBase`：存一份 `Paused` / `Engine.TimeScale`，再设 Paused=true + TimeScale=1。
	//   面板层在创建时就设了 `ProcessMode = Always` ⇒ 暂停树下复选框照样能点 ✓。
	// ─────────────────────────────────────────────────────────────────────────────
	/// <summary>★ 时停面板里的「自动解除时停」控件（回填时用它，避免误触发写入）。</summary>
	private static HSlider _autoResumeSlider;
	private static Label _autoResumeValue;
	/// <summary>面板控件回填中 ⇒ 忽略其信号。</summary>
	private static bool _syncPanelCtl;

	private bool _panelPauseSaved;
	private bool _savedPaused;
	private double _savedTimeScale = 1.0;

	/// <summary>★ 每帧维持面板暂停（与时停功能的 MaintainTimeStop 同款）——
	/// 实测发现"打开面板不暂停"：暂停设下去会被别处（场景/游戏逻辑）改回，
	/// 所以要像时停那样**每帧校正**，而不是只设一次。</summary>
	private void MaintainPanelPause()
	{
		try
		{
			if (!_panelPauseSaved || !_panelVisible) { return; }
			if (_tree == null || !GodotObject.IsInstanceValid(_tree)) { return; }
			if (!_tree.Paused) { _tree.Paused = true; }
			if (Engine.TimeScale != 1.0) { Engine.TimeScale = 1.0; }
		}
		catch { }
	}

	private void PauseWhilePanel()
	{
		try
		{
			if (_panelPauseSaved) { return; }
			if (_tree == null || !GodotObject.IsInstanceValid(_tree)) { return; }
			_savedPaused = _tree.Paused;
			_savedTimeScale = Engine.TimeScale;
			_panelPauseSaved = true;
			Engine.TimeScale = 1.0;
			_tree.Paused = true;
		}
		catch { }
	}

	private void ResumeAfterPanel()
	{
		try
		{
			if (!_panelPauseSaved) { return; }
			_panelPauseSaved = false;
			if (_tree == null || !GodotObject.IsInstanceValid(_tree)) { return; }
			Engine.TimeScale = _savedTimeScale;
			_tree.Paused = _savedPaused;
		}
		catch { }
	}

	/// <summary>这次视口坐标是否落在「时停面板」上（用于屏蔽 Mod 自己的点击中继）。</summary>
	private bool IsOverPanel(Vector2 viewportPos)
	{
		try
		{
			if (!_panelVisible || _panelRoot == null || !GodotObject.IsInstanceValid(_panelRoot))
			{
				return false;
			}
			Rect2 r = new Rect2(_panelRoot.GetGlobalPosition(), _panelRoot.Size);
			return r.HasPoint(viewportPos);
		}
		catch
		{
			return false;
		}
	}

	// ================================================================ 面板快捷键

	private bool _panelHotkeyPrev;

	private string ReadPanelHotkey()
	{
		try
		{
			object v = (_context == null) ? null : _context.Settings.Get<object>(KPanelHotkey);
			string s = null;
			if (v is string str)
			{
				s = str;
			}
			else if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.String)
			{
				s = je.GetString();
			}
			if (!string.IsNullOrWhiteSpace(s))
			{
				return s.Trim();
			}
		}
		catch { }
		return "";
	}

	private void WritePanelHotkeySetting(string keySetting)
	{
		try
		{
			if (_context?.Settings == null)
			{
				try { GD.PrintErr("[TimeStop] 配置不可用，面板快捷键未保存。"); } catch { }
				return;
			}
			var changes = new Dictionary<string, object>();
			changes[KPanelHotkey] = keySetting ?? "";
			if (_context.Settings.TrySave(changes, out XWModSettingsSaveResult result))
			{
				Info("面板快捷键已保存：" + (string.IsNullOrEmpty(keySetting) ? "（无绑定）" : keySetting) + "（" + result.Message + "）");
				SyncOptionButtonVisibility();
			}
			else
			{
				try { GD.PrintErr("[TimeStop] 面板快捷键保存失败：" + result.Message); } catch { }
			}
		}
		catch (Exception ex)
		{
			try { GD.PrintErr("[TimeStop] 面板快捷键保存异常：" + ex.Message); } catch { }
		}
	}

	/// <summary>
	/// 面板呼出键轮询（每帧；由 `OnHotkeyTick` 调用）。
	/// ⚠️ 录制会话期间必须停手，否则"录 Q 到面板键"的瞬间就会被自己当成快捷键执行。
	/// </summary>
	private void PanelHotkeyTick()
	{
		try
		{
			if (KeybindPanelManager.IsCapturing())
			{
				_panelHotkeyPrev = false;
				return;
			}
			string key = ReadPanelHotkey();
			if (string.IsNullOrEmpty(key))
			{
				_panelHotkeyPrev = false;
				return;
			}
			Key k = ParseKeySetting(key);
			if (k == Key.None)
			{
				return;
			}
			bool down = false;
			try { down = Input.IsKeyPressed(k); } catch { }
			if (down && !_panelHotkeyPrev)
			{
				SetPanelVisible(!_panelVisible);
				Info("面板快捷键 " + key + " → " + (_panelVisible ? "打开" : "关闭") + "面板。");
			}
			_panelHotkeyPrev = down;
		}
		catch { }
	}

	/// <summary>
	/// 面板快捷键变化后同步"战斗设置页兜底按钮"的显隐
	/// （需求：设了面板快捷键 ⇒ 不再需要设置页里那个按钮）。
	/// </summary>
	private void SyncOptionButtonVisibility()
	{
		try
		{
			string now = ReadPanelHotkey();
			if (string.Equals(now, _optBtnHotkeySnapshot, StringComparison.Ordinal))
			{
				return;
			}
			_optBtnHotkeySnapshot = now;
			if (_tree == null || !GodotObject.IsInstanceValid(_tree) || _tree.Root == null)
			{
				return;
			}
			var found = new List<Node>();
			CollectByNameContains(_tree.Root, 0, new string[] { "BattleOption" }, found, 4);
			foreach (Node n in found)
			{
				if (n == null || !GodotObject.IsInstanceValid(n))
				{
					continue;
				}
				Button b = n.FindChild(OptBtnName, true, false) as Button;
				if (b != null && GodotObject.IsInstanceValid(b))
				{
					b.Visible = string.IsNullOrWhiteSpace(now);
				}
			}
		}
		catch { }
	}
}
