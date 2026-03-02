using System;
using System.Collections.Generic;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("ServerInfo", "BLOODHELL", "1.0.0")]
    [Description("PNG-style server info UI with tabs and admin layout editor.")]
    public class ServerInfo : RustPlugin
    {
        private const string PermAdmin = "serverinfo.admin";
        private const string UiMain = "ServerInfo.UI.Main";
        private const string UiOverlay = "ServerInfo.UI.Overlay";

        [PluginReference] private Plugin ImageLibrary;

        private ConfigData _config;
        private readonly Dictionary<ulong, string> _activeTabByPlayer = new Dictionary<ulong, string>();

        private class ConfigData
        {
            public UiConfig Ui = new UiConfig();
            public List<TabConfig> Tabs = new List<TabConfig>
            {
                new TabConfig { Key = "info", Name = "Информация", ImageKey = "serverinfo_tab_info", FallbackUrl = "" },
                new TabConfig { Key = "events", Name = "Ивенты", ImageKey = "serverinfo_tab_events", FallbackUrl = "" },
                new TabConfig { Key = "rules", Name = "Правила", ImageKey = "serverinfo_tab_rules", FallbackUrl = "" },
                new TabConfig { Key = "about", Name = "О нас", ImageKey = "serverinfo_tab_about", FallbackUrl = "" }
            };

            public string DefaultTab = "info";
        }

        private class UiConfig
        {
            public string OverlayColor = "0 0 0 0.75";
            public string MainAnchorMin = "0.2 0.12";
            public string MainAnchorMax = "0.8 0.88";

            public string FrameImageKey = "serverinfo_frame";
            public string FrameFallbackUrl = "";

            public RectConfig LeftTabsArea = new RectConfig
            {
                AnchorMin = "0.03 0.10",
                AnchorMax = "0.31 0.90"
            };

            public RectConfig ContentArea = new RectConfig
            {
                AnchorMin = "0.33 0.10",
                AnchorMax = "0.97 0.90"
            };

            public RectConfig CloseButton = new RectConfig
            {
                AnchorMin = "0.94 0.92",
                AnchorMax = "0.985 0.985"
            };

            public RectConfig SettingsButton = new RectConfig
            {
                AnchorMin = "0.80 0.92",
                AnchorMax = "0.935 0.985"
            };
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
        }

        protected override void LoadDefaultConfig()
        {
            _config = new ConfigData();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<ConfigData>();
                if (_config == null) throw new Exception("Config is null");
            }
            catch
            {
                PrintWarning("Config invalid, creating default config");
                LoadDefaultConfig();
            }

            EnsureConfig();
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        private void Init()
        {
            permission.RegisterPermission(PermAdmin, this);
            EnsureConfig();
            cmd.AddChatCommand("info", this, nameof(CmdInfo));
            cmd.AddConsoleCommand("serverinfo.ui", this, nameof(CmdUi));
            cmd.AddConsoleCommand("serverinfo.close", this, nameof(CmdClose));
            cmd.AddConsoleCommand("serverinfo.editor", this, nameof(CmdEditor));
        }

        private void EnsureConfig()
        {
            if (_config == null) _config = new ConfigData();
            if (_config.Ui == null) _config.Ui = new UiConfig();
            if (_config.Tabs == null || _config.Tabs.Count == 0)
            {
                _config.Tabs = new ConfigData().Tabs;
            }

            if (string.IsNullOrEmpty(_config.DefaultTab)) _config.DefaultTab = "info";
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

        private void CmdEditor(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!HasAdmin(player))
            {
                SendReply(player, "Нет прав: serverinfo.admin");
                return;
            }

            if (arg.Args == null || arg.Args.Length < 5)
            {
                SendReply(player, "Использование: serverinfo.editor <target> <anchorMinX> <anchorMinY> <anchorMaxX> <anchorMaxY>");
                SendReply(player, "target: close | settings | lefttabs | content");
                return;
            }

            var target = arg.Args[0].ToLowerInvariant();
            var min = $"{arg.Args[1]} {arg.Args[2]}";
            var max = $"{arg.Args[3]} {arg.Args[4]}";

            if (!TryUpdateTargetRect(target, min, max))
            {
                SendReply(player, $"Неизвестный target: {target}");
                return;
            }

            SaveConfig();
            SendReply(player, $"Обновлено: {target} => {min} / {max}");
            OpenUi(player, _activeTabByPlayer.TryGetValue(player.userID, out var tab) ? tab : null);
        }

        private bool TryUpdateTargetRect(string target, string min, string max)
        {
            if (target == "close")
            {
                _config.Ui.CloseButton.AnchorMin = min;
                _config.Ui.CloseButton.AnchorMax = max;
                return true;
            }

            if (target == "settings")
            {
                _config.Ui.SettingsButton.AnchorMin = min;
                _config.Ui.SettingsButton.AnchorMax = max;
                return true;
            }

            if (target == "lefttabs")
            {
                _config.Ui.LeftTabsArea.AnchorMin = min;
                _config.Ui.LeftTabsArea.AnchorMax = max;
                return true;
            }

            if (target == "content")
            {
                _config.Ui.ContentArea.AnchorMin = min;
                _config.Ui.ContentArea.AnchorMax = max;
                return true;
            }

            return false;
        }

        private void OpenUi(BasePlayer player, string tabKey)
        {
            EnsureImagesRegistered();

            var selected = ResolveTab(tabKey);
            _activeTabByPlayer[player.userID] = selected.Key;

            DestroyUi(player);

            var container = new CuiElementContainer();

            // Layer rule: PNG backgrounds are added first.
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

            AddImageElement(container, root, _config.Ui.FrameImageKey, _config.Ui.FrameFallbackUrl, "0 0", "1 1", "ServerInfo.Frame");

            AddImageElement(container, root, selected.ImageKey, selected.FallbackUrl, _config.Ui.ContentArea.AnchorMin, _config.Ui.ContentArea.AnchorMax, "ServerInfo.Content");

            AddTabButtons(container, root, selected.Key);
            AddCloseButton(container, root);

            if (HasAdmin(player))
                AddSettingsButton(container, root);

            CuiHelper.AddUi(player, container);
        }

        private void AddTabButtons(CuiElementContainer container, string parent, string selectedKey)
        {
            var area = _config.Ui.LeftTabsArea;
            var count = Mathf.Max(1, _config.Tabs.Count);
            var step = 1f / count;

            for (var i = 0; i < _config.Tabs.Count; i++)
            {
                var tab = _config.Tabs[i];
                var minY = 1f - ((i + 1) * step);
                var maxY = 1f - (i * step);

                var buttonParent = container.Add(new CuiPanel
                {
                    Image = { Color = "1 1 1 0" },
                    RectTransform =
                    {
                        AnchorMin = $"{area.AnchorMin.Split(' ')[0]} {Mathf.Lerp(ParseY(area.AnchorMin), ParseY(area.AnchorMax), minY)}",
                        AnchorMax = $"{area.AnchorMax.Split(' ')[0]} {Mathf.Lerp(ParseY(area.AnchorMin), ParseY(area.AnchorMax), maxY)}"
                    }
                }, parent, $"ServerInfo.Tab.Area.{tab.Key}");

                container.Add(new CuiButton
                {
                    Button =
                    {
                        Color = tab.Key.Equals(selectedKey, StringComparison.OrdinalIgnoreCase) ? "1 1 1 0.06" : "1 1 1 0.01",
                        Command = $"serverinfo.ui {tab.Key}",
                        Close = string.Empty
                    },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    Text = { Text = tab.Name, FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, buttonParent, $"ServerInfo.Tab.Button.{tab.Key}");
            }
        }

        private void AddCloseButton(CuiElementContainer container, string parent)
        {
            container.Add(new CuiButton
            {
                Button = { Color = "1 1 1 0", Command = "serverinfo.close" },
                RectTransform = { AnchorMin = _config.Ui.CloseButton.AnchorMin, AnchorMax = _config.Ui.CloseButton.AnchorMax },
                Text = { Text = string.Empty }
            }, parent, "ServerInfo.Close");
        }

        private void AddSettingsButton(CuiElementContainer container, string parent)
        {
            container.Add(new CuiButton
            {
                Button = { Color = "0.2 0.5 0.8 0.35", Command = "serverinfo.editor help" },
                RectTransform = { AnchorMin = _config.Ui.SettingsButton.AnchorMin, AnchorMax = _config.Ui.SettingsButton.AnchorMax },
                Text = { Text = "Настройки", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, parent, "ServerInfo.Settings");
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

        private void EnsureImagesRegistered()
        {
            if (ImageLibrary == null) return;

            TryRegister(_config.Ui.FrameImageKey, _config.Ui.FrameFallbackUrl);
            foreach (var tab in _config.Tabs)
            {
                TryRegister(tab.ImageKey, tab.FallbackUrl);
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
            return permission.UserHasPermission(player.UserIDString, PermAdmin);
        }

        private void DestroyUi(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UiMain);
            CuiHelper.DestroyUi(player, UiOverlay);
        }

        private static float ParseY(string anchor)
        {
            if (string.IsNullOrEmpty(anchor)) return 0f;
            var split = anchor.Split(' ');
            if (split.Length != 2) return 0f;
            return float.TryParse(split[1], out var y) ? y : 0f;
        }
    }
}
