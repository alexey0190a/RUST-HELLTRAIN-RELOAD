
using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;
using System.Text;

namespace Oxide.Plugins
{
    [Info("KitsSuite", "HELLBLOOD", "0.3.9")]
    [Description("Kits 2.0 — functional core with full-card PNG preview, invisible action hitbox, cooldown & perms.")]
    public class KitsSuite : RustPlugin
    {
		
		object isKit(string kitName)
{
    if (string.IsNullOrWhiteSpace(kitName) || _config?.Kits == null) return false;
    return _config.Kits.Any(k => k != null &&
        !string.IsNullOrWhiteSpace(k.Name) &&
        string.Equals(k.Name, kitName.Trim(), StringComparison.OrdinalIgnoreCase));
}

		
        // Use default attachments for weapons that lack explicit Mods in JSON
        private bool _useDefaultWeaponMods = true;

        [PluginReference] private Plugin ImageLibrary;


        // ===== Versioning =====
        private const string PluginVersion = "v0.3.9.c22";
        private const string BuildHash = "lucifer-claim-herald";

        private const string Prefix = "[BLOODHELL] : ";

        // ===== Permissions =====
        private const string PermAdmin = "kitssuite.admin";

        // ===== UI IDs =====
        private const string UI_MENU_ROOT = "KITSUITE_MENU_ROOT";
        private const string UI_CARD_ROOT = "KITSUITE_CARD_ROOT";
        private const string UI_EDITOR_ROOT = "KITSUITE_EDITOR_ROOT";

        // ===== State =====
        private readonly HashSet<ulong> _menuOpen = new HashSet<ulong>();
        private readonly HashSet<ulong> _cardOpen = new HashSet<ulong>();
        private readonly HashSet<ulong> _editorOpen = new HashSet<ulong>();

        private readonly HashSet<ulong> _menuEditorOpen = new HashSet<ulong>();
        // ===== Data =====
        private ConfigData _config;
        private DynamicConfigFile _cfgFile;
        // Temp edit buffers per slot (not saved until Save)
        private readonly KitDef[] _editBuffer = new KitDef[20];
        // Cooldowns: player -> slot -> nextTime
        private readonly Dictionary<ulong, Dictionary<int, double>> _cooldowns = new Dictionary<ulong, Dictionary<int, double>>();

        // Debug
        private bool _debug = true; // toggleable via /kitdebug
        private string _lastGiveFailReason = string.Empty;

        #region Data Models

        [Serializable]
        public class ConfigData
        {
            public KitDef[] Kits = new KitDef[20];
            public bool DebugHighlight = true;
            public float[][] MenuCells = new float[6][];
            public float[] MainMenuRect;
            // NEW: optional background for /kit menu (drawn under buttons)
            public string MenuBackgroundKey = "";
            public string MenuBackgroundUrl = "";
            public float[] MenuCloseRect = new float[] { 0.96f, 0.94f, 0.995f, 0.99f };
        }

        [Serializable]
        public class CooldownsData
        {
            public Dictionary<string, Dictionary<string, double>> Data = new Dictionary<string, Dictionary<string, double>>();
        }

        [Serializable]
        public class KitDef
        {
            public string CardArtKey = null;
            public float[] CloseRect = new float[] { 0.96f, 0.94f, 0.995f, 0.99f };
            public float[] EditRect  = new float[] { 0.02f, 0.01f, 0.14f, 0.06f };
            public string Name = "";
            public string Permission = "";
            public int CooldownSeconds = 600;
            public string GiveMessage = "";

            // Full-card PNG (covers entire card BG)
            public string CardArtUrl = "";

            // Invisible action button rectangle on the card PNG [xmin, ymin, xmax, ymax] normalized (0..1)
            public float[] ActionRect = new float[] { 0.06f, 0.05f, 0.94f, 0.17f };

            // Future: different PNGs for the action states; reserved (not used yet to avoid mechanics change)
            public string BtnPng_Take = "";
            public string BtnPng_Cooldown = "";
            public string BtnPng_Locked = "";

            public List<ItemEntry> Main = new List<ItemEntry>();
            public List<ItemEntry> Belt = new List<ItemEntry>();
            public List<ItemEntry> Wear = new List<ItemEntry>();
        }

        [Serializable]
        public class ItemEntry
        {
            public string Shortname;
            public int Amount;
            public ulong Skin;
            public int Ammo;        // for weapons (primary)
            public int Magazine;    // current magazine count (best-effort)
            public int Slot = -1;   // slot index in its container
            public string Container; 
            // Weapon attachments (shortnames)
            public List<string> Mods = new List<string>();
// "main","belt","wear"

            public ItemEntry() { }
            public ItemEntry(string sn, int amount, ulong skin = 0, int ammo = 0, int mag = 0, int slot = -1, string container = null)
            {
                Shortname = sn;
                Amount = amount;
                Skin = skin;
                Ammo = ammo;
                Magazine = mag;
                Slot = slot;
                Container = container;
            }
        }

        private void SafeDestroyAllUI(BasePlayer player)
        {
            if (player == null) return;
            CloseEditor(player);
            CloseCard(player);
            CloseMenu(player);
        }
        
        private static List<string> DefaultModsFor(string shortname)
        {
            if (string.IsNullOrEmpty(shortname)) return null;
            if (shortname.StartsWith("rifle.", StringComparison.OrdinalIgnoreCase))
                return new List<string>{ "weapon.mod.holosight", "weapon.mod.lasersight", "weapon.mod.muzzlebrake" };
            if (shortname.StartsWith("smg.", StringComparison.OrdinalIgnoreCase))
                return new List<string>{ "weapon.mod.holosight", "weapon.mod.lasersight" };
            if (shortname.StartsWith("lmg.", StringComparison.OrdinalIgnoreCase))
                return new List<string>{ "weapon.mod.holosight", "weapon.mod.lasersight" };
            // keep pistols/shotguns clean by default to avoid incompat
            return null;
        }

        #endregion

        #region Hooks

        private void Init()
        {
            Puts($"[KitsSuite] Loaded {PluginVersion} ({BuildHash})");

            permission.RegisterPermission(PermAdmin, this);
            LoadAll();
            RegisterKitPermissions();

            EnsureBackfill();
}
        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            _menuOpen.Remove(player.userID);
            _menuEditorOpen.Remove(player.userID);
            _cardOpen.Remove(player.userID);
            _editorOpen.Remove(player.userID);
            SafeDestroyAllUI(player);
        }


        private void Unload()
        {
            SaveCooldowns();
            foreach (var player in BasePlayer.activePlayerList)
                SafeDestroyAllUI(player);
        }

        #endregion

        #region Config

        protected override void LoadDefaultConfig()
        {
            _config = new ConfigData();
            _config.MenuBackgroundKey = "";
            _config.MenuBackgroundUrl = "";
            _config.MenuCloseRect = new float[] { 0.96f, 0.94f, 0.995f, 0.99f };
            for (int i = 0; i < 6; i++) {
                _config.Kits[i] = new KitDef
                {
                    Name = $"Kit {i + 1}",
                    Permission = $"kitssuite.slot{i + 1}",
                    CooldownSeconds = 600,
                    CardArtUrl = "" // none by default
                };
            }
            SaveConfig();
        }

        private void LoadAll()
        {
            _cfgFile = Interface.Oxide.DataFileSystem.GetFile($"{Name}.cooldowns");
            try
            {
                _config = Config.ReadObject<ConfigData>();
                
            // Ensure Kits has 20 slots (migration from 12)
            if (_config.Kits == null) _config.Kits = new KitDef[20];
            if (_config.Kits.Length != 20) {
                var old = _config.Kits;
                var newer = new KitDef[20];
                int copy = Math.Min(old.Length, newer.Length);
                for (int i = 0; i < copy; i++) newer[i] = old[i];
                _config.Kits = newer;
            }
// Backfill newly added fields for old configs
                for (int i = 0; i < _config.Kits.Length; i++)
                {
                    if (_config.Kits[i] == null) _config.Kits[i] = new KitDef();
                    if (_config.Kits[i].ActionRect == null || _config.Kits[i].ActionRect.Length != 4)
                        _config.Kits[i].ActionRect = new float[] { 0.06f, 0.05f, 0.94f, 0.17f };
                    if (_config.Kits[i].CardArtUrl == null) _config.Kits[i].CardArtUrl = "";
                    if (_config.Kits[i].GiveMessage == null) _config.Kits[i].GiveMessage = string.Empty;
                }
                if (_config.MenuBackgroundKey == null) _config.MenuBackgroundKey = string.Empty;
                if (_config.MenuCloseRect == null || _config.MenuCloseRect.Length != 4) _config.MenuCloseRect = new float[] { 0.96f, 0.94f, 0.995f, 0.99f };
                if (_config.MenuBackgroundUrl == null) _config.MenuBackgroundUrl = string.Empty;
            }
            catch
            {
                PrintWarning("Config invalid or missing, creating default.");
                LoadDefaultConfig();
                SaveConfig();
            }
        
            EnsureHiddenKitNames();
            LoadCooldowns();
}

        private void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void RegisterKitPermissions()
        {
            foreach (var kit in _config.Kits)
            {
                if (!string.IsNullOrEmpty(kit.Permission))
                    permission.RegisterPermission(kit.Permission, this);
            }
        }

        #endregion

        #region Chat Commands (helper admin setters until full UI inputs)

