// TouchRunner.cs: MonoTouch.Dialog-based driver to run unit tests
//
// Authors:
//	Sebastien Pouliot  <sebastien@xamarin.com>
//
// Copyright 2011-2013 Xamarin Inc.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//   http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//

using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

#if XAMCORE_2_0
using Foundation;
using ObjCRuntime;
using UIKit;
using Constants = global::ObjCRuntime.Constants;
#else
using MonoTouch.Foundation;
using MonoTouch.ObjCRuntime;
using MonoTouch.UIKit;
using Constants = global::MonoTouch.Constants;
#endif

#if !__WATCHOS__
using MonoTouch.Dialog;
#endif

using NUnit.Framework.Api;
using NUnit.Framework.Internal;
using NUnit.Framework.Internal.Commands;
using NUnit.Framework.Internal.WorkItems;

namespace MonoTouch.NUnit.UI {
	public abstract class BaseTouchRunner : ITestListener {
		TestSuite suite = new TestSuite (String.Empty);
		ITestFilter filter = TestFilter.Empty;
		bool connection_failure;

		public int PassedCount { get; private set; }
		public int FailedCount { get; private set; }
		public int IgnoredCount { get; private set; }
		public int InconclusiveCount { get; private set; }
		public int TestCount {
			get {
				return suite.TestCaseCount;
			}
		}
		public TestSuite Suite { get { return suite; } }

		public bool AutoStart {
			get { return TouchOptions.Current.AutoStart; }
			set { TouchOptions.Current.AutoStart = value; }
		}

		public ITestFilter Filter {
			get { return filter; }
			set { filter = value; }
		}

		public bool TerminateAfterExecution {
			get { return TouchOptions.Current.TerminateAfterExecution && !connection_failure; }
			set { TouchOptions.Current.TerminateAfterExecution = value; }
		}

		List<Assembly> assemblies = new List<Assembly> ();
		List<string> fixtures;

		public void Add (Assembly assembly)
		{
			if (assembly == null)
				throw new ArgumentNullException ("assembly");

			assemblies.Add (assembly);
		}

		public void Add (Assembly assembly, IList<string> fixtures)
		{
			Add (assembly);
			if (fixtures != null) {
				if (this.fixtures == null) {
					this.fixtures = new List<string> (fixtures);
				} else {
					this.fixtures.AddRange (fixtures);
				}
			}
		}

		[DllImport ("libc")]
		static extern void exit (int code);
		protected virtual void TerminateWithSuccess ()
		{
			// For WatchOS we're terminating the extension, not the watchos app itself.
			Console.WriteLine ("Exiting test run with success");
			exit (0);
		}

		protected virtual void ExecuteOnMainThread (Action action)
		{
			var obj = new NSObject ();
			obj.BeginInvokeOnMainThread (() =>
			{
				action ();
				obj.Dispose ();
			});
		}

		public void LoadSync ()
		{
			foreach (Assembly assembly in assemblies)
				Load (assembly, fixtures == null ? null : new Dictionary<string, IList<string>> () { { "LOAD", fixtures } });
			assemblies.Clear ();
		}

		public void AutoRun ()
		{
			if (!AutoStart)
				return;

			ExecuteOnMainThread (() => {
				Run ();

				// optionally end the process, e.g. click "Touch.Unit" -> log tests results, return to springboard...
				// http://stackoverflow.com/questions/1978695/uiapplication-sharedapplication-terminatewithsuccess-is-not-there
				if (TerminateAfterExecution) {
					if (WriterFinishedTask != null) {
						Task.Run (async () => {
							await WriterFinishedTask;
							TerminateWithSuccess ();
						});
					} else {
						TerminateWithSuccess ();
					}
				}
			});
		}

		bool running;
		public void Run ()
		{
			if (running) {
				Console.WriteLine ("Not running because another test run is already in progress.");
				return;
			}

			running = true;
			if (!OpenWriter ("Run Everything")) {
				running = false;
				return;
			}

			try {
				Run (suite);
			} finally {
				CloseWriter ();
				running = false;
			}
		}

#region writer

		public TestResult Result { get; set; }

		public TextWriter Writer { get; set; }

		Task WriterFinishedTask { get; set; }

