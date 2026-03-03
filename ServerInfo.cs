using System;
using System.Collections.Generic;
using System.Globalization;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("ServerInfo", "BLOODHELL", "1.1.0")]
    [Description("PNG-style server info UI with tabs and admin layout editor.")]
    public class ServerInfo : RustPlugin
    {
        private const string PermAdmin = "serverinfo.admin";
        private const string UiMain = "ServerInfo.UI.Main";
        private const string UiOverlay = "ServerInfo.UI.Overlay";
        private const string UiEditor = "ServerInfo.UI.Editor";

        [PluginReference] private Plugin ImageLibrary;

        private ConfigData _config;
        private readonly Dictionary<ulong, string> _activeTabByPlayer = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, int> _eventsPageByPlayer = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, EditorState> _editorStateByPlayer = new Dictionary<ulong, EditorState>();

        private class ConfigData
        {
            public UiConfig Ui = new UiConfig();
            public List<TabConfig> Tabs = new List<TabConfig>
            {
                new TabConfig { Key = "info", Name = "Информация", ImageKey = "serverinfo_tab_info", FallbackUrl = "", ButtonRect = new RectConfig { AnchorMin = "0.03 0.81", AnchorMax = "0.31 0.95" } },
                new TabConfig { Key = "rules", Name = "Правила", ImageKey = "serverinfo_tab_rules", FallbackUrl = "", ButtonRect = new RectConfig { AnchorMin = "0.03 0.66", AnchorMax = "0.31 0.80" } },
                new TabConfig { Key = "commands", Name = "Команды", ImageKey = "serverinfo_tab_commands", FallbackUrl = "", ButtonRect = new RectConfig { AnchorMin = "0.03 0.51", AnchorMax = "0.31 0.65" } },
                new TabConfig { Key = "events", Name = "Ивенты", ImageKey = "serverinfo_tab_events", FallbackUrl = "", ButtonRect = new RectConfig { AnchorMin = "0.03 0.36", AnchorMax = "0.31 0.50" } },
                new TabConfig { Key = "about", Name = "О нас", ImageKey = "serverinfo_tab_about", FallbackUrl = "", ButtonRect = new RectConfig { AnchorMin = "0.03 0.21", AnchorMax = "0.31 0.35" } },
                new TabConfig { Key = "links", Name = "Ссылки", ImageKey = "serverinfo_tab_links", FallbackUrl = "", ButtonRect = new RectConfig { AnchorMin = "0.03 0.06", AnchorMax = "0.31 0.20" } }
            };

            public string DefaultTab = "info";
        }

        private class UiConfig
        {
            public string OverlayColor = "0 0 0 0.75";
            public string MainAnchorMin = "0.05 0.05";
            public string MainAnchorMax = "0.95 0.95";

            public string FrameImageKey = "serverinfo_frame";
            public string FrameFallbackUrl = "";

            public string LeftPanelImageKey = "serverinfo_leftpanel";
            public string LeftPanelFallbackUrl = "";
            public RectConfig LeftPanelArea = new RectConfig
            {
                AnchorMin = "0.03 0.05",
                AnchorMax = "0.31 0.95"
            };

            public RectConfig ContentArea = new RectConfig
            {
                AnchorMin = "0.33 0.05",
                AnchorMax = "0.95 0.95"
            };

            public string CloseImageKey = "serverinfo_close";
            public string CloseFallbackUrl = "";
            public RectConfig CloseButton = new RectConfig
            {
                AnchorMin = "0.955 0.935",
                AnchorMax = "0.99 0.985"
            };

            public RectConfig SettingsButton = new RectConfig
            {
                AnchorMin = "0.80 0.935",
                AnchorMax = "0.945 0.985"
            };

            public RectConfig EventsPrevButton = new RectConfig
            {
                AnchorMin = "0.62 0.06",
                AnchorMax = "0.70 0.12"
            };

            public RectConfig EventsNextButton = new RectConfig
            {
                AnchorMin = "0.72 0.06",
                AnchorMax = "0.80 0.12"
            };

            public float EditorStep = 0.005f;
        }

        private class RectConfig
        {
            public string AnchorMin;
            public string AnchorMax;
        }

        private class TabConfig
        {
            public string Key;
            public string Name;
            public string ImageKey;
            public string FallbackUrl;
            public string ContentImageKey;
            public string ContentFallbackUrl;
            public RectConfig ButtonRect;
        }

        private class EditorState
        {
            public int TargetIndex;
        }

        protected override void LoadDefaultConfig()
        {
            _config = new ConfigData();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            var configLoadFailed = false;
            try
            {
                _config = Config.ReadObject<ConfigData>();
                if (_config == null) throw new Exception("Config is null");
            }
            catch (Exception ex)
            {
                configLoadFailed = true;
                PrintWarning($"Config invalid ({ex.Message}), creating default config in memory");
                LoadDefaultConfig();
            }

            var changed = EnsureConfig();
            if (changed && !configLoadFailed)
            {
                SaveConfig();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        private void Init()
        {
            permission.RegisterPermission(PermAdmin, this);
            EnsureConfig();
            cmd.AddChatCommand("info", this, nameof(CmdInfo));
            cmd.AddConsoleCommand("serverinfo.ui", this, nameof(CmdUi));
            cmd.AddConsoleCommand("serverinfo.events.page", this, nameof(CmdEventsPage));
            cmd.AddConsoleCommand("serverinfo.close", this, nameof(CmdClose));
            cmd.AddConsoleCommand("serverinfo.editor", this, nameof(CmdEditor));
        }

        private bool EnsureConfig()
        {
            var changed = false;
            if (_config == null)
            {
                _config = new ConfigData();
                changed = true;
            }
            if (_config.Ui == null)
            {
                _config.Ui = new UiConfig();
                changed = true;
            }
            if (_config.Ui.LeftPanelArea == null)
            {
                _config.Ui.LeftPanelArea = new RectConfig { AnchorMin = "0.03 0.05", AnchorMax = "0.31 0.95" };
                changed = true;
            }
            if (_config.Ui.ContentArea == null)
            {
                _config.Ui.ContentArea = new RectConfig { AnchorMin = "0.33 0.05", AnchorMax = "0.95 0.95" };
                changed = true;
            }
            if (_config.Ui.CloseButton == null)
            {
                _config.Ui.CloseButton = new RectConfig { AnchorMin = "0.955 0.935", AnchorMax = "0.99 0.985" };
                changed = true;
            }
            if (_config.Ui.SettingsButton == null)
            {
                _config.Ui.SettingsButton = new RectConfig { AnchorMin = "0.80 0.935", AnchorMax = "0.945 0.985" };
                changed = true;
            }
            if (_config.Ui.EventsPrevButton == null)
            {
                _config.Ui.EventsPrevButton = new RectConfig { AnchorMin = "0.62 0.06", AnchorMax = "0.70 0.12" };
                changed = true;
            }
            if (_config.Ui.EventsNextButton == null)
            {
                _config.Ui.EventsNextButton = new RectConfig { AnchorMin = "0.72 0.06", AnchorMax = "0.80 0.12" };
                changed = true;
            }
            if (_config.Ui.EditorStep <= 0f)
            {
                _config.Ui.EditorStep = 0.005f;
                changed = true;
            }

            if (_config.Tabs == null || _config.Tabs.Count == 0)
            {
                _config.Tabs = new ConfigData().Tabs;
                changed = true;
            }

            var uniqTabs = new List<TabConfig>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tabsWereNormalized = false;
            for (var i = _config.Tabs.Count - 1; i >= 0; i--)
            {
                var tab = _config.Tabs[i];
                if (tab == null || string.IsNullOrEmpty(tab.Key))
                {
                    tabsWereNormalized = true;
                    continue;
                }

                if (!seenKeys.Add(tab.Key))
                {
                    tabsWereNormalized = true;
                    continue;
                }

                uniqTabs.Insert(0, tab);
            }

            // Важно: если есть пользовательские вкладки, не дополняем и не заменяем их дефолтами.
            // Дефолт нужен только когда вкладок нет вообще.
            if (uniqTabs.Count > 0)
            {
                if (tabsWereNormalized)
                {
                    _config.Tabs = uniqTabs;
                    changed = true;
                }
            }
            else if (_config.Tabs.Count > 0)
            {
                _config.Tabs = new ConfigData().Tabs;
                changed = true;
            }

            var defaultTabs = new ConfigData().Tabs;

            for (var i = 0; i < _config.Tabs.Count; i++)
            {
                if (_config.Tabs[i].ButtonRect != null) continue;

                RectConfig fallbackRect = null;
                for (var j = 0; j < defaultTabs.Count; j++)
                {
                    if (!defaultTabs[j].Key.Equals(_config.Tabs[i].Key, StringComparison.OrdinalIgnoreCase)) continue;
                    fallbackRect = defaultTabs[j].ButtonRect;
                    break;
                }

                if (fallbackRect != null)
                {
                    _config.Tabs[i].ButtonRect = new RectConfig
                    {
                        AnchorMin = fallbackRect.AnchorMin,
                        AnchorMax = fallbackRect.AnchorMax
                    };
                    changed = true;
                    continue;
                }

                var minY = 0.76f - (0.22f * i);
                var maxY = 0.95f - (0.22f * i);
                _config.Tabs[i].ButtonRect = new RectConfig
                {
                    AnchorMin = $"0.03 {minY.ToString("0.###", CultureInfo.InvariantCulture)}",
                    AnchorMax = $"0.31 {maxY.ToString("0.###", CultureInfo.InvariantCulture)}"
                };
                changed = true;
            }

            if (string.IsNullOrEmpty(_config.DefaultTab))
            {
                _config.DefaultTab = "info";
                changed = true;
            }

            return changed;
        }

        private void OnServerInitialized()
        {
            EnsureImagesRegistered();
        }

        private void Unload()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                DestroyUi(player);
            }
        }

        private void CmdInfo(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            OpenUi(player, null);
        }

        private void CmdUi(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;

            var tab = arg.Args != null && arg.Args.Length > 0 ? arg.Args[0] : null;
            OpenUi(player, tab);
        }

        private void CmdClose(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            DestroyUi(player);
        }

        private void CmdEventsPage(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;

            if (!_activeTabByPlayer.TryGetValue(player.userID, out var activeTab) || !activeTab.Equals("events", StringComparison.OrdinalIgnoreCase)) return;
            if (arg.Args == null || arg.Args.Length == 0) return;

            var direction = arg.Args[0].ToLowerInvariant();
            var maxPage = GetEventsMaxPage();
            if (maxPage <= 1) return;

            var page = GetCurrentEventsPage(player.userID);
            if (direction == "next") page++;
            else if (direction == "prev") page--;
            else return;

            if (page < 1) page = maxPage;
            if (page > maxPage) page = 1;

            _eventsPageByPlayer[player.userID] = page;
            OpenUi(player, "events");
        }

        private void CmdEditor(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!HasAdmin(player))
            {
                SendReply(player, "Нет прав: oxide.admin / serverinfo.admin");
                return;
            }

            var action = arg.Args != null && arg.Args.Length > 0 ? arg.Args[0].ToLowerInvariant() : "open";

            if (action == "open")
            {
                if (!_editorStateByPlayer.ContainsKey(player.userID))
                    _editorStateByPlayer[player.userID] = new EditorState();
                OpenUi(player, _activeTabByPlayer.TryGetValue(player.userID, out var tab) ? tab : null);
                return;
            }

            if (action == "close")
            {
                _editorStateByPlayer.Remove(player.userID);
                OpenUi(player, _activeTabByPlayer.TryGetValue(player.userID, out var tab) ? tab : null);
                return;
            }

            if (!_editorStateByPlayer.TryGetValue(player.userID, out var state))
            {
                _editorStateByPlayer[player.userID] = new EditorState();
                state = _editorStateByPlayer[player.userID];
            }

            if (action == "next")
            {
                state.TargetIndex = (state.TargetIndex + 1) % GetEditableTargetCount();
                OpenUi(player, _activeTabByPlayer.TryGetValue(player.userID, out var tab) ? tab : null);
                return;
            }

            if (action == "prev")
            {
                state.TargetIndex = (state.TargetIndex - 1 + GetEditableTargetCount()) % GetEditableTargetCount();
                OpenUi(player, _activeTabByPlayer.TryGetValue(player.userID, out var tab) ? tab : null);
                return;
            }

            if (action == "step" && arg.Args.Length >= 2)
            {
                if (!TryParseFloat(arg.Args[1], out var step) || step <= 0f)
                {
                    SendReply(player, "Неверный шаг. Пример: serverinfo.editor step 0.005");
                    return;
                }

                _config.Ui.EditorStep = step;
                SaveConfig();
                OpenUi(player, _activeTabByPlayer.TryGetValue(player.userID, out var tab) ? tab : null);
                return;
            }

            if (action == "nudge" && arg.Args.Length >= 3)
            {
                if (!TryParseFloat(arg.Args[1], out var dx) || !TryParseFloat(arg.Args[2], out var dy))
                {
                    SendReply(player, "Неверные значения nudge. Пример: serverinfo.editor nudge 1 -1");
                    return;
                }

                MoveTarget(state.TargetIndex, dx * _config.Ui.EditorStep, dy * _config.Ui.EditorStep);
                SaveConfig();
                OpenUi(player, _activeTabByPlayer.TryGetValue(player.userID, out var tab) ? tab : null);
                return;
            }

            if (action == "resize" && arg.Args.Length >= 3)
            {
                if (!TryParseFloat(arg.Args[1], out var dx) || !TryParseFloat(arg.Args[2], out var dy))
                {
                    SendReply(player, "Неверные значения resize. Пример: serverinfo.editor resize 1 -1");
                    return;
                }

                ResizeTarget(state.TargetIndex, dx * _config.Ui.EditorStep, dy * _config.Ui.EditorStep);
                SaveConfig();
                OpenUi(player, _activeTabByPlayer.TryGetValue(player.userID, out var tab) ? tab : null);
                return;
            }

            SendReply(player, "Команды редактора: open | close | next | prev | step <value> | nudge <x> <y> | resize <x> <y>");
        }

        private void OpenUi(BasePlayer player, string tabKey)
        {
            EnsureImagesRegistered();

            var selected = ResolveTab(tabKey);
            _activeTabByPlayer[player.userID] = selected.Key;
            var eventsPage = GetCurrentEventsPage(player.userID);

            DestroyUi(player);

            var container = new CuiElementContainer();

            // Hard rule: images/backgrounds are always added before any buttons.
            container.Add(new CuiPanel
            {
                Image = { Color = _config.Ui.OverlayColor },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true
            }, "Overlay", UiOverlay);

            var root = container.Add(new CuiPanel
            {
                Image = { Color = "1 1 1 0" },
                RectTransform = { AnchorMin = _config.Ui.MainAnchorMin, AnchorMax = _config.Ui.MainAnchorMax },
                CursorEnabled = true
            }, UiOverlay, UiMain);

            AddImageElement(container, root, _config.Ui.LeftPanelImageKey, _config.Ui.LeftPanelFallbackUrl, _config.Ui.LeftPanelArea.AnchorMin, _config.Ui.LeftPanelArea.AnchorMax, "ServerInfo.LeftPanel");
            var contentImageKey = ResolveContentImageKey(selected, eventsPage);
            var contentFallbackUrl = ResolveContentFallbackUrl(selected);
            AddImageElement(container, root, contentImageKey, contentFallbackUrl, _config.Ui.ContentArea.AnchorMin, _config.Ui.ContentArea.AnchorMax, "ServerInfo.Content");
            AddImageElement(container, root, _config.Ui.FrameImageKey, _config.Ui.FrameFallbackUrl, "0 0", "1 1", "ServerInfo.Frame");
            AddImageElement(container, root, _config.Ui.CloseImageKey, _config.Ui.CloseFallbackUrl, _config.Ui.CloseButton.AnchorMin, _config.Ui.CloseButton.AnchorMax, "ServerInfo.Close.Image");

            AddTabButtons(container, root, selected.Key);
            AddEventsPagerButtons(container, root, selected.Key, _editorStateByPlayer.ContainsKey(player.userID));
            AddCloseButton(container, root);

            if (HasAdmin(player))
            {
                AddSettingsButton(container, root);

                if (_editorStateByPlayer.ContainsKey(player.userID))
                    AddEditorUi(container, root, player.userID);
            }

            CuiHelper.AddUi(player, container);
        }

        private void AddTabButtons(CuiElementContainer container, string parent, string selectedKey)
        {
            for (var i = 0; i < _config.Tabs.Count; i++)
            {
                var tab = _config.Tabs[i];

                container.Add(new CuiElement
                {
                    Name = $"ServerInfo.Tab.Border.{i}",
                    Parent = parent,
                    Components =
                    {
                        new CuiImageComponent { Color = "1 1 1 0" },
                        new CuiOutlineComponent
                        {
                            Color = "1 1 1 0",
                            Distance = "1.25 -1.25"
                        },
                        new CuiRectTransformComponent { AnchorMin = tab.ButtonRect.AnchorMin, AnchorMax = tab.ButtonRect.AnchorMax }
                    }
                });

                container.Add(new CuiButton
                {
                    Button =
                    {
                        Color = "1 1 1 0",
                        Command = $"serverinfo.ui {tab.Key}"
                    },
                    RectTransform = { AnchorMin = tab.ButtonRect.AnchorMin, AnchorMax = tab.ButtonRect.AnchorMax },
                    Text = { Text = "", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 0" }
                }, parent, $"ServerInfo.Tab.Button.{i}");

            }
        }

        private void AddCloseButton(CuiElementContainer container, string parent)
        {
            container.Add(new CuiButton
            {
                Button = { Color = "1 1 1 0", Command = "serverinfo.close" },
                RectTransform = { AnchorMin = _config.Ui.CloseButton.AnchorMin, AnchorMax = _config.Ui.CloseButton.AnchorMax },
                Text = { Text = "", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 0" }
            }, parent, "ServerInfo.Close");
        }

        private void AddEventsPagerButtons(CuiElementContainer container, string parent, string selectedKey, bool editorMode)
        {
            if (!selectedKey.Equals("events", StringComparison.OrdinalIgnoreCase)) return;
            if (!editorMode && GetEventsMaxPage() <= 1) return;

            var buttonColor = editorMode ? "0.2 0.8 1 0.35" : "1 1 1 0";
            var textColor = editorMode ? "1 1 1 0.95" : "1 1 1 0";
            var prevText = editorMode ? "<" : "";
            var nextText = editorMode ? ">" : "";

            container.Add(new CuiButton
            {
                Button = { Color = buttonColor, Command = "serverinfo.events.page prev" },
                RectTransform = { AnchorMin = _config.Ui.EventsPrevButton.AnchorMin, AnchorMax = _config.Ui.EventsPrevButton.AnchorMax },
                Text = { Text = prevText, FontSize = 14, Align = TextAnchor.MiddleCenter, Color = textColor }
            }, parent, "ServerInfo.Events.Prev");

            container.Add(new CuiButton
            {
                Button = { Color = buttonColor, Command = "serverinfo.events.page next" },
                RectTransform = { AnchorMin = _config.Ui.EventsNextButton.AnchorMin, AnchorMax = _config.Ui.EventsNextButton.AnchorMax },
                Text = { Text = nextText, FontSize = 14, Align = TextAnchor.MiddleCenter, Color = textColor }
            }, parent, "ServerInfo.Events.Next");
        }

        private void AddSettingsButton(CuiElementContainer container, string parent)
        {
            container.Add(new CuiButton
            {
                Button = { Color = "0.2 0.5 0.8 0.75", Command = "serverinfo.editor open" },
                RectTransform = { AnchorMin = _config.Ui.SettingsButton.AnchorMin, AnchorMax = _config.Ui.SettingsButton.AnchorMax },
                Text = { Text = "Настройки", FontSize = 13, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, parent, "ServerInfo.Settings");
        }

        private void AddEditorUi(CuiElementContainer container, string parent, ulong userId)
        {
            var state = _editorStateByPlayer[userId];
            var targetName = GetTargetName(state.TargetIndex);
            var targetRect = GetTargetRect(state.TargetIndex);

            var panel = container.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.8" },
                RectTransform = { AnchorMin = "0.27 0.35", AnchorMax = "0.73 0.65" }
            }, parent, UiEditor);

            container.Add(new CuiLabel
            {
                RectTransform = { AnchorMin = "0.03 0.72", AnchorMax = "0.97 0.97" },
                Text =
                {
                    Text = $"РЕДАКТОР | цель: {targetName} | шаг: {_config.Ui.EditorStep.ToString("0.###", CultureInfo.InvariantCulture)}\nмин: {targetRect.AnchorMin} макс: {targetRect.AnchorMax}",
                    FontSize = 13,
                    Align = TextAnchor.UpperLeft,
                    Color = "1 1 1 1"
                }
            }, panel);

            AddEditorButton(container, panel, "Назад", "0.03 0.46", "0.22 0.70", "serverinfo.editor prev");
            AddEditorButton(container, panel, "Дальше", "0.24 0.46", "0.43 0.70", "serverinfo.editor next");
            AddEditorButton(container, panel, "Шаг +", "0.45 0.46", "0.64 0.70", $"serverinfo.editor step {(_config.Ui.EditorStep + 0.001f).ToString("0.###", CultureInfo.InvariantCulture)}");
            AddEditorButton(container, panel, "Шаг -", "0.66 0.46", "0.85 0.70", $"serverinfo.editor step {Mathf.Max(0.001f, _config.Ui.EditorStep - 0.001f).ToString("0.###", CultureInfo.InvariantCulture)}");
            AddEditorButton(container, panel, "Выход", "0.03 0.18", "0.22 0.42", "serverinfo.editor close");

            AddEditorButton(container, panel, "Лево", "0.24 0.18", "0.39 0.42", "serverinfo.editor nudge -1 0");
            AddEditorButton(container, panel, "Право", "0.41 0.18", "0.56 0.42", "serverinfo.editor nudge 1 0");
            AddEditorButton(container, panel, "Вверх", "0.58 0.18", "0.73 0.42", "serverinfo.editor nudge 0 1");
            AddEditorButton(container, panel, "Вниз", "0.75 0.18", "0.90 0.42", "serverinfo.editor nudge 0 -1");

            AddEditorButton(container, panel, "Шир +", "0.24 0.02", "0.39 0.16", "serverinfo.editor resize 1 0");
            AddEditorButton(container, panel, "Шир -", "0.41 0.02", "0.56 0.16", "serverinfo.editor resize -1 0");
            AddEditorButton(container, panel, "Выс +", "0.58 0.02", "0.73 0.16", "serverinfo.editor resize 0 1");
            AddEditorButton(container, panel, "Выс -", "0.75 0.02", "0.90 0.16", "serverinfo.editor resize 0 -1");
        }

        private void AddEditorButton(CuiElementContainer container, string parent, string text, string min, string max, string command)
        {
            container.Add(new CuiButton
            {
                Button = { Color = "0.25 0.25 0.25 0.95", Command = command },
                RectTransform = { AnchorMin = min, AnchorMax = max },
                Text = { Text = text, FontSize = 13, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, parent);
        }

        private string GetTargetName(int index)
        {
            if (index == 0) return "close";
            var tabIndex = index - 1;
            if (tabIndex >= 0 && tabIndex < _config.Tabs.Count) return $"tab:{_config.Tabs[tabIndex].Key}";
            if (index == _config.Tabs.Count + 1) return "events_prev";
            if (index == _config.Tabs.Count + 2) return "events_next";
            return "close";
        }

        private int GetEditableTargetCount() => _config.Tabs.Count + 3;

        private RectConfig GetTargetRect(int index)
        {
            if (index == 0) return _config.Ui.CloseButton;
            var tabIndex = index - 1;
            if (tabIndex >= 0 && tabIndex < _config.Tabs.Count) return _config.Tabs[tabIndex].ButtonRect;
            if (index == _config.Tabs.Count + 1) return _config.Ui.EventsPrevButton;
            if (index == _config.Tabs.Count + 2) return _config.Ui.EventsNextButton;
            return _config.Ui.CloseButton;
        }

        private void MoveTarget(int index, float x, float y)
        {
            var rect = GetTargetRect(index);
            var min = ParseVec2(rect.AnchorMin);
            var max = ParseVec2(rect.AnchorMax);
            min += new Vector2(x, y);
            max += new Vector2(x, y);
            rect.AnchorMin = VecToAnchor(min);
            rect.AnchorMax = VecToAnchor(max);
        }

        private void ResizeTarget(int index, float x, float y)
        {
            var rect = GetTargetRect(index);
            var min = ParseVec2(rect.AnchorMin);
            var max = ParseVec2(rect.AnchorMax);
            max += new Vector2(x, y);
            if (max.x <= min.x + 0.001f) max.x = min.x + 0.001f;
            if (max.y <= min.y + 0.001f) max.y = min.y + 0.001f;
            rect.AnchorMax = VecToAnchor(max);
        }

        private Vector2 ParseVec2(string anchor)
        {
            if (string.IsNullOrEmpty(anchor)) return Vector2.zero;
            var split = anchor.Split(' ');
            if (split.Length != 2) return Vector2.zero;
            if (!TryParseFloat(split[0], out var x)) x = 0f;
            if (!TryParseFloat(split[1], out var y)) y = 0f;
            return new Vector2(x, y);
        }

        private string VecToAnchor(Vector2 value)
        {
            return $"{value.x.ToString("0.###", CultureInfo.InvariantCulture)} {value.y.ToString("0.###", CultureInfo.InvariantCulture)}";
        }

        private bool TryParseFloat(string value, out float parsed)
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
        }

        private void AddImageElement(CuiElementContainer container, string parent, string imageKey, string fallbackUrl, string anchorMin, string anchorMax, string name)
        {
            var png = GetPng(imageKey);
            if (!string.IsNullOrEmpty(png))
            {
                container.Add(new CuiElement
                {
                    Name = name,
                    Parent = parent,
                    Components =
                    {
                        new CuiRawImageComponent { Png = png, Color = "1 1 1 1" },
                        new CuiRectTransformComponent { AnchorMin = anchorMin, AnchorMax = anchorMax }
                    }
                });
                return;
            }

            if (!string.IsNullOrEmpty(fallbackUrl))
            {
                container.Add(new CuiElement
                {
                    Name = name,
                    Parent = parent,
                    Components =
                    {
                        new CuiRawImageComponent { Url = fallbackUrl, Color = "1 1 1 1" },
                        new CuiRectTransformComponent { AnchorMin = anchorMin, AnchorMax = anchorMax }
                    }
                });
                return;
            }

            container.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.5" },
                RectTransform = { AnchorMin = anchorMin, AnchorMax = anchorMax }
            }, parent, name + ".Fallback");
        }

        private string GetPng(string key)
        {
            if (ImageLibrary == null || string.IsNullOrEmpty(key)) return null;
            try
            {
                var res = ImageLibrary.Call("GetImage", key);
                return res as string;
            }
            catch
            {
                return null;
            }
        }

        private string ResolveContentImageKey(TabConfig tab, int eventsPage = 1)
        {
            if (tab == null) return null;

            if (tab.Key.Equals("events", StringComparison.OrdinalIgnoreCase))
            {
                if (eventsPage < 1) eventsPage = 1;

                var pageSpecific = BuildEventsPageKey(tab, eventsPage);
                if (!string.IsNullOrEmpty(pageSpecific) && !string.IsNullOrEmpty(GetPng(pageSpecific))) return pageSpecific;

                if (eventsPage == 1)
                {
                    var pageOneAlt = BuildEventsPageKey(tab, 0);
                    if (!string.IsNullOrEmpty(pageOneAlt) && !string.IsNullOrEmpty(GetPng(pageOneAlt))) return pageOneAlt;
                }
            }

            if (!string.IsNullOrEmpty(tab.ContentImageKey) && !string.IsNullOrEmpty(GetPng(tab.ContentImageKey))) return tab.ContentImageKey;
            if (!string.IsNullOrEmpty(GetPng(tab.ImageKey))) return tab.ImageKey;

            var prefixed = $"serverinfo_content_{tab.Key}";
            if (!string.IsNullOrEmpty(GetPng(prefixed))) return prefixed;

            var generic = $"serverinfo_{tab.Key}";
            if (!string.IsNullOrEmpty(GetPng(generic))) return generic;

            if (!string.IsNullOrEmpty(GetPng(tab.Key))) return tab.Key;

            return !string.IsNullOrEmpty(tab.ContentImageKey) ? tab.ContentImageKey : tab.ImageKey;
        }

        private int GetCurrentEventsPage(ulong userId)
        {
            if (!_eventsPageByPlayer.TryGetValue(userId, out var page) || page < 1)
            {
                page = 1;
                _eventsPageByPlayer[userId] = page;
            }

            return page;
        }

        private int GetEventsMaxPage()
        {
            var tab = _config.Tabs.Find(t => t.Key.Equals("events", StringComparison.OrdinalIgnoreCase));
            if (tab == null) return 1;

            var max = 1;
            for (var i = 2; i <= 20; i++)
            {
                var key = BuildEventsPageKey(tab, i);
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(GetPng(key))) break;
                max = i;
            }

            return max;
        }

        private string BuildEventsPageKey(TabConfig tab, int page)
        {
            if (tab == null) return null;

            if (!string.IsNullOrEmpty(tab.ContentImageKey))
                return page <= 1 ? tab.ContentImageKey : $"{tab.ContentImageKey}_{page}";

            var baseKey = $"serverinfo_content_{tab.Key}";
            return page <= 1 ? baseKey : $"{baseKey}_{page}";
        }

        private string ResolveContentFallbackUrl(TabConfig tab)
        {
            if (tab == null) return null;
            return !string.IsNullOrEmpty(tab.ContentFallbackUrl) ? tab.ContentFallbackUrl : tab.FallbackUrl;
        }

        private void EnsureImagesRegistered()
        {
            if (ImageLibrary == null) return;

            TryRegister(_config.Ui.LeftPanelImageKey, _config.Ui.LeftPanelFallbackUrl);
            TryRegister(_config.Ui.FrameImageKey, _config.Ui.FrameFallbackUrl);
            TryRegister(_config.Ui.CloseImageKey, _config.Ui.CloseFallbackUrl);
            foreach (var tab in _config.Tabs)
            {
                TryRegister(tab.ImageKey, tab.FallbackUrl);
                TryRegister(tab.ContentImageKey, tab.ContentFallbackUrl);
            }
        }

        private void TryRegister(string key, string url)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(url) || ImageLibrary == null) return;
            ImageLibrary.Call("AddImage", url, key);
        }

        private TabConfig ResolveTab(string tabKey)
        {
            if (!string.IsNullOrEmpty(tabKey))
            {
                var explicitTab = _config.Tabs.Find(t => t.Key.Equals(tabKey, StringComparison.OrdinalIgnoreCase));
                if (explicitTab != null) return explicitTab;
            }

            var def = _config.Tabs.Find(t => t.Key.Equals(_config.DefaultTab, StringComparison.OrdinalIgnoreCase));
            return def ?? _config.Tabs[0];
        }

        private bool HasAdmin(BasePlayer player)
        {
            if (player == null) return false;
            return player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermAdmin);
        }

        private void DestroyUi(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UiEditor);
            CuiHelper.DestroyUi(player, UiMain);
            CuiHelper.DestroyUi(player, UiOverlay);
        }
    }
}
