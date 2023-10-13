using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Perftools.Profiles;

namespace dd_pprof
{
    class pprofWrapper
    {
        public pprofWrapper(Profile profile)
        {
            _profile = profile;
            LoadStringTable();
            LoadValueTypes();
            LoadFunctions();
            LoadLocations();
        }

        public string GetString(long id)
        {
            if (_stringTable.TryGetValue(id, out var value))
            {
                return value;
            }

            return $"?{id}";
        }

        public string GetValueName(int pos)
        {
            if (_sampleTypes.Count <= pos)
            {
                return $"v#{pos}";
            }

            return _sampleTypes[pos];
        }

        public string GetFunction(int id)
        {
            if (_functions.Count < id)
            {
                return $"f#{id}";
            }

            return _functions[id-1];
        }

        public string GetLocation(ulong location)
        {
            if (_locations.Count < (int)location)
            {
                return $"l#{location}";
            }

            return _locations[(int)location-1];
        }

        private void LoadStringTable()
        {
            var stringsCount = _profile.StringTable.Count;
            _stringTable = new Dictionary<long, string>(stringsCount);

            var current = 0;
            foreach (var entry in _profile.StringTable)
            {
                _stringTable[current] = entry;
                current++;
            }
        }

        private void LoadValueTypes()
        {
            _sampleTypes = new List<string>(_profile.SampleType.Count);
            foreach (var entry in _profile.SampleType)
            {
                _sampleTypes.Add(GetString(entry.Type));
            }
        }

        private void LoadFunctions()
        {
            _functions = new List<string>(_profile.Function.Count);
            foreach (var function in _profile.Function)
            {
                _functions.Add(GetString(function.Name));
            }
        }

        private void LoadLocations()
        {
            _locations = new List<string>(_profile.Location.Count);
            foreach (var entry in _profile.Location)
            {
                // TODO: fetch other interesting fields (Address, list<lines>, Id)
                _locations.Add(GetFunction((int)entry.Line[0].FunctionId));
            }
        }

        private Profile _profile;
        private Dictionary<long, string> _stringTable;
        private List<string> _sampleTypes;
        private List<string> _locations;
        private List<string> _functions;
    }


    class Program
    {
        static int Main(string[] args)
        {
            ShowHeader();

            (string filename, bool showAll) parameters;
            try
            {
                parameters = GetParameters(args);
            }
            catch (InvalidOperationException x)
            {
                Console.WriteLine(x.Message);
                ShowHelp();
                return -1;
            }

            try
            {
                DumpPProfEx(parameters.filename, parameters.showAll);
            }
            catch (Exception x)
            {
                Console.WriteLine(x.ToString());
                return -2;
            }

            return 0;
        }

