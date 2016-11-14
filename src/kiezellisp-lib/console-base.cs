﻿// Copyright (C) Jan Tolenaar. See the file LICENSE for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Linq;
using System.Text;
using System.IO;

namespace Kiezel
{
    [RestrictedImport]
    public partial class RuntimeConsoleBase
    {
        public delegate void ResetDisplayFunction();

        public delegate void ResetRuntimeFunction(int level);

        public delegate string ReadLineFunction();

        public static string[] ReplCommands = new string[]
        {
            ":clear", ":continue", ":globals", ":quit",
            ":abort", ":backtrace", ":variables", ":$variables",
            ":top", ":exception", ":Exception", ":force",
            ":describe", ":reset", ":time"
        };

        public static ReplHistory History = new ReplHistory();

        public static Stack<ThreadContextState> state;

        public static Stopwatch timer = Stopwatch.StartNew();

        public static Exception LastException = null;

        public static ResetDisplayFunction ResetDisplayFunctionImp = null;
 
        public static ResetRuntimeFunction ResetRuntimeFunctionImp = null;

        public static ReadLineFunction ReadFunctionImp = null;

        public static object GetConsoleOut()
        {
            return Runtime.AssertStream(Symbols.StdOut.Value);
        }

        public static object GetConsoleLog()
        {
            return Runtime.AssertStream(Symbols.StdLog.Value);
        }

        public static object GetConsoleErr()
        {
            return Runtime.AssertStream(Symbols.StdErr.Value);
        }

        public static bool IsNotDlrCode(string line)
        {
            return line.IndexOf("CallSite") == -1
            && line.IndexOf("System.Dynamic") == -1
            && line.IndexOf("Microsoft.Scripting") == -1;
        }

        public static string RemoveDlrReferencesFromException(Exception ex)
        {
            return String.Join("\n", ex.ToString().Split('\n').Where(IsNotDlrCode));
        }

        public static int[] GetIntegerArgs(string lispCode)
        {
            var results = new List<int>();
            foreach (var expr in Runtime.ReadAllFromString( lispCode ))
            {
                results.Add(Convert.ToInt32(Runtime.Eval(expr)));
            }
            return results.ToArray();
        }

        public static bool IsCompleteSourceCode(string data)
        {
            Cons code;

            return ParseCompleteSourceCode(data, out code);
        }

        public static bool ParseCompleteSourceCode(string data, out Cons code)
        {
            code = null;

            if (data.Trim() == "")
            {
                return true;
            }

            try
            {
                code = Runtime.ReadAllFromString(data);
                return true;
            }
            catch (LispException ex)
            {
                return !ex.Message.Contains("EOF:");
            }
        }

        public static List<string> GetCompletions(string prefix)
        {
            var nameset = new HashSet<string>();

            foreach (string s in ReplCommands)
            {
                if (s.StartsWith(prefix))
                {
                    nameset.Add(s);
                }
            }

            Runtime.FindCompletions(prefix, nameset);

            var names = nameset.ToList();

            if (names.Count == 0)
            {
                names.Add(prefix);
            }
            names.Sort();
            if (names.Count > 50)
            {
                names = names.GetRange(0, 50);
                names.Add("*** Too many completions ***");
            }
            return names;
        }

        public static void Quit()
        {
            History.Close();
            Environment.Exit(0);
        }

        [Lisp("breakpoint")]
        public static void Breakpoint()
        {
            var oldState = state;
            state = new Stack<ThreadContextState>();
            state.Push(Runtime.SaveStackAndFrame());

            try
            {
                ReadEvalPrintLoop(initialized: true, debugging: true);
            }
            catch (ContinueFromBreakpointException)
            {
            }
            catch (AbortingDebuggerException)
            {
                throw new AbortedDebuggerException();
            }
            finally
            {
                state = oldState;
            }
        }



