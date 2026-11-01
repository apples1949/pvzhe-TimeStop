// ============================================================================
//  设置落盘哨兵（每个 Mod 各自编译一份，**不要放到共享组件里**）
//
//  为什么需要（2026-11-01，玩家反馈「战斗设置页里改的设置当时生效、重启后失效」）：
//
//  宿主 `XWModSettingsService.Save()`（unpack/addons/ModEditor/ModSystem/
//  XWModSettingsService.cs:664-745）把**所有**失败都折叠成一个 bool：
//    · 任一键被 `TryValue` 判为非法 ⇒ 抛 ArgumentException ⇒ 整批不落盘（:700-712、:736-740）；
//    · `_saving` 重入（在 `Settings.Changed` 回调里再保存）⇒ 直接 return false（:676-680）；
//    · `.lock` 独占 / tmp / File.Move 失败 ⇒ IOException ⇒ 整批不落盘（:607-662、:736-740）；
//    · 主线程校验失败 ⇒ return false（:669-675）。
//  而 Mod 侧的调用点大多是 `ctx.Settings.TrySave(ch, out _)`（**返回值丢掉**）或者只把
//  失败打到被 `enableLog` 门控的日志里 ⇒ 玩家看到的是"当场生效、无任何提示、重启没了"。
//
//  本类的职责（对应上面四条失败路径，逐条兜住）：
//    ① **延后一帧写**：控件回调只入队，真正落盘在下一帧的 `Tick()`（`OnFrame` 里调）
//       ⇒ 天然避开 `_saving` 重入区间（Apply 期间的 Changed 回调里不会再保存）；
//    ② **失败重试**：整批失败时**逐键**再试，把被宿主拒绝的那个键单独拎出来并报出宿主的
//       原话（不门控）；合法键不会陪着一起丢；
//    ③ **落盘自证**：成功后回读 `Settings.Get<object>(key)` 比对，不一致就再入队重试一次；
//    ④ **日志不门控**：成功/失败都用 `GD.Print` / `GD.PrintErr` 打一条，`enableLog=off` 也看得见
//       —— 玩家反馈日志里 grep 不到任何保存痕迹，正是排查卡死的根源。
// ============================================================================
using System;
using System.Collections.Generic;
using Godot;
using PVZHE.ModEditor.ModSystem;

internal sealed class SettingsPersist
{
	/// <summary>同一个键最多尝试几次逐键保存（整批失败后）；超了就放弃并报错，避免每帧刷屏。</summary>
	private const int MaxKeyAttempts = 3;

	private readonly XWModRuntimeContext _ctx;
	private readonly string _prefix;

	/// <summary>待落盘的键值（有序，便于日志可读）。</summary>
	private readonly Dictionary<string, object> _queue = new Dictionary<string, object>(StringComparer.Ordinal);

	/// <summary>逐键尝试次数。</summary>
	private readonly Dictionary<string, int> _tries = new Dictionary<string, int>(StringComparer.Ordinal);

	/// <summary>最近一次整批失败时宿主给的原话（诊断用）。</summary>
	private string _lastError = "";

	/// <summary>已经因"回读不一致"重试过的键（只重试一次，避免每帧刷日志）。</summary>
	private readonly HashSet<string> _verifyRetried = new HashSet<string>(StringComparer.Ordinal);

	internal SettingsPersist(XWModRuntimeContext ctx, string prefix)
	{
		_ctx = ctx;
		_prefix = string.IsNullOrEmpty(prefix) ? "[SettingsPersist] " : prefix;
	}

	internal bool HasPending { get { return _queue.Count > 0; } }

	/// <summary>入队一个键（控件回调里调它；真正的写盘发生在下一帧 <see cref="Tick"/>）。</summary>
	internal void Set(string key, object value)
	{
		try
		{
			if (string.IsNullOrEmpty(key)) { return; }
			_queue[key] = value;
			_tries[key] = 0;
		}
		catch { }
	}

	/// <summary>整批入队（同一批里的键会一起尝试，失败时再逐键隔离）。</summary>
	internal void SetMany(IDictionary<string, object> changes)
	{
		try
		{
			if (changes == null) { return; }
			foreach (KeyValuePair<string, object> kv in changes) { Set(kv.Key, kv.Value); }
		}
		catch { }
	}

	/// <summary>每帧调用（放在 Mod 的 process_frame 里）。有排队就尝试落盘。</summary>
	internal void Tick()
	{
		try
		{
			if (_queue.Count == 0) { return; }
			if (_ctx == null || _ctx.Settings == null) { return; }
			Flush();
		}
		catch (Exception ex) { Warn("落盘异常（已吞）：" + ex.Message); }
	}