        [ChatCommand("kitname")]
        private void CmdKitName(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player)) { player.ChatMessage(Prefix + "Недостаточно прав."); return; }
            if (args.Length < 2) { player.ChatMessage(Prefix + "Использование: /kitname <slot 1-6> <name>"); return; }
            if (!int.TryParse(args[0], out int slot) || slot < 1 || slot > 20) { player.ChatMessage(Prefix + "Слот 1..6"); return; }
            string name = string.Join(" ", args.Skip(1).ToArray());
            GetEdit(slot - 1).Name = name;
            player.ChatMessage($"[BLOODHELL] Имя для слота {slot}: {name} (в редакторе → Save).");
        }

        [ChatCommand("kitperm")]
        private void CmdKitPerm(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player)) { player.ChatMessage(Prefix + "Недостаточно прав."); return; }
            if (args.Length < 2) { player.ChatMessage(Prefix + "Использование: /kitperm <slot 1-6> <permission>"); return; }
            if (!int.TryParse(args[0], out int slot) || slot < 1 || slot > 20) { player.ChatMessage(Prefix + "Слот 1..6"); return; }
            string perm = args[1];
            GetEdit(slot - 1).Permission = perm;
            permission.RegisterPermission(perm, this);
            player.ChatMessage($"[BLOODHELL] Permission для слота {slot}: {perm} (в редакторе → Save).");
        }

        [ChatCommand("kitcd")]
        private void CmdKitCd(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player)) { player.ChatMessage(Prefix + "Недостаточно прав."); return; }
            if (args.Length < 2) { player.ChatMessage(Prefix + "Использование: /kitcd <slot 1-6> <seconds>"); return; }
            if (!int.TryParse(args[0], out int slot) || slot < 1 || slot > 20) { player.ChatMessage(Prefix + "Слот 1..6"); return; }
            if (!int.TryParse(args[1], out int sec) || sec < 0) { player.ChatMessage(Prefix + "seconds >= 0"); return; }
            GetEdit(slot - 1).CooldownSeconds = sec;
            player.ChatMessage($"[BLOODHELL] КД для слота {slot}: {sec} сек (в редакторе → Save).");
        }

        [ChatCommand("kiturl")]
        private void CmdKitUrl(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player)) { player.ChatMessage(Prefix + "Недостаточно прав."); return; }
            if (args.Length < 2) { player.ChatMessage(Prefix + "Использование: /kiturl <slot 1-6> <https-url>"); return; }
            if (!int.TryParse(args[0], out int slot) || slot < 1 || slot > 20) { player.ChatMessage(Prefix + "Слот 1..6"); return; }
            string url = string.Join(" ", args.Skip(1).ToArray());
            GetEdit(slot - 1).CardArtUrl = url;
            player.ChatMessage($"[BLOODHELL] Card PNG URL для слота {slot} установлен (в редакторе → Save).");
        }

        [ChatCommand("kithitbox")]
        private void CmdKitHitbox(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player)) { player.ChatMessage(Prefix + "Недостаточно прав."); return; }
            if (args.Length < 5) { player.ChatMessage(Prefix + "Использование: /kithitbox <slot 1-6> <xmin> <ymin> <xmax> <ymax>"); return; }
            if (!int.TryParse(args[0], out int slot) || slot < 1 || slot > 20) { player.ChatMessage(Prefix + "Слот 1..6"); return; }
            float xmin, ymin, xmax, ymax;
            if (!float.TryParse(args[1], out xmin)
                || !float.TryParse(args[2], out ymin)
                || !float.TryParse(args[3], out xmax)
                || !float.TryParse(args[4], out ymax))
            {
                player.ChatMessage(Prefix + "Координаты должны быть числами 0..1");
                return;
            }
            xmin = Mathf.Clamp01(xmin); ymin = Mathf.Clamp01(ymin); xmax = Mathf.Clamp01(xmax); ymax = Mathf.Clamp01(ymax);
            var ed = GetEdit(slot - 1);
            ed.ActionRect = new float[] { xmin, ymin, xmax, ymax };
            player.ChatMessage($"[BLOODHELL] Hitbox для слота {slot} обновлён: [{xmin} {ymin} {xmax} {ymax}] (в редакторе → Save).");
        }

        [ChatCommand("kitdebug")]
        private void CmdKitDebug(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player)) { player.ChatMessage(Prefix + "Недостаточно прав."); return; }
            _debug = !_debug;
            player.ChatMessage($"[BLOODHELL] Debug UI: {(_debug ? "ON" : "OFF")}");
        }

        #endregion

        #region /kit

        [ChatCommand("kit")]
        private void CmdKit(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            OpenMenu(player);
        }

        #endregion

        #region UI — Menu / Card / Editor

        private void OpenMenu(BasePlayer player)
        {
if (player == null) return;

    // close previous UI parts
    CloseCard(player);
    CloseEditor(player);
    CloseMenu(player);

    var ui = new CuiElementContainer();

    // Root
    ui.Add(new CuiElement
    {
        Name = UI_MENU_ROOT,
        Parent = "Overlay",
        Components =
        {
            new CuiRawImageComponent{ Color = "0 0 0 0" },
            new CuiNeedsCursorComponent(),
            new CuiRectTransformComponent{ AnchorMin = "0 0", AnchorMax = "1 1" }
        }
    });
    _menuOpen.Add(player.userID);

    // Dim
    AddPanel(ui, UI_MENU_ROOT, "0 0", "1 1", "0.1 0.1 0.1 0.8", "KITSUITE_MENU_BG");

    // === Background image in a dedicated child container (always below) ===
    if (_config.MainMenuRect == null || _config.MainMenuRect.Length != 4)
        _config.MainMenuRect = new float[] { 0.12f, 0.18f, 0.88f, 0.88f };
    var Rimg = _config.MainMenuRect;
    string bgMin = $"{Rimg[0]} {Rimg[1]}";
    string bgMax = $"{Rimg[2]} {Rimg[3]}";

    // child container for image
    ui.Add(new CuiElement
    {
        Name = "KITSUITE_MENU_IMG",
        Parent = "KITSUITE_MENU_BG",
        Components =
        {
            new CuiRawImageComponent{ Color = "0 0 0 0" },
            new CuiRectTransformComponent{ AnchorMin = bgMin, AnchorMax = bgMax }
        }
    });
    if (!string.IsNullOrEmpty(_config.MenuBackgroundKey))
        AddImageFromKeyOrUrl(ui, "KITSUITE_MENU_IMG", "0 0", "1 1", _config.MenuBackgroundKey);
    else if (!string.IsNullOrEmpty(_config.MenuBackgroundUrl))
        AddImageFromKeyOrUrl(ui, "KITSUITE_MENU_IMG", "0 0", "1 1", _config.MenuBackgroundUrl);

    var __mcr = _config.MenuCloseRect ?? new float[] { 0.96f, 0.94f, 0.995f, 0.99f };

    // === Admin controls (above) ===
    if (IsAdmin(player))
    {
        AddButton(ui, "KITSUITE_MENU_BG", "0.015 0.94", "0.24 0.985", "0.2 0.4 0.2 0.95", "Настройка позиций меню", $"{nameof(Console_KS_MenuToggleEditor)}");
        AddButton(ui, "KITSUITE_MENU_BG", "0.245 0.94", "0.395 0.985", "0.2 0.2 0.4 0.95", $"Debug: {(_debug ? "ON" : "OFF")}", "Console_KS_ToggleGlobalDebug");
    }

	// Кнопка "Закрыть" — для всех игроков
AddButton(ui, "KITSUITE_MENU_BG", $"{__mcr[0]} {__mcr[1]}", $"{__mcr[2]} {__mcr[3]}",
    "0 0 0 0", "", $"{nameof(Console_KS_CloseMenu)}");
    if (this._debug) AddBorder(ui, "KITSUITE_MENU_BG", __mcr, 1.5f/1000f, "1 0 0 0.9");

    // === Slots grid ===
    var R = _config.MainMenuRect;
    float rx = R[0], ry = R[1], rw = Mathf.Clamp01(R[2]-R[0]), rh = Mathf.Clamp01(R[3]-R[1]);

    bool haveCells = (_config.MenuCells != null && _config.MenuCells.Length == 6 && _config.MenuCells[0] != null);
    if (!haveCells)
    {
        float gap = 0.02f;
        int rows = 2, cols = 3;
        float cellW = (rw - gap * (cols + 1)) / cols;
        float cellH = (rh - gap * (rows + 1)) / rows;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                int slotIndex = r*cols + c;
                float xMin = rx + gap + c * (cellW + gap);
                float yTopInner = ry + rh - gap;
                float yMin = yTopInner - (r + 1) * cellH - r * gap;
                float xMax = xMin + cellW;
                float yMax = yMin + cellH;

                string cellName = $"KITSUITE_MENU_CELL_{slotIndex}";
                AddPanel(ui, "KITSUITE_MENU_BG", $"{xMin} {yMin}", $"{xMax} {yMax}", "0.6 0 0 0.25", cellName);
                var kitName = (slotIndex < _config.Kits.Length && _config.Kits[slotIndex] != null) ? (_config.Kits[slotIndex].Name ?? string.Empty) : string.Empty;
                AddLabel(ui, cellName, kitName, "0.05 0.85", "0.95 0.98", 14, "1 1 1 1");
                AddButton(ui, cellName, "0 0", "1 1", "0 0 0 0", "", $"{nameof(Console_KS_OpenCard)} {slotIndex}");
            }
        }
    }
    else
    {
        for (int i = 0; i < _config.Kits.Length; i++)
        {
            var cr = (_config.MenuCells != null && _config.MenuCells.Length > i) ? _config.MenuCells[i] : null;
            if (cr == null || cr.Length != 4) continue;
            float xMin = rx + cr[0] * rw;
            float yMin = ry + cr[1] * rh;
            float xMax = rx + cr[2] * rw;
            float yMax = ry + cr[3] * rh;

            string cellName = $"KITSUITE_MENU_CELL_{i}";
            AddPanel(ui, "KITSUITE_MENU_BG", $"{xMin} {yMin}", $"{xMax} {yMax}", "0.6 0 0 0.25", cellName);
            var kitName = (i < _config.Kits.Length && _config.Kits[i] != null) ? (_config.Kits[i].Name ?? string.Empty) : string.Empty;
            AddLabel(ui, cellName, kitName, "0.05 0.85", "0.95 0.98", 14, "1 1 1 1");
            AddButton(ui, cellName, "0 0", "1 1", "0 0 0 0", "", $"{nameof(Console_KS_OpenCard)} {i}");
        }
    }

    CuiHelper.AddUi(player, ui);
}


        private void CloseMenu(BasePlayer player)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, UI_MENU_ROOT);
            _menuOpen.Remove(player.userID);
        }

        [ConsoleCommand(nameof(Console_KS_CloseMenu))]
        private void Console_KS_CloseMenu(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null) return;
            CloseMenu(player);
        }

        [ConsoleCommand(nameof(Console_KS_OpenCard))]
        private void Console_KS_OpenCard(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null) return;
            int slot = 0;
            if (arg.Args != null && arg.Args.Length > 0)
            {
                int.TryParse(arg.Args[0], out slot);
            }
            slot = Mathf.Clamp(slot, 0, 19);
            OpenCard(player, slot);
        }

        private void OpenCard(BasePlayer player, int slot)
        {
            CloseCard(player);

            var ui = new CuiElementContainer();

            var root = new CuiElement
            {
                Name = UI_CARD_ROOT,
                Parent = "Overlay",
                Components =
                {
                    new CuiRawImageComponent{ Color = "0 0 0 0" },
                    new CuiNeedsCursorComponent(),
                    new CuiRectTransformComponent{ AnchorMin = "0 0", AnchorMax = "1 1" }
                }
            };
            ui.Add(root);
            ui.Add(new CuiElement{ Name = "KITSUITE_CARD_BLOCKER", Parent = UI_CARD_ROOT,
                Components = { new CuiRawImageComponent{ Color = "0 0 0 0" }, new CuiRectTransformComponent{ AnchorMin = "0 0", AnchorMax = "1 1" } } });
            _cardOpen.Add(player.userID);

            // Single container for full-card PNG
            AddPanel(ui, UI_CARD_ROOT, "0.10 0.125", "0.90 0.935", "0.08 0.08 0.08 0.96", "KITSUITE_CARD_BG");
            var kit = _config.Kits[slot];


// Full-card PNG, prefer IL key if present else URL
            string __cardKey = kit.CardArtKey;
            if (!string.IsNullOrEmpty(__cardKey))
            {
                var __png = GetILPng(__cardKey);
                if (!string.IsNullOrEmpty(__png))
                {
                    ui.Add(new CuiElement
                    {
                        Parent = "KITSUITE_CARD_BG",
                        Components =
                        {
                            new CuiRawImageComponent{ Png = __png, Color = "1 1 1 1" },
                            new CuiRectTransformComponent{ AnchorMin = "0 0", AnchorMax = "1 1" }
                        }
                    });
                }
            }
            if (string.IsNullOrEmpty(__cardKey) || ImageLibrary == null)
            {
                if (!string.IsNullOrEmpty(kit.CardArtUrl))
                {
                    ui.Add(new CuiElement
                    {
                        Parent = "KITSUITE_CARD_BG",
                        Components =
                        {
                            new CuiRawImageComponent{ Url = kit.CardArtUrl, Color = "1 1 1 1" },
                            new CuiRectTransformComponent{ AnchorMin = "0 0", AnchorMax = "1 1" }
                        }
                    });
                }
            }// Top-most transparent layer for clickable hitboxes
            ui.Add(new CuiElement{ Name = "KITSUITE_CARD_HITBOX", Parent = "KITSUITE_CARD_BG",
                Components = { new CuiRawImageComponent{ Color = "0 0 0 0" }, new CuiRectTransformComponent{ AnchorMin = "0 0", AnchorMax = "1 1" } } });
if (this._debug) AddBorder(ui, "KITSUITE_CARD_BG", 2f/1000f, "1 1 1 0.9");

            // Close (X) — top-right; isolated layer to avoid overlap
                                    // UI_LOCKED — соблюдай правило наложения CUI согласно BIO
if (IsAdmin(player))
            {
                var __er = kit.EditRect ?? new float[]{0.02f,0.01f,0.14f,0.06f};
                AddButton(ui, "KITSUITE_CARD_HITBOX", $"{__er[0]} {__er[1]}", $"{__er[2]} {__er[3]}", "0.2 0.2 0.2 0.95",
                    "Редактировать", $"{nameof(Console_KS_OpenEditor)} {slot}");
                AddButton(ui, "KITSUITE_CARD_HITBOX", $"{__er[0]+0.14f} {__er[1]}", $"{__er[0]+0.30f} {__er[3]}", "0.2 0.4 0.2 0.95", "Настройка позиций карточки", $"Console_KS_ToggleCardEditor {slot} toggle");
            }

            
                        // UI_LOCKED — соблюдай правило наложения CUI согласно BIO
if (IsAdmin(player) && IsCardEditorOpen(player, slot))
            {
                AddPanel(ui, UI_CARD_ROOT, "0.10 0.56", "0.46 0.98", "0 0 0 0.95", "KITSUITE_CARD_TOOL");
                int __target = 0; if (_cardTarget.TryGetValue(player.userID, out var t)) __target = t;
                var __act = kit.ActionRect ?? new float[] { 0.06f, 0.05f, 0.94f, 0.17f };
                var __cls = kit.CloseRect  ?? new float[] { 0.96f, 0.94f, 0.995f, 0.99f };
                var __edt = kit.EditRect   ?? new float[] { 0.02f, 0.01f, 0.14f, 0.06f };

                // Draw outlines for targets
                DrawOutline(ui, "KITSUITE_CARD_BG", __act, "CARD_ACT", "0 1 0 0.7");
                DrawOutline(ui, "KITSUITE_CARD_BG", __cls, "CARD_CLS", "1 0 0 0.7");
                DrawOutline(ui, "KITSUITE_CARD_BG", __edt, "CARD_EDT", "0 0 1 0.7");

                // Tools panel
                
                string __tname = (__target==0?"Действие": (__target==1?"X":"Редактировать"));
                AddLabel(ui, "KITSUITE_CARD_TOOL", "Настройка позиций карточки", "0.04 0.84", "0.96 0.96", 14, "1 1 1 1", TextAnchor.MiddleCenter);
                AddLabel(ui, "KITSUITE_CARD_TOOL", $"Цель: {__tname}", "0.06 0.76", "0.94 0.82", 14, "1 1 1 1", TextAnchor.MiddleCenter);

                // Select target
                AddButton(ui, "KITSUITE_CARD_TOOL", "0.06 0.70", "0.46 0.76", "0.25 0.25 0.25 0.95", "Действие", $"Console_KS_SelTarget {slot} 0");
                AddButton(ui, "KITSUITE_CARD_TOOL", "0.54 0.70", "0.94 0.76", "0.25 0.25 0.25 0.95", "Закрыть X", $"Console_KS_SelTarget {slot} 1");
                AddButton(ui, "KITSUITE_CARD_TOOL", "0.06 0.62", "0.46 0.68", "0.25 0.25 0.25 0.95", "Редактировать", $"Console_KS_SelTarget {slot} 2");

                // Move
                                AddLabel(ui, "KITSUITE_CARD_TOOL", "Сдвиг", "0.06 0.60", "0.46 0.66", 12, "1 1 1 0.8", TextAnchor.MiddleLeft);
AddButton(ui, "KITSUITE_CARD_TOOL", "0.06 0.54", "0.46 0.60", "0.25 0.25 0.25 0.95", "→ Вправо", $"Console_KS_MoveRect {slot} +x 0.005");
                AddButton(ui, "KITSUITE_CARD_TOOL", "0.54 0.54", "0.94 0.60", "0.25 0.25 0.25 0.95", "← Влево", $"Console_KS_MoveRect {slot} -x 0.005");
                AddButton(ui, "KITSUITE_CARD_TOOL", "0.06 0.46", "0.46 0.52", "0.25 0.25 0.25 0.95", "↑ Вверх", $"Console_KS_MoveRect {slot} +y 0.005");
                AddButton(ui, "KITSUITE_CARD_TOOL", "0.54 0.46", "0.94 0.52", "0.25 0.25 0.25 0.95", "↓ Вниз", $"Console_KS_MoveRect {slot} -y 0.005");

                // Resize
                                AddLabel(ui, "KITSUITE_CARD_TOOL", "Размер", "0.06 0.42", "0.46 0.48", 12, "1 1 1 0.8", TextAnchor.MiddleLeft);
AddButton(ui, "KITSUITE_CARD_TOOL", "0.06 0.38", "0.46 0.44", "0.25 0.25 0.25 0.95", "Шир+ →", $"Console_KS_ResizeRect {slot} +w 0.005");
                AddButton(ui, "KITSUITE_CARD_TOOL", "0.54 0.38", "0.94 0.44", "0.25 0.25 0.25 0.95", "Шир− ←", $"Console_KS_ResizeRect {slot} -w 0.005");
                AddButton(ui, "KITSUITE_CARD_TOOL", "0.06 0.30", "0.48 0.36", "0.25 0.25 0.25 0.95", "Выс+ ↑", $"Console_KS_ResizeRect {slot} +h 0.005");
                AddButton(ui, "KITSUITE_CARD_TOOL", "0.52 0.30", "0.94 0.36", "0.25 0.25 0.25 0.95", "Выс− ↓", $"Console_KS_ResizeRect {slot} -h 0.005");

                // Save/Reset/Close
                AddButton(ui, "KITSUITE_CARD_TOOL", "0.06 0.18", "0.46 0.26", "0.20 0.40 0.20 0.95", "Сохранить", $"Console_KS_SaveRect {slot}");
                AddButton(ui, "KITSUITE_CARD_TOOL", "0.54 0.18", "0.94 0.26", "0.40 0.20 0.20 0.95", "Сброс", $"Console_KS_ResetRect {slot} {__target}");
                AddButton(ui, "KITSUITE_CARD_TOOL", "0.06 0.08", "0.94 0.16", "0.25 0.25 0.25 0.95", "Закрыть", $"Console_KS_ToggleCardEditor {slot} toggle");
            }

            // Action button state
            _lastPlayerIdForState = player.userID; _lastSlotForState = slot;

            bool canTake = CanTake(player, slot, out _);
            string actionCmd = canTake ? $"{nameof(Console_KS_TakeKit)} {slot}" : $"{nameof(Console_KS_Reason)} {slot}";

            // Invisible action hitbox (over card art). No overlap with X and Edit by using given rect.
            var rect = kit.ActionRect ?? new float[] { 0.06f, 0.05f, 0.94f, 0.17f };
            rect = ClampRect(rect);
            // Render PNG for action button state (from IL key or URL)
            var __btnKey = GetButtonKeyForState(kit, canTake);
            AddImageFromKeyOrUrl(ui, "KITSUITE_CARD_HITBOX",  $"{rect[0]} {rect[1]}", $"{rect[2]} {rect[3]}", __btnKey);
            AddButton(ui, "KITSUITE_CARD_HITBOX", $"{rect[0]} {rect[1]}", $"{rect[2]} {rect[3]}",
                "0 0 0 0", "", actionCmd);
            if (this._debug) AddBorder(ui, "KITSUITE_CARD_BG", rect, 1.5f/1000f, canTake ? "0 1 0 0.9" : "1 1 0 0.9");

            // Admin extras: editor toggle rendering + debug outline
            
            
            var __cr = kit.CloseRect ?? new float[]{0.96f,0.94f,0.995f,0.99f};
                        // UI_LOCKED — соблюдай правило наложения CUI согласно BIO
AddButton(ui, "KITSUITE_CARD_HITBOX", $"{__cr[0]} {__cr[1]}", $"{__cr[2]} {__cr[3]}", "0 0 0 0", "",
                $"{nameof(Console_KS_CloseCard)}");
            if (this._debug) AddBorder(ui, "KITSUITE_CARD_BG", __cr, 1.5f/1000f, "1 0 0 0.9");

            
            CuiHelper.AddUi(player, ui);
            
        }
        private void CloseCard(BasePlayer player)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, UI_CARD_ROOT);
            _cardOpen.Remove(player.userID);
        }

        private void CloseEditor(BasePlayer player)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, UI_EDITOR_ROOT);
            _editorOpen.Remove(player.userID);
        }

        [ConsoleCommand(nameof(Console_KS_CloseCard))]
        private void Console_KS_CloseCard(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null) return;
            CloseCard(player);
        }

        [ConsoleCommand(nameof(Console_KS_OpenEditor))]
        private void Console_KS_OpenEditor(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null) return;
            if (!IsAdmin(player)) { player.ChatMessage(Prefix + "Недостаточно прав (kitssuite.admin)."); return; }
            int slot = 0;
            if (arg.Args != null && arg.Args.Length > 0)
                int.TryParse(arg.Args[0], out slot);
            slot = Mathf.Clamp(slot, 0, 19);
            OpenEditor(player, slot);
        }

        private void OpenEditor(BasePlayer player, int slot)
        {
            CloseEditor(player);

            var ui = new CuiElementContainer();
            var root = new CuiElement
            {
                Name = UI_EDITOR_ROOT,
                Parent = "Overlay",
                Components =
                {
                    new CuiRawImageComponent{ Color = "0 0 0 0" },
                    new CuiNeedsCursorComponent(),
                    new CuiRectTransformComponent{ AnchorMin = "0 0", AnchorMax = "1 1" }
                }
            };
            ui.Add(root);
            _editorOpen.Add(player.userID);

            // Editor background
            AddPanel(ui, UI_EDITOR_ROOT, "0.14 0.166", "0.86 0.895", "0.12 0.12 0.12 0.98", "KITSUITE_EDITOR_BG");
            if (this._debug) AddBorder(ui, "KITSUITE_EDITOR_BG", 2f/1000f, "0 1 1 0.9");

            AddLabel(ui, "KITSUITE_EDITOR_BG", $"Редактор слота {slot + 1}", "0.02 0.90", "0.98 0.98", 18, "1 1 1 1", TextAnchor.MiddleLeft);
            AddButton(ui, "KITSUITE_EDITOR_BG", "0.96 0.94", "0.995 0.99", "0.2 0.2 0.2 0.95", "X",
                $"{nameof(Console_KS_CloseEditor)}");

            var edit = GetEdit(slot);

            // Left column inputs
            AddLabel(ui, "KITSUITE_EDITOR_BG", "Название:", "0.06 0.84", "0.24 0.90", 14, "1 1 1 1", TextAnchor.MiddleLeft);
            AddTextInput(ui, "KITSUITE_EDITOR_BG", $"ed_name_{slot}", edit.Name, "0.26 0.84", "0.48 0.90",
                $"Console_KS_EditField {slot} name");

            AddLabel(ui, "KITSUITE_EDITOR_BG", "Разрешение:", "0.06 0.76", "0.24 0.82", 14, "1 1 1 1", TextAnchor.MiddleLeft);
            AddTextInput(ui, "KITSUITE_EDITOR_BG", $"ed_perm_{slot}", edit.Permission, "0.26 0.76", "0.48 0.82",
                $"Console_KS_EditField {slot} perm");

            AddLabel(ui, "KITSUITE_EDITOR_BG", "Кулдаун (сек):", "0.06 0.68", "0.24 0.74", 14, "1 1 1 1", TextAnchor.MiddleLeft);
            AddTextInput(ui, "KITSUITE_EDITOR_BG", $"ed_cd_{slot}", edit.CooldownSeconds.ToString(), "0.26 0.68", "0.48 0.74",
                $"Console_KS_EditField {slot} cd");

            // New: Card PNG URL
            AddLabel(ui, "KITSUITE_EDITOR_BG", "Card PNG URL:", "0.06 0.60", "0.24 0.66", 14, "1 1 1 1", TextAnchor.MiddleLeft);
            AddTextInput(ui, "KITSUITE_EDITOR_BG", $"ed_url_{slot}", edit.CardArtUrl ?? "", "0.26 0.60", "0.74 0.66",
                $"Console_KS_EditField {slot} url");

            // New: Action Rect
            string rectStr = $"{edit.ActionRect[0]:0.###} {edit.ActionRect[1]:0.###} {edit.ActionRect[2]:0.###} {edit.ActionRect[3]:0.###}";
            AddLabel(ui, "KITSUITE_EDITOR_BG", "ActionRect (xmin ymin xmax ymax 0..1):", "0.06 0.52", "0.48 0.58", 14, "1 1 1 1", TextAnchor.MiddleLeft);
            AddTextInput(ui, "KITSUITE_EDITOR_BG", $"ed_rect_{slot}", rectStr, "0.50 0.52", "0.74 0.58",
                $"Console_KS_EditField {slot} rect");

            // Editor grids (kept for visual reference; do not affect PNG)
            AddPanel(ui, "KITSUITE_EDITOR_BG", "0.48 0.44", "0.96 0.90", "0.10 0.10 0.10 0.85", "KITSUITE_EDITOR_GRID_MAIN"); AddBorder(ui, "KITSUITE_EDITOR_GRID_MAIN", 0.0015f, "0 1 0 0.8");
            if (this._debug) AddBorder(ui, "KITSUITE_EDITOR_GRID_MAIN", 2f/1000f, "0.9 0.9 0.9 0.9");
            DrawGridWithItems(ui, "KITSUITE_EDITOR_GRID_MAIN", 6, 4, edit.Main);

            AddPanel(ui, "KITSUITE_EDITOR_BG", "0.48 0.34", "0.96 0.41", "0.10 0.10 0.10 0.85", "KITSUITE_EDITOR_GRID_BELT"); AddBorder(ui, "KITSUITE_EDITOR_GRID_BELT", 0.0015f, "0 1 0 0.8");
            if (this._debug) AddBorder(ui, "KITSUITE_EDITOR_GRID_BELT", 2f/1000f, "0.9 0.9 0.9 0.9");
            DrawGridWithItems(ui, "KITSUITE_EDITOR_GRID_BELT", 6, 1, edit.Belt);

            AddPanel(ui, "KITSUITE_EDITOR_BG", "0.48 0.22", "0.96 0.30", "0.10 0.10 0.10 0.85", "KITSUITE_EDITOR_GRID_WEAR"); AddBorder(ui, "KITSUITE_EDITOR_GRID_WEAR", 0.0015f, "0 1 0 0.8");
            if (this._debug) AddBorder(ui, "KITSUITE_EDITOR_GRID_WEAR", 2f/1000f, "0.9 0.9 0.9 0.9");
            DrawGridWithItems(ui, "KITSUITE_EDITOR_GRID_WEAR", 8, 1, edit.Wear);

            // Admin actions
            AddButton(ui, "KITSUITE_EDITOR_BG", "0.06 0.10", "0.22 0.18", "0.25 0.25 0.25 0.95", "Import",
                $"{nameof(Console_KS_EditorAction)} {slot} import");
            AddButton(ui, "KITSUITE_EDITOR_BG", "0.26 0.10", "0.44 0.18", "0.25 0.25 0.25 0.95", "Save",
                $"{nameof(Console_KS_EditorAction)} {slot} save");

            // Small hint
            if (IsAdmin(player))
            {
                AddLabel(ui, "KITSUITE_EDITOR_BG",
                    "Import — из инвентаря, Save — сохранить слот. Card PNG и Hitbox задаются слева и сохраняются через Save.",
                    "0.06 0.02", "0.96 0.08", 12, "0.9 0.9 0.9 1", TextAnchor.MiddleLeft);
            }

            // Admin extras: editor toggle rendering + debug outline
            
            CuiHelper.AddUi(player, ui);
            
        }

        [ConsoleCommand(nameof(Console_KS_CloseEditor))]
        private void Console_KS_CloseEditor(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null) return;
            CloseEditor(player);
        }

        [ConsoleCommand(nameof(Console_KS_TakeKit))]
        private void Console_KS_TakeKit(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null) return;
            int slot = 0;
            if (arg.Args != null && arg.Args.Length >= 1) int.TryParse(arg.Args[0], out slot);
            slot = Mathf.Clamp(slot, 0, 19);

            if (!CanTake(player, slot, out var reason))
            {
                SafeDestroyAllUI(player);
                player.ChatMessage($"[BLOODHELL] Недоступно: {reason}");
                return;
            }

            var kit = _config.Kits[slot];
            if (!GiveKit(player, kit)) { SafeDestroyAllUI(player); player.ChatMessage(Prefix + "Недоступно : к сожалению твой инвентарь не резиновый,прежде чем попробовать снова освободи его!"); return; }
            SetCooldown(player.userID, slot, kit.CooldownSeconds);
            var giveMessage = (slot >= 0 && slot <= 5 && !string.IsNullOrWhiteSpace(kit.GiveMessage))
                ? kit.GiveMessage
                : "Ты получил набор! А теперь иди и <color=#ff0000>КРОМСАЙ ВСЕХ В ТРУХУ!</color>";
            player.ChatMessage(Prefix + giveMessage);
            
            
            SafeDestroyAllUI(player);
        }

        [ConsoleCommand(nameof(Console_KS_Reason))]
        private void Console_KS_Reason(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null) return;
            int slot = 0;
            if (arg.Args != null && arg.Args.Length >= 1) int.TryParse(arg.Args[0], out slot);
            slot = Mathf.Clamp(slot, 0, 19);

            SafeDestroyAllUI(player);
            CanTake(player, slot, out var reason);
            player.ChatMessage($"[BLOODHELL] Недоступно: {reason}");
        }

        [ConsoleCommand(nameof(Console_KS_EditorAction))]
        private void Console_KS_EditorAction(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null) return;
            if (!IsAdmin(player)) { player.ChatMessage(Prefix + "Недостаточно прав (kitssuite.admin)."); return; }

            int slot = 0;
            string action = "none";
            if (arg.Args != null && arg.Args.Length >= 1) int.TryParse(arg.Args[0], out slot);
            if (arg.Args != null && arg.Args.Length >= 2) action = arg.Args[1];

            slot = Mathf.Clamp(slot, 0, 19);
            switch (action)
            {
                case "import":
                {
                    var buf = GetEdit(slot);
                    ImportFromPlayerInventory(player, ref buf);
                    _editBuffer[slot] = buf;
                    player.ChatMessage($"[BLOODHELL] Импортировал инвентарь в слот {slot + 1}. Нажми Save.");
                    OpenEditor(player, slot);
                    break;
                }
                case "save":
                {
                    var buf2 = GetEdit(slot);
                    _config.Kits[slot] = Clone(buf2);
                    SaveConfig();
                    RegisterKitPermissions();
                    player.ChatMessage(Prefix + $"Набор сохранен под названием {buf2.Name}");
                    OpenEditor(player, slot);
                    break;
                }
            }
        }

        [ConsoleCommand(nameof(Console_KS_EditField))]
        private void Console_KS_EditField(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null) return;
            if (!IsAdmin(player)) return;

            int slot = 0;
            string field = null;
            string value = string.Empty;

            if (arg.Args == null || arg.Args.Length < 2) return;
            int.TryParse(arg.Args[0], out slot);
            field = arg.Args[1];
            if (arg.Args.Length >= 3)
                value = string.Join(" ", arg.Args.Skip(2).ToArray());

            slot = Mathf.Clamp(slot, 0, 19);
            var edit = GetEdit(slot);
            switch (field)
            {
                case "name":
                    edit.Name = value ?? string.Empty;
                    break;
                case "perm":
                    edit.Permission = value ?? string.Empty;
                    if (!string.IsNullOrEmpty(edit.Permission))
                        permission.RegisterPermission(edit.Permission, this);
                    break;
                case "cd":
                    int sec;
                    if (!int.TryParse(value, out sec)) sec = edit.CooldownSeconds;
                    edit.CooldownSeconds = Mathf.Clamp(sec, 0, 604800);
                    break;
                case "url":
                    edit.CardArtUrl = value ?? string.Empty;
                    break;
                case "rect":
                {
                    // Expect "xmin ymin xmax ymax"
                    var parts = (value ?? "").Split(new[] {' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 4)
                    {
                        float xmin, ymin, xmax, ymax;
                        if (float.TryParse(parts[0], out xmin) &&
                            float.TryParse(parts[1], out ymin) &&
                            float.TryParse(parts[2], out xmax) &&
                            float.TryParse(parts[3], out ymax))
                        {
                            xmin = Mathf.Clamp01(xmin); ymin = Mathf.Clamp01(ymin); xmax = Mathf.Clamp01(xmax); ymax = Mathf.Clamp01(ymax);
                            edit.ActionRect = new float[] { xmin, ymin, xmax, ymax };
                        }
                    }
                    break;
                }
            }
            _editBuffer[slot] = edit;
        }

        private bool CanTake(BasePlayer player, int slot, out string reason)
        {
            var kit = _config.Kits[slot];
            // Permission
            if (!string.IsNullOrEmpty(kit.Permission) && !permission.UserHasPermission(player.UserIDString, kit.Permission) && !IsAdmin(player))
            {
                reason = "Будь добр купи набор в магазине сервера на TEST.com";
                return false;
            }
            // Cooldown
            double now = GetNowUnix();
            double next = GetCooldown(player.userID, slot);
            if (next > now)
            {
                int remain = Mathf.CeilToInt((float)(next - now));
                int min = remain / 60;
                int sec = remain % 60;
                string timer = min > 0 ? $"{min} мин {sec} сек" : $"{sec} сек";
                reason = $"Перезарядка : ты сможешь заново получить набор через <color=#ff2400>{timer}</color>.";
                return false;
            }
            // Items present?
            if (kit.Main.Count == 0 && kit.Belt.Count == 0 && kit.Wear.Count == 0)
            {
                reason = "Ничего нет, довольствуйся тем что найдешь на свалке!";
                return false;
            }
            reason = null;
            return true;
        }

        // ===== Smart placement helpers =====
        private List<ItemContainer> CandidateContainers(BasePlayer player, string cont)
        {
            var list = new List<ItemContainer>();
            cont = string.IsNullOrEmpty(cont) ? "main" : cont.ToLowerInvariant();
            var main = player.inventory?.containerMain;
            var belt = player.inventory?.containerBelt;
            var wear = player.inventory?.containerWear;

            var ring = new List<string> { "main", "belt", "wear" };
            int startIdx = ring.IndexOf(cont);
            if (startIdx < 0) startIdx = 0;
            for (int k = 0; k < ring.Count; k++)
            {
                var name = ring[(startIdx + k) % ring.Count];
                ItemContainer c = null;
                if (name == "main") c = main;
                else if (name == "belt") c = belt;
                else if (name == "wear") c = wear;
                if (c != null) list.Add(c);
            }
            return list;
        }

        private bool TryPlaceItem(Item item, ItemEntry e, List<ItemContainer> order)
        {
            var targetSlot = e.Slot;
            if (!string.IsNullOrEmpty(e.Container) && e.Container.Equals("belt", StringComparison.OrdinalIgnoreCase) && targetSlot > 5)
                targetSlot = 5;

            // 1) Preferred slot in first container
            if (order.Count > 0 && targetSlot >= 0)
            {
                var preferred = order[0];
                if (item.MoveToContainer(preferred, targetSlot, true))
                    return true;
            }
            // 2) Any free in first container
            if (order.Count > 0)
            {
                var c0 = order[0];
                if (item.MoveToContainer(c0, -1, true))
                    return true;
            }
            // 3) Any free in rest containers
            for (int i = 1; i < order.Count; i++)
            {
                if (item.MoveToContainer(order[i], -1, true))
                    return true;
            }
            return false;
        }

        private bool TryGiveList(BasePlayer player, List<ItemEntry> list, List<Item> created)
        {
            if (list == null) return true;
            foreach (var e in list)
            {
                if (string.IsNullOrEmpty(e.Shortname) || e.Amount <= 0) continue;
                var def = ItemManager.FindItemDefinition(e.Shortname);
                if (def == null) continue;

                int amountLeft = e.Amount;
                while (amountLeft > 0)
                {
                    int stack = Mathf.Min(amountLeft, def.stackable);
                    var item = ItemManager.Create(def, stack, e.Skin);
                    if (item == null) return false;
                    // Apply weapon attachments from kit (if any)
                    try
                    {
                        var modsListPlayer = (e.Mods != null && e.Mods.Count > 0) ? e.Mods : (_useDefaultWeaponMods ? DefaultModsFor(e.Shortname) : null);
                    if (modsListPlayer != null && item != null && item.contents != null) {
                        // Resolve mods list
                        var modsListNpc = (e.Mods != null && e.Mods.Count > 0) ? e.Mods : (_useDefaultWeaponMods ? DefaultModsFor(e.Shortname) : null);
                        

                            foreach (var modSn in modsListPlayer)
                            {
                                if (string.IsNullOrEmpty(modSn)) continue;
                                var mdef = ItemManager.FindItemDefinition(modSn);
                                if (mdef == null) continue;
                                var modItem = ItemManager.Create(mdef, 1, 0);
                                if (modItem == null) continue;
                                modItem.MoveToContainer(item.contents);
                            }
                        }
                    } catch { }


                    // best-effort magazine fill
                    var held = item.GetHeldEntity() as BaseProjectile;
                    if (held != null && held.primaryMagazine != null)
                    {
                        held.primaryMagazine.contents = Mathf.Clamp(e.Magazine, 0, held.primaryMagazine.capacity);
                    }

                    var order = CandidateContainers(player, e.Container);
                    if (!TryPlaceItem(item, e, order))
                    {
                        var occupiedInfo = string.Empty;
                        var occupiedSlot = e.Slot;
                        if (!string.IsNullOrEmpty(e.Container) && e.Container.Equals("belt", StringComparison.OrdinalIgnoreCase) && occupiedSlot > 5)
                            occupiedSlot = 5;
                        if (order.Count > 0 && occupiedSlot >= 0 && occupiedSlot < order[0].capacity)
                        {
                            var occupied = order[0].GetSlot(occupiedSlot);
                            if (occupied != null && occupied.info != null)
                                occupiedInfo = $", занято: {occupied.info.shortname} x{occupied.amount}";
                        }
                        _lastGiveFailReason = string.Empty;
                        item.Remove();
                        // rollback everything
                        foreach (var it in created)
                        {
                            if (it == null) continue;
                            it.RemoveFromContainer();
                            it.Remove();
                        }
                        return false;
                    }

                    created.Add(item);
                    amountLeft -= stack;
                }
            }
            return true;
        }

        private bool GiveKit(BasePlayer player, KitDef kit)
        {
            _lastGiveFailReason = string.Empty;
            var created = new List<Item>();

            if (!TryGiveList(player, kit.Main, created)) return false;
            if (!TryGiveList(player, kit.Belt, created)) return false;
            if (!TryGiveList(player, kit.Wear, created)) return false;

            player.inventory.SendSnapshot();
            return true;
        }

        private void GiveItemsToContainer(ItemContainer container, List<ItemEntry> list)
        {
            foreach (var e in list)
            {
                if (string.IsNullOrEmpty(e.Shortname) || e.Amount <= 0) continue;
                var def = ItemManager.FindItemDefinition(e.Shortname);
                if (def == null) continue;
                var item = ItemManager.CreateByItemID(def.itemid, e.Amount, e.Skin);
                if (item == null) continue;

                // ammo / magazine best-effort for guns
                var held = item.GetHeldEntity() as BaseProjectile;
                if (held != null)
                {
                    if (e.Ammo > 0 && held.primaryMagazine != null)
                    {
                        held.primaryMagazine.ammoType = held.primaryMagazine.ammoType ?? ItemManager.FindItemDefinition("ammo.rifle");
                        held.primaryMagazine.contents = Mathf.Clamp(e.Magazine, 0, held.primaryMagazine.capacity);
                    }
                }

                int targetSlot = (e.Slot >= 0) ? e.Slot : -1;
                item.MoveToContainer(container, targetSlot, true);
            }
        }

        private void ImportFromPlayerInventory(BasePlayer player, ref KitDef buf)
        {
            var main = new List<ItemEntry>();
            var belt = new List<ItemEntry>();
            var wear = new List<ItemEntry>();

            foreach (var it in player.inventory.containerMain.itemList)
            {
                if (it?.info == null) continue;
                int pos = Mathf.Clamp(it.position, 0, 100);
                main.Add(new ItemEntry(it.info.shortname, it.amount, it.skin, 0, 0, pos, "main"));
            }

            foreach (var it in player.inventory.containerBelt.itemList)
            {
                if (it?.info == null) continue;
                int pos = Mathf.Clamp(it.position, 0, 100);
                belt.Add(new ItemEntry(it.info.shortname, it.amount, it.skin, 0, 0, pos, "belt"));
            }

            foreach (var it in player.inventory.containerWear.itemList)
            {
                if (it?.info == null) continue;
                int pos = Mathf.Clamp(it.position, 0, 100);
                wear.Add(new ItemEntry(it.info.shortname, it.amount, it.skin, 0, 0, pos, "wear"));
            }

            buf.Main = main;
            buf.Belt = belt;
            buf.Wear = wear;
        }

        private void AddItemRecursive(List<ItemEntry> list, Item item)
        {
            if (item == null || item.info == null) return;
            var entry = new ItemEntry(item.info.shortname, item.amount, item.skin, 0, 0);
            var held = item.GetHeldEntity() as BaseProjectile;
            if (held != null && held.primaryMagazine != null)
            {
                entry.Magazine = held.primaryMagazine.contents;
            }
            list.Add(entry);

            if (item.contents != null)
            {
                foreach (var sub in item.contents.itemList)
                {
                    if (sub?.info == null) continue;
                    list.Add(new ItemEntry(sub.info.shortname, sub.amount, sub.skin));
                }
            }
        }

        private static double GetNowUnix()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        private void LoadCooldowns()
        {
            _cooldowns.Clear();
            try
            {
                var data = _cfgFile.ReadObject<CooldownsData>();
                if (data?.Data == null) return;

                foreach (var playerKv in data.Data)
                {
                    if (!ulong.TryParse(playerKv.Key, out var userId) || playerKv.Value == null) continue;
                    var slotMap = new Dictionary<int, double>();
                    foreach (var slotKv in playerKv.Value)
                    {
                        if (!int.TryParse(slotKv.Key, out var slot)) continue;
                        slotMap[slot] = slotKv.Value;
                    }
                    if (slotMap.Count > 0) _cooldowns[userId] = slotMap;
                }
            }
            catch
            {
                _cfgFile.WriteObject(new CooldownsData(), true);
            }
        }

        private void SaveCooldowns()
        {
            var data = new CooldownsData();
            foreach (var playerKv in _cooldowns)
            {
                var slotMap = new Dictionary<string, double>();
                foreach (var slotKv in playerKv.Value)
                    slotMap[slotKv.Key.ToString()] = slotKv.Value;
                data.Data[playerKv.Key.ToString()] = slotMap;
            }
            _cfgFile.WriteObject(data, true);
        }

        private double GetCooldown(ulong userId, int slot)
        {
            if (_cooldowns.TryGetValue(userId, out var map) && map.TryGetValue(slot, out var t))
                return t;
            return 0;
        }

        private void SetCooldown(ulong userId, int slot, int seconds)
        {
            if (!_cooldowns.TryGetValue(userId, out var map))
            {
                map = new Dictionary<int, double>();
                _cooldowns[userId] = map;
            }
            map[slot] = GetNowUnix() + seconds;
            SaveCooldowns();
        }

        private KitDef GetEdit(int slot)
        {
            if (_editBuffer[slot] == null) _editBuffer[slot] = Clone(_config.Kits[slot]);
            return _editBuffer[slot];
        }

        private KitDef Clone(KitDef src)
        {
            return new KitDef
            {
                CardArtKey = src.CardArtKey,
                CloseRect = (src.CloseRect != null && src.CloseRect.Length == 4) ? new float[] { src.CloseRect[0], src.CloseRect[1], src.CloseRect[2], src.CloseRect[3] } : new float[] { 0.96f, 0.94f, 0.995f, 0.99f },
                EditRect = (src.EditRect != null && src.EditRect.Length == 4) ? new float[] { src.EditRect[0], src.EditRect[1], src.EditRect[2], src.EditRect[3] } : new float[] { 0.02f, 0.01f, 0.14f, 0.06f },
                Name = src.Name,
                Permission = src.Permission,
                CooldownSeconds = src.CooldownSeconds,
                CardArtUrl = src.CardArtUrl,
                ActionRect = (src.ActionRect != null && src.ActionRect.Length == 4)
                    ? new float[] { src.ActionRect[0], src.ActionRect[1], src.ActionRect[2], src.ActionRect[3] }
                    : new float[] { 0.06f, 0.05f, 0.94f, 0.17f },
                BtnPng_Take = src.BtnPng_Take,
                BtnPng_Cooldown = src.BtnPng_Cooldown,
                BtnPng_Locked = src.BtnPng_Locked,
                Main = src.Main.Select(i => { var _e = new ItemEntry(i.Shortname, i.Amount, i.Skin, i.Ammo, i.Magazine, i.Slot, i.Container); if (i.Mods != null) _e.Mods = new List<string>(i.Mods); return _e; }).ToList(),
                Belt = src.Belt.Select(i => { var _e = new ItemEntry(i.Shortname, i.Amount, i.Skin, i.Ammo, i.Magazine, i.Slot, i.Container); if (i.Mods != null) _e.Mods = new List<string>(i.Mods); return _e; }).ToList(),
                Wear = src.Wear.Select(i => { var _e = new ItemEntry(i.Shortname, i.Amount, i.Skin, i.Ammo, i.Magazine, i.Slot, i.Container); if (i.Mods != null) _e.Mods = new List<string>(i.Mods); return _e; }).ToList()
            };
        }

        private string BuildItemsSummary(KitDef kit)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            System.Action<System.Collections.Generic.List<ItemEntry>, string> append = (lst, title) =>
            {
                if (lst == null || lst.Count == 0) return;
                sb.AppendLine(title + ":");
                foreach (var e in lst)
                {
                    if (string.IsNullOrEmpty(e.Shortname)) continue;
                    sb.Append(" • ").Append(e.Shortname);
                    if (e.Amount > 1) sb.Append(" x").Append(e.Amount);
                    sb.AppendLine();
                }
                sb.AppendLine();
            };
            append(kit.Main, "MAIN");
            append(kit.Belt, "BELT");
            append(kit.Wear, "WEAR");
            return sb.ToString();
        }

        private bool IsAdmin(BasePlayer player) =>
            player != null && permission.UserHasPermission(player.UserIDString, PermAdmin);

        // Try to get an icon URL for an item by shortname (fallback: RustLabs CDN)
        private string GetItemIconUrl(string shortname, ulong skin = 0)
        {
            if (string.IsNullOrEmpty(shortname)) return null;
            return $"https://rustlabs.com/img/items180/{shortname}.png";
        }


        #endregion

        
        [ConsoleCommand("Console_KS_MenuSelSlot")]
        private void Console_KS_MenuSelSlot(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !IsAdmin(player)) return;
            int sel = 0; if (arg.Args != null && arg.Args.Length >= 1) int.TryParse(arg.Args[0], out sel);
            sel = Mathf.Clamp(sel, 0, 6);
            _menuSelSlot[player.userID] = sel;
            DrawMenuEditor(player);
        }

        [ConsoleCommand("Console_KS_MenuCellMove")]
        private void Console_KS_MenuCellMove(ConsoleSystem.Arg arg)
        {
            var player = arg.Player(); if (player == null || !IsAdmin(player)) return;
            if (arg.Args == null || arg.Args.Length < 2) return;
            string dir = arg.Args[0];
            float step; if (!float.TryParse(arg.Args[1], out step)) return;
            int sel = 0; if (_menuSelSlot.TryGetValue(player.userID, out var tmp)) sel = tmp;
            float[] r;
            if (sel == 6)
            {
                r = _config.MenuCloseRect ?? (_config.MenuCloseRect = new float[] { 0.96f, 0.94f, 0.995f, 0.99f });
            }
            else
            {
                if (_config.MenuCells == null || _config.MenuCells.Length != 6) return;
                r = _config.MenuCells[sel] ?? (_config.MenuCells[sel] = new float[]{0.06f,0.05f,0.30f,0.20f});
            }
            if (dir == "+x") { r[0]+=step; r[2]+=step; }
            else if (dir == "-x") { r[0]-=step; r[2]-=step; }
            else if (dir == "+y") { r[1]+=step; r[3]+=step; }
            else if (dir == "-y") { r[1]-=step; r[3]-=step; }
            ClampRectF01(r); SaveConfig(); DrawMenuEditor(player);
        }

        [ConsoleCommand("Console_KS_MenuCellResize")]
        private void Console_KS_MenuCellResize(ConsoleSystem.Arg arg)
        {
            var player = arg.Player(); if (player == null || !IsAdmin(player)) return;
            if (arg.Args == null || arg.Args.Length < 2) return;
            string dir = arg.Args[0];
            float step; if (!float.TryParse(arg.Args[1], out step)) return;
            int sel = 0; if (_menuSelSlot.TryGetValue(player.userID, out var tmp)) sel = tmp;
            float[] r;
            if (sel == 6)
            {
                r = _config.MenuCloseRect ?? (_config.MenuCloseRect = new float[] { 0.96f, 0.94f, 0.995f, 0.99f });
            }
            else
            {
                if (_config.MenuCells == null || _config.MenuCells.Length != 6) return;
                r = _config.MenuCells[sel] ?? (_config.MenuCells[sel] = new float[]{0.06f,0.05f,0.30f,0.20f});
            }
            if (dir == "+w") { r[2]+=step; } else if (dir == "-w") { r[2]-=step; }
            else if (dir == "+h") { r[3]+=step; } else if (dir == "-h") { r[3]-=step; }
            ClampRectF01(r); SaveConfig(); DrawMenuEditor(player);
        }

                [ConsoleCommand("Console_KS_MenuCellReset")]
        private void Console_KS_MenuCellReset(ConsoleSystem.Arg arg)
        {
            var player = arg.Player(); if (player == null || !IsAdmin(player)) return;

            if (_config.MainMenuRect == null || _config.MainMenuRect.Length != 4)
                _config.MainMenuRect = new float[] { 0.12f, 0.18f, 0.88f, 0.88f };
            if (_config.MenuCells == null || _config.MenuCells.Length != 6)
                _config.MenuCells = new float[6][];

            int sel = 0; if (_menuSelSlot.TryGetValue(player.userID, out var tmp)) sel = tmp;
            if (sel == 6)
            {
                _config.MenuCloseRect = new float[] { 0.96f, 0.94f, 0.995f, 0.99f };
                SaveConfig();
                DrawMenuEditor(player);
                return;
            }
            int rows = 2, cols = 3, rr = sel/cols, cc = sel%cols;
            float gap = 0.02f;

            float rw = Mathf.Clamp01(_config.MainMenuRect[2]-_config.MainMenuRect[0]);
            float rh = Mathf.Clamp01(_config.MainMenuRect[3]-_config.MainMenuRect[1]);
            float innerW = rw - gap*(cols+1);
            float innerH = rh - gap*(rows+1);
            float cellW = innerW/cols, cellH = innerH/rows;

            float xMinAbs = gap + cc*(cellW + gap);
            float xMaxAbs = xMinAbs + cellW;
            float yTopInnerAbs = rh - gap;
            float yMinAbs = yTopInnerAbs - (rr+1)*cellH - rr*gap;
            float yMaxAbs = yMinAbs + cellH;

            _config.MenuCells[sel] = new float[]{ xMinAbs/rw, yMinAbs/rh, xMaxAbs/rw, yMaxAbs/rh };
            SaveConfig();
            DrawMenuEditor(player);
        }

        // === Image helpers (IL or URL) ===
        private bool HasIL(string key)
        {
            try { return (bool?)(ImageLibrary?.Call("HasImage", key, 0UL)) ?? false; } catch { return false; }
        }
        private string GetILPng(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            try
            {
                var data = ImageLibrary?.Call("GetImage", key, 0UL) as string;
                if (!string.IsNullOrEmpty(data)) return data;
            } catch {}
            try
            {
                var data = ImageLibrary?.Call("GetImageData", key) as string;
                if (!string.IsNullOrEmpty(data)) return data;
            } catch {}
            return null;
        }
        private void AddImageFromKeyOrUrl(CuiElementContainer ui, string parent, string amin, string amax, string keyOrUrl)
        {
            if (string.IsNullOrEmpty(keyOrUrl)) return;
            if (keyOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                ui.Add(new CuiElement
                {
                    Parent = parent,
                    Components =
                    {
                        new CuiRawImageComponent{ Url = keyOrUrl, Color = "1 1 1 1" },
                        new CuiRectTransformComponent{ AnchorMin = amin, AnchorMax = amax }
                    }
                });
                return;
            }
            var png = GetILPng(keyOrUrl);
            if (!string.IsNullOrEmpty(png))
            {
                ui.Add(new CuiElement
                {
                    Parent = parent,
                    Components =
                    {
                        new CuiRawImageComponent{ Png = png, Color = "1 1 1 1" },
                        new CuiRectTransformComponent{ AnchorMin = amin, AnchorMax = amax }
                    }
                });
            }
        }
        private string GetButtonKeyForState(KitDef kit, bool canTake)
        {
            if (canTake)
                return string.IsNullOrEmpty(kit.BtnPng_Take) ? "kit_btn_take" : kit.BtnPng_Take;
            // cannot take: cooldown or locked
            // Try cooldown first (if in cooldown), else locked
            double now = GetNowUnix();
            double next = GetCooldown(_lastPlayerIdForState, _lastSlotForState); // uses fields set before render
            if (next > now)
                return string.IsNullOrEmpty(kit.BtnPng_Cooldown) ? "kit_btn_cd" : kit.BtnPng_Cooldown;
            return string.IsNullOrEmpty(kit.BtnPng_Locked) ? "kit_btn_locked" : kit.BtnPng_Locked;
        }
        private ulong _lastPlayerIdForState; private int _lastSlotForState;
