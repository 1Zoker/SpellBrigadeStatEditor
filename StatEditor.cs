using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(StatEditor.StatEditorMod), "StatEditor", "1.0.0", "1Zoker")]
[assembly: MelonGame(null, "The Spell Brigade")]

namespace StatEditor
{
    /// <summary>One line of the stat list in the menu.</summary>
    internal class Row
    {
        public StatType Type;
        public Stat Stat;
        public string Key;            // config name, e.g. "MovementSpeed"
        public string Label;          // what the menu shows, e.g. "MOVE SPEED"
        public string Display = "";
        public int Order;             // position on the main tab, -1 = "additional modifiers" tab
        public float Step = 1f;       // size of one slider notch, in the game's own units
        public float Max = 100f;      // largest bonus the slider offers
        public float Cap = float.NaN; // hard limit on the stat's total value (NaN = none)
        public float Applied;         // bonus this mod has added so far in this run
        public float Target;          // where the slider is sitting
        public float ChangedAt = -1f; // when the slider last moved (-1 = nothing waiting)
        public MelonPreferences_Entry<float> Entry;
    }

    public class StatEditorMod : MelonMod
    {
        internal static MelonLogger.Instance Log;
        internal static MelonPreferences_Category Cat;
        internal static MelonPreferences_Entry<bool> Enabled;
        internal static MelonPreferences_Entry<float> XpMultiplier;
        internal static MelonPreferences_Entry<float> GoldMultiplier;
        internal static MelonPreferences_Entry<bool> KeepBonuses;
        internal static MelonPreferences_Entry<string> FontName;

        internal static readonly List<PlayerStats> Pending = new List<PlayerStats>();
        internal static PlayerStats Local;
        internal static readonly List<Row> Rows = new List<Row>();
        private static readonly List<Row> MainRows = new List<Row>();
        private static readonly List<Row> ExtraRows = new List<Row>();

        private static bool xpHookOk, goldHookOk, statHookOk;
        private static bool menuOpen, prefsDirty;
        private static bool savedCursorVisible;
        private static CursorLockMode savedCursorLock;
        private static float nextRefresh;
        private static int tab;

        private const float MultMin = 1f, MultMax = 10f, MultStep = 0.5f;

        // The stats the game shows on its own "Your stats" panel, in the same order and wording.
        private static readonly KeyValuePair<StatType, string>[] MainStats =
        {
            new KeyValuePair<StatType, string>(StatType.MaxHealth, "MAX HEALTH"),
            new KeyValuePair<StatType, string>(StatType.HealthRegeneration, "HEALTH REGENERATION"),
            new KeyValuePair<StatType, string>(StatType.Armor, "ARMOR"),
            new KeyValuePair<StatType, string>(StatType.Dodge, "DODGE"),
            new KeyValuePair<StatType, string>(StatType.MovementSpeed, "MOVE SPEED"),
            new KeyValuePair<StatType, string>(StatType.Damage, "DAMAGE"),
            new KeyValuePair<StatType, string>(StatType.Size, "SPELL SIZE"),
            new KeyValuePair<StatType, string>(StatType.FireRate, "CAST SPEED"),
            new KeyValuePair<StatType, string>(StatType.CriticalChance, "CRITICAL CHANCE"),
            new KeyValuePair<StatType, string>(StatType.CriticalDamage, "CRITICAL DAMAGE"),
            new KeyValuePair<StatType, string>(StatType.PickupRadius, "MANA PULL"),
            new KeyValuePair<StatType, string>(StatType.XPGain, "MANA GAIN"),
            new KeyValuePair<StatType, string>(StatType.Luck, "LUCK"),
        };

        // Order of the "additional modifiers" tab: plain, useful ones first, long oddball ones last.
        private static readonly StatType[] ExtraOrder =
        {
            StatType.Rerolls, StatType.Bans, StatType.Pins, StatType.Revives, StatType.Projectiles,
            StatType.GoldGain, StatType.DropChance, StatType.Range, StatType.Speed, StatType.LifeTime,
            StatType.ReviveSpeed, StatType.ReviveZoneSize, StatType.HealOnDodge, StatType.HealOnLevelUp,
            StatType.HealingMultiplier, StatType.DamageTickRate, StatType.HitboxSizeMultiplier,
            StatType.TerrainSpeedMultiplier, StatType.HealthPercentageOnRevive, StatType.XPGainOnDamageTaken,
            StatType.ElementStrengthMultiplier, StatType.ElementWeaknessMultiplier, StatType.HealthPotionAmountMultiplier,
        };

        private static int ExtraRank(Row r)
        {
            int i = Array.IndexOf(ExtraOrder, r.Type);
            return i >= 0 ? i : 1000 + r.Label.Length;   // anything unknown goes last, longest names at the bottom
        }

