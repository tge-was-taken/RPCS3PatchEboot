using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using YamlDotNet.Serialization;
using ELFSharp.ELF;
using ELFSharp.ELF.Segments;

namespace RPCS3PatchEboot
{
    class Program
    {
        private static readonly byte[] ElfMagic = { 0x7F, ( byte ) 'E', ( byte ) 'L', ( byte ) 'F' };
        private static readonly byte[] SceMagic = { ( byte ) 'S', ( byte ) 'C', ( byte ) 'E', 0x00 };

        public static string InEbootPath { get; private set; }

        public static string PatchYamlPath { get; private set; }

        public static string OutEbootPath { get; private set; }

        public static string PPUHashString { get; private set; }

        public static List<string> PatchFilter { get; private set; }

        static void Main( string[] args )
        {
            if ( args.Length == 0 )
            {
                PrintHelp();
                return;
            }

#if !DEBUG
            try
#endif
            {
                if ( !TryParseArguments( args ) )
                {
                    Console.WriteLine( "Error: Failed to parse arguments. Press any key to exit." );
                    Console.ReadKey();
                    return;
                }

                if ( !TryGetBaseOffsetFromEBOOT( out int ebootBaseOffset ) )
                {
                    Console.WriteLine( "Error: Invalid EBOOT. Press any key to exit." );
                    Console.ReadKey();
                    return;
                }

                var parseResult = ParsePatchYaml();
                if ( !parseResult.Success )
                {
                    Console.WriteLine( $"Error: Invalid patch YAML.\nException message:\n{parseResult.Exception.Message}\nPress any key to exit." );
                    Console.ReadKey();
                    return;
                }

                if ( !TryApplyPatchUnitsToEBOOT( ebootBaseOffset, parseResult.Patches ) )
                {
                    Console.WriteLine( "Error: Failed to apply patches to EBOOT. Press any key to exit." );
                    Console.ReadKey();
                    return;
                }
            }
#if !DEBUG
            catch ( Exception exception )
            {
                Console.WriteLine( $"Error: Exception thrown: {exception.Message}" );
                Console.WriteLine( "Stacktrace:" );
                Console.WriteLine( exception.StackTrace );
                Console.WriteLine( "Press any key to exit." );
                Console.ReadKey();
                return;
            }
#endif

            Console.WriteLine( "Success. Patches were applied successfully!" );
        }

        static void PrintHelp()
        {
            Console.WriteLine( "RPCS3PatchEboot version 1.3 by TGE. Please give credit where is due." );
            Console.WriteLine();
            Console.WriteLine( "Info:");
            Console.WriteLine( "    This application applies patches in a RPCS3 patch.yml file and applies it to the EBOOT file directly." );
            Console.WriteLine();
            Console.WriteLine( "Usage:" );
            Console.WriteLine();
            Console.WriteLine( "    <Path to EBOOT> <Path to patch YAML> [path to patched EBOOT output] [Options]" );
            Console.WriteLine();
            Console.WriteLine( "Options:" );
            Console.WriteLine();
            Console.WriteLine( "     -FilterByHash <PPU hash string>\t\t\tInstructs the program to filter patches by PPU hash string in the format of \"PPU-XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX\"." );
            Console.WriteLine( "     -FilterByName  <\"path name 1\" \"patch name 2\" ... >\tInstructs the program to filter patches by name." );
            Console.WriteLine();
            Console.WriteLine( "Notes:" );
            Console.WriteLine();
            Console.WriteLine( "    This only applies to patches following the old format:");
            Console.WriteLine( "    The YAML standard doesn't allow duplicate keys in a YAML map sequence but the RPCS3's YAML parser doesn't care about this." );
            Console.WriteLine( "    Duplicate 'PPU-' hashes won't get detected so be wary of this when you use FilterByHash." );
            Console.ReadKey();
        }

