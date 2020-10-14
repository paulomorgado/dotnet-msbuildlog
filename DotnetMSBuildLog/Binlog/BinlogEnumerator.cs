using Microsoft.Build.Framework;
using Microsoft.Build.Framework.Profiler;
using Microsoft.Build.Logging;
using Microsoft.Build.Logging.StructuredLogger;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace PauloMorgado.DotnetMSBuildLog.Binlog
{
    public class BinlogEnumerable : IEnumerable<BuildEventArgs>
    {
        private readonly string logFilePath;

        public BinlogEnumerable(string logFilePath)
        {
            if (string.IsNullOrWhiteSpace(logFilePath))
            {
                throw new ArgumentException($"'{nameof(logFilePath)}' cannot be null, empty or whitespace", nameof(logFilePath));
            }

            this.logFilePath = logFilePath;
        }

        public IEnumerator<BuildEventArgs> GetEnumerator()
        {
            return new BinlogEnumerator(this.logFilePath);
        }

        IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

        private class BinlogEnumerator : IEnumerator<BuildEventArgs>
        {
            // version 2: 
            //   - new BuildEventContext.EvaluationId
            //   - new record kinds: ProjectEvaluationStarted, ProjectEvaluationFinished
            // version 3:
            //   - new ProjectImportedEventArgs.ImportIgnored
            // version 4:
            //   - new TargetSkippedEventArgs
            //   - new TargetStartedEventArgs.BuildReason
            // version 5:
            //   - new EvaluationFinished.ProfilerResult
            // version 6:
            //   -  Ids and parent ids for the evaluation locations
            // version 7:
            //   - Include ProjectStartedEventArgs.GlobalProperties
            // version 8:
            //   - This was used in a now-reverted change but is the same as 9.
            // version 9:
            //   - new record kinds: EnvironmentVariableRead, PropertyReassignment, UninitializedPropertyRead
            private const int FileFormatVersion = 9;

            // reflection is needed to set these three fields because public constructors don't provide
            // a way to set these from the outside
            private static readonly FieldInfo buildEventArgsFieldThreadId = typeof(BuildEventArgs).GetField("threadId", BindingFlags.Instance | BindingFlags.NonPublic)!;
            private static readonly FieldInfo buildEventArgsFieldSenderName = typeof(BuildEventArgs).GetField("senderName", BindingFlags.Instance | BindingFlags.NonPublic)!;
            private static readonly FieldInfo buildEventArgsFieldTimestamp = typeof(BuildEventArgs).GetField("timestamp", BindingFlags.Instance | BindingFlags.NonPublic)!;

            private readonly BinaryReader binaryReader;
            private readonly int fileFormatVersion;
            private bool done;

#pragma warning disable CS8618 // Current is null value when exiting constructor. It should not be called before Netx().
            public BinlogEnumerator(string logFilePath)
#pragma warning restore CS8618 // Current is null value when exiting constructor. It should not be called before Netx().
            {
                var fileStream = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress, leaveOpen: false);
                this.binaryReader = new BinaryReader(gzipStream, Encoding.UTF8, leaveOpen: false);

                this.fileFormatVersion = this.binaryReader.ReadInt32();

                // the log file is written using a newer version of file format
                // that we don't know how to read
                if (this.fileFormatVersion > FileFormatVersion)
                {
                    throw new NotSupportedException($"Unsupported log file format. Latest supported version is {FileFormatVersion}, the log file has version {this.fileFormatVersion}.");
                }
            }

            public BuildEventArgs Current { get; private set; }

            object IEnumerator.Current => this.Current;

            public void Dispose()
            {
                this.binaryReader.Dispose();
            }

            public bool MoveNext()
            {
                while (!this.done)
                {
                    var recordKind = (BinaryLogRecordKind)this.ReadInt32();

                    while (IsBlob(recordKind))
                    {
                        this.SkipBlob();

                        recordKind = (BinaryLogRecordKind)this.ReadInt32();
                    }

                    switch (recordKind)
                    {
                        case BinaryLogRecordKind.EndOfFile:
                            this.done = true;
                            break;
                        case BinaryLogRecordKind.BuildStarted:
                            this.Current = this.ReadBuildStartedEventArgs();
                            return true;
                        case BinaryLogRecordKind.BuildFinished:
                            this.SkipBuildFinishedEventArgs();
                            break;
                        case BinaryLogRecordKind.ProjectStarted:
                            this.Current = this.ReadProjectStartedEventArgs();
                            return true;
                        case BinaryLogRecordKind.ProjectFinished:
                            this.Current = this.ReadProjectFinishedEventArgs();
                            return true;
                        case BinaryLogRecordKind.TargetStarted:
                            this.Current = this.ReadTargetStartedEventArgs();
                            return true;
                        case BinaryLogRecordKind.TargetFinished:
                            this.Current = this.ReadTargetFinishedEventArgs();
                            return true;
                        case BinaryLogRecordKind.TaskStarted:
                            this.Current = this.ReadTaskStartedEventArgs();
                            return true;
                        case BinaryLogRecordKind.TaskFinished:
                            this.Current = this.ReadTaskFinishedEventArgs();
                            return true;
                        case BinaryLogRecordKind.Error:
                            this.SkipBuildErrorEventArgs();
                            break;
                        case BinaryLogRecordKind.Warning:
                            this.SkipBuildWarningEventArgs();
                            break;
                        case BinaryLogRecordKind.Message:
                            this.SkipBuildMessageEventArgs();
                            break;
                        case BinaryLogRecordKind.CriticalBuildMessage:
                            this.SkipCriticalBuildMessageEventArgs();
                            break;
                        case BinaryLogRecordKind.TaskCommandLine:
                            this.SkipTaskCommandLineEventArgs();
                            break;
                        case BinaryLogRecordKind.ProjectEvaluationStarted:
                            this.Current = this.ReadProjectEvaluationStartedEventArgs();
                            return true;
                        case BinaryLogRecordKind.ProjectEvaluationFinished:
                            this.Current = this.ReadProjectEvaluationFinishedEventArgs();
                            return true;
                        case BinaryLogRecordKind.ProjectImported:
                            this.SkipProjectImportedEventArgs();
                            break;
                        case BinaryLogRecordKind.TargetSkipped:
                            this.SkipTargetSkippedEventArgs();
                            break;
                        case BinaryLogRecordKind.EnvironmentVariableRead:
                            this.SkipEnvironmentVariableReadEventArgs();
                            break;
                        case BinaryLogRecordKind.PropertyReassignment:
                            this.SkipPropertyReassignmentEventArgs();
                            break;
                        case BinaryLogRecordKind.UninitializedPropertyRead:
                            this.SkipUninitializedPropertyReadEventArgs();
                            break;
                        case BinaryLogRecordKind.PropertyInitialValueSet:
                            this.SkipPropertyInitialValueSetEventArgs();
                            break;
                        default:
                            break;
                    }
                }

                return false;
            }

            public void Reset()
            {
                throw new NotImplementedException();
            }

            /// <summary>
            /// For now it's just the ProjectImportArchive.
            /// </summary>
            private static bool IsBlob(BinaryLogRecordKind recordKind)
            {
                return recordKind == BinaryLogRecordKind.ProjectImportArchive;
            }

            private void SkipBlob()
            {
                var length = this.ReadInt32();
                this.SkipBytes(length);
            }

            private void SkipProjectImportedEventArgs()
            {
                this.SkipBuildEventArgsFields();

                this.SkipInt32();

                if (this.fileFormatVersion > 2)
                {
                    this.SkipBoolean();
                }

                this.SkipOptionalString();
                this.SkipOptionalString();
            }

            private void SkipTargetSkippedEventArgs()
            {
                this.SkipBuildEventArgsFields();
                this.SkipInt32();
                this.SkipOptionalString();
                this.SkipOptionalString();
                this.SkipOptionalString();
                this.SkipInt32();
            }

            private BuildEventArgs ReadBuildStartedEventArgs()
            {
                var fields = this.ReadBuildEventArgsFields();
                var environment = this.ReadStringDictionary();

                var e = new BuildStartedEventArgs(
                    fields.Message,
                    fields.HelpKeyword,
                    environment);
                SetCommonFields(e, fields);
                return e;
            }

            private void SkipBuildFinishedEventArgs()
            {
                this.SkipBuildEventArgsFields();
                this.SkipBoolean();
            }

            private BuildEventArgs ReadProjectEvaluationStartedEventArgs()
            {
                var fields = this.ReadBuildEventArgsFields();
                var projectFile = this.ReadString();

                var e = new ProjectEvaluationStartedEventArgs(fields.Message)
                {
                    ProjectFile = projectFile
                };
                SetCommonFields(e, fields);
                return e;
            }

            private BuildEventArgs ReadProjectEvaluationFinishedEventArgs()
            {
                var fields = this.ReadBuildEventArgsFields();
                var projectFile = this.ReadString();

                var e = new ProjectEvaluationFinishedEventArgs(fields.Message)
                {
                    ProjectFile = projectFile
                };
                SetCommonFields(e, fields);

                // ProfilerResult was introduced in version 5
                if (this.fileFormatVersion > 4)
                {
                    var hasProfileData = this.ReadBoolean();
                    if (hasProfileData)
                    {
                        var count = this.ReadInt32();

                        var d = new Dictionary<EvaluationLocation, ProfiledLocation>(count);
                        for (var i = 0; i < count; i++)
                        {
                            d.Add(this.ReadEvaluationLocation(), this.ReadProfiledLocation());
                        }
                        e.ProfilerResult = new ProfilerResult(d);
                    }
                }

                return e;
            }

            private BuildEventArgs ReadProjectStartedEventArgs()
            {
                var fields = this.ReadBuildEventArgsFields();
                BuildEventContext? parentContext = null;
                if (this.ReadBoolean())
                {
                    parentContext = this.ReadBuildEventContext();
                }

                var projectFile = this.ReadOptionalString();
                var projectId = this.ReadInt32();
                var targetNames = this.ReadString();
                var toolsVersion = this.ReadOptionalString();

                Dictionary<string, string>? globalProperties = null;

                if (this.fileFormatVersion > 6)
                {
                    if (this.ReadBoolean())
                    {
                        globalProperties = this.ReadStringDictionary();
                    }
                }

                var propertyList = this.ReadPropertyList();
                var itemList = this.ReadItems();

                var e = new ProjectStartedEventArgs(
                    projectId,
                    fields.Message,
                    fields.HelpKeyword,
                    projectFile,
                    targetNames,
                    propertyList,
                    itemList,
                    parentContext,
                    globalProperties,
                    toolsVersion);
                SetCommonFields(e, fields);
                return e;
            }

            private BuildEventArgs ReadProjectFinishedEventArgs()
            {
                var fields = this.ReadBuildEventArgsFields();
                var projectFile = this.ReadOptionalString();
                var succeeded = this.ReadBoolean();

                var e = new ProjectFinishedEventArgs(
                    fields.Message,
                    fields.HelpKeyword,
                    projectFile,
                    succeeded,
                    fields.Timestamp);
                SetCommonFields(e, fields);
                return e;
            }

            private BuildEventArgs ReadTargetStartedEventArgs()
            {
                var fields = this.ReadBuildEventArgsFields();
                var targetName = this.ReadOptionalString();
                var projectFile = this.ReadOptionalString();
                var targetFile = this.ReadOptionalString();
                var parentTarget = this.ReadOptionalString();
                // BuildReason was introduced in version 4
                var buildReason = this.fileFormatVersion > 3 ? (TargetBuiltReason)this.ReadInt32() : TargetBuiltReason.None;

                var e = new TargetStartedEventArgs(
                    fields.Message,
                    fields.HelpKeyword,
                    targetName,
                    projectFile,
                    targetFile,
                    parentTarget,
                    buildReason,
                    fields.Timestamp);
                SetCommonFields(e, fields);
                return e;
            }

            private BuildEventArgs ReadTargetFinishedEventArgs()
            {
                var fields = this.ReadBuildEventArgsFields();
                var succeeded = this.ReadBoolean();
                var projectFile = this.ReadOptionalString();
                var targetFile = this.ReadOptionalString();
                var targetName = this.ReadOptionalString();
                var targetOutputItemList = this.ReadItemList();

                var e = new TargetFinishedEventArgs(
                    fields.Message,
                    fields.HelpKeyword,
                    targetName,
                    projectFile,
                    targetFile,
                    succeeded,
                    fields.Timestamp,
                    targetOutputItemList);
                SetCommonFields(e, fields);
                return e;
            }

            private BuildEventArgs ReadTaskStartedEventArgs()
            {
                var fields = this.ReadBuildEventArgsFields();
                var taskName = this.ReadOptionalString();
                var projectFile = this.ReadOptionalString();
                var taskFile = this.ReadOptionalString();

                var e = new TaskStartedEventArgs(
                    fields.Message,
                    fields.HelpKeyword,
                    projectFile,
                    taskFile,
                    taskName,
                    fields.Timestamp);
                SetCommonFields(e, fields);
                return e;
            }

            private BuildEventArgs ReadTaskFinishedEventArgs()
            {
                var fields = this.ReadBuildEventArgsFields();
                var succeeded = this.ReadBoolean();
                var taskName = this.ReadOptionalString();
                var projectFile = this.ReadOptionalString();
                var taskFile = this.ReadOptionalString();

                var e = new TaskFinishedEventArgs(
                    fields.Message,
                    fields.HelpKeyword,
                    projectFile,
                    taskFile,
                    taskName,
                    succeeded,
                    fields.Timestamp);
                SetCommonFields(e, fields);
                return e;
            }

            private void SkipBuildErrorEventArgs()
            {
                this.SkipBuildEventArgsFields();
                this.SkipDiagnosticFields();
            }

            private void SkipBuildWarningEventArgs()
            {
                this.SkipBuildEventArgsFields();
                this.SkipDiagnosticFields();
            }

            private void SkipBuildMessageEventArgs()
            {
                this.SkipBuildEventArgsFields();
                this.SkipInt32();
            }

            private void SkipTaskCommandLineEventArgs()
            {
                this.SkipBuildEventArgsFields();
                this.SkipInt32();
                this.SkipOptionalString();
                this.SkipOptionalString();
            }

            private void SkipCriticalBuildMessageEventArgs()
            {
                this.SkipBuildEventArgsFields();
                this.SkipInt32();
            }

            private void SkipEnvironmentVariableReadEventArgs()
            {
                this.SkipBuildEventArgsFields();
                this.SkipInt32();
                this.SkipString();
            }

            private void SkipPropertyReassignmentEventArgs()
            {
                this.SkipBuildEventArgsFields();
                this.SkipInt32();
                this.SkipString();
                this.SkipString();
                this.SkipString();
                this.SkipString();
            }

            private void SkipUninitializedPropertyReadEventArgs()
            {
                this.SkipBuildEventArgsFields();
                this.SkipInt32();
                this.SkipString();
            }

            private void SkipPropertyInitialValueSetEventArgs()
            {
                this.SkipBuildEventArgsFields();
                this.SkipInt32();
                this.SkipString();
                this.SkipString();
                this.SkipString();
            }

            private void SkipDiagnosticFields()
            {
                this.SkipOptionalString();
                this.SkipOptionalString();
                this.SkipOptionalString();
                this.SkipOptionalString();
                this.SkipInt32();
                this.SkipInt32();
                this.SkipInt32();
                this.SkipInt32();
            }

            private BuildEventArgsFields ReadBuildEventArgsFields()
            {
                var flags = (BuildEventArgsFieldFlags)this.ReadInt32();
                var result = new BuildEventArgsFields
                {
                    Flags = flags
                };

                if ((flags & BuildEventArgsFieldFlags.Message) != 0)
                {
                    result.Message = this.ReadString();
                }

                if ((flags & BuildEventArgsFieldFlags.BuildEventContext) != 0)
                {
                    result.BuildEventContext = this.ReadBuildEventContext();
                }

                if ((flags & BuildEventArgsFieldFlags.ThreadId) != 0)
                {
                    result.ThreadId = this.ReadInt32();
                }

                if ((flags & BuildEventArgsFieldFlags.HelpHeyword) != 0)
                {
                    result.HelpKeyword = this.ReadString();
                }

                if ((flags & BuildEventArgsFieldFlags.SenderName) != 0)
                {
                    result.SenderName = this.ReadString();
                }

                if ((flags & BuildEventArgsFieldFlags.Timestamp) != 0)
                {
                    result.Timestamp = this.ReadDateTime();
                }

                if ((flags & BuildEventArgsFieldFlags.Subcategory) != 0)
                {
                    result.Subcategory = this.ReadString();
                }

                if ((flags & BuildEventArgsFieldFlags.Code) != 0)
                {
                    result.Code = this.ReadString();
                }

                if ((flags & BuildEventArgsFieldFlags.File) != 0)
                {
                    result.File = this.ReadString();
                }

                if ((flags & BuildEventArgsFieldFlags.ProjectFile) != 0)
                {
                    result.ProjectFile = this.ReadString();
                }

                if ((flags & BuildEventArgsFieldFlags.LineNumber) != 0)
                {
                    result.LineNumber = this.ReadInt32();
                }

                if ((flags & BuildEventArgsFieldFlags.ColumnNumber) != 0)
                {
                    result.ColumnNumber = this.ReadInt32();
                }

                if ((flags & BuildEventArgsFieldFlags.EndLineNumber) != 0)
                {
                    result.EndLineNumber = this.ReadInt32();
                }

                if ((flags & BuildEventArgsFieldFlags.EndColumnNumber) != 0)
                {
                    result.EndColumnNumber = this.ReadInt32();
                }

                return result;
            }

            private void SkipBuildEventArgsFields()
            {
                var flags = (BuildEventArgsFieldFlags)this.ReadInt32();

                if ((flags & BuildEventArgsFieldFlags.Message) != 0)
                {
                    this.SkipString();
                }

                if ((flags & BuildEventArgsFieldFlags.BuildEventContext) != 0)
                {
                    this.SkipBuildEventContext();
                }

                if ((flags & BuildEventArgsFieldFlags.ThreadId) != 0)
                {
                    this.SkipInt32();
                }

                if ((flags & BuildEventArgsFieldFlags.HelpHeyword) != 0)
                {
                    this.SkipString();
                }

                if ((flags & BuildEventArgsFieldFlags.SenderName) != 0)
                {
                    this.SkipString();
                }

                if ((flags & BuildEventArgsFieldFlags.Timestamp) != 0)
                {
                    this.SkipDateTime();
                }

                if ((flags & BuildEventArgsFieldFlags.Subcategory) != 0)
                {
                    this.SkipString();
                }

                if ((flags & BuildEventArgsFieldFlags.Code) != 0)
                {
                    this.SkipString();
                }

                if ((flags & BuildEventArgsFieldFlags.File) != 0)
                {
                    this.SkipString();
                }

                if ((flags & BuildEventArgsFieldFlags.ProjectFile) != 0)
                {
                    this.SkipString();
                }

                if ((flags & BuildEventArgsFieldFlags.LineNumber) != 0)
                {
                    this.SkipInt32();
                }

                if ((flags & BuildEventArgsFieldFlags.ColumnNumber) != 0)
                {
                    this.SkipInt32();
                }

                if ((flags & BuildEventArgsFieldFlags.EndLineNumber) != 0)
                {
                    this.SkipInt32();
                }

                if ((flags & BuildEventArgsFieldFlags.EndColumnNumber) != 0)
                {
                    this.SkipInt32();
                }
            }

            private static void SetCommonFields(BuildEventArgs buildEventArgs, BuildEventArgsFields fields)
            {
                buildEventArgs.BuildEventContext = fields.BuildEventContext;

                if ((fields.Flags & BuildEventArgsFieldFlags.ThreadId) != 0)
                {
                    buildEventArgsFieldThreadId.SetValue(buildEventArgs, fields.ThreadId);
                }

                if ((fields.Flags & BuildEventArgsFieldFlags.SenderName) != 0)
                {
                    buildEventArgsFieldSenderName.SetValue(buildEventArgs, fields.SenderName);
                }

                if ((fields.Flags & BuildEventArgsFieldFlags.Timestamp) != 0)
                {
                    buildEventArgsFieldTimestamp.SetValue(buildEventArgs, fields.Timestamp);
                }
            }

            private IEnumerable ReadPropertyList()
            {
                var properties = this.ReadStringDictionary();
                if (properties == null)
                {
                    return Array.Empty<DictionaryEntry>();
                }

                var list = new ArrayList();
                foreach (var property in properties)
                {
                    var entry = new DictionaryEntry(property.Key, property.Value);
                    list.Add(entry);
                }

                return list;
            }

            private BuildEventContext ReadBuildEventContext()
            {
                var nodeId = this.ReadInt32();
                var projectContextId = this.ReadInt32();
                var targetId = this.ReadInt32();
                var taskId = this.ReadInt32();
                var submissionId = this.ReadInt32();
                var projectInstanceId = this.ReadInt32();

                // evaluationId was introduced in format version 2
                var evaluationId = BuildEventContext.InvalidEvaluationId;
                if (this.fileFormatVersion > 1)
                {
                    evaluationId = this.ReadInt32();
                }

                var result = new BuildEventContext(
                    submissionId,
                    nodeId,
                    evaluationId,
                    projectInstanceId,
                    projectContextId,
                    targetId,
                    taskId);
                return result;
            }

            private void SkipBuildEventContext()
            {
                this.SkipInt32((this.fileFormatVersion > 1) ? 7 : 6);
            }

            private Dictionary<string, string>? ReadStringDictionary()
            {
                var count = this.ReadInt32();

                if (count == 0)
                {
                    return null;
                }

                var result = new Dictionary<string, string>(count);
                for (var i = 0; i < count; i++)
                {
                    var key = this.ReadString()!;
                    var value = this.ReadString()!;
                    result[key] = value;
                }

                return result;
            }

            private ITaskItem ReadItem()
            {
                var item = new TaskItem
                {
                    ItemSpec = this.ReadString()
                };

                var count = this.ReadInt32();
                for (var i = 0; i < count; i++)
                {
                    var name = this.ReadString();
                    var value = this.ReadString();
                    item.Metadata[name] = value;
                }

                return item;
            }

            private IEnumerable ReadItems()
            {
                var count = this.ReadInt32();

                var list = new List<DictionaryEntry>(count);

                for (var i = 0; i < count; i++)
                {
                    var key = this.ReadString();
                    var item = this.ReadItem();
                    list.Add(new DictionaryEntry(key, item));
                }

                return list;
            }

            private IEnumerable ReadItemList()
            {
                var count = this.ReadInt32();
                if (count == 0)
                {
                    return Array.Empty<ITaskItem>();
                }

                var list = new List<ITaskItem>(count);

                for (var i = 0; i < count; i++)
                {
                    var item = this.ReadItem();
                    list.Add(item);
                }

                return list;
            }

            private string? ReadOptionalString() => this.ReadBoolean() ? this.ReadString() : null;

            private void SkipOptionalString()
            {
                if (this.ReadBoolean())
                {
                    this.SkipString();
                }
            }

            private string ReadString() => this.binaryReader.ReadString();

            private void SkipString() => this.SkipBytes(this.Read7BitEncodedInt());

            private void SkipBytes(int count)
            {
                //this.binaryReader.BaseStream.Seek(count, SeekOrigin.Current);
                _ = this.binaryReader.ReadBytes(count);
            }

            private int ReadInt32() => this.Read7BitEncodedInt();

            private void SkipInt32(int count = 1) => Skip7BitEncodedInt(this.binaryReader, count);

            private long ReadInt64() => this.binaryReader.ReadInt64();

            private void SkipInt64() => this.binaryReader.ReadInt64();

            private bool ReadBoolean() => this.binaryReader.ReadBoolean();

            private void SkipBoolean() => this.binaryReader.ReadBoolean();

            private DateTime ReadDateTime() => new(this.binaryReader.ReadInt64(), (DateTimeKind)this.ReadInt32());

            private void SkipDateTime()
            {
                this.SkipInt64();
                this.SkipInt32();
            }

            private TimeSpan ReadTimeSpan() => new TimeSpan(this.ReadInt64());

            private void SkipTimeSpan() => this.SkipInt64();

            private int Read7BitEncodedInt()
            {
                // Read out an Int32 7 bits at a time.  The high bit
                // of the byte when on means to continue reading more bytes.
                var value = 0;
                var shift = 0;
                byte b;
                do
                {
                    // Check for a corrupted stream.  Read a max of 5 bytes.
                    // In a future version, add a DataFormatException.
                    if (shift == 5 * 7)  // 5 bytes max per Int32, shift += 7
                    {
                        throw new FormatException();
                    }

                    // ReadByte handles end of stream cases for us.
                    b = this.binaryReader.ReadByte();
                    value |= (b & 0x7F) << shift;
                    shift += 7;
                } while ((b & 0x80) != 0);

                return value;
            }

            private static void Skip7BitEncodedInt(BinaryReader reader, int count = 1)
            {
                while (count-- > 0)
                {
                    // Read out an Int32 7 bits at a time.  The high bit
                    // of the byte when on means to continue reading more bytes.
                    var value = 0;
                    var shift = 0;
                    byte b;
                    do
                    {
                        // Check for a corrupted stream.  Read a max of 5 bytes.
                        // In a future version, add a DataFormatException.
                        if (shift == 5 * 7)  // 5 bytes max per Int32, shift += 7
                        {
                            throw new FormatException();
                        }

                        // ReadByte handles end of stream cases for us.
                        b = reader.ReadByte();
                        value |= (b & 0x7F) << shift;
                        shift += 7;
                    } while ((b & 0x80) != 0);
                }
            }

            private ProfiledLocation ReadProfiledLocation()
            {
                var numberOfHits = this.ReadInt32();
                var exclusiveTime = this.ReadTimeSpan();
                var inclusiveTime = this.ReadTimeSpan();

                return new ProfiledLocation(inclusiveTime, exclusiveTime, numberOfHits);
            }

            private EvaluationLocation ReadEvaluationLocation()
            {
                var elementName = this.ReadOptionalString();
                var description = this.ReadOptionalString();
                var evaluationDescription = this.ReadOptionalString();
                var file = this.ReadOptionalString();
                var kind = (EvaluationLocationKind)this.ReadInt32();
                var evaluationPass = (EvaluationPass)this.ReadInt32();

                int? line = null;
                var hasLine = this.ReadBoolean();
                if (hasLine)
                {
                    line = this.ReadInt32();
                }

                // Id and parent Id were introduced in version 6
                if (this.fileFormatVersion > 5)
                {
                    var id = this.ReadInt64();
                    long? parentId = null;
                    var hasParent = this.ReadBoolean();
                    if (hasParent)
                    {
                        parentId = this.ReadInt64();

                    }
                    return new EvaluationLocation(id, parentId, evaluationPass, evaluationDescription, file, line, elementName, description, kind);
                }

                return new EvaluationLocation(0, null, evaluationPass, evaluationDescription, file, line, elementName, description, kind);
            }
        }
    }
}
