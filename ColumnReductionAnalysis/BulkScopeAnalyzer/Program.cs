﻿using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Cci;
using Frontend;
using ScopeAnalyzer;
using ScopeAnalyzer.Misc;


namespace BulkScopeAnalyzer
{

    class Program
    {
        const string GEN_STATS_PATH = "scripts/compute_stats.py";
        const string MAP_STATS_PATH = "scripts/compute_mappings_stats.py";
        const string MAPPINGS_DIR = @".mappings";

        static void Main(string[] args)
        {
            var options = Options.ParseCommandLineArguments(args);

            if (options.AskingForHelp)
            {
                Utils.WriteLine("Check README file in the project root.");
                return;
            }

            if (options.TracePath != null) Utils.SetOutput(options.TracePath);

            Stopwatch stopWatch = new Stopwatch();
            stopWatch.Start();
            DoParallel(options);
            stopWatch.Stop();
            var ts = stopWatch.Elapsed;

            Utils.WriteLine(String.Format("Total analysis time: {0:00}:{1:00}:{2:00}.{3:00}", ts.Hours, ts.Minutes, ts.Seconds, ts.Milliseconds / 10));
            Utils.OutputClose();
        }


        static void DoParallel(Options options)
        { 
            var subdirs = Utils.GetSubDirectoriesPaths(options.ScopeDirPath);
            Utils.WriteLine(String.Format("Creating tasks for {0} Scope projects...\n", subdirs.Length));

            Utils.IsVerbose = options.Verbose;
            var tasks = new List<Task>();
            int skippedProjects = 0;
            for (int i = 0; i < subdirs.Length; i++)
            {
                var subdir = subdirs[i];
                var libs = LibraryPaths(subdir);

                string mapping = MAPPINGS_DIR + "\\" + String.Format("{0}{1}.txt", Utils.PROCESSOR_ID_MAPPING_NAME, (i + 1));
                // If mapping from processors to ids was not possible,
                // then there is no need to analyze this Scope project.
                if (!CreateProcessorIdMapping(subdir, libs, mapping))
                {
                    Utils.WriteLine("Skipping Scope project since processor mapping computation was unsuccessful: " + subdir);
                    skippedProjects++;
                    continue;
                }

                for (int j = 0; j < libs.Count; j++)
                {
                    string output = options.OutputPath + "\\" + String.Format("output_{0}.txt", (tasks.Count + 1));
                    string input = libs[j];

                    var task = Task.Run(delegate
                    {
                        Process process = new Process();
                        process.StartInfo.FileName = options.ScopeAnalyzerPath;
                        process.StartInfo.Arguments = String.Format("/assembly:\"{0}\" /libs:\"{1}\" /output:\"{2}\" /processorIds:\"{3}\" /verbose",
                                                                    input, options.LibPath, output, mapping);
                        process.StartInfo.UseShellExecute = false;
                        process.StartInfo.CreateNoWindow = true;
                        process.Start();
                        process.WaitForExit();
                    });
                    tasks.Add(task);
                }
            }

            Utils.IsVerbose = true;
            Utils.WriteLine("Skipped projects: " + skippedProjects);
            Utils.WriteLine(String.Format("Running analysis for {0} dlls...", tasks.Count));
            Task.WaitAll(tasks.ToArray());
            Utils.WriteLine("Done with analysis, computing summary of the results...\n");

            // Compute summary stats by calling respective python scripts.
            // TODO: implement the script functionalities in C#.
            ComputeGeneralStats(options.OutputPath);
            ComputeMappingStats(MAPPINGS_DIR);

            Utils.WriteLine("Done!");
        }




        private static void ComputeMappingStats(string mappingPrefix)
        {
            Process mapStatsProcess = new Process();
            mapStatsProcess.StartInfo.FileName = "python";
            mapStatsProcess.StartInfo.Arguments = String.Format("{0} \"{1}\"", MAP_STATS_PATH, mappingPrefix);
            mapStatsProcess.StartInfo.UseShellExecute = false;
            mapStatsProcess.StartInfo.CreateNoWindow = true;
            mapStatsProcess.StartInfo.RedirectStandardOutput = true;
            mapStatsProcess.StartInfo.RedirectStandardError = true;
            mapStatsProcess.Start();
            string text = String.Empty;
            while (!mapStatsProcess.StandardOutput.EndOfStream)
            {
                text += mapStatsProcess.StandardOutput.ReadLine() + "\n";
            }
            string err = String.Empty;
            while (!mapStatsProcess.StandardError.EndOfStream)
            {
                err += mapStatsProcess.StandardError.ReadLine() + "\n";
            }
            mapStatsProcess.WaitForExit();
            Utils.WriteLine(text);
            Utils.WriteLine(err);
        }

