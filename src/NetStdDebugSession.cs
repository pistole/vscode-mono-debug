/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Linq;
using System.Net;
using System.Text;
using Microsoft.Samples.Debugging.MdbgEngine;
using Microsoft.Samples.Debugging.CorDebug;
using Microsoft.Samples.Debugging.CorDebug.NativeApi;
using Microsoft.Samples.Tools.Mdbg;
using System.Reflection;
using System.Runtime.InteropServices;

namespace VSCodeDebug
{
	public class NetStdDebugSession : DebugSession
	{
		private const string MONO = "mono";
		private readonly string[] MONO_EXTENSIONS = new String[] {
			".cs", ".csx",
			".cake",
			".fs", ".fsi", ".ml", ".mli", ".fsx", ".fsscript",
			".hx"
		};
		private const int MAX_CHILDREN = 100;
		private const int MAX_CONNECTION_ATTEMPTS = 10;
		private const int CONNECTION_ATTEMPT_INTERVAL = 500;

		private AutoResetEvent _resumeEvent = new AutoResetEvent(false);
		private bool _debuggeeExecuting = false;
		private readonly object _lock = new object();
		private static MDbgEngine _session;
		private volatile bool _debuggeeKilled = true;
		private MDbgProcess _activeProcess;
		private MDbgFrame _activeFrame;
		private long _nextBreakpointId = 0;
		private SortedDictionary<long, MDbgBreakpoint> _breakpoints;
		private List<string> _catchpoints;
		private MDbgOptions _debuggerSessionOptions => _session.Options;

		private static MDbgStopOptions stopOptions = new MDbgStopOptions();

		private System.Diagnostics.Process _process;
		private Handles<MDbgValue[]> _variableHandles;
		private Handles<MDbgFrame> _frameHandles;
		private MDbgValue _exception;
		private Dictionary<int, Thread> _seenThreads = new Dictionary<int, Thread>();
		private bool _attachMode = false;
		private bool _terminated = false;
		private bool _stderrEOF = true;
		private bool _stdoutEOF = true;

	public static MDbgEngine Debugger{
		get {
			return _session;
		}
	}

    private void Processes_ProcessAdded(object sender, ProcessCollectionChangedEventArgs e)
    {
      e.Process.Runtimes.RuntimeAdded += new EventHandler<RuntimeLoadEventArgs>(Process_RuntimeAdded);
      e.Process.Runtimes.RuntimeLoadFailed += new EventHandler<FailedRuntimeLoadEventArgs>(Runtimes_RuntimeLoadFailed);
    }

    private void Runtimes_RuntimeLoadFailed(object sender, FailedRuntimeLoadEventArgs e)
    {
      stopOptions.ActOnCallback((sender as RuntimeManager).Process, (CustomEventArgs) e);
    }

    private void Process_RuntimeAdded(object sender, RuntimeLoadEventArgs e)
    {
		ManagedRuntime runtime1 = e.Runtime as ManagedRuntime;
		if (runtime1 != null) {
			runtime1.PostDebugEvent += new PostCallbackEventHandler(PostDebugEventHandler);
		}
		NativeRuntime runtime2 = e.Runtime as NativeRuntime;
		if (runtime2 != null) {
			runtime2.CustomEvent += new EventHandler<CustomEventArgs>(nr_CustomEvent);
		}
    	stopOptions.ActOnCallback((sender as RuntimeManager).Process, (CustomEventArgs) e);
    }

    private void nr_CustomEvent(object sender, CustomEventArgs e)
    {
      stopOptions.ActOnCallback((sender as NativeRuntime).Process, e);
    }