		static string SelectHostName (string[] names, int port)
		{
			if (names.Length == 0)
				return null;

			if (names.Length == 1)
				return names [0];

			object lock_obj = new object ();
			string result = null;
			int failures = 0;

			using (var evt = new ManualResetEvent (false)) {
				for (int i = names.Length - 1; i >= 0; i--) {
					var name = names [i];
					ThreadPool.QueueUserWorkItem ((v) =>
						{
							try {
								var client = new TcpClient (name, port);
								using (var writer = new StreamWriter (client.GetStream ())) {
									writer.WriteLine ("ping");
								}
								lock (lock_obj) {
									if (result == null)
										result = name;
								}
								evt.Set ();
							} catch (Exception) {
								lock (lock_obj) {
									failures++;
									if (failures == names.Length)
										evt.Set ();
								}
							}
						});
				}

				// Wait for 1 success or all failures
				evt.WaitOne ();
			}

			return result;
		}

		public bool OpenWriter (string message)
		{
			TouchOptions options = TouchOptions.Current;
			DateTime now = DateTime.Now;
			// let the application provide it's own TextWriter to ease automation with AutoStart property
			if (Writer == null) {
				if (options.ShowUseNetworkLogger) {
					try {
						string hostname = null;
						WriterFinishedTask = null;
						TextWriter defaultWriter = null;
						switch (options.Transport) {
						case "FILE":
							Console.WriteLine ("[{0}] Sending '{1}' results to the file {2}", now, message, options.LogFile);
							defaultWriter = new StreamWriter (options.LogFile, true, System.Text.Encoding.UTF8)
							{
								AutoFlush = true,
							};
							break;
						case "HTTP":
							var hostnames = options.HostName.Split (',');
							hostname = hostnames [0];
							if (hostnames.Length > 1)
								Console.WriteLine ("[{0}] Found multiple host names ({1}); will only try sending to the first ({2})", now, options.HostName, hostname);
							Console.WriteLine ("[{0}] Sending '{1}' results to {2}:{3}", now, message, hostname, options.HostPort);
							var w = new HttpTextWriter ()
							{
								HostName = hostname,
								Port = options.HostPort,
							};
							w.Open ();
							defaultWriter = w;
							WriterFinishedTask = w.FinishedTask;
							break;
						default:
							Console.WriteLine ("Unknown transport '{0}': switching to default (TCP)", options.Transport);
							goto case "TCP";
						case "TCP":
							hostname = SelectHostName (options.HostName.Split (','), options.HostPort);
							if (string.IsNullOrEmpty (hostname))
								break;
							Console.WriteLine ("[{0}] Sending '{1}' results to {2}:{3}", now, message, hostname, options.HostPort);
							defaultWriter = new TcpTextWriter (hostname, options.HostPort);
							break;
						}
						if (options.EnableXml) {
							Writer = new NUnitOutputTextWriter (
								this, defaultWriter, new NUnitLite.Runner.NUnit2XmlOutputWriter (DateTime.UtcNow), options.XmlMode);
						} else {
							Writer = defaultWriter;
						}
					} catch (Exception ex) {
						connection_failure = true;
						if (!ShowConnectionErrorAlert (options.HostName, options.HostPort, ex))
							return false;

						Console.WriteLine ("Network error: Cannot connect to {0}:{1}: {2}. Continuing on console.", options.HostName, options.HostPort, ex);
						Writer = Console.Out;
					}
				}
			}

			if (Writer == null)
				Writer = Console.Out;

			Writer.WriteLine ("[Runner executing:\t{0}]", message);
			Writer.WriteLine ("[MonoTouch Version:\t{0}]", Constants.Version);
			Writer.WriteLine ("[Assembly:\t{0}.dll ({1} bits)]", typeof (NSObject).Assembly.GetName ().Name, IntPtr.Size * 8);
			Writer.WriteLine ("[GC:\t{0}]", GC.MaxGeneration == 0 ? "Boehm": "sgen");
			WriteDeviceInformation (Writer);
			Writer.WriteLine ("[Device Locale:\t{0}]", NSLocale.CurrentLocale.Identifier);
			Writer.WriteLine ("[Device Date/Time:\t{0}]", now); // to match earlier C.WL output

			Writer.WriteLine ("[Bundle:\t{0}]", NSBundle.MainBundle.BundleIdentifier);
			// FIXME: add data about how the app was compiled (e.g. ARMvX, LLVM, GC and Linker options)
			PassedCount = 0;
			IgnoredCount = 0;
			FailedCount = 0;
			InconclusiveCount = 0;
			return true;
		}

