using Discord;
using Discord.WebSocket;
using System.Globalization;
using System;
using System.Text.RegularExpressions;
using System.Drawing;

namespace IrcDiscordRelay
{
    internal static class DiscordToIrcConverter
    {
        private static readonly Regex BoldRegex = new(@"\*\*(.*?)\*\*");
        private static readonly Regex ItalicRegex = new(@"\*(.*?)\*");
        private static readonly Regex UnderlineRegex = new(@"__(.*?)__");
        private static readonly Regex StrikethroughRegex = new(@"~~(.*?)~~");
        private static readonly Regex SlashCommandRegex = new(@"<\/(\w+):?\d*>");

        public static string Convert(SocketMessage message)
        {
            string messageContent = message.Content;

            // Replace Discord markdown with IRC formatting codes
            messageContent = BoldRegex.Replace(messageContent, "\x02$1\x02");
            messageContent = ItalicRegex.Replace(messageContent, "\x1D$1\x1D");
            messageContent = UnderlineRegex.Replace(messageContent, "\x1F$1\x1F");
            messageContent = StrikethroughRegex.Replace(messageContent, "\x1E$1\x1E");

            // Parse mentions
            foreach (SocketUser userMention in message.MentionedUsers)
            {
                messageContent = messageContent.Replace($"<@{userMention.Id}>", $"@{userMention.Username}");
            }

            foreach (SocketRole roleMention in message.MentionedRoles)
            {
                string roleColorCode = HexToIrcColorCode(roleMention.Color.ToString());
                messageContent = messageContent.Replace($"<@&{roleMention.Id}>", $"{roleColorCode}@{roleMention.Name}\x0F");
            }

            foreach (SocketGuildChannel channelMention in message.MentionedChannels)
            {
                messageContent = messageContent.Replace($"<#{channelMention.Id}>", $"#{channelMention.Name}");
            }

            // Parse slash commands
            messageContent = SlashCommandRegex.Replace(messageContent, m =>
            {
                string tag = m.Value;
                string commandName = tag[2..^1].Split(':')[0];
                return $"/{commandName}";
            });

            return messageContent;
        }

        private static string HexToIrcColorCode(string hexColor)
        {
            // Remove any "#" symbol from the hex code
            hexColor = hexColor.Replace("#", "");

            // Split the hex code into its RGB components
            int r = int.Parse(hexColor.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
            int g = int.Parse(hexColor.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
            int b = int.Parse(hexColor.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);

            // Calculate the IRC color code
            int ircColor = (r / 0x11) * 36 + (g / 0x11) * 6 + (b / 0x11);

            // Return the IRC color code as a string
            return $"\u0003{ircColor.ToString("D2")}";
        }



        private static string HexToIrcColorCode2(string hexColor)
        {
            // Strip any leading "#" characters
            // Remove leading '#' if it exists
            if (hexColor.StartsWith("#"))
            {
                hexColor = hexColor.Substring(1);
            }

            // Parse the RGB values
            if (int.TryParse(hexColor, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            {
                // Split into separate R, G, B components
                int r = (rgb >> 16) & 0xFF;
                int g = (rgb >> 8) & 0xFF;
                int b = rgb & 0xFF;

                // Calculate closest IRC color codes for each component
                int ircR = r / 51;
                int ircG = g / 51;
                int ircB = b / 51;

                // Format the IRC color code
                return $"\x03{ircR.ToString("D2")}{ircG.ToString("D2")}{ircB.ToString("D2")}";
            }
            else
            {
                // Return default black if parsing fails
                return "\x0301";
            }
        }

    }
}