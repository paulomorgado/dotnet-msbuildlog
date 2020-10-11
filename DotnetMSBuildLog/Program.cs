using PauloMorgado.DotnetMSBuildLog.CommandLine.Commands;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;

namespace PauloMorgado.DotnetMSBuildLog
{
    static class Program
    {
        static int Main(string[] args)
        {
            var parser = new CommandLineBuilder()
                .AddCommand(ConvertCommandHandler.ConvertCommand())
                .UseDefaults()
                .Build();

            return parser.Invoke(args);
        }
    }
}