		// returns true if test run should still start
		bool ShowConnectionErrorAlert (string hostname, int port, Exception ex)
		{
#if __TVOS__ || __WATCHOS__
			return true;
#else
			// Don't show any alerts if we're running automated.
			if (AutoStart)
				return true;

			// UIAlert is not available for extensions.
			if (NSBundle.MainBundle.BundlePath.EndsWith (".appex", StringComparison.Ordinal))
				return true;
			
			Console.WriteLine ("Network error: Cannot connect to {0}:{1}: {2}.", hostname, port, ex);
			UIAlertView alert = new UIAlertView ("Network Error",
				String.Format ("Cannot connect to {0}:{1}: {2}. Continue on console ?", hostname, port, ex.Message),
				(IUIAlertViewDelegate) null, "Cancel", "Continue");
			int button = -1;
			alert.Clicked += delegate (object sender, UIButtonEventArgs e)
			{
				button = (int) e.ButtonIndex;
			};
			alert.Show ();
			while (button == -1)
				NSRunLoop.Current.RunUntil (NSDate.FromTimeIntervalSinceNow (0.5));
			Console.WriteLine (button);
			Console.WriteLine ("[Host unreachable: {0}]", button == 0 ? "Execution cancelled" : "Switching to console output");
			return button != 0;
#endif
		}

		protected abstract void WriteDeviceInformation (TextWriter writer);

		public void CloseWriter ()
		{
			int total = PassedCount + InconclusiveCount + FailedCount; // ignored are *not* run
			Writer.WriteLine ("Tests run: {0} Passed: {1} Inconclusive: {2} Failed: {3} Ignored: {4}", total, PassedCount, InconclusiveCount, FailedCount, IgnoredCount);

			// In some cases, the close is not correctly implemented and we might get a InvalidOperationException, we try to close and then null the obj for it to be
			// GC.
			try {
				Writer.Close ();
			} finally {
				Writer = null;
			}
		}

#endregion

		public void TestStarted (ITest test)
		{
			if (test is TestSuite) {
				Writer.WriteLine ();
				Writer.WriteLine (test.Name);
			}
		}

		public virtual void TestFinished (ITestResult r)
		{
			TestResult result = r as TestResult;

			if (result.Test is TestSuite) {
				if (!result.IsFailure () && !result.IsSuccess () && !result.IsInconclusive () && !result.IsIgnored ())
					Writer.WriteLine ("\t[INFO] {0}", result.Message);

				string name = result.Test.Name;
				if (!String.IsNullOrEmpty (name))
					Writer.WriteLine ("{0} : {1} ms", name, result.Duration.TotalMilliseconds);
			} else {
				if (result.IsSuccess ()) {
					Writer.Write ("\t[PASS] ");
					PassedCount++;
				} else if (result.IsIgnored ()) {
					Writer.Write ("\t[IGNORED] ");
					IgnoredCount++;
				} else if (result.IsFailure ()) {
					Writer.Write ("\t[FAIL] ");
					FailedCount++;
				} else if (result.IsInconclusive ()) {
					Writer.Write ("\t[INCONCLUSIVE] ");
					InconclusiveCount++;
				} else {
					Writer.Write ("\t[INFO] ");
				}
				Writer.Write (result.Test.FixtureType.Name);
				Writer.Write (".");
				Writer.Write (result.Test.Name);

				string message = result.Message;
				if (!String.IsNullOrEmpty (message)) {
					Writer.Write (" : {0}", message.Replace ("\r\n", "\\r\\n"));
				}
				Writer.WriteLine ();

				string stacktrace = result.StackTrace;
				if (!String.IsNullOrEmpty (result.StackTrace)) {
					string[] lines = stacktrace.Split (new char [] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
					foreach (string line in lines)
						Writer.WriteLine ("\t\t{0}", line);
				}
			}
		}

		NUnitLiteTestAssemblyBuilder builder = new NUnitLiteTestAssemblyBuilder ();
		Dictionary<string, object> empty = new Dictionary<string, object> ();

		public bool Load (string assemblyName, IDictionary settings)
		{
			return AddSuite (builder.Build (assemblyName, settings ?? empty));
		}

		public bool Load (Assembly assembly, IDictionary settings)
		{
			return AddSuite (builder.Build (assembly, settings ?? empty));
		}

		bool AddSuite (TestSuite ts)
		{
			if (ts == null)
				return false;
			suite.Add (ts);
			return true;
		}

		public TestResult Run (Test test)
		{
			PassedCount = 0;
			IgnoredCount = 0;
			FailedCount = 0;
			InconclusiveCount = 0;

			Result = null;
			TestExecutionContext current = TestExecutionContext.CurrentContext;
			current.WorkDirectory = Environment.CurrentDirectory;
			current.Listener = this;
			WorkItem wi = test.CreateWorkItem (filter, new FinallyDelegate ());
			wi.Execute (current);
			Result = wi.Result;
			return Result;
		}

		public ITest LoadedTest {
			get {
				return suite;
			}
		}

		public void TestOutput (TestOutput testOutput)
		{
		}
	}

#if __WATCHOS__
	public class WatchOSRunner : BaseTouchRunner {
		protected override void WriteDeviceInformation (TextWriter writer)
		{
			var device = WatchKit.WKInterfaceDevice.CurrentDevice;
			writer.WriteLine ("[{0}:\t{1} v{2}]", device.Model, device.SystemName, device.SystemVersion);
			writer.WriteLine ("[Device Name:\t{0}]", device.Name);
		}
	}
#endif
	
#if !__WATCHOS__
	public class ConsoleRunner : BaseTouchRunner {
		protected override void WriteDeviceInformation (TextWriter writer)
		{
			UIDevice device = UIDevice.CurrentDevice;
			writer.WriteLine ("[{0}:\t{1} v{2}]", device.Model, device.SystemName, device.SystemVersion);
			writer.WriteLine ("[Device Name:\t{0}]", device.Name);
		}
	}

