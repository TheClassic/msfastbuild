// MSFASTBuild.cs - Generates and executes a bff file for fastbuild from a .sln or .vcxproj.
// Copyright 2016 Liam Flookes and Yassine Riahi
// Available under an MIT license. See license file on github for details.
using System;
using System.Collections.Generic;
using System.Linq;
using CommandLine;
using CommandLine.Text;
using System.IO;
using System.Reflection;
using System.Text;

using Microsoft.Build.Evaluation;
using Microsoft.Build.Construction;
using Microsoft.Build.Utilities;

namespace msfastbuild
{
	public class Options
	{
		[Option('p', "vcproject", DefaultValue = "",
		HelpText = "Path of vcproj to be built. Or name of vcproj if a solution is provided.")]
		public string Project { get; set; }

		[Option('s', "sln", DefaultValue = "",
		HelpText = "sln which contains the vcproj.")]
		public string Solution { get; set; }

		[Option('c', "config", DefaultValue = "Debug",
		HelpText = "Configuration to build.")]
		public string Config { get; set; }

		[Option('f', "platform", DefaultValue = "Win32",
		HelpText = "Platform to build.")]
		public string Platform { get; set; }

		[Option('a', "fbargs", DefaultValue = "-dist",
		HelpText = "Arguments to pass through to FASTBuild.")]
		public string FBArgs { get; set; }
		
		[Option('g', "generateonly", DefaultValue = false,
		HelpText = "Generate the bff file without calling FASTBuild.")]
		public bool GenerateOnly { get; set; }
		
		[Option('r', "regen", DefaultValue = false, //true for dev
		HelpText = "If true, regenerate the bff file even when the project hasn't changed.")]
		public bool AlwaysRegenerate { get; set; }

		//@"E:\fastbuild-dev\fastbuild\tmp\x64-Release\Tools\FBuild\FBuildApp\FBuild.exe"
		[Option('b', "fbpath", DefaultValue = @"FBuild.exe",
		HelpText = "Path to FASTBuild executable.")]
		public string FBPath { get; set; }

		[HelpOption]
		public string GetUsage()
		{
			return HelpText.AutoBuild(this,(HelpText current) => HelpText.DefaultParsingErrorsHandler(this, current));
		}
	}

	public class msfastbuild
	{
		static public string PlatformToolsetVersion = "140";
		static public string VCBasePath = "";
		static public Options CommandLineOptions = new Options();
		static public string WindowsSDKTarget = "10.0.10240.0";
		static public Assembly CPPTasksAssembly;
		static public string PreBuildBatchFile = "";
		static public string SolutionDir = "";
		static public bool HasCompileActions = true;
		static public string settingsFilePath = "";

		public enum BuildType
		{
		    Application,
		    StaticLib,
		    DynamicLib
		}

		static public BuildType BuildOutput = BuildType.Application;

		public class MSFBProject
		{
			public MSFBProject(string vcprojxPath)
			{
				try
				{
					ProjectCollection projColl = new ProjectCollection();
					if (!string.IsNullOrEmpty(SolutionDir))
						projColl.SetGlobalProperty("SolutionDir", SolutionDir);

					Proj = projColl.LoadProject(vcprojxPath);

					if (Proj != null)
					{
						Proj.SetGlobalProperty("Configuration", CommandLineOptions.Config);
						Proj.SetGlobalProperty("Platform", CommandLineOptions.Platform);
						if (!string.IsNullOrEmpty(SolutionDir))
							Proj.SetGlobalProperty("SolutionDir", SolutionDir);
						Proj.ReevaluateIfNecessary();
					}
				}
				catch (Exception e)
				{
					Console.WriteLine("Exception loading project " + vcprojxPath + " exception " + e.Message);
				}
			}

			public Project Proj;
			public List<MSFBProject> Dependencies = new List<MSFBProject>();
			public string AdditionalLinkInputs = "";
			public string TargetName;
		}

		static public string GenerateBFF_FilePath(string projectPath, Options CommandLineOptions)
		{
			return projectPath + "_" + CommandLineOptions.Config.Replace(" ", "") + "_" + CommandLineOptions.Platform.Replace(" ", "") + ".bff";
		}

		static public void InitializeSettingsFilePath(string solutionPath)
		{
			settingsFilePath = Path.GetDirectoryName(solutionPath);
			settingsFilePath = Path.Combine(settingsFilePath, "settings.bff");
		}

