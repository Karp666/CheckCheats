using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json;
using System.Threading.Tasks;
using System.Net.Http;
using System.Reflection;
using CSTimers = CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Utils;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using T3MenuSharedApi;
using CounterStrikeSharp.API.Core.Capabilities;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace CheckCheats
{
    public class CheckCheats : BasePlugin
    {
        public override string ModuleName => "CheckCheats by ABKAM, modified by Karp";
        public override string ModuleVersion => "1.1.0";
        private Dictionary<CCSPlayerController, CSTimers.Timer> checkTimers = new Dictionary<CCSPlayerController, CSTimers.Timer>();
        private Dictionary<CCSPlayerController, (string message, bool continueUpdating)> playerCenterMessages = new Dictionary<CCSPlayerController, (string message, bool continueUpdating)>();
        private Dictionary<CCSPlayerController, bool> isCheckActive = new Dictionary<CCSPlayerController, bool>();
        private Dictionary<CCSPlayerController, CSTimers.Timer> playerMessageTimers = new Dictionary<CCSPlayerController, CSTimers.Timer>();
        private Dictionary<CCSPlayerController, CCSPlayerController> adminInitiatingCheck = new Dictionary<CCSPlayerController, CCSPlayerController>();
        private static readonly HttpClient _httpClient = new HttpClient();
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
        private PluginConfig _config;

        private void LoadConfig()
        {
            string configFilePath = Path.Combine(ModuleDirectory, "Config.yml");
            if (!File.Exists(configFilePath))
            {
                _config = new PluginConfig();
                SaveConfig(_config, configFilePath);
            }
            else
            {
                string yamlConfig = File.ReadAllText(configFilePath);
                var deserializer = new DeserializerBuilder().Build();
                _config = deserializer.Deserialize<PluginConfig>(yamlConfig) ?? new PluginConfig();
            }
        }
        private void SaveConfig(PluginConfig config, string filePath)
        {
            var stringBuilder = new StringBuilder();

            stringBuilder.AppendLine("# Configuration file for Check Cheats :X");

            stringBuilder.AppendLine("# Command format for banning players (css_ban {0}(time) {1})");
            AppendConfigValue(stringBuilder, nameof(config.BanCommand), config.BanCommand);

            stringBuilder.AppendLine("# Ban reason for Denial/Disconnect");
            AppendConfigValue(stringBuilder, nameof(config.BanReason), config.BanReason);

            stringBuilder.AppendLine("# Duration of player verification in seconds");
            AppendConfigValue(stringBuilder, nameof(config.CheckDuration), config.CheckDuration);

            stringBuilder.AppendLine("# Countdown message displayed for player on check");
            AppendConfigValue(stringBuilder, nameof(config.CountdownMessageFormat), config.CountdownMessageFormat);

            stringBuilder.AppendLine("# Message displayed and succesful check");
            AppendConfigValue(stringBuilder, nameof(config.SuccessMessage), config.SuccessMessage);

            stringBuilder.AppendLine("# Discord logging via webhook");
            AppendConfigValue(stringBuilder, nameof(config.EnableDiscordLogging), config.EnableDiscordLogging);

            stringBuilder.AppendLine("# Discord Webhook URL for logs");
            AppendConfigValue(stringBuilder, nameof(config.DiscordWebhookUrl), config.DiscordWebhookUrl);


            File.WriteAllText(filePath, stringBuilder.ToString());
        }
        private void AppendConfigValue(StringBuilder stringBuilder, string key, object value)
        {
            var valueStr = value?.ToString() ?? string.Empty;
            stringBuilder.AppendLine($"{key}: \"{EscapeMessage(valueStr)}\"");
        }
        private async Task SendDiscordWebhookNotification(string webhookUrl, string playerName, string adminName, string playerSteamId64, string adminSteamId64)
        {
            var embed = new
            {
                title = $"Admin {adminName} summoned {playerName} to PC Check",
                color = 16777215,
                fields = new[]
                {
                    new
                    {
                        name = "Player",
                        value = $"[{playerName}]({await GetSteamProfileLinkAsync(playerSteamId64)})",
                        inline = true
                    },
                    new
                    {
                        name = "Admin",
                        value = $"[{adminName}]({await GetSteamProfileLinkAsync(adminSteamId64)})",
                        inline = true
                    }
                }
            };
            var payload = new { embeds = new[] { embed } };
            var jsonPayload = JsonConvert.SerializeObject(payload);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            await _httpClient.PostAsync(webhookUrl, content);
        }
        static async Task<string> GetSteamProfileLinkAsync(string userId)
        {
            return await Task.FromResult($"https://steamcommunity.com/profiles/{userId}");
        }


        private string EscapeMessage(string message)
        {
            return message.Replace("\"", "\\\"");
        }
        public override void Load(bool hotReload)
        {
            AddCommand("css_check", "Call to check", AdminCheckCommand);
            AddCommand("css_uncheck", "Stop Pc Check", AdminUncheckCommand);
            AddCommand("css_checkreload", "Reload config config.yml", ReloadConfigCommand);
            RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
            RegisterListener<Listeners.OnTick>(() =>
            {
                foreach (var kvp in playerCenterMessages)
                {
                    var player = kvp.Key;
                    var (message, _) = kvp.Value;
                    if (player != null && player.IsValid)
                    {
                        player.PrintToCenterHtml(message);
                    }
                }
            });
            LoadConfig();
        }

        public IT3MenuManager? MenuManager;

        // get the instance
        public IT3MenuManager? GetMenuManager()
        {
            if (MenuManager == null)
                MenuManager = new PluginCapability<IT3MenuManager>("t3menu:manager").Get();

            return MenuManager;
        }

        [RequiresPermissions("@admin/uncheck")]
        private void AdminUncheckCommand(CCSPlayerController? caller, CommandInfo info)
        {
            if (caller == null) return;
            var manager = GetMenuManager();
            if (manager == null)
                return;

            var mainMenu = manager.CreateMenu("Un-Check Players", isSubMenu: false);

            foreach (var player in Utilities.GetPlayers())
            {
                string playerName = player.PlayerName;
                mainMenu.Add(playerName, (admin, option) => UncheckPlayer(player));
                manager.CloseMenu(caller);
            }
            manager.OpenMainMenu(caller, mainMenu);
        }
        private void UncheckPlayer(CCSPlayerController playerToUncheck)
        {
            if (playerMessageTimers.ContainsKey(playerToUncheck))
            {
                var timer = playerMessageTimers[playerToUncheck];
                timer.Kill();
                playerMessageTimers.Remove(playerToUncheck);
            }

            isCheckActive[playerToUncheck] = false;

            string successMessage = _config.SuccessMessage;
            playerCenterMessages[playerToUncheck] = (successMessage, true);

            var messageRemovalTimer = AddTimer(3, () =>
            {
                playerCenterMessages.Remove(playerToUncheck);
            });

            playerMessageTimers[playerToUncheck] = messageRemovalTimer;
        }
        [RequiresPermissions("@admin/check")]
        private void AdminCheckCommand(CCSPlayerController? caller, CommandInfo info)
        {
            if (caller == null) return;
            var manager = GetMenuManager();
            if (manager == null)
                return;

            var mainMenu = manager.CreateMenu("Select player for check");
            foreach (var player in Utilities.GetPlayers())
            {
                string playerName = player.PlayerName;
                mainMenu.Add(playerName, async (admin, option) => await CheckPlayer(player, admin));
            }
            manager.OpenMainMenu(caller, mainMenu);
       
        }
        private HookResult OnPlayerDisconnect(EventPlayerDisconnect disconnectEvent, GameEventInfo info)
        {
            if (disconnectEvent?.Userid != null && !disconnectEvent.Userid.IsBot)
            {
                if (isCheckActive.TryGetValue(disconnectEvent.Userid, out var isActive) && isActive)
                {
                    BanPlayer(disconnectEvent.Userid);
                }

                isCheckActive.Remove(disconnectEvent.Userid);
            }

            return HookResult.Continue;
        }

        private async Task CheckPlayer(CCSPlayerController playerToCheck, CCSPlayerController admin)
        {
            playerToCheck.ChangeTeam(CsTeam.Spectator);
            adminInitiatingCheck[playerToCheck] = admin;
            int totalTime = _config.CheckDuration;
            ShowCenterMessageWithCountdown(playerToCheck, totalTime);

            var timer = AddTimer(totalTime, () =>
            {
                if (playerToCheck == null || !playerToCheck.IsValid)
                {
                    Console.WriteLine("Player is not valid or null. Exiting timer callback.");
                    return;
                }

                BanPlayer(playerToCheck);
                playerCenterMessages.Remove(playerToCheck);
                playerMessageTimers.Remove(playerToCheck);
            });

            playerMessageTimers[playerToCheck] = timer;
            if (_config.EnableDiscordLogging && !string.IsNullOrEmpty(_config.DiscordWebhookUrl))
            {
                var playerSteamId64 = playerToCheck.AuthorizedSteamID?.SteamId64.ToString();
                var adminSteamId64 = admin.AuthorizedSteamID?.SteamId64.ToString();

                if (!string.IsNullOrEmpty(playerSteamId64) && !string.IsNullOrEmpty(adminSteamId64))
                {
                    await SendDiscordWebhookNotification(_config.DiscordWebhookUrl, playerToCheck.PlayerName,
                        admin.PlayerName, playerSteamId64, adminSteamId64);
                }
            }
        }

        private void BanPlayer(CCSPlayerController player)
        {
            string banCommand = string.Format(_config.BanCommand, player.SteamID, _config.BanReason);
            Server.ExecuteCommand(banCommand);
        }
        private void ShowCenterMessageWithCountdown(CCSPlayerController player, int totalTime)
        {
            if (playerMessageTimers.TryGetValue(player, out var existingTimer))
            {
                existingTimer.Kill();
                playerMessageTimers.Remove(player);
            }

            int remainingTime = totalTime;
            isCheckActive[player] = true;

            CSTimers.Timer? messageTimer = null;
            messageTimer = AddTimer(1, () =>
            {
                if (!isCheckActive.ContainsKey(player) || !playerMessageTimers.ContainsKey(player))
                {
                    return;
                }
                if (!isCheckActive[player] || messageTimer == null)
                {
                    if (messageTimer != null)
                    {
                        messageTimer.Kill();
                        playerMessageTimers.Remove(player);
                    }
                    playerCenterMessages[player] = (playerCenterMessages[player].message, false);
                    return;
                }

                if (remainingTime <= 0)
                {
                    playerCenterMessages[player] = (playerCenterMessages[player].message, false);
                    isCheckActive[player] = false;
                    if (messageTimer != null)
                    {
                        messageTimer.Kill();
                        playerMessageTimers.Remove(player);
                    }
                    return;
                }

                remainingTime--;
                string message = _config.CountdownMessageFormat
                                .Replace("{remainingTime}", remainingTime.ToString());

                playerCenterMessages[player] = (message, true);
            }, CSTimers.TimerFlags.REPEAT);

            playerMessageTimers[player] = messageTimer;
        }
        [CommandHelper(minArgs: 0, usage: "Reloads the configuration file Config.yml", whoCanExecute: CommandUsage.SERVER_ONLY)]
        public void ReloadConfigCommand(CCSPlayerController? player, CommandInfo command)
        {
            if (player == null)
            {
                try
                {
                    LoadConfig();
                    Console.WriteLine("[CheckCheatsPlugin] Configuration successfully reloaded.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CheckCheatsPlugin] Error reloading the configuration: {ex.Message}");
                }
            }
            else
            {
                player.PrintToChat("This command is only available from the server console.");
            }
        }

        public class PluginConfig
        {
            public string BanCommand { get; set; } = "css_ban {0} 0 {1}";
            public string BanReason { get; set; } = "PC Check Denial";
            public int CheckDuration { get; set; } = 120;
            public string CountdownMessageFormat { get; set; } = "<font color='red' class='fontSize-l'>You're called to PC CHECK. Join discord: https://discord.gg/invite [Waiting Room] . Time left: {remainingTime} seconds.</font>";
            public string SuccessMessage { get; set; } = "<font color='green' class='fontSize-l'>You passed the PC CHECK!</font>";
            public bool EnableDiscordLogging { get; set; } = true;
            public string DiscordWebhookUrl { get; set; } = "";
        }
    }
}
