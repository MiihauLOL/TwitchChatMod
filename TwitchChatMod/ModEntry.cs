﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using GenericModConfigMenu;
using System.Numerics;
//I know my code is not the best but I try :3
//I used a lot of try an error to get to the point the code is now :3
namespace TwitchChatMod
{
    public class ModEntry : Mod, IDisposable
    {
        private TcpClient client;
        private StreamReader reader;
        private StreamWriter writer;
        private Task listeningTask;
        private bool listening;
        private string channel;
        private CancellationTokenSource cancellationTokenSource;
        private Dictionary<string, Color> assignedColors = new();
        private static readonly Random Random = new();
        private ModConfig Config;

        public override void Entry(IModHelper helper)
        {
            Config = helper.ReadConfig<ModConfig>();
            channel = Config.TwitchChannel.ToLower();
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.ReturnedToTitle += ReturnedToTitle;
            helper.Events.Display.RenderingHud += OnRenderingHud;
        }
        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(channel))
            {
                Monitor.Log("Twitch channel not set. Please configure the mod.", LogLevel.Warn);
            }
            RegisterGMCM();
        }
        private void RegisterGMCM()
        {
            var gmcmApi = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (gmcmApi == null)
                return;
            gmcmApi.Register(ModManifest, () => Config = new ModConfig(), () => Helper.WriteConfig(Config));
            gmcmApi.AddSectionTitle(
                ModManifest,
                text: () => Helper.Translation.Get("twitchchatmod.gen")
                ); ;
            gmcmApi.AddParagraph(
                ModManifest,
                text: () => Helper.Translation.Get("twitchchatmod.description"));

            gmcmApi.AddTextOption(
                ModManifest,
                name: () => Helper.Translation.Get("twitchchatmod.twitch_channel"),
                tooltip: () => Helper.Translation.Get("twitchchatmod.twitch_channel_tooltip"),
                getValue: () => Config.TwitchChannel,
                setValue: value => Config.TwitchChannel = value
            );

            gmcmApi.AddTextOption(
                ModManifest,
                name: () => Helper.Translation.Get("twitchchatmod.ignored_usernames"),
                tooltip: () => Helper.Translation.Get("twitchchatmod.ignored_usernames_tooltip"),
                getValue: () => string.Join(",", Config.IgnoredUsernames),
                setValue: value => Config.IgnoredUsernames = new List<string>(value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            );

            gmcmApi.AddNumberOption(
                ModManifest,
                name: () => Helper.Translation.Get("twitchchatmod.chat_width_scale"),
                tooltip: () => Helper.Translation.Get("twitchchatmod.chat_width_scale_tooltip"),
                getValue: () => Config.ChatWidthScale,
                setValue: value => Config.ChatWidthScale = value,
                min: 0.3f,
                max: 1.0f,
                interval: 0.05f
            );

            gmcmApi.AddNumberOption(
                ModManifest,
                name: () => Helper.Translation.Get("twitchchatmod.max_messages_displayed"),
                tooltip: () => Helper.Translation.Get("twitchchatmod.max_messages_displayed_tooltip"),
                getValue: () => Config.MaxMessages,
                setValue: value => Config.MaxMessages = value,
                min: 1,
                max: 10,
                interval: 1
            );

            gmcmApi.AddBoolOption(
                ModManifest,
                name: () => Helper.Translation.Get("twitchchatmod.show_chat_ingame"),
                tooltip: () => Helper.Translation.Get("twitchchatmod.show_chat_ingame_tooltip"),
                getValue: () => Config.ShowChatIngame,
                setValue: value => Config.ShowChatIngame = value
            );

        }
        private void OnRenderingHud(object sender, RenderingHudEventArgs e)
        {
            AdjustChatBoxWidth();
        }
        private void AdjustChatBoxWidth()
        {
            if (Game1.chatBox != null)
            {
                Game1.chatBox.chatBox.Width = (int)(896 * Config.ChatWidthScale);
                Game1.chatBox.emojiMenuIcon.bounds.X = (int)(896 * Config.ChatWidthScale);
                Game1.chatBox.emojiMenu.xPositionOnScreen = Game1.chatBox.emojiMenuIcon.bounds.Center.X - 146;
                Game1.chatBox.maxMessages = Config.MaxMessages;
            }
        }
        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            AdjustChatBoxWidth();
            channel = Config.TwitchChannel.ToLower();
            if (Context.IsWorldReady && !listening && !string.IsNullOrWhiteSpace(channel))
            {
                StartListeningToTwitchChat();
            }
        }
        private void StartListeningToTwitchChat()
        {
            listening = true;
            cancellationTokenSource = new CancellationTokenSource();
            var token = cancellationTokenSource.Token;
            listeningTask = Task.Run(async () =>
            {
                try
                {
                    Monitor.Log("Connecting to Twitch IRC...", LogLevel.Info);
                    client = new TcpClient("irc.chat.twitch.tv", 6667);
                    var stream = client.GetStream();
                    reader = new StreamReader(stream);
                    writer = new StreamWriter(stream) { AutoFlush = true };
                    await writer.WriteLineAsync("CAP REQ :twitch.tv/tags twitch.tv/commands");
                    await writer.WriteLineAsync($"NICK justinfan{new Random().Next(10000, 99999)}");
                    await writer.WriteLineAsync($"JOIN #{channel}");
                    Monitor.Log($"Joined channel: #{channel}", LogLevel.Info);
                    while (listening && !token.IsCancellationRequested && client.Connected)
                    {
                        try
                        {
                            var message = await reader.ReadLineAsync();
                            if (string.IsNullOrWhiteSpace(message) || token.IsCancellationRequested)
                                continue;
                            //Monitor.Log($"Received message: {message}", LogLevel.Debug);
                            if (message.Contains("PRIVMSG"))
                            {
                                var user = ExtractDisplayName(message) ?? ExtractUsername(message);
                                if (Config.IgnoredUsernames.Contains(user, StringComparer.OrdinalIgnoreCase))
                                {
                                    //Monitor.Log($"Ignored message from user: {user}", LogLevel.Debug);
                                    continue;
                                }
                                var chatMessage = ExtractChatMessage(message);
                                var colorHex = ExtractTwitchColor(message);
                                var color = string.IsNullOrEmpty(colorHex) ? GetOrAssignRandomColor(user) : ParseColor(colorHex);
                                ChatInfo chatInfo = new ChatInfo()
                                {
                                    message = chatMessage,
                                    user = user,
                                    color = color
                                };
                                this.Helper.Multiplayer.SendMessage(chatInfo, "ChatInfo", modIDs: new[] { "Miihau.CommandsForTwitchChatMod" });

                                if (Config.ShowChatIngame)
                                {
                                    Game1.chatBox?.addMessage($"{user}: {chatMessage}", color);
                                }
                            }
                        }
                        catch (IOException ex) when (token.IsCancellationRequested)
                        {
                            Monitor.Log("Twitch chat listener stopped due to cancellation.", LogLevel.Info);
                            break;
                        }
                        catch (Exception ex)
                        {
                            Monitor.Log($"Error in Twitch chat listener: {ex}", LogLevel.Error);
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    Monitor.Log("Twitch chat listener task canceled.", LogLevel.Info);
                }
                catch (Exception ex)
                {
                    Monitor.Log($"Error in Twitch chat listener: {ex}", LogLevel.Error);
                }
                finally
                {
                    listening = false;
                }
            }, token);
        }
        private string ExtractDisplayName(string message)
        {
            try
            {
                if (message.StartsWith("@"))
                {
                    var tags = message.Substring(1, message.IndexOf(" ") - 1).Split(';');
                    foreach (var tag in tags)
                    {
                        if (tag.StartsWith("display-name="))
                        {
                            return tag.Substring(13); 
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"Error extracting display name: {ex}", LogLevel.Warn);
            }
            return null;
        }
        private string ExtractUsername(string message)
        {
            try
            {
                var prefixStart = message.IndexOf(" :") + 2;
                var exclamationMarkIndex = message.IndexOf("!");
                if (exclamationMarkIndex > prefixStart)
                {
                    return message.Substring(prefixStart, exclamationMarkIndex - prefixStart);
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"Error extracting username: {ex}", LogLevel.Warn);
            }
            return "Unknown";
        }
        private string ExtractChatMessage(string message)
        {
            try
            {
                var privMsgIndex = message.IndexOf("PRIVMSG");
                if (privMsgIndex > -1)
                {
                    var messageStart = message.IndexOf(" :", privMsgIndex);
                    if (messageStart > -1)
                    {
                        return message.Substring(messageStart + 2).Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"Error extracting chat message: {ex}", LogLevel.Warn);
            }
            return "[Message Parsing Error]";
        }
        private string ExtractTwitchColor(string message)
        {
            if (message.StartsWith("@"))
            {
                var tags = message.Substring(1, message.IndexOf(" ") - 1).Split(';');
                foreach (var tag in tags)
                {
                    if (tag.StartsWith("color="))
                    {
                        return tag.Substring(6);
                    }
                }
            }
            return null;
        }
        private Color GetOrAssignRandomColor(string username)
        {
            if (assignedColors.TryGetValue(username, out var existingColor))
            {
                return existingColor;
            }
            var randomColor = new Color(Random.Next(256), Random.Next(256), Random.Next(256));
            assignedColors[username] = randomColor;
            if (assignedColors.Count > 100)
            {
                assignedColors.Clear();
            }
            return randomColor;
        }
        private Color ParseColor(string hex)
        {
            try
            {
                if (hex.StartsWith("#"))
                {
                    hex = hex.Substring(1);
                }
                if (hex.Length == 6)
                {
                    var r = int.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
                    var g = int.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
                    var b = int.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
                    return new Color(r, g, b);
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"Error parsing color hex: {ex}", LogLevel.Warn);
            }
            return Color.White; 
        }
        private void ReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
        {
            Monitor.Log("Closing connection to Twitch IRC...", LogLevel.Info);
            cancellationTokenSource?.Cancel();
            Dispose();
        }
        public void Dispose()
        {
            if (cancellationTokenSource != null)
            {
                cancellationTokenSource.Cancel();
                cancellationTokenSource.Dispose();
                cancellationTokenSource = null;
            }
            if (client != null)
            {
                client.Close();
                client = null;
            }
            listening = false;
        }
    }
}