    private void PostDebugEventHandler(object sender, CustomPostCallbackEventArgs e)
    {
		var process = sender as MDbgProcess;
		switch(e.CallbackType)
		{
			case ManagedCallbackType.OnStepComplete:
				Stopped();
				SendEvent(CreateStoppedEvent("step", e.Thread));
				_resumeEvent.Set();
				break;
			case ManagedCallbackType.OnBreakpoint:
				Stopped();
				SendEvent(CreateStoppedEvent("breakpoint", e.Thread));
				_resumeEvent.Set();
				break;
			case ManagedCallbackType.OnException:
			case ManagedCallbackType.OnException2:
				var ex = DebuggerActiveException();
				if (ex != null)
				{
					var message = ex.GetField("Message").GetStringValue(false);
					SendEvent(CreateStoppedEvent("exception", e.Thread, message));
				}
				_resumeEvent.Set();
				break;
			case ManagedCallbackType.OnCreateProcess:
				_activeFrame = null;
				_activeProcess = _session.Processes.Active;
				break;
			case ManagedCallbackType.OnProcessExit:
				DebuggerKill();
				_debuggeeKilled = true;
				Terminate("target exited");
				_resumeEvent.Set();
				break;
			case ManagedCallbackType.OnBreak:
				_resumeEvent.Set();
				break;
			case ManagedCallbackType.OnCreateThread:
				int tid = (int)e.Thread.Id;
				lock (_seenThreads) {
					_seenThreads[tid] = new Thread(tid, e.Thread.Number.ToString());
				}
				SendEvent(new ThreadEvent("started", tid));
				break;
			case ManagedCallbackType.OnThreadExit:
				int tid2 = (int)e.Thread.Id;
				lock (_seenThreads) {
					_seenThreads.Remove(tid2);
				}
				SendEvent(new ThreadEvent("exited", tid2));
				break;
			default:
				break;		
		}
		stopOptions.ActOnCallback((sender as ManagedRuntime).Process, (CustomEventArgs) e);
    }

		public NetStdDebugSession() : base()
		{
			_variableHandles = new Handles<MDbgValue[]>();
			_frameHandles = new Handles<MDbgFrame>();
			_seenThreads = new Dictionary<int, Thread>();

			
			_session = new MDbgEngine();
			_breakpoints = new SortedDictionary<long, MDbgBreakpoint>();
			_catchpoints = new List<string>();

			_session.Processes.ProcessAdded += new ProcessCollectionChangedEventHandler(Processes_ProcessAdded);

		}

		public override void Initialize(Response response, dynamic args)
		{
			OperatingSystem os = Environment.OSVersion;
			if (os.Platform != PlatformID.MacOSX && os.Platform != PlatformID.Unix && os.Platform != PlatformID.Win32NT) {
				SendErrorResponse(response, 3000, "Mono Debug is not supported on this platform ({_platform}).", new { _platform = os.Platform.ToString() }, true, true);
				return;
			}

			SendResponse(response, new Capabilities() {
				// This debug adapter does not need the configurationDoneRequest.
				supportsConfigurationDoneRequest = false,

				// This debug adapter does not support function breakpoints.
				supportsFunctionBreakpoints = false,

				// This debug adapter doesn't support conditional breakpoints.
				supportsConditionalBreakpoints = false,

				// This debug adapter does not support a side effect free evaluate request for data hovers.
				supportsEvaluateForHovers = false,

				// This debug adapter does not support exception breakpoint filters
				exceptionBreakpointFilters = new dynamic[0]
			});

			// Mono Debug is ready to accept breakpoints immediately
			SendEvent(new InitializedEvent());
		}

