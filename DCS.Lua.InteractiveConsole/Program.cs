using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using DCS.Lua.Connector;

namespace DCS.Lua.InteractiveConsole {
    static class Program {
        public static LuaConnector LuaConnector;
        static async Task Main(string[] args) {
            using (LuaConnector = new LuaConnector(IPAddress.Loopback, 5000)) {
                LuaConnector.Timeout = TimeSpan.FromSeconds(5);
                while (true) {
                    Console.Write("> ");
                    var command = Console.ReadLine();
                    try {
                        Console.WriteLine(await LuaConnector.SendReceiveCommandAsync(command));
                    } catch (TimeoutException) {
                        Console.WriteLine("Command timed out");
                    }
                }
            }
        }
    }
}