using Discord;
using Discord.WebSocket;
using IniParser;
using IniParser.Model;
using Meebey.SmartIrc4net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static System.Collections.Specialized.BitVector32;

namespace IrcToDiscordRelay
{
    public class IrcToDiscordRelay
    {
        private readonly string ircServer;
        private readonly int ircPort;
        private readonly string ircNickname;
        private readonly string ircRealname;
        private readonly string ircUsername;
        private readonly string ircPassword;
        private readonly bool ircUseSSL;
        private readonly string discordBotToken;

        private readonly Dictionary<ulong, string> discordToIrcChannelMap;
        private readonly Dictionary<string, ulong> ircToDiscordChannelMap;
        private readonly Dictionary<string, IMessageChannel> discordChannelsMap;

        private readonly IrcClient ircClient;
        private readonly DiscordSocketClient discordClient;

        public IrcToDiscordRelay()
        {
            FileIniDataParser parser = new();
            IniData data = parser.ReadFile("config.ini");

            ircServer = data["IRC"]["Server"];
            ircPort = int.Parse(data["IRC"]["Port"]);
            ircNickname = data["IRC"]["Nickname"];
            ircRealname = data["IRC"]["Realname"] ?? ircNickname;
            ircUsername = data["IRC"]["Username"] ?? ircNickname;
            ircPassword = data["IRC"]["Password"];
            ircUseSSL = bool.Parse(data["IRC"]["UseSSL"]);
            discordBotToken = data["Discord"]["BotToken"];

            discordToIrcChannelMap = new Dictionary<ulong, string>();
            ircToDiscordChannelMap = new Dictionary<string, ulong>();

            SectionData discordChannelMappingSection = data.Sections.GetSectionData("DiscordChannelMapping");
            if (discordChannelMappingSection != null)
            {
                foreach (KeyData key in discordChannelMappingSection.Keys)
                {
                    ulong discordChannelId = ulong.Parse(key.KeyName);
                    string ircChannel = key.Value;
                    discordToIrcChannelMap[discordChannelId] = ircChannel;
                    ircToDiscordChannelMap[ircChannel] = discordChannelId;
                }
            }

            discordChannelsMap = new Dictionary<string, IMessageChannel>();


            // Create the IRC client and register event handlers
            ircClient = new IrcClient
            {
                Encoding = System.Text.Encoding.UTF8,
                AutoReconnect = true,
                AutoRejoin = true,

                SendDelay = 200
            };
            ircClient.OnConnected += IrcClient_OnConnected;
            ircClient.OnDisconnected += IrcClient_OnDisconnected;
            ircClient.OnChannelMessage += IrcClient_OnChannelMessage;
            ircClient.OnError += IrcClient_OnError;

            ircClient.UseSsl = ircUseSSL;

            // Create the Discord client
            discordClient = new DiscordSocketClient();
            discordClient.MessageReceived += DiscordClient_MessageReceived;
        }

        public async Task Start()
        {
            // Connect to the IRC server and start listening for messages
            ircClient.Connect(ircServer, ircPort);
            ircClient.Login(ircNickname, ircRealname, 4, ircUsername, ircPassword);

            FileIniDataParser parser = new();
            IniData data = parser.ReadFile("config.ini");

            // Join all the IRC channels specified in the configuration file
            SectionData ircChannelMappingSection = data.Sections.GetSectionData("IRCChannelMapping");
            if (ircChannelMappingSection != null)
            {
                foreach (KeyData ircChannelKey in ircChannelMappingSection.Keys)
                {
                    ircClient.RfcJoin(ircChannelKey.KeyName);
                }
            }

            // Connect to the Discord bot
            await discordClient.LoginAsync(TokenType.Bot, discordBotToken);
            await discordClient.StartAsync();

            // Map each Discord channel to its corresponding IMessageChannel object
            foreach (KeyValuePair<ulong, string> entry in discordToIrcChannelMap)
            {
                ulong discordChannelId = entry.Key;
                string ircChannel = entry.Value;

                IMessageChannel message = await discordClient.Rest.GetChannelAsync(discordChannelId) as IMessageChannel;
                discordChannelsMap[ircChannel] = message;
            }

            ircClient.Listen();
        }

        public async Task Stop()
        {
            // Disconnect from the IRC server and stop listening for messages
            ircClient.Disconnect();
            ircClient.OnChannelMessage -= IrcClient_OnChannelMessage;
            ircClient.OnError -= IrcClient_OnError;

            // Disconnect from the Discord bot
            await discordClient.StopAsync();
            await discordClient.LogoutAsync();
        }

        private async Task DiscordClient_MessageReceived(SocketMessage message)
        {
            // Ignore messages from the bot itself
            if (message.Author.Id == discordClient.CurrentUser.Id)
            {
                return;
            }

            // Get the IRC channel for the Discord channel, if available
            if (discordToIrcChannelMap.TryGetValue(message.Channel.Id, out string ircChannel))
            {
                // Relay the Discord message to the IRC channel asynchronously
                await SendMessageToIrcChannel(ircChannel, $"<{message.Author.Username}#{message.Author.Discriminator}> {message.Content}");
            }
        }


        private async Task SendMessageToIrcChannel(string ircChannel, string message)
        {
            // Send the message to the IRC channel asynchronously
            ircClient.SendMessage(SendType.Message, ircChannel, message);
        }


        private void IrcClient_OnConnected(object sender, EventArgs e)
        {
            // Notify the user that the IRC client has successfully connected
            Console.WriteLine($"Connected to {ircServer}:{ircPort}.");
        }

        private void IrcClient_OnDisconnected(object sender, EventArgs e)
        {
            // Reconnect to the IRC server
            ircClient.Connect(ircServer, ircPort);
            ircClient.Login(ircNickname, ircRealname, 4, ircUsername, ircPassword);

            FileIniDataParser parser = new();
            IniData data = parser.ReadFile("config.ini");

            // Join all the IRC channels specified in the configuration file
            SectionData ircChannelMappingSection = data.Sections.GetSectionData("IRCChannelMapping");
            if (ircChannelMappingSection != null)
            {
                foreach (KeyData ircChannelKey in ircChannelMappingSection.Keys)
                {
                    ircClient.RfcJoin(ircChannelKey.KeyName);
                }
            }
        }

        private void IrcClient_OnChannelMessage(object sender, IrcEventArgs e)
        {
            // Ignore messages from the bot itself
            if (e.Data.Nick == ircNickname)
            {
                return;
            }

            // Get the corresponding Discord channel for the IRC channel, if available
            if (ircToDiscordChannelMap.TryGetValue(e.Data.Channel, out ulong discordChannelId))
            {
                // Relay the IRC message to the Discord channel asynchronously
                _ = SendMessageToDiscordChannel(discordChannelId.ToString(), $"<{e.Data.Nick}> {e.Data.Message}");
            }
        }


        private void IrcClient_OnError(object sender, ErrorEventArgs e)
        {
            // Log any IRC error messages to the console
            Console.WriteLine($"IRC Error: {e.Data.Message}");
        }

        private async Task SendMessageToDiscordChannel(string discordChannelId, string message)
        {
            // Get the Discord channel by ID
            if (!discordChannelsMap.TryGetValue(discordChannelId, out IMessageChannel messageChannel))
            {
                Discord.Rest.RestChannel channel = await discordClient.Rest.GetChannelAsync(ulong.Parse(discordChannelId));
                messageChannel = channel as IMessageChannel;
                discordChannelsMap[discordChannelId] = messageChannel;
            }

            // Send the message to the Discord channel asynchronously
            _ = await messageChannel.SendMessageAsync(message);
        }
    }
}