		public override async void Launch(Response response, dynamic args)
		{
			_attachMode = false;

			SetExceptionBreakpoints(args.__exceptionOptions);

			// validate argument 'program'
			string programPath = getString(args, "program");
			if (programPath == null) {
				SendErrorResponse(response, 3001, "Property 'program' is missing or empty.", null);
				return;
			}
			programPath = ConvertClientPathToDebugger(programPath);
			if (!File.Exists(programPath) && !Directory.Exists(programPath)) {
				SendErrorResponse(response, 3002, "Program '{path}' does not exist.", new { path = programPath });
				return;
			}

			// validate argument 'cwd'
			var workingDirectory = (string)args.cwd;
			if (workingDirectory != null) {
				workingDirectory = workingDirectory.Trim();
				if (workingDirectory.Length == 0) {
					SendErrorResponse(response, 3003, "Property 'cwd' is empty.");
					return;
				}
				workingDirectory = ConvertClientPathToDebugger(workingDirectory);
				if (!Directory.Exists(workingDirectory)) {
					SendErrorResponse(response, 3004, "Working directory '{path}' does not exist.", new { path = workingDirectory });
					return;
				}
			}

			// validate argument 'runtimeExecutable'
			var runtimeExecutable = (string)args.runtimeExecutable;
			if (runtimeExecutable != null) {
				runtimeExecutable = runtimeExecutable.Trim();
				if (runtimeExecutable.Length == 0) {
					SendErrorResponse(response, 3005, "Property 'runtimeExecutable' is empty.");
					return;
				}
				runtimeExecutable = ConvertClientPathToDebugger(runtimeExecutable);
				if (!File.Exists(runtimeExecutable)) {
					SendErrorResponse(response, 3006, "Runtime executable '{path}' does not exist.", new { path = runtimeExecutable });
					return;
				}
			}


			Dictionary<string, string> env = null;
			var environmentVariables = args.env;
			if (environmentVariables != null) {
				env = new Dictionary<string, string>();
				foreach (var entry in environmentVariables) {
					env.Add((string)entry.Name, (string)entry.Value);
				}
				if (env.Count == 0) {
					env = null;
				}
			}


			var cmdLine = new List<String>();

			// add 'runtimeArgs'
			if (args.runtimeArgs != null) {
				string[] runtimeArguments = args.runtimeArgs.ToObject<string[]>();
				if (runtimeArguments != null && runtimeArguments.Length > 0) {
					cmdLine.AddRange(runtimeArguments);
				}
			}

			// add 'program'
			if (workingDirectory == null) {
				// if no working dir given, we use the direct folder of the executable
				workingDirectory = Path.GetDirectoryName(programPath);
				cmdLine.Add(Path.GetFileName(programPath));
			}
			else {
				// if working dir is given and if the executable is within that folder, we make the program path relative to the working dir
				cmdLine.Add(Utilities.MakeRelativePath(workingDirectory, programPath));
			}

			// add 'args'
			if (args.args != null) {
				string[] arguments = args.args.ToObject<string[]>();
				if (arguments != null && arguments.Length > 0) {
					cmdLine.AddRange(arguments);
				}
			}

			// what console?
			var console = getString(args, "console", null);
			if (console == null) {
				// continue to read the deprecated "externalConsole" attribute
				bool externalConsole = getBool(args, "externalConsole", false);
				if (externalConsole) {
					console = "externalTerminal";
				}
			}

			if (console == "externalTerminal" || console == "integratedTerminal") {

				// cmdLine.Insert(0, mono_path);

				var termArgs = new {
					kind = console == "integratedTerminal" ? "integrated" : "external",
					title = "CLR Debug Console",
					cwd = workingDirectory,
					args = cmdLine.ToArray(),
					env = environmentVariables
				};

				var resp = await SendRequest("runInTerminal", termArgs);
				if (!resp.success) {
					SendErrorResponse(response, 3011, "Cannot launch debug target in terminal ({_error}).", new { _error = resp.message });
					return;
				}

			} else { // internalConsole


				if (env != null) {
					// we cannot set the env vars on the process StartInfo because we need to set StartInfo.UseShellExecute to true at the same time.
					// instead we set the env vars on MonoDebug itself because we know that MonoDebug lives as long as a debug session.
					foreach (var entry in env) {
						System.Environment.SetEnvironmentVariable(entry.Key, entry.Value);
					}
				}
				var cmdArgs = Utilities.ConcatArgs(cmdLine.Skip(1).ToArray());
	
				string debuggerVersion = VersionPolicy.GetDefaultLaunchVersion(cmdLine[0]);

				_process = new System.Diagnostics.Process();
				_process.StartInfo.CreateNoWindow = true;
				_process.StartInfo.UseShellExecute = false;
				_process.StartInfo.WorkingDirectory = workingDirectory;
				_process.StartInfo.FileName = cmdLine[0];
				_process.StartInfo.Arguments = Utilities.ConcatArgs(cmdLine.Skip(1).ToArray());

				_stdoutEOF = false;
				_process.StartInfo.RedirectStandardOutput = true;
				_process.OutputDataReceived += (object sender, System.Diagnostics.DataReceivedEventArgs e) => {
					if (e.Data == null) {
						_stdoutEOF = true;
					}
					SendOutput("stdout", e.Data);
				};

				_stderrEOF = false;
				_process.StartInfo.RedirectStandardError = true;
				_process.ErrorDataReceived += (object sender, System.Diagnostics.DataReceivedEventArgs e) => {
					if (e.Data == null) {
						_stderrEOF = true;
					}
					SendOutput("stderr", e.Data);
				};

				_process.EnableRaisingEvents = true;
				_process.Exited += (object sender, EventArgs e) => {
					Terminate("runtime process exited");
				};
				var localProcess = _session.Processes.CreateLocalProcess(new CorDebugger(debuggerVersion));

				try {
					_process.Start();
					localProcess.Attach(_process.Id);
					localProcess.AsyncStop().WaitOne();
					_process.BeginOutputReadLine();
					_process.BeginErrorReadLine();
				}
				catch (Exception e) {
					SendErrorResponse(response, 3012, "Can't launch terminal ({reason}).", new { reason = e.Message });
					return;
				}

				try {
					localProcess.Go().WaitOne();
				}
				catch (Exception e) {
					SendErrorResponse(response, 3012, "Can't launch terminal ({reason}).", new { reason = e.Message });
					return;
				}
			}

			SendResponse(response);

		}

