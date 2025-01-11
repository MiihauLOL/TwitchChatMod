using System;
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
            // Load configuration
            Config = helper.ReadConfig<ModConfig>();
            channel = Config.TwitchChannel.ToLower();

            // Hook game events
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
                text: () => "General"
                );
            gmcmApi.AddParagraph(
                ModManifest,
                text: () => "Hover over the Variable to get a description");


            gmcmApi.AddTextOption(
                        ModManifest,
                        name: () => "Twitch Channel",
                        tooltip: () => "Enter the Twitch channel name to display chat messages.",
                        getValue: () => Config.TwitchChannel,
                        setValue: value => Config.TwitchChannel = value
                    );

            gmcmApi.AddTextOption(
                ModManifest,
                name: () => "Ignored Usernames",
                tooltip: () => "Comma-separated list of usernames to ignore.",
                getValue: () => string.Join(",", Config.IgnoredUsernames),
                setValue: value => Config.IgnoredUsernames = new List<string>(value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            );

            gmcmApi.AddNumberOption(
                ModManifest,
                name: () => "Chat Width Scale",
                tooltip: () => "Adjust the width of the in-game chat. '0.8' to '0.5' recommended, depending on your prefferd UI scale.",
                getValue: () => Config.ChatWidthScale,
                setValue: value => Config.ChatWidthScale = value,
                min: 0.3f,
                max: 1.0f,
                interval: 0.05f
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
                                var chatMessage = ExtractChatMessage(message);
                                var colorHex = ExtractTwitchColor(message);
                                var color = string.IsNullOrEmpty(colorHex) ? GetOrAssignRandomColor(user) : ParseColor(colorHex);

                                if (Config.IgnoredUsernames.Contains(user, StringComparer.OrdinalIgnoreCase))
                                {
                                    //Monitor.Log($"Ignored message from user: {user}", LogLevel.Debug);
                                    continue;
                                }

                                Game1.chatBox?.addMessage($"{user}: {chatMessage}", color);
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
