using Discord;
using Discord.WebSocket;
using IniParser;
using IniParser.Model;
using Meebey.SmartIrc4net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace IrcToDiscordRelay
{
    internal class IrcToDiscordRelay
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

        private HashSet<string> ircIgnoredUsers = new();
        private HashSet<string> discordIgnoredUsers = new();

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

            SectionData channelMappingSection = data.Sections.GetSectionData("ChannelMapping");
            if (channelMappingSection != null)
            {
                foreach (KeyData key in channelMappingSection.Keys)
                {
                    ulong discordChannelId = ulong.Parse(key.KeyName);
                    string ircChannel = key.Value;
                    discordToIrcChannelMap[discordChannelId] = ircChannel;
                    ircToDiscordChannelMap[ircChannel] = discordChannelId;
                }
            }

            discordChannelsMap = new Dictionary<string, IMessageChannel>();

            ParseIgnoreUsers(data);


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
            ircClient.OnChannelNotice += IrcClient_OnChannelNotice;
            ircClient.OnError += IrcClient_OnError;

            ircClient.UseSsl = ircUseSSL;

            // Create the Discord client
            discordClient = new DiscordSocketClient(
                new DiscordSocketConfig
                {
                    GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent
                }
            );

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
            SectionData channelMappingSection = data.Sections.GetSectionData("ChannelMapping");
            if (channelMappingSection != null)
            {
                foreach (KeyData channelKey in channelMappingSection.Keys)
                {
                    ircClient.RfcJoin(channelKey.Value);
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
            ircClient.OnChannelNotice -= IrcClient_OnChannelNotice;
            ircClient.OnError -= IrcClient_OnError;

            // Disconnect from the Discord bot
            await discordClient.StopAsync();
            await discordClient.LogoutAsync();
        }

        private void ParseIgnoreUsers(IniData data)
        {
            SectionData ignoreUsersSection = data.Sections.GetSectionData("IgnoreUsers");
            if (ignoreUsersSection != null)
            {
                KeyData ircIgnoreUsers = ignoreUsersSection.Keys.FirstOrDefault(k => k.KeyName == "IRC");
                if (ircIgnoreUsers != null)
                {
                    ircIgnoredUsers = new HashSet<string>(ircIgnoreUsers.Value.Split(','));
                }
                KeyData discordIgnoreUsers = ignoreUsersSection.Keys.FirstOrDefault(k => k.KeyName == "Discord");
                if (discordIgnoreUsers != null)
                {
                    discordIgnoredUsers = new HashSet<string>(discordIgnoreUsers.Value.Split(','));
                }
            }
        }

        private async Task DiscordClient_MessageReceived(SocketMessage message)
        {
            // Ignore messages from the bot itself and ignored users
            if (message.Author.Id == discordClient.CurrentUser.Id || discordIgnoredUsers.Contains(message.Author.Username))
            {
                return;
            }

            // Get the IRC channel for the Discord channel, if available
            if (discordToIrcChannelMap.TryGetValue(message.Channel.Id, out string ircChannel))
            {
                string messageContent = message.CleanContent;

                // Remove Discriminator from mentioned users
                foreach (SocketUser mention in message.MentionedUsers)
                {
                    messageContent = messageContent.Replace($"#{mention.Discriminator}", "");
                }

                if (message.Reference != null)
                {
                    // If it is a reply, send the replied-to message's content as part of the message
                    IMessage repliedToMessage = await message.Channel.GetMessageAsync(message.Reference.MessageId.Value);
                    messageContent = $"<{message.Author}, replying to {repliedToMessage.Author}> {messageContent}";
                }
                else
                {
                    messageContent = $"<{message.Author}> {messageContent}";
                }

                // Relay the Discord message to the IRC channel asynchronously
                await SendMessageToIrcChannel(ircChannel, messageContent);
            }
        }


        private async Task SendMessageToIrcChannel(string ircChannel, string message)
        {
            // Send the message to the IRC channel asynchronously
            await Task.Run(() => ircClient.SendMessage(SendType.Message, ircChannel, message));
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
            SectionData channelMappingSection = data.Sections.GetSectionData("ChannelMapping");
            if (channelMappingSection != null)
            {
                foreach (KeyData channelKey in channelMappingSection.Keys)
                {
                    ircClient.RfcJoin(channelKey.Value);
                }
            }
        }

        private void IrcClient_OnChannelMessage(object sender, IrcEventArgs e)
        {
            // Ignore messages from the bot itself and ignored users
            if (e.Data.Nick == ircNickname || ircIgnoredUsers.Contains(e.Data.Nick))
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

        private void IrcClient_OnChannelNotice(object sender, IrcEventArgs e)
        {
            // Ignore messages from the bot itself and ignored users
            if (e.Data.Nick == ircNickname || ircIgnoredUsers.Contains(e.Data.Nick))
            {
                return;
            }

            // Get the corresponding Discord channel for the IRC channel, if available
            if (ircToDiscordChannelMap.TryGetValue(e.Data.Channel, out ulong discordChannelId))
            {
                // Relay the IRC notice message to the Discord channel asynchronously
                _ = SendMessageToDiscordChannel(discordChannelId.ToString(), $"<{e.Data.Nick}> NOTICE: {e.Data.Message.Replace("*", "\\*")}");
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

            // Parse mentions
            message = await ParseIrcMentions(message, messageChannel);

            // Send the message to the Discord channel asynchronously
            _ = await messageChannel.SendMessageAsync(message);
        }

        private static async Task<string> ParseIrcMentions(string message, IMessageChannel channel)
        {
            _ = await Task.Run(() =>
            {
                List<string> mentions = message.Split(' ').Where(s => s.StartsWith('@') || s.EndsWith(':')).ToList();
                if (!mentions.Any())
                {
                    return message;
                }

                // Get the current Discord server
                IGuild guild = (channel as IGuildChannel)?.Guild;

                // Parse mentions to Discord mentions
                foreach (string mention in mentions)
                {
                    // Remove the @ or : characters from the mention
                    string cleanedMention = mention.Trim('@', ':');

                    // Try to find the user by username
                    IGuildUser user = guild?.SearchUsersAsync(cleanedMention)?.Result?.FirstOrDefault(u => u.Username == cleanedMention);

                    // Replace the mention with a Discord mention if found, or leave it as is otherwise
                    if (user != null)
                    {
                        message = message.Replace(mention, $"<@{user.Id}>");
                    }
                }

                return message;
            });

            return message;
        }
    }
}
