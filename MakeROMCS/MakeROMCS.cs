//
// MakeROMCS - A C# drop-in replacement for XFlash's "romfile.exe"
// Feb 2020 - github.com/JonathanDotCel
//
// MakeROMCS packages up all the .ROM files in the current directory for use with XFlash
// It is intended to be used as a 1:1 replacement for romfile.exe, with some new options.
// 
// GPL BEER LICENSE.
// It's the same as the GPL3, but if you fork it, you must send me a beer and leave the original headers.
// As an addition, only add credits; never remove.
//
// 
//

// Little over-engineered but gives us room to grow.
// 
// Basic layout:
// 
// Header size = 0x2000 / 8kb
//
// 0x40 / 64d bytes per entry
// 0x04 = length
// 0x04 = offset
// 0x04 = checksum
// 0x04 = unused
// 0x2D = description (45 chars) **
// 0x03 = wasted zeros
//
// ** Unirom / XFlash will attempt to load 50, but only 45 are ever written 
// https://i.imgur.com/x8zs4Ba.png
//
// So we can have a max of 128 ROMs per file.
// Please take a look at the Unirom8 or XFlash source for a more information.
//


#define DEBUG_ARGS

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;



namespace MakeROMCS {
	
	// Cache this info as we verify each ROM
	// each is padded to a 0x800/2kb sector boundary
	[System.Serializable]
	class ROMEntry{
		
		// Header Entries
		public UInt32 actualLength;		// how many actual bytes
		public UInt32 offset;			// how many BYTES into ROMFILE.DAT
		public UInt32 checkSum;			// nothing fancy - adding the bytes

		// bytes		
		public byte[] paddedData;		// padded data since it requires a new array anyway
		public UInt32 paddedLength;		// filesize padded to 0x800 / 2kb

		// meta
		public string fileName;			// the actual file name
		public string internalName;		// ROM's internal identifier

	}

	class MakeROMCS {
		
		// All nice n uppercase for CDXA(Mode 2)
		const string DEFAULT_FILENAME = "ROMFILE.DAT";
		const string VERSION = "0.4";

		const int FILENAME_LIMIT = 45;
		const int HEADER_SIZE = 0x2000;
		const UInt32 SECTOR_SIZE = 0x800;

		// sort by the ROM's internal name? (same as original way)
		static bool useInternalNameSort = false;
		static string targetFilename = "";
		
		static int numWarnings = 0;

		static List<string> romFileNames;
		static List<ROMEntry> roms;
		
		static bool VerifyArgs( string[] inArgs ){
			
			// Linq <3
			List<string> remainingArgs = inArgs.ToList();

			// 0 args is valid
			if ( inArgs.Length == 0 ){
				return true;
			}

			// go through the args ticking them off
			for( int i = remainingArgs.Count -1; i >= 0; i-- ){
				
				string s = remainingArgs[i].ToUpperInvariant();

				// internalNameSort
				if ( s == "/I" || s == "--I" || s == "-I" ){
					
					// did they already specify this arg?
					if ( useInternalNameSort ){
						PrintError( "Arg specified twice: /i" );
						return false;
					}
						
					useInternalNameSort = true;
					remainingArgs.RemoveAt( i );

				} // arg /I

				// if we need to add more args in the future
				// e.g. expanded mode or something

				if ( s == "/F" || s == "--F" || s == "-F" ){
					
					if ( !string.IsNullOrEmpty( targetFilename ) ){
						// already specified this	
						PrintError( "Arg specified twice: /f (filename)" );
						return false;
					}
							
					// remove the /f switch, and the next thing should be the file name.
					remainingArgs.RemoveAt( i );

					if ( i >= remainingArgs.Count ){
						PrintError( "/f specified for filename, but no file name given!" );
						return false;
					}

					// next arg should be the filename
					targetFilename = remainingArgs[ i ];					
					remainingArgs.RemoveAt( i );

					continue;
					
				}


			} // for


			// User has left something we don't understand on the commandline
			if ( remainingArgs.Count > 0 ){
				for( int i = 0; i < remainingArgs.Count; i++ ){
					PrintError( "Unknown arg: " + remainingArgs[i] );
				}
				return false;
			}


			// all set
			return true;

		}
		