	public class TouchRunner : BaseTouchRunner {
		
		UIWindow window;

		[CLSCompliant (false)]
		public TouchRunner (UIWindow window)
		{
			if (window == null)
				throw new ArgumentNullException ("window");
			
			this.window = window;
		}
				
		[CLSCompliant (false)]
		public UINavigationController NavigationController {
			get { return (UINavigationController) window.RootViewController; }
		}

		protected override void TerminateWithSuccess ()
		{
			Selector selector = new Selector ("terminateWithSuccess");
			UIApplication.SharedApplication.PerformSelector (selector, UIApplication.SharedApplication, 0);						
		}

		[CLSCompliant (false)]
		public UIViewController GetViewController ()
		{
			var menu = new RootElement ("Test Runner");
			
			Section main = new Section ("Loading test suites...");
			menu.Add (main);
			
			Section options = new Section () {
				new StyledStringElement ("Options", Options) { Accessory = UITableViewCellAccessory.DisclosureIndicator },
				new StyledStringElement ("Credits", Credits) { Accessory = UITableViewCellAccessory.DisclosureIndicator }
			};
			menu.Add (options);

			// large unit tests applications can take more time to initialize
			// than what the iOS watchdog will allow them on devices, so loading
			// must be done async.

			// ensure that the dialog's view has been loaded so we can call Reload later
			var dialog = new DialogViewController (menu) { Autorotate = true };
			var dialogView = dialog.View;

			ThreadPool.QueueUserWorkItem ((v) => {
				LoadSync ();

				ExecuteOnMainThread (() =>
				{
					foreach (TestSuite ts in Suite.Tests) {
						main.Add (Setup (ts));
					}

					main.Caption = null;
					menu.Reload (main, UITableViewRowAnimation.Fade);

					options.Insert (0, new StringElement ("Run Everything", Run));
					menu.Reload (options, UITableViewRowAnimation.Fade);

					AutoRun ();
				});
			});

			return dialog;
		}

		void Options ()
		{
			NavigationController.PushViewController (TouchOptions.Current.GetViewController (), true);				
		}
		