        static void DumpPProf(Profile profile, bool showAll)
        {
            var wrapper = new pprofWrapper(profile);
            Console.WriteLine($"{profile.SampleType.Count} sample types");
            Console.WriteLine("-----------------------");
            foreach (var type in profile.SampleType)
            {
                Console.WriteLine($"{wrapper.GetString(type.Type),12} | {wrapper.GetString(type.Unit)}");
            }
            Console.WriteLine();

            Console.WriteLine($"{profile.Sample.Count} samples");
            Console.WriteLine("----------------------------------------------");
            foreach (var sample in profile.Sample)
            {
                // show values with their type name
                var valuePos = 0;
                var valueCount = sample.Value.Count;
                var values = new List<long>();  // non 0 values
                foreach (var value in sample.Value)
                {
                    if (value != 0)
                    {
                        values.Add(value);
                        Console.Write($"{wrapper.GetValueName(valuePos),12}");
                        Console.Write(" ");
                    }
                    valuePos++;
                }
                Console.WriteLine();
                foreach (var val in values)
                {
                    Console.Write($"{val,12} ");
                }
                Console.WriteLine();

                // show labels
                var pos = 0;
                var labelsCount = sample.Label.Count;
                if (labelsCount == 0)
                {
                    Console.Write("[no label]");
                }
                else
                {
                    foreach (var label in sample.Label)
                    {
                        var key = wrapper.GetString(label.Key);
                        if (!string.IsNullOrEmpty(key))
                        {
                            Console.Write($"{key} = '{wrapper.GetString(label.Str)}'");
                            if (pos + 1 < labelsCount)
                            {
                                Console.Write(" | ");
                            }
                        }

                        pos++;
                    }
                }
                Console.WriteLine();

                // show callstack
                Console.WriteLine();
                Console.WriteLine("Location  Frame");
                foreach (var frame in sample.LocationId)
                {
                    Console.WriteLine($"    {frame,4}  {wrapper.GetLocation(frame)}");
                }

                Console.WriteLine();
                Console.WriteLine();
            }

            if (!showAll)
            {
                return;
            }

            Console.WriteLine($"{profile.Mapping.Count} mapping");
            Console.WriteLine("-----------------------");
            Console.WriteLine("   #   Id build Filename");
            var currentMapping = 0;
            foreach (var mapping in profile.Mapping)
            {
                Console.WriteLine($"{currentMapping,4} {mapping.Id,4}  {mapping.BuildId,4} {wrapper.GetString(mapping.Filename)}");
                currentMapping++;
            }
            Console.WriteLine();

            Console.WriteLine($"{profile.Location.Count} locations");
            Console.WriteLine("-----------------------");
            Console.WriteLine("  ID Mapping    Address  Lines");
            foreach (var location in profile.Location)
            {
                Console.Write($"{location.Id,4}    {location.MappingId,4}   {location.Address,8}   ");
                var currentLine = 0;
                foreach (Line line in location.Line)
                {
                    if (currentLine == 0)
                    {
                        Console.WriteLine($"{location.Line[0].FunctionId,3} - {wrapper.GetFunction((int)location.Line[0].FunctionId)}");
                    }
                    else
                    {
                        Console.WriteLine($"                         {line.FunctionId,3} - {wrapper.GetFunction((int)location.Line[currentLine].FunctionId)}");
                    }

                    currentLine++;
                }
            }
            Console.WriteLine();

            Console.WriteLine($"{profile.Function.Count} functions");
            Console.WriteLine("-----------------------");
            Console.WriteLine("   #    ID  Name");
            var currentFunction = 0;
            foreach (var function in profile.Function)
            {
                Console.WriteLine($"{currentFunction,4}  {function.Id,4}  {wrapper.GetString(function.Name)}");
                currentFunction++;
            }
            Console.WriteLine();

            Console.WriteLine($"{profile.StringTable.Count} strings");
            Console.WriteLine("-----------------------");
            Console.WriteLine("   #  String");
            var currentString = 0;
            foreach (var str in profile.StringTable)
            {
                Console.WriteLine($"{currentString,4}  {str}");
                currentString++;
            }
            Console.WriteLine();
        }

