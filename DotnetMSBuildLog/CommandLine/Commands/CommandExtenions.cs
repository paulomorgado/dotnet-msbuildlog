using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PauloMorgado.DotnetMSBuildLog.CommandLine.Commands
{
    internal static class CommandExtenions
    {
        /// <summary>
        /// Allows the command handler to be included in the collection initializer.
        /// </summary>
        public static void Add(this Command command, ICommandHandler handler) => command.Handler = handler;
    }
}