        public static void EvalPrintCommand(string data, bool debugging)
        {
            var output = GetConsoleOut();
            bool leadingSpace = char.IsWhiteSpace(data, 0);
            Cons code = Runtime.ReadAllFromString(data);

            if (code == null)
            {
                return;
            }

            Runtime.RestoreStackAndFrame(state.Peek());

            var head = Runtime.First(code) as Symbol;

            if (head != null && (Runtime.Keywordp(head) || head.Name == "?"))
            {
                var dotCommand = Runtime.First(code).ToString();
                var command = "";
                var commandsPrefix = ReplCommands.Where(x => ((String)x).StartsWith(dotCommand)).ToList();
                var commandsExact = ReplCommands.Where(x => ((String)x) == dotCommand).ToList();

                if (commandsPrefix.Count == 0)
                {
                    Runtime.PrintLine(output, "Command not found");
                    return;
                }
                else if (commandsExact.Count == 1)
                {
                    command = commandsExact[0];
                }
                else if (commandsPrefix.Count == 1)
                {
                    command = commandsPrefix[0];
                }
                else
                {
                    Runtime.PrintLine(output, "Ambiguous command. Did you mean:");
                    for (int i = 0; i < commandsPrefix.Count; ++i)
                    {
                        var str = String.Format("{0} {1}", (i == 0 ? "" : i + 1 == commandsPrefix.Count ? " or" : ","), commandsPrefix[i]);
                        Runtime.PrintLine(output, str);
                    }
                    Runtime.PrintLine(output, "?");
                    return;
                }

                switch (command)
                {
                    case ":continue":
                    {
                        if (debugging)
                        {
                            throw new ContinueFromBreakpointException();
                        }
                        break;
                    }
                    case ":clear":
                    {
                        History.Clear();
                        ResetDisplayFunctionImp();
                        state = new Stack<ThreadContextState>();
                        state.Push(Runtime.SaveStackAndFrame());
                        break;
                    }
                    case ":abort":
                    {
                        if (state.Count > 1)
                        {
                            state.Pop();
                        }
                        else if (debugging)
                        {
                            throw new AbortingDebuggerException();
                        }
                        break;
                    }

                    case ":top":
                    {
                        while (state.Count > 1)
                        {
                            state.Pop();
                        }
                        break;
                    }

                    case ":quit":
                    {
                        Quit();
                        break;
                    }

                    case ":globals":
                    {
                        var pattern = (string)Runtime.Second(code);
                        Runtime.DumpDictionary(output, Runtime.GetGlobalVariablesDictionary(pattern));
                        break;
                    }

                    case ":variables":
                    {
                        var pos = Runtime.Integerp(Runtime.Second(code)) ? (int)Runtime.Second(code) : 0;
                        Runtime.DumpDictionary(output, Runtime.GetLexicalVariablesDictionary(pos));
                        break;
                    }

                    case ":$variables":
                    {
                        var pos = Runtime.Integerp(Runtime.Second(code)) ? (int)Runtime.Second(code) : 0;
                        Runtime.DumpDictionary(output, Runtime.GetDynamicVariablesDictionary(pos));
                        break;
                    }

                    case ":backtrace":
                    {
                        Runtime.PrintLine(output, Runtime.GetEvaluationStack());
                        break;
                    }

                    case ":Exception":
                    {
                        Runtime.PrintLine(output, LastException.ToString());
                        break;
                    }

                    case ":exception":
                    {
                        Runtime.PrintLine(output, RemoveDlrReferencesFromException(LastException));
                        break;
                    }

                    case ":force":
                    {
                        var expr = (object)Runtime.Second(code) ?? Symbols.It;
                        RunCommand(null, Runtime.MakeList(Runtime.MakeList(Symbols.Force, expr)));
                        break;
                    }

                    case ":time":
                    {
                        var expr = (object)Runtime.Second(code) ?? Symbols.It;
                        RunCommand(null, Runtime.MakeList(expr), showTime: true);
                        break;
                    }

                    case ":describe":
                    {
                        RunCommand(x =>
                        {
                            Runtime.SetSymbolValue(Symbols.It, x);
                            Runtime.Describe(x);
                        }, Runtime.Cdr(code));
                        break;
                    }

                    case ":reset":
                    {
                        var level = Runtime.Integerp(Runtime.Second(code)) ? (int)Runtime.Second(code) : 0;
                        while (state.Count > 1)
                        {
                            state.Pop();
                        }
                        timer.Reset();
                        timer.Start();
                        ResetRuntimeFunctionImp(level);
                        timer.Stop();
                        var time = timer.ElapsedMilliseconds;
                        Runtime.PrintTrace("Startup time: ", time, "ms");
                        break;
                    }

                }
            }
            else
            {
                RunCommand(null, code, smartParens: !leadingSpace);
            }
        }