#region UI Helpers

        private static void AddPanel(CuiElementContainer ui, string parent, string anchorMin, string anchorMax, string color, string name = null)
        {
            ui.Add(new CuiElement
            {
                Parent = parent,
                Name = name,
                Components =
                {
                    new CuiImageComponent{ Color = color },
                    new CuiRectTransformComponent{ AnchorMin = anchorMin, AnchorMax = anchorMax }
                }
            });
        }

        
private static void AddBorder(CuiElementContainer ui, string parent, float thickness, string color)
{
    // Draw border on the full parent rect
    AddPanel(ui, parent, $"0 {1 - thickness}", "1 1", color);          // Top
    AddPanel(ui, parent, "0 0", $"1 {thickness}", color);               // Bottom
    AddPanel(ui, parent, "0 0", $"{thickness} 1", color);               // Left
    AddPanel(ui, parent, $"{1 - thickness} 0", "1 1", color);           // Right
}

        


private static void AddBorder(CuiElementContainer ui, string parent, float[] rect, float thickness, string color)
{
    if (rect == null || rect.Length != 4) return;
    // Top
    AddPanel(ui, parent, $"{rect[0]} {rect[3]-thickness}", $"{rect[2]} {rect[3]}", color);
    // Bottom
    AddPanel(ui, parent, $"{rect[0]} {rect[1]}", $"{rect[2]} {rect[1]+thickness}", color);
    // Left
    AddPanel(ui, parent, $"{rect[0]} {rect[1]}", $"{rect[0]+thickness} {rect[3]}", color);
    // Right
    AddPanel(ui, parent, $"{rect[2]-thickness} {rect[1]}", $"{rect[2]} {rect[3]}", color);
}


        private static void AddButton(CuiElementContainer ui, string parent, string anchorMin, string anchorMax,
            string color, string text, string command)
        {
            var btn = new CuiButton
            {
                RectTransform = { AnchorMin = anchorMin, AnchorMax = anchorMax },
                Button = { Color = color, Command = command, Close = "" },
                Text = { Text = text, FontSize = 14, Align = TextAnchor.MiddleCenter }
            };
            ui.Add(btn, parent);
        }

        private static void AddLabel(CuiElementContainer ui, string parent, string text,
            string anchorMin, string anchorMax, int fontSize = 14, string color = "1 1 1 1",
            TextAnchor align = TextAnchor.MiddleCenter)
        {
            ui.Add(new CuiElement
            {
                Parent = parent,
                Components =
                {
                    new CuiTextComponent{ Text = text, Color = color, FontSize = fontSize, Align = align },
                    new CuiRectTransformComponent{ AnchorMin = anchorMin, AnchorMax = anchorMax }
                }
            });
        }

        private static void AddTextInput(CuiElementContainer ui, string parent, string name, string initial,
            string anchorMin, string anchorMax, string command)
        {
            string bg = $"{name}_BG";
            AddPanel(ui, parent, anchorMin, anchorMax, "0.18 0.18 0.18 0.95", bg);
            ui.Add(new CuiElement
            {
                Parent = bg,
                Components =
                {
                    new CuiInputFieldComponent{ Color = "1 1 1 1", FontSize = 16, Text = initial, Command = command },
                    new CuiRectTransformComponent{ AnchorMin = "0.02 0.1", AnchorMax = "0.98 0.9" }
                }
            });
        }

        private void DrawGridWithItems(CuiElementContainer ui, string parent, int cols, int rows, List<ItemEntry> items)
        {
            float padX = 0.002f;
            float padY = 0.002f;

            float totalPadX = padX * (cols + 1);
            float totalPadY = padY * (rows + 1);
            if (totalPadX > 0.25f) totalPadX = 0.25f;
            if (totalPadY > 0.25f) totalPadY = 0.25f;

            float cellW = (1f - totalPadX) / cols;
            float cellH = (1f - totalPadY) / rows;

            var map = new Dictionary<int, ItemEntry>();
            if (items != null)
            {
                foreach (var it in items)
                {
                    int slot = Mathf.Max(0, it.Slot);
                    if (!map.ContainsKey(slot)) map[slot] = it;
                }
            }

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int slotIndex = r * cols + c;
                    float xMin = padX + c * (cellW + padX);
                    float yMin = padY + (rows - 1 - r) * (cellH + padY);
                    float xMax = xMin + cellW;
                    float yMax = yMin + cellH;

                    string cellName = $"{parent}_CELL_{slotIndex}";
                    AddPanel(ui, parent, $"{xMin} {yMin}", $"{xMax} {yMax}", "0.08 0.08 0.08 0.85", cellName);
                    if (this._debug) AddBorder(ui, cellName, 0.001f, "0.25 0.25 0.25 0.9");

                    if (map.ContainsKey(slotIndex))
                    {
                        var e = map[slotIndex];
                        string url = GetItemIconUrl(e.Shortname, e.Skin);
                        if (!string.IsNullOrEmpty(url))
                        {
                            ui.Add(new CuiElement
                            {
                                Parent = cellName,
                                Components =
                                {
                                    new CuiRawImageComponent{ Url = url, Color = "1 1 1 1" },
                                    new CuiRectTransformComponent{ AnchorMin = "0.02 0.04", AnchorMax = "0.98 0.98" }
                                }
                            });
                            if (e.Amount > 1)
                            {
                                AddLabel(ui, cellName, $"x{e.Amount}", "0.62 0.00", "0.98 0.22", 14, "1 1 1 1", TextAnchor.LowerRight);
                            }
                        }
                        else
                        {
                            string label = string.IsNullOrEmpty(e.Shortname) ? "?" : e.Shortname;
                            if (e.Amount > 1) label += $" x{e.Amount}";
                            AddLabel(ui, cellName, label, "0.05 0.05", "0.95 0.95", 12, "1 1 1 1", TextAnchor.MiddleCenter);
                        }
                    }
                }
            }
        }


        // ===== Lucifer claim UI / audio (slot index 4) =====
        private const string UI_LUCIFER_CLAIM = "KITSUITE_LUCIFER_CLAIM";
        private const int LUCIFER_SLOT_INDEX = 4; // 0-based (user said lucifer(5))

        private void ShowLuciferClaim(BasePlayer player, float durationSec = 15f)
        {
            if (player == null) return;
            // Close all our plugin windows first (as requested)
            SafeDestroyAllUI(player);

            var ui = new CuiElementContainer();
            // Transparent root
            ui.Add(new CuiElement
            {
                Name = UI_LUCIFER_CLAIM,
                Parent = "Overlay",
                Components =
                {
                    new CuiRawImageComponent{ Color = "0 0 0 0" },
                    new CuiRectTransformComponent{ AnchorMin = "0 0", AnchorMax = "1 1" }
                }
            });

            // Center-top region "under compass": approximate normalized anchors
            // Width ~20% of screen, height auto by image. We'll place a container and fill image inside.
            string anchorMin = "0.40 0.86";
            string anchorMax = "0.60 0.98";

            // Background container (transparent) to host the PNG
            string host = UI_LUCIFER_CLAIM + "_HOST";
            ui.Add(new CuiElement
            {
                Name = host,
                Parent = UI_LUCIFER_CLAIM,
                Components =
                {
                    new CuiRawImageComponent{ Color = "0 0 0 0" },
                    new CuiRectTransformComponent{ AnchorMin = anchorMin, AnchorMax = anchorMax }
                }
            });

            // Image from ImageLibrary key "luciferclaim"
            try
            {
                var png = GetILPng("luciferclaim");
                if (!string.IsNullOrEmpty(png))
                {
                    ui.Add(new CuiElement
                    {
                        Parent = host,
                        Components =
                        {
                            new CuiRawImageComponent{ Png = png, Color = "1 1 1 1" },
                            new CuiRectTransformComponent{ AnchorMin = "0 0", AnchorMax = "1 1" }
                        }
                    });
                }
                else
                {
                    // Fallback: show a simple panel if image not ready
                    AddPanel(ui, host, "0 0", "1 1", "0.2 0 0 0.8");
                    AddLabel(ui, host, "LUCIFER CLAIM", "0.05 0.05", "0.95 0.95", 18, "1 1 1 1", TextAnchor.MiddleCenter);
                }
                if (_config.MenuBackgroundKey == null) _config.MenuBackgroundKey = string.Empty;
                if (_config.MenuCloseRect == null || _config.MenuCloseRect.Length != 4) _config.MenuCloseRect = new float[] { 0.96f, 0.94f, 0.995f, 0.99f };
                if (_config.MenuBackgroundUrl == null) _config.MenuBackgroundUrl = string.Empty;
            }
            catch {}

            CuiHelper.AddUi(player, ui);

            // Auto-hide after duration
            timer.Once(durationSec, () => HideLuciferClaim(player));
        }

        private void HideLuciferClaim(BasePlayer player)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, UI_LUCIFER_CLAIM);
        }

        
        // === Lucifer SFX via SoundLibraryApi ===
        private void PlayLuciferVoice(BasePlayer player, string fileName)
        {
            if (player == null || string.IsNullOrEmpty(fileName)) return;
            // Use SoundLibraryApi console endpoint to play to a single player
            // Requires SoundLibraryApi to be loaded and the file to exist in oxide/data/SoundLibraryApi as <fileName>.data
            try
            {
                // Run as server console to avoid chat permission checks
                ConsoleSystem.Run(ConsoleSystem.Option.Server, $"audio.message send {player.UserIDString} {fileName}");
            }
            catch (System.Exception e)
            {
                PrintWarning($"[LuciferVoice] Failed to run audio.message: {e.Message}");
            }
        }
