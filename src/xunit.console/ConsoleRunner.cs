﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Xunit.ConsoleClient
{
    class ConsoleRunner
    {
        volatile bool cancel;
        readonly ConcurrentDictionary<string, ExecutionSummary> completionMessages = new ConcurrentDictionary<string, ExecutionSummary>();
        bool failed;
        IRunnerLogger logger;
        IMessageSinkWithTypes reporterMessageHandler;

        public int EntryPoint(string[] args)
        {
            try
            {
                var reporters = GetAvailableRunnerReporters();

                if (args.Length == 0 || args[0] == "-?" || args[0] == "/?" || args[0] == "-h" || args[0] == "--help")
                {
                    PrintHeader();
                    PrintUsage(reporters);
                    return 2;
                }

#if NET452
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
#endif

                Console.CancelKeyPress += (sender, e) =>
                {
                    if (!cancel)
                    {
                        Console.WriteLine("Canceling... (Press Ctrl+C again to terminate)");
                        cancel = true;
                        e.Cancel = true;
                    }
                };

                var defaultDirectory = Directory.GetCurrentDirectory();
                if (!defaultDirectory.EndsWith(new string(new[] { Path.DirectorySeparatorChar }), StringComparison.Ordinal))
                    defaultDirectory += Path.DirectorySeparatorChar;

                var commandLine = CommandLine.Parse(reporters, args);

                if (commandLine.Debug)
                    Debugger.Launch();

                logger = new ConsoleRunnerLogger(!commandLine.NoColor);
                reporterMessageHandler = MessageSinkWithTypesAdapter.Wrap(commandLine.Reporter.CreateMessageHandler(logger));

                if (!commandLine.NoLogo)
                    PrintHeader();

                var failCount = RunProject(commandLine.Project, commandLine.Serialize, commandLine.ParallelizeAssemblies,
                                           commandLine.ParallelizeTestCollections, commandLine.MaxParallelThreads,
                                           commandLine.DiagnosticMessages, commandLine.NoColor, commandLine.NoAppDomain,
                                           commandLine.FailSkips, commandLine.InternalDiagnosticMessages);

                if (commandLine.Wait)
                {
                    Console.WriteLine();
                    Console.Write("Press any key to continue...");
                    Console.ReadKey();
                    Console.WriteLine();
                }

                return failCount > 0 ? 1 : 0;
            }
            catch (ArgumentException ex)
            {
                Console.WriteLine($"error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return 3;
            }
            catch (BadImageFormatException ex)
            {
                Console.WriteLine(ex.Message);
                return 4;
            }
            finally
            {
                ConsoleHelper.ResetColor();
            }
        }

        List<IRunnerReporter> GetAvailableRunnerReporters()
        {
            var result = new List<IRunnerReporter>();

            var runnerPath = Path.GetDirectoryName(typeof(Program).GetTypeInfo().Assembly.Location);

            foreach (var dllFile in Directory.GetFiles(runnerPath, "*.dll").Select(f => Path.Combine(runnerPath, f)))
            {
                Type[] types = new Type[0];

                try
                {
#if NET452
                    var assembly = Assembly.LoadFile(dllFile);
#else
                    var assembly = Assembly.Load(new AssemblyName(Path.GetFileNameWithoutExtension(dllFile)));
#endif
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
#pragma warning disable CS0618
                    if (type == null || type.GetTypeInfo().IsAbstract || type == typeof(DefaultRunnerReporter) || type == typeof(DefaultRunnerReporterWithTypes) || !type.GetInterfaces().Any(t => t == typeof(IRunnerReporter)))
                        continue;
#pragma warning restore CS0618
                    var ctor = type.GetConstructor(new Type[0]);
                    if (ctor == null)
                    {
                        ConsoleHelper.SetForegroundColor(ConsoleColor.Yellow);
                        Console.WriteLine($"Type {type.FullName} in assembly {dllFile} appears to be a runner reporter, but does not have an empty constructor.");
                        ConsoleHelper.ResetColor();
                        continue;
                    }

                    result.Add((IRunnerReporter)ctor.Invoke(new object[0]));
                }
            }

            return result;
        }

#if NET452
        void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;

            if (ex != null)
                Console.WriteLine(ex.ToString());
            else
                Console.WriteLine("Error of unknown type thrown in application domain");

            Environment.Exit(1);
        }
#endif

        void PrintHeader()
        {
#if NET452
            var platform = $"Desktop .NET {Environment.Version}";
#elif NETCOREAPP1_0 || NETCOREAPP2_0
            var platform = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
#else
#error Unknown target platform
#endif

            Console.WriteLine($"xUnit.net Console Runner ({IntPtr.Size * 8}-bit {platform})");
        }

        void PrintUsage(IReadOnlyList<IRunnerReporter> reporters)
        {
#if NET452
            var executableName = Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().GetLocalCodeBase());
#else
            var executableName = "dotnet xunit";
#endif

            Console.WriteLine("Copyright (C) .NET Foundation.");
            Console.WriteLine();
            Console.WriteLine($"usage: {executableName} <assemblyFile> [configFile] [assemblyFile [configFile]...] [options] [reporter] [resultFormat filename [...]]");
            Console.WriteLine();
#if NET452
            Console.WriteLine("Note: Configuration files must end in .json (for JSON) or .config (for XML)");
#else
            Console.WriteLine("Note: Configuration files must end in .json (XML is not supported on .NET Core)");
#endif
            Console.WriteLine();
            Console.WriteLine("Valid options:");
            Console.WriteLine("  -nologo                : do not show the copyright message");
            Console.WriteLine("  -nocolor               : do not output results with colors");
#if NET452
            Console.WriteLine("  -noappdomain           : do not use app domains to run test code");
#endif
            Console.WriteLine("  -failskips             : convert skipped tests into failures");
            Console.WriteLine("  -parallel option       : set parallelization based on option");
            Console.WriteLine("                         :   none        - turn off all parallelization");
            Console.WriteLine("                         :   collections - only parallelize collections");
            Console.WriteLine("                         :   assemblies  - only parallelize assemblies");
            Console.WriteLine("                         :   all         - parallelize assemblies & collections");
            Console.WriteLine("  -maxthreads count      : maximum thread count for collection parallelization");
            Console.WriteLine("                         :   default   - run with default (1 thread per CPU thread)");
            Console.WriteLine("                         :   unlimited - run with unbounded thread count");
            Console.WriteLine("                         :   (number)  - limit task thread pool size to 'count'");
#if NET452
            Console.WriteLine("  -noshadow              : do not shadow copy assemblies");
#endif
            Console.WriteLine("  -wait                  : wait for input after completion");
            Console.WriteLine("  -diagnostics           : enable diagnostics messages for all test assemblies");
            Console.WriteLine("  -internaldiagnostics   : enable internal diagnostics messages for all test assemblies");
            Console.WriteLine("  -debug                 : launch the debugger to debug the tests");
            Console.WriteLine("  -serialize             : serialize all test cases (for diagnostic purposes only)");
            Console.WriteLine("  -trait \"name=value\"    : only run tests with matching name/value traits");
            Console.WriteLine("                         : if specified more than once, acts as an OR operation");
            Console.WriteLine("  -notrait \"name=value\"  : do not run tests with matching name/value traits");
            Console.WriteLine("                         : if specified more than once, acts as an AND operation");
            Console.WriteLine("  -method \"name\"         : run a given test method (should be fully specified;");
            Console.WriteLine("                         : i.e., 'MyNamespace.MyClass.MyTestMethod')");
            Console.WriteLine("                         : if specified more than once, acts as an OR operation");
            Console.WriteLine("  -class \"name\"          : run all methods in a given test class (should be fully");
            Console.WriteLine("                         : specified; i.e., 'MyNamespace.MyClass')");
            Console.WriteLine("                         : if specified more than once, acts as an OR operation");
            Console.WriteLine("  -namespace \"name\"      : run all methods in a given namespace (i.e.,");
            Console.WriteLine("                         : 'MyNamespace.MySubNamespace')");
            Console.WriteLine("                         : if specified more than once, acts as an OR operation");
            Console.WriteLine("  -noautoreporters       : do not allow reporters to be auto-enabled by environment");
            Console.WriteLine("                         : (for example, auto-detecting TeamCity or AppVeyor)");
#if NETCOREAPP1_0 || NETCOREAPP2_0
            Console.WriteLine("  -framework \"name\"      : set the target framework");
#endif
            Console.WriteLine();

            var switchableReporters = reporters.Where(r => !string.IsNullOrWhiteSpace(r.RunnerSwitch)).ToList();
            if (switchableReporters.Count > 0)
            {
                Console.WriteLine("Reporters: (optional, choose only one)");

                foreach (var reporter in switchableReporters.OrderBy(r => r.RunnerSwitch))
                    Console.WriteLine($"  -{reporter.RunnerSwitch.ToLowerInvariant().PadRight(21)} : {reporter.Description}");

                Console.WriteLine();
            }

            Console.WriteLine("Result formats: (optional, choose one or more)");
            TransformFactory.AvailableTransforms.ForEach(
                transform => Console.WriteLine($"  -{$"{transform.CommandLine} <filename>".PadRight(21).Substring(0, 21)} : {transform.Description}")
            );
        }

        int RunProject(XunitProject project,
                       bool serialize,
                       bool? parallelizeAssemblies,
                       bool? parallelizeTestCollections,
                       int? maxThreadCount,
                       bool diagnosticMessages,
                       bool noColor,
                       bool noAppDomain,
                       bool failSkips,
                       bool internalDiagnosticMessages)
        {
            XElement assembliesElement = null;
            var clockTime = Stopwatch.StartNew();
            var xmlTransformers = TransformFactory.GetXmlTransformers(project);
            var needsXml = xmlTransformers.Count > 0;
            var consoleLock = new object();

            if (!parallelizeAssemblies.HasValue)
                parallelizeAssemblies = project.All(assembly => assembly.Configuration.ParallelizeAssemblyOrDefault);

            if (needsXml)
                assembliesElement = new XElement("assemblies");

            var originalWorkingFolder = Directory.GetCurrentDirectory();

            if (parallelizeAssemblies.GetValueOrDefault())
            {
                var tasks = project.Assemblies.Select(assembly => Task.Run(() => ExecuteAssembly(consoleLock, assembly, serialize, needsXml, parallelizeTestCollections, maxThreadCount, diagnosticMessages, noColor, noAppDomain, failSkips, project.Filters, internalDiagnosticMessages)));
                var results = Task.WhenAll(tasks).GetAwaiter().GetResult();
                foreach (var assemblyElement in results.Where(result => result != null))
                    assembliesElement.Add(assemblyElement);
            }
            else
            {
                foreach (var assembly in project.Assemblies)
                {
                    var assemblyElement = ExecuteAssembly(consoleLock, assembly, serialize, needsXml, parallelizeTestCollections, maxThreadCount, diagnosticMessages, noColor, noAppDomain, failSkips, project.Filters, internalDiagnosticMessages);
                    if (assemblyElement != null)
                        assembliesElement.Add(assemblyElement);
                }
            }

            clockTime.Stop();

            if (assembliesElement != null)
                assembliesElement.Add(new XAttribute("timestamp", DateTime.Now.ToString(CultureInfo.InvariantCulture)));

            if (completionMessages.Count > 0)
                reporterMessageHandler.OnMessage(new TestExecutionSummary(clockTime.Elapsed, completionMessages.OrderBy(kvp => kvp.Key).ToList()));

            Directory.SetCurrentDirectory(originalWorkingFolder);

            xmlTransformers.ForEach(transformer => transformer(assembliesElement));

            return failed ? 1 : completionMessages.Values.Sum(summary => summary.Failed);
        }

        XElement ExecuteAssembly(object consoleLock,
                                 XunitProjectAssembly assembly,
                                 bool serialize,
                                 bool needsXml,
                                 bool? parallelizeTestCollections,
                                 int? maxThreadCount,
                                 bool diagnosticMessages,
                                 bool noColor,
                                 bool noAppDomain,
                                 bool failSkips,
                                 XunitFilters filters,
                                 bool internalDiagnosticMessages)
        {
            if (cancel)
                return null;

            var assemblyElement = needsXml ? new XElement("assembly") : null;

            try
            {
                if (!ValidateFileExists(consoleLock, assembly.AssemblyFilename) || !ValidateFileExists(consoleLock, assembly.ConfigFilename))
                    return null;

                // Turn off pre-enumeration of theories, since there is no theory selection UI in this runner
                assembly.Configuration.PreEnumerateTheories = false;
                assembly.Configuration.DiagnosticMessages |= diagnosticMessages;
                assembly.Configuration.InternalDiagnosticMessages |= internalDiagnosticMessages;

                if (noAppDomain)
                    assembly.Configuration.AppDomain = AppDomainSupport.Denied;

                // Setup discovery and execution options with command-line overrides
                var discoveryOptions = TestFrameworkOptions.ForDiscovery(assembly.Configuration);
                var executionOptions = TestFrameworkOptions.ForExecution(assembly.Configuration);
                if (maxThreadCount.HasValue)
                    executionOptions.SetMaxParallelThreads(maxThreadCount);
                if (parallelizeTestCollections.HasValue)
                    executionOptions.SetDisableParallelization(!parallelizeTestCollections.GetValueOrDefault());

                var assemblyDisplayName = Path.GetFileNameWithoutExtension(assembly.AssemblyFilename);
                var diagnosticMessageSink = new DiagnosticMessageSink(consoleLock, assemblyDisplayName, assembly.Configuration.DiagnosticMessagesOrDefault, noColor);
                var appDomainSupport = assembly.Configuration.AppDomainOrDefault;
                var shadowCopy = assembly.Configuration.ShadowCopyOrDefault;
                var longRunningSeconds = assembly.Configuration.LongRunningTestSecondsOrDefault;

#if NETCOREAPP1_0 || NETCOREAPP2_0
                using (new NetCoreAssemblyDependencyResolver(assembly.AssemblyFilename))
#endif
                using (var controller = new XunitFrontController(appDomainSupport, assembly.AssemblyFilename, assembly.ConfigFilename, shadowCopy, diagnosticMessageSink: diagnosticMessageSink))
                using (var discoverySink = new TestDiscoverySink(() => cancel))
                {
                    // Discover & filter the tests
                    reporterMessageHandler.OnMessage(new TestAssemblyDiscoveryStarting(assembly, controller.CanUseAppDomains && appDomainSupport != AppDomainSupport.Denied, shadowCopy, discoveryOptions));

                    controller.Find(false, discoverySink, discoveryOptions);
                    discoverySink.Finished.WaitOne();

                    var testCasesDiscovered = discoverySink.TestCases.Count;
                    var filteredTestCases = discoverySink.TestCases.Where(filters.Filter).ToList();
                    var testCasesToRun = filteredTestCases.Count;

                    reporterMessageHandler.OnMessage(new TestAssemblyDiscoveryFinished(assembly, discoveryOptions, testCasesDiscovered, testCasesToRun));

                    // Run the filtered tests
                    if (testCasesToRun == 0)
                        completionMessages.TryAdd(Path.GetFileName(assembly.AssemblyFilename), new ExecutionSummary());
                    else
                    {
                        if (serialize)
                            filteredTestCases = filteredTestCases.Select(controller.Serialize).Select(controller.Deserialize).ToList();

                        reporterMessageHandler.OnMessage(new TestAssemblyExecutionStarting(assembly, executionOptions));

                        IExecutionSink resultsSink = new DelegatingExecutionSummarySink(reporterMessageHandler, () => cancel, (path, summary) => completionMessages.TryAdd(path, summary));
                        if (assemblyElement != null)
                            resultsSink = new DelegatingXmlCreationSink(resultsSink, assemblyElement);
                        if (longRunningSeconds > 0)
                            resultsSink = new DelegatingLongRunningTestDetectionSink(resultsSink, TimeSpan.FromSeconds(longRunningSeconds), diagnosticMessageSink);
                        if (failSkips)
                            resultsSink = new DelegatingFailSkipSink(resultsSink);

                        controller.RunTests(filteredTestCases, resultsSink, executionOptions);
                        resultsSink.Finished.WaitOne();

                        reporterMessageHandler.OnMessage(new TestAssemblyExecutionFinished(assembly, executionOptions, resultsSink.ExecutionSummary));
                    }
                }
            }
            catch (Exception ex)
            {
                failed = true;

                var e = ex;
                while (e != null)
                {
                    Console.WriteLine($"{e.GetType().FullName}: {e.Message}");

                    if (internalDiagnosticMessages)
                        Console.WriteLine(e.StackTrace);

                    e = e.InnerException;
                }
            }

            return assemblyElement;
        }

        bool ValidateFileExists(object consoleLock, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || File.Exists(fileName))
                return true;

            lock (consoleLock)
            {
                ConsoleHelper.SetForegroundColor(ConsoleColor.Red);
                Console.WriteLine($"File not found: {fileName}");
                ConsoleHelper.SetForegroundColor(ConsoleColor.Gray);
            }

            return false;
        }
    }
}