		static bool VerifyROMS(){
			
			// Get a list of .ROM files + Verify
			string currentDirectory = "";

			try{
				currentDirectory = Directory.GetCurrentDirectory();
			} catch ( Exception e ){
				// UnathorisedAccessException or NotSupportedException officially
				// but best to print it anyway.
				PrintError( "Exception: " + e );
				return false;
			}

			// instead of maintaining separate arrays, let's stick it in a list
			// and cross them off one by one			
			romFileNames = new List<string>();

			try{				
				// C# really spoils us sometimes...				
				romFileNames = Directory.GetFiles( currentDirectory ).OrderByDescending( f => f ).ToList<string>();
			} catch ( Exception e ){
				// Again, a bunch of possible errors, but given
				// the low importance, let's just print it and move on.
				PrintError( "Exception " + e );
				return false;
			}

			// Filter by extension
			// Todo: add .bin?

			for( int i = romFileNames.Count -1; i >= 0; i-- ){
				
				if ( !romFileNames[i].ToUpperInvariant().EndsWith( ".ROM" ) ){
					romFileNames.RemoveAt(i );
					continue;
				}

				Console.WriteLine( "Found: " + Path.GetFileNameWithoutExtension( romFileNames[i] ) );
				
			}

			if ( romFileNames.Count == 0 ){
				PrintError( "Didn't find any ROMs!" );
				return false;
			}
			if ( romFileNames.Count > 128 ){
				PrintError( "Sorry, the limit is 128 ROM files!" );
				return false;
			}

			// Init the list of byte arrays
			roms = new List<ROMEntry>();

			// we can keep a running tally of this now
			UInt32 romOffset = HEADER_SIZE;

			// Filter by what is actually a ROM
			// since we're already opening them to check, hold them in RAM
			for( int i = romFileNames.Count -1; i >= 0; i-- ){
				
				Console.WriteLine( "\nChecking: " + romFileNames[i] );

				byte[] romData;

				try{
					romData = File.ReadAllBytes( romFileNames[i] );
				} catch( Exception e ){
					PrintError( "Exception reading file: " + romFileNames[i] );
					PrintError( "The exception returned was: " + e );
					PrintError( "Skipping that file..." );
					continue;
				}

				// let's verify the header
				// the first header is optional at 0x04, the original romfile.exe did not take account of this
				// instead we'll be using the second header at 0x84 - 0xB0 (exclusive)
				// which should read "Licensed by Sony Computer Entertainment Inc."
				// checksum it.
				
				// technically there could be code in just the header gaps
				// nocash has put code there, paul, and smartcart patches like Ahoy too
				if ( romData.Length < 0xB0 ){
					PrintError( "Not a valid rom! " + romFileNames[i] );
					continue;
				}

				UInt32 checkSum = 0;
				for( int b = 0x84; b < 0xB0; b++ ){
					checkSum += romData[b];
				}

				#if DEBUG_ARGS
				Console.WriteLine( "    License checksum is 0x" + checkSum.ToString("X4") );
				#endif

				if ( checkSum != 0x1040 ){
					PrintWarning( "Invalid license data! " + romFileNames[i] );
					PrintWarning( "(file will be skipped!" );
					continue;
				}

				UInt32 paddedLength = (uint)romData.Length;
				if ( romData.Length % SECTOR_SIZE > 0 ){
					paddedLength += SECTOR_SIZE - ( paddedLength % SECTOR_SIZE );
				}

				byte[] paddedData = new byte[ paddedLength ];

				Console.Write( 
					"    Padded size from 0x" + romData.Length.ToString("X8") + " to 0x" + paddedLength.ToString("X8") + "\n"
				);

				// seems sound enough, let's checksum the whole thing
				// while copying into a 2kb boundary aligned array for the CD
				checkSum = 0;
				for( int b = 0; b < paddedData.Length; b++ ){
					
					if ( b < romData.Length ){
						checkSum += romData[b];
						paddedData[b] = romData[b];
					} else {
						// just to emphasize
						paddedData[b] = 0;
					}
					
				}

				

				#if DEBUG_ARGS
				Console.WriteLine( "    File checksum is 0x" + checkSum.ToString("X8") );
				#endif

				// So not portable,but look how neat it is.
				string fileName = Path.GetFileNameWithoutExtension( romFileNames[i] );
				fileName = fileName.Replace( "[!]", "" );	// For SquareSoft74's sanity
				fileName = fileName.Substring( 0, Math.Min( FILENAME_LIMIT, fileName.Length ) );


				string internalName = GetInternalName( fileName, romData );
				internalName = internalName.Substring( 0, Math.Min( FILENAME_LIMIT, internalName.Length ) );

				// not a fan of this setup as it makes searching for ".fileName" a little 
				// awkward, but equally I hate huge constructors. Thoughts?
				ROMEntry rom = new ROMEntry(){
					
					actualLength = (UInt32)romData.Length,
					offset = romOffset,  // haven't sorted the list so we don't know this yet
					checkSum = checkSum,

					paddedData = paddedData,
					paddedLength = (UInt32)paddedData.Length,
					
					fileName = fileName,
					internalName = internalName

				};
				
				romOffset += paddedLength;
				roms.Add( rom );
								
			}

			if ( roms.Count == 0 ){
				PrintError( "None of those ROMs were valid!" );
				return false;
			}

			return true;

		}
		
