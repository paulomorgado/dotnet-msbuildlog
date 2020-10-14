using Microsoft.Build.Logging.StructuredLogger;
using PauloMorgado.DotnetMSBuildLog.Binlog;
using PauloMorgado.DotnetMSBuildLog.Writers.Chromium;
using PauloMorgado.DotnetMSBuildLog.Writers.Speedscope;
using System;
using System.Collections.Immutable;
using System.CommandLine;
using System.CommandLine.IO;
using System.IO;

namespace PauloMorgado.DotnetMSBuildLog.Converters
{
    internal static class MSBuildLogFileFormatConverter
    {
        private static ImmutableDictionary<MSBuildLogFileFormat, string> TraceFileFormatExtensions = GetTraceFileFormatExtensions();

        private static ImmutableDictionary<MSBuildLogFileFormat, string> GetTraceFileFormatExtensions()
        {
            var builder = ImmutableDictionary.CreateBuilder<MSBuildLogFileFormat, string>();
            builder.Add(MSBuildLogFileFormat.MSBuildBinaryLog, "binlog");
            builder.Add(MSBuildLogFileFormat.Speedscope, "speedscope.json");
            builder.Add(MSBuildLogFileFormat.Chromium, "chromium.json");
            return builder.ToImmutable();
        }

        internal static void ConvertToFormat(IConsole console, MSBuildLogFileFormat format, string fileToConvertFilePath, string outputFilePath, bool includeAllTasks)
        {
            if (string.IsNullOrWhiteSpace(outputFilePath))
            {
                outputFilePath = fileToConvertFilePath;
            }

            outputFilePath = Path.ChangeExtension(outputFilePath, TraceFileFormatExtensions[format]);
            console.Out.WriteLine($"Writing:\t{outputFilePath}");

            switch (format)
            {
                case MSBuildLogFileFormat.MSBuildBinaryLog:
                    break;
                case MSBuildLogFileFormat.Chromium:
                case MSBuildLogFileFormat.Speedscope:
                    Convert(format, fileToConvertFilePath, outputFilePath, includeAllTasks);
                    break;
                default:
                    // Validation happened way before this, so we shoud never reach this...
                    throw new ArgumentException($"Invalid TraceFileFormat \"{format}\"");
            }

            console.Out.WriteLine("Conversion complete");
        }

        private static void Convert(MSBuildLogFileFormat format, string fileToConvertFilePath, string outputFilePath, bool includeAllTasks)
        {
            switch (format)
            {
                case MSBuildLogFileFormat.Chromium:
                    var buildEnumerator = new BinlogEnumerable(fileToConvertFilePath);
                    ChromiumMSBuildLogWriter.WriteTo(buildEnumerator, outputFilePath, includeAllTasks);
                    break;
                case MSBuildLogFileFormat.Speedscope:
                    var build = Serialization.Read(fileToConvertFilePath);
                    SpeedscopeMSBuildLogWriter.WriteTo(build, outputFilePath, includeAllTasks);
                    break;
                default:
                    // we should never get here
                    throw new ArgumentException($"Invalid MSBuildLogFileFormat: {format}");
            }
        }
    }
}
