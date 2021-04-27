﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml;
using Aquarius.TimeSeries.Client;
using Aquarius.TimeSeries.Client.ServiceModels.Acquisition;
using Aquarius.TimeSeries.Client.ServiceModels.Provisioning;
using log4net;
using NodaTime;
using NodaTime.Text;
using ServiceStack;
using ServiceStack.Logging.Log4Net;

namespace PointZilla
{
    public class Program
    {
        private static ILog _log;

        public static void Main(string[] args)
        {
            Environment.ExitCode = 1;

            try
            {
                ConfigureLogging();

                ServiceStackConfig.ConfigureServiceStack();

                var context = ParseArgs(args);
                new Program(context).Run();

                Environment.ExitCode = 0;
            }
            catch (WebServiceException exception)
            {
                _log.Error($"API: ({exception.StatusCode}) {string.Join(" ", exception.StatusDescription, exception.ErrorCode)}: {string.Join(" ", exception.Message, exception.ErrorMessage)}", exception);
            }
            catch (ExpectedException exception)
            {
                _log.Error(exception.Message);
            }
            catch (Exception exception)
            {
                _log.Error("Unhandled exception", exception);
            }
        }

        private static void ConfigureLogging()
        {
            using (var stream = new MemoryStream(LoadEmbeddedResource("log4net.config")))
            using (var reader = new StreamReader(stream))
            {
                var xml = new XmlDocument();
                xml.LoadXml(reader.ReadToEnd());

                log4net.Config.XmlConfigurator.Configure(xml.DocumentElement);

                _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

                ServiceStack.Logging.LogManager.LogFactory = new Log4NetFactory();
            }
        }

        private static byte[] LoadEmbeddedResource(string path)
        {
            // ReSharper disable once PossibleNullReferenceException
            var resourceName = $"{MethodBase.GetCurrentMethod().DeclaringType.Namespace}.{path}";

            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    throw new ExpectedException($"Can't load '{resourceName}' as embedded resource.");

                return stream.ReadFully();
            }
        }

        private static string GetProgramName()
        {
            // ReSharper disable once PossibleNullReferenceException
            return Path.GetFileNameWithoutExtension(Assembly.GetEntryAssembly().Location);
        }

        private static string GetExecutingFileVersion()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var fileVersionInfo = FileVersionInfo.GetVersionInfo(assembly.Location);