private void PlayFallbackJingle(BasePlayer player)
        {
            if (player == null) return;
            // We can't play external audio from server; simulate a short jingle using built-in fx chains.
            // We'll space a few effects over ~6 seconds to at least give usable feedback.
            Effect.server.Run("assets/bundled/prefabs/fx/notice/item.select.fx.prefab", player.transform.position);
            timer.Once(0.6f, () => Effect.server.Run("assets/bundled/prefabs/fx/notice/loot.drag.prefab", player.transform.position));
            timer.Once(1.2f, () => Effect.server.Run("assets/bundled/prefabs/fx/invite_notice.prefab", player.transform.position));
            timer.Once(2.0f, () => Effect.server.Run("assets/bundled/prefabs/fx/notice/loot.taken.prefab", player.transform.position));
            timer.Once(3.5f, () => Effect.server.Run("assets/bundled/prefabs/fx/notice/loot.taken.prefab", player.transform.position));
            timer.Once(5.0f, () => Effect.server.Run("assets/bundled/prefabs/fx/invite_notice.prefab", player.transform.position));
        }
        

        #region Public Hook for NPC/Player

        // Called by other plugins: Interface.CallHook("GiveKit", entity, "kitname");
        // ============================================
// ✅ УЛУЧШЕННАЯ ВЕРСИЯ ПУБЛИЧНОГО ХУКА GiveKit
// Добавь это В НАЧАЛО метода GiveKit()
// ============================================