        private static void ComputeGeneralStats(string outputPrefix)
        {
            Process genStatsProcess = new Process();
            genStatsProcess.StartInfo.FileName = "python";
            genStatsProcess.StartInfo.Arguments = String.Format("{0} \"{1}\"", GEN_STATS_PATH, outputPrefix);
            genStatsProcess.StartInfo.UseShellExecute = false;
            genStatsProcess.StartInfo.CreateNoWindow = true;
            genStatsProcess.StartInfo.RedirectStandardOutput = true;
            genStatsProcess.StartInfo.RedirectStandardError = true;
            genStatsProcess.Start();
            string text = String.Empty;
            while (!genStatsProcess.StandardOutput.EndOfStream)
            {
                text += genStatsProcess.StandardOutput.ReadLine() + "\n";
            }
            string err = String.Empty;
            while (!genStatsProcess.StandardError.EndOfStream)
            {
                err += genStatsProcess.StandardError.ReadLine() + "\n";
            }

            genStatsProcess.WaitForExit();
            Utils.WriteLine(text);
            Utils.WriteLine(err);
        }


        private static bool CreateProcessorIdMapping(string subdir, IEnumerable<string> libs, string outpath)
        {
            var mainDll = subdir + "\\" + Utils.MAIN_DLL_NAME;
            if (!libs.Contains(mainDll))
            {
                Utils.WriteLine("Did not find main scope dll: " + subdir);
                return false;
            }

            // delete existing processor id mapping file
            File.Delete(outpath);
            
            Assembly asm;
            try
            {              
                var host = new PeReader.DefaultHost();
                asm = new Assembly(host);
                asm.Load(mainDll);

                var mappingAnalyzer = new ProcessorMappingAnalyzer(asm);
                mappingAnalyzer.Analyze();
                var results = mappingAnalyzer.ProcessorIdMapping;
                var summary = String.Empty;
                foreach(var k in results.Keys)
                {
                    summary += k + "\t" + results[k] + "\n";
                }

                using (StreamWriter file = new StreamWriter(outpath))
                {
                    file.Write(summary);
                }
                Utils.WriteLine("Successfully extracted processor to id mapping for the main dll: " + subdir);
            }
            catch
            {
                Utils.WriteLine("WARNING: failed to extract processor to id mapping the main dll: " + subdir);
                return false;
            }
            return true;
        }


        private static List<string> LibraryPaths(string dir)
        {
            var dlls = new List<string>();
            try
            {
                dlls = Utils.CollectAssemblies(dir);
            }
            catch (Exception e)
            {
                Utils.WriteLine("WARNING: failed to get dll paths " + e.Message);
            }
            return dlls;
        }

        #region deprecated