		static public string GenerateMSVCCompilerString(string Platform)
		{
			StringBuilder CompilerString = new StringBuilder();

			string CompilerRoot = CompilerRoot = VCBasePath + "bin/";
			if (Platform == "Win64" || Platform == "x64")
			{
				CompilerString.Append("Compiler('msvc64')\n{\n");
				CompilerString.Append("\t.Root = '$VSBasePath$/VC/bin/amd64'\n");
				CompilerRoot += "amd64/";
			}
			else if (Platform == "Win32" || Platform == "x86" || true) //Hmm.
			{
				CompilerString.Append("Compiler('msvc32')\n{\n");
				CompilerString.Append("\t.Root = '$VSBasePath$/VC/bin'\n");
			}
			CompilerString.Append("\t.Executable = '$Root$/cl.exe'\n");
			CompilerString.Append("\t.ExtraFiles =\n\t{\n");
			CompilerString.Append("\t\t'$Root$/c1.dll'\n");
			CompilerString.Append("\t\t'$Root$/c1xx.dll'\n");
			CompilerString.Append("\t\t'$Root$/c2.dll'\n");

			if (File.Exists(CompilerRoot + "1033/clui.dll")) //Check English first...
			{
				CompilerString.Append("\t\t'$Root$/1033/clui.dll'\n");
			}
			else
			{
				var numericDirectories = Directory.GetDirectories(CompilerRoot).Where(d => Path.GetFileName(d).All(char.IsDigit));
				var cluiDirectories = numericDirectories.Where(d => Directory.GetFiles(d, "clui.dll").Any());
				if (cluiDirectories.Any())
				{
					CompilerString.AppendFormat("\t\t'$Root$/{0}/clui.dll'\n", Path.GetFileName(cluiDirectories.First()));
				}
			}

			CompilerString.Append("\t\t'$Root$/mspdbsrv.exe'\n");
			CompilerString.Append("\t\t'$Root$/mspdbcore.dll'\n");

			CompilerString.AppendFormat("\t\t'$Root$/mspft{0}.dll'\n", PlatformToolsetVersion);
			CompilerString.AppendFormat("\t\t'$Root$/msobj{0}.dll'\n", PlatformToolsetVersion);
			CompilerString.AppendFormat("\t\t'$Root$/mspdb{0}.dll'\n", PlatformToolsetVersion);
			CompilerString.AppendFormat("\t\t'$VSBasePath$/VC/redist/{0}/Microsoft.VC{1}.CRT/msvcp{1}.dll'\n", Platform == "Win32" ? "x86" : "x64", PlatformToolsetVersion);
			CompilerString.AppendFormat("\t\t'$VSBasePath$/VC/redist/{0}/Microsoft.VC{1}.CRT/vccorlib{1}.dll'\n", Platform == "Win32" ? "x86" : "x64", PlatformToolsetVersion);

			CompilerString.Append("\t}\n"); //End extra files
			CompilerString.Append("}\n\n"); //End compiler

			return CompilerString.ToString();
		}

		static public void CreateSettingsFile(Project project)
		{
			StringBuilder settingsContent = new StringBuilder();
			settingsContent.Append("#once\n\n");

			settingsContent.AppendFormat(".VSBasePath = '{0}'\n", project.GetProperty("VSInstallDir").EvaluatedValue);
			VCBasePath = project.GetProperty("VCInstallDir").EvaluatedValue;
			settingsContent.AppendFormat(".VCBasePath = '{0}'\n", VCBasePath);

			WindowsSDKTarget = project.GetProperty("WindowsTargetPlatformVersion") != null ? project.GetProperty("WindowsTargetPlatformVersion").EvaluatedValue : "8.1";

			settingsContent.AppendFormat(".WindowsSDKBasePath = '{0}'\n\n", project.GetProperty("WindowsSdkDir").EvaluatedValue);

			settingsContent.Append("Settings\n{\n\t.Environment = \n\t{\n");
			settingsContent.AppendFormat("\t\t\"INCLUDE={0}\",\n", project.GetProperty("IncludePath").EvaluatedValue);
			settingsContent.AppendFormat("\t\t\"LIB={0}\",\n", project.GetProperty("LibraryPath").EvaluatedValue);
			settingsContent.AppendFormat("\t\t\"LIBPATH={0}\",\n", project.GetProperty("ReferencePath").EvaluatedValue);
			settingsContent.AppendFormat("\t\t\"PATH={0}\"\n", project.GetProperty("Path").EvaluatedValue);
			settingsContent.AppendFormat("\t\t\"TMP={0}\"\n", project.GetProperty("Temp").EvaluatedValue);
			settingsContent.AppendFormat("\t\t\"TEMP={0}\"\n", project.GetProperty("Temp").EvaluatedValue);
			settingsContent.AppendFormat("\t\t\"SystemRoot={0}\"\n", project.GetProperty("SystemRoot").EvaluatedValue);
			settingsContent.Append("\t}\n}\n\n");

			StringBuilder CompilerString = new StringBuilder();
			CompilerString.Append(GenerateMSVCCompilerString("Win32"));
			CompilerString.Append(GenerateMSVCCompilerString("Win64"));
			CompilerString.Append("Compiler('rc') { .Executable = '$WindowsSDKBasePath$\\bin\\x64\\rc.exe' }\n\n");

			settingsContent.Append(CompilerString);

			File.WriteAllText(settingsFilePath, settingsContent.ToString());

		}