		public override void Attach(Response response, dynamic args)
		{
			_attachMode = true;

			SetExceptionBreakpoints(args.__exceptionOptions);

			var name = getString(args, "program") ;
			if (name == null) {
				SendErrorResponse(response, 3007, "Property 'program' is missing or empty.");
				return;
			}

			var procs = System.Diagnostics.Process.GetProcessesByName(name);
			if (procs == null || procs.Length == 0)
			{
				SendErrorResponse(response, 3007, "Process could not be found");
				return;				
			}
			var proc = procs[0];
			var pid = proc.Id;
			var debuggerVersion = VersionPolicy.GetDefaultLaunchVersion(proc.MainModule.FileName);
			var localProcess = _session.Processes.CreateLocalProcess(new CorDebugger(debuggerVersion));

			try {
				localProcess.Attach(proc.Id);
				localProcess.AsyncStop().WaitOne();
				_process = proc;
			}
			catch (Exception e) {
				Console.WriteLine(e);
				SendErrorResponse(response, 3012, "Can't attach: ({reason}).", new { reason = e.Message });
				return;
			}

			// try {
			// 	localProcess.Go().WaitOne();
			// }
			// catch (Exception e) {
			// 	Console.WriteLine(e);
			// 	SendErrorResponse(response, 3012, "Can't attach: ({reason}).", new { reason = e.Message });
			// 	return;
			// }


			SendResponse(response);

		}

		public override void Disconnect(Response response, dynamic args)
		{
			if (_attachMode) {

				lock (_lock) {
					if (_session != null) {
						_debuggeeExecuting = true;
						_breakpoints.Clear();
						if (_session.Processes.HaveActive)
						{
							var bps = _session.Processes.Active.Breakpoints.UserBreakpoints.ToArray();
							foreach (var bp in bps)
							{
								bp.Delete();
							}
							if (_session.Processes.Active != null) {
								_session.Processes.Active.Detach();
								// _session.Processes.Active.Go().WaitOne();
							}
						}
						_session = new MDbgEngine();
					}
				}

			} else {
				// Let's not leave dead processes behind...
				if (_process != null) {
					_process.Kill();
					_process = null;
				} else {
					PauseDebugger();
					DebuggerKill();

					while (!_debuggeeKilled) {
						System.Threading.Thread.Sleep(10);
					}
				}
			}

			SendResponse(response);
		}

		public override void Continue(Response response, dynamic args)
		{
			WaitForSuspend();
			SendResponse(response);
			lock (_lock) {
				if (_session != null && !_session.Processes.Active.IsRunning && _session.Processes.Active.IsAlive) {
					_session.Processes.Active.Go().WaitOne();
					_debuggeeExecuting = true;
				}
			}
		}

		public override void Next(Response response, dynamic args)
		{
			WaitForSuspend();
			SendResponse(response);
			lock (_lock) {
				if (_session != null && !_session.Processes.Active.IsRunning && _session.Processes.Active.IsAlive) {
					_session.Processes.Active.StepOver(false);
					_debuggeeExecuting = true;
				}
			}
		}

		public override void StepIn(Response response, dynamic args)
		{
			WaitForSuspend();
			SendResponse(response);
			lock (_lock) {
				if (_session != null && !_session.Processes.Active.IsRunning && _session.Processes.Active.IsAlive) {
					_session.Processes.Active.StepInto(false);
					_debuggeeExecuting = true;
				}
			}
		}

		public override void StepOut(Response response, dynamic args)
		{
			WaitForSuspend();
			SendResponse(response);
			lock (_lock) {
				if (_session != null && !_session.Processes.Active.IsRunning && _session.Processes.Active.IsAlive) {
					_session.Processes.Active.StepOut();
					_debuggeeExecuting = true;
				}
			}
		}

		public override void Pause(Response response, dynamic args)
		{
			SendResponse(response);
			PauseDebugger();
		}

		public override void SetExceptionBreakpoints(Response response, dynamic args)
		{
			SetExceptionBreakpoints(args.exceptionOptions);
			SendResponse(response);
		}

