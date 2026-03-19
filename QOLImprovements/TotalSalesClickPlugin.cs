using System;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using ProcessorTycoon.GraphSystem;
using ProcessorTycoon.Hardware;
using ProcessorTycoon.MarketSystem.UI;
using ProcessorTycoon.Desktop.Inspector;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace QOLImprovements
{
    [BepInPlugin("whitebow.processortycoon.totalsalesclick", "Total Sales Click", "1.0.0")]
    public class TotalSalesClickPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;
        private static InspectorWindow? _compareInspector;
        private static bool _compareVisible;
        private void Awake()
        {
            Log = Logger;
            var harmony = new Harmony("whitebow.processortycoon.totalsalesclick.harmony");
            try
            {
                // Find the original ShowSalesGraph method (it's non-public)
                var original = typeof(MarketSalesGraphWindow).GetMethod("ShowSalesGraph", BindingFlags.Instance | BindingFlags.NonPublic);
                var postfix = typeof(TotalSalesClickPlugin).GetMethod("ShowSalesGraphPostfix", BindingFlags.Static | BindingFlags.NonPublic);
                if (original != null && postfix != null)
                {
                    harmony.Patch(original, postfix: new HarmonyMethod(postfix));
                    Log.LogInfo("TotalSalesClickPlugin: patched MarketSalesGraphWindow.ShowSalesGraph");
                }
                else
                {
                    Log.LogWarning("TotalSalesClickPlugin: failed to find ShowSalesGraph or postfix method for patching.");
                }
            }
            catch (Exception e)
            {
                try { Log.LogError(e.ToString()); } catch { }
            }

            Log.LogInfo("TotalSalesClickPlugin loaded: will attach click handlers to sales legend entries.");
        }

        // Postfix method that will be invoked after MarketSalesGraphWindow.ShowSalesGraph
        private static void ShowSalesGraphPostfix(MarketSalesGraphWindow __instance)
        {
            try
            {
                Log.LogInfo("TotalSalesClickPlugin: ShowSalesGraph postfix running");

                // Access the private legendHolder field via reflection
                var t = __instance.GetType();
                var fh = t.GetField("legendHolder", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (fh == null)
                {
                    Log.LogWarning("TotalSalesClickPlugin: legendHolder field not found");
                    return;
                }
                var legendHolder = fh.GetValue(__instance) as MonoBehaviour;
                if (legendHolder == null)
                {
                    Log.LogWarning("TotalSalesClickPlugin: legendHolder is null");
                    return;
                }

                // Iterate children of the legend holder and attach click handlers.
                Transform holderTransform = legendHolder.transform;
                for (int i = 0; i < holderTransform.childCount; i++)
                {
                    var child = holderTransform.GetChild(i).gameObject;

                    // Try to get the visible name text from TMP_Text components.
                    var tmps = child.GetComponentsInChildren<TMP_Text>(includeInactive: true);
                    string? cpuName = null;
                    if (tmps != null && tmps.Length > 0)
                    {
                        // Prefer the first TMP_Text that looks like a name (not a percentage/number).
                        foreach (var tcomp in tmps)
                        {
                            var txt = tcomp.text?.Trim();
                            if (string.IsNullOrEmpty(txt))
                                continue;
                            // crude heuristic: skip strings that look like percentages or pure numbers
                            if (txt.EndsWith("%") || System.Text.RegularExpressions.Regex.IsMatch(txt, "^[0-9,.]+$"))
                                continue;
                            cpuName = txt;
                            break;
                        }
                        // fallback to first
                        if (cpuName == null)
                            cpuName = tmps[0].text?.Trim();
                    }

                    if (string.IsNullOrEmpty(cpuName))
                        continue;

                    // Find CPU by name (like SpreadsheetRowClick plugin does)
                    var cpu = CpuDataProvider.Instance.GetAllCpus()?.FirstOrDefault(c => c != null && string.Equals(c.Name, cpuName, StringComparison.Ordinal));
                    if (cpu == null)
                        continue;

                    // Ensure the child can receive pointer events: add an Image with transparent color if none exists
                    var img = child.GetComponent<Image>();
                    if (img == null)
                    {
                        img = child.AddComponent<Image>();
                        img.color = new Color(0f, 0f, 0f, 0f);
                        img.raycastTarget = true;
                    }

                    // Remove any existing handler to avoid duplicates
                    var existing = child.GetComponents<LegendClickHandler>();
                    foreach (var ex in existing)
                    {
                        UnityEngine.Object.Destroy(ex);
                    }

                    // Add a click handler component and set its target CPU
                    var handler = child.AddComponent<LegendClickHandler>();
                    handler.TargetCpu = cpu;

                        Log.LogInfo($"TotalSalesClickPlugin: attached handler for '{cpuName}'");
                }
            }
            catch (Exception e)
            {
                // Log exception to Unity console but don't throw to avoid interfering with game
                    try { Log.LogError(e.ToString()); } catch { }
            }
        }

        // Small MonoBehaviour to receive clicks on legend entries.
        public class LegendClickHandler : MonoBehaviour, IPointerClickHandler
        {
            public Cpu? TargetCpu;

            public void OnPointerClick(PointerEventData eventData)
            {
                if (TargetCpu == null)
                    return;
                try
                {
                    Log.LogInfo($"TotalSalesClickPlugin: legend clicked '{TargetCpu.Name}'");
                    // Find InspectorWindow in scene
                    var inspector = Resources.FindObjectsOfTypeAll<InspectorWindow>()?.FirstOrDefault();
                    if (inspector == null)
                    {
                        return;
                    }

                    inspector.gameObject.SetActive(true);
                    inspector.transform.SetAsLastSibling();

                    // Try to set the company and cpu dropdowns inside InspectorWindow so the UI reflects the selected CPU.
                    try
                    {
                        var inspType = inspector.GetType();

                        // Get companyDropdown and cpuDropdown objects
                        var companyField = inspType.GetField("companyDropdown", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        var cpuField = inspType.GetField("cpuDropdown", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        var companyDropdownObj = companyField?.GetValue(inspector);
                        var cpuDropdownObj = cpuField?.GetValue(inspector);

                        if (companyDropdownObj != null && cpuDropdownObj != null)
                        {
                            // Build the allCompanies list the InspectorWindow uses: player first, then AI companies (excluding player/foundry)
                            object playerObj = null;
                            object[] ownersArray = Array.Empty<object>();

                            // Find Player.Instance via reflection
                            var playerType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => a.GetTypes()).FirstOrDefault(t => t.Name == "Player");
                            if (playerType != null)
                            {
                                var instProp = playerType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                                if (instProp != null)
                                    playerObj = instProp.GetValue(null);
                            }

                            // Find DataFinder.FindAllOwnersOfCompany()
                            var dfType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => a.GetTypes()).FirstOrDefault(t => t.Name == "DataFinder");
                            if (dfType != null)
                            {
                                var findMethod = dfType.GetMethod("FindAllOwnersOfCompany", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                                if (findMethod != null)
                                {
                                    var owners = findMethod.Invoke(null, null) as System.Collections.IEnumerable;
                                    if (owners != null)
                                    {
                                        var list = new System.Collections.Generic.List<object>();
                                        foreach (var o in owners)
                                            list.Add(o);
                                        ownersArray = list.ToArray();
                                    }
                                }
                            }

                            var allCompanies = new System.Collections.Generic.List<object>();
                            if (playerObj != null)
                                allCompanies.Add(playerObj);
                            // add owners filtered similarly to InspectorWindow.UpdateCompanies
                            foreach (var o in ownersArray)
                            {
                                try
                                {
                                    var companyProp = o.GetType().GetProperty("Company", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    var companyObj = companyProp?.GetValue(o);
                                    if (companyObj == null) continue;
                                    var isPlayerProp = companyObj.GetType().GetProperty("IsPlayer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    var isFoundryProp = companyObj.GetType().GetProperty("IsFoundry", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    bool isPlayer = isPlayerProp != null && (bool)isPlayerProp.GetValue(companyObj);
                                    bool isFoundry = isFoundryProp != null && (bool)isFoundryProp.GetValue(companyObj);
                                    if (!isPlayer && !isFoundry)
                                    {
                                        allCompanies.Add(o);
                                    }
                                }
                                catch { }
                            }

                            // Find target company index by matching company name
                            int companyIndex = 0;
                            for (int i = 0; i < allCompanies.Count; i++)
                            {
                                try
                                {
                                    var comp = allCompanies[i].GetType().GetProperty("Company", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(allCompanies[i]);
                                    var nameProp = comp?.GetType().GetProperty("Name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    var name = nameProp?.GetValue(comp) as string;
                                    if (name != null && name == TargetCpu.Company.Name)
                                    {
                                        companyIndex = i;
                                        break;
                                    }
                                }
                                catch { }
                            }

                            // Invoke SetSelected on companyDropdown to trigger its OnValueChanged and update the CPU dropdown
                            var setSelectedMethod = companyDropdownObj.GetType().GetMethod("SetSelected", new Type[] { typeof(int) });
                            setSelectedMethod?.Invoke(companyDropdownObj, new object[] { companyIndex });

                            // Now compute cpu index within the current company's CPU list (InspectorWindow reverses the list)
                            int cpuIndex = 0;
                            try
                            {
                                var ownerObj = allCompanies[companyIndex];
                                var getCpus = ownerObj.GetType().GetMethod("GetCpus", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                var cpusObj = getCpus?.Invoke(ownerObj, null) as System.Collections.IList;
                                if (cpusObj != null)
                                {
                                    // Build reversed list and find index
                                    for (int j = 0; j < cpusObj.Count; j++)
                                    {
                                        var idx = cpusObj.Count - 1 - j; // reversed index maps to dropdown index j
                                        var cpuObj = cpusObj[idx];
                                        var cpuNameProp = cpuObj.GetType().GetProperty("Name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                        var cpuName = cpuNameProp?.GetValue(cpuObj) as string;
                                        if (cpuName != null && cpuName == TargetCpu.Name)
                                        {
                                            cpuIndex = j;
                                            break;
                                        }
                                    }
                                }
                            }
                            catch { }

                            // Set cpu dropdown selection
                            var setCpuSelected = cpuDropdownObj.GetType().GetMethod("SetSelected", new Type[] { typeof(int) });
                            setCpuSelected?.Invoke(cpuDropdownObj, new object[] { cpuIndex });
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.LogError(ex.ToString());
                    }

                    // As a fallback, still call CpuUI.SetCpu to ensure UI updates even if dropdown manipulation failed
                    var cpuUi = inspector.GetComponentInChildren<ProcessorTycoon.Desktop.Inspector.CpuUI>(includeInactive: true);
                    if (cpuUi != null)
                    {
                        cpuUi.SetCpu(TargetCpu);
                    }

                    // Ensure the inspector has a compare button so user can open a compare inspector.
                    try
                    {
                        EnsureCompareButton(inspector);
                    }
                    catch (Exception ex)
                    {
                        Log.LogError(ex.ToString());
                    }
                }
                catch
                {
                    // swallow
                }
            }
        }

        // Ensure the inspector has a Compare button (idempotent).
        private static void EnsureCompareButton(InspectorWindow inspector)
        {
            if (inspector == null)
                return;

            // If we've already added a compare button to this inspector, skip.
            var existing = inspector.transform.Find("TotalSalesCompareButton");
            if (existing != null)
                return;

            // Create button GameObject
            var parent = inspector.transform;
            var buttonGo = new GameObject("TotalSalesCompareButton", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
            buttonGo.transform.SetParent(parent, worldPositionStays: false);
            var rt = buttonGo.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.sizeDelta = new Vector2(120f, 32f);
            rt.anchoredPosition = new Vector2(-12f, -36f);

            var img = buttonGo.GetComponent<UnityEngine.UI.Image>();
            img.color = new Color(0.2f, 0.2f, 0.2f, 0.9f);

            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(buttonGo.transform, worldPositionStays: false);
            var tmp = textGo.AddComponent<TMPro.TextMeshProUGUI>();
            tmp.text = "Compare";
            tmp.alignment = TMPro.TextAlignmentOptions.Center;
            tmp.raycastTarget = false;
            tmp.fontSize = 18f;
            tmp.color = Color.white;
            var trt = textGo.GetComponent<RectTransform>();
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;

            var btn = buttonGo.GetComponent<UnityEngine.UI.Button>();
            // Capture the inspector reference for the toggle
            var primary = inspector;
            btn.onClick.AddListener(() => ToggleCompare(primary));
        }

        private static void ToggleCompare(InspectorWindow primary)
        {
            try
            {
                if (primary == null)
                    return;

                _compareVisible = !_compareVisible;
                if (!_compareVisible)
                {
                    if (_compareInspector != null)
                    {
                        _compareInspector.gameObject.SetActive(false);
                    }
                    return;
                }

                if (_compareInspector == null)
                {
                    var clonedGo = UnityEngine.Object.Instantiate(primary.gameObject, primary.transform.parent);
                    clonedGo.name = primary.gameObject.name + " (Compare)";
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
                try { Log.LogError(e.ToString()); } catch { }
            }
        }
    }
}