		static void Main(string[] args)
		{			
			Parser parser = new Parser();
			if (!parser.ParseArguments(args, CommandLineOptions))
			{
				Console.WriteLine(CommandLineOptions.GetUsage());
				return;
			}

			if (string.IsNullOrEmpty(CommandLineOptions.Solution) && string.IsNullOrEmpty(CommandLineOptions.Project))
			{
				Console.WriteLine("No vcxproj or sln provided:  nothing to do!");
				Console.WriteLine(CommandLineOptions.GetUsage());
				return;
			}

			string masterBffPath = "fbuild.bff";
			List <string> ProjectsToBuild = new List<string>();
			if (!string.IsNullOrEmpty(CommandLineOptions.Solution) && File.Exists(CommandLineOptions.Solution))
			{
				InitializeSettingsFilePath(CommandLineOptions.Solution);
				masterBffPath = Path.Combine(Path.GetDirectoryName(CommandLineOptions.Solution), Path.GetFileNameWithoutExtension(CommandLineOptions.Solution) + ".bff");
				try
				{
					if (string.IsNullOrEmpty(CommandLineOptions.Project))
					{
						List<ProjectInSolution> SolutionProjects = SolutionFile.Parse(Path.GetFullPath(CommandLineOptions.Solution)).ProjectsInOrder.Where(el => el.ProjectType == SolutionProjectType.KnownToBeMSBuildFormat).ToList();
						SolutionProjects.Sort((x, y) => //Very dubious sort.
						{
							if (x.Dependencies.Contains(y.ProjectGuid)) return 1;
							if (y.Dependencies.Contains(x.ProjectGuid)) return -1;
							return 0;
						});
						ProjectsToBuild = SolutionProjects.ConvertAll(el => el.AbsolutePath);
					}
					else
					{
						ProjectsToBuild.Add(Path.GetFullPath(CommandLineOptions.Project));
					}

					SolutionDir = Path.GetDirectoryName(Path.GetFullPath(CommandLineOptions.Solution));
					SolutionDir = SolutionDir.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
					if(SolutionDir.Last() != Path.AltDirectorySeparatorChar)
						SolutionDir += Path.AltDirectorySeparatorChar;
				}
				catch (Exception e)
				{
					Console.WriteLine("Failed to load input file: " + CommandLineOptions.Solution + ", exception thrown was: " + e.Message);
					return;
				}
			}
			else if (!string.IsNullOrEmpty(CommandLineOptions.Project))
			{
				InitializeSettingsFilePath(CommandLineOptions.Project);
				masterBffPath = Path.Combine(Path.GetDirectoryName(CommandLineOptions.Project), Path.GetFileNameWithoutExtension(CommandLineOptions.Project) + ".bff");
				
				ProjectsToBuild.Add(Path.GetFullPath(CommandLineOptions.Project));
			}

			StringBuilder masterBffContent = new StringBuilder();

			Dictionary<string, MSFBProject> GeneratedProjects = new Dictionary<string, MSFBProject>();

			bool settingsCreated = false;
			for (int i=0; i < ProjectsToBuild.Count; ++i)
			{
				if (!GeneratedProjects.ContainsKey(Path.GetFullPath(ProjectsToBuild[i])))
				{
					MSFBProject msfbProject = new MSFBProject(ProjectsToBuild[i]);
					if (!settingsCreated)
					{
						CreateSettingsFile(msfbProject.Proj);
						settingsCreated = true;
					}
					GenerateBffFromVcxproj(msfbProject, CommandLineOptions.Config, CommandLineOptions.Platform, GeneratedProjects);
				}
				masterBffContent.AppendFormat("#include \"{0}\"\n", GenerateBFF_FilePath(ProjectsToBuild[i], CommandLineOptions));
			}

			masterBffContent.Append("\nAlias ('All')\n");
			masterBffContent.Append("{\n");
			masterBffContent.Append(".Targets = { ");

			foreach (MSFBProject msfbProject in GeneratedProjects.Values)
			{
				masterBffContent.AppendFormat("'{0}'\n", msfbProject.TargetName);
			}

			masterBffContent.Append("}\n");
			masterBffContent.Append("}\n");

			//essentially a bff for the solution, including each of the projects
			File.WriteAllText(masterBffPath, masterBffContent.ToString());

			ExecuteBffFile(masterBffPath, CommandLineOptions.Platform);
		}