		void Credits ()
		{
			var title = new MultilineElement ("Touch.Unit Runner\nCopyright 2011-2012 Xamarin Inc.\nAll rights reserved.");
			title.Alignment = UITextAlignment.Center;
			
			var root = new RootElement ("Credits") {
				new Section () { title },
				new Section () {
#if TVOS
					new StringElement ("About Xamarin: https://www.xamarin.com"),
					new StringElement ("About MonoTouch: https://ios.xamarin.com"),
					new StringElement ("About MonoTouch.Dialog: https://github.com/migueldeicaza/MonoTouch.Dialog"),
					new StringElement ("About NUnitLite: http://www.nunitlite.org"),
					new StringElement ("About Font Awesome: https://fortawesome.github.com/Font-Awesome")
#else
					new HtmlElement ("About Xamarin", "https://www.xamarin.com"),
					new HtmlElement ("About MonoTouch", "https://ios.xamarin.com"),
					new HtmlElement ("About MonoTouch.Dialog", "https://github.com/migueldeicaza/MonoTouch.Dialog"),
					new HtmlElement ("About NUnitLite", "http://www.nunitlite.org"),
					new HtmlElement ("About Font Awesome", "https://fortawesome.github.com/Font-Awesome")
#endif
				}
			};
				
			var dv = new DialogViewController (root, true) { Autorotate = true };
			NavigationController.PushViewController (dv, true);				
		}

		Dictionary<TestSuite, TouchViewController> suites_dvc = new Dictionary<TestSuite, TouchViewController> ();
		Dictionary<TestSuite, TestSuiteElement> suite_elements = new Dictionary<TestSuite, TestSuiteElement> ();
		Dictionary<TestMethod, TestCaseElement> case_elements = new Dictionary<TestMethod, TestCaseElement> ();
		
		public void Show (TestSuite suite)
		{
			NavigationController.PushViewController (suites_dvc [suite], true);
		}
	
		TestSuiteElement Setup (TestSuite suite)
		{
			TestSuiteElement tse = new TestSuiteElement (suite, this);
			suite_elements.Add (suite, tse);
			
			var root = new RootElement ("Tests");
		
			Section section = new Section (suite.Name);
			foreach (ITest test in suite.Tests) {
				TestSuite ts = (test as TestSuite);
				if (ts != null) {
					section.Add (Setup (ts));
				} else {
					TestMethod tc = (test as TestMethod);
					if (tc != null) {
						section.Add (Setup (tc));
					} else {
						throw new NotImplementedException (test.GetType ().ToString ());
					}
				}
			}
		
			root.Add (section);
			
			if (section.Count > 1) {
				Section options = new Section () {
					new StringElement ("Run all", delegate () {
						if (OpenWriter (suite.Name)) {
							Run (suite);
							CloseWriter ();
							suites_dvc [suite].Filter ();
						}
					})
				};
				root.Add (options);
			}

			suites_dvc.Add (suite, new TouchViewController (root));
			return tse;
		}
		
		TestCaseElement Setup (TestMethod test)
		{
			TestCaseElement tce = new TestCaseElement (test, this);
			case_elements.Add (test, tce);
			return tce;
		}

		public override void TestFinished (ITestResult r)
		{
			base.TestFinished (r);

			TestResult result = r as TestResult;
			TestSuite ts = result.Test as TestSuite;
			if (ts != null) {
				TestSuiteElement tse;
				if (suite_elements.TryGetValue (ts, out tse))
					tse.Update (result);
			} else {
				TestMethod tc = result.Test as TestMethod;
				if (tc != null)
					case_elements [tc].Update (result);
			}
		}

		protected override void WriteDeviceInformation (TextWriter writer)
		{
			UIDevice device = UIDevice.CurrentDevice;
			writer.WriteLine ("[{0}:\t{1} v{2}]", device.Model, device.SystemName, device.SystemVersion);
			writer.WriteLine ("[Device Name:\t{0}]", device.Name);
			writer.WriteLine ("[Device UDID:\t{0}]", UniqueIdentifier);
		}

		[System.Runtime.InteropServices.DllImport ("/usr/lib/libobjc.dylib")]
		static extern IntPtr objc_msgSend (IntPtr receiver, IntPtr selector);

		// Apple blacklisted `uniqueIdentifier` (for the appstore) but it's still 
		// something useful to have inside the test logs
		static string UniqueIdentifier {
			get {
				IntPtr handle = UIDevice.CurrentDevice.Handle;
				if (UIDevice.CurrentDevice.RespondsToSelector (new Selector ("uniqueIdentifier")))
					return NSString.FromHandle (objc_msgSend (handle, Selector.GetHandle("uniqueIdentifier")));
				return "unknown";
			}
		}

		protected override void ExecuteOnMainThread (Action action)
		{
			window.BeginInvokeOnMainThread (() => action ());
		}
	}
#endif
}