		public override void SetBreakpoints(Response response, dynamic args)
		{
			string path = null;
			if (args.source != null) {
				string p = (string)args.source.path;
				if (p != null && p.Trim().Length > 0) {
					path = p;
				}
			}
			if (path == null) {
				SendErrorResponse(response, 3010, "setBreakpoints: property 'source' is empty or misformed", null, false, true);
				return;
			}
			path = ConvertClientPathToDebugger(path);

			if (!HasMonoExtension(path) || !_session.Processes.HaveActive) {
				SendResponse(response, new SetBreakpointsResponseBody());
				return;
			}

			var clientLines = args.lines.ToObject<int[]>();
			var modulePath = path;
			HashSet<ManagedBoundFunctionAndOffsetLocation> lin = new HashSet<ManagedBoundFunctionAndOffsetLocation>();
			for (int i = 0; i < clientLines.Length; i++) {
				var loc = new LineNumberLocation(path, clientLines[i]);
				var mbp = bindBreakpoint(loc);
				if (mbp != null)
				{
					modulePath = mbp.Function.Module.FileName;
					lin.Add(mbp);
				}
			}


			// find all breakpoints for the given path and remember their id and line number
			var bpts = new List<Tuple<int, ManagedBoundFunctionAndOffsetLocation>>();
			foreach (var be in _breakpoints) {
				var bp = be.Value;
				var loc = bp.Location as ManagedBoundFunctionAndOffsetLocation;
				if (loc != null && loc.Function.Module.FileName == modulePath ) {
					bpts.Add(new Tuple<int,ManagedBoundFunctionAndOffsetLocation>((int)be.Key, loc));
				}
			}
			HashSet<ManagedBoundFunctionAndOffsetLocation> lin2 = new HashSet<ManagedBoundFunctionAndOffsetLocation>();
			foreach (var bpt in bpts) {
				if (lin.Contains(bpt.Item2)) {
					lin2.Add(bpt.Item2);
				}
				else {
					// Program.Log("cleared bpt #{0} for line {1}", bpt.Item1, bpt.Item2);

					MDbgBreakpoint b;
					if (_breakpoints.TryGetValue(bpt.Item1, out b)) {
						_breakpoints.Remove(bpt.Item1);
						_session.Processes.Active.Breakpoints.UserBreakpoints.First(x => x == b).Delete();
					}
				}
			}

			for (int i = 0; i < clientLines.Length; i++) {
				var l = ConvertClientLineToDebugger(clientLines[i]);
				var loc = new LineNumberLocation(path, l);
				var mbp = bindBreakpoint(loc);

				if (!lin2.Contains(mbp)) {
					var id = _nextBreakpointId++;
					var b = _session.Processes.Active.Breakpoints.CreateBreakpoint(mbp, true);
					_breakpoints.Add(id, b);

					// Program.Log("added bpt #{0} for line {1}", id, l);
				}
			}

			var breakpoints = new List<Breakpoint>();
			foreach (var l in clientLines) {
				breakpoints.Add(new Breakpoint(true, l));
			}

			SendResponse(response, new SetBreakpointsResponseBody(breakpoints));
		}



		protected bool TryBindToModuleWorker(ManagedModule managedModule, LineNumberLocation loc, out ManagedBoundFunctionAndOffsetLocation resolvedLocation, out string bindFailedReason)
		{
			resolvedLocation = (ManagedBoundFunctionAndOffsetLocation) null;
			bindFailedReason = (string) null;
			if (managedModule.SymReader == null)
			{
				bindFailedReason = "No symbols loaded";
				return false;
			}
			bool flag = false;
			foreach (var document in managedModule.SymReader.GetDocuments())
			{
				if (string.Compare(Path.GetFileName(document.URL), Path.GetFileName(loc.FilePath), true, CultureInfo.InvariantCulture) == 0)
				{
					int num = 0;
					flag = true;
					try
					{
						num = document.FindClosestLine(loc.LineNumber);
					}
					catch (COMException ex)
					{
						if (ex.ErrorCode == -2147467259)
							continue;
					}
					var documentPosition = managedModule.SymReader.GetMethodFromDocumentPosition(document, num, 0);
					ManagedFunction function1 = managedModule.GetFunction(documentPosition.Token.GetToken());
					int ipFromPosition1; 
					
					if (!function1.GetIPFromLine(num, out ipFromPosition1))
					{
						foreach (Type nestedType in function1.MethodInfo.DeclaringType.GetNestedTypes())
						{
							foreach (MethodInfo method in nestedType.GetMethods())
							{
								ManagedFunction function2 = managedModule.GetFunction(method.MetadataToken);
								int ipFromPosition2;
								if (function2.GetIPFromLine(num, out ipFromPosition2))
								{
									resolvedLocation = new ManagedBoundFunctionAndOffsetLocation(function2, ipFromPosition2, OffsetKind.IL);
									return true;
								}
							}
					}
					bindFailedReason = "Unable to determine IL offset from line number " + (object) num;
					return false;
				}
				resolvedLocation = new ManagedBoundFunctionAndOffsetLocation(function1, ipFromPosition1, OffsetKind.IL);
				return true;
				}
			}
			bindFailedReason = !flag ? "Symbols did not have information for file " + loc.FilePath : "No information for line " + (object) loc.LineNumber;
			return false;
		}
	