        static bool TryParseArguments( string[] args )
        {
            if ( args.Length < 1 )
            {
                Console.WriteLine( "Error: Missing path to EBOOT." );
                return false;
            }

            InEbootPath = args[0];

            if ( args.Length < 2 )
            {
                Console.WriteLine( "Error: Missing path to patch YAML." );
                return false;
            }

            PatchYamlPath = args[1];

            for ( int i = 2; i < args.Length; i++ )
            {
                var arg = args[i];
                if ( !arg.StartsWith( "-" ) )
                {
                    OutEbootPath = arg;
                }
                else
                {
                    switch ( arg.Substring( 1 ) )
                    {
                        case "FilterByHash":

                            if ( i + 1 == args.Length )
                            {
                                Console.WriteLine( "Missing argument for option FilterByHash" );
                                return false;
                            }

                            PPUHashString = args[++i];
                            break;

                        case "FilterByName":
                            if ( i + 1 == args.Length )
                            {
                                Console.WriteLine( "Missing argument for option FilterByName" );
                                return false;
                            }

                            PatchFilter = new List<string>();

                            while ( ( i != args.Length - 1 ) && ( i < args.Length ) )
                            {
                                var filter = args[++i];

                                if ( filter.StartsWith( "-i" ) )
                                {
                                    --i;
                                    break;
                                }
                                else
                                {
                                    PatchFilter.Add( filter );
                                }
                            }
                            break;
                    }
                }
            }

            if ( OutEbootPath == null )
                OutEbootPath = InEbootPath + ".patched.bin";

            return true;
        }

        static bool TryGetBaseOffsetFromEBOOT( out int baseOffset )
        {
            baseOffset = -1;

            using ( var fileStream = File.OpenRead( InEbootPath ) )
            {
                if ( !CheckMagic( fileStream, ElfMagic ) )
                {
                    if ( !CheckMagic( fileStream, SceMagic) )
                    {
                        Console.WriteLine( "Invalid EBOOT. Did you maybe forget to decrypt it?" );
                        return false;
                    }
                    else
                    {
                        var elfPath = InEbootPath + ".elf";
                        int elfOffset = -1;
                        int elfsFound = 0;

                        while ( (fileStream.Position + ElfMagic.Length) < fileStream.Length )
                        {
                            if ( CheckMagic( fileStream, ElfMagic, false ) )
                            {
                                ++elfsFound;

                                if ( elfsFound == 2 ) // first elf is system stuff, second is the game
                                {
                                    elfOffset = ( int ) ( fileStream.Position - ElfMagic.Length );
                                    break;
                                }
                            }
                        }

                        if ( elfOffset == -1 )
                        {
                            Console.WriteLine( "Invalid EBOOT. Can't find start of ELF data. Did you maybe forget to decrypt it?" );
                            return false;
                        }

                        using ( var elfFileStream = File.Create( elfPath ) )
                        {
                            fileStream.Position = elfOffset;
                            fileStream.CopyTo( elfFileStream );
                        }

                        if ( !TryGetBaseOffsetFromElfFile( elfPath, out baseOffset ) )
                            return false;

                        baseOffset -= elfOffset;
                    }
                }
                else
                {
                    if ( !TryGetBaseOffsetFromElfFile( InEbootPath, out baseOffset ) )
                        return false;
                }
            }

            return true;
        }

        static bool CheckMagic( Stream stream, byte[] magic, bool resetPosition = true )
        {
            var magicBytes = new byte[magic.Length];
            stream.Read( magicBytes, 0, magic.Length );

            bool matches = true;
            for ( int i = 0; i < magicBytes.Length; i++ )
            {
                if ( magicBytes[i] != magic[i] )
                {
                    matches = false;
                    break;
                }
            }

            if ( resetPosition )
                stream.Position -= magic.Length;

            return matches;
        }

        static bool TryGetBaseOffsetFromElfFile( string path, out int baseOffset )
        {
            baseOffset = -1;

            if ( !ELFReader.TryLoad( path, out ELF<ulong> elf ) )
            {
                Console.WriteLine( "Invalid ELF." );
                return false;
            }
            else
            {
                var firstExecSegment = elf.Segments.FirstOrDefault( x => x.Flags.HasFlag( SegmentFlags.Execute ) );

                if ( firstExecSegment == null )
                {
                    Console.WriteLine( "Invalid ELF. No executable segment found." );
                    return false;
                }
                else
                {
                    baseOffset = ( int )( firstExecSegment.Address - (ulong)firstExecSegment.Offset );
                }
            }

            return true;
        }

