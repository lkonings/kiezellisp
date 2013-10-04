// Copyright (C) 2012-2013 Jan Tolenaar. See the file LICENSE for details.

using System;
using System.Collections;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Globalization;
using System.Reflection;
using System.Linq;
using System.Runtime.InteropServices;

[assembly: CLSCompliant( false )]


namespace Kiezel
{
    class MainApp
    {
        [STAThread]
        static void Main( string[] args )
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Runtime.RunConsole( args );
        }
    }


    public partial class Runtime
    {

        internal static bool IsCompleteSourceCode( string data )
        {
            Cons code;

            return ParseCompleteSourceCode( data, out code );
        }

        internal static bool ParseCompleteSourceCode( string data, out Cons code )
        {
            code = null;

            if ( data.Trim() == "" )
            {
                return true;
            }

            try
            {
                code = ReadAllFromString( data );
                return true;
            }
            catch ( LispException ex )
            {
                return !ex.Message.Contains( "EOF:" );
            }
        }


        internal static bool CanAddToHistory( string data )
        {
            if ( data.Trim() == "" )
            {
                return false;
            }
            //else if ( data[ 0 ] == ':' )
            //{
            //    return false;
            //}
            else
            {
                return true;
            }
        }

        internal static string[] ReplCommands = new string[] 
        {
            "?", ":help", 
            ":clear", ":globals", ":history", ":quit",
            ":abort", ":backtrace", ":variables", ":$variables",
            ":top", ":exception", ":Exception", ":force",
            ":describe", ":reset", ":Reset", ":time"
        };

        internal static LineEditor.Completion AutoCompleteHandler( string text, int pos )
        {
            var start = pos;
            var terminator = ( char ) 0;

            while ( start > 0 )
            {
                var ch = text[ start - 1 ];
                if ( Scanner.IsWhiteSpace( ch ) || Scanner.IsTerminator( ch ) )
                {
                    terminator = ch;
                    break;
                }
                --start;
            }

            var prefix = text.Substring( start, pos - start );
            var nameset = new HashSet<string>();

            if ( terminator == '#' && prefix.StartsWith( "\\" ) )
            {
                prefix = "#" + prefix;

                foreach ( var item in CharacterTable )
                {
                    var s = item.Name;

                    if ( s != null && s.StartsWith( prefix.Substring( 2 ) ) )
                    {
                        nameset.Add( s.Substring( prefix.Length - 2 ) );
                    }
                }

                goto exit;
            }

            foreach ( string s in ReplCommands )
            {
                if ( s.StartsWith( prefix ) )
                {
                    nameset.Add( s.Substring( prefix.Length ) );
                }
            }

            var currentPackage = CurrentPackage();

            foreach ( var sym in currentPackage.Dict.Values )
            {
                var s = sym.Name;

                if ( !sym.IsTemp && s.StartsWith( prefix ) )
                {
                    nameset.Add( s.Substring( prefix.Length ) );
                }
            }

            foreach ( var package in currentPackage.UseList )
            {
                foreach ( var sym in package.Dict.Values )
                {
                    var s = sym.Name;

                    if ( !sym.IsTemp && s.StartsWith( prefix ) && package.FindExternal( s ) != null )
                    {
                        nameset.Add( s.Substring( prefix.Length ) );
                    }
                }
            }

            var packageNames = ListVisiblePackageNames();

            foreach ( var package in packageNames )
            {
                if ( package.StartsWith( prefix ) )
                {
                    nameset.Add( package.Substring( prefix.Length ) + "." );
                }
            }

            if ( prefix.IndexOf( "." ) > 0 )
            {
                foreach ( var name in packageNames )
                {
                    var package = FindPackage( name );

                    if ( package == null || package.Name == "" )
                    {
                        continue;
                    }

                    // Show only external symbols
                    foreach ( var sym in package.Dict.Values )
                    {
                        if ( package.FindExternal( sym.Name ) != null )
                        {
                            var s = name + "." + sym.Name;

                            if ( !sym.IsTemp && s.StartsWith( prefix ) )
                            {
                                nameset.Add( s.Substring( prefix.Length ) );
                            }
                        }
                    }
                }
            }

            if ( prefix.IndexOf( "!" ) > 0 )
            {
                foreach ( var name in packageNames )
                {
                    var package = FindPackage( name );

                    if ( package == null || package.Name == "" )
                    {
                        continue;
                    }

                    // Show only internal symbols
                    foreach ( var sym in package.Dict.Values )
                    {
                        if ( !package.ExternalSymbols.Contains( sym.Name ) )
                        {
                            var s = name + "!" + sym.Name;

                            if ( !sym.IsTemp && s.StartsWith( prefix ) )
                            {
                                nameset.Add( s.Substring( prefix.Length ) );
                            }
                        }
                    }
                }
            }

        exit:

            var names = nameset.ToList();
            names.Sort();
            return new LineEditor.Completion( prefix, names.ToArray() );
        }

        internal static LineEditor le;
        internal static Stack<ThreadContextState> state;
        internal static Stopwatch timer = Stopwatch.StartNew();
        internal static string clipboard = "";


        public static void InsertExternalCommand( string text )
        {
            le.SetExternalInput( text.ConvertToExternalLineEndings() );
        }

        [Lisp( "set-console-key-binding" )]
        public static void SetConsoleKeyBinding( Symbol key, Cons modifiers, object handler )
        {
            le.SetKeyBinding( key, modifiers, handler );
        }

        internal static void RunCommand( Action<object> func, Cons lispCode, bool showTime = false )
        {
            if ( lispCode != null )
            {
                var scope = ReconstructAnalysisScope( CurrentThreadContext.Frame );

                timer.Reset();

                foreach ( var expr in lispCode )
                {
                    var expr2 = Compile( expr, scope );
                    timer.Start();
                    object val = Execute( expr2 );
                    timer.Stop();
                    if ( func == null )
                    {
                        Symbols.It.Value = val;
                        Console.Write( "it: " );
                        PrettyPrint( Console.Out, 4, val );
                    }
                    else
                    {
                        func( val );
                    }
                }

                var time = timer.ElapsedMilliseconds;
                if ( showTime )
                {
                    Console.WriteLine( "Elapsed time: {0} ms", time );
                }
            }
            else
            {
                func( Symbols.It.Value );
            }
        }

        internal static int[] GetIntegerArgs( string lispCode )
        {
            var results = new List<int>();
            foreach ( var expr in ReadAllFromString( lispCode ) )
            {
                results.Add( Convert.ToInt32( Eval( expr ) ) );
            }
            return results.ToArray();
        }

        internal static void ShowManual( Symbol target )
        {
            var helper = GetClosure( GetDynamic( Symbols.HelpHook ) );
            if ( helper != null )
            {
                helper.Apply( MakeArray( target ) );
            }
            else
            {
                Console.WriteLine( "See lib/help.h" );
            }
        }

        [Lisp( "get-version" )]
        public static string GetVersion()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var fileVersion = FileVersionInfo.GetVersionInfo( assembly.Location );
            var date = new DateTime( 2000, 1, 1 ).AddDays( fileVersion.FileBuildPart );
#if DEBUG
            return String.Format( "{0} {1} (Debug Build {2} - {3:dd/MMM/yyyy})", fileVersion.ProductName, fileVersion.ProductVersion, fileVersion.FileBuildPart, date );
#else
            return String.Format( "{0} {1} (Release Build {2} - {3:dd/MMM/yyyy})", fileVersion.ProductName, fileVersion.ProductVersion, fileVersion.FileBuildPart, date );
#endif

        }

        internal static void RunConsole( string[] args )
        {

            var x = ConsoleMode;

            CommandLineParser parser = new CommandLineParser();

            parser.AddOption( "-c", "--command code" );
            parser.AddOption( "-d", "--debug" );
            parser.AddOption( "-n", "--nodebug" );
            parser.AddOption( "-o", "--optimize" );

            parser.Parse( args );

#if KIEZELLISPW
            ConsoleMode = false;
#else
            ConsoleMode = true;
#endif

            if ( ConsoleMode )
            {
                Console.OutputEncoding = System.Text.Encoding.UTF8;
            }


            UserArguments = AsList( parser.GetArgumentArray( 0 ) );

            string expr = parser.GetOption( "c" );

            InitEditor();

            if ( expr == null )
            {
                if ( !ConsoleMode )
                {
                    throw new LispException( "Must use --command option when running in windows mode" );
                }

                DebugMode = parser.GetOption( "n" ) == null;
                OptimizerEnabled = !DebugMode && parser.GetOption( "o" ) != null;

                InteractiveMode = true;

                var assembly = Assembly.GetExecutingAssembly();
                var fileVersion = FileVersionInfo.GetVersionInfo( assembly.Location );
                var date = new DateTime( 2000, 1, 1 ).AddDays( fileVersion.FileBuildPart );
#if DEBUG
                Console.WriteLine( "{0} {1} (Debug Build {2} - {3:dd/MMM/yyyy})", fileVersion.ProductName, fileVersion.ProductVersion, fileVersion.FileBuildPart, date );
#else
                Console.WriteLine( "{0} {1} (Release Build {2} - {3:dd/MMM/yyyy})", fileVersion.ProductName, fileVersion.ProductVersion, fileVersion.FileBuildPart, date );
#endif
                Console.WriteLine( fileVersion.LegalCopyright );
                Console.WriteLine( "Type :help or ? for help on top-level commands" );
                ReadEvalPrintLoop( true );
            }
            else
            {
                try
                {
                    DebugMode = parser.GetOption( "d" ) == null;
                    OptimizerEnabled = !DebugMode;
                    InteractiveMode = false;
                    Reset( false );
                    var code = ReadFromString( "(do " + expr + ")" );
                    Eval( code );
                }
                catch ( Exception ex )
                {
                    PrintLog( GetDiagnostics( ex ) );
                }
            }
        }


        internal static void InitEditor()
        {
            le = new LineEditor( "kiezellisp" );
            le.AcceptReturnAsCommand = IsCompleteSourceCode;
            le.CanAddToHistory = CanAddToHistory;
            le.AutoCompleteEvent = AutoCompleteHandler;
        }

        [Lisp( "read-string-from-console" )]
        public static string ReadStringFromConsole()
        {
            le.ReadingFromREPL = false;
            bool isExternalInput;
            return le.Edit( "", "", out isExternalInput );
        }

        internal static void ConsoleSetColor( object color, object bkcolor = null )
        {
            var c1 = GetColor( color );
            var c2 = GetColor( bkcolor );

            if ( c1 != null )
            {
                var c = ( ConsoleColor ) c1;
                Console.ForegroundColor = c;
            }

            if ( c2 != null )
            {
                var c = ( ConsoleColor ) c2;
                Console.BackgroundColor = c;
            }
        }

        internal static void ReadEvalPrintLoop( bool restart )
        {
            var unusedInputFromRestart = "";

            Console.TreatControlCAsInput = true;

            state = new Stack<ThreadContextState>();
            state.Push( SaveStackAndFrame() );

            while ( true )
            {
                try
                {
                    if ( restart )
                    {
                        restart = false;
                        timer.Reset();
                        timer.Start();
                        Reset( false );
                        RestartListeners();
                        timer.Stop();
                        var time = timer.ElapsedMilliseconds;
                        Console.WriteLine( "Startup time: {0} ms", time );
                    }

                    ReadEvalPrint( unusedInputFromRestart );

                    unusedInputFromRestart = "";
                }
                catch ( RestartException ex )
                {
                    unusedInputFromRestart = ex.UnusedInput;
                    Symbols.Exception.Value = ex;
                    state.Push( SaveStackAndFrame() );
                }
                catch ( Exception ex )
                {
                    unusedInputFromRestart = "";
                    ex = UnwindException( ex );
                    Symbols.Exception.Value = ex;
                    ConsoleSetColor( GetDynamic( Symbols.StandoutColor ), GetDynamic( Symbols.StandoutBackgroundColor ) );
                    Console.WriteLine( ex.Message );
                    Console.ResetColor();
                    state.Push( SaveStackAndFrame() );
                }
            }
        }

        internal static Exception UnwindException( Exception ex )
        {
            while ( ex.InnerException != null && ex is System.Reflection.TargetInvocationException )
            {
                ex = ex.InnerException;
            }
            return ex;
        }

        internal static Exception UnwindExceptionIntoNewException( Exception ex )
        {
            var ex2 = UnwindException( ex );
            string str = GetDiagnostics( ex2 ).Indent( ">>> " );
            var ex3 = new LispException( str, ex2 );
            return ex3;
        }

        internal static void ReadEvalPrint( string unusedInputFromRestart )
        {
            var data = "";
            bool isExternalInput = false;

            if ( unusedInputFromRestart == "" )
            {
                int counter = le.History.Count + 1;
                string prompt;

                if ( state.Count == 1 )
                {
                    Console.WriteLine();
                    prompt = System.String.Format( "{0} {1} > ", ( ( Package ) Symbols.Package.Value ).Name, counter );
                }
                else
                {
                    Console.WriteLine();
                    prompt = System.String.Format( "{0} {1} : {2} > ", ( ( Package ) Symbols.Package.Value ).Name, counter, state.Count - 1 );
                }

                le.ReadingFromREPL = true;

                data = le.Edit( prompt, clipboard, out isExternalInput );
                clipboard = "";

                if ( data == null )
                {
                    return;
                }
            }
            else
            {
                data = unusedInputFromRestart;
            }

            Cons code = ReadAllFromString( data );
            
            if ( code == null )
            {
                return;
            }

            RestoreStackAndFrame( state.Peek() );

            var head = First( code ) as Symbol;

            if ( head != null && ( Keywordp( head ) || head.Name == "?" ) )
            {
                var dotCommand = First( code ).ToString();
                var command = "";
                var commandsPrefix = ReplCommands.Where( x => ( ( String ) x ).StartsWith( dotCommand ) ).ToList();
                var commandsExact = ReplCommands.Where( x => ( ( String ) x ) == dotCommand ).ToList();

                if ( commandsPrefix.Count == 0 )
                {
                    Console.WriteLine( "Command not found" );
                    return;
                }
                else if ( commandsExact.Count == 1 )
                {
                    command = commandsExact[ 0 ];
                }
                else if ( commandsPrefix.Count == 1 )
                {
                    command = commandsPrefix[ 0 ];
                }
                else
                {
                    Console.Write( "Ambiguous command. Did you mean:" );
                    for ( int i = 0; i < commandsPrefix.Count; ++i )
                    {
                        Console.Write( "{0} {1}", ( i == 0 ? "" : i + 1 == commandsPrefix.Count ? " or" : "," ), commandsPrefix[ i ] );
                    }
                    Console.WriteLine( "?" );
                    return;
                }


                switch ( command )
                {
                    case ":clear":
                    {
                        Console.Clear();
                        le.ClearHistory();
                        state = new Stack<ThreadContextState>();
                        state.Push( SaveStackAndFrame() );
                        break;
                    }
                    case ":history":
                    {
                        for ( int i = 0; i < le.History.Count; ++i )
                        {
                            Console.WriteLine( "[{0}] {1}", i + 1, le.History.Line( i ) );
                        }
                        break;
                    }
                    case ":abort":
                    {
                        if ( state.Count > 1 )
                        {
                            state.Pop();
                        }
                        break;
                    }

                    case ":top":
                    {
                        while ( state.Count > 1 )
                        {
                            state.Pop();
                        }
                        break;
                    }

                    case ":quit":
                    {
                        le.SaveHistory();
                        AbortListeners();
                        Environment.Exit( 0 );
                        break;
                    }

                    case ":globals":
                    {
                        DumpDictionary( Console.Out, GetGlobalVariablesDictionary( ToPrintString( Second( code ), false ) ) );
                        break;
                    }

                    case ":variables":
                    {
                        var pos = Integerp( Second( code ) ) ? ( int ) Second( code ) : 0;
                        DumpDictionary( Console.Out, GetLexicalVariablesDictionary( pos ) );
                        break;
                    }

                    case ":$variables":
                    {
                        var pos = Integerp( Second( code ) ) ? ( int ) Second( code ) : 0;
                        DumpDictionary( Console.Out, GetDynamicVariablesDictionary( pos ) );
                        break;
                    }

                    case ":backtrace":
                    {
                        Console.Write( GetEvaluationStack() );
                        break;
                    }

                    case ":Exception":
                    {
                        Console.WriteLine( Symbols.Exception.Value );
                        break;
                    }

                    case ":exception":
                    {
                        Console.WriteLine( RemoveDlrReferencesFromException( ( Exception ) Symbols.Exception.Value ) );
                        break;
                    }

                    case ":force":
                    {
                        var expr = (object) Second( code ) ?? Symbols.It;
                        RunCommand( null, MakeList( MakeList( Symbols.Force, expr) ) );
                        break;
                    }

                    case ":time":
                    {
                        var expr = ( object ) Second( code ) ?? Symbols.It;
                        RunCommand( null, MakeList( expr ), true );
                        break;
                    }

                    case ":help":
                    case "?":
                    {
                        ShowManual( Second( code ) as Symbol );
                        break;
                    }
                    case ":describe":
                    {
                        RunCommand( x =>
                        {
                            Symbols.It.Value = x;
                            Describe( x );
                        }, Cdr( code ) );
                        break;
                    }
                    case ":reset":
                    {
                        while ( state.Count > 1 )
                        {
                            state.Pop();
                        }
                        Reset( false );
                        break;
                    }
                    case ":Reset":
                    {
                        while ( state.Count > 1 )
                        {
                            state.Pop();
                        }
                        Reset( true );
                        break;
                    }
                }

            }
            else
            {
                RunCommand( null, code );
            }
        }


    }
}