	private void Flush()
	{
		Dictionary<string, object> batch = new Dictionary<string, object>(_queue, StringComparer.Ordinal);
		if (batch.Count == 0) { return; }

		XWModSettingsSaveResult result = null;
		bool ok = false;
		try { ok = _ctx.Settings.TrySave(batch, out result); }
		catch (Exception ex) { Warn("整批保存抛异常（已吞）：" + ex.GetBaseException().Message); }

		if (ok)
		{
			_lastError = "";
			GD.Print(_prefix + "设置落盘：" + Describe(batch) + "（" + Message(result) + "）");
			Verify(batch);
			foreach (string k in new List<string>(batch.Keys)) { _queue.Remove(k); _tries.Remove(k); }
			return;
		}

		// ── 整批被拒：宿主的 Save 是"任一键非法 ⇒ 整批不落盘"（XWModSettingsService.cs:700-712）
		//    ⇒ 这里逐键重试，把坏键单独拎出来，合法键照样落盘。
		_lastError = Message(result);
		Err("整批保存被拒：" + _lastError + "；改为逐键重试：" + Describe(batch));
		List<string> keys = new List<string>(batch.Keys);
		for (int i = 0; i < keys.Count; i++)
		{
			string key = keys[i];
			int tries;
			_tries.TryGetValue(key, out tries);
			tries++;
			_tries[key] = tries;
			Dictionary<string, object> one = new Dictionary<string, object>(1);
			one[key] = batch[key];
			XWModSettingsSaveResult r1 = null;
			bool okOne = false;
			try { okOne = _ctx.Settings.TrySave(one, out r1); }
			catch (Exception ex) { Err("单键 " + key + " 保存抛异常：" + ex.GetBaseException().Message); }
			if (okOne)
			{
				GD.Print(_prefix + "设置落盘（单键）：" + key + "=" + Fmt(batch[key]) + "（" + Message(r1) + "）");
				Verify(one);
				_queue.Remove(key);
				_tries.Remove(key);
				continue;
			}
			if (tries >= MaxKeyAttempts)
			{
				Err("单键保存被拒（连续 " + tries + " 次，放弃）：键='" + key + "' " + Message(r1));
				_queue.Remove(key);
				_tries.Remove(key);
			}
			else
			{
				Err("单键保存被拒（第 " + tries + " 次，将重试）：键='" + key + "' " + Message(r1));
			}
		}
	}

	/// <summary>落盘后回读自证：宿主把值归一化过（int→int、float→double、enum/string→string），
	/// 这里按字符串形态比对，不一致说明没写进去（会再入队重试一次）。</summary>
	private void Verify(Dictionary<string, object> written)
	{
		try
		{
			foreach (KeyValuePair<string, object> kv in written)
			{
				object now = null;
				try { now = _ctx.Settings.Get<object>(kv.Key); }
				catch (Exception ex) { Err("回读 '" + kv.Key + "' 失败：" + ex.GetBaseException().Message); continue; }
				if (Str(now) != Str(kv.Value))
				{
					if (_verifyRetried.Contains(kv.Key))
					{
						Err("回读不一致（已重试过，放弃）：'" + kv.Key + "' 期望 " + Str(kv.Value) + "，实际 " + Str(now));
						continue;
					}
					_verifyRetried.Add(kv.Key);
					Err("回读不一致：'" + kv.Key + "' 期望 " + Str(kv.Value) + "，实际 " + Str(now) + " ⇒ 重新入队重试一次");
					Set(kv.Key, kv.Value);
				}
			}
		}
		catch { }
	}

	private static string Message(XWModSettingsSaveResult r)
	{
		if (r == null) { return "无返回信息"; }
		return r.Message ?? "";
	}

	private static string Describe(Dictionary<string, object> d)
	{
		System.Text.StringBuilder sb = new System.Text.StringBuilder();
		foreach (KeyValuePair<string, object> kv in d)
		{
			if (sb.Length > 0) { sb.Append(", "); }
			sb.Append(kv.Key).Append('=').Append(Fmt(kv.Value));
		}
		return sb.ToString();
	}

	private static string Fmt(object v)
	{
		try
		{
			if (v == null) { return "<null>"; }
			if (v is bool b) { return b ? "true" : "false"; }
			if (v is string s) { return "\"" + s + "\""; }
			if (v is Color c) { return "#" + c.ToHtml(); }
			if (v is IFormattable f) { return f.ToString(null, System.Globalization.CultureInfo.InvariantCulture); }
			return v.ToString();
		}
		catch { return "?"; }
	}

	/// <summary>值的字符串形态（回读比对用；宿主会把 int 归一化为 int、float 归一化为 double）。</summary>
	private static string Str(object v)
	{
		try
		{
			if (v == null) { return "<null>"; }
			if (v is bool b) { return b ? "true" : "false"; }
			if (v is string s) { return s; }
			if (v is Color c) { return "#" + c.ToHtml().ToLowerInvariant(); }
			if (v is double d) { return d.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture); }
			if (v is float f) { return ((double)f).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture); }
			if (v is System.Text.Json.JsonElement je)
			{
				switch (je.ValueKind)
				{
					case System.Text.Json.JsonValueKind.String: return je.GetString();
					case System.Text.Json.JsonValueKind.True: return "true";
					case System.Text.Json.JsonValueKind.False: return "false";
					case System.Text.Json.JsonValueKind.Number: return je.GetRawText();
					default: return je.GetRawText();
				}
			}
			if (v is IFormattable fm) { return fm.ToString(null, System.Globalization.CultureInfo.InvariantCulture); }
			return v.ToString();
		}
		catch { return "?"; }
	}

	/// <summary>成功/诊断行：**不门控**（enableLog=off 也要能在 godot.log 里看到）。</summary>
	private void Warn(string msg)
	{
		try { GD.PrintErr(_prefix + msg); } catch { }
	}

	private void Err(string msg)
	{
		try { GD.PrintErr(_prefix + msg); } catch { }
	}
}