        static PatchYamlParseResult ParsePatchYaml()
        {
            var result = new PatchYamlParseResult();

            // Set up deserialization
            var input = File.ReadAllText( PatchYamlPath );
            input = input.Replace( "\t", "    " );

            var deserializer = new DeserializerBuilder()
                .IgnoreUnmatchedProperties()
                .Build();

            Dictionary<string, List<List<string>>> yamlMap;

            try
            {
                yamlMap = deserializer.Deserialize<Dictionary<string, List<List<string>>>>( input );
            }
            catch ( Exception e )
            {
                result.Exception = e;
                return result;
            }

            // Read patch units
            var patchUnitMap = new Dictionary<string, PatchUnit>();

            foreach ( var yamlMapEntry in yamlMap )
            {
                if ( yamlMapEntry.Key.StartsWith( "PPU-" ) || yamlMapEntry.Key.StartsWith( "SPU-" ) && PPUHashString != null )
                {
                    // read patch unit
                    if ( yamlMapEntry.Key == PPUHashString )
                    {
                        if ( TryParsePatchUnits( yamlMapEntry, patchUnitMap, out var parsedPatchUnits ) )
                        {
                            result.Patches.AddRange( parsedPatchUnits );
                        }
                    }
                }
                else
                {
                    if ( PatchFilter != null && !PatchFilter.Contains( yamlMapEntry.Key ) )
                        continue;

                    // read patch unit
                    if ( TryParsePatchUnits( yamlMapEntry, patchUnitMap, out var parsedPatchUnits ) && parsedPatchUnits[0] != null )
                    {
                        if ( PPUHashString != null )
                            patchUnitMap[parsedPatchUnits[0].Name] = parsedPatchUnits[0];
                        else
                            result.Patches.Add( parsedPatchUnits[0] );
                    }
                }
            }

            return result;
        }

        static bool TryParseValue( string value, out dynamic parsedValue )
        {
            parsedValue = 0;

            if ( string.IsNullOrWhiteSpace( value ) )
                return false;

            var isNegative = value.StartsWith( "-" );
            var offsetParseString = isNegative ? value.Substring( 1 ) : value;
            if ( offsetParseString.StartsWith( "0x" ) )
            {
                if ( !long.TryParse( offsetParseString.Substring( 2 ), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var longParsedValue ) )
                    return false;

                if ( isNegative )
                    longParsedValue = -longParsedValue;

                parsedValue = longParsedValue;
            }
            else
            {
                if ( !double.TryParse( offsetParseString, NumberStyles.Number, CultureInfo.InvariantCulture, out var doubleParsedValue ) )
                    return false;

                if ( isNegative )
                    doubleParsedValue = -doubleParsedValue;

                parsedValue = doubleParsedValue;
            }

            return true;
        }

        static bool TryParsePatchUnits( KeyValuePair<string, List<List<string>>> yamlMapEntry, Dictionary<string, PatchUnit> patchUnitMap, out List<PatchUnit> patchUnits )
        {
            patchUnits = new List<PatchUnit>();
            var patchUnit = new PatchUnit( yamlMapEntry.Key );
            bool validPatch = true;

            foreach ( var yamlPatch in yamlMapEntry.Value )
            {
                // read patch
                if ( yamlPatch.Count < 2 )
                {
                    Console.WriteLine( $"Error: Patch in patch unit is truncated. Skipping {yamlMapEntry.Key}!" );
                    validPatch = false;
                    break;
                }

                if ( !Enum.TryParse<PatchType>( yamlPatch[0], true, out var patchType ) )
                {
                    Console.WriteLine( $"Error: Unknown patch type. Skipping {yamlMapEntry.Key}" );
                    validPatch = false;
                    break;
                }

                if ( patchType == PatchType.Load )
                {
                    if ( !patchUnitMap.TryGetValue( yamlPatch[1], out patchUnit ) )
                    {
                        Console.WriteLine( $"Error: Could not find patch unit {yamlPatch[1]}" );
                        continue;
                    }

                    if ( yamlPatch.Count > 2 )
                    {
                        if ( TryParseValue( yamlPatch[2], out var offset ) )
                        {
                            Console.WriteLine("Error: Failed to parse patch address offset");
                        }
                        else
                        {
                            // make a copy of the patch
                            var patchUnitCopy = new PatchUnit( patchUnit.Name );
                            patchUnitCopy.Patches.AddRange( patchUnit.Patches );

                            // apply address offset
                            for ( int i = 0; i < patchUnitCopy.Patches.Count; i++ )
                            {
                                var patch = patchUnitCopy.Patches[i];
                                patch.Offset = ( uint )( patch.Offset + offset );

                                patchUnitCopy.Patches[i] = patch;
                            }

                            patchUnit = patchUnitCopy;
                        }
                    }

                    patchUnits.Add( patchUnit );
                }
                else
                {
                    if ( !TryParseValue( yamlPatch[1], out var offset ) )
                    {
                        Console.WriteLine( $"Error: Unable to parse patch offset. Skipping {yamlMapEntry.Key}" );
                        validPatch = false;
                        break;
                    }

                dynamic value;
                if (patchType == PatchType.Utf8)
                {
                    value = yamlPatch[2];
                }
                else
                {
                    if (!TryParseValue(yamlPatch[2], out value))
                    {
                        Console.WriteLine($"Error: Unable to parse patch value. Skipping {yamlMapEntry.Key}");
                        validPatch = false;
                        break;
                }
            }

                    var patch = new Patch( patchType, (uint)offset, value );
                    patchUnit.Patches.Add( patch );
                }
            }

            if ( !patchUnits.Contains( patchUnit ) )
                patchUnits.Add( patchUnit );

            return validPatch;
        }