		public ManagedBoundFunctionAndOffsetLocation bindBreakpoint(LineNumberLocation loc)
		{
			if (!_session.Processes.HaveActive || _session.Processes.Active.TemporaryDefaultManagedRuntime == null)
			{
				return null;
			}
			foreach (ManagedModule module in _session.Processes.Active.TemporaryDefaultManagedRuntime.Modules)
			{
				string err;
				ManagedBoundFunctionAndOffsetLocation outval;
				if(TryBindToModuleWorker(module, loc, out outval, out err))
				{
					return outval;
				}
			}

			return null;			
		}


		public override void StackTrace(Response response, dynamic args)
		{
			int maxLevels = getInt(args, "levels", 10);
			int threadReference = getInt(args, "threadId", 0);

			WaitForSuspend();

			var thread = DebuggerActiveThread();
			if (thread.Id != threadReference) {
				// Program.Log("stackTrace: unexpected: active thread should be the one requested");
				thread = FindThread(threadReference);
				if (thread != null) {
					_session.Processes.Active.Threads.Active = thread;
				}
			}

			var stackFrames = new List<StackFrame>();
			int totalFrames = 0;

			var bt = thread.Frames;
			if (bt != null && bt.Count() >= 0) {

				totalFrames = bt.Count();

				for (var i = 0; i < Math.Min(totalFrames, maxLevels); i++) {

					var frame = thread.Frames.ElementAt(i);

					string path = frame.SourcePosition.Path;

					var hint = "subtle";
					Source source = null;
					if (!string.IsNullOrEmpty(path)) {
						string sourceName = Path.GetFileName(path);
						if (!string.IsNullOrEmpty(sourceName)) {
							if (File.Exists(path)) {
								source = new Source(sourceName, ConvertDebuggerPathToClient(path), 0, "normal");
								hint = "normal";
							} else {
								source = new Source(sourceName, null, 1000, "deemphasize");
							}
						}
					}

					var frameHandle = _frameHandles.Create(frame);
					Console.WriteLine(frameHandle);
					Console.WriteLine(frame.Number);
					Console.WriteLine(frame.GetHashCode());
					Console.WriteLine("---");
					string name = frame.Function.FullName;
					int line = frame.SourcePosition.Line;
					stackFrames.Add(new StackFrame(frameHandle, name, source, ConvertDebuggerLineToClient(line), 0, hint));
				}
			}

			SendResponse(response, new StackTraceResponseBody(stackFrames, totalFrames));
		}

		public override void Source(Response response, dynamic arguments) {
			SendErrorResponse(response, 1020, "No source available");
		}

		public override void Scopes(Response response, dynamic args) {

			int frameId = getInt(args, "frameId", 0);
			var frame = _frameHandles.Get(frameId, null);

			Console.WriteLine(_session.Processes.Active.Threads.Active.Id);

			var scopes = new List<Scope>();
			if (!_session.Processes.Active.Threads.Active.Frames.Any(x => x == frame))
			{
				Console.WriteLine("Frame not found!! " +frameId);
				SendResponse(response, new ScopesResponseBody(scopes));
				return;			
			}
			if (frame.Number == 0 && _exception != null) {
				scopes.Add(new Scope("Exception", _variableHandles.Create(new MDbgValue[] { _exception })));
			}

			var locals = frame.GetArguments().Concat(frame.GetActiveLocalVariables()).Where(x => x != null).ToArray();
			Console.WriteLine(locals);
			Console.WriteLine(string.Join(",", locals.Select(x => x.ToString())));
			if (locals.Length > 0) {
				scopes.Add(new Scope("Local", _variableHandles.Create(locals)));
			}

			SendResponse(response, new ScopesResponseBody(scopes));
		}