        static void DumpPProfEx(string filename, bool showAll)
        {
            PProfFile wrapper = new();
            if (!wrapper.Load(filename))
            {
                return;
            }

            TimeSpan ts = new TimeSpan(0, 0, 0, 0, (int)(wrapper.DurationNS / 1000000));
            Console.WriteLine($"Duration: {ts.TotalSeconds} s");
            Console.WriteLine();

            Console.WriteLine($"{wrapper.ValueTypes.Count()} value types");
            Console.WriteLine("-----------------------");
            foreach (var type in wrapper.ValueTypes)
            {
                Console.WriteLine($"{type.Name,14} | {type.Unit}");
            }
            Console.WriteLine();

            Console.WriteLine($"{wrapper.Samples.Count()} samples");
            Console.WriteLine("----------------------------------------------");
            foreach (var sample in wrapper.Samples)
            {
                // show values with their type name
                var valuePos = 0;
                var valueCount = sample.Values.Count();
                var values = new List<long>();  // non 0 values
                foreach (var value in sample.Values)
                {
                    if (value != 0)
                    {
                        values.Add(value);
                        Console.Write($"{wrapper.GetValueName(valuePos),12}");
                        Console.Write(" ");
                    }
                    valuePos++;
                }
                Console.WriteLine();
                foreach (var val in values)
                {
                    Console.Write($"{val,12} ");
                }
                Console.WriteLine();

                // show labels
                var pos = 0;
                var labelsCount = sample.Labels.Count();
                if (labelsCount == 0)
                {
                    Console.Write("[no label]");
                }
                else
                {
                    foreach (var label in sample.Labels)
                    {
                        Console.Write($"{label.Key} = '{label.Value}'");
                        if (pos + 1 < labelsCount)
                        {
                            Console.Write(" | ");
                        }

                        pos++;
                    }
                }
                Console.WriteLine();

                // show callstack
                Console.WriteLine();
                Console.WriteLine("Location  Frame");
                foreach (var location in sample.Locations)
                {
                    int framesCount = location.Frames.Count();
                    int currentFrame = 0;
                    foreach (var frame in location.Frames)
                    {
                        if (currentFrame == 0)
                        {
                            if (frame.IsInlined)
                            {
                                Console.WriteLine($"    {location.Id,4}  *{frame.Name}");
                            }
                            else
                            {
                                Console.WriteLine($"    {location.Id,4}  {frame.Name}");
                            }
                        }
                        else
                        {
                            if (frame.IsInlined)
                            {
                                Console.WriteLine($"          *{frame.Name}");
                            }
                            else
                            {
                                Console.WriteLine($"          {frame.Name}");
                            }
                        }
                    }
                }

                Console.WriteLine();
                Console.WriteLine();
            }

            if (!showAll)
            {
                return;
            }

            Console.WriteLine($"{wrapper.Mappings.Count()} mappings");
            Console.WriteLine("-----------------------");
            Console.WriteLine("   #   Id build Filename");
            var currentMapping = 0;
            foreach (var mapping in wrapper.Mappings)
            {
                Console.WriteLine($"{currentMapping,4} {mapping.Id,4}  {mapping.BuildId,4} {mapping.Filename}");
                currentMapping++;
            }
            Console.WriteLine();

            Console.WriteLine($"{wrapper.Locations.Count()} locations");
            Console.WriteLine("-----------------------");
            Console.WriteLine("  ID Mapping    Address  Lines");
            foreach (var location in wrapper.Locations)
            {
                Console.Write($"{location.Id,4}    {location.MappingId,4}   {location.Address,8}   ");
                var currentLine = 0;
                foreach (var frame in location.Frames)
                {
                    if (!frame.IsInlined)
                    if (currentLine == 0)
                    {
                        Console.WriteLine($"{frame.Id,3} - {frame.Name}");
                    }
                    else
                    {
                        Console.WriteLine($"                         {frame.Id,3} - {frame.Name}");
                    }

                    currentLine++;
                }
            }
            Console.WriteLine();

            Console.WriteLine($"{wrapper.Functions.Count()} functions");
            Console.WriteLine("-----------------------");
            Console.WriteLine("   #    ID  Name");
            var currentFunction = 0;
            foreach (var function in wrapper.Functions)
            {
                Console.WriteLine($"{currentFunction,4}  {function.Id,4}  {function.Name}");
                currentFunction++;
            }
            Console.WriteLine();

            Console.WriteLine($"{wrapper.StringTable.Count()} strings");
            Console.WriteLine("-----------------------");
            Console.WriteLine("   #  String");
            var currentString = 0;
            foreach (var str in wrapper.StringTable)
            {
                Console.WriteLine($"{currentString,4}  {str}");
                currentString++;
            }
            Console.WriteLine();
        }

        private static (string filename, bool showAll) GetParameters(string[] args)
        {
            if (args.Length < 1)
            {
                throw new InvalidOperationException("Missing parameters");
            }

            (string filename, bool showAll) parameters = (filename: null, showAll: false);
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].ToLower() == "-a")
                {
                    parameters.showAll = true;
                }
                else
                if (!string.IsNullOrEmpty(parameters.filename))
                {
                    throw new InvalidOperationException(".pprof filename cannot be set more than once");
                }
                else
                {
                    parameters.filename = args[i];
                }

            }

            if (string.IsNullOrEmpty(parameters.filename))
            {
                throw new InvalidOperationException("Missing .pprof filename");
            }
            else
            {
                if (!File.Exists(parameters.filename))
                {
                    throw new InvalidOperationException($".pprof file '{parameters.filename}' does not exist");
                }
            }

            return parameters;
        }

        private static void ShowHeader()
        {
            var name = System.Reflection.Assembly.GetExecutingAssembly().GetName();
            Console.WriteLine($"dotnet-pprof v{name.Version.Major}.{name.Version.Minor}.{name.Version.Build} - .pprof dumper");
            Console.WriteLine("by Christophe Nasarre");
            Console.WriteLine();
        }
        private static void ShowHelp()
        {
            Console.WriteLine();
            Console.WriteLine("dotnet-pprof shows what's inside a .pprof file.");
            Console.WriteLine("Usage: dotnet-pprof <.pprof file path>");
            Console.WriteLine("   Ex: dotnet-pprof c:\\pprof\\myprofile.pprof      Show only samples");
            Console.WriteLine("   Ex: dotnet-pprof c:\\pprof\\myprofile.pprof -a   Show all .pprof content");
            Console.WriteLine();
        }
    }
}