// ============================================
// ✅ ИСПРАВЛЕННАЯ ВЕРСИЯ GiveKit
// NPC получают киты БЕЗ проверки пермишнов!
// ============================================

private object GiveKit(BaseEntity entity, string kitName)
{
    // Puts($"[GiveKit] Вызван! entity={entity?.ShortPrefabName ?? "null"}, kitName={kitName}");
    
    if (entity == null || string.IsNullOrEmpty(kitName))
    {
        // Puts($"[GiveKit] ❌ Отклонён: entity={entity != null}, kitName='{kitName}'");
        return null;
    }

    var kit = _config?.Kits?.FirstOrDefault(k => k != null && string.Equals(k.Name, kitName.Trim(), StringComparison.OrdinalIgnoreCase));
    if (kit == null)
    {
        // Puts($"[GiveKit] ❌ Кит '{kitName}' не найден!");
        return null;
    }
    
    // Puts($"[GiveKit] ✅ Кит '{kitName}' найден, выдаём...");

    // ============================================
    // ✅ NPC - БЕЗ ПРОВЕРКИ ПРАВ!
    // ============================================
    var npc = entity as ScientistNPC;
    if (npc != null)
    {
        // Puts($"[GiveKit] 📦 Выдаём кит NPC (без проверки пермишнов)...");
        
        timer.Once(0.2f, () =>
        {
            if (npc == null || npc.IsDestroyed)
            {
                // Puts($"[GiveKit] ⚠️ NPC уничтожен до выдачи кита!");
                return;
            }
            
            // Puts($"[GiveKit] 🔧 Main: {kit.Main?.Count ?? 0} предметов");
            TryGiveListNPC(npc, kit.Main);
            
            // Puts($"[GiveKit] 🔧 Belt: {kit.Belt?.Count ?? 0} предметов");
            TryGiveListNPC(npc, kit.Belt);
            
            // Puts($"[GiveKit] 🔧 Wear: {kit.Wear?.Count ?? 0} предметов");
            TryGiveListNPC(npc, kit.Wear);
            
            int total = npc.inventory.containerMain.itemList.Count 
                      + npc.inventory.containerBelt.itemList.Count 
                      + npc.inventory.containerWear.itemList.Count;
            
            // Puts($"[GiveKit] ✅ Итого у NPC: {total} предметов");
        });
        
        // Puts($"[GiveKit] ✅ Применён кит '{kit.Name}' к NPC.");
        return true;
    }

