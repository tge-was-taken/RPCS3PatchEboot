﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using YamlDotNet.Serialization;
using ELFSharp.ELF;
using ELFSharp.ELF.Sections;

namespace RPCS3PatchEboot
{
    class Program
    {
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

            try
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

                if ( !TryParsePatchYaml( out var patchUnits ) )
                {
                    Console.WriteLine( "Error: Invalid patch YAML. Press any key to exit." );
                    Console.ReadKey();
                    return;
                }

                if ( !TryApplyPatchUnitsToEBOOT( ebootBaseOffset, patchUnits ) )
                {
                    Console.WriteLine( "Error: Failed to apply patch units to EBOOT. Press any key to exit." );
                    Console.ReadKey();
                    return;
                }
            }
            catch ( Exception exception )
            {
#if DEBUG
                throw exception;
#endif
                Console.WriteLine( $"Error: Exception thrown: {exception.Message}" );
                Console.WriteLine( "Stacktrace:" );
                Console.WriteLine( exception.StackTrace );
                Console.WriteLine( "Press any key to exit." );
                Console.ReadKey();
                return;
            }

            Console.WriteLine( "Success. Patches were applied successfully!" );
        }

        static void PrintHelp()
        {
            Console.WriteLine( "RPCS3PatchEboot version 1.1 by TGE. Please give credit where is due." );
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
                                Console.WriteLine( "Missing argument for option FilterByHash" );
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
                if ( !CheckMagic( fileStream, new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' } ) )
                {
                    if ( !CheckMagic( fileStream, new byte[] { (byte)'S', ( byte )'C', ( byte )'E', 0x00 } ) )
                    {
                        Console.WriteLine( "Invalid EBOOT. Did you maybe forget to decrypt it?" );
                        return false;
                    }
                    else
                    {
                        var elfPath = InEbootPath + ".elf";
                        using ( var elfFileStream = File.Create( elfPath ) )
                        {
                            fileStream.Position = 0x980;
                            fileStream.CopyTo( elfFileStream );
                        }

                        if ( !TryGetBaseOffsetFromElfFile( elfPath, out baseOffset ) )
                            return false;

                        baseOffset -= 0x980;
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

        static bool CheckMagic( Stream stream, byte[] magic )
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

            stream.Position = 0;

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
                var firstCodeSection = elf.GetSections<Section<ulong>>().FirstOrDefault( x => x.Type == SectionType.ProgBits );
                if ( firstCodeSection == null )
                {
                    Console.WriteLine( "Invalid ELF. No code (ProgBits) section found." );
                    return false;
                }
                else
                {
                    baseOffset = ( int )( firstCodeSection.LoadAddress - firstCodeSection.Offset );
                }
            }

            return true;
        }

        static bool TryParsePatchYaml( out List<PatchUnit> patchUnits )
        {
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
                patchUnits = null;
                return false;
            }

            // Read patch units
            patchUnits = new List<PatchUnit>();
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
                            patchUnits.AddRange( parsedPatchUnits );
                        }
                    }
                }
                else
                {
                    if ( PatchFilter != null && !PatchFilter.Contains( yamlMapEntry.Key ) )
                        continue;

                    // read patch unit
                    if ( TryParsePatchUnits( yamlMapEntry, patchUnitMap, out var parsedPatchUnits ) )
                    {
                        if ( PPUHashString != null )
                            patchUnitMap[parsedPatchUnits[0].Name] = parsedPatchUnits[0];
                        else
                            patchUnits.Add( parsedPatchUnits[0] );
                    }
                }
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
                        bool isNegative = yamlPatch[2][0] == '-';
                        string offsetParseString = isNegative ? yamlPatch[2].Substring( 3 ) : yamlPatch[2].Substring( 2 );

                        if ( !int.TryParse( offsetParseString, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int offset ) )
                        {
                            Console.WriteLine("Error: Failed to parse patch address offset");
                        }
                        else
                        {
                            if ( isNegative )
                            {
                                offset = -offset;
                            }

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
                    if ( !uint.TryParse( yamlPatch[1].Substring( 2 ), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var offset ) )
                    {
                        Console.WriteLine( $"Error: Unable to parse patch offset. Skipping {yamlMapEntry.Key}" );
                        validPatch = false;
                        break;
                    }

                    if ( !ulong.TryParse( yamlPatch[2].Substring( 2 ), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value ) )
                    {
                        Console.WriteLine( $"Error: Unable to parse patch value. Skipping {yamlMapEntry.Key}" );
                        validPatch = false;
                        break;
                    }

                    var patch = new Patch( patchType, offset, value );
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
                        outFileStream.Position = ( patch.Offset - ebootBaseOffset );
                        Console.WriteLine( $"{outFileStream.Position:X8} -> {patch.Value:X8} ({patch.Type})" );

                        switch ( patch.Type )
                        {
                            case PatchType.Byte:
                                outFileStream.WriteByte( ( byte )patch.Value );
                                break;
                            case PatchType.Le16:
                                outFileStream.WriteByte( ( byte )( patch.Value ) );
                                outFileStream.WriteByte( ( byte )( patch.Value >> 8 ) );
                                break;
                            case PatchType.Le32:
                            case PatchType.LeF32:
                                outFileStream.WriteByte( ( byte )( patch.Value ) );
                                outFileStream.WriteByte( ( byte )( patch.Value >> 08 ) );
                                outFileStream.WriteByte( ( byte )( patch.Value >> 16 ) );
                                outFileStream.WriteByte( ( byte )( patch.Value >> 24 ) );
                                break;
                            case PatchType.Le64:
                            case PatchType.LeF64:
                                outFileStream.WriteByte( ( byte )( patch.Value ) );
                                outFileStream.WriteByte( ( byte )( patch.Value >> 08 ) );
                                outFileStream.WriteByte( ( byte )( patch.Value >> 16 ) );
                                outFileStream.WriteByte( ( byte )( patch.Value >> 24 ) );
                                outFileStream.WriteByte( ( byte )( patch.Value >> 32 ) );
                                outFileStream.WriteByte( ( byte )( patch.Value >> 40 ) );
                                outFileStream.WriteByte( ( byte )( patch.Value >> 48 ) );
                                outFileStream.WriteByte( ( byte )( patch.Value >> 56 ) );
                                break;
                            case PatchType.Be16:
                                outFileStream.WriteByte( ( byte )( patch.Value >> 8 ) );
                                outFileStream.WriteByte( ( byte )( patch.Value ) );
                                break;
                            case PatchType.Be32:
                            case PatchType.BeF32:
                                outFileStream.WriteByte( ( byte )( patch.Value >> 24 ) );
                                outFileStream.WriteByte( ( byte )( patch.Value >> 16 ) );
                                outFileStream.WriteByte( ( byte )( patch.Value >> 08 ) );
                                outFileStream.WriteByte( ( byte )( patch.Value ) );
                                break;
                            case PatchType.Be64:
                            case PatchType.BeF64:
                                outFileStream.WriteByte( ( byte )( patch.Value >> 56 ) );
                                outFileStream.WriteByte( ( byte )( patch.Value >> 48 ) );
                                outFileStream.WriteByte( ( byte )( patch.Value >> 40 ) );
                                outFileStream.WriteByte( ( byte )( patch.Value >> 32 ) );
                                outFileStream.WriteByte( ( byte )( patch.Value >> 24 ) );
                                outFileStream.WriteByte( ( byte )( patch.Value >> 16 ) );
                                outFileStream.WriteByte( ( byte )( patch.Value >> 08 ) );
                                outFileStream.WriteByte( ( byte )( patch.Value ) );
                                break;
                            default:
                                Console.WriteLine( $"Unknown patch type: {patch.Type}" );
                                return false;
                        }
                    }
                }
            }

            return true;
        }
    }
}