		static public List<string> EvaluateProjectReferences(Project project)
		{
			List<string> dependencies = new List<string>();
			var ProjectReferences = project.Items.Where(elem => elem.ItemType == "ProjectReference");
			foreach (var ProjRef in ProjectReferences)
			{
				if (ProjRef.GetMetadataValue("ReferenceOutputAssembly") == "true" || ProjRef.GetMetadataValue("LinkLibraryDependencies") == "true")
				{
					dependencies.Add(Path.GetDirectoryName(project.FullPath) + Path.DirectorySeparatorChar + ProjRef.EvaluatedInclude);
				}
			}
			return dependencies;
		}

		static public bool HasFileChanged(string bffFile, string InputFile, string Platform, string Config, out string MD5hash)
		{
			using (var md5 = System.Security.Cryptography.MD5.Create())
			{
				using (var stream = File.OpenRead(InputFile))
				{
					MD5hash = ";" + InputFile + "_" + Platform + "_" + Config + "_" + BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", "").ToLower();
				}
			}
			
			if (!File.Exists(bffFile))
				return true;
			
			string FirstLine = File.ReadAllLines(bffFile).First(); //bit wasteful to read the whole file...
			if (FirstLine == MD5hash) 
				return false;
			else
				return true;
		}

		static public bool ExecuteBffFile(string masterBffFile, string Platform)
		{
			string projectDir = Path.GetDirectoryName(masterBffFile) + "\\";

			string BatchFileText = "@echo off\n"
				+ "%comspec% /c \"\"" + VCBasePath
				+ "vcvarsall.bat\" " + (Platform == "Win32" ? "x86" : "x64") + " " 
				+ (PlatformToolsetVersion == "140" ? WindowsSDKTarget : "") // Only VS2015R3 specifies the WinSDK?
				+ " && \"" + CommandLineOptions.FBPath  +"\" %*\"";
			File.WriteAllText(projectDir + "fb.bat", BatchFileText);

			Console.WriteLine("Building " + Path.GetFileNameWithoutExtension(masterBffFile));

			try
			{
				System.Diagnostics.Process FBProcess = new System.Diagnostics.Process();
				FBProcess.StartInfo.FileName = projectDir + "fb.bat";
				FBProcess.StartInfo.Arguments = "-config \"" + masterBffFile + "\" " + CommandLineOptions.FBArgs; //TODO need to correctly specify the file for it to build
				FBProcess.StartInfo.RedirectStandardOutput = true;
				FBProcess.StartInfo.UseShellExecute = false;
				FBProcess.StartInfo.WorkingDirectory = projectDir;
				FBProcess.StartInfo.StandardOutputEncoding = Console.OutputEncoding;

				FBProcess.Start();
				while (!FBProcess.StandardOutput.EndOfStream)
				{
				    Console.Write(FBProcess.StandardOutput.ReadLine() + "\n");
				}
				FBProcess.WaitForExit();
				return FBProcess.ExitCode == 0;
			}
			catch (Exception e)
			{
				Console.WriteLine("Problem launching fb.bat. Exception: " + e.Message);
				return false;
			}
		}

		public class BffProjectFile
		{
			string path;  /// path and filename
			HashSet<string> compilers; /// string comprising compilers needed for this project
			string settings; /// string containing settings needed for this project
			List<string> objects; /// list of objects created by this project
		}

		public class ObjectListNode
		{
			public string Name;
			string Compiler;
			string CompilerOutputPath;
			string CompilerOptions;
			string CompilerOutputExtension;
			string PrecompiledHeaderString;

			List<string> CompilerInputFiles;
		
			public ObjectListNode(string InputFile, string InCompiler, string InCompilerOutputPath, string InCompilerOptions, string InPrecompiledHeaderString, string InCompilerOutputExtension = "")
			{
				CompilerInputFiles = new List<string>();
				CompilerInputFiles.Add(InputFile);
				Compiler = InCompiler;
				CompilerOutputPath = InCompilerOutputPath;
				CompilerOptions = InCompilerOptions;
				CompilerOutputExtension = InCompilerOutputExtension;
				PrecompiledHeaderString = InPrecompiledHeaderString;
			}
		
			public bool AddIfMatches(string InputFile, string InCompiler, string InCompilerOutputPath, string InCompilerOptions)
			{
				if(Compiler == InCompiler && CompilerOutputPath == InCompilerOutputPath && CompilerOptions == InCompilerOptions)
				{
				    CompilerInputFiles.Add(InputFile);
				    return true;
				}
				return false;
			}
		