        // ------------------------------------------------------------------ setup

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;
            Cat = MelonPreferences.CreateCategory("StatEditor");
            Enabled = Cat.CreateEntry("Enabled", true, description: "Master switch for the whole mod.");
            XpMultiplier = Cat.CreateEntry("XpMultiplier", 1.0f, description: "Run XP multiplier. 1.0 = vanilla. Solo or host only.");
            GoldMultiplier = Cat.CreateEntry("GoldMultiplier", 1.0f, description: "End-of-run gold multiplier. 1.0 = vanilla.");
            KeepBonuses = Cat.CreateEntry("KeepBonusesBetweenRuns", false, description: "true = stat bonuses set in the menu are re-applied at the start of every run. false = every run starts with no bonuses.");
            FontName = Cat.CreateEntry("FontName", "", description: "Menu font. Empty = Noto Sans SC ExtraBold (or another game font if that is missing), 'default' = plain built-in font. F9 in the menu cycles through the choices.");

            // Only TryAddExperience is hooked. TryAddExperienceAndSkipStores is an empty method in the
            // shipped game and shares its machine code with hundreds of other empty methods, so a hook
            // on it fires for all of them.
            xpHookOk = Patch("XP", typeof(PartyLevelManager), "TryAddExperience", nameof(Hooks.XpPrefix), null);
            goldHookOk = Patch("Gold", typeof(RunGoldCalculator), "Calculate", null, nameof(Hooks.GoldPostfix));
            statHookOk = Patch("Stats", typeof(PlayerStats), "Awake", null, nameof(Hooks.PlayerStatsAwakePostfix));

            Log.Msg("Loaded. Press F8 in game to open the menu.");
        }

