using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace IrcDiscordRelay
{
    internal static class IrcToDiscordConverter
    {
        private const char CharBold = '\x02';
        private const char CharMonospace = '\x11';
        private const char CharItalics = '\x1D';
        private const char CharStrikethrough = '\x1E';
        private const char CharUnderline = '\x1F';
        private const char CharColor = '\x03';
        private const char CharReverseColor = '\x16';
        private const char CharReset = '\x0F';

        private static readonly Regex colorRegex = new("\x03(\\d\\d?)?(?:,(\\d\\d?))?");

        public static List<Block> Parse(string text)
        {
            List<Block> result = new();
            Block prev = Block.Empty;
            int startIndex = 0;
            Dictionary<int, Color> indexToColor = GetIndexToColorMap(text);

            // Append a resetter to simplify code a bit
            text += CharReset;

            for (int i = 0; i < text.Length; i++)
            {
                Block current = prev;
                bool updated = true;
                int nextStart = -1;
                char ch = text[i];

                switch (ch)
                {
                    case CharBold:
                    case CharMonospace:
                    case CharItalics:
                    case CharStrikethrough:
                    case CharUnderline:
                        current.SetField(ch, !prev.GetField(ch));
                        break;
                    case CharColor:
                        Color color = indexToColor[i];
                        current.Foreground = color.foreground;
                        current.Background = color.background;
                        nextStart = i + color.strSize;
                        break;
                    case CharReverseColor:
                        if (prev.Foreground != -1)
                        {
                            current.Foreground = prev.Background;
                            current.Background = prev.Foreground;
                            if (current.Foreground == -1)
                            {
                                current.Foreground = 0;
                            }
                        }
                        current.Reverse = !prev.Reverse;
                        break;
                    case CharReset:
                        current = Block.Empty;
                        break;
                    default:
                        updated = false;
                        break;
                }

                if (updated)
                {
                    prev.Text = text[startIndex..i];

                    startIndex = nextStart != -1 ? nextStart : i + 1;

                    if (prev.Text.Length > 0)
                    {
                        result.Add(prev);
                    }

                    prev = current;
                }
            }

            return result;
        }

        public static string BlocksToMarkdown(List<Block> blocks)
        {
            string mdText = "";

            for (int i = 0; i < blocks.Count + 1; i++)
            {
                // Default to unstyled blocks when index out of range
                Block block = Block.Empty;
                if (i < blocks.Count)
                {
                    block = blocks[i];
                }
                Block prevBlock = Block.Empty;
                if (i > 0)
                {
                    prevBlock = blocks[i - 1];
                }

                // Consider reverse as italic, some IRC clients use that
                bool prevItalic = prevBlock.Italic || prevBlock.Reverse;
                bool italic = block.Italic || block.Reverse;

                // If foreground == background, then spoiler
                bool prevSpoiler = prevBlock.Foreground != -1 && prevBlock.Foreground == prevBlock.Background;
                bool spoiler = block.Foreground != -1 && block.Foreground == block.Background;

                // Add start markers when style turns from false to true
                if (!prevItalic && italic)
                {
                    mdText += "*";
                }
                if (!prevBlock.Bold && block.Bold)
                {
                    mdText += "**";
                }
                if (!prevBlock.Underline && block.Underline)
                {
                    mdText += "__";
                }
                if (!prevBlock.Strikethrough && block.Strikethrough)
                {
                    mdText += "~~";
                }
                if (!prevBlock.Monospace && block.Monospace)
                {
                    mdText += "`";
                }

                // NOTE: non-standard discord spoilers
                if (!prevSpoiler && spoiler)
                {
                    mdText += "||";
                }

                // Add end markers when style turns from true to false
                // (and apply in reverse order to maintain nesting)
                if (prevBlock.Monospace && !block.Monospace)
                {
                    mdText += "`";
                }
                if (prevBlock.Strikethrough && !block.Strikethrough)
                {
                    mdText += "~~";
                }
                if (prevBlock.Underline && !block.Underline)
                {
                    mdText += "__";
                }
                if (prevBlock.Bold && !block.Bold)
                {
                    mdText += "**";
                }
                if (prevItalic && !italic)
                {
                    mdText += "*";
                }

                // NOTE: non-standard discord spoilers
                if (prevSpoiler && !spoiler)
                {
                    mdText += "||";
                }

                mdText += block.Text;
            }

            return mdText;
        }

        private static Dictionary<int, Color> GetIndexToColorMap(string text)
        {
            Dictionary<int, Color> indexToColor = new();
            MatchCollection matches = colorRegex.Matches(text);

            foreach (Match match in matches.Cast<Match>())
            {
                // The index where the entire colour submatch starts/ends
                int startIndex = match.Index;
                int endIndex = startIndex + match.Length;

                Color c = new()
                {
                    foreground = -1,
                    background = -1,
                    strSize = endIndex - startIndex,
                };

                // Errors are impossible, our regex only matches numbers
                if (match.Groups[1].Success)
                {
                    c.foreground = int.Parse(match.Groups[1].Value);
                }

                if (match.Groups[2].Success)
                {
                    c.background = int.Parse(match.Groups[2].Value);
                }

                indexToColor[startIndex] = c;
            }

            return indexToColor;
        }

        private struct Color
        {
            public int foreground;
            public int background;
            public int strSize;
        }
    }

    public struct Block
    {
        private const char CharBold = '\x02';
        private const char CharMonospace = '\x11';
        private const char CharItalics = '\x1D';
        private const char CharStrikethrough = '\x1E';
        private const char CharUnderline = '\x1F';

        public static readonly Block Empty = new("");
        public string Text;
        public bool Bold;
        public bool Monospace;
        public bool Italic;
        public bool Strikethrough;
        public bool Underline;
        public bool Reverse;
        public int Foreground;
        public int Background;

        public Block(string text)
        {
            Text = text;
            Bold = false;
            Monospace = false;
            Italic = false;
            Strikethrough = false;
            Underline = false;
            Reverse = false;
            Foreground = -1;
            Background = -1;
        }

        public void SetField(char key, bool value)
        {
            switch (key)
            {
                case CharBold:
                    Bold = value;
                    break;
                case CharMonospace:
                    Monospace = value;
                    break;
                case CharItalics:
                    Italic = value;
                    break;
                case CharStrikethrough:
                    Strikethrough = value;
                    break;
                case CharUnderline:
                    Underline = value;
                    break;
            }
        }

        public bool GetField(char key)
        {
            return key switch
            {
                CharBold => Bold,
                CharMonospace => Monospace,
                CharItalics => Italic,
                CharStrikethrough => Strikethrough,
                CharUnderline => Underline,
                _ => false,
            };
        }
    }
}
