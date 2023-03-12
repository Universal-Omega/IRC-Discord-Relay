using System;
using System.Threading.Tasks;

namespace IrcDiscordRelay
{
    internal class Program
    {
        private static async Task Main()
        {
            // Create a new instance of the IrcDiscordRelay class
            IrcDiscordRelay ircDiscordRelay = new();

            // Start the relay
            await ircDiscordRelay.Start();

            // Wait for the user to quit
            Console.WriteLine("Press any key to stop...");
            _ = Console.ReadKey();

            // Stop the relay
            await ircDiscordRelay.Stop();
        }
    }
}