            // ReSharper disable once PossibleNullReferenceException
            return $"{MethodBase.GetCurrentMethod().DeclaringType.Namespace} v{fileVersionInfo.FileVersion}";
        }

        private static Context ParseArgs(string[] args)
        {
            var context = new Context
            {
                ExecutingFileVersion = GetExecutingFileVersion()
            };

            SetNgCsvFormat(context);

            var resolvedArgs = args
                .SelectMany(ResolveOptionsFromFile)
                .ToArray();

            var options = new[]
            {
                new Option {Key = nameof(context.Server), Setter = value => context.Server = value, Getter = () => context.Server, Description = "AQTS server name"},
                new Option {Key = nameof(context.Username), Setter = value => context.Username = value, Getter = () => context.Username, Description = "AQTS username"},
                new Option {Key = nameof(context.Password), Setter = value => context.Password = value, Getter = () => context.Password, Description = "AQTS password"},
                new Option {Key = nameof(context.SessionToken), Setter = value => context.SessionToken = value, Getter = () => context.SessionToken},

                new Option {Key = nameof(context.Wait), Setter = value => context.Wait = bool.Parse(value), Getter = () => context.Wait.ToString(), Description = "Wait for the append request to complete"},
                new Option {Key = nameof(context.AppendTimeout), Setter = value => context.AppendTimeout = TimeSpan.Parse(value), Getter = () => context.AppendTimeout.ToString(), Description = "Timeout period for append completion, in .NET TimeSpan format."},
                new Option {Key = nameof(context.BatchSize), Setter = value => context.BatchSize = int.Parse(value), Getter = () => context.BatchSize.ToString(), Description = "Maximum number of points to send in a single append request"},

                new Option(), new Option {Description = "Time-series options:"},
                new Option {Key = nameof(context.TimeSeries), Setter = value => context.TimeSeries = value, Getter = () => context.TimeSeries, Description = "Target time-series identifier or unique ID"},
                new Option {Key = nameof(context.TimeRange), Setter = value => context.TimeRange = ParseInterval(value), Getter = () => context.TimeRange?.ToString(), Description = "Time-range for overwrite in ISO8061/ISO8601 (defaults to start/end points)"},
                new Option {Key = nameof(context.Command), Setter = value => context.Command = ParseEnum<CommandType>(value), Getter = () => context.Command.ToString(), Description = $"Append operation to perform.  {EnumOptions<CommandType>()}"},
                new Option {Key = nameof(context.GradeCode), Setter = value => context.GradeCode = int.Parse(value), Getter = () => context.GradeCode.ToString(), Description = "Optional grade code for all appended points"},
                new Option {Key = nameof(context.Qualifiers), Setter = value => context.Qualifiers = QualifiersParser.Parse(value), Getter = () => string.Empty, Description = "Optional qualifier list for all appended points"},

                new Option(), new Option {Description = "Metadata options:"},
                new Option {Key = nameof(context.IgnoreGrades), Setter = value => context.IgnoreGrades = bool.Parse(value), Getter = () => $"{context.IgnoreGrades}", Description = "Ignore any specified grade codes."},
                new Option {Key = nameof(context.IgnoreQualifiers), Setter = value => context.IgnoreQualifiers = bool.Parse(value), Getter = () => $"{context.IgnoreQualifiers}", Description = "Ignore any specified qualifiers."},
                new Option {Key = nameof(context.MappedGrades), Setter = value => ParseMappedGrade(context, value), Getter = () => string.Empty, Description = "Grade mapping in sourceValue:mappedValue syntax. Can be set multiple times."},
                new Option {Key = nameof(context.MappedQualifiers), Setter = value => ParseMappedQualifier(context, value), Getter = () => string.Empty, Description = "Qualifier mapping in sourceValue:mappedValue syntax. Can be set multiple times."},

                new Option(), new Option {Description = "Time-series creation options:"},
                new Option {Key = nameof(context.CreateMode), Setter = value => context.CreateMode = ParseEnum<CreateMode>(value), Getter = () => context.CreateMode.ToString(), Description = $"Mode for creating missing time-series. {EnumOptions<CreateMode>()}"},
                new Option {Key = nameof(context.GapTolerance), Setter = value => context.GapTolerance = value.FromJson<Duration>(), Getter = () => context.GapTolerance.ToJson(), Description = "Gap tolerance for newly-created time-series."},
                new Option {Key = nameof(context.UtcOffset), Setter = value => context.UtcOffset = ParseOffset(value), Getter = () => string.Empty, Description = "UTC offset for any created time-series or location. [default: Use system timezone]"},
                new Option {Key = nameof(context.Unit), Setter = value => context.Unit = value, Getter = () => context.Unit, Description = "Time-series unit"},
                new Option {Key = nameof(context.InterpolationType), Setter = value => context.InterpolationType = ParseEnum<InterpolationType>(value), Getter = () => context.InterpolationType.ToString(), Description = $"Time-series interpolation type. {EnumOptions<InterpolationType>()}"},
                new Option {Key = nameof(context.Publish), Setter = value => context.Publish = bool.Parse(value), Getter = () => context.Publish.ToString(), Description = "Publish flag."},
                new Option {Key = nameof(context.Description), Setter = value => context.Description = value, Getter = () => context.Description, Description = "Time-series description"},
                new Option {Key = nameof(context.Comment), Setter = value => context.Comment = value, Getter = () => context.Comment, Description = "Time-series comment"},
                new Option {Key = nameof(context.Method), Setter = value => context.Method = value, Getter = () => context.Method, Description = "Time-series monitoring method"},
                new Option {Key = nameof(context.ComputationIdentifier), Setter = value => context.ComputationIdentifier = value, Getter = () => context.ComputationIdentifier, Description = "Time-series computation identifier"},
                new Option {Key = nameof(context.ComputationPeriodIdentifier), Setter = value => context.ComputationPeriodIdentifier = value, Getter = () => context.ComputationPeriodIdentifier, Description = "Time-series computation period identifier"},
                new Option {Key = nameof(context.SubLocationIdentifier), Setter = value => context.SubLocationIdentifier = value, Getter = () => context.SubLocationIdentifier, Description = "Time-series sub-location identifier"},
                new Option {Key = nameof(context.TimeSeriesType), Setter = value => context.TimeSeriesType = ParseEnum<TimeSeriesType>(value), Getter = () => context.TimeSeriesType.ToString(), Description = $"Time-series type. {EnumOptions<TimeSeriesType>()}"},
                new Option {Key = nameof(context.ExtendedAttributeValues), Setter = value => ParseExtendedAttributeValue(context, value), Getter = () => string.Empty, Description = "Extended attribute values in UPPERCASE_COLUMN_NAME@UPPERCASE_TABLE_NAME=value syntax. Can be set multiple times."},

                new Option(), new Option {Description = "Copy points from another time-series:"},
                new Option {Key = nameof(context.SourceTimeSeries), Setter = value => {if (TimeSeriesIdentifier.TryParse(value, out var tsi)) context.SourceTimeSeries = tsi; }, Getter = () => context.SourceTimeSeries?.ToString(), Description = "Source time-series to copy. Prefix with [server2] or [server2:username2:password2] to copy from another server"},
                new Option {Key = nameof(context.SourceQueryFrom), Setter = value => context.SourceQueryFrom = ParseInstant(value), Getter = () => context.SourceQueryFrom?.ToJson(), Description = "Start time of extracted points in ISO8601 format."},
                new Option {Key = nameof(context.SourceQueryTo), Setter = value => context.SourceQueryTo = ParseInstant(value), Getter = () => context.SourceQueryTo?.ToJson(), Description = "End time of extracted points"},

                new Option(), new Option {Description = "Point-generator options:"},
                new Option {Key = nameof(context.StartTime), Setter = value => context.StartTime = ParseInstant(value), Getter = () => string.Empty, Description = "Start time of generated points, in ISO8601 format. [default: the current time]"},
                new Option {Key = nameof(context.PointInterval), Setter = value => context.PointInterval = TimeSpan.Parse(value), Getter = () => context.PointInterval.ToString(), Description = "Interval between generated points, in .NET TimeSpan format."},
                new Option {Key = nameof(context.NumberOfPoints), Setter = value => context.NumberOfPoints = int.Parse(value), Getter = () => context.NumberOfPoints.ToString(), Description = $"Number of points to generate. If 0, use {nameof(context.NumberOfPeriods)}"},
                new Option {Key = nameof(context.NumberOfPeriods), Setter = value => context.NumberOfPeriods = double.Parse(value), Getter = () => context.NumberOfPeriods.ToString(CultureInfo.InvariantCulture), Description = "Number of waveform periods to generate."},
                new Option {Key = nameof(context.WaveformType), Setter = value => context.WaveformType = ParseEnum<WaveformType>(value), Getter = () => context.WaveformType.ToString(), Description = $"Waveform to generate. {EnumOptions<WaveformType>()}"},
                new Option {Key = nameof(context.WaveformOffset), Setter = value => context.WaveformOffset = double.Parse(value), Getter = () => context.WaveformOffset.ToString(CultureInfo.InvariantCulture), Description = "Offset the generated waveform by this constant."},
                new Option {Key = nameof(context.WaveformPhase), Setter = value => context.WaveformPhase = double.Parse(value), Getter = () => context.WaveformPhase.ToString(CultureInfo.InvariantCulture), Description = "Phase within one waveform period"},
                new Option {Key = nameof(context.WaveformScalar), Setter = value => context.WaveformScalar = double.Parse(value), Getter = () => context.WaveformScalar.ToString(CultureInfo.InvariantCulture), Description = "Scale the waveform by this amount"},
                new Option {Key = nameof(context.WaveformPeriod), Setter = value => context.WaveformPeriod = double.Parse(value), Getter = () => context.WaveformPeriod.ToString(CultureInfo.InvariantCulture), Description = "Waveform period before repeating"},
                new Option {Key = nameof(context.WaveFormTextX), Setter = value => context.WaveFormTextX = value, Getter = () => context.WaveFormTextX, Description = "Select the X values of the vectorized text"},
                new Option {Key = nameof(context.WaveFormTextY), Setter = value => context.WaveFormTextY = value, Getter = () => context.WaveFormTextY, Description = "Select the Y values of the vectorized text"},

                new Option(), new Option {Description = "CSV parsing options:"},
                new Option {Key = "CSV", Setter = value => context.CsvFiles.Add(value), Getter = () => string.Join(", ", context.CsvFiles), Description = "Parse the CSV file"},
                new Option {Key = nameof(context.CsvDateTimeField), Setter = value => context.CsvDateTimeField = int.Parse(value), Getter = () => context.CsvDateTimeField.ToString(), Description = "CSV column index for combined date+time timestamps"},
                new Option {Key = nameof(context.CsvDateTimeFormat), Setter = value => context.CsvDateTimeFormat = value, Getter = () => context.CsvDateTimeFormat, Description = "Format of CSV date+time fields [default: ISO8601 format]"},
                new Option {Key = nameof(context.CsvDateOnlyField), Setter = value => context.CsvDateOnlyField = int.Parse(value), Getter = () => context.CsvDateOnlyField.ToString(), Description = "CSV column index for date-only timestamps"},
                new Option {Key = nameof(context.CsvDateOnlyFormat), Setter = value => context.CsvDateOnlyFormat = value, Getter = () => context.CsvDateOnlyFormat, Description = "Format of CSV date-only fields"},
                new Option {Key = nameof(context.CsvTimeOnlyField), Setter = value => context.CsvTimeOnlyField = int.Parse(value), Getter = () => context.CsvTimeOnlyField.ToString(), Description = "CSV column index for time-only timestamps"},
                new Option {Key = nameof(context.CsvTimeOnlyFormat), Setter = value => context.CsvTimeOnlyFormat = value, Getter = () => context.CsvTimeOnlyFormat, Description = "Format of CSV time-only fields"},
                new Option {Key = nameof(context.CsvDefaultTimeOfDay), Setter = value => context.CsvDefaultTimeOfDay = value, Getter = () => context.CsvDefaultTimeOfDay, Description = "Time of day value when no time field is used"},
                new Option {Key = nameof(context.CsvValueField), Setter = value => context.CsvValueField = int.Parse(value), Getter = () => context.CsvValueField.ToString(), Description = "CSV column index for values"},
                new Option {Key = nameof(context.CsvGradeField), Setter = value => context.CsvGradeField = int.Parse(value), Getter = () => context.CsvGradeField.ToString(), Description = "CSV column index for grade codes"},
                new Option {Key = nameof(context.CsvQualifiersField), Setter = value => context.CsvQualifiersField = int.Parse(value), Getter = () => context.CsvQualifiersField.ToString(), Description = "CSV column index for qualifiers"},
                new Option {Key = nameof(context.CsvComment), Setter = value => context.CsvComment = value, Getter = () => context.CsvComment, Description = "CSV comment lines begin with this prefix"},
                new Option {Key = nameof(context.CsvSkipRows), Setter = value => context.CsvSkipRows = int.Parse(value), Getter = () => context.CsvSkipRows.ToString(), Description = "Number of CSV rows to skip before parsing"},
                new Option {Key = nameof(context.CsvIgnoreInvalidRows), Setter = value => context.CsvIgnoreInvalidRows = bool.Parse(value), Getter = () => context.CsvIgnoreInvalidRows.ToString(), Description = "Ignore CSV rows that can't be parsed"},
                new Option {Key = nameof(context.CsvRealign), Setter = value => context.CsvRealign = bool.Parse(value), Getter = () => context.CsvRealign.ToString(), Description = $"Realign imported CSV points to the /{nameof(context.StartTime)} value"},
                new Option {Key = nameof(context.CsvRemoveDuplicatePoints), Setter = value => context.CsvRemoveDuplicatePoints = bool.Parse(value), Getter = () => context.CsvRemoveDuplicatePoints.ToString(), Description = "Remove duplicate points in the CSV before appending."},
                new Option {Key = nameof(context.CsvDelimiter), Setter = value => context.CsvDelimiter = value, Getter = () => context.CsvDelimiter, Description = "Delimiter between CSV fields."},
                new Option {Key = nameof(context.CsvNanValue), Setter = value => context.CsvNanValue = value, Getter = () => context.CsvNanValue, Description = "Special value text used to represent NaN values"},
                new Option {Key = "CsvFormat", Description = "Shortcut for known CSV formats. One of 'NG', '3X', or 'PointZilla'. [default: NG]", Setter =
                    value =>
                    {
                        if (value.Equals("Ng", StringComparison.InvariantCultureIgnoreCase))
                        {
                            SetNgCsvFormat(context);
                        }
                        else if (value.Equals("3x", StringComparison.InvariantCultureIgnoreCase))
                        {
                            Set3XCsvFormat(context);
                        }
                        else if (value.Equals("PointZilla", StringComparison.InvariantCultureIgnoreCase))
                        {
                            CsvWriter.SetPointZillaCsvFormat(context);
                        }
                        else
                        {
                            throw new ExpectedException($"'{value}' is an unknown CSV format.");
                        }
                    }},
                new Option {Key = nameof(context.ExcelSheetNumber), Setter = value => context.ExcelSheetNumber = int.Parse(value), Getter = () => context.ExcelSheetNumber.ToString(), Description = $"Excel worksheet number to parse [default to first sheet]"},
                new Option {Key = nameof(context.ExcelSheetName), Setter = value => context.ExcelSheetName = value, Getter = () => context.ExcelSheetName, Description = $"Excel worksheet name to parse [default to first sheet]"},

                new Option(), new Option {Description = "CSV saving options:"},
                new Option {Key = nameof(context.SaveCsvPath), Setter = value => context.SaveCsvPath = value, Getter = () => context.SaveCsvPath, Description = "When set, saves the extracted/generated points to a CSV file. If only a directory is specified, an appropriate filename will be generated."},
                new Option {Key = nameof(context.StopAfterSavingCsv), Setter = value => context.StopAfterSavingCsv = bool.Parse(value), Getter = () => context.StopAfterSavingCsv.ToString(), Description = "When true, stop after saving a CSV file, before appending any points."},
            };

            var usageMessage
                    = $"Append points to an AQTS time-series."
                      + $"\n"
                      + $"\nusage: {GetProgramName()} [-option=value] [@optionsFile] [command] [identifierOrGuid] [value] [csvFile] ..."
                      + $"\n"
                      + $"\nSupported -option=value settings (/option=value works too):\n\n  {string.Join("\n  ", options.Select(o => o.UsageText()))}"
                      + $"\n"
                      + $"\nUse the @optionsFile syntax to read more options from a file."
                      + $"\n"
                      + $"\n  Each line in the file is treated as a command line option."
                      + $"\n  Blank lines and leading/trailing whitespace is ignored."
                      + $"\n  Comment lines begin with a # or // marker."
                ;

            var helpGuidance = "See /help screen for details.";

            var knownCommands = new Dictionary<string, CommandType>(StringComparer.InvariantCultureIgnoreCase);
            foreach (var commandType in Enum.GetValues(typeof(CommandType)).Cast<CommandType>())
            {
                knownCommands.Add(commandType.ToString(), commandType);
            }

            foreach (var arg in resolvedArgs)
            {
                var match = ArgRegex.Match(arg);

                if (!match.Success)
                {
                    if (HelpKeyWords.Contains(arg))
                        throw new ExpectedException(usageMessage);

                    // Try positional arguments: [command] [identifierOrGuid] [value] [csvFile]
                    if (knownCommands.TryGetValue(arg, out var command))
                    {
                        context.Command = command;
                        continue;
                    }

                    if (double.TryParse(arg, out var numericValue))
                    {
                        ParseManualPoints(context, numericValue);
                        continue;
                    }

                    if (Enum.TryParse<PointType>(arg, true, out var pointType))
                    {
                        if (pointType == PointType.Gap)
                        {
                            ParseManualGap(context);
                        }

                        continue;
                    }

                    if (File.Exists(arg))
                    {
                        context.CsvFiles.Add(arg);
                        continue;
                    }

                    if (Guid.TryParse(arg, out var _))
                    {
                        context.TimeSeries = arg;
                        continue;
                    }

                    if (TimeSeriesIdentifier.TryParse(arg, out var _))
                    {
                        context.TimeSeries = arg;
                        continue;
                    }

                    throw new ExpectedException($"Unknown argument: {arg}\n\n{helpGuidance}");
                }

                var key = match.Groups["key"].Value.ToLower();
                var value = match.Groups["value"].Value;

                var option =
                    options.FirstOrDefault(o => o.Key != null && o.Key.Equals(key, StringComparison.InvariantCultureIgnoreCase));

                if (option == null)
                {
                    throw new ExpectedException($"Unknown -option=value: {arg}\n\n{helpGuidance}");
                }

                option.Setter(value);
            }

            if (string.IsNullOrEmpty(context.Server) && string.IsNullOrEmpty(context.TimeSeries) && !string.IsNullOrEmpty(context.SaveCsvPath))
                context.StopAfterSavingCsv = true;

            if (context.SourceTimeSeries != null && string.IsNullOrEmpty(context.SourceTimeSeries.Server) && string.IsNullOrEmpty(context.Server))
                throw new ExpectedException($"A /{nameof(context.Server)} option is required to load the source time-series.\n\n{helpGuidance}");

            if (!context.StopAfterSavingCsv)
            {
                if (string.IsNullOrWhiteSpace(context.Server))
                    throw new ExpectedException($"A /{nameof(context.Server)} option is required.\n\n{helpGuidance}");

                if (string.IsNullOrWhiteSpace(context.TimeSeries))
                    throw new ExpectedException($"A /{nameof(context.TimeSeries)} option is required.\n\n{helpGuidance}");
            }

            return context;
        }

        private static readonly Regex ArgRegex = new Regex(@"^([/-])(?<key>[^=]+)=(?<value>.*)$", RegexOptions.Compiled);

        private static readonly HashSet<string> HelpKeyWords =
            new HashSet<string>(
                new[] {"?", "h", "help"}
                    .SelectMany(keyword => new[] {"/", "-", "--"}.Select(prefix => prefix + keyword)),
                StringComparer.InvariantCultureIgnoreCase);
        
        private static IEnumerable<string> ResolveOptionsFromFile(string arg)
        {
            if (!arg.StartsWith("@"))
                return new[] { arg };

            var path = arg.Substring(1);

            if (!File.Exists(path))
                throw new ExpectedException($"Options file '{path}' does not exist.");

            return File.ReadAllLines(path)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Where(s => !s.StartsWith("#") && !s.StartsWith("//"));
        }

        private static void SetNgCsvFormat(Context context)
        {
            // Match AQTS 201x Export-from-Springboard CSV format

            // # Take Volume.CS1004@IM974363.EntireRecord.csv generated at 2018-09-14 05:03:15 (UTC-07:00) by AQUARIUS 18.3.79.0
            // # 
            // # Time series identifier: Take Volume.CS1004@IM974363
            // # Location: 20017_CS1004
            // # UTC offset: (UTC+12:00)
            // # Value units: m^3
            // # Value parameter: Take Volume
            // # Interpolation type: Instantaneous Totals
            // # Time series type: Basic
            // # 
            // # Export options: Corrected signal from Beginning of Record to End of Record
            // # 
            // # CSV data starts at line 15.
            // # 
            // ISO 8601 UTC, Timestamp (UTC+12:00), Value, Approval Level, Grade, Qualifiers
            // 2013-07-01T11:59:59Z,2013-07-01 23:59:59,966.15,Raw - yet to be review,200,
            // 2013-07-02T11:59:59Z,2013-07-02 23:59:59,966.15,Raw - yet to be review,200,
            // 2013-07-03T11:59:59Z,2013-07-03 23:59:59,966.15,Raw - yet to be review,200,

            context.CsvDateTimeField = 1;
            context.CsvDateTimeFormat = null;
            context.CsvDateOnlyField = 0;
            context.CsvTimeOnlyField = 0;
            context.CsvValueField = 3;
            context.CsvGradeField = 5;
            context.CsvQualifiersField = 6;
            context.CsvComment = "#";
            context.CsvSkipRows = 0;
            context.CsvIgnoreInvalidRows = true;
            context.CsvRealign = false;
        }

        private static void Set3XCsvFormat(Context context)
        {
            // Match AQTS 3.x Export format

            // ,Take Volume.CS1004@IM974363,Take Volume.CS1004@IM974363,Take Volume.CS1004@IM974363,Take Volume.CS1004@IM974363
            // mm/dd/yyyy HH:MM:SS,m^3,,,
            // Date-Time,Value,Grade,Approval,Interpolation Code
            // 07/01/2013 23:59:59,966.15,200,1,6
            // 07/02/2013 23:59:59,966.15,200,1,6

            context.CsvDateTimeField = 1;
            context.CsvDateTimeFormat = "MM/dd/yyyy HH:mm:ss";
            context.CsvDateOnlyField = 0;
            context.CsvTimeOnlyField = 0;
            context.CsvValueField = 2;
            context.CsvGradeField = 3;
            context.CsvQualifiersField = 0;
            context.CsvComment = null;
            context.CsvSkipRows = 2;
            context.CsvIgnoreInvalidRows = true;
            context.CsvRealign = false;
        }

        private static TEnum ParseEnum<TEnum>(string value) where TEnum : struct
        {
            if (Enum.TryParse<TEnum>(value, true, out var enumValue))
                return enumValue;

            throw new ExpectedException($"'{value}' is not a valid {typeof(TEnum).Name} value.");
        }

        private static string EnumOptions<TEnum>() where TEnum : struct
        {
            return $"One of {string.Join(", ", Enum.GetNames(typeof(TEnum)))}.";
        }

        private static void ParseManualPoints(Context context, double numericValue)
        {
            context.ManualPoints.Add(new TimeSeriesPoint
            {
                Time = context.StartTime,
                Value = numericValue,
                GradeCode = context.GradeCode,
                Qualifiers = context.Qualifiers
            });

            context.StartTime = context.StartTime.Plus(Duration.FromTimeSpan(context.PointInterval));
        }

        private static void ParseManualGap(Context context)
        {
            context.ManualPoints.Add(new TimeSeriesPoint{Type = PointType.Gap});
        }

        private static Instant ParseInstant(string text)
        {
            if (DateTimeOffset.TryParse(text, out var dateTimeOffset))
                return Instant.FromDateTimeOffset(dateTimeOffset);

            throw new ExpectedException($"'{text}' can't be parsed as an unambiguous date time");
        }

        private static Interval ParseInterval(string text)
        {
            var components = text.Split(new[] {'/'}, StringSplitOptions.RemoveEmptyEntries);

            if (components.Length != 2)
                throw new ExpectedException($"'{text}' is an invalid Interval format. Use 'StartInstant/EndInstant' (two ISO8601 timestamps, separated by a forward slash)");

            var start = ParseInstant(components[0]);
            var end = ParseInstant(components[1]);

            if (end < start)
                throw new ExpectedException($"Invalid {nameof(Context.TimeRange)}: Start={start} must be less than or equal to End={end}.");

            return new Interval(start, end);
        }

        private static void ParseExtendedAttributeValue(Context context, string text)
        {
            var components = SplitOnFirstSeparator(text, '=');

            if (components.Length < 2)
                throw new ExpectedException($"'{text}' is not in UPPERCASE_COLUMN_NAME@UPPERCASE_TABLE_NAME=value format.");

            var columnIdentifier = components[0].Trim();
            var value = components[1].Trim();

            context.ExtendedAttributeValues.Add(new ExtendedAttributeValue
            {
                ColumnIdentifier = columnIdentifier,
                Value = value
            });
        }

        private static string[] SplitOnFirstSeparator(string text, params char[] separators)
        {
            return text.Split(separators, 2);
        }

        private static void ParseMappedGrade(Context context, string text)
        {
            var components = SplitOnFirstSeparator(text, ':');

            if (components.Length < 2)
                throw new ExpectedException($"'{text}' is not in sourceValue:mappedValue syntax.");

            var sourceValueText = components[0].Trim();
            var mappedValueText = components[1].Trim();

            int? mappedGrade = null;

            if (!string.IsNullOrEmpty(mappedValueText))
            {
                if (!int.TryParse(mappedValueText, out var mappedValue))
                    throw new ExpectedException($"'{text}' is not in sourceValue:mappedValue syntax.");

                mappedGrade = mappedValue;
            }

            context.GradeMappingEnabled = true;

            if (string.IsNullOrEmpty(sourceValueText))
            {
                context.MappedDefaultGrade = mappedGrade;
                return;
            }

            var ranges = SplitOnFirstSeparator(sourceValueText, ',')
                .Select(s => int.TryParse(s, out var value)
                    ? value
                    : throw new ExpectedException($"'{text}' is not in sourceValue:mappedValue syntax."))
                .OrderBy(value => value)
                .ToList();

            var lowSourceValue = ranges[0];
            var highSourceValue = ranges.Count > 1 ? ranges[1] : lowSourceValue;

            for(var sourceValue = lowSourceValue; sourceValue <= highSourceValue; ++sourceValue)
            {
                context.MappedGrades[sourceValue] = mappedGrade;
            }
        }

        private static void ParseMappedQualifier(Context context, string text)
        {
            var mapping = SplitOnFirstSeparator(text, ':');

            if (mapping.Length < 2)
                throw new ExpectedException($"'{text}' is not in sourceValue:mappedValue syntax.");

            var sourceValue = mapping[0].Trim();
            var mappedValue = mapping[1].Trim();

            if (string.IsNullOrEmpty(mappedValue))
            {
                mappedValue = null;
            }

            context.QualifierMappingEnabled = true;

            if (string.IsNullOrEmpty(sourceValue))
            {
                context.MappedDefaultQualifiers = !string.IsNullOrEmpty(mappedValue)
                    ? new List<string> {mappedValue}
                    : null;
            }
            else
            {
                context.MappedQualifiers[sourceValue] = mappedValue;
            }
        }

        private static Offset ParseOffset(string text)
        {
            try
            {
                var offset = text.FromJson<Offset>();

                return offset;
            }
            // ReSharper disable once EmptyGeneralCatchClause
            catch (Exception)
            {
            }

            var result = OffsetPattern.GeneralInvariantPattern.Parse(text);

            if (result.Success)
                return result.Value;

            throw new ExpectedException($"'{text}' is not a valid UTC offset {result.Exception.Message}");
        }

        private readonly Context _context;

        public Program(Context context)
        {
            _context = context;
        }

        private void Run()
        {
            new PointsAppender(_context)
                .AppendPoints();
        }
    }
}