        static bool TryApplyPatchUnitsToEBOOT( int ebootBaseOffset, List<PatchUnit> patchUnits )
        {
            using ( var inFileStream = File.OpenRead( InEbootPath ) )
            using ( var outFileStream = File.Create( OutEbootPath ) )
            {
                // copy over the entire file before we apply the patches
                inFileStream.CopyTo( outFileStream );

                // apply patches
                foreach ( var patchUnit in patchUnits )
                {
                    Console.WriteLine( $"Applying patch unit {patchUnit.Name}" );

                    foreach ( var patch in patchUnit.Patches )
                    {
                        outFileStream.Position = patch.Offset - (uint)ebootBaseOffset;
                        var valueStr = Math.Truncate((double)patch.Value) == patch.Value ? ((ulong)patch.Value).ToString("X8") : patch.Value.ToString();
                        Console.WriteLine( $"{outFileStream.Position:X8} -> {valueStr} ({patch.Type})" );

                        byte[] valueBuffer = null;
                        bool reverse = false;
                        switch ( patch.Type )
                        {
                            case PatchType.Byte:
                                valueBuffer = new[] { ( byte )patch.Value };
                                break;
                            case PatchType.Le16:
                                valueBuffer = BitConverter.GetBytes( ( ushort )patch.Value );
                                break;
                            case PatchType.Le32:
                            case PatchType.LeF32:
                                if ( patch.Type == PatchType.Le32 ) valueBuffer = BitConverter.GetBytes( ( uint )patch.Value );
                                else valueBuffer = BitConverter.GetBytes( ( float )patch.Value );
                                break;
                            case PatchType.Le64:
                            case PatchType.LeF64:
                                if ( patch.Type == PatchType.Le64 ) valueBuffer = BitConverter.GetBytes( ( ulong )patch.Value );
                                else valueBuffer = BitConverter.GetBytes( ( double )patch.Value );
                                break;
                            case PatchType.Be16:
                                valueBuffer = BitConverter.GetBytes( ( ushort )patch.Value );
                                reverse = true;
                                break;
                            case PatchType.Be32:
                            case PatchType.BeF32:
                                if ( patch.Type == PatchType.Be32 ) valueBuffer = BitConverter.GetBytes( ( uint )patch.Value );
                                else valueBuffer = BitConverter.GetBytes( ( float )patch.Value );
                                reverse = true;
                                break;
                            case PatchType.Be64:
                            case PatchType.Utf8:
                                valueBuffer = System.Text.Encoding.UTF8.GetBytes((string)patch.Value);
                                break;
                            case PatchType.BeF64:
                                if ( patch.Type == PatchType.Be64 ) valueBuffer = BitConverter.GetBytes( ( ulong )patch.Value );
                                else valueBuffer = BitConverter.GetBytes( ( double )patch.Value );
                                reverse = true;
                                break;
                            default:
                                Console.WriteLine( $"Unknown patch type: {patch.Type}" );
                                return false;
                        }

                        if ( reverse )
                        {
                            for ( int i = valueBuffer.Length - 1; i >= 0; i-- )
                                outFileStream.WriteByte( valueBuffer[i] );
                        }
                        else
                        {
                            for ( int i = 0; i < valueBuffer.Length; i++ )
                                outFileStream.WriteByte( valueBuffer[i] );
                        }
                    }
                }
            }

            return true;
        }
    }
}