        private bool Patch(string label, Type type, string method, string prefix, string postfix)
        {
            try
            {
                var target = AccessTools.Method(type, method);
                if (target == null) throw new Exception("method " + type.Name + "." + method + " not found");
                HarmonyInstance.Patch(target,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(Hooks), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(Hooks), postfix));
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(label + " hook failed: " + ex);
                return false;
            }
        }

        // ------------------------------------------------------------------ per frame

        public override void OnUpdate()
        {
            try
            {
                var kb = Keyboard.current;
                if (kb != null)
                {
                    if (kb.f8Key.wasPressedThisFrame) ToggleMenu();
                    if (menuOpen && kb.f9Key.wasPressedThisFrame) Ui.NextFont();
                }
            }
            catch (Exception) { }

            Ui.PollMouse();

            if (menuOpen)
            {
                // The game hides or locks the mouse during a run; keep it usable while the menu is up.
                try { Cursor.visible = true; Cursor.lockState = CursorLockMode.None; } catch (Exception) { }
                Ui.UpdateBlocker(true);
            }

            if (!Enabled.Value) return;

            // Find our own wizard once it has spawned.
            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                try
                {
                    var ps = Pending[i];
                    if (ps == null || ps.WasCollected) { Pending.RemoveAt(i); continue; }
                    if (!ps.IsSpawned) continue;
                    Pending.RemoveAt(i);
                    if (!ps.IsOwner) continue;
                    Local = ps;
                    ClearRows();
                }
                catch (Exception ex)
                {
                    Pending.RemoveAt(i);
                    Log.Warning("Could not check a player: " + ex.Message);
                }
            }

            if (!HasRun())
            {
                if (Rows.Count > 0) ClearRows();
                return;
            }

            float now = Time.unscaledTime;
            if (now >= nextRefresh)
            {
                nextRefresh = now + 0.25f;
                if (Rows.Count == 0) BuildRows();
                if (menuOpen) RefreshDisplay();
            }

            // Apply slider moves once the slider has been still for a moment,
            // so one drag becomes one change instead of dozens.
            for (int i = 0; i < Rows.Count; i++)
            {
                var r = Rows[i];
                if (r.ChangedAt >= 0f && now - r.ChangedAt > 0.2f)
                {
                    r.ChangedAt = -1f;
                    SetBonus(r, r.Target);
                }
            }
        }

        private static void ClearRows()
        {
            Rows.Clear(); MainRows.Clear(); ExtraRows.Clear();
        }

        private static bool HasRun()
        {
            try { return Local != null && !Local.WasCollected && Local.IsSpawned; }
            catch (Exception) { return false; }
        }

        private void ToggleMenu()
        {
            menuOpen = !menuOpen;
            try
            {
                if (menuOpen)
                {
                    savedCursorVisible = Cursor.visible;
                    savedCursorLock = Cursor.lockState;
                    nextRefresh = 0f;
                    Ui.OnOpen();
                }
                else
                {
                    Cursor.visible = savedCursorVisible;
                    Cursor.lockState = savedCursorLock;
                    Ui.UpdateBlocker(false);
                    Ui.OnClose();
                    if (prefsDirty) { MelonPreferences.Save(); prefsDirty = false; }
                }
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------ stats

        private static string Pretty(string camel)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < camel.Length; i++)
            {
                char c = camel[i];
                if (i > 0 && char.IsUpper(c) && (char.IsLower(camel[i - 1]) || (i + 1 < camel.Length && char.IsLower(camel[i + 1]))))
                    sb.Append(' ');
                sb.Append(char.ToUpperInvariant(c));
            }
            return sb.ToString();
        }

        private static void BuildRows()
        {
            try
            {
                var list = Local.GetPlayerStats();
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                {
                    var stat = list[i];
                    if (stat == null) continue;
                    var row = new Row { Stat = stat, Type = stat.StatType, Order = -1 };
                    row.Key = row.Type.ToString();
                    row.Label = Pretty(row.Key);
                    for (int k = 0; k < MainStats.Length; k++)
                        if (MainStats[k].Key == row.Type) { row.Order = k; row.Label = MainStats[k].Value; }
                    Configure(row);
                    try
                    {
                        Log.Msg("  " + row.Key + ": " + stat.StatCalculationType + ", base " + stat.BaseValue + ", now " + stat.GetValue()
                            + ", step " + row.Step + ", max bonus " + row.Max + (float.IsNaN(row.Cap) ? "" : ", cap " + row.Cap));
                    }
                    catch (Exception) { }
                    row.Entry = Cat.GetEntry<float>(row.Key) ?? Cat.CreateEntry(row.Key, 0f,
                        description: "Bonus added to " + row.Key + " at the start of each run. 0 = off.");
                    Rows.Add(row);
                    if (row.Order >= 0) MainRows.Add(row); else ExtraRows.Add(row);
                }
                MainRows.Sort((a, b) => a.Order.CompareTo(b.Order));
                ExtraRows.Sort((a, b) => ExtraRank(a).CompareTo(ExtraRank(b)));

                // Runs start with the game's own stats unless the player asked to keep bonuses.
                for (int i = 0; i < Rows.Count; i++)
                {
                    var r = Rows[i];
                    if (KeepBonuses.Value) { if (r.Entry.Value > 0f) SetBonus(r, r.Entry.Value); }
                    else if (r.Entry.Value != 0f) { r.Entry.Value = 0f; prefsDirty = true; }
                    r.Target = r.Applied;
                    if (r.Applied > r.Max) r.Max = r.Applied;
                }
                RefreshDisplay();
                Log.Msg("Run started: " + Rows.Count + " stats found.");
            }
            catch (Exception ex)
            {
                Log.Warning("Could not read stats: " + ex.Message);
            }
        }

        /// <summary>
        /// Slider notch size, largest bonus and (optionally) a hard cap on the stat's total value.
        /// All amounts are in the game's own units: whole points, where percentage stats count 1 as 1%.
        /// </summary>
        private static void Configure(Row row)
        {
            switch (row.Type)
            {
                // Your stats
                case StatType.MaxHealth: Limits(row, 10f, 10000f); break;
                case StatType.HealthRegeneration: Limits(row, 1f, 500f); break;
                case StatType.Armor: Limits(row, 1f, 500f); break;
                case StatType.Dodge: Limits(row, 1f, 100f, 100f); break;
                case StatType.MovementSpeed: Limits(row, 5f, 200f); break;   // faster than this breaks movement
                case StatType.Damage: Limits(row, 1f, 100f); break;
                case StatType.Size: Limits(row, 1f, 100f); break;
                case StatType.FireRate: Limits(row, 1f, 100f); break;
                case StatType.CriticalChance: Limits(row, 1f, 100f, 100f); break;
                case StatType.CriticalDamage: Limits(row, 5f, 500f); break;
                case StatType.PickupRadius: Limits(row, 10f, 1000f); break;
                case StatType.XPGain: Limits(row, 10f, 1000f); break;
                case StatType.Luck: Limits(row, 1f, 100f, 100f); break;

                // Additional modifiers
                case StatType.Rerolls:
                case StatType.Bans:
                case StatType.Pins:
                case StatType.Revives:
                case StatType.Projectiles: Limits(row, 1f, 20f); break;
                case StatType.GoldGain: Limits(row, 1f, 50f); break;
                case StatType.ReviveSpeed:
                case StatType.ReviveZoneSize: Limits(row, 10f, 1000f); break;
                case StatType.DropChance:
                case StatType.HealingMultiplier:
                case StatType.TerrainSpeedMultiplier:
                case StatType.ElementStrengthMultiplier:
                case StatType.ElementWeaknessMultiplier: Limits(row, 50f, 5000f); break;

                default: Limits(row, 1f, 100f); break;
            }
        }

        private static void Limits(Row row, float step, float max, float cap = float.NaN)
        {
            row.Step = step; row.Max = max; row.Cap = cap;
        }

        internal static float Snap(float value, float step)
        {
            return (float)Math.Round(Math.Round(value / step) * step, 4);
        }

        /// <summary>Move this mod's bonus on a stat to <paramref name="target"/>, respecting the cap.</summary>
        private static void SetBonus(Row row, float target)
        {
            if (!HasRun()) return;
            try
            {
                target = Snap(Math.Max(0f, target), row.Step);
                float delta = target - row.Applied;
                if (Math.Abs(delta) < 0.0001f) return;

                float before = Local.GetCharacterValue(row.Type);
                Local.AddCharacterImprovement(row.Type, delta, StatModifiersType.Static);
                row.Applied = target;

                if (!float.IsNaN(row.Cap) && delta > 0f)
                {
                    int guard = 0;
                    while (row.Applied > 0f && guard++ < 500 && Local.GetCharacterValue(row.Type) > row.Cap + 0.001f)
                    {
                        float back = Math.Min(row.Step, row.Applied);
                        Local.AddCharacterImprovement(row.Type, -back, StatModifiersType.Static);
                        row.Applied = Snap(row.Applied - back, row.Step);
                    }
                }

                row.Target = row.Applied;
                float toSave = KeepBonuses.Value ? row.Applied : 0f;
                if (row.Entry.Value != toSave) { row.Entry.Value = toSave; prefsDirty = true; }
                Log.Msg(row.Key + " bonus " + row.Applied + ": " + before + " -> " + Local.GetCharacterValue(row.Type));
            }
            catch (Exception ex)
            {
                row.Target = row.Applied;
                Log.Warning(row.Key + " could not be changed: " + ex.Message);
            }
        }

        private static void RefreshDisplay()
        {
            for (int i = 0; i < Rows.Count; i++)
            {
                var r = Rows[i];
                try
                {
                    string text = null;
                    try { text = r.Stat.ToCachedStat().DisplayText; } catch (Exception) { }
                    if (string.IsNullOrEmpty(text)) text = Local.GetCharacterValue(r.Type).ToString("0.##");
                    r.Display = text;
                }
                catch (Exception) { r.Display = "?"; }
            }
        }

        // ------------------------------------------------------------------ menu

        public override void OnGUI()
        {
            if (!menuOpen) return;
            try { DrawMenu(); }
            catch (Exception ex)
            {
                menuOpen = false;
                Ui.UpdateBlocker(false);
                Log.Error("Menu error, closing it: " + ex);
            }
        }

        // Column positions, measured from the left edge of the panel.
        private const float PanelX = 30f, PanelY = 30f, PanelW = 760f, Pad = 26f;
        private const float ColValue = 270f, ColValueW = 96f, ColStep = 392f, ColBonus = 646f;

        private static void DrawMenu()
        {
            // Lay everything out for a 1080-pixel-tall screen and scale to the real one.
            float scale = Mathf.Max(0.6f, Screen.height / 1080f);
            var oldMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            GUI.depth = -1000;
            Ui.Begin();

            bool inRun = HasRun();
            var rows = tab == 0 ? MainRows : ExtraRows;
            int statRows = inRun ? rows.Count : 0;
            float rowH = 30f;
            float maxH = Screen.height / scale - 60f;
            const float fixedH = 372f;
            if (statRows > 0 && fixedH + statRows * rowH > maxH) rowH = Mathf.Max(22f, (maxH - fixedH) / statRows);

            float x = PanelX, y = PanelY, w = PanelW;
            float h = fixedH + Mathf.Max(1, statRows) * rowH;
            float lx = x + Pad, inner = w - 2 * Pad;
            Ui.Panel = new Rect(x, y, w, h);
            Ui.Scale = scale;
            Ui.Frame(Ui.Panel);

            // Header
            Ui.Text(new Rect(lx, y + 18f, 400f, 40f), "STAT EDITOR", Ui.Title, Color.white);
            Ui.Text(new Rect(x + w - Pad - 260f, y + 16f, 260f, 20f), "F8  CLOSE", Ui.SmallRight, Ui.Dim);
            Ui.Text(new Rect(x + w - Pad - 260f, y + 36f, 260f, 20f), "F9  FONT: " + Ui.FontLabel, Ui.SmallRight, Ui.Dim);
            float cy = y + 70f;
            Ui.Fill(new Rect(lx, cy, inner, 2f), Ui.Line);
            cy += 14f;

            // Multipliers
            bool on = Ui.Check(new Rect(lx, cy, 260f, 24f), "MOD ENABLED", Enabled.Value);
            if (on != Enabled.Value) { Enabled.Value = on; prefsDirty = true; }
            cy += 34f;
            MultiplierRow(1, x, cy, "XP MULTIPLIER", XpMultiplier, xpHookOk);
            cy += 32f;
            MultiplierRow(2, x, cy, "GOLD MULTIPLIER", GoldMultiplier, goldHookOk);
            cy += 42f;

            // Tabs
            float tabW = inner / 2f - 4f;
            if (Ui.Tab(new Rect(lx, cy, tabW, 34f), "YOUR STATS", tab == 0)) tab = 0;
            if (Ui.Tab(new Rect(lx + tabW + 8f, cy, tabW, 34f), "ADDITIONAL MODIFIERS", tab == 1)) tab = 1;
            cy += 34f;
            Ui.Fill(new Rect(lx, cy, inner, 2f), Ui.Accent);
            cy += 12f;

            if (!statHookOk)
            {
                Ui.Text(new Rect(lx, cy, inner, rowH), "STAT EDITING IS UNAVAILABLE (SEE MELONLOADER/LATEST.LOG)", Ui.Label, Ui.Dim);
                cy += rowH;
            }
            else if (!inRun || statRows == 0)
            {
                Ui.Text(new Rect(lx, cy, inner, rowH), "START A RUN TO SEE AND CHANGE YOUR STATS", Ui.Label, Ui.Dim);
                cy += rowH;
            }
            else
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    var line = new Rect(lx - 8f, cy, inner + 16f, rowH);
                    if (line.Contains(Ui.MousePos)) Ui.Fill(line, Ui.Hover);
                    float mid = cy + (rowH - 24f) / 2f;
                    Ui.Text(new Rect(lx, mid, ColValue - Pad, 24f), r.Label, Ui.Label, Color.white);
                    Ui.Text(new Rect(x + ColValue, mid, ColValueW, 24f), r.Display, Ui.Value, Color.white);
                    float v = Ui.Stepper(100 + (int)r.Type, x + ColStep, mid, r.Target, 0f, r.Max, r.Step);
                    if (Math.Abs(v - r.Target) > 0.0001f)
                    {
                        r.Target = v;
                        r.ChangedAt = Time.unscaledTime;
                    }
                    bool has = r.Target > 0f;
                    string bonus = has ? "+" + r.Target.ToString(r.Step < 1f ? "0.00" : "0") : "+0";
                    if (!float.IsNaN(r.Cap)) bonus += "  CAP " + r.Cap.ToString("0");
                    Ui.Text(new Rect(x + ColBonus, mid, w - ColBonus - Pad, 24f), bonus, Ui.Label, has ? Ui.Accent : Ui.Dim);
                    cy += rowH;
                }
            }

            // Footer
            cy += 12f;
            Ui.Fill(new Rect(lx, cy, inner, 2f), Ui.Line);
            cy += 14f;
            if (Ui.Button(new Rect(lx, cy, 230f, 32f), "RESET STAT BONUSES"))
            {
                for (int i = 0; i < Rows.Count; i++) { Rows[i].Target = 0f; Rows[i].ChangedAt = 0f; }
                // Also clear saved bonuses for stats that are not on screen (for example between runs).
                foreach (var name in Enum.GetNames(typeof(StatType)))
                {
                    try
                    {
                        var saved = Cat.GetEntry<float>(name);
                        if (saved != null && saved.Value != 0f) { saved.Value = 0f; prefsDirty = true; }
                    }
                    catch (Exception) { }
                }
            }
            if (Ui.Button(new Rect(lx + 242f, cy, 230f, 32f), "RESET MULTIPLIERS"))
            {
                XpMultiplier.Value = 1f; GoldMultiplier.Value = 1f; prefsDirty = true;
            }
            cy += 44f;
            bool keep = Ui.Check(new Rect(lx, cy, inner, 24f), "KEEP STAT BONUSES BETWEEN RUNS", KeepBonuses.Value);
            if (keep != KeepBonuses.Value)
            {
                KeepBonuses.Value = keep;
                prefsDirty = true;
                for (int i = 0; i < Rows.Count; i++) Rows[i].Entry.Value = keep ? Rows[i].Applied : 0f;
            }

            Ui.End();
            GUI.matrix = oldMatrix;
        }

        private static void MultiplierRow(int id, float x, float y, string name, MelonPreferences_Entry<float> entry, bool hookOk)
        {
            Ui.Text(new Rect(x + Pad, y, ColValue - Pad, 24f), name, Ui.Label, Color.white);
            if (!hookOk)
            {
                Ui.Text(new Rect(x + ColValue, y, 420f, 24f), "UNAVAILABLE (SEE LATEST.LOG)", Ui.Label, Ui.Dim);
                return;
            }
            float shown = Mathf.Clamp(Snap(entry.Value, MultStep), MultMin, MultMax);
            float v = Ui.Stepper(id, x + ColStep, y, shown, MultMin, MultMax, MultStep);
            // Only write when the control really moved, so a hand-edited value above 10 is left alone.
            if (Math.Abs(v - shown) > 0.0001f)
            {
                entry.Value = v;
                prefsDirty = true;
            }
            Ui.Text(new Rect(x + ColValue, y, ColValueW, 24f), "x" + entry.Value.ToString("0.0"), Ui.Value, entry.Value > 1f ? Ui.Accent : Color.white);
        }

        public override void OnApplicationQuit()
        {
            try { if (prefsDirty) MelonPreferences.Save(); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Hand-made menu widgets. Unity's own buttons and sliders do not get the mouse events they need
    /// in this game, so everything here is drawn from plain rectangles and text and reads the mouse itself.
    /// </summary>
    internal static class Ui
    {
        // Colours taken from the game's own stat panel: near-black slate with a lavender stone frame.
        internal static readonly Color Back = new Color(0.086f, 0.082f, 0.106f, 0.97f);
        internal static readonly Color FrameOuter = new Color(0.60f, 0.57f, 0.72f, 1f);
        internal static readonly Color FrameInner = new Color(0.27f, 0.25f, 0.36f, 1f);
        internal static readonly Color Line = new Color(0.25f, 0.23f, 0.33f, 1f);
        internal static readonly Color Accent = new Color(0.74f, 0.66f, 1f, 1f);
        internal static readonly Color Dim = new Color(0.62f, 0.60f, 0.72f, 1f);
        internal static readonly Color Hover = new Color(1f, 1f, 1f, 0.05f);
        internal static readonly Color Track = new Color(0.22f, 0.20f, 0.30f, 1f);
        internal static readonly Color ButtonBack = new Color(0.17f, 0.16f, 0.23f, 1f);
        internal static readonly Color ButtonHover = new Color(0.30f, 0.27f, 0.42f, 1f);

        internal static GUIStyle Title, Label, Value, Center, SmallRight;
        internal static Rect Panel;
        internal static float Scale = 1f;
        internal static Vector2 MousePos;
        internal static string FontLabel = "DEFAULT";

        private static Texture2D whiteTex;
        private static GUIStyle solid;
        private static bool click;            // a left click arrived in this GUI event
        private static int activeSlider = -1;
        private static bool sawPressed;
        private static int evDown, evUp, evDrag, pollPressed, pollErrors;

        private static readonly List<Font> fonts = new List<Font>();
        private static int fontIndex = -1;     // -1 = built-in font
        private static bool fontsLoaded;

        // ---------------------------------------------------------------- frame bookkeeping

        internal static void OnOpen()
        {
            evDown = evUp = evDrag = pollPressed = pollErrors = 0;
            activeSlider = -1;
            if (!fontsLoaded) LoadFonts();
        }

        internal static void OnClose()
        {
            activeSlider = -1;
            StatEditorMod.Log.Msg("Menu input: " + evDown + " down, " + evUp + " up, " + evDrag + " drag events; button seen held on "
                + pollPressed + " frames, " + pollErrors + " read errors.");
        }

        /// <summary>Called every frame. Lets go of a slider when the mouse button comes up.</summary>
        internal static void PollMouse()
        {
            if (activeSlider == -1) return;
            try
            {
                var mouse = Mouse.current;
                if (mouse == null) return;
                if (mouse.leftButton.isPressed) { sawPressed = true; pollPressed++; }
                else if (sawPressed) { activeSlider = -1; sawPressed = false; }
            }
            catch (Exception) { pollErrors++; }
        }

        internal static void Begin()
        {
            EnsureStyles();
            click = false;
            var e = Event.current;
            if (e == null) return;
            MousePos = e.mousePosition;
            var type = e.type;
            if (type == EventType.MouseDown && e.button == 0)
            {
                evDown++;
                // A new click always ends a drag that was never released.
                activeSlider = -1;
                click = true;
            }
            else if (type == EventType.MouseUp) { evUp++; activeSlider = -1; }
            else if (type == EventType.MouseDrag) evDrag++;
        }

        internal static void End()
        {
            // Swallow clicks that landed on the panel so the game's own IMGUI (if any) does not see them.
            try
            {
                var e = Event.current;
                if (e != null && click && Panel.Contains(MousePos)) e.Use();
            }
            catch (Exception) { }
            GUI.color = Color.white;
            GUI.contentColor = Color.white;
        }

        private static bool Clicked(Rect r)
        {
            return click && r.Contains(MousePos);
        }

        // ---------------------------------------------------------------- drawing

        private static void EnsureStyles()
        {
            if (solid == null || whiteTex == null)
            {
                whiteTex = new Texture2D(1, 1);
                whiteTex.hideFlags = HideFlags.HideAndDontSave;
                whiteTex.SetPixel(0, 0, Color.white);
                whiteTex.Apply();
                solid = new GUIStyle();
                solid.normal.background = whiteTex;
            }
            if (Title == null)
            {
                Title = Make(30, TextAnchor.MiddleLeft);
                Label = Make(16, TextAnchor.MiddleLeft);
                Value = Make(16, TextAnchor.MiddleRight);
                Center = Make(16, TextAnchor.MiddleCenter);
                SmallRight = Make(12, TextAnchor.MiddleRight);
                ApplyFont();
            }
        }

        private static GUIStyle Make(int size, TextAnchor anchor)
        {
            var s = new GUIStyle();
            s.fontSize = size;
            s.fontStyle = FontStyle.Bold;
            s.alignment = anchor;
            s.clipping = TextClipping.Overflow;
            s.normal.textColor = Color.white;
            return s;
        }

        internal static void Fill(Rect r, Color c)
        {
            GUI.color = c;
            GUI.Box(r, "", solid);
            GUI.color = Color.white;
        }

        internal static void Outline(Rect r, Color c, float t)
        {
            Fill(new Rect(r.x, r.y, r.width, t), c);
            Fill(new Rect(r.x, r.y + r.height - t, r.width, t), c);
            Fill(new Rect(r.x, r.y + t, t, r.height - 2 * t), c);
            Fill(new Rect(r.x + r.width - t, r.y + t, t, r.height - 2 * t), c);
        }

        internal static void Text(Rect r, string text, GUIStyle style, Color c)
        {
            GUI.contentColor = c;
            GUI.Label(r, text, style);
            GUI.contentColor = Color.white;
        }

        /// <summary>The panel: dark slate, a pale stone border, a thin inner line and studded corners.</summary>
        internal static void Frame(Rect r)
        {
            Fill(r, Back);
            Outline(r, FrameOuter, 4f);
            Outline(new Rect(r.x + 8f, r.y + 8f, r.width - 16f, r.height - 16f), FrameInner, 1f);
            const float s = 16f;
            Corner(r.x - 4f, r.y - 4f, s);
            Corner(r.x + r.width - s + 4f, r.y - 4f, s);
            Corner(r.x - 4f, r.y + r.height - s + 4f, s);
            Corner(r.x + r.width - s + 4f, r.y + r.height - s + 4f, s);
        }

        private static void Corner(float x, float y, float s)
        {
            Fill(new Rect(x, y, s, s), FrameOuter);
            Fill(new Rect(x + 3f, y + 3f, s - 6f, s - 6f), Back);
            Fill(new Rect(x + 6f, y + 6f, s - 12f, s - 12f), Accent);
        }

        // ---------------------------------------------------------------- widgets

        internal static bool Button(Rect r, string text)
        {
            bool hover = r.Contains(MousePos);
            Fill(r, hover ? ButtonHover : ButtonBack);
            Outline(r, hover ? Accent : FrameInner, 2f);
            Text(r, text, Center, Color.white);
            return Clicked(r);
        }

        internal static bool Tab(Rect r, string text, bool selected)
        {
            bool hover = r.Contains(MousePos);
            Fill(r, selected ? Accent : hover ? ButtonHover : ButtonBack);
            Text(r, text, Center, selected ? Back : Color.white);
            return Clicked(r);
        }

        internal static bool Check(Rect r, string text, bool value)
        {
            var box = new Rect(r.x, r.y + (r.height - 20f) / 2f, 20f, 20f);
            Fill(box, ButtonBack);
            Outline(box, r.Contains(MousePos) ? Accent : FrameOuter, 2f);
            if (value) Fill(new Rect(box.x + 5f, box.y + 5f, 10f, 10f), Accent);
            Text(new Rect(r.x + 30f, r.y, r.width - 30f, r.height), text, Label, Color.white);
            // Only the box and its caption are clickable, not the whole width of the panel.
            return Clicked(new Rect(r.x, r.y, Mathf.Min(r.width, 420f), r.height)) ? !value : value;
        }

        private static float Slider(int id, Rect r, float value, float min, float max)
        {
            var grab = new Rect(r.x - 8f, r.y - 4f, r.width + 16f, r.height + 8f);
            if (Clicked(grab)) { activeSlider = id; sawPressed = false; }
            if (activeSlider == id)
            {
                // Follow the mouse on every GUI event while the slider is held, not only on drag events.
                float f = Mathf.Clamp01((MousePos.x - r.x) / r.width);
                value = min + f * (max - min);
            }
            float t = max > min ? Mathf.Clamp01((value - min) / (max - min)) : 0f;
            float mid = r.y + r.height / 2f;
            bool hot = activeSlider == id || grab.Contains(MousePos);
            Fill(new Rect(r.x, mid - 3f, r.width, 6f), Track);
            Fill(new Rect(r.x, mid - 3f, r.width * t, 6f), Accent);
            float hx = r.x + r.width * t;
            Fill(new Rect(hx - 6f, mid - 10f, 12f, 20f), hot ? Color.white : FrameOuter);
            Fill(new Rect(hx - 2f, mid - 6f, 4f, 12f), Back);
            return value;
        }

        /// <summary>[-] slider [+] in one go. Returns the new value, snapped to the step.</summary>
        internal static float Stepper(int id, float x, float y, float value, float min, float max, float step)
        {
            if (Button(new Rect(x, y, 26f, 24f), "-")) value -= step;
            value = Slider(id, new Rect(x + 40f, y + 2f, 160f, 20f), value, min, max);
            if (Button(new Rect(x + 214f, y, 26f, 24f), "+")) value += step;
            return Mathf.Clamp(StatEditorMod.Snap(value, step), min, max);
        }

        // ---------------------------------------------------------------- fonts

        private static void LoadFonts()
        {
            fontsLoaded = true;
            try
            {
                var found = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Font>());
                var names = new List<string>();
                for (int i = 0; i < found.Length; i++)
                {
                    var f = found[i] == null ? null : found[i].TryCast<Font>();
                    if (f == null) continue;
                    bool dynamic = false;
                    try { dynamic = f.dynamic; } catch (Exception) { }
                    names.Add(f.name + (dynamic ? "" : " (fixed size, skipped)"));
                    if (!dynamic) continue;
                    bool have = false;
                    for (int k = 0; k < fonts.Count; k++) if (fonts[k].name == f.name) have = true;
                    if (!have) fonts.Add(f);
                }
                StatEditorMod.Log.Msg("Fonts found: " + (names.Count == 0 ? "none" : string.Join(", ", names)));

                string wanted = StatEditorMod.FontName.Value ?? "";
                fontIndex = -1;
                if (wanted != "default")
                {
                    for (int i = 0; i < fonts.Count && fontIndex == -1; i++)
                        if (fonts[i].name == wanted) fontIndex = i;
                    // No saved choice: Noto Sans SC ExtraBold if the game has it loaded...
                    for (int i = 0; i < fonts.Count && fontIndex == -1 && wanted == ""; i++)
                    {
                        string n = fonts[i].name.Replace("-", "").Replace(" ", "").Replace("_", "").ToLowerInvariant();
                        if (n.Contains("notosanssc") && n.Contains("extrabold")) fontIndex = i;
                    }
                    // ...otherwise the first font that is not one of Unity's stock ones.
                    for (int i = 0; i < fonts.Count && fontIndex == -1 && wanted == ""; i++)
                    {
                        string n = fonts[i].name;
                        if (n.IndexOf("Arial", StringComparison.OrdinalIgnoreCase) < 0
                            && n.IndexOf("Legacy", StringComparison.OrdinalIgnoreCase) < 0
                            && n.IndexOf("Liberation", StringComparison.OrdinalIgnoreCase) < 0)
                            fontIndex = i;
                    }
                }
                ApplyFont();
            }
            catch (Exception ex)
            {
                StatEditorMod.Log.Warning("Could not look up game fonts: " + ex.Message);
            }
        }

        internal static void NextFont()
        {
            if (!fontsLoaded) LoadFonts();
            fontIndex++;
            if (fontIndex >= fonts.Count) fontIndex = -1;
            ApplyFont();
            StatEditorMod.FontName.Value = fontIndex == -1 ? "default" : fonts[fontIndex].name;
            MelonPreferences.Save();
        }

        private static void ApplyFont()
        {
            try
            {
                Font f = fontIndex >= 0 && fontIndex < fonts.Count ? fonts[fontIndex] : null;
                if (f != null && f.WasCollected) f = null;
                FontLabel = f == null ? "DEFAULT" : f.name.ToUpperInvariant();
                if (Title == null) return;
                Title.font = f; Label.font = f; Value.font = f; Center.font = f; SmallRight.font = f;
            }
            catch (Exception) { }
        }

        // ---------------------------------------------------------------- click blocker

        private static GameObject blockerRoot;
        private static RectTransform blockerRect;
        private static bool blockerFailed;

        /// <summary>
        /// An invisible panel on the game's own UI system, sitting on top of everything and exactly
        /// behind the menu, so clicks on the menu do not reach the game's buttons underneath.
        /// </summary>
        internal static void UpdateBlocker(bool show)
        {
            if (blockerFailed) return;
            try
            {
                if (blockerRoot == null || blockerRoot.WasCollected)
                {
                    if (!show) return;
                    blockerRoot = new GameObject("StatEditorClickBlocker");
                    UnityEngine.Object.DontDestroyOnLoad(blockerRoot);
                    var canvas = blockerRoot.AddComponent<Canvas>();
                    canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    canvas.sortingOrder = 32000;
                    blockerRoot.AddComponent<UnityEngine.UI.GraphicRaycaster>();
                    var child = new GameObject("Area");
                    child.transform.SetParent(blockerRoot.transform, false);
                    var image = child.AddComponent<UnityEngine.UI.Image>();
                    image.color = new Color(0f, 0f, 0f, 0f);
                    image.raycastTarget = true;
                    blockerRect = child.GetComponent<RectTransform>();
                }
                if (blockerRoot.activeSelf != show) blockerRoot.SetActive(show);
                if (!show || blockerRect == null || Panel.width <= 0f) return;

                float sw = Screen.width, sh = Screen.height;
                float left = (Panel.x - 8f) * Scale / sw, right = (Panel.x + Panel.width + 8f) * Scale / sw;
                float top = 1f - (Panel.y - 8f) * Scale / sh, bottom = 1f - (Panel.y + Panel.height + 8f) * Scale / sh;
                blockerRect.anchorMin = new Vector2(Mathf.Clamp01(left), Mathf.Clamp01(bottom));
                blockerRect.anchorMax = new Vector2(Mathf.Clamp01(right), Mathf.Clamp01(top));
                blockerRect.offsetMin = Vector2.zero;
                blockerRect.offsetMax = Vector2.zero;
            }
            catch (Exception ex)
            {
                blockerFailed = true;
                StatEditorMod.Log.Warning("Click blocker unavailable, clicks may reach the game behind the menu: " + ex.Message);
            }
        }
    }

    internal static class Hooks
    {
        // Every wizard gets a PlayerStats component; remember it until we know whose it is.
        public static void PlayerStatsAwakePostfix(PlayerStats __instance)
        {
            StatEditorMod.Pending.Add(__instance);
        }

        // Multiplies party XP as it is added.
        public static void XpPrefix(ref float experience)
        {
            if (StatEditorMod.Enabled.Value && experience > 0f)
                experience *= StatEditorMod.XpMultiplier.Value;
        }

        // Scale every part of the end-of-run gold, so the reward screen adds up.
        public static void GoldPostfix(EarnedGold __result)
        {
            try
            {
                if (__result == null || !StatEditorMod.Enabled.Value) return;
                float m = StatEditorMod.GoldMultiplier.Value;
                if (m <= 0f || Math.Abs(m - 1f) < 0.0001f) return;
                __result._BaseGold_k__BackingField = Scale(__result._BaseGold_k__BackingField, m);
                __result._BonusGoldFromFinalUpgrade_k__BackingField = Scale(__result._BonusGoldFromFinalUpgrade_k__BackingField, m);
                __result._BonusGoldFromGoldGainStat_k__BackingField = Scale(__result._BonusGoldFromGoldGainStat_k__BackingField, m);
                __result._BonusGoldFromCurses_k__BackingField = Scale(__result._BonusGoldFromCurses_k__BackingField, m);
                __result._BonusGoldFromDifficulty_k__BackingField = Scale(__result._BonusGoldFromDifficulty_k__BackingField, m);
                __result._BonusGoldFromUnusedCharacter_k__BackingField = Scale(__result._BonusGoldFromUnusedCharacter_k__BackingField, m);
            }
            catch (Exception ex)
            {
                StatEditorMod.Log.Warning("Gold multiplier failed: " + ex.Message);
            }
        }

        private static int Scale(int gold, float m)
        {
            return (int)Math.Round(gold * (double)m);
        }
    }
}