		// Write an int/uint into to a byte array (at a given offset)
		static UInt32 WriteLittleEndian( UInt32 inValue, byte[] inStream, UInt32 inOffset ){
			
			// if porting to C, etc obvs just cast it.
			byte[] bytes = BitConverter.GetBytes( inValue );
			for( int i = 0; i < 4; i++ ){
				inStream[ inOffset + i ] = bytes[ i ];
			}

			return inOffset + 4;

		}
		
		// should be UInts, but C# array access is signed, so it doesn't rightly matter.
		static int FindSequence( string inString, byte[] inBytes ){
			
			for( int i = 0; i < inBytes.Length - inString.Length; i++ ){
				
				// heh gotta be future proof for those 4gb strings...
				for( int j = 0; j < inString.Length; j++ ){

					if( inBytes[ i + j ] != inString[ j ] ) {
						goto noMatch;
					}

				}

				// whole sequence matched!
				return i;

				// TODO: why do c# gotos require a sacrificial var?
				noMatch:
				int x = 0;

			}

			return -1;

		}
		
		static string GetInternalName( string inDefault, byte[] inBytes ){
			
			int offset = FindSequence( "release", inBytes );

			if ( offset == -1 ){
				
				#if DEBUG_ARGS
				Console.Write( "    No release info found!" );
				#endif
				return inDefault;

			}

			#if DEBUG_ARGS
			Console.Write( "    Release info found at: 0x" + offset.ToString("X8") );
			#endif

			// really good way to waste RAM but the program
			// won't even be open long enough for the GC
			string returnString = "";
			for( int i = 0; i < 32; i++ ){
				returnString += (char)inBytes[ offset + i ];
			}

			char languageByte = (char)inBytes[ offset + 33 ];
			#if DEBUG_ARGS
			Console.Write( "    Language byte: \"" + languageByte + "\"" );
			#endif

			// only seen Dutch, German, Spanish, English(EU/UK), Japanese, USA
			switch( languageByte ){
				
				case 'D': returnString += "(Dutch)"; break;
				case 'F': returnString += "(French)"; break;
				case 'G': returnString += "(German)"; break;
				case 'I': returnString += "(Italian)"; break;
				case 'J': returnString += "(Japanese)"; break;
				case 'U': returnString += "(USA)"; break;
				default: returnString += "(English)"; break;

			}

			#if DEBUG_ARGS
			Console.Write( "    Release Info: " + returnString );
			#endif

			return returnString;

		}