    // ============================================
    // ✅ ИГРОК - С ПРОВЕРКОЙ ПРАВ!
    // ============================================
    var player = entity as BasePlayer;
    if (player != null)
    {
        // ✅ Проверяем пермишн ТОЛЬКО для игроков!
        if (!string.IsNullOrEmpty(kit.Permission) && !permission.UserHasPermission(player.UserIDString, kit.Permission) && !IsAdmin(player))
        {
            // Puts($"[GiveKit] ❌ У игрока нет прав: {kit.Permission}");
            return null;
        }

        if (GiveKit(player, kit))
        {
            // Puts($"[GiveKit] ✅ Применён кит '{kit.Name}' к игроку '{player.displayName}'.");
            return true;
        }
        
        // Puts($"[GiveKit] ❌ Не удалось выдать кит игроку (нет места?)");
        return null;
    }

    // Puts($"[GiveKit] ❌ Неизвестный тип entity: {entity.GetType().Name}");
    return null;
}

        private void TryGiveListNPC(ScientistNPC npc, List<ItemEntry> list)
{
    if (npc == null || list == null) return;
    
    // Puts($"[KS] TryGiveListNPC: {list.Count} items to give");
    
    foreach (var e in list)
    {
        if (string.IsNullOrEmpty(e.Shortname) || e.Amount <= 0) continue;
        var def = ItemManager.FindItemDefinition(e.Shortname);
        if (def == null)
        {
            PrintWarning($"[KS] Item not found: {e.Shortname}");
            continue;
        }

        var item = ItemManager.Create(def, e.Amount, e.Skin);
        if (item == null)
        {
            PrintWarning($"[KS] Failed to create: {e.Shortname}");
            continue;
        }

        // Моды
        try
        {
            var modsListNpc = (e.Mods != null && e.Mods.Count > 0) 
                ? e.Mods 
                : (_useDefaultWeaponMods ? DefaultModsFor(e.Shortname) : null);
            
            if (modsListNpc != null && item.contents != null)
            {
                foreach (var modSn in modsListNpc)
                {
                    if (string.IsNullOrEmpty(modSn)) continue;
                    var mdef = ItemManager.FindItemDefinition(modSn);
                    if (mdef == null) continue;
                    var modItem = ItemManager.Create(mdef, 1, 0);
                    if (modItem == null) continue;
                    modItem.MoveToContainer(item.contents);
                }
            }
        }
        catch (Exception ex)
        {
            PrintWarning($"[KS] Mod attach error: {ex.Message}");
        }
        
        // ✅ ЗАПОЛНЯЕМ МАГАЗИН
        var held = item.GetHeldEntity() as BaseProjectile;
        if (held != null && held.primaryMagazine != null)
        {
            int magCount = (e.Magazine > 0) ? e.Magazine : held.primaryMagazine.capacity;
            held.primaryMagazine.contents = Mathf.Clamp(magCount, 0, held.primaryMagazine.capacity);
            // Puts($"[KS] Filled magazine: {e.Shortname} -> {held.primaryMagazine.contents}/{held.primaryMagazine.capacity}");
        }
        
        // Определяем контейнер
        ItemContainer cont = null;
        if (e.Container == "wear")
            cont = npc.inventory?.containerWear;
        else if (e.Container == "belt")
            cont = npc.inventory?.containerBelt;
        else
            cont = npc.inventory?.containerMain;
        
        if (cont == null)
        {
            PrintWarning($"[KS] Container is null for {e.Shortname}! Using main as fallback.");
            cont = npc.inventory?.containerMain;
        }
        
        // Пытаемся поместить
        bool placed = false;
        
        if (e.Slot >= 0 && e.Slot < cont.capacity)
            placed = item.MoveToContainer(cont, e.Slot, true);
        
        if (!placed)
            placed = item.MoveToContainer(cont, -1, true);
        
        if (!placed && npc.inventory != null)
            placed = npc.inventory.GiveItem(item, cont);
        
        if (placed)
        {
            // Puts($"[KS] ✅ Placed: {e.Shortname} in {e.Container ?? "main"}");
        }
        else
        {
            PrintWarning($"[KS] ❌ Failed to place: {e.Shortname}");
            item.Remove();
        }
    }
	
// [HELLBLOOD] Force-equip оружие NPC после выдачи кита
timer.Once(0.2f, () =>
{
    if (npc == null || npc.IsDestroyed || npc.inventory == null) return;

    Item gun = null;
    var belt = npc.inventory.containerBelt;
    if (belt != null)
    {
        foreach (var it in belt.itemList)
        {
            var proj = it?.GetHeldEntity() as BaseProjectile;
            if (proj != null)
            {
                gun = it;
                break;
            }
        }
    }

    if (gun == null) return;

    var held = gun.GetHeldEntity() as HeldEntity;
    if (held != null)
    {
        npc.UpdateActiveItem(gun.uid);
        held.SetHeld(true);
        held.SendNetworkUpdateImmediate();
    }
    npc.inventory.ServerUpdate(0f);
});

}



        #endregion