        static void DoNaiveParallel()
        {
            string mainFolder = @"C:\Users\t-zpavli\Desktop\scope benchmarks\real examples";
            //string mainFolder = @"C:\Users\t-zpavli\Desktop\scope benchmarks\issues";
            //string mainFolder = @"\\madanm2\parasail2\TFS\parasail\ScopeSurvey\AutoDownloader\bin\Debug";
            string libPath = @"\\madanm2\parasail2\TFS\parasail\ScopeSurvey\AutoDownloader\bin\Debug";
            string scopeAnalyzer = @"C:\Users\t-zpavli\Desktop\dfa-analysis\zvonimir\analysis-net\ScopeAnalyzer\bin\\Debug\ScopeAnalyzer.exe";
            string outputPrefix = @"C:\Users\t-zpavli\Desktop\test output\";
            string mappingPrefix = @"C:\Users\t-zpavli\Desktop\test output\mappings\";

            var subdirs = Utils.GetSubDirectoriesPaths(mainFolder);
            Console.WriteLine(String.Format("Analyzing {0} Scope projects\n", subdirs.Length));
            int dll_count = 0;
            for (int i = 0; i < subdirs.Length; i++)
            {
                var subdir = subdirs[i];
                var libs = LibraryPaths(subdir);

                string mapping = mappingPrefix + String.Format("{0}{1}.txt", Utils.PROCESSOR_ID_MAPPING_NAME, (i + 1));
                if (!CreateProcessorIdMapping(subdir, libs, mapping))
                {
                    Console.WriteLine("Skipping this Scope project...");
                    continue;
                }

                for (int j = 0; j < libs.Count; j++)
                {
                    if (dll_count >= 150 && dll_count % 150 == 0)
                    {
                        Console.WriteLine("Giving myself 6 minutes...");
                        Thread.Sleep(360000);
                    }

                    Console.WriteLine("Spawning process " + (dll_count + 1));
                    string output = outputPrefix + String.Format("output_{0}.txt", (dll_count + 1));
                    string input = libs[j];

                    Process process = new Process();
                    process.StartInfo.FileName = scopeAnalyzer;
                    process.StartInfo.Arguments = String.Format("/assembly:\"{0}\" /libs:\"{1}\" /output:\"{2}\" /processorIds:\"{3}\"", input, libPath, output, mapping);
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();
                    dll_count++;
                }
            }
        }


        //static void DoSequential()
        //{
        //    Stopwatch stopWatch = new Stopwatch();
        //    stopWatch.Start();

        //    string mainFolder = @"C:\Users\t-zpavli\Desktop\scope benchmarks\real examples";
        //    //string mainFolder = @"\\MADANM2\parasail\ScopeSurvey\ScopeMapAccess\bin\LocalDebug";
        //    var subdirs = SubDirectoryPaths(mainFolder);

        //    //string libPath = "C:\\Users\\t-zpavli\\Desktop\\libs";
        //    string libPath = @"\\MADANM2\\parasail\\ScopeSurvey\\ScopeMapAccess\\bin\\LocalDebug";
        //    string outPath = "bulk-trace-output";

        //    ScopeAnalyzer.Utils.SetOutput(outPath);
        //    ScopeAnalyzer.Utils.WriteLine("Analyzing " + subdirs.Count() + " Scope projects.\n");

        //    var cumulativeStats = new ScopeAnalyzer.ScopeAnalysisStats();
        //    foreach (var subdir in subdirs)
        //    {
        //        ScopeAnalyzer.Utils.WriteLine("**************** ANALYZING " + subdir + " * ***************");

        //        try
        //        {
        //            var stats = ScopeAnalyzer.Program.AnalyzeAssemblies(new string[] { subdir, libPath });
        //            ScopeAnalyzer.Utils.WriteLine("\nLocal stats:");
        //            ScopeAnalyzer.Program.PrintScopeAnalysisStats(stats);

        //            cumulativeStats.Assemblies += stats.Assemblies;
        //            cumulativeStats.AssembliesLoaded += stats.AssembliesLoaded;
        //            cumulativeStats.FailedMethods += stats.FailedMethods;
        //            cumulativeStats.Methods += stats.Methods;
        //            cumulativeStats.UnsupportedMethods += stats.UnsupportedMethods;
        //            cumulativeStats.InterestingMethods += stats.InterestingMethods;
        //            cumulativeStats.NotCPropagationDummies += stats.InterestingMethods;
        //            cumulativeStats.NotEscapeDummies += stats.NotEscapeDummies;
        //            cumulativeStats.NotColumnDummies += stats.NotColumnDummies;
        //        }
        //        catch (Exception e)
        //        {
        //            ScopeAnalyzer.Utils.WriteLine("TOP LEVEL ERROR: " + e.Message);
        //        }
        //    }

        //    ScopeAnalyzer.Utils.WriteLine("\n\nGLOBAL STATS:");
        //    ScopeAnalyzer.Program.PrintScopeAnalysisStats(cumulativeStats);

        //    var ts = stopWatch.Elapsed;
        //    string elapsedTime = String.Format("{0:00}:{1:00}:{2:00}.{3:00}",
        //        ts.Hours, ts.Minutes, ts.Seconds,
        //        ts.Milliseconds / 10);
        //    ScopeAnalyzer.Utils.WriteLine("\nTotal time: " + elapsedTime);
        //}
        #endregion
    }
}