			public string ToString(string bffName, int ActionNumber)
			{
				Name = string.Format("{0}_Action_{1}", bffName, ActionNumber);
				StringBuilder ObjectListString = new StringBuilder(string.Format("ObjectList('{0}')\n{{\n", Name));
				ObjectListString.AppendFormat("\t.Compiler = '{0}'\n",Compiler);
				ObjectListString.AppendFormat("\t.CompilerOutputPath = \"{0}\"\n", CompilerOutputPath);
				ObjectListString.AppendFormat("\t.CompilerInputFiles = {{ {0} }}\n", string.Join(",", CompilerInputFiles.ConvertAll(el => string.Format("'{0}'", el)).ToArray()));
				ObjectListString.AppendFormat("\t.CompilerOptions = '{0}'\n", CompilerOptions);
				if(!string.IsNullOrEmpty(CompilerOutputExtension))
				{
				    ObjectListString.AppendFormat("\t.CompilerOutputExtension = '{0}'\n", CompilerOutputExtension);
				}
				if(!string.IsNullOrEmpty(PrecompiledHeaderString))
				{
					ObjectListString.Append(PrecompiledHeaderString);
				}
				if(!string.IsNullOrEmpty(PreBuildBatchFile))
				{
					ObjectListString.Append("\t.PreBuildDependencies  = 'prebuild'\n");
				}
				ObjectListString.Append("}\n\n");
				return ObjectListString.ToString();
			}
		}

