using System;
using System.Threading.Tasks;

namespace IrcToDiscordRelay
{
    internal class Program
    {
        private static async Task Main(string[] args)
        {
            // Create a new instance of the IrcToDiscordRelay class
            IrcToDiscordRelay ircToDiscordRelay = new();

            // Start the relay
            await ircToDiscordRelay.Start();

            // Wait for the user to quit
            Console.WriteLine("Press any key to stop...");
            _ = Console.ReadKey();

            // Stop the relay
            await ircToDiscordRelay.Stop();
        }
    }
}