#endregion
        private void EnsureBackfill()
        {
            bool changed = false;
            if (_config == null) return;
            if (_config.Kits == null || _config.Kits.Length != 20)
            {
                var old = _config.Kits ?? new KitDef[0];
                var arr = new KitDef[20];
                int copy = Math.Min(old.Length, arr.Length);
                for (int i = 0; i < copy; i++)
                    arr[i] = (i < old.Length && old[i] != null) ? old[i] : new KitDef();
                for (int i = copy; i < arr.Length; i++)
                    arr[i] = new KitDef();
                _config.Kits = arr;
                changed = true;
            
            // Backfill MenuCells and MainMenuRect
            if (_config.MenuCells == null || _config.MenuCells.Length != 6)
            {
                _config.MenuCells = new float[6][];
                changed = true;
            }
            if (_config.MainMenuRect == null || _config.MainMenuRect.Length != 4)
            {
                _config.MainMenuRect = new float[] { 0.12f, 0.18f, 0.88f, 0.88f };
                changed = true;
            }
            if (_config.MenuCloseRect == null || _config.MenuCloseRect.Length != 4)
            {
                _config.MenuCloseRect = new float[] { 0.96f, 0.94f, 0.995f, 0.99f };
                changed = true;
            }
            // Initialize missing MenuCells to a uniform 2x3 grid inside MainMenuRect
            {
                int rows = 2, cols = 3; float gap = 0.02f;
                float rw = Mathf.Clamp01(_config.MainMenuRect[2]-_config.MainMenuRect[0]);
                float rh = Mathf.Clamp01(_config.MainMenuRect[3]-_config.MainMenuRect[1]);
                float innerW = rw - gap*(cols+1);
                float innerH = rh - gap*(rows+1);
                float cellW = innerW/cols, cellH = innerH/rows;
                for (int rr=0; rr<rows; rr++)
                for (int cc=0; cc<cols; cc++)
                {
                    int idx = rr*cols+cc;
                    if (_config.MenuCells[idx] == null || _config.MenuCells[idx].Length != 4)
                    {
                        float xMinAbs = gap + cc*(cellW + gap);
                        float xMaxAbs = xMinAbs + cellW;
                        float yTopInnerAbs = rh - gap;
                        float yMinAbs = yTopInnerAbs - (rr+1)*cellH - rr*gap;
                        float yMaxAbs = yMinAbs + cellH;
                        _config.MenuCells[idx] = new float[]{ xMinAbs/rw, yMinAbs/rh, xMaxAbs/rw, yMaxAbs/rh };
                        changed = true;
                    }
                }
            }
        }
            for (int i = 0; i < _config.Kits.Length; i++)
            {
                var k = _config.Kits[i] ?? (_config.Kits[i] = new KitDef());
                if (k.ActionRect == null || k.ActionRect.Length != 4) { k.ActionRect = new float[] { 0.06f, 0.05f, 0.94f, 0.17f }; changed = true; }
                if (k.CloseRect  == null || k.CloseRect.Length  != 4) { k.CloseRect  = new float[] { 0.96f, 0.94f, 0.995f, 0.99f }; changed = true; }
                if (k.EditRect   == null || k.EditRect.Length   != 4) { k.EditRect   = new float[] { 0.02f, 0.01f, 0.14f, 0.06f  }; changed = true; }
            }
            if (changed) SaveConfig();
        }

        private readonly HashSet<string> _cardEditorOpen = new HashSet<string>();
        private readonly Dictionary<ulong, int> _cardTarget = new Dictionary<ulong, int>(); // 0=Take,1=Close,2=Edit
        private string Key(ulong id, int slot) => id.ToString() + ":" + slot.ToString();
        private bool IsCardEditorOpen(BasePlayer p, int slot) => _cardEditorOpen.Contains(Key(p.userID, slot));

        [ConsoleCommand("Console_KS_ToggleCardEditor")]
        private void Console_KS_ToggleCardEditor(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null || !IsAdmin(player)) return;
            if (arg.Args == null || arg.Args.Length < 2) return;
            if (!int.TryParse(arg.Args[0], out var slot)) return;
            var key = Key(player.userID, slot);
            if (_cardEditorOpen.Contains(key)) _cardEditorOpen.Remove(key); else _cardEditorOpen.Add(key);
            OpenCard(player, slot);
        }


        [ConsoleCommand("Console_KS_SelTarget")]
        private void Console_KS_SelTarget(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null || !IsAdmin(player)) return;
            if (arg.Args == null || arg.Args.Length < 2) return;
            if (!int.TryParse(arg.Args[0], out var slot)) return;
            if (!int.TryParse(arg.Args[1], out var target)) return;
            _cardTarget[player.userID] = target;
            OpenCard(player, slot);
        }

        [ConsoleCommand("Console_KS_MoveRect")]
        private void Console_KS_MoveRect(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null || !IsAdmin(player)) return;
            if (arg.Args == null || arg.Args.Length < 3) return;
            if (!int.TryParse(arg.Args[0], out var slot)) return;
            var dir = arg.Args[1];
            if (!float.TryParse(arg.Args[2], out var step)) return;

            var kit = _config.Kits[slot];
            var r = GetTargetRect(kit, _cardTarget.ContainsKey(player.userID) ? _cardTarget[player.userID] : 0);
            if (r == null) return;
            switch (dir) { case "+x": r[0]+=step; r[2]+=step; break; case "-x": r[0]-=step; r[2]-=step; break; case "+y": r[1]+=step; r[3]+=step; break; case "-y": r[1]-=step; r[3]-=step; break; }
            ClampRectF(r);
            SetTargetRect(kit, _cardTarget.ContainsKey(player.userID) ? _cardTarget[player.userID] : 0, r);
            OpenCard(player, slot);
        }

        [ConsoleCommand("Console_KS_ResizeRect")]
        private void Console_KS_ResizeRect(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null || !IsAdmin(player)) return;
            if (arg.Args == null || arg.Args.Length < 3) return;
            if (!int.TryParse(arg.Args[0], out var slot)) return;
            var dir = arg.Args[1];
            if (!float.TryParse(arg.Args[2], out var step)) return;

            var kit = _config.Kits[slot];
            var r = GetTargetRect(kit, _cardTarget.ContainsKey(player.userID) ? _cardTarget[player.userID] : 0);
            if (r == null) return;
            switch (dir) { case "+w": r[2]+=step; break; case "-w": r[2]-=step; break; case "+h": r[3]+=step; break; case "-h": r[3]-=step; break; }
            ClampRectF(r);
            SetTargetRect(kit, _cardTarget.ContainsKey(player.userID) ? _cardTarget[player.userID] : 0, r);
            OpenCard(player, slot);
        }

        [ConsoleCommand("Console_KS_SaveRect")]
        private void Console_KS_SaveRect(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null || !IsAdmin(player)) return;
            SaveConfig();
            if (arg.Args != null && arg.Args.Length > 0 && int.TryParse(arg.Args[0], out var slot))
                OpenCard(player, slot);
        }

        [ConsoleCommand("Console_KS_ResetRect")]
        private void Console_KS_ResetRect(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null || !IsAdmin(player)) return;
            if (arg.Args == null || arg.Args.Length < 2) return;
            if (!int.TryParse(arg.Args[0], out var slot)) return;
            if (!int.TryParse(arg.Args[1], out var target)) return;
            var kit = _config.Kits[slot];
            if (target == 0) kit.ActionRect = new float[] { 0.06f, 0.05f, 0.94f, 0.17f };
            else if (target == 1) kit.CloseRect = new float[] { 0.96f, 0.94f, 0.995f, 0.99f };
            else kit.EditRect = new float[] { 0.02f, 0.01f, 0.14f, 0.06f };
            SaveConfig();
            OpenCard(player, slot);
        }

        [ConsoleCommand("Console_KS_ToggleGlobalDebug")]
        private void Console_KS_ToggleGlobalDebug(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null || !IsAdmin(player)) return;
            _debug = !_debug;
            _config.DebugHighlight = _debug;
            SaveConfig();
            OpenMenu(player);
        }

        [ConsoleCommand("Console_KS_ToggleDebug")]
        private void Console_KS_ToggleDebug(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null || !IsAdmin(player)) return;
            _debug = !_debug;
            player.ChatMessage($"[BLOODHELL] Debug UI: {(_debug ? "ON" : "OFF")}");
            OpenMenu(player);
            if (arg.Args != null && arg.Args.Length > 0 && int.TryParse(arg.Args[0], out var slot))
                OpenCard(player, slot);
        }

        private float[] GetTargetRect(KitDef k, int target)
        {
            if (target == 0) return k.ActionRect;
            if (target == 1) return k.CloseRect;
            return k.EditRect;
        }
        private void SetTargetRect(KitDef k, int target, float[] r)
        {
            if (target == 0) k.ActionRect = r;
            else if (target == 1) k.CloseRect = r;
            else k.EditRect = r;
        }
        private void ClampRectF(float[] r)
        {
            if (r == null || r.Length != 4) return;
            r[0] = Mathf.Clamp01(r[0]); r[1] = Mathf.Clamp01(r[1]); r[2] = Mathf.Clamp01(r[2]); r[3] = Mathf.Clamp01(r[3]);
            if (r[2] < r[0] + 0.02f) r[2] = r[0] + 0.02f;
            if (r[3] < r[1] + 0.02f) r[3] = r[1] + 0.02f;
        }

        

        // Helper to clamp and return same array
        private float[] ClampRect(float[] r)
        {
            if (r == null || r.Length != 4) return new float[] {0f,0f,1f,1f};
            ClampRectF(r);
            return r;
        }
private void DrawOutline(CuiElementContainer cont, string parent, float[] r, string idPrefix, string rgba = "1 0 0 0.6")
        {
            if (r == null || r.Length != 4) return;
            cont.Add(new CuiPanel { Image = { Color = rgba }, RectTransform = { AnchorMin = $"{r[0]} {r[3]-0.002f}", AnchorMax = $"{r[2]} {r[3]}" } }, parent, idPrefix + "_TOP");
            cont.Add(new CuiPanel { Image = { Color = rgba }, RectTransform = { AnchorMin = $"{r[0]} {r[1]}", AnchorMax = $"{r[2]} {r[1]+0.002f}" } }, parent, idPrefix + "_BOTTOM");
            cont.Add(new CuiPanel { Image = { Color = rgba }, RectTransform = { AnchorMin = $"{r[0]} {r[1]}", AnchorMax = $"{r[0]+0.002f} {r[3]}" } }, parent, idPrefix + "_LEFT");
            cont.Add(new CuiPanel { Image = { Color = rgba }, RectTransform = { AnchorMin = $"{r[2]-0.002f} {r[1]}", AnchorMax = $"{r[2]} {r[3]}" } }, parent, idPrefix + "_RIGHT");
        }
    