		/// <summary>
		/// Generates a bff file for a project, and recursively for all dependencies
		/// </summary>
		/// <param name="ProjectPath"></param>
		/// <param name="Config"></param>
		/// <param name="Platform"></param>
		static private void GenerateBffFromVcxproj(MSFBProject CurrentProject, string Config, string Platform, Dictionary<string, MSFBProject> generatedProjects)
		{
			string ProjectPath = CurrentProject.Proj.FullPath;

			string BFFOutputFilePath = GenerateBFF_FilePath(ProjectPath, CommandLineOptions);

			if (Path.GetExtension(ProjectPath) != ".vcxproj")
			{
				Console.WriteLine("Cannot handle project {0}.", ProjectPath);
				File.WriteAllText(BFFOutputFilePath, "");
				return;
			}


			List<string> dependencies = EvaluateProjectReferences(CurrentProject.Proj);

			foreach (string dependencyPath in dependencies)
			{
				MSFBProject dependency = null;
				
				if (!generatedProjects.TryGetValue(Path.GetFullPath(dependencyPath), out dependency))
				{
					dependency = new MSFBProject(dependencyPath);
					GenerateBffFromVcxproj(dependency, Config, Platform, generatedProjects);
					generatedProjects[Path.GetFullPath(dependencyPath)] = dependency;
				}
				CurrentProject.Dependencies.Add(dependency);
			}

			generatedProjects[ProjectPath] = CurrentProject;

			Project ActiveProject = CurrentProject.Proj;

			CPPTasksAssembly = Assembly.LoadFrom(CurrentProject.Proj.GetPropertyValue("VCTargetsPath14") + "Microsoft.Build.CPPTasks.Common.dll"); //Dodgy? VCTargetsPath may not be there...

			string MD5hash = "wafflepalooza";
			bool FileChanged = HasFileChanged(BFFOutputFilePath, ProjectPath, Platform, Config, out MD5hash);


			string bffName = Path.GetFileNameWithoutExtension(ActiveProject.FullPath) + '_' + Platform + '_' + Config; /// we'll use this as the filename as well as objects within the bff

			string configType = ActiveProject.GetProperty("ConfigurationType").EvaluatedValue;
			switch(configType)
			{
				case "DynamicLibrary": BuildOutput = BuildType.DynamicLib; break;
				case "StaticLibrary": BuildOutput = BuildType.StaticLib; break;
				default:
				case "Application": BuildOutput = BuildType.Application; break;				
			}

			PlatformToolsetVersion = ActiveProject.GetProperty("PlatformToolsetVersion").EvaluatedValue;

			string OutDir = ActiveProject.GetProperty("OutDir").EvaluatedValue;
			string IntDir = ActiveProject.GetProperty("IntDir").EvaluatedValue;

			StringBuilder OutputString = new StringBuilder(MD5hash + "\n\n#once\n\n");

			OutputString.AppendFormat("#include \"{0}\"\n\n", settingsFilePath);

			if (ActiveProject.GetItems("PreBuildEvent").Any())
			{
				var buildEvent = ActiveProject.GetItems("PreBuildEvent").First();
				if (buildEvent.Metadata.Any())
				{
					var mdPi = buildEvent.Metadata.First();
					if(!string.IsNullOrEmpty(mdPi.EvaluatedValue))
					{
						string BatchText = "call \"" + VCBasePath + "vcvarsall.bat\" " + 
							(Platform == "Win32" ? "x86" : "x64") + " "
							+ (PlatformToolsetVersion == "140" ? WindowsSDKTarget : "") + "\n";
						PreBuildBatchFile = Path.Combine(ActiveProject.DirectoryPath, Path.GetFileNameWithoutExtension(ActiveProject.FullPath) + "_prebuild.bat");
						File.WriteAllText(PreBuildBatchFile, BatchText + mdPi.EvaluatedValue);						
						OutputString.Append("Exec('prebuild') \n{\n");
						OutputString.AppendFormat("\t.ExecExecutable = '{0}' \n", PreBuildBatchFile);
						OutputString.AppendFormat("\t.ExecInput = '{0}' \n", PreBuildBatchFile);
						OutputString.AppendFormat("\t.ExecOutput = '{0}' \n", PreBuildBatchFile + ".txt");
						OutputString.Append("\t.ExecUseStdOutAsOutput = true \n");
						OutputString.Append("}\n\n");
					}
				}
			}

			string CompilerOptions = "";

			List<ObjectListNode> ObjectLists = new List<ObjectListNode>();
			var CompileItems = ActiveProject.GetItems("ClCompile");
			string PrecompiledHeaderString = "";

			foreach (var Item in CompileItems)
			{
				if (Item.DirectMetadata.Any())
				{
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "ExcludedFromBuild" && dmd.EvaluatedValue == "true").Any())
						continue;
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "PrecompiledHeader" && dmd.EvaluatedValue == "Create").Any())
					{
						ToolTask CLtask = (ToolTask)Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.CL"));
						CLtask.GetType().GetProperty("Sources").SetValue(CLtask, new TaskItem[] { new TaskItem() });
						string pchCompilerOptions = GenerateTaskCommandLine(CLtask, new string[] { "PrecompiledHeaderOutputFile", "ObjectFileName", "AssemblerListingLocation" }, Item.Metadata) + " /FS";
						PrecompiledHeaderString = "\t.PCHOptions = '" + string.Format("\"%1\" /Fp\"%2\" /Fo\"%3\" {0} '\n", pchCompilerOptions);
						PrecompiledHeaderString += "\t.PCHInputFile = '" + Item.EvaluatedInclude + "'\n";
						PrecompiledHeaderString += "\t.PCHOutputFile = '" + Item.GetMetadataValue("PrecompiledHeaderOutputFile") + "'\n";
						break; //Assumes only one pch...
					}
				}
			}

			foreach (var Item in CompileItems)
			{
				if (Item.DirectMetadata.Any())
				{
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "ExcludedFromBuild" && dmd.EvaluatedValue == "true").Any())
						continue;
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "PrecompiledHeader" && dmd.EvaluatedValue == "Create").Any())
						continue;
				}
	
				ToolTask Task = (ToolTask) Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.CL"));
				Task.GetType().GetProperty("Sources").SetValue(Task, new TaskItem[] { new TaskItem() }); //CPPTasks throws an exception otherwise...
				string TempCompilerOptions = GenerateTaskCommandLine(Task, new string[] { "ObjectFileName", "AssemblerListingLocation" }, Item.Metadata) + " /FS";
				if (Path.GetExtension(Item.EvaluatedInclude) == ".c")
					TempCompilerOptions += " /TC";
				else
					TempCompilerOptions += " /TP";
				CompilerOptions = TempCompilerOptions;
				string FormattedCompilerOptions = string.Format("\"%1\" /Fo\"%2\" {0}", TempCompilerOptions);
				var MatchingNodes = ObjectLists.Where(el => el.AddIfMatches(Item.EvaluatedInclude, "msvc", IntDir, FormattedCompilerOptions));
				if(!MatchingNodes.Any())
				{
					string ItemPrecompiledHeaderString = PrecompiledHeaderString;
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "PrecompiledHeader" && dmd.EvaluatedValue == "NotUsing").Any())
						ItemPrecompiledHeaderString = "";
					ObjectLists.Add(new ObjectListNode(Item.EvaluatedInclude, "msvc", IntDir, FormattedCompilerOptions, ItemPrecompiledHeaderString));
				}
			}

			PrecompiledHeaderString = "";

			var ResourceCompileItems = ActiveProject.GetItems("ResourceCompile");
			foreach (var Item in ResourceCompileItems)
			{
				if (Item.DirectMetadata.Any())
				{
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "ExcludedFromBuild" && dmd.EvaluatedValue == "true").Any())
						continue;
				}
			
				ToolTask Task = (ToolTask)Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.RC"));
				string ResourceCompilerOptions = GenerateTaskCommandLine(Task, new string[] { "ResourceOutputFileName", "DesigntimePreprocessorDefinitions" }, Item.Metadata);
			
				string formattedCompilerOptions = string.Format("{0} /fo\"%2\" \"%1\"", ResourceCompilerOptions);
				var MatchingNodes = ObjectLists.Where(el => el.AddIfMatches(Item.EvaluatedInclude, "rc", IntDir, formattedCompilerOptions));
				if (!MatchingNodes.Any())
				{
					ObjectLists.Add(new ObjectListNode(Item.EvaluatedInclude, "rc", IntDir, formattedCompilerOptions, PrecompiledHeaderString, ".res"));
				}
			}

			int ActionNumber = 0;
			foreach (ObjectListNode ObjList in ObjectLists)
			{
				OutputString.Append(ObjList.ToString(bffName, ActionNumber));
				ActionNumber++;
			}

			if (ActionNumber > 0)
			{
				HasCompileActions = true;
			}
			else
			{
				HasCompileActions = false;
				Console.WriteLine("Project {0} has no actions to compile.", ProjectPath);
			}

			string[] Libraries = ObjectLists.Select(x => string.Format("'{0}'", x.Name)).ToArray();
			string CompileActions = string.Join(",", Libraries);

			if (BuildOutput == BuildType.Application || BuildOutput == BuildType.DynamicLib)
			{
				OutputString.AppendFormat("{0}('{1}')\n{{", BuildOutput == BuildType.Application ? "Executable" : "DLL", bffName);

				if (Platform == "Win32" || Platform == "x86")
				{
					OutputString.Append("\t.Linker = '$VSBasePath$\\VC\\bin\\link.exe'\n");
				}
				else
				{
					OutputString.Append("\t.Linker = '$VSBasePath$\\VC\\bin\\amd64\\link.exe'\n");
				}
        
				var LinkDefinitions = ActiveProject.ItemDefinitions["Link"];
				string OutputFile = LinkDefinitions.GetMetadataValue("OutputFile").Replace('\\', '/');

				if(HasCompileActions)
				{
					string DependencyOutputPath = LinkDefinitions.GetMetadataValue("ImportLibrary");
					if (Path.IsPathRooted(DependencyOutputPath))
						DependencyOutputPath = DependencyOutputPath.Replace('\\', '/');
					else
						DependencyOutputPath = Path.Combine(ActiveProject.DirectoryPath, DependencyOutputPath).Replace('\\', '/');

					foreach (var deps in CurrentProject.Dependencies)
					{
						deps.AdditionalLinkInputs += " \"" + DependencyOutputPath + "\" ";
					}
				}

				ToolTask Task = (ToolTask)Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.Link"));
				string LinkerOptions = GenerateTaskCommandLine(Task, new string[] { "OutputFile", "ProfileGuidedDatabase" }, LinkDefinitions.Metadata);

				if (!string.IsNullOrEmpty(CurrentProject.AdditionalLinkInputs))
				{
					LinkerOptions += CurrentProject.AdditionalLinkInputs;
				}
				OutputString.AppendFormat("\t.LinkerOptions = '\"%1\" /OUT:\"%2\" {0}'\n", LinkerOptions.Replace("'","^'"));
				OutputString.AppendFormat("\t.LinkerOutput = '{0}'\n", OutputFile);

				OutputString.Append("\t.Libraries = { ");
				OutputString.Append(CompileActions);
				OutputString.Append(" }\n");

				OutputString.Append("}\n\n");
			}
			else if(BuildOutput == BuildType.StaticLib)
			{
				OutputString.Append(string.Format("Library('{0}')", bffName));
				OutputString.Append("\n{\n\t.Compiler = 'msvc'\n");
				OutputString.Append(string.Format("\t.CompilerOptions = '\"%1\" /Fo\"%2\" /c {0}'\n", CompilerOptions));
				OutputString.Append(string.Format("\t.CompilerOutputPath = \"{0}\"\n", IntDir));

				if (Platform == "Win32" || Platform == "x86")
				{
					OutputString.Append("\t.Librarian = '$VSBasePath$\\VC\\bin\\lib.exe'\n");
				}
				else
				{
					OutputString.Append("\t.Librarian = '$VSBasePath$\\VC\\bin\\amd64\\lib.exe'\n");
				}

				var LibDefinitions = ActiveProject.ItemDefinitions["Lib"];
				string OutputFile = LibDefinitions.GetMetadataValue("OutputFile").Replace('\\','/');

				if(HasCompileActions)
				{
					string DependencyOutputPath = "";
					if (Path.IsPathRooted(OutputFile))
						DependencyOutputPath = Path.GetFullPath(OutputFile).Replace('\\', '/');
					else
						DependencyOutputPath = Path.Combine(ActiveProject.DirectoryPath, OutputFile).Replace('\\', '/');

					foreach (var deps in CurrentProject.Dependencies)
					{
						deps.AdditionalLinkInputs += " \"" + DependencyOutputPath + "\" ";
					}
				}

				ToolTask task = (ToolTask)Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.LIB"));
				string linkerOptions = GenerateTaskCommandLine(task, new string[] { "OutputFile" }, LibDefinitions.Metadata);
				if(!string.IsNullOrEmpty(CurrentProject.AdditionalLinkInputs))
				{
					linkerOptions += CurrentProject.AdditionalLinkInputs;
				}
				OutputString.AppendFormat("\t.LibrarianOptions = '\"%1\" /OUT:\"%2\" {0}'\n", linkerOptions);
				OutputString.AppendFormat("\t.LibrarianOutput = '{0}'\n", OutputFile);

				OutputString.Append("\t.LibrarianAdditionalInputs = { ");
				OutputString.Append(CompileActions);
				OutputString.Append(" }\n");

				OutputString.Append("}\n\n");
			}

			string postbuildName = "postbuild" + bffName;
			string PostBuildBatchFile = "";

			if (ActiveProject.GetItems("PostBuildEvent").Any())
			{
				ProjectItem BuildEvent = ActiveProject.GetItems("PostBuildEvent").First();
				if (BuildEvent.Metadata.Any())
				{
					ProjectMetadata MetaData = BuildEvent.Metadata.First();
					if(!string.IsNullOrEmpty(MetaData.EvaluatedValue))
					{
						string BatchText = "call \"" + VCBasePath + "vcvarsall.bat\" " +
							   (Platform == "Win32" ? "x86" : "x64") + " "
							   + (PlatformToolsetVersion == "140" ? WindowsSDKTarget : "") + "\n";
						PostBuildBatchFile = Path.Combine(ActiveProject.DirectoryPath, Path.GetFileNameWithoutExtension(ActiveProject.FullPath) + "_postbuild.bat");
						File.WriteAllText(PostBuildBatchFile, BatchText + MetaData.EvaluatedValue);
						OutputString.AppendFormat("Exec('{0}') \n{{\n", postbuildName);
						OutputString.AppendFormat("\t.ExecExecutable = '{0}' \n", PostBuildBatchFile);
						OutputString.AppendFormat("\t.ExecInput = '{0}' \n", PostBuildBatchFile);
						OutputString.AppendFormat("\t.ExecOutput = '{0}' \n", PostBuildBatchFile + ".txt");
						OutputString.AppendFormat("\t.PreBuildDependencies = '{0}' \n", bffName);
						OutputString.Append("\t.ExecUseStdOutAsOutput = true \n");
						OutputString.Append("}\n\n");
					}
				}
			}

			CurrentProject.TargetName = string.IsNullOrEmpty(PostBuildBatchFile) ? bffName : postbuildName;
			OutputString.AppendFormat("Alias ('All_{0}')\n{{\n\t.Targets = {{ '{1}' }}\n}} ", bffName, CurrentProject.TargetName);

			if(FileChanged || CommandLineOptions.AlwaysRegenerate)
			{
				File.WriteAllText(BFFOutputFilePath, OutputString.ToString());
			}           
		}

		public static string GenerateTaskCommandLine(
			ToolTask Task,
			string[] PropertiesToSkip,
			IEnumerable<ProjectMetadata> MetaDataList)
		{
			foreach (ProjectMetadata MetaData in MetaDataList)
			{
				if (PropertiesToSkip.Contains(MetaData.Name))
					continue;

				var MatchingProps = Task.GetType().GetProperties().Where(prop => prop.Name == MetaData.Name);
				if (MatchingProps.Any() && !string.IsNullOrEmpty(MetaData.EvaluatedValue))
				{
					string EvaluatedValue = MetaData.EvaluatedValue.Trim();
					if(MetaData.Name == "AdditionalIncludeDirectories")
					{
						EvaluatedValue = EvaluatedValue.Replace("\\\\", "\\");
						EvaluatedValue = EvaluatedValue.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
					}

					PropertyInfo propInfo = MatchingProps.First(); //Dubious
					if (propInfo.PropertyType.IsArray && propInfo.PropertyType.GetElementType() == typeof(string))
					{
						propInfo.SetValue(Task, Convert.ChangeType(EvaluatedValue.Split(';'), propInfo.PropertyType));
					}
					else
					{
						propInfo.SetValue(Task, Convert.ChangeType(EvaluatedValue, propInfo.PropertyType));
					}
				}
			}

			var GenCmdLineMethod = Task.GetType().GetRuntimeMethods().Where(meth => meth.Name == "GenerateCommandLine").First(); //Dubious
			return GenCmdLineMethod.Invoke(Task, new object[] { Type.Missing, Type.Missing }) as string;
		}
	}

}
