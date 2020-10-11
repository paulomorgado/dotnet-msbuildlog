using System;
using System.Collections.Immutable;
using System.CommandLine;
using System.CommandLine.IO;
using System.IO;
using Microsoft.Build.Logging.StructuredLogger;
using PauloMorgado.DotnetMSBuildLog.Writers;

namespace PauloMorgado.DotnetMSBuildLog.Converters
{
    internal enum MSBuildLogFileFormat { MSBuildBinaryLog, Speedscope };

    internal static class MSBuildLogFileFormatConverter
    {
        private static ImmutableDictionary<MSBuildLogFileFormat, string> TraceFileFormatExtensions = GetTraceFileFormatExtensions();

        private static ImmutableDictionary<MSBuildLogFileFormat, string> GetTraceFileFormatExtensions()
        {
            var builder = ImmutableDictionary.CreateBuilder<MSBuildLogFileFormat, string>();
            builder.Add(MSBuildLogFileFormat.MSBuildBinaryLog, "binlog");
            builder.Add(MSBuildLogFileFormat.Speedscope, "speedscope.json");
            return builder.ToImmutable();
        }

        internal static void ConvertToFormat(IConsole console, MSBuildLogFileFormat format, string fileToConvertFilePath, string outputFilePath)
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
                case MSBuildLogFileFormat.Speedscope:
                    Convert(format, fileToConvertFilePath, outputFilePath);
                    break;
                default:
                    // Validation happened way before this, so we shoud never reach this...
                    throw new ArgumentException($"Invalid TraceFileFormat \"{format}\"");
            }

            console.Out.WriteLine("Conversion complete");
        }

        private static void Convert(MSBuildLogFileFormat format, string fileToConvertFilePath, string outputFilePath)
        {
            var build = Serialization.Read(fileToConvertFilePath);

            switch (format)
            {
                case MSBuildLogFileFormat.Speedscope:
                    SpeedscopeMSBuildLogWriter.WriteTo(build, outputFilePath);
                    break;
                default:
                    // we should never get here
                    throw new ArgumentException($"Invalid MSBuildLogFileFormat: {format}");
            }
        }
    }
}