		public override void Variables(Response response, dynamic args)
		{
			int reference = getInt(args, "variablesReference", -1);
			if (reference == -1) {
				SendErrorResponse(response, 3009, "variables: property 'variablesReference' is missing", null, false, true);
				return;
			}

			WaitForSuspend();
			var variables = new List<Variable>();

			MDbgValue[] children;
			Console.WriteLine(_variableHandles);
			Console.WriteLine(reference);
			if (_variableHandles.TryGet(reference, out children)) {
				Console.WriteLine(children);
				if (children != null && children.Length > 0) {
					Console.WriteLine(children.Length);
					bool more = false;
					if (children.Length > MAX_CHILDREN) {
						children = children.Take(MAX_CHILDREN).ToArray();
						more = true;
					}

					if (children.Length < 20) {
						// Wait for all values at once.
						// FIXME
						// WaitHandle.WaitAll(children.Select(x => x.WaitHandle).ToArray());
						foreach (var v in children) {
							Console.WriteLine(v);
							
							variables.Add(CreateVariable(v));
							Console.WriteLine("---");
						}
					}
					else {
						foreach (var v in children) {
							// v.WaitHandle.WaitOne();
							variables.Add(CreateVariable(v));
						}
					}

					if (more) {
						variables.Add(new Variable("...", null, null));
					}
				}
			}

			SendResponse(response, new VariablesResponseBody(variables));
		}

		public override void Threads(Response response, dynamic args)
		{
			var threads = new List<Thread>();
			var process = _activeProcess;
			if (process != null) {
				Dictionary<int, Thread> d;
				lock (_seenThreads) {
					d = new Dictionary<int, Thread>(_seenThreads);
				}
				foreach (var t in process.Threads.AsEnumerable()) {
					int tid = (int)t.Id;
					d[tid] = new Thread(tid,  t.Number.ToString());
				}
				threads = d.Values.ToList();
			}
			SendResponse(response, new ThreadsResponseBody(threads));
		}

		public override void Evaluate(Response response, dynamic args)
		{
			string error = null;

			var expression = getString(args, "expression");
			if (expression == null) {
				error = "expression missing";
			} else {
				int frameId = getInt(args, "frameId", -1);
				var frame = _frameHandles.Get(frameId, null);
				CorEval eval = _session.Processes.Active.Threads.Active.Get<ManagedThread>().CorThread.CreateEval();
				List<CorValue> corValueList = new List<CorValue>();
				if (frame != null) {
					Console.WriteLine(expression);

					var val = _session.Processes.Active.ResolveVariable(expression, frame);
					var stringValue = "<N/A>";
					int handle = 0;
					if (val != null)
					{
						var mval = val as ManagedValue;
						var wrapped = CreateVariable(mval);
						stringValue = wrapped.value;
						handle = wrapped.variablesReference;
					} else if (val != null) {
						stringValue = val.ToString();
					}
					SendResponse(response, new EvaluateResponseBody(stringValue, handle));
					return;
				}
				else {
					error = "no active stackframe";
				}
			}
			SendErrorResponse(response, 3014, "Evaluate request failed ({_reason}).", new { _reason = error } );
		}


		private void SetExceptionBreakpoints(dynamic exceptionOptions)
		{
			if (exceptionOptions != null) {

				// clear all existig catchpoints
				foreach (var cp in _catchpoints) {					
					stopOptions.ModifyOptions("ca",  MDbgStopOptionPolicy.DebuggerBehavior.Ignore, cp);
				}
				_catchpoints.Clear();

				var exceptions = exceptionOptions.ToObject<dynamic[]>();
				for (int i = 0; i < exceptions.Length; i++) {

					var exception = exceptions[i];

					string exName = null;
					string exBreakMode = exception.breakMode;

					if (exception.path != null) {
						var paths = exception.path.ToObject<dynamic[]>();
						var path = paths[0];
						if (path.names != null) {
							var names = path.names.ToObject<dynamic[]>();
							if (names.Length > 0) {
								exName = names[0];
							}
						}
					}

					if (exName != null && exBreakMode == "always") {
						stopOptions.ModifyOptions("ca",  MDbgStopOptionPolicy.DebuggerBehavior.Stop, exName);
					}
				}
			}
		}

		private void SendOutput(string category, string data) {
			if (!String.IsNullOrEmpty(data)) {
				if (data[data.Length-1] != '\n') {
					data += '\n';
				}
				SendEvent(new OutputEvent(category, data));
			}
		}

		private void Terminate(string reason) {
			if (!_terminated) {

				// wait until we've seen the end of stdout and stderr
				for (int i = 0; i < 100 && (_stdoutEOF == false || _stderrEOF == false); i++) {
					System.Threading.Thread.Sleep(100);
				}

				SendEvent(new TerminatedEvent());

				_terminated = true;
				_process = null;
			}
		}

