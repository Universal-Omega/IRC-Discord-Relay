# IRC-Discord-Relay

A simple bot that relays messages between IRC and Discord.
It allows users to send messages in one platform and have them appear in the other, making it a great tool for bridging conversations between the two services.
The bot supports message splitting for long messages, as well as parsing mentions and replies.
It can be configured to join multiple IRC channels and map them to specific Discord channels, and can also ignore messages from certain users on both platforms.
It also supports converting from IRC formatting to Discord markdown and vice-versa.

### Configuration

The configuration file should be named config.ini and placed in the same directory as this repository.

The following configuration options are available:

```ini
[IRC]
Server = the IRC server to connect to
Port = the port to use for the IRC server
Nickname = the nickname to use for the IRC client
Realname = the real name to use for the IRC client (defaults to the nickname if not specified)
Username = the username to use for the IRC client (defaults to the nickname if not specified)
Password = the password to use for the IRC client (if required)
UseSSL = whether to use SSL for the IRC connection (true or false)

[Discord]
BotToken = the Discord bot token to use for the bot account
Proxy = the HTTP proxy to use for the Discord connection (if required)

[ChannelMapping]
Each key in this section should be a Discord channel ID, and the corresponding value should be the name of the IRC channel to relay messages to. For example:
123456789 = #irc-channel

[IgnoreUsers]
IRC = a comma-separated list of IRC nicknames to ignore messages from; supports regex
Discord = a comma-separated list of Discord usernames to ignore messages from; supports regex
```

Note: Any options not specified in the configuration file will use their default values.

## Credits

This project was created by [Universal Omega](https://github.com/Universal-Omega).
