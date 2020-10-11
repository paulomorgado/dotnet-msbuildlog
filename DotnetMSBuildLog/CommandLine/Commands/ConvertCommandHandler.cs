using PauloMorgado.DotnetMSBuildLog.Converters;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.IO;
using System.IO;

namespace PauloMorgado.DotnetMSBuildLog.CommandLine.Commands
{
    internal sealed class ConvertCommandArguments
    {
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
        public IConsole Console { get; set; }
        public FileInfo InputFileName { get; set; }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
        public MSBuildLogFileFormat Format { get; set; }
        public FileInfo? OutputFileName { get; set; }
    }

    internal static class ConvertCommandHandler
    {
        private const string DefaultMSBuildLogFileName = "msbuild.binlog";

        public static int ConvertFile(ConvertCommandArguments arguments)
        {
            if ((int)arguments.Format <= 0)
            {
                arguments.Console.Error.WriteLine("--format is required.");
                return ErrorCodes.ArgumentError;
            }

            if (arguments.Format == MSBuildLogFileFormat.MSBuildBinaryLog)
            {
                arguments.Console.Error.WriteLine("Cannot convert a nettrace file to nettrace format.");
                return ErrorCodes.ArgumentError;
            }

            if (!arguments.InputFileName.Exists)
            {
                arguments.Console.Error.WriteLine($"File '{arguments.InputFileName.FullName}' does not exist.");
                return ErrorCodes.ArgumentError;
            }

            if (arguments.OutputFileName == null)
            {
                arguments.OutputFileName = arguments.InputFileName;
            }

            MSBuildLogFileFormatConverter.ConvertToFormat(arguments.Console, arguments.Format, arguments.InputFileName.FullName, arguments.OutputFileName.FullName);

            return 0;
        }

        public static Command ConvertCommand() =>
            new Command(
                name: "convert",
                description: "Converts MSBuild binary logs to alternate formats for use with alternate trace analysis tools. Can only convert from the nettrace format")
            {
                // Handler
                CommandHandler.Create<ConvertCommandArguments>(ConvertFile),

                // Arguments and Options
                InputFileArgument(),
                ConvertFormatOption(),
                OutputOption(),
            };

        private static Argument InputFileArgument() =>
            new Argument<FileInfo>(
                name: "input-filename",
                description: $"Input binary log file to be converted. Defaults to '{ConvertCommandHandler.DefaultMSBuildLogFileName}'.",
                getDefaultValue: () => new FileInfo(ConvertCommandHandler.DefaultMSBuildLogFileName))
                .ExistingOnly();

        public static Option ConvertFormatOption() =>
            new Option(
                alias: "--format",
                description: $"Sets the output format for the trace file conversion.")
            {
                Argument = new Argument<MSBuildLogFileFormat>(name: "trace-file-format")
            };

        private static Option OutputOption() =>
            new Option(
                aliases: new[] { "-o", "--output" },
                description: "Output filename. Extension of target format will be added.")
            {
                Argument = new Argument<FileInfo>(name: "output-filename")
            };
    }
}
