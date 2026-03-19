using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using ProcessorTycoon.SpreadsheetSystem;
using ProcessorTycoon.Desktop.Hardware;
using ProcessorTycoon.Desktop.Inspector;
using ProcessorTycoon.Hardware;
using ProcessorTycoon.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine.UI;

namespace SpreadsheetRowClick;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class SpreadsheetRowClickPlugin : BaseUnityPlugin
{
	public const string PluginGuid = "whitebow.processortycoon.spreadsheetrowclick";
	public const string PluginName = "Spreadsheet Row Click";
	public const string PluginVersion = "1.0.0";

	internal static ManualLogSource Log = null!;

	// If true, clicking a row will clone the InspectorWindow so multiple inspectors can be opened.
	// If false, it reuses the existing InspectorWindow instance.
	private static bool SpawnNewInspectorPerClick;
	private static int _spawnCount;

	// Compare support (max 2 inspector windows)
	private static InspectorWindow? _primaryInspector;
	private static InspectorWindow? _compareInspector;
	private static Button? _compareToggleButton;
	private static bool _compareVisible;
	private static Cpu? _lastPrimaryCpu;

	/// <summary>
	/// Public callback you can replace from other plugins.
	/// Args: spreadsheet instance, row instance.
	/// </summary>
	public static Action<Spreadsheet, Spreadsheet.Row>? OnRowClicked;

	private void Awake()
	{
		Log = Logger;
		// Deprecated: we no longer spawn per click; compare is handled via a button.
		SpawnNewInspectorPerClick = false;
		OnRowClicked ??= DefaultRowClickHandler;

		new Harmony(PluginGuid).PatchAll(typeof(Patches));
		Log.LogInfo("Patched Spreadsheet.Row ctor to add click handler.");
	}

	private static void DefaultRowClickHandler(Spreadsheet sheet, Spreadsheet.Row row)
	{
		// If the click came from the hardware window spreadsheets, open InspectorWindow for that CPU.
		try
		{
			if (TryHandleHardwareWindowRowClick(sheet, row))
				return;
		}
		catch (Exception e)
		{
			Log.LogWarning($"HardwareWindow row-click handling failed: {e}");
		}

		// Fallback: just log something.
		try
		{
			Log.LogInfo($"Row clicked on '{sheet.name}': col0='{row.GetTextAtColumn(0)}'");
		}
		catch (Exception e)
		{
			Log.LogWarning($"Row clicked, but couldn't read column 0: {e}");
		}
	}

	private static bool TryHandleHardwareWindowRowClick(Spreadsheet sheet, Spreadsheet.Row row)
	{
		// The HardwareWindow fills spreadsheets with:
		// col0=Company, col1=CPU (both in both Specs and Market).
		string companyName = row.GetTextAtColumn(0);
		string cpuName = row.GetTextAtColumn(1);
		if (string.IsNullOrWhiteSpace(cpuName))
			return false;

		// Only act if this spreadsheet belongs to a HardwareWindow.
		var hw = sheet.GetComponentInParent<HardwareWindow>();
		if (hw == null)
			return false;

		// Find target CPU.
		var cpu = CpuDataProvider.Instance
			.GetAllCpus()
			.FirstOrDefault(c => c != null
				&& string.Equals(c.Name, cpuName, StringComparison.Ordinal)
				&& (string.IsNullOrEmpty(companyName) || string.Equals(c.Company?.Name, companyName, StringComparison.Ordinal)));

		if (cpu == null)
		{
			Log.LogInfo($"Row click: couldn't find Cpu for company='{companyName}', cpu='{cpuName}'.");
			return true; // handled (we tried)
		}

		OpenInspectorWithCpu(cpu);
		return true;
	}

	private static void OpenInspectorWithCpu(Cpu cpu)
	{
		EnsureInspectorsExist();
		if (_primaryInspector == null)
		{
			Log.LogWarning("Couldn't find InspectorWindow in scene.");
			return;
		}

		// Mode B: row clicks update ONLY the primary inspector.
		ShowCpuInInspector(_primaryInspector, cpu, focus: true);
		_lastPrimaryCpu = cpu;
	}