        public static string ReadCommand(bool debugging = false)
        {
            var output = GetConsoleOut();
            var data = "";

            while (String.IsNullOrWhiteSpace(data))
            {
                int counter = History.Count + 1;
                string prompt;
                string debugText = debugging ? "> debug " : "";
                var package = Runtime.CurrentPackage();
                if (state.Count == 1)
                {
                    Runtime.PrintLine(output);
                    prompt = System.String.Format("{0} {1} {2}> ", package.Name, counter, debugText);
                }
                else
                {
                    Runtime.PrintLine(output);
                    prompt = System.String.Format("{0} {1} {2}: {3} > ", package.Name, counter, debugText, state.Count - 1);
                }

                Runtime.Print(output, prompt);

                while ((data = ReadFunctionImp()) == null)
                {
                }

                Runtime.PrintLine(output);

                if (String.IsNullOrWhiteSpace(data))
                {
                    // Show prompt again
                    continue;
                }
            }

            History.Append(data);

            return data;
        }

        public static void ReadEvalPrintLoop(string commandOptionArgument = null, bool initialized = false, bool debugging = false)
        {
            if (!initialized)
            {
                state = new Stack<ThreadContextState>();
                state.Push(Runtime.SaveStackAndFrame());
            }

            while (true)
            {
                try
                {
                    if (!initialized)
                    {
                        initialized = true;
                        ResetRuntimeFunctionImp(0);
                    }

                    if (String.IsNullOrWhiteSpace(commandOptionArgument))
                    {
                        var command = ReadCommand(debugging);
                        EvalPrintCommand(command, debugging);
                    }
                    else
                    {
                        var scriptFile = commandOptionArgument;
                        commandOptionArgument = "";
                        Runtime.Run(scriptFile, Symbols.LoadPrintKeyword, false, Symbols.LoadVerboseKeyword, false);
                        if (!Runtime.Repl)
                        {
                            Runtime.Exit();
                        }
                    }
                }
                catch (ContinueFromBreakpointException)
                {
                    if (debugging)
                    {
                        throw;
                    }
                }
                catch (AbortingDebuggerException)
                {
                    if (debugging)
                    {
                        throw;
                    }
                }
                catch (AbortedDebuggerException)
                {
                }
                catch (InterruptException)
                {
                    // Effect of Ctrl+D
                    Runtime.PrintStream(GetConsoleErr(), "error", "Interrupt.\n");
                }
                catch (Exception ex)
                {
                    //ClearKeyboardBuffer();
                    ex = Runtime.UnwindException(ex);
                    LastException = ex;
                    Runtime.PrintStream(GetConsoleErr(), "error", ex.Message + "\n");
                    state.Push(Runtime.SaveStackAndFrame());
                } 
            }
        }

        public static void RunCommand(Action<object> func, Cons lispCode, bool showTime = false, bool smartParens = false)
        {
            var output = GetConsoleOut();

            if (lispCode != null)
            {
                var head = Runtime.First(lispCode) as Symbol;
                var scope = Runtime.ReplGetCurrentAnalysisScope();

                if (smartParens && head != null)
                {
                    if (Runtime.LooksLikeFunction(head))
                    {
                        lispCode = Runtime.MakeCons(lispCode, (Cons)null);
                    }
                }

                timer.Reset();

                foreach (var expr in lispCode)
                {
                    var expr2 = Runtime.Compile(expr, scope);
                    timer.Start();
                    object val = Runtime.Execute(expr2);
                    if (Runtime.ToBool(Runtime.GetDynamic(Symbols.ReplForceIt)))
                    {
                        val = Runtime.Force(val);
                    }
                    timer.Stop();
                    if (func == null)
                    {
                        Runtime.SetSymbolValue(Symbols.It, val);
                        if (val != VOID.Value)
                        {
                            Runtime.PrintStream(output, "", "it: ");
                            Runtime.PrettyPrintLine(output, 4, null, val);
                        }
                    }
                    else
                    {
                        func(val);
                    }
                }

                var time = timer.ElapsedMilliseconds;
                if (showTime)
                {
                    Runtime.PrintStream(GetConsoleLog(), "info", Runtime.MakeString("Elapsed time: ", time, " ms\n"));
                }
            }
            else
            {
                func(Runtime.SymbolValue(Symbols.It));
            }
        }


    }

}
