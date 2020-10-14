using Microsoft.Build.Logging.StructuredLogger;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PauloMorgado.DotnetMSBuildLog.Writers.Speedscope
{
    /// <summary>
    /// Exports provided StackSource to a https://www.speedscope.app/ format.
    /// schema: https://www.speedscope.app/file-format-schema.json
    /// </summary>
    internal static class SpeedscopeMSBuildLogWriter
    {
        private static readonly JsonWriterOptions jsonWriterOptions = new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
#if DEBUG
            Indented = true,
#else
            Indented = false,
#endif
        };

        public static void WriteTo(Build build, string filePath, bool includeAllTasks)
        {
            var frames = new List<string>();
            var buildNodes = new Dictionary<int, LinkedList<SpeedscopeEvent>>();

            PopulateSpeedscopeData(build, 0u, build.StartTime.Ticks, frames, buildNodes);

            using var fileStrem = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var jsonWriter = new Utf8JsonWriter(fileStrem, jsonWriterOptions);

            Export(Path.GetFileNameWithoutExtension(filePath), jsonWriter, build.EndTime.Ticks - build.StartTime.Ticks, frames, buildNodes);

            Dump(filePath, build.StartTime.Ticks, build);
        }

        private static void PopulateSpeedscopeData(TimedNode node, uint level, long baseTime, List<string> frames, Dictionary<int, LinkedList<SpeedscopeEvent>> buildNodes)
        {
            var startTimestamp = node.StartTime.Ticks - baseTime;
            var endTimestamp = node.EndTime.Ticks - baseTime;
            var duration = endTimestamp - startTimestamp;

            if (startTimestamp >= 0 && duration > 0) // Zero duration has no value for performance analyses and messes up the timeline
            {
                var frame = frames.Count;
#if DEBUG
                node.Index = frame;
#endif

                frames.Add(GetFrameName(node));

                if (!buildNodes.TryGetValue(node.NodeId, out var events))
                {
                    events = new LinkedList<SpeedscopeEvent>();
                    buildNodes.Add(node.NodeId, events);
                }

                insertEvent(events, new SpeedscopeEvent(frame, true, startTimestamp, duration, level));
                insertEvent(events, new SpeedscopeEvent(frame, false, endTimestamp, duration, level));
            }
#if DEBUG
            else
            {
                node.Index = -1;
            }
#endif

            if (node.HasChildren)
            {
                var childLevel = level + (1u << 16) & 0xFFFFu << 16;

                foreach (var childNode in node.Children)
                {
                    if (childNode is TimedNode timedChildNode)
                    {
                        PopulateSpeedscopeData(timedChildNode, ++childLevel, baseTime, frames, buildNodes);
                    }
                }
            }

            static void insertEvent(LinkedList<SpeedscopeEvent> events, SpeedscopeEvent @event)
            {
                var node = events.Last;

                while (node is not null && node.Value.CompareTo(@event) > 0)
                {
                    node = node.Previous;
                }

                if (node is null)
                {
                    events.AddLast(@event);
                }
                else
                {
                    events.AddAfter(node, @event);
                }
            }
        }

        [Conditional("DEBUG")]
        private static void Dump(string filePath, long baseTime, TimedNode node)
        {
            using var fileStrem = new FileStream(filePath.Replace(".speedscope.json", ".msbuild.json"), FileMode.Create, FileAccess.Write, FileShare.Read);
            using var jsonWriter = new Utf8JsonWriter(fileStrem, jsonWriterOptions);

            dump(jsonWriter, baseTime, node);

            static void dump(Utf8JsonWriter jsonWriter, long baseTime, TimedNode node)
            {
                jsonWriter.WriteStartObject();

                jsonWriter.WriteString("typeName", node.TypeName);
                jsonWriter.WriteString("description", node.ToString());
                jsonWriter.WriteString("name", node.Name);
                jsonWriter.WriteNumber("id", node.Id);
                jsonWriter.WriteNumber("frame", node.Index);
                jsonWriter.WriteNumber("nodeId", node.NodeId);
                jsonWriter.WriteString("startTime", node.StartTime.ToString("HH:mm:ss.fffffff"));
                jsonWriter.WriteString("endTime", node.EndTime.ToString("HH:mm:ss.fffffff"));
                jsonWriter.WriteString("duration", node.Duration.ToString("G"));
                jsonWriter.WriteNumber("startTimestamp", (node.StartTime.Ticks - baseTime) / (double)TimeSpan.TicksPerMillisecond);
                jsonWriter.WriteNumber("endTimestamp", (node.EndTime.Ticks - baseTime) / (double)TimeSpan.TicksPerMillisecond);
                jsonWriter.WriteNumber("durationTime", node.Duration.Ticks / (double)TimeSpan.TicksPerMillisecond);
                jsonWriter.WriteString("durationText", node.DurationText);

                if (node.HasChildren)
                {
                    jsonWriter.WriteStartArray("children");

                    foreach (var childNode in node.Children)
                    {
                        if (childNode is TimedNode timedChildNode)
                        {
                            dump(jsonWriter, baseTime, timedChildNode);
                        }
                    }

                    jsonWriter.WriteEndArray();
                }

                jsonWriter.WriteEndObject();
            }
        }

        static string GetFrameName(BaseNode node)
#if DEBUG
            => node switch
            {
                Project project => $"#{(node as TimedNode)?.Index} {project.TypeName} \"{Path.GetFileName(project.ProjectFile)}\"({string.Join(',', project.EntryTargets)} target(s))@\"{project.ProjectFile}\"",
                Task task => $"#{(node as TimedNode)?.Index} {task.TypeName} {task.Name}@[{task.FromAssembly}]",
                Target target => $"#{(node as TimedNode)?.Index} {target.TypeName} {target.Name}@\"{target.SourceFilePath}\"",
                _ => $"#{(node as TimedNode)?.Index} {node}"
            };
#else
            => node switch
            {
                Project project => $"{project.TypeName} \"{Path.GetFileName(project.ProjectFile)}\"({string.Join(',', project.EntryTargets)} target(s))@\"{project.ProjectFile}\"",
                Task task => $"{task.TypeName} {task.Name}@[{task.FromAssembly}]",
                Target target => $"{target.TypeName} {target.Name}@\"{target.SourceFilePath}\"",
                _ => node.ToString()
            };
#endif

        private static void Export(string name, Utf8JsonWriter jsonWriter, long duration, List<string> frames, Dictionary<int, LinkedList<SpeedscopeEvent>> buildNodes)
        {
            jsonWriter.WriteStartObject();

            ExportHeader(name, jsonWriter);

            ExportShared(jsonWriter, frames);

            ExportProfiles(jsonWriter, duration, buildNodes);

            jsonWriter.WriteEndObject();
        }

        private static void ExportShared(Utf8JsonWriter jsonWriter, List<string> frames)
        {
            jsonWriter.WriteStartObject("shared");

            ExportSharedFrames(jsonWriter, frames);

            jsonWriter.WriteEndObject();
        }

        private static void ExportSharedFrames(Utf8JsonWriter jsonWriter, List<string> frames)
        {
            jsonWriter.WriteStartArray("frames");

            foreach (var frame in frames)
            {
                ExportSharedFrame(jsonWriter, frame!);
            }

            jsonWriter.WriteEndArray();
        }

        private static void ExportSharedFrame(Utf8JsonWriter jsonWriter, string frame)
        {
            jsonWriter.WriteStartObject();
            jsonWriter.WriteString("name", frame);
            jsonWriter.WriteEndObject();
        }

        private static void ExportProfiles(Utf8JsonWriter jsonWriter, long duration, Dictionary<int, LinkedList<SpeedscopeEvent>> buildNodes)
        {
            jsonWriter.WriteStartArray("profiles");

            foreach (var (buildNodeId, events) in buildNodes.OrderBy(bn => bn.Key))
            {
                ExportProfile(jsonWriter, duration, buildNodeId, events);
            }

            jsonWriter.WriteEndArray();
        }

        private static void ExportProfile(Utf8JsonWriter jsonWriter, long duration, int buildNodeId, LinkedList<SpeedscopeEvent> events)
        {
            jsonWriter.WriteStartObject();

            jsonWriter.WriteString("type", "evented");
            jsonWriter.WriteString("name", $"Node #{buildNodeId}");
            jsonWriter.WriteString("unit", "milliseconds");
            jsonWriter.WriteNumber("startValue", 0.0);
            jsonWriter.WriteNumber("endValue", duration / (double)TimeSpan.TicksPerMillisecond);

            jsonWriter.WriteStartArray("events");

            foreach (var @event in events)
            {
                ExportProfileBuildNodeEvent(jsonWriter, @event);
            }

            jsonWriter.WriteEndArray();

            jsonWriter.WriteEndObject();
        }

        private static void ExportProfileBuildNodeEvent(Utf8JsonWriter jsonWriter, SpeedscopeEvent @event)
        {
            jsonWriter.WriteStartObject();

            jsonWriter.WriteString("type", @event.IsStart ? "O" : "C");
            jsonWriter.WriteNumber("frame", @event.Frame);
            jsonWriter.WriteNumber("at", @event.At / (double)TimeSpan.TicksPerMillisecond);

            jsonWriter.WriteEndObject();
        }

        private static void ExportHeader(string name, Utf8JsonWriter jsonWriter)
        {
            jsonWriter.WriteString("exporter", GetExporterInfo());
            jsonWriter.WriteString("name", name);
            jsonWriter.WriteNumber("activeProfileIndex", 0);
            jsonWriter.WriteString("$schema", "https://www.speedscope.app/file-format-schema.json");
        }

        internal static string GetExporterInfo()
        {
            var writer = typeof(SpeedscopeMSBuildLogWriter).GetTypeInfo().Assembly.GetName();

            return $"{writer.Name}@{writer.Version}";
        }

        private class SpeedscopeEvent : IComparable<SpeedscopeEvent>
        {
            public readonly bool IsStart;
            public readonly int Frame;
            public readonly long At;
            public readonly long Duration;
            public readonly uint Level;

            public SpeedscopeEvent(int frame, bool isStart, long at, long duration, uint level)
            {
                this.Frame = frame;
                this.IsStart = isStart;
                this.At = at;
                this.Duration = duration;
                this.Level = level;
            }

            public int CompareTo(SpeedscopeEvent? other)
            {
                if (other is null)
                {
                    return 1;
                }

                var compareAt = this.At.CompareTo(other.At);

                if (compareAt == 0)
                {
                    var compareDuration = this.Duration.CompareTo(other.Duration);

                    var compareLevel = this.Level.CompareTo(other.Level);

                    if (this.IsStart && other.IsStart)
                    {
                        return compareDuration == 0 ? compareLevel : -compareDuration;
                    }
                    else if (!(this.IsStart || other.IsStart))
                    {
                        return compareDuration == 0 ? -compareLevel : compareDuration;
                    }
                    else
                    {
                        var result = this.IsStart ? 1 : -1;

                        if (this.Frame == other.Frame)
                        {
                            result = -result;
                        }

                        return result;
                    }
                }

                return compareAt;
            }
        }
    }
}
