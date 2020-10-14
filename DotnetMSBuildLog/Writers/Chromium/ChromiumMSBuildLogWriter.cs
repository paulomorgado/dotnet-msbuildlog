using Microsoft.Build.Framework;
using PauloMorgado.DotnetMSBuildLog.Binlog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PauloMorgado.DotnetMSBuildLog.Writers.Chromium
{
    /// <summary>
    /// Exports provided StackSource to a Chromium Trace File format
    /// schema: https://docs.google.com/document/d/1CvAClvFfyA5R-PhYUmn5OOQtYMH4h6I0nSsKchNAySU/
    /// </summary>
    internal static class ChromiumMSBuildLogWriter
    {
        private static readonly long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1_000u;
        private static readonly JsonWriterOptions jsonWriterOptions = new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
#if DEBUG
            Indented = true,
#else
            Indented = false,
#endif
        };

        internal static void WriteTo(BinlogEnumerable buildEnumerator, string outputFilePath, bool includeAllTasks)
        {
            var firstObservedTime = DateTime.MinValue.Ticks;
            var msbuildStartEvents = new Dictionary<int, TaskStartedEventArgs>();

            using var fileStrem = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var jsonWriter = new Utf8JsonWriter(fileStrem, jsonWriterOptions);

            jsonWriter.WriteStartArray();

            foreach (var buildEventArgs in buildEnumerator)
            {
                var ts = (uint)((buildEventArgs.Timestamp.Ticks - firstObservedTime) / TicksPerMicrosecond);

                switch (buildEventArgs)
                {
                    case BuildStartedEventArgs buildStartedEventArgs:

                        firstObservedTime = buildStartedEventArgs.Timestamp.Ticks;

                        break;

                    case ProjectStartedEventArgs projectStartedEventArgs:
                        {
                            writeEvent(
                                jsonWriter: jsonWriter,
                                name: $"Project \"{projectStartedEventArgs.ProjectFile}\" ({projectStartedEventArgs.TargetNames}) ({projectStartedEventArgs.BuildEventContext.ProjectInstanceId})",
                                ts: ts,
                                ph: ChromiumTraceEventPhases.DurationBegin,
                                tid: projectStartedEventArgs.BuildEventContext.ProjectInstanceId,
                                pid: projectStartedEventArgs.BuildEventContext.NodeId.ToString());

                            if (msbuildStartEvents.TryGetValue(projectStartedEventArgs.ParentProjectBuildEventContext.ProjectInstanceId, out var callingMsbuildTaskInvocation))
                            {
                                writeEvent(
                                    jsonWriter: jsonWriter,
                                    cat: ChromiumTraceEventCategories.ProjectToProject,
                                    name: $"MSBuild \"{projectStartedEventArgs.ProjectFile}\" ({projectStartedEventArgs.TargetNames})",
                                    ph: ChromiumTraceEventPhases.FlowStart,
                                    ts: (uint)((callingMsbuildTaskInvocation.Timestamp.Ticks - firstObservedTime) / TicksPerMicrosecond) + 1,
                                    tid: projectStartedEventArgs.ParentProjectBuildEventContext.ProjectInstanceId,
                                    pid: projectStartedEventArgs.ParentProjectBuildEventContext.NodeId.ToString(),
                                    id: projectStartedEventArgs.BuildEventContext.BuildRequestId.ToString());

                                writeEvent(
                                    jsonWriter: jsonWriter,
                                    cat: ChromiumTraceEventCategories.ProjectToProject,
                                    name: $"MSBuild \"{projectStartedEventArgs.ProjectFile}\" ({projectStartedEventArgs.TargetNames})",
                                    ph: ChromiumTraceEventPhases.FlowEnd,
                                    ts: ts - 1,
                                    tid: projectStartedEventArgs.BuildEventContext.ProjectInstanceId,
                                    pid: projectStartedEventArgs.BuildEventContext.NodeId.ToString(),
                                    id: projectStartedEventArgs.BuildEventContext.BuildRequestId.ToString());
                            }
                        }

                        break;

                    case ProjectFinishedEventArgs projectFinishedEventArgs:

                        writeEvent(
                            jsonWriter: jsonWriter,
                            name: $"Project \"{projectFinishedEventArgs.ProjectFile}\" ({projectFinishedEventArgs.BuildEventContext.ProjectInstanceId})",
                            ts: ts,
                            ph: ChromiumTraceEventPhases.DurationEnd,
                            tid: projectFinishedEventArgs.BuildEventContext.ProjectInstanceId,
                            pid: projectFinishedEventArgs.BuildEventContext.NodeId.ToString());

                        break;

                    case TargetStartedEventArgs targetStartedEventArgs:

                        writeEvent(
                            jsonWriter: jsonWriter,
                            name: $"Target \"{targetStartedEventArgs.TargetName}\" in project \"{targetStartedEventArgs.ProjectFile}\" ({targetStartedEventArgs.BuildEventContext.ProjectInstanceId})",
                            ts: ts,
                            ph: ChromiumTraceEventPhases.DurationBegin,
                            tid: targetStartedEventArgs.BuildEventContext.ProjectInstanceId,
                            pid: targetStartedEventArgs.BuildEventContext.NodeId.ToString());

                        break;

                    case TargetFinishedEventArgs targetFinishedEventArgs:

                        writeEvent(
                            jsonWriter: jsonWriter,
                            name: $"Target \"{targetFinishedEventArgs.TargetName}\" in project \"{targetFinishedEventArgs.ProjectFile}\" ({targetFinishedEventArgs.BuildEventContext.ProjectInstanceId})",
                            ts: ts,
                            ph: ChromiumTraceEventPhases.DurationEnd,
                            tid: targetFinishedEventArgs.BuildEventContext.ProjectInstanceId,
                            pid: targetFinishedEventArgs.BuildEventContext.NodeId.ToString());

                        break;

                    case TaskStartedEventArgs taskStartedEventArgs:
                        {
                            string name;

                            if (taskStartedEventArgs.TaskName.EndsWith("MSBuild", StringComparison.Ordinal))
                            {
                                name = $"MSBuild (yielded) in project \"{taskStartedEventArgs.ProjectFile}\" ({taskStartedEventArgs.BuildEventContext.ProjectInstanceId})";
                            }
                            else
                            {
                                if (!includeAllTasks)
                                {
                                    break;
                                }

                                name = $"Task \"{taskStartedEventArgs.TaskName}\" in project \"{taskStartedEventArgs.ProjectFile}\" ({taskStartedEventArgs.BuildEventContext.ProjectInstanceId})";
                            }

                            msbuildStartEvents[taskStartedEventArgs.BuildEventContext.ProjectInstanceId] = taskStartedEventArgs;

                            writeEvent(
                                jsonWriter: jsonWriter,
                                name: name,
                                ts: ts,
                                ph: ChromiumTraceEventPhases.DurationBegin,
                                tid: taskStartedEventArgs.BuildEventContext.ProjectInstanceId,
                                pid: taskStartedEventArgs.BuildEventContext.NodeId.ToString());
                        }

                        break;

                    case TaskFinishedEventArgs taskFinishedEventArgs:
                        {
                            string name;

                            if (taskFinishedEventArgs.TaskName.EndsWith("MSBuild", StringComparison.Ordinal))
                            {
                                name = $"MSBuild (yielded) in project \"{taskFinishedEventArgs.ProjectFile}\" ({taskFinishedEventArgs.BuildEventContext.ProjectInstanceId})";
                            }
                            else
                            {
                                if (!includeAllTasks)
                                {
                                    break;
                                }

                                name = $"Task \"{taskFinishedEventArgs.TaskName}\" in project \"{taskFinishedEventArgs.ProjectFile}\" ({taskFinishedEventArgs.BuildEventContext.ProjectInstanceId})";
                            }

                            writeEvent(
                                    jsonWriter: jsonWriter,
                                    name: name,
                                    ts: ts,
                                    ph: ChromiumTraceEventPhases.DurationEnd,
                                    tid: taskFinishedEventArgs.BuildEventContext.ProjectInstanceId,
                                    pid: taskFinishedEventArgs.BuildEventContext.NodeId.ToString());
                        }

                        break;

                    case ProjectEvaluationStartedEventArgs projectEvaluationStartedEventArgs:

                        writeEvent(
                            jsonWriter: jsonWriter,
                            name: $"Project \"{projectEvaluationStartedEventArgs.ProjectFile}\" ({projectEvaluationStartedEventArgs.BuildEventContext.ProjectInstanceId}) evaluation",
                            ts: ts,
                            ph: ChromiumTraceEventPhases.DurationBegin,
                            tid: projectEvaluationStartedEventArgs.BuildEventContext.ProjectInstanceId,
                            pid: projectEvaluationStartedEventArgs.BuildEventContext.NodeId == BuildEventContext.InvalidNodeId ? "Evaluation" : projectEvaluationStartedEventArgs.BuildEventContext.NodeId.ToString());

                        break;

                    case ProjectEvaluationFinishedEventArgs projectEvaluationFinishedEventArgs:
                        {
                            var name = $"Project \"{projectEvaluationFinishedEventArgs.ProjectFile}\" ({projectEvaluationFinishedEventArgs.BuildEventContext.ProjectInstanceId}) evaluation";
                            var pid = projectEvaluationFinishedEventArgs.BuildEventContext.NodeId == BuildEventContext.InvalidNodeId ? "Evaluation" : projectEvaluationFinishedEventArgs.BuildEventContext.NodeId.ToString();

                            writeEvent(
                                jsonWriter: jsonWriter,
                                name: name,
                                ts: ts,
                                ph: ChromiumTraceEventPhases.DurationEnd,
                                tid: projectEvaluationFinishedEventArgs.BuildEventContext.ProjectInstanceId,
                                pid: pid
                            );

                            writeEvent(
                                jsonWriter: jsonWriter,
                                name: name,
                                ts: ts,
                                ph: ChromiumTraceEventPhases.DurationEnd,
                                tid: projectEvaluationFinishedEventArgs.BuildEventContext.ProjectInstanceId,
                                pid: pid
                            );
                        }

                        break;
                }
            }

            jsonWriter.WriteEndArray();

            static void writeEvent(
                Utf8JsonWriter jsonWriter,
                string name,
                uint ts,
                string? cat = null,
                string? ph = null,
                uint tts = 0,
                string? pid = null,
                int tid = 0,
                Dictionary<string, string>? args = null,
                string? id = null)
            {
                jsonWriter.WriteStartObject();

                jsonWriter.WriteString("name", name);
                jsonWriter.WriteString("cat", cat);
                jsonWriter.WriteString("ph", ph);
                jsonWriter.WriteNumber("ts", ts);
                jsonWriter.WriteNumber("tts", tts);
                jsonWriter.WriteString("pid", pid);
                jsonWriter.WriteNumber("tid", tid);

                if (args is null)
                {
                    jsonWriter.WriteString("args", (string?)null);
                }
                else
                {
                    jsonWriter.WriteStartObject("args");

                    foreach (var (key, value) in args)
                    {
                        jsonWriter.WriteString(key, value);
                    }

                    jsonWriter.WriteEndObject();
                }

                jsonWriter.WriteString("id", id);

                jsonWriter.WriteEndObject();
            }
        }

        private static class ChromiumTraceEventCategories
        {
            public const string ProjectToProject = "p2p";
        }

        private static class ChromiumTraceEventPhases
        {
            public const string DurationBegin = "B";
            public const string DurationEnd = "E";
            public const string FlowStart = "s";
            public const string FlowEnd = "F";
        }
    }
}