		// gathered enough data, let's go
		static bool BuildROMS(){
			
			// how big is our output array?
			uint finalSize = HEADER_SIZE;  //8kb

			// add the ROM files too..
			for( int i = 0; i < roms.Count; i++ ){
				finalSize += roms[i].paddedLength;
			}

			Console.WriteLine( "\nBuilding index...\n" );

			#if DEBUG_ARGS
			Console.WriteLine( "\n Final Size: " + finalSize + " (0x" + finalSize.ToString("X8")+ ")\n" );
			#endif
			
			// Not going to use a binary writer here to keep the code as portable as possible

			byte[] writeBytes = new byte[ finalSize ];

			// Better keeping separate vars for both than inlining it
			// for debugging
			uint headerOffset = 0;
			uint dataOffset = 0;
			
			for( int i = 0; i < roms.Count; i++ ){
				
				ROMEntry rom = roms[i];

				// 0x00 to 0x10 -> Header
				headerOffset = WriteLittleEndian( rom.actualLength, writeBytes, headerOffset );				
				headerOffset = WriteLittleEndian( rom.offset, writeBytes, headerOffset );
				headerOffset = WriteLittleEndian( rom.checkSum, writeBytes, headerOffset );
				headerOffset = WriteLittleEndian( 0, writeBytes, headerOffset );

				// name stored in the rom?
				string nameSource = useInternalNameSort ? rom.internalName : rom.fileName;

				// 45 chars for description, pad with 0's after we read past the length
				for( int b = 0; b < FILENAME_LIMIT; b++ ){
					writeBytes[ headerOffset++ ] = ( b < nameSource.Length ) ? (byte)nameSource[ b ] : (byte)0;
				}
				// 3 final zeros
				writeBytes[ headerOffset++ ] = 0;
				writeBytes[ headerOffset++ ] = 0;
				writeBytes[ headerOffset++ ] = 0;

				dataOffset = rom.offset;
				// and write the file
				for( int b = 0; b < rom.paddedLength; b++ ){
					writeBytes[ dataOffset + b ] = rom.paddedData[b];
				}

			}

			// this is not time-traveller-proof
			TimeSpan startSpan = (DateTime.UtcNow - new DateTime(1970, 1, 1)); // shortest way to represent the epoch?
			double lastReadTime = startSpan.TotalSeconds;

			if ( File.Exists( targetFilename ) ){
				
				PrintWarning( "Filename already exists: " + targetFilename );

				try{ 
					
					if ( File.Exists( targetFilename+ "_BACKUP" ) ){
						File.Delete( targetFilename+ "_BACKUP" );
					}

					File.Move( targetFilename, targetFilename + "_BACKUP" );
					PrintWarning( "Renamed " + targetFilename + " to " + targetFilename + "_BACKUP" );

				} catch ( System.Exception e ){
					PrintWarning( "Could not rename the existing file... " + e );
					targetFilename += lastReadTime + ".DAT";
					PrintWarning( "Using alternate filename: " + targetFilename );
				}

			}

			try{ 
				File.WriteAllBytes( targetFilename, writeBytes );
			} catch ( Exception e ){
				
				PrintError( "Error writing to disk: " + e );
				return false;

			}

			if ( numWarnings > 0 ){
				Console.Write( "\n\n Build completed successfully with warnings!\n\n" );
				WaitKey();
			} else {
				Console.Write( "\n\n Build completed successfully with no warnings!\n\n" );
			}

			return true;

		}

		static void WaitKey(){
			Console.Write( "\nPress a thing to continue...\n" );
			Console.ReadKey();
		}

		static void Main( string[] args ) {
			
			Console.Clear();

			// Let's always show the top of the header
			PrintUsage( true );

			if ( !VerifyArgs( args ) ){
				
				PrintError( "Input error; exiting!" );
				WaitKey();
				return;

			}

			// revert back to XFlash default; ROMFILE.DAT
			if ( string.IsNullOrEmpty( targetFilename ) ){
				targetFilename = DEFAULT_FILENAME;
			}

			if ( !VerifyROMS() ){
				
				PrintError( "ROM files error; exiting!" );
				WaitKey();
				return;

			}

			if ( !BuildROMS() ){
				
				PrintError( "Error building ROMs!" );
				WaitKey();
				return;

			}


			// start doing the stuff n things.
			
		}

		static void PrintWarning( string inString, bool printHeader = false ){
			
			numWarnings++;
			Console.ForegroundColor = ConsoleColor.Green;
			Console.WriteLine( "    Warning : " + inString );
			Console.ForegroundColor = ConsoleColor.White;

		}

		static void PrintError( string inString, bool printHeader = false ){
			
			if ( printHeader )
				PrintUsage();

			Console.ForegroundColor = ConsoleColor.Red;
			Console.WriteLine( "ERROR! : " + inString );
			Console.ForegroundColor = ConsoleColor.White;

		}

		static void PrintUsage( bool justTheTip = false ){
			
			Console.Write( "\n" );
			Console.Write( "================================================================================\n" );
			Console.Write( "    \n" );
			Console.Write( "    MakeROMCS " + VERSION + "\n" );
			Console.Write( "    \n" );
			Console.Write( "    Replacement for XFlash's romfile.exe\n" );
			Console.Write( "    github.io/JonathanDotCel\n" );
			Console.Write( "    \n" );
			Console.Write( "    Many thanks to SquareSoft74 for testing & suggestions!\n" );
			Console.Write( "    \n" );
			Console.Write( "================================================================================\n" );
			Console.Write( "\n" );

			if ( justTheTip ) return;

			Console.Write( " Usage:\n" );
			Console.Write( "    /I - Sort by Internal name (if any) instead of filename\n" );
			Console.Write( "    /F - Specify a filename, e.g. /F ROMS.DAT\n" );
			Console.Write( "\n" );
			Console.Write( " Example:\n" );
			Console.Write( "    MAKEROMCS /I /F NEWROMS.DAT" );
			Console.Write( "\n" );
			Console.Write( " Packages up all the .ROM files in the current directory!\n" );
			Console.Write( "\n" );

		}

	}

}