		private StoppedEvent CreateStoppedEvent(string reason, MDbgThread ti, string text = null)
		{
			return new StoppedEvent((int)ti.Id, reason, text);
		}

		private MDbgThread FindThread(int threadReference)
		{
			if (_activeProcess != null) {
				foreach (var t in _activeProcess.Threads) {
					if (t.Id == threadReference) {
						return t;
					}
				}
			}
			return null;
		}

		private void Stopped()
		{
			_exception = null;
			_variableHandles.Reset();
			_frameHandles.Reset();
		}

		private Variable CreateVariable(MDbgValue v)
		{
			var mv = v as ManagedValue;
			Console.WriteLine(mv.TypeName);
			bool hasChildren = false;
			int childrenRef = 0;

			if (mv.IsComplexType)
			{
				hasChildren = true;
				Console.WriteLine(mv.GetFields());
				childrenRef = _variableHandles.Create(mv.GetFields().OfType<MDbgValue>().ToArray());
			}
			if (mv.IsArrayType)
			{
				hasChildren = true;
				childrenRef = _variableHandles.Create(mv.GetArrayItems());				
			}
			var dv = mv.GetStringValue(false);
			Console.WriteLine(dv);
			if (dv.Length > 1 && dv [0] == '{' && dv [dv.Length - 1] == '}') {
				dv = dv.Substring (1, dv.Length - 2);
			}
			return new Variable(v.Name, dv, v.TypeName, childrenRef);
		}

		private bool HasMonoExtension(string path)
		{
			foreach (var e in MONO_EXTENSIONS) {
				if (path.EndsWith(e)) {
					return true;
				}
			}
			return false;
		}

		private static bool getBool(dynamic container, string propertyName, bool dflt = false)
		{
			try {
				return (bool)container[propertyName];
			}
			catch (Exception) {
				// ignore and return default value
			}
			return dflt;
		}

		private static int getInt(dynamic container, string propertyName, int dflt = 0)
		{
			try {
				return (int)container[propertyName];
			}
			catch (Exception) {
				// ignore and return default value
			}
			return dflt;
		}

		private static string getString(dynamic args, string property, string dflt = null)
		{
			var s = (string)args[property];
			if (s == null) {
				return dflt;
			}
			s = s.Trim();
			if (s.Length == 0) {
				return dflt;
			}
			return s;
		}

		//-----------------------

		private void WaitForSuspend()
		{
			if (_debuggeeExecuting) {
				_resumeEvent.WaitOne();
				_debuggeeExecuting = false;
			}
		}

		private MDbgThread DebuggerActiveThread()
		{
			lock (_lock) {
				return _session == null ? null : _session.Processes.Active.Threads.Active;
			}
		}

		private StackDiagnostics DebuggerActiveBacktrace() {
			var thr = DebuggerActiveThread();
			return thr == null ? null : thr.StackDiagnostics;
		}

		private MDbgFrame DebuggerActiveFrame() {
			if (_activeFrame != null)
				return _activeFrame;

			var thread = DebuggerActiveThread();
			var bt = thread.CurrentFrame;
			if (bt != null)
				return _activeFrame = bt;

			return null;
		}

		private ManagedValue DebuggerActiveException() {
			var thr = DebuggerActiveThread();
			return thr == null ? null : thr.CurrentException;
		}

		private void Connect(IPAddress address, int port)
		{
			lock (_lock) {

				_debuggeeKilled = false;

				// var args0 = new Mono.Debugging.Soft.SoftDebuggerConnectArgs(string.Empty, address, port) {
				// 	MaxConnectionAttempts = MAX_CONNECTION_ATTEMPTS,
				// 	TimeBetweenConnectionAttempts = CONNECTION_ATTEMPT_INTERVAL
				// };

				// _session.
				// Run(new Mono.Debugging.Soft.SoftDebuggerStartInfo(args0), _debuggerSessionOptions);

				// _debuggeeExecuting = true;
			}
		}

		private void PauseDebugger()
		{
			lock (_lock) {
				if (_session != null && !_session.Processes.Active.IsRunning && _session.Processes.Active.IsAlive) {
					_session.Processes.Active.AsyncStop().WaitOne();
				}
			}
		}

		private void DebuggerKill()
		{
			lock (_lock) {
				if (_session != null) {

					_debuggeeExecuting = true;

					if (_session.Processes.Active.IsAlive)
					{
						_session.Processes.Active.Kill().WaitOne();
					}
					// _session.Dispose();
					_session = null;
				}
			}
		}
	}


}