	private static void EnsureInspectorsExist()
	{
		if (_primaryInspector != null)
			return;

		_primaryInspector = Resources.FindObjectsOfTypeAll<InspectorWindow>().FirstOrDefault();
		if (_primaryInspector == null)
			return;

		_primaryInspector.gameObject.SetActive(true);
		_primaryInspector.transform.SetAsLastSibling();

		EnsureCompareButton(_primaryInspector);
	}

	private static void EnsureCompareButton(InspectorWindow inspector)
	{
		if (_compareToggleButton != null)
			return;

		try
		{
			_compareToggleButton = CreateSimpleButton(inspector.transform, name: "CompareToggleButton", label: "Compare", size: new Vector2(120f, 32f), anchoredPos: new Vector2(-12f, -36f), onClick: ToggleCompare);
		}
		catch (Exception e)
		{
			Log.LogWarning($"Failed to create Compare button: {e}");
		}
	}

	private static Button CreateSimpleButton(Transform parent, string name, string label, Vector2 size, Vector2 anchoredPos, Action onClick)
	{
		var buttonGo = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
		buttonGo.transform.SetParent(parent, worldPositionStays: false);
		var rt = buttonGo.GetComponent<RectTransform>();
		rt.anchorMin = new Vector2(1f, 1f);
		rt.anchorMax = new Vector2(1f, 1f);
		rt.pivot = new Vector2(1f, 1f);
		rt.sizeDelta = size;
		rt.anchoredPosition = anchoredPos;

		var img = buttonGo.GetComponent<Image>();
		img.color = new Color(0.2f, 0.2f, 0.2f, 0.9f);

		var textGo = new GameObject("Text", typeof(RectTransform));
		textGo.transform.SetParent(buttonGo.transform, worldPositionStays: false);
		var tmp = textGo.AddComponent<TextMeshProUGUI>();
		tmp.text = label;
		tmp.alignment = TextAlignmentOptions.Center;
		tmp.raycastTarget = false;
		tmp.fontSize = 18f;
		tmp.color = Color.white;
		var trt = textGo.GetComponent<RectTransform>();
		trt.anchorMin = Vector2.zero;
		trt.anchorMax = Vector2.one;
		trt.offsetMin = Vector2.zero;
		trt.offsetMax = Vector2.zero;

		var b = buttonGo.GetComponent<Button>();
		b.onClick.AddListener(() => onClick());
		return b;
	}

	private static void ToggleCompare()
	{
		try
		{
			EnsureInspectorsExist();
			if (_primaryInspector == null)
				return;

			_compareVisible = !_compareVisible;
			if (!_compareVisible)
			{
				if (_compareInspector != null)
					_compareInspector.gameObject.SetActive(false);
				return;
			}

			if (_compareInspector == null)
			{
				var clonedGo = Instantiate(_primaryInspector.gameObject, _primaryInspector.transform.parent);
				clonedGo.name = $"{_primaryInspector.gameObject.name} (Compare)";
				_compareInspector = clonedGo.GetComponent<InspectorWindow>();

				// Offset to the right a bit.
				var rt = clonedGo.GetComponent<RectTransform>();
				if (rt != null)
					rt.anchoredPosition += new Vector2(340f, 0f);
			}

			_compareInspector.gameObject.SetActive(true);
			_compareInspector.transform.SetAsLastSibling();
		}
		catch (Exception e)
		{
			Log.LogWarning($"ToggleCompare failed: {e}");
		}
	}

