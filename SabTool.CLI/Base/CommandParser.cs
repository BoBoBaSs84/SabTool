﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace SabTool.CLI.Base
{
    using Utils.Extensions;

    public static class CommandParser
    {
        const string CommandNamespace = "SabTool.CLI.Commands";

        private static readonly Dictionary<string, ICommand> _commands = new Dictionary<string, ICommand>();

        static CommandParser()
        {
            static bool filter(Type a) => a.Namespace.StartsWith(CommandNamespace) && a.IsClass && !a.IsNested && a.GetInterfaces().Contains(typeof(ICommand));

            foreach (var type in Assembly.GetExecutingAssembly().GetTypes().Where(filter))
            {
                var newInstance = Activator.CreateInstance(type) as ICommand;

                _commands.Add(newInstance.Key, newInstance);

                newInstance.Setup();
            }
        }

        private static IEnumerable<string> SplitCommandLine(string commandLine)
        {
            bool inQuotes = false;
            bool splitter(char c)
            {
                if (c == '\"')
                    inQuotes = !inQuotes;

                return !inQuotes && c == ' ';
            }

            return commandLine.Split(splitter).Select(arg => arg.Trim().TrimMatchingQuotes('\"')).Where(arg => !string.IsNullOrEmpty(arg));
        }

        public static bool ExecuteCommand(string command)
        {
            return ExecuteCommand(SplitCommandLine(command));
        }

        public static bool ExecuteCommand(IEnumerable<string> commandParts)
        {
            Console.WriteLine();

            var commandKey = commandParts.FirstOrDefault();
            if (!_commands.ContainsKey(commandKey))
            {
                Console.WriteLine("ERROR: Unknown command!");
                return false;
            }

            var res = false;

            try
            {
                res = _commands[commandKey].Execute(commandParts.Skip(1));
            }
            catch (Exception e)
            {
                Console.WriteLine("Exception occured while running the command! Exception: {0}", e);
            }
            
            if (!res)
            {
                Console.WriteLine();
                Console.WriteLine("Command could not be ran! Usage:");

                var sb = new StringBuilder();
                _commands[commandKey].BuildUsage(sb, commandParts.Skip(1));

                Console.WriteLine(sb.ToString().Trim());
            }

            return res;
        }
    }
}
