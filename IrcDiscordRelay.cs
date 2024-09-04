using Discord;
using Discord.Net.Rest;
using Discord.Net.WebSockets;
using Discord.WebSocket;
using IniParser;
using IniParser.Model;
using Meebey.SmartIrc4net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace IrcDiscordRelay
{
    internal class IrcDiscordRelay
    {
        private const int MAX_MESSAGE_LENGTH = 400;

        private readonly string ircServer;
        private readonly int ircPort;
        private readonly string ircNickname;
        private readonly string ircRealname;
        private readonly string ircUsername;
        private readonly string ircPassword;
        private readonly bool ircUseSSL;
        private readonly string discordBotToken;
        private readonly string discordProxy;
        private readonly bool discordIncludeEdited;

        private readonly Dictionary<ulong, string> discordToIrcChannelMap;
        private readonly Dictionary<string, ulong> ircToDiscordChannelMap;
        private readonly Dictionary<string, IMessageChannel> discordChannelsMap;

        private HashSet<string> ircIgnoredUsers;
        private HashSet<Regex> ircIgnoredUsersRegex;
        private HashSet<string> discordIgnoredUsers;
        private HashSet<Regex> discordIgnoredUsersRegex;

        private readonly IrcClient ircClient;
        private readonly DiscordSocketClient discordClient;

        public IrcDiscordRelay()
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
            discordProxy = data["Discord"]["Proxy"];
            discordIncludeEdited = bool.Parse(data["Discord"]["IncludeEdited"] ?? "false");

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
                Encoding = Encoding.UTF8,
                AutoReconnect = true,
                AutoRejoin = true,

                SendDelay = 300,

                UseSsl = ircUseSSL
            };
            ircClient.OnConnected += IrcClient_OnConnected;
            ircClient.OnDisconnected += IrcClient_OnDisconnected;
            ircClient.OnChannelMessage += IrcClient_OnChannelMessage;
            ircClient.OnChannelNotice += IrcClient_OnChannelNotice;
            ircClient.OnChannelAction += IrcClient_OnChannelAction;
            ircClient.OnError += IrcClient_OnError;

            if (discordProxy != null)
            {
                HttpClient.DefaultProxy = new WebProxy(discordProxy);
            }

            // Create the Discord client
            discordClient = new DiscordSocketClient(
                new DiscordSocketConfig
                {
                    FormatUsersInBidirectionalUnicode = false,
                    GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent,
                    RestClientProvider = DefaultRestClientProvider.Create(useProxy: discordProxy != null),
                    WebSocketProvider = DefaultWebSocketProvider.Create(discordProxy != null ? HttpClient.DefaultProxy : null)
                }
            );

            discordClient.MessageReceived += DiscordClient_MessageReceived;

            if (discordIncludeEdited)
            {
                discordClient.MessageUpdated += DiscordClient_MessageUpdated;
            }
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
                try
                {
                    ulong discordChannelId = entry.Key;
                    string ircChannel = entry.Value;

                    IMessageChannel message = await discordClient.Rest.GetChannelAsync(discordChannelId) as IMessageChannel;
                    discordChannelsMap[ircChannel] = message;
                }
                catch (Discord.Net.HttpException e) when (e.DiscordCode == DiscordErrorCode.MissingPermissions)
                {
                    // Ignore, bot does not have permissions to view the channel
                }
            }

            ircClient.Listen();
        }

        public async Task Stop()
        {
            // Disconnect from the IRC server and stop listening for messages
            ircClient.Disconnect();
            ircClient.OnChannelMessage -= IrcClient_OnChannelMessage;
            ircClient.OnChannelNotice -= IrcClient_OnChannelNotice;
            ircClient.OnChannelAction -= IrcClient_OnChannelAction;
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
                    ircIgnoredUsers = new HashSet<string>(ircIgnoreUsers.Value.Split(',').Select(val => val.Trim()));
                    ircIgnoredUsers = new HashSet<string>(ircIgnoredUsers.Where(val => !val.StartsWith("/") || !val.EndsWith("/") ||
                        !Regex.IsMatch("", val.Trim('/'), RegexOptions.IgnoreCase)));
                    ircIgnoredUsersRegex = new HashSet<Regex>(ircIgnoredUsers.Where(val => val.StartsWith("/") && val.EndsWith("/"))
                        .Select(val => new Regex(val.Trim('/'), RegexOptions.IgnoreCase)));
                }

                KeyData discordIgnoreUsers = ignoreUsersSection.Keys.FirstOrDefault(k => k.KeyName == "Discord");
                if (discordIgnoreUsers != null)
                {
                    discordIgnoredUsers = new HashSet<string>(discordIgnoreUsers.Value.Split(',').Select(val => val.Trim()));
                    discordIgnoredUsers = new HashSet<string>(discordIgnoredUsers.Where(val => !val.StartsWith("/") || !val.EndsWith("/") ||
                        !Regex.IsMatch("", val.Trim('/'), RegexOptions.IgnoreCase)));
                    discordIgnoredUsersRegex = new HashSet<Regex>(discordIgnoredUsers.Where(val => val.StartsWith("/") && val.EndsWith("/"))
                        .Select(val => new Regex(val.Trim('/'), RegexOptions.IgnoreCase)));
                }
            }
        }

        private async Task DiscordClient_MessageReceived(SocketMessage message)
        {
            // Ignore messages from the bot itself and ignored users
            if (
                message.Author.Id == discordClient.CurrentUser.Id ||
                discordIgnoredUsers.Contains(message.Author.Username) ||
                discordIgnoredUsersRegex.Any(r => r.IsMatch(message.Author.Username))
            )
            {
                return;
            }

            // Get the IRC channel for the Discord channel, if available
            if (discordToIrcChannelMap.TryGetValue(message.Channel.Id, out string ircChannel))
            {
                // Determine if this message is a reply to another message
                string author = $"<{message.Author}>";
                if (message.Reference != null)
                {
                    IMessage repliedToMessage = await message.Channel.GetMessageAsync(message.Reference.MessageId.Value);

                    // If replying to the bot, return the actual user that the reply is for
                    if (repliedToMessage.Author.Id == discordClient.CurrentUser.Id)
                    {
                        string[] parts = repliedToMessage.Content.Split(' ');
                        if (parts.Length > 1 && parts[0].StartsWith("<") && parts[0].EndsWith(">"))
                        {
                            author = $"<{message.Author}, replying to {parts[0][1..^1]}>";
                        }
                    }
                    else
                    {
                        author = $"<{message.Author}, replying to {repliedToMessage.Author}>";
                    }
                }

                await SendMessageToIrcChannel(ircChannel, DiscordToIrcConverter.Convert(message), author);
            }
        }

        private async Task DiscordClient_MessageUpdated(Cacheable<IMessage, ulong> cacheable, SocketMessage message, ISocketMessageChannel channel)
        {
            // Ignore messages from the bot itself and ignored users
            if (
                message.Author.Id == discordClient.CurrentUser.Id ||
                discordIgnoredUsers.Contains(message.Author.Username) ||
                discordIgnoredUsersRegex.Any(r => r.IsMatch(message.Author.Username))
            )
            {
                return;
            }

            // Get the IRC channel for the Discord channel, if available
            if (discordToIrcChannelMap.TryGetValue(message.Channel.Id, out string ircChannel))
            {
                // Determine if this message is a reply to another message
                string author = $"<{message.Author}>";
                if (message.Reference != null)
                {
                    IMessage repliedToMessage = await message.Channel.GetMessageAsync(message.Reference.MessageId.Value);

                    // If replying to the bot, return the actual user that the reply is for
                    if (repliedToMessage.Author.Id == discordClient.CurrentUser.Id)
                    {
                        string[] parts = repliedToMessage.Content.Split(' ');
                        if (parts.Length > 1 && parts[0].StartsWith("<") && parts[0].EndsWith(">"))
                        {
                            author = $"<{message.Author}, replying to {parts[0][1..^1]}>";
                        }
                    }
                    else
                    {
                        author = $"<{message.Author}, replying to {repliedToMessage.Author}>";
                    }
                }

                author += " (edited)";

                await SendMessageToIrcChannel(ircChannel, DiscordToIrcConverter.Convert(message), author);
            }
        }

        private async Task SendMessageToIrcChannel(string ircChannel, string message, string author)
        {
            int maxMessageLength = MAX_MESSAGE_LENGTH - author.Length - 5;
            string[] chunks = SplitMessage(message, maxMessageLength);

            for (int i = 0; i < chunks.Length; i++)
            {
                string formattedLine = author;
                int messageNumber = i + 1;
                int totalMessages = chunks.Length;

                if (chunks.Length > 1)
                {
                    formattedLine += $" [{messageNumber}/{totalMessages}]";
                }

                formattedLine += $" {chunks[i]}";

                await Task.Run(() => ircClient.SendMessage(SendType.Message, ircChannel, formattedLine));
            }
        }

        private static string[] SplitMessage(string message, int chunkSize)
        {
            if (message == null || chunkSize <= 0)
            {
                return new[] { message };
            }

            List<string> parts = new();

            string[] lines = message.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            foreach (string line in lines)
            {
                if (line.Length == 0 || line.Trim().Length == 0)
                {
                    continue;
                }

                if (line.Length <= chunkSize)
                {
                    parts.Add(line);
                }
                else
                {
                    int startIndex = 0;
                    while (startIndex < line.Length)
                    {
                        int length = Math.Min(chunkSize, line.Length - startIndex);
                        string part = line.Substring(startIndex, length);
                        parts.Add(part);
                        startIndex += length;
                    }
                }
            }

            return parts.ToArray();
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
            if (
                e.Data.Nick == ircNickname ||
                ircIgnoredUsers.Contains(e.Data.Nick) ||
                ircIgnoredUsersRegex.Any(r => r.IsMatch(e.Data.Nick))
            )
            {
                return;
            }

            // Get the corresponding Discord channel for the IRC channel, if available
            if (ircToDiscordChannelMap.TryGetValue(e.Data.Channel, out ulong discordChannelId))
            {
                // Relay the IRC message to the Discord channel asynchronously
                _ = SendMessageToDiscordChannel(discordChannelId.ToString(), $"<{e.Data.Nick}> {IrcToDiscordConverter.BlocksToMarkdown(IrcToDiscordConverter.Parse(e.Data.Message))}");
            }
        }

        private void IrcClient_OnChannelNotice(object sender, IrcEventArgs e)
        {
            // Ignore messages from the bot itself and ignored users
            if (
                e.Data.Nick == ircNickname ||
                ircIgnoredUsers.Contains(e.Data.Nick) ||
                ircIgnoredUsersRegex.Any(r => r.IsMatch(e.Data.Nick))
            )
            {
                return;
            }

            // Get the corresponding Discord channel for the IRC channel, if available
            if (ircToDiscordChannelMap.TryGetValue(e.Data.Channel, out ulong discordChannelId))
            {
                // Relay the IRC notice message to the Discord channel asynchronously
                _ = SendMessageToDiscordChannel(discordChannelId.ToString(), $"<{e.Data.Nick}> NOTICE: {IrcToDiscordConverter.BlocksToMarkdown(IrcToDiscordConverter.Parse(e.Data.Message.Replace("*", "\\*")))}");
            }
        }

        private void IrcClient_OnChannelAction(object sender, ActionEventArgs e)
        {
            // Ignore messages from the bot itself and ignored users
            if (
                e.Data.Nick == ircNickname ||
                ircIgnoredUsers.Contains(e.Data.Nick) ||
                ircIgnoredUsersRegex.Any(r => r.IsMatch(e.Data.Nick))
            )
            {
                return;
            }

            // Get the corresponding Discord channel for the IRC channel, if available
            if (ircToDiscordChannelMap.TryGetValue(e.Data.Channel, out ulong discordChannelId))
            {
                // Format the action message as an italicized text with the sender's nickname
                string formattedMessage = $"_**{e.Data.Nick}** {IrcToDiscordConverter.BlocksToMarkdown(IrcToDiscordConverter.Parse(e.ActionMessage))}_";

                // Relay the action message to the Discord channel asynchronously
                _ = SendMessageToDiscordChannel(discordChannelId.ToString(), formattedMessage);
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
                List<string> mentions = message
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(s => s.StartsWith('@') || s.EndsWith(':'))
                    .ToList();

                if (!mentions.Any())
                {
                    return message;
                }

                // Get the current Discord server
                IGuild guild = (channel as IGuildChannel)?.Guild;

                // Parse mentions to Discord mentions
                foreach (string mention in mentions)
                {
                    // Convert to lower case for case-insensitive comparison
                    string mentionLower = mention.ToLowerInvariant();

                    // Replace @everyone and @here with escaped versions
                    if (mentionLower.StartsWith("@everyone") || mentionLower.StartsWith("@here"))
                    {
                        message = message.Replace(mention, $"`{mention}`");
                        continue;
                    }

                    // Remove the @, :, or , characters from the mention
                    string cleanedMention = mentionLower.Trim('@', ':', ',');

                    // Try to find the user by username
                    IReadOnlyCollection<IGuildUser> users = guild?.SearchUsersAsync(cleanedMention)?.Result;

                    // Check if there is at least one matching user
                    if (users != null && users.Count >= 1)
                    {
                        IGuildUser user = users.FirstOrDefault(u => u.Username.ToLowerInvariant() == cleanedMention);

                        // Replace the mention with a Discord mention if found, or leave it as is otherwise
                        if (user != null)
                        {
                            message = message.Replace(mention, $"<@{user.Id}>");
                        }
                    }
                }

                return message;
            });

            return message;
        }
    }
}