	private static void ShowCpuInInspector(InspectorWindow inspector, Cpu cpu, bool focus)
	{
		inspector.gameObject.SetActive(true);
		if (focus)
			inspector.transform.SetAsLastSibling();

		var t = typeof(InspectorWindow);
		var updateCompanies = t.GetMethod("UpdateCompanies", BindingFlags.Instance | BindingFlags.NonPublic);
		var updateCpuDropdown = t.GetMethod("UpdateCpuDropdown", BindingFlags.Instance | BindingFlags.NonPublic);
		var updateCpuUi = t.GetMethod("UpdateCpuUI", BindingFlags.Instance | BindingFlags.NonPublic);

		updateCompanies?.Invoke(inspector, null);

		// Set currentCompany/currentCpu directly so UpdateCpuDropdown and UpdateCpuUI operate on the correct objects.
		var allCompaniesField = t.GetField("allCompanies", BindingFlags.Instance | BindingFlags.NonPublic);
		var currentCompanyField = t.GetField("currentCompany", BindingFlags.Instance | BindingFlags.NonPublic);
		var currentCpuField = t.GetField("currentCpu", BindingFlags.Instance | BindingFlags.NonPublic);
		var companyDropdownField = t.GetField("companyDropdown", BindingFlags.Instance | BindingFlags.NonPublic);
		var cpuDropdownField = t.GetField("cpuDropdown", BindingFlags.Instance | BindingFlags.NonPublic);

		var allCompanies = allCompaniesField?.GetValue(inspector) as System.Collections.IEnumerable;
		object? matchedCompanyOwner = null;
		if (allCompanies != null)
		{
			foreach (var owner in allCompanies)
			{
				try
				{
					var companyProp = owner?.GetType().GetProperty("Company");
					var companyObj = companyProp?.GetValue(owner);
					var nameProp = companyObj?.GetType().GetProperty("Name");
					var name = nameProp?.GetValue(companyObj) as string;
					if (!string.IsNullOrEmpty(name) && string.Equals(name, cpu.Company.Name, StringComparison.Ordinal))
					{
						matchedCompanyOwner = owner;
						break;
					}
				}
				catch
				{
				}
			}
		}

		if (matchedCompanyOwner != null)
			currentCompanyField?.SetValue(inspector, matchedCompanyOwner);
		currentCpuField?.SetValue(inspector, cpu);

		// Update dropdown visuals too (best-effort).
		var companyDropdown = companyDropdownField?.GetValue(inspector) as DropdownUI;
		var cpuDropdown = cpuDropdownField?.GetValue(inspector) as DropdownUI;
		companyDropdown?.SetSelected(cpu.Company.Name);

		updateCpuDropdown?.Invoke(inspector, null);
		cpuDropdown?.SetSelected(cpu.Name);

		updateCpuUi?.Invoke(inspector, null);
	}

	private static class Patches
	{
		// Patch the Row constructor so every new row gets a click listener.
		[HarmonyPostfix]
		[HarmonyPatch(typeof(Spreadsheet.Row), MethodType.Constructor,
			new[] { typeof(Spreadsheet), typeof(Color32), typeof(GrowthData) })]
		private static void SpreadsheetRowCtor_Postfix(Spreadsheet.Row __instance, Spreadsheet targetSpreadsheet)
		{
			try
			{
				RowClickBinder.TryBind(__instance, targetSpreadsheet);
			}
			catch (Exception e)
			{
				Log.LogError($"Failed binding row click: {e}");
			}
		}
	}

	private sealed class RowClickBinder : MonoBehaviour, IPointerClickHandler
	{
		private Spreadsheet _sheet = null!;
		private Spreadsheet.Row _row = null!;
		private bool _bound;

		public static void TryBind(Spreadsheet.Row row, Spreadsheet sheet)
		{
			if (row.Background == null)
				return;

			var go = row.Background.gameObject;
			var binder = go.GetComponent<RowClickBinder>();
			if (binder == null)
				binder = go.AddComponent<RowClickBinder>();

			binder.Bind(sheet, row);
		}

		private void Bind(Spreadsheet sheet, Spreadsheet.Row row)
		{
			_sheet = sheet;
			_row = row;

			if (_bound)
				return;
			_bound = true;

			// Ensure the row can receive pointer events.
			var bg = _row.Background;
			bg.raycastTarget = true;

			// Ensure there's an EventSystem in the scene (Unity UI requirement).
			if (FindFirstObjectByType<EventSystem>() == null)
			{
				var esGo = new GameObject("[SpreadsheetRowClick] EventSystem");
				esGo.AddComponent<EventSystem>();
				esGo.AddComponent<StandaloneInputModule>();
				DontDestroyOnLoad(esGo);
			}
		}

		public void OnPointerClick(PointerEventData eventData)
		{
			try
			{
				OnRowClicked?.Invoke(_sheet, _row);
			}
			catch (Exception e)
			{
				Log.LogError($"OnRowClicked handler threw: {e}");
			}
		}
	}
}