/*
        [ConsoleCommand("        private void         {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null || !IsAdmin(player)) return;
            // toggle by presence of editor BG
            bool isOpen = CuiHelper.DestroyUi(player, "KITSUITE_MENU_EDITOR_BG");
            CuiHelper.DestroyUi(player, "KITSUITE_MENU_DBG_TOP");
            CuiHelper.DestroyUi(player, "KITSUITE_MENU_DBG_BOTTOM");
            CuiHelper.DestroyUi(player, "KITSUITE_MENU_DBG_LEFT");
            CuiHelper.DestroyUi(player, "KITSUITE_MENU_DBG_RIGHT");
            if (!isOpen) RedrawMenuOutline(player);
        }
*/
    
        [ConsoleCommand("Console_KS_MenuMove")]
        private void Console_KS_MenuMove(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null || !IsAdmin(player)) return;
            if (arg.Args == null || arg.Args.Length < 2) return;
            var dir = arg.Args[0];
            if (!float.TryParse(arg.Args[1], out var step)) return;
            var r = (_config.MainMenuRect != null && _config.MainMenuRect.Length == 4) ? _config.MainMenuRect : (_config.MainMenuRect = new float[] { 0.10f, 0.10f, 0.90f, 0.90f });
            switch (dir) { case "+x": r[0]+=step; r[2]+=step; break; case "-x": r[0]-=step; r[2]-=step; break; case "+y": r[1]+=step; r[3]+=step; break; case "-y": r[1]-=step; r[3]-=step; break; }
            // clamp
            r[0] = Mathf.Clamp01(r[0]); r[1] = Mathf.Clamp01(r[1]); r[2] = Mathf.Clamp01(r[2]); r[3] = Mathf.Clamp01(r[3]);
            if (r[2] < r[0] + 0.05f) r[2] = r[0] + 0.05f;
            if (r[3] < r[1] + 0.05f) r[3] = r[1] + 0.05f;
            RedrawMenuOutline(player);
        }


        [ConsoleCommand("Console_KS_MenuResize")]
        private void Console_KS_MenuResize(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null || !IsAdmin(player)) return;
            if (arg.Args == null || arg.Args.Length < 2) return;
            var dir = arg.Args[0];
            if (!float.TryParse(arg.Args[1], out var step)) return;
            var r = (_config.MainMenuRect != null && _config.MainMenuRect.Length == 4) ? _config.MainMenuRect : (_config.MainMenuRect = new float[] { 0.10f, 0.10f, 0.90f, 0.90f });
            switch (dir) { case "+w": r[2]+=step; break; case "-w": r[2]-=step; break; case "+h": r[3]+=step; break; case "-h": r[3]-=step; break; }
            // clamp
            r[0] = Mathf.Clamp01(r[0]); r[1] = Mathf.Clamp01(r[1]); r[2] = Mathf.Clamp01(r[2]); r[3] = Mathf.Clamp01(r[3]);
            if (r[2] < r[0] + 0.05f) r[2] = r[0] + 0.05f;
            if (r[3] < r[1] + 0.05f) r[3] = r[1] + 0.05f;
            RedrawMenuOutline(player);
        }
        private void Console_KS_MenuMove_Alt(ConsoleSystem.Arg arg)
        {
            var player = arg.Player(); if (player == null || !IsAdmin(player)) return;
            if (arg.Args == null || arg.Args.Length < 2) return;
            string dir = arg.Args[0];
            float step; if (!float.TryParse(arg.Args[1], out step)) return;

            if (_config.MainMenuRect == null || _config.MainMenuRect.Length != 4)
                _config.MainMenuRect = new float[] { 0.12f, 0.18f, 0.88f, 0.88f };

            var r = _config.MainMenuRect;
            if (dir == "+x") { r[0] += step; r[2] += step; }
            else if (dir == "-x") { r[0] -= step; r[2] -= step; }
            else if (dir == "+y") { r[1] += step; r[3] += step; }
            else if (dir == "-y") { r[1] -= step; r[3] -= step; }

            // clamp to 0..1 and ensure min size
            ClampRectF01(r);
            SaveConfig();
            RedrawMenuOutline(player);
        }

        // ===== Menu per-slot editor state =====
        private readonly Dictionary<ulong, int> _menuSelSlot = new Dictionary<ulong, int>();

        

        // Helper: clamp rect in 0..1 local (ensures min size)
        private void ClampRectF01(float[] r)
        {
            if (r == null || r.Length != 4) return;
            r[0] = Mathf.Clamp01(r[0]); r[1] = Mathf.Clamp01(r[1]);
            r[2] = Mathf.Clamp01(r[2]); r[3] = Mathf.Clamp01(r[3]);
            if (r[2] < r[0] + 0.05f) r[2] = r[0] + 0.05f;
            if (r[3] < r[1] + 0.05f) r[3] = r[1] + 0.05f;
        }




        [ConsoleCommand("Console_KS_MenuSave")]
        private void Console_KS_MenuSave(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null || !IsAdmin(player)) return;
            SaveConfig(); RedrawMenuOutline(player);
        }



        [ConsoleCommand("Console_KS_MenuReset")]
        private void Console_KS_MenuReset(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null || !IsAdmin(player)) return;
            _config.MainMenuRect = new float[] { 0.12f, 0.18f, 0.88f, 0.88f };
            SaveConfig(); RedrawMenuOutline(player);
        }
        private void DrawMenuEditor(BasePlayer player)
{
    if (player == null || !IsAdmin(player)) return;

    CuiHelper.DestroyUi(player, "KITSUITE_MENU_EDITOR");
    var ui = new CuiElementContainer();
    ui.Add(new CuiElement
    {
        Name = "KITSUITE_MENU_EDITOR",
        Parent = UI_MENU_ROOT,
        Components =
        {
            new CuiRawImageComponent{ Color = "0 0 0 0" },
            new CuiRectTransformComponent{ AnchorMin = "0 0", AnchorMax = "1 1" }
        }
    });

    AddPanel(ui, "KITSUITE_MENU_EDITOR", "0.02 0.06", "0.42 0.94", "0 0 0 0.95", "KITSUITE_MENU_TOOL");
    AddLabel(ui, "KITSUITE_MENU_TOOL", "Настройка позиций меню", "0.04 0.92", "0.96 0.98", 16, "1 1 1 1", TextAnchor.MiddleCenter);

    // A) Slot select 2x3
    AddLabel(ui, "KITSUITE_MENU_TOOL", "Карточки (выбор слота):", "0.06 0.86", "0.94 0.90", 12, "1 1 1 0.9", TextAnchor.MiddleLeft);
    AddButton(ui, "KITSUITE_MENU_TOOL", "0.06 0.80", "0.30 0.86", "0.25 0.25 0.25 0.95", "Kit1", "Console_KS_MenuSelSlot 0");
    AddButton(ui, "KITSUITE_MENU_TOOL", "0.36 0.80", "0.60 0.86", "0.25 0.25 0.25 0.95", "Kit2", "Console_KS_MenuSelSlot 1");
    AddButton(ui, "KITSUITE_MENU_TOOL", "0.66 0.80", "0.90 0.86", "0.25 0.25 0.25 0.95", "Kit3", "Console_KS_MenuSelSlot 2");
    AddButton(ui, "KITSUITE_MENU_TOOL", "0.06 0.74", "0.30 0.80", "0.25 0.25 0.25 0.95", "Kit4", "Console_KS_MenuSelSlot 3");
    AddButton(ui, "KITSUITE_MENU_TOOL", "0.36 0.74", "0.60 0.80", "0.25 0.25 0.25 0.95", "Kit5", "Console_KS_MenuSelSlot 4");
    AddButton(ui, "KITSUITE_MENU_TOOL", "0.66 0.74", "0.90 0.80", "0.25 0.25 0.25 0.95", "Kit6", "Console_KS_MenuSelSlot 5");
    AddButton(ui, "KITSUITE_MENU_TOOL", "0.66 0.68", "0.90 0.74", "0.25 0.25 0.25 0.95", "Закрыть X", "Console_KS_MenuSelSlot 6");

    // B) Move selected card
    AddLabel(ui, "KITSUITE_MENU_TOOL", "Сдвиг выбранной карточки", "0.06 0.66", "0.94 0.70", 12, "1 1 1 0.85", TextAnchor.MiddleLeft);
    AddButton(ui, "KITSUITE_MENU_TOOL", "0.06 0.60", "0.30 0.66", "0.25 0.25 0.25 0.95", "← Влево", "Console_KS_MenuCellMove -x 0.01");
    AddButton(ui, "KITSUITE_MENU_TOOL", "0.36 0.60", "0.60 0.66", "0.25 0.25 0.25 0.95", "→ Вправо", "Console_KS_MenuCellMove +x 0.01");
    AddButton(ui, "KITSUITE_MENU_TOOL", "0.66 0.60", "0.90 0.66", "0.25 0.25 0.25 0.95", "↑ Вверх", "Console_KS_MenuCellMove +y 0.01");
    AddButton(ui, "KITSUITE_MENU_TOOL", "0.06 0.54", "0.30 0.60", "0.25 0.25 0.25 0.95", "↓ Вниз", "Console_KS_MenuCellMove -y 0.01");
    AddButton(ui, "KITSUITE_MENU_TOOL", "0.36 0.54", "0.90 0.60", "0.40 0.20 0.20 0.95", "Сбросить выделенную карточку", "Console_KS_MenuCellReset");

    // C) Resize selected card
    AddLabel(ui, "KITSUITE_MENU_TOOL", "Размер выбранной карточки", "0.06 0.46", "0.94 0.50", 12, "1 1 1 0.85", TextAnchor.MiddleLeft);
    AddButton(ui, "KITSUITE_MENU_TOOL", "0.06 0.40", "0.30 0.46", "0.25 0.25 0.25 0.95", "Шир+ →", "Console_KS_MenuCellResize +w 0.01");
    AddButton(ui, "KITSUITE_MENU_TOOL", "0.36 0.40", "0.60 0.46", "0.25 0.25 0.25 0.95", "Шир− ←", "Console_KS_MenuCellResize -w 0.01");
    AddButton(ui, "KITSUITE_MENU_TOOL", "0.66 0.40", "0.90 0.46", "0.25 0.25 0.25 0.95", "Выс+ ↑", "Console_KS_MenuCellResize +h 0.01");
    AddButton(ui, "KITSUITE_MENU_TOOL", "0.06 0.34", "0.30 0.40", "0.25 0.25 0.25 0.95", "Выс− ↓", "Console_KS_MenuCellResize -h 0.01");

    // D) Global menu rect (optional)
    AddLabel(ui, "KITSUITE_MENU_TOOL", "Область меню (глобально)", "0.06 0.26", "0.94 0.30", 12, "1 1 1 0.85", TextAnchor.MiddleLeft);
    AddButton(ui, "KITSUITE_MENU_TOOL", "0.06 0.20", "0.30 0.26", "0.25 0.25 0.25 0.95", "Меню: ←", "Console_KS_MenuMove -x 0.01");
    AddButton(ui, "KITSUITE_MENU_TOOL", "0.36 0.20", "0.60 0.26", "0.25 0.25 0.25 0.95", "Меню: →", "Console_KS_MenuMove +x 0.01");
    AddButton(ui, "KITSUITE_MENU_TOOL", "0.66 0.20", "0.90 0.26", "0.25 0.25 0.25 0.95", "Меню: ↑", "Console_KS_MenuMove +y 0.01");
    AddButton(ui, "KITSUITE_MENU_TOOL", "0.06 0.14", "0.30 0.20", "0.25 0.25 0.25 0.95", "Меню: ↓", "Console_KS_MenuMove -y 0.01");
    AddButton(ui, "KITSUITE_MENU_TOOL", "0.36 0.14", "0.60 0.20", "0.25 0.25 0.25 0.95", "Меню: Шир+", "Console_KS_MenuResize +w 0.01");
    AddButton(ui, "KITSUITE_MENU_TOOL", "0.66 0.14", "0.90 0.20", "0.25 0.25 0.25 0.95", "Меню: Шир−", "Console_KS_MenuResize -w 0.01");
    // NEW: vertical grow/shrink upward
    AddButton(ui, "KITSUITE_MENU_TOOL", "0.36 0.08", "0.60 0.14", "0.25 0.25 0.25 0.95", "Меню: Выс+", "Console_KS_MenuResize +h 0.01");
    AddButton(ui, "KITSUITE_MENU_TOOL", "0.66 0.08", "0.90 0.14", "0.25 0.25 0.25 0.95", "Меню: Выс−", "Console_KS_MenuResize -h 0.01");

    // E) Save/Reset/Close
    AddButton(ui, "KITSUITE_MENU_TOOL", "0.06 0.06", "0.46 0.12", "0.20 0.40 0.20 0.95", "Сохранить", "Console_KS_MenuSave");
    AddButton(ui, "KITSUITE_MENU_TOOL", "0.54 0.06", "0.94 0.12", "0.40 0.20 0.20 0.95", "Сброс", "Console_KS_MenuReset");
    AddButton(ui, "KITSUITE_MENU_TOOL", "0.06 0.02", "0.94 0.06", "0.25 0.25 0.25 0.95", "Закрыть", "Console_KS_MenuToggleEditor");

    if (_config.MainMenuRect == null || _config.MainMenuRect.Length != 4)
        _config.MainMenuRect = new float[] { 0.12f, 0.18f, 0.88f, 0.88f };
    var R = _config.MainMenuRect;
    float rx = R[0], ry = R[1], rw = Mathf.Clamp01(R[2]-R[0]), rh = Mathf.Clamp01(R[3]-R[1]);
    int sel = 0; if (_menuSelSlot.TryGetValue(player.userID, out var s)) sel = s;
    if (sel == 6)
    {
        var cr = _config.MenuCloseRect ?? new float[] { 0.96f, 0.94f, 0.995f, 0.99f };
        DrawOutline(ui, "KITSUITE_MENU_BG", cr, "MENU_SEL", "1 0 0 0.9");
    }
    else if (_config.MenuCells != null && sel >= 0 && sel < 6)
    {
        var cr = _config.MenuCells[sel];
        if (cr != null && cr.Length == 4)
        {
            float[] abs = new float[]{ rx + cr[0]*rw, ry + cr[1]*rh, rx + cr[2]*rw, ry + cr[3]*rh };
            
            CuiHelper.DestroyUi(player, "MENU_SEL_TOP");
            CuiHelper.DestroyUi(player, "MENU_SEL_BOTTOM");
            CuiHelper.DestroyUi(player, "MENU_SEL_LEFT");
            CuiHelper.DestroyUi(player, "MENU_SEL_RIGHT");
            DrawOutline(ui, "KITSUITE_MENU_BG", abs, "MENU_SEL", "0 1 0 0.9");

        }
    }

    

    CuiHelper.AddUi(player, ui);
}




        private void RedrawMenuOutline(BasePlayer player)
        {
            if (player == null || !IsAdmin(player)) return;
            if (_config.MainMenuRect == null || _config.MainMenuRect.Length != 4)
                _config.MainMenuRect = new float[] { 0.12f, 0.18f, 0.88f, 0.88f };
            // remove only outline parts
            CuiHelper.DestroyUi(player, "MENU_TOP");
            CuiHelper.DestroyUi(player, "MENU_BOTTOM");
            CuiHelper.DestroyUi(player, "MENU_LEFT");
            CuiHelper.DestroyUi(player, "MENU_RIGHT");
            var ui = new CuiElementContainer();
            DrawOutline(ui, "KITSUITE_MENU_EDITOR", _config.MainMenuRect, "MENU");
            CuiHelper.AddUi(player, ui);
        }
[ConsoleCommand(nameof(Console_KS_MenuToggleEditor))]
        private void Console_KS_MenuToggleEditor(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !IsAdmin(player)) return;
            if (_menuEditorOpen.Contains(player.userID))
            {
                CuiHelper.DestroyUi(player, "KITSUITE_MENU_EDITOR");
                // also remove outline fragments (defensive)
                CuiHelper.DestroyUi(player, "MENU_TOP");
                CuiHelper.DestroyUi(player, "MENU_BOTTOM");
                CuiHelper.DestroyUi(player, "MENU_LEFT");
                CuiHelper.DestroyUi(player, "MENU_RIGHT");
                _menuEditorOpen.Remove(player.userID);
            }
            else
            {
                DrawMenuEditor(player);
                _menuEditorOpen.Add(player.userID);
            }
        }

[ConsoleCommand("ks.menueditor.close")]
private void Console_KS_MenuEditorClose(ConsoleSystem.Arg arg)
{
    var player = arg.Player();
    if (player == null || !IsAdmin(player)) return;
    CuiHelper.DestroyUi(player, "KITSUITE_MENU_EDITOR");
}



        
        [ConsoleCommand("ks.edit")]
        private void Console_KS_Edit(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !IsAdmin(player)) return;
            if (arg.Args == null || arg.Args.Length < 1)
            {
                SendReply(player, "Usage: ks.edit <index|name>");
                return;
            }
            var token = arg.Args[0];
            int slot1based;
            if (int.TryParse(token, out slot1based))
            {
                var idx = slot1based - 1;
                if (idx < 0 || idx >= _config.Kits.Length)
                {
                    SendReply(player, $"Slot out of range 1..{_config.Kits.Length}");
                    return;
                }
                OpenEditor(player, idx);
                return;
            }
            // try by name (case-insensitive)
            int found = -1;
            for (int i = 0; i < _config.Kits.Length; i++)
            {
                var k = _config.Kits[i];
                if (k != null && !string.IsNullOrEmpty(k.Name) && string.Equals(k.Name, token, StringComparison.OrdinalIgnoreCase))
                {
                    found = i; break;
                }
            }
            if (found == -1)
            {
                SendReply(player, $"Kit '{token}' not found.");
                return;
            }
            OpenEditor(player, found);
        }

[ConsoleCommand("ks.version")]
        private void Console_KS_Version(ConsoleSystem.Arg arg)
        {
            var msg = $"[KitsSuite] Current version: {PluginVersion} ({BuildHash})";
            var player = arg.Player();
            if (player != null) player.ChatMessage(msg); else Puts(msg);
        }

        private static readonly string[] _hiddenNames = new string[]
        {
            "kitbandit1","kitbandit2","kitcobalt1","kitcobalt2","kitpmc1","kitpmc2"
        };

        private void EnsureHiddenKitNames()
        {
            if (_config == null || _config.Kits == null) return;
            for (int i = 6; i < _config.Kits.Length && i < 12; i++)
            {
                if (_config.Kits[i] == null) _config.Kits[i] = new KitDef();
                if (string.IsNullOrEmpty(_config.Kits[i].Name))
                    _config.Kits[i].Name = _hiddenNames[i - 6];
            }
            SaveConfig();
        }
        }
}
