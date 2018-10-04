﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DebuggerCLI
{
    public class Program
    {
        private static Thread ConnectionThread;
        private static string DecodeMSG(dynamic obj)
        {
            switch (((string)obj.mode).ToLower())
            {
                case "position":
                    return $"line {obj.data.line}, col {obj.data.col}, file {obj.data.file}";
                case "error":
                    return $"Error: {obj.data}";
                case "message":
                    return $"{obj.data}";
                case "variables":
                    {
                        var builder = new StringBuilder();
                        foreach (var it in obj.data)
                        {
                            if (builder.Length > 0)
                            {
                                builder.Append(", ");
                            }
                            builder.Append($"{it.name}: {it.value}");
                        }
                        return builder.ToString();
                    }
                case "status":
                    return $"Server now in {obj.data} status.";
                case "callstack":
                default:
                    {
                        var output = JsonConvert.SerializeObject(obj, Formatting.Indented);
                        return output;
                    }
            }
        }

        private static bool continueExecution = true;
        private static bool isInInterrupt = false;
        private static bool decodemessages = true;

        private static void ConnectionThread_Method()
        {
            while (continueExecution)
            {
                try
                {
                    using (var client = new TcpClient("localhost", 9090))
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"Connected to {(client.Client.RemoteEndPoint as System.Net.IPEndPoint).Address.ToString()} on port {(client.Client.RemoteEndPoint as System.Net.IPEndPoint).Port} via local port {(client.Client.LocalEndPoint as System.Net.IPEndPoint).Port}.");
                        Console.ResetColor();
                        stream = client.GetStream();
                        var buffer = new byte[1 << 11];
                        while (client.Connected)
                        {
                            if (!continueExecution)
                            {
                                break;
                            }

                            var read = stream.Read(buffer, 0, buffer.Length);
                            if (read == 0)
                            {
                                break;
                            }
                            var recv = Encoding.ASCII.GetString(buffer, 0, read);
                            foreach (var str in recv.Split('\0').Where((s) => !String.IsNullOrWhiteSpace(s)))
                            {
                                while (isInInterrupt) { Thread.Sleep(100); }
                                try
                                {
                                    dynamic obj = JsonConvert.DeserializeObject(str);
                                    if (decodemessages)
                                    {
                                        Console.WriteLine($"[<--][{DateTime.Now}] {DecodeMSG(obj)}");
                                    }
                                    else
                                    {
                                        var output = JsonConvert.SerializeObject(obj, Formatting.Indented);
                                        Console.WriteLine($"[<--][{DateTime.Now}] RECEIVE");
                                        Console.WriteLine(output);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.ForegroundColor = ConsoleColor.Red;
                                    Console.WriteLine($"{DateTime.Now} - {ex.Message}");
                                    Console.ResetColor();
                                    Console.WriteLine($"[<--][{DateTime.Now}] RECEIVE WITH JSON PARSE ERR");
                                    Console.WriteLine(str);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    stream = null;
                    while (isInInterrupt) { Thread.Sleep(100); }
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"{DateTime.Now} - {ex.Message}");
                    Console.ResetColor();
                }
            }
        }

        static Stream stream;
        static void Main(string[] args)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Using CTRL+BACKSPACE, you can delete whole words (space-to-space).");
            Console.WriteLine("Using ENTER you can send a command/request.");
            Console.WriteLine("Using TAB you can auto-complete a command.");
            Console.WriteLine("Arguments are separated via space. Using quotes (\"), it is possible to chain arguments.");
            Console.WriteLine("'?' will output help.");
            Console.ResetColor();
            var toExecute = String.Empty;
            var handler = new CommandHandler();
            var displaysend = false;

            #region quit
            handler.Add(new CommandHandlerItem("q", "quit", "Shuts down the application.", () => Environment.Exit(0)));
            #endregion
            #region decodemsg
            handler.Add(new CommandHandlerItem<bool>("", "decodemsg", $"Allows to change if server messages should be decoded or not. Default: {decodemessages}", (flag) => decodemessages = flag));
            #endregion
            #region decodemsg
            handler.Add(new CommandHandlerItem<bool>("", "displaysend", $"Will set a flag wether or not you want the send stuff to output. Default: {displaysend}", (flag) => decodemessages = flag));
            #endregion
            #region callstack (cs)
            handler.Add(new CommandHandlerItem("cs", "callstack", "Sends a request to get the current callstack.", () =>
            {
                var obj = JsonConvert.DeserializeObject(@"{""mode"":""get-callstack"",""data"":null}");
                toExecute = JsonConvert.SerializeObject(obj, Formatting.Indented);
            }));
            #endregion
            #region getvariable (gv)
            handler.Add(new CommandHandlerItem<string, string>("gv", "getvariable", "Sends a request to get a variable from the connected VM.", (var, scope) =>
            {
                if (String.IsNullOrWhiteSpace(var)) { throw new ArgumentException("Missing argument 1"); }
                if (String.IsNullOrWhiteSpace(scope)) { throw new ArgumentException("Missing argument 2"); }
                var obj = JsonConvert.DeserializeObject($@"{{ ""mode"": ""get-variables"", ""data"": [ {{ ""name"": ""{var}"", ""scope"": ""{scope}"" }} ] }}");
                toExecute = JsonConvert.SerializeObject(obj, Formatting.Indented);
            }).SetDetails(
                "Expects 2 input arguments:",
                "- variablename to get.",
                "- scope to get from.",
                String.Join(" ",
                    "If the variable is private, the scope argument is expected to be a NUMBER in the range [0,-N) indicating where",
                    "the search should start. 0 will be the top, -N being how many scopes one would want to 'ignore'."
                ),
                "If the variable is not private, the scope is expected to be one of the following:",
                "- missionNamespace",
                "- uiNamespace",
                "- profileNamespace",
                "- parsingNamespace"
            ));
            #endregion
            #region control (c)
            handler.Add(new CommandHandlerItem<string>("c", "control", "Changes the current state of the VM.", (status) =>
            {
                if (String.IsNullOrWhiteSpace(status)) { throw new ArgumentException("Missing argument 1"); }
                var obj = JsonConvert.DeserializeObject($@"{{ ""mode"": ""control"", ""data"": {{ ""status"": ""{status}"" }} }}");
                toExecute = JsonConvert.SerializeObject(obj, Formatting.Indented);
            }).SetDetails(
                "Expects 1 input arguments:",
                "- status.",
                "Status has to be one of the following enum values:",
                "- stop",
                "- pause",
                "- resume",
                "- quit"
            ));
            #endregion
            #region breakpoint (bp)
            handler.Add(new CommandHandlerItem<int, string>("bp", "breakpoint", "Adds a breakpoint at provided line and file.", (line, file) =>
            {
                var obj = JsonConvert.DeserializeObject($@"{{ ""mode"": ""set-breakpoint"", ""data"": {{ ""line"": {line}, ""file"": ""{(file ?? "")}"" }} }}");
                toExecute = JsonConvert.SerializeObject(obj, Formatting.Indented);
            }).SetDetails(
                "Expects up to 2 input arguments:",
                "- line number to break on.",
                "- OPTIONAL file name."
            ));
            #endregion
            #region parseqf (sqf)
            handler.Add(new CommandHandlerItem<string, string>("sqf", "parsesqf", "Parses and executes provided SQF code.", (sqf, file) =>
            {
                if (String.IsNullOrWhiteSpace(sqf)) { throw new ArgumentException("Missing argument 1"); }
                var obj = new Newtonsoft.Json.Linq.JObject
                {
                    { "mode", "parse-sqf" },
                    { "data", new Newtonsoft.Json.Linq.JObject {
                        { "sqf", sqf },
                        { "file", file ?? "" }
                    } }
                };
                toExecute = JsonConvert.SerializeObject(obj, Formatting.Indented);
            }).SetDetails(
                "Expects up to 2 input arguments:",
                "- SQF content.",
                "- OPTIONAL file name."
            ));
            #endregion
            #region loadsqflocal (lsqf)
            handler.Add(new CommandHandlerItem<string, string>("lsqf", "loadsqflocal", "Loads sqf from provided path on disk where the application is local, transfers and executes it.", (filepath, file) =>
            {
                if (String.IsNullOrWhiteSpace(filepath)) { throw new ArgumentException("Missing argument 1"); }
                if (!File.Exists(filepath)) { throw new ArgumentException("File from argument 1 could not be located"); }
                using (var reader = new StreamReader(filepath))
                {
                    var obj = new Newtonsoft.Json.Linq.JObject
                    {
                        { "mode", "parse-sqf" },
                        { "data", new Newtonsoft.Json.Linq.JObject {
                            { "sqf", reader.ReadToEnd() },
                            { "file", file ?? "" }
                        } }
                    };
                    toExecute = JsonConvert.SerializeObject(obj, Formatting.Indented);
                }
            }).SetDetails(
                "Expects up to 2 input arguments:",
                "- Path to file.",
                "- OPTIONAL file name."
            ));
            #endregion
            #region loadsqf
            handler.Add(new CommandHandlerItem<string, string>("loadsqf", "Loads sqf from provided path on disk where the VM is local and executes it.", (path, file) =>
            {
                if (String.IsNullOrWhiteSpace(path)) { throw new ArgumentException("Missing argument 1"); }
                var obj = new Newtonsoft.Json.Linq.JObject
                    {
                        { "mode", "load-sqf" },
                        { "data", new Newtonsoft.Json.Linq.JObject {
                            { "path", path },
                            { "file", file ?? "" }
                        } }
                    };
                toExecute = JsonConvert.SerializeObject(obj, Formatting.Indented);
            }).SetDetails(
                "Expects up to 2 input arguments:",
                "- Path to file (needs to be local to VM!).",
                "- OPTIONAL file name."
            ));
            #endregion

            ConnectionThread = new Thread(ConnectionThread_Method);
            ConnectionThread.Start();

            while (continueExecution)
            {
                ConsoleKeyInfo keyinfo = Console.ReadKey(true);
                if (keyinfo.Key != ConsoleKey.Escape && !(keyinfo.Modifiers == ConsoleModifiers.Shift && keyinfo.Key == ConsoleKey.C))
                {
                    isInInterrupt = true;
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Write("> ");
                }
                var builder = new StringBuilder();
                string tabsource = String.Empty;
                IEnumerator<string> autocomplete = null;
                bool first = true;
                while ((first || keyinfo.Key != ConsoleKey.Enter) && keyinfo.Key != ConsoleKey.Escape && !(keyinfo.Modifiers == ConsoleModifiers.Shift && keyinfo.Key == ConsoleKey.C))
                {
                    if (keyinfo.Key == ConsoleKey.Tab && !builder.ToString().Any((c) => c == ' '))
                    {
                        if (autocomplete != null && !autocomplete.MoveNext())
                        {
                            autocomplete = null;
                            if (builder.ToString() != tabsource)
                            {
                                Console.Write(new string('\b', builder.Length));
                                Console.Write(new string(' ', builder.Length));
                                Console.Write(new string('\b', builder.Length));
                                builder = new StringBuilder(tabsource);
                                Console.Write(tabsource);
                                tabsource = null;
                            }
                            System.Media.SystemSounds.Beep.Play();
                        }
                        if (autocomplete == null)
                        {
                            autocomplete = handler.AutoComplete(builder.ToString());
                            tabsource = builder.ToString();
                            if (!autocomplete.MoveNext())
                            {
                                autocomplete = null;
                                System.Media.SystemSounds.Beep.Play();
                            }
                        }
                        if (autocomplete != null)
                        {
                            Console.Write(new string('\b', builder.Length));
                            Console.Write(new string(' ', builder.Length));
                            Console.Write(new string('\b', builder.Length));
                            builder = new StringBuilder(autocomplete.Current);
                            Console.Write(builder.ToString());
                        }
                    }
                    else
                    {
                        if (autocomplete != null)
                        {
                            autocomplete = null;
                            tabsource = null;
                        }
                        if (keyinfo.Key == ConsoleKey.Backspace)
                        {
                            if (builder.Length == 0)
                            {
                                if (!first)
                                {
                                    System.Media.SystemSounds.Beep.Play();
                                }
                            }
                            else if (keyinfo.Modifiers == ConsoleModifiers.Control)
                            {
                                var tmp = builder.ToString();
                                var lastindex = tmp.TrimEnd().LastIndexOf(' ');
                                if (lastindex >= 0)
                                {
                                    var range = builder.Length - lastindex;
                                    builder.Remove(lastindex, range);
                                    Console.Write(new string('\b', range));
                                    Console.Write(new string(' ', range));
                                    Console.Write(new string('\b', range));
                                }
                                else if (builder.Length > 0)
                                {
                                    Console.Write(new string('\b', builder.Length));
                                    Console.Write(new string(' ', builder.Length));
                                    Console.Write(new string('\b', builder.Length));
                                    builder = new StringBuilder();
                                }
                            }
                            else
                            {
                                builder.Remove(builder.Length - 1, 1);
                                Console.Write("\b \b");
                            }
                        }
                        else if (keyinfo.KeyChar != '\0' && keyinfo.Key != ConsoleKey.Enter && !(builder.Length == 0 && keyinfo.KeyChar == ' '))
                        {
                            builder.Append(keyinfo.KeyChar);
                            Console.Write(keyinfo.KeyChar);
                        }
                    }
                    first = false;
                    keyinfo = Console.ReadKey(true);
                }
                Console.ResetColor();
                if (keyinfo.Key == ConsoleKey.Escape || builder.Length == 0)
                {
                    if (builder.Length > 0)
                    {
                        Console.Write(new string('\b', builder.Length));
                        Console.Write(new string(' ', builder.Length));
                        Console.Write(new string('\b', builder.Length));
                    }
                    Console.Write("\b\b \b");
                }
                else if (keyinfo.Modifiers == ConsoleModifiers.Shift && keyinfo.Key == ConsoleKey.C)
                {
                    continueExecution = false;
                }
                else
                {
                    Console.WriteLine();
                    byte[] bytes;
                    var input = builder.ToString();
                    if (handler.TryHandle(input))
                    {
                        if (!String.IsNullOrWhiteSpace(toExecute))
                        {
                            if (displaysend)
                            {
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine($"[-->][{DateTime.Now}] SEND");
                                Console.WriteLine(toExecute);
                                Console.ResetColor();
                            }
                            bytes = Encoding.ASCII.GetBytes(toExecute);
                            stream.Write(bytes, 0, bytes.Length);
                            stream.WriteByte(0);
                            toExecute = String.Empty;
                        }
                    }
                    else if (stream == null)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"Not yet connected.");
                        Console.ResetColor();
                    }
                    else
                    {
                        bytes = Encoding.ASCII.GetBytes(input);
                        stream.Write(bytes, 0, bytes.Length);
                        stream.WriteByte(0);
                    }
                }
                isInInterrupt = false;
            }
            ConnectionThread.Join();
        }

    }
}