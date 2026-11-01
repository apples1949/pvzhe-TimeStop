// ============================================================================
//  快捷键白名单护栏（每个 Mod 各自编译一份，**不是共享组件**）
//
//  为什么需要（2026-11-01，玩家反馈「战斗设置页里的设置当时生效、重启后失效」）：
//
//  宿主 `XWModSettingsService.Save()` 对 changes **逐键** 走 `TryValue()`，其中 enum 型要求
//  值必须是 `mod.json` 里 `options[].value` 的**逐字相等**（XWModSettingsService.cs:303-321）；
//  **任意一键不合法 ⇒ 整批不落盘**（:700-712 + :736-740 折叠成 return false）。
//
//  而快捷键录制走 `KeybindPanelManager.KeyToString()`（mods/KeybindPanelManager.cs:390-415），
//  它会把物理键转成 Godot `Key` 枚举名（`Space` / `Backspace` / `Slash` / `Semicolon` /
//  `Apostrophe` / `Backslash` / `Bracketleft` / `Bracketright` / `Pageup` / `Left` …，
//  音量三键修正成 `VolumeUp/VolumeDown/VolumeMute`）。
//  ⇒ 只要 mod.json 的 enum options 少列了某个键，玩家一录到它，**整批**设置（含同批其它合法项）
//  就会被宿主拒掉，且 Mod 侧以前根本看不到失败。
//
//  本类的职责：保存前按 **mod.json 里真实声明的 options** 校验一次；
//  不合法 ⇒ 不保存、保留旧值、打一条**不受日志门控**的提示（把"无声丢设置"变成"明确告知"）。
//  白名单从 mod.json 读（`ctx.PackageRoot/mod.json`），以后改 mod.json 不会和代码失配；
//  读不到时退化为静态兜底表（与 2026-11-01 加宽后的 options 一致）。
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;
using PVZHE.ModEditor.ModSystem;

internal static class KeyOptionGuard
{
	/// <summary>已解析的 options（键 = 设置键名，值 = 允许的取值集合）。</summary>
	private static readonly Dictionary<string, HashSet<string>> Cache = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

	/// <summary>上次解析用的包目录（包重装/换目录 ⇒ 缓存作废）。</summary>
	private static string _cachedRoot;

	/// <summary>
	/// 该键名是否允许保存。<paramref name="value"/> 为空串表示"无绑定"，一律允许。
	/// 读不到 mod.json 时用静态兜底表（宁可放过，也不要把玩家挡死）。
	/// </summary>
	internal static bool IsAllowed(XWModRuntimeContext ctx, string key, string value)
	{
		try
		{
			if (string.IsNullOrEmpty(value)) { return true; }
			HashSet<string> allowed = Options(ctx, key);
			if (allowed == null || allowed.Count == 0) { return Fallback.Contains(value); }
			return allowed.Contains(value);
		}
		catch { return true; }   // 校验本身出错就别拦（真被宿主拒了还有 SettingsPersist 的日志兜）
	}

	/// <summary>该键允许取值的可读描述（日志用，最多列 12 项）。</summary>
	internal static string Describe(XWModRuntimeContext ctx, string key)
	{
		try
		{
			HashSet<string> allowed = Options(ctx, key);
			List<string> list = new List<string>();
			if (allowed != null) { foreach (string v in allowed) { if (!string.IsNullOrEmpty(v)) { list.Add(v); } } }
			else { foreach (string v in Fallback) { list.Add(v); } }
			list.Sort(StringComparer.Ordinal);
			if (list.Count > 12) { return string.Join("/", list.GetRange(0, 12).ToArray()) + "/…"; }
			return string.Join("/", list.ToArray());
		}
		catch { return "?"; }
	}

	private static HashSet<string> Options(XWModRuntimeContext ctx, string key)
	{
		string root = "";
		try { root = ctx == null ? "" : (ctx.PackageRoot ?? ""); } catch { }
		if (!string.Equals(root, _cachedRoot, StringComparison.Ordinal))
		{
			Cache.Clear();
			_cachedRoot = root;
		}
		HashSet<string> hit;
		if (Cache.TryGetValue(key, out hit)) { return hit; }

		HashSet<string> set = TryReadFromManifest(root, key);
		Cache[key] = set;     // 可能是 null（= mod.json 读不到），下次不重复 IO
		return set;
	}

	/// <summary>从 `&lt;PackageRoot&gt;/mod.json` 的 settings[].options 里取该键的允许值。</summary>
	private static HashSet<string> TryReadFromManifest(string root, string key)
	{
		try
		{
			if (string.IsNullOrEmpty(root)) { return null; }
			string path = Path.Combine(root, "mod.json");
			if (!File.Exists(path)) { return null; }
			using (JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path)))
			{
				JsonElement rootEl = doc.RootElement;
				JsonElement settings;
				if (!rootEl.TryGetProperty("settings", out settings) || settings.ValueKind != JsonValueKind.Array) { return null; }
				foreach (JsonElement item in settings.EnumerateArray())
				{
					JsonElement k;
					if (!item.TryGetProperty("key", out k) || k.ValueKind != JsonValueKind.String) { continue; }
					if (!string.Equals(k.GetString(), key, StringComparison.Ordinal)) { continue; }
					JsonElement opts;
					if (!item.TryGetProperty("options", out opts) || opts.ValueKind != JsonValueKind.Array) { return null; }
					HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
					foreach (JsonElement o in opts.EnumerateArray())
					{
						JsonElement v;
						if (o.TryGetProperty("value", out v) && v.ValueKind == JsonValueKind.String) { set.Add(v.GetString()); }
					}
					return set;
				}
			}
		}
		catch (Exception ex)
		{
			try { GD.PrintErr("[KeyOptionGuard] 读取 mod.json 的 options 失败（改用内置兜底表）：" + ex.Message); } catch { }
		}
		return null;
	}

	/// <summary>兜底表：与 2026-11-01 加宽后的 options 一致（mod.json 读不到时才用）。</summary>
	private static readonly HashSet<string> Fallback = BuildFallback();

	private static HashSet<string> BuildFallback()
	{
		HashSet<string> s = new HashSet<string>(StringComparer.Ordinal);
		s.Add("");
		for (char c = 'A'; c <= 'Z'; c++) { s.Add(c.ToString()); }
		for (int i = 0; i <= 9; i++) { s.Add(i.ToString()); }
		for (int i = 1; i <= 12; i++) { s.Add("F" + i); }
		string[] named =
		{
			"Space", "Enter", "Tab", "Backspace", "Insert", "Delete", "Home", "End",
			"Pageup", "Pagedown", "Left", "Up", "Right", "Down",
			"Minus", "Equal", "Comma", "Period", "Slash", "Semicolon",
			"Apostrophe", "Backslash", "Bracketleft", "Bracketright",
			"VolumeUp", "VolumeDown", "VolumeMute",
		};
		for (int i = 0; i < named.Length; i++) { s.Add(named[i]); }
		return s;
	}
}
