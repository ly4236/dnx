// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Framework.PathHelpers;
using Microsoft.Framework.Runtime.Compilation;
using Microsoft.Framework.Runtime.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NuGet;

namespace Microsoft.Framework.Runtime
{
    public class Project : ICompilationProject
    {
        public const string ProjectFileName = "project.json";

        public static readonly TypeInformation DefaultRuntimeCompiler = new TypeInformation("Microsoft.Framework.Runtime.Roslyn", "Microsoft.Framework.Runtime.Roslyn.RoslynProjectCompiler");
        public static readonly TypeInformation DefaultDesignTimeCompiler = new TypeInformation("Microsoft.Framework.Runtime.Compilation.DesignTime", "Microsoft.Framework.Runtime.DesignTimeHostProjectCompiler");

        public static TypeInformation DefaultCompiler = DefaultRuntimeCompiler;

        private static readonly CompilerOptions _emptyOptions = new CompilerOptions();

        private readonly Dictionary<FrameworkName, TargetFrameworkInformation> _targetFrameworks = new Dictionary<FrameworkName, TargetFrameworkInformation>();
        private readonly Dictionary<FrameworkName, CompilerOptions> _compilationOptions = new Dictionary<FrameworkName, CompilerOptions>();
        private readonly Dictionary<string, CompilerOptions> _configurations = new Dictionary<string, CompilerOptions>(StringComparer.OrdinalIgnoreCase);

        private CompilerOptions _defaultCompilerOptions;

        private TargetFrameworkInformation _defaultTargetFrameworkConfiguration;

        public Project()
        {
        }

        public string ProjectFilePath { get; private set; }

        public string ProjectDirectory
        {
            get
            {
                return Path.GetDirectoryName(ProjectFilePath);
            }
        }

        public string Name { get; private set; }

        public string Title { get; set; }

        public string Description { get; private set; }

        public string Copyright { get; set; }

        public string Summary { get; set; }

        public string Language { get; set; }

        public string ReleaseNotes { get; set; }

        public string[] Authors { get; private set; }

        public string[] Owners { get; private set; }

        public bool EmbedInteropTypes { get; set; }

        public SemanticVersion Version { get; private set; }

        // Temporary while old and new runtime are separate
        string ICompilationProject.Version { get { return Version?.ToString(); } }
        string ICompilationProject.AssemblyFileVersion { get { return AssemblyFileVersion?.ToString(); } }

        public Version AssemblyFileVersion { get; private set; }

        public IList<LibraryDependency> Dependencies { get; private set; }

        public CompilerServices CompilerServices { get; private set; }

        public string WebRoot { get; private set; }

        public string EntryPoint { get; private set; }

        public string ProjectUrl { get; private set; }

        public string LicenseUrl { get; set; }

        public string IconUrl { get; set; }

        public bool RequireLicenseAcceptance { get; private set; }

        public string[] Tags { get; private set; }

        public bool IsLoadable { get; set; }

        public IProjectFilesCollection Files { get; private set; }

        public IDictionary<string, string> Commands { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public IDictionary<string, IEnumerable<string>> Scripts { get; } = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<TargetFrameworkInformation> GetTargetFrameworks()
        {
            return _targetFrameworks.Values;
        }

        public IEnumerable<string> GetConfigurations()
        {
            return _configurations.Keys;
        }

        public static bool HasProjectFile(string path)
        {
            string projectPath = Path.Combine(path, ProjectFileName);

            return File.Exists(projectPath);
        }

        public static bool TryGetProject(string path, out Project project, ICollection<ICompilationMessage> diagnostics = null)
        {
            project = null;

            string projectPath = null;

            if (String.Equals(Path.GetFileName(path), ProjectFileName, StringComparison.OrdinalIgnoreCase))
            {
                projectPath = path;
                path = Path.GetDirectoryName(path);
            }
            else if (!HasProjectFile(path))
            {
                return false;
            }
            else
            {
                projectPath = Path.Combine(path, ProjectFileName);
            }

            // Assume the directory name is the project name if none was specified
            var projectName = PathUtility.GetDirectoryName(path);
            projectPath = Path.GetFullPath(projectPath);

            try
            {
                using (var stream = File.OpenRead(projectPath))
                {
                    project = GetProjectFromStream(stream, projectName, projectPath, diagnostics);
                }
            }
            catch (JsonReaderException ex)
            {
                throw FileFormatException.Create(ex, projectPath);
            }

            return true;
        }

        public static Project GetProject(string json, string projectName, string projectPath, ICollection<ICompilationMessage> diagnostics = null)
        {
            var ms = new MemoryStream(Encoding.UTF8.GetBytes(json));

            var project = GetProjectFromStream(ms, projectName, projectPath, diagnostics);

            return project;
        }

        internal static Project GetProjectFromStream(Stream stream, string projectName, string projectPath, ICollection<ICompilationMessage> diagnostics = null)
        {
            var project = new Project();

            var reader = new JsonTextReader(new StreamReader(stream));
            var rawProject = JObject.Load(reader);

            // Meta-data properties
            var version = rawProject["version"];
            var authors = rawProject["authors"];
            var owners = rawProject["owners"];
            var tags = rawProject["tags"];
            var buildVersion = Environment.GetEnvironmentVariable("DNX_BUILD_VERSION");

            project.Name = projectName;
            project.ProjectFilePath = Path.GetFullPath(projectPath);

            if (version == null)
            {
                project.Version = new SemanticVersion("1.0.0");
            }
            else
            {
                try
                {
                    project.Version = SpecifySnapshot(version.Value<string>(), buildVersion);
                }
                catch (Exception ex)
                {
                    var lineInfo = (IJsonLineInfo)version;

                    throw FileFormatException.Create(ex, version, project.ProjectFilePath);
                }
            }

            var fileVersion = Environment.GetEnvironmentVariable("DNX_ASSEMBLY_FILE_VERSION");
            if (string.IsNullOrWhiteSpace(fileVersion))
            {
                project.AssemblyFileVersion = project.Version.Version;
            }
            else
            {
                try
                {
                    var simpleVersion = project.Version.Version;
                    project.AssemblyFileVersion = new Version(simpleVersion.Major,
                        simpleVersion.Minor,
                        simpleVersion.Build,
                        int.Parse(fileVersion));
                }
                catch (FormatException ex)
                {
                    throw new FormatException("The assembly file version is invalid: " + fileVersion, ex);
                }
            }

            project.Description = rawProject.GetValue<string>("description");
            project.Summary = rawProject.GetValue<string>("summary");
            project.Copyright = rawProject.GetValue<string>("copyright");
            project.Title = rawProject.GetValue<string>("title");
            project.Authors = authors == null ? new string[] { } : authors.ValueAsArray<string>();
            project.Owners = owners == null ? new string[] { } : owners.ValueAsArray<string>();
            project.Dependencies = new List<LibraryDependency>();
            project.WebRoot = rawProject.GetValue<string>("webroot");
            project.EntryPoint = rawProject.GetValue<string>("entryPoint");
            project.ProjectUrl = rawProject.GetValue<string>("projectUrl");
            project.LicenseUrl = rawProject.GetValue<string>("licenseUrl");
            project.IconUrl = rawProject.GetValue<string>("iconUrl");
            project.RequireLicenseAcceptance = rawProject.GetValue<bool?>("requireLicenseAcceptance") ?? false;
            project.Tags = tags == null ? new string[] { } : tags.ValueAsArray<string>();
            project.Language = rawProject.GetValue<string>("language");
            project.ReleaseNotes = rawProject.GetValue<string>("releaseNotes");
            project.IsLoadable = rawProject.GetValue<bool?>("loadable") ?? true;

            // TODO: Move this to the dependencies node
            project.EmbedInteropTypes = rawProject.GetValue<bool>("embedInteropTypes");

            // Project files
            project.Files = new ProjectFilesCollection(rawProject, project.ProjectDirectory, project.ProjectFilePath, diagnostics);

            var compilerInfo = rawProject["compiler"] as JObject;

            if (compilerInfo != null)
            {
                var languageName = compilerInfo.GetValue<string>("name") ?? "C#";
                var compilerAssembly = compilerInfo.GetValue<string>("compilerAssembly");
                var compilerType = compilerInfo.GetValue<string>("compilerType");

                var compiler = new TypeInformation(compilerAssembly, compilerType);
                project.CompilerServices = new CompilerServices(languageName, compiler);
            }

            var commands = rawProject["commands"] as JObject;
            if (commands != null)
            {
                foreach (var command in commands)
                {
                    project.Commands[command.Key] = command.Value.Value<string>();
                }
            }

            var scripts = rawProject["scripts"] as JObject;
            if (scripts != null)
            {
                foreach (var script in scripts)
                {
                    var value = script.Value;
                    if (value.Type == JTokenType.String)
                    {
                        project.Scripts[script.Key] = new string[] { value.Value<string>() };
                    }
                    else if (value.Type == JTokenType.Array)
                    {
                        project.Scripts[script.Key] = script.Value.ValueAsArray<string>();
                    }
                    else
                    {
                        throw FileFormatException.Create(
                            string.Format("The value of a script in {0} can only be a string or an array of strings", ProjectFileName),
                            value,
                            project.ProjectFilePath);
                    }
                }
            }

            project.BuildTargetFrameworksAndConfigurations(rawProject);

            PopulateDependencies(
                project.ProjectFilePath,
                project.Dependencies,
                rawProject,
                "dependencies",
                isGacOrFrameworkReference: false);

            return project;
        }

        public bool IsValid(LockFile lockFile)
        {
            if (lockFile.Version != LockFileFormat.Version)
            {
                return false;
            }

            var actualTargetFrameworks = GetTargetFrameworks();

            // The lock file should contain dependencies for each framework plus dependencies shared by all frameworks
            if (lockFile.ProjectFileDependencyGroups.Count != actualTargetFrameworks.Count() + 1)
            {
                return false;
            }

            foreach (var group in lockFile.ProjectFileDependencyGroups)
            {
                IOrderedEnumerable<string> actualDependencies;
                var expectedDependencies = group.Dependencies.OrderBy(x => x);

                // If the framework name is empty, the associated dependencies are shared by all frameworks
                if (string.IsNullOrEmpty(group.FrameworkName))
                {
                    actualDependencies = Dependencies.Select(x => x.LibraryRange.ToString()).OrderBy(x => x);
                }
                else
                {
                    var framework = actualTargetFrameworks
                        .FirstOrDefault(f =>
                            string.Equals(f.FrameworkName.ToString(), group.FrameworkName, StringComparison.Ordinal));
                    if (framework == null)
                    {
                        return false;
                    }

                    actualDependencies = framework.Dependencies.Select(d => d.LibraryRange.ToString()).OrderBy(x => x);
                }

                if (!actualDependencies.SequenceEqual(expectedDependencies))
                {
                    return false;
                }
            }

            return true;
        }

        private static SemanticVersion SpecifySnapshot(string version, string snapshotValue)
        {
            if (version.EndsWith("-*"))
            {
                if (string.IsNullOrEmpty(snapshotValue))
                {
                    version = version.Substring(0, version.Length - 2);
                }
                else
                {
                    version = version.Substring(0, version.Length - 1) + snapshotValue;
                }
            }

            return new SemanticVersion(version);
        }

        private static void PopulateDependencies(
            string projectPath,
            IList<LibraryDependency> results,
            JObject settings,
            string propertyName,
            bool isGacOrFrameworkReference)
        {
            var dependencies = settings[propertyName] as JObject;
            if (dependencies != null)
            {
                foreach (var dependency in dependencies.Properties())
                {
                    if (string.IsNullOrEmpty(dependency.Name))
                    {

                        throw FileFormatException.Create(
                            "Unable to resolve dependency ''.",
                            dependency,
                            projectPath);
                    }

                    // Support 
                    // "dependencies" : {
                    //    "Name" : "1.0"
                    // }

                    var dependencyValue = dependency.Value;
                    var dependencyTypeValue = LibraryDependencyType.Default;

                    string dependencyVersionValue = null;
                    JToken dependencyVersionToken = dependencyValue;

                    if (dependencyValue.Type == JTokenType.String)
                    {
                        dependencyVersionValue = dependencyValue.Value<string>();
                    }
                    else
                    {
                        if (dependencyValue.Type == JTokenType.Object)
                        {
                            dependencyVersionToken = dependencyValue["version"];
                            if (dependencyVersionToken != null && dependencyVersionToken.Type == JTokenType.String)
                            {
                                dependencyVersionValue = dependencyVersionToken.Value<string>();
                            }
                        }

                        IEnumerable<string> strings;
                        if (TryGetStringEnumerable(dependencyValue["type"], out strings))
                        {
                            dependencyTypeValue = LibraryDependencyType.Parse(strings);
                        }
                    }

                    SemanticVersionRange dependencyVersionRange = null;

                    if (!string.IsNullOrEmpty(dependencyVersionValue))
                    {
                        try
                        {
                            dependencyVersionRange = VersionUtility.ParseVersionRange(dependencyVersionValue);
                        }
                        catch (Exception ex)
                        {
                            throw FileFormatException.Create(
                                ex,
                                dependencyVersionToken,
                                projectPath);
                        }
                    }

                    var dependencyLineInfo = (IJsonLineInfo)dependency;

                    results.Add(new LibraryDependency
                    {
                        LibraryRange = new LibraryRange
                        {
                            Name = dependency.Name,
                            VersionRange = dependencyVersionRange,
                            IsGacOrFrameworkReference = isGacOrFrameworkReference,
                            FileName = projectPath,
                            Line = dependencyLineInfo.LineNumber,
                            Column = dependencyLineInfo.LinePosition
                        },
                        Type = dependencyTypeValue
                    });
                }
            }
        }

        private static bool TryGetStringEnumerable(JToken token, out IEnumerable<string> result)
        {
            IEnumerable<string> values;
            if (token == null)
            {
                result = null;
                return false;
            }
            else if (token.Type == JTokenType.String)
            {
                values = new[]
                {
                    token.Value<string>()
                };
            }
            else
            {
                values = token.Value<string[]>();
            }
            result = values
                .SelectMany(value => value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries));
            return true;
        }

        public CompilerOptions GetCompilerOptions()
        {
            return _defaultCompilerOptions;
        }

        public CompilerOptions GetCompilerOptions(string configurationName)
        {
            CompilerOptions options;
            if (_configurations.TryGetValue(configurationName, out options))
            {
                return options;
            }

            return null;
        }

        public CompilerOptions GetCompilerOptions(FrameworkName frameworkName)
        {
            CompilerOptions options;
            if (_compilationOptions.TryGetValue(frameworkName, out options))
            {
                return options;
            }

            return null;
        }

        public ICompilerOptions GetCompilerOptions(FrameworkName targetFramework,
                                                  string configurationName)
        {
            // Get all project options and combine them
            var rootOptions = GetCompilerOptions();
            var configurationOptions = configurationName != null ? GetCompilerOptions(configurationName) : null;
            var targetFrameworkOptions = targetFramework != null ? GetCompilerOptions(targetFramework) : null;

            // Combine all of the options
            return CompilerOptions.Combine(rootOptions, configurationOptions, targetFrameworkOptions);
        }

        public TargetFrameworkInformation GetTargetFramework(FrameworkName targetFramework)
        {
            TargetFrameworkInformation targetFrameworkInfo;
            if (_targetFrameworks.TryGetValue(targetFramework, out targetFrameworkInfo))
            {
                return targetFrameworkInfo;
            }

            IEnumerable<TargetFrameworkInformation> compatibleConfigurations;
            if (VersionUtility.TryGetCompatibleItems(targetFramework, GetTargetFrameworks(), out compatibleConfigurations) &&
                compatibleConfigurations.Any())
            {
                targetFrameworkInfo = compatibleConfigurations.FirstOrDefault();
            }

            return targetFrameworkInfo ?? _defaultTargetFrameworkConfiguration;
        }

        private void BuildTargetFrameworksAndConfigurations(JObject rawProject)
        {
            // Get the shared compilationOptions
            _defaultCompilerOptions = GetCompilationOptions(rawProject) ?? _emptyOptions;

            _defaultTargetFrameworkConfiguration = new TargetFrameworkInformation
            {
                Dependencies = new List<LibraryDependency>()
            };

            // Add default configurations
            _configurations["Debug"] = new CompilerOptions
            {
                Defines = new[] { "DEBUG", "TRACE" },
                Optimize = false
            };

            _configurations["Release"] = new CompilerOptions
            {
                Defines = new[] { "RELEASE", "TRACE" },
                Optimize = true
            };

            // The configuration node has things like debug/release compiler settings
            /*
                {
                    "configurations": {
                        "Debug": {
                        },
                        "Release": {
                        }
                    }
                }
            */
            var configurations = rawProject["configurations"] as JObject;
            if (configurations != null)
            {
                foreach (var configuration in configurations)
                {
                    var compilerOptions = GetCompilationOptions(configuration.Value);

                    // Only use this as a configuration if it's not a target framework
                    _configurations[configuration.Key] = compilerOptions;
                }
            }

            // The frameworks node is where target frameworks go
            /*
                {
                    "frameworks": {
                        "net45": {
                        },
                        "k10": {
                        }
                    }
                }
            */

            var frameworks = rawProject["frameworks"] as JObject;
            if (frameworks != null)
            {
                foreach (var framework in frameworks)
                {
                    try
                    {
                        BuildTargetFrameworkNode(framework);
                    }
                    catch (Exception ex)
                    {
                        throw FileFormatException.Create(ex, framework.Value, ProjectFilePath);
                    }
                }
            }
        }

        private bool BuildTargetFrameworkNode(KeyValuePair<string, JToken> targetFramework)
        {
            // If no compilation options are provided then figure them out from the node
            var compilerOptions = GetCompilationOptions(targetFramework.Value) ??
                                  new CompilerOptions();

            var frameworkName = FrameworkNameHelper.ParseFrameworkName(targetFramework.Key);

            // If it's not unsupported then keep it
            if (frameworkName == VersionUtility.UnsupportedFrameworkName)
            {
                // REVIEW: Should we skip unsupported target frameworks
                return false;
            }

            // Add the target framework specific define
            var defines = new HashSet<string>(compilerOptions.Defines ?? Enumerable.Empty<string>());
            var frameworkDefinition = Tuple.Create(targetFramework.Key, frameworkName);
            var frameworkDefine = FrameworkNameHelper.MakeDefaultTargetFrameworkDefine(frameworkDefinition);

            if (!string.IsNullOrEmpty(frameworkDefine))
            {
                defines.Add(frameworkDefine);
            }

            compilerOptions.Defines = defines;

            var targetFrameworkInformation = new TargetFrameworkInformation
            {
                FrameworkName = frameworkName,
                Dependencies = new List<LibraryDependency>()
            };

            var properties = targetFramework.Value.Value<JObject>();
            var frameworkDependencies = new List<LibraryDependency>();

            PopulateDependencies(
                ProjectFilePath,
                frameworkDependencies,
                properties,
                "dependencies",
                isGacOrFrameworkReference: false);

            var frameworkAssemblies = new List<LibraryDependency>();
            PopulateDependencies(
                ProjectFilePath,
                frameworkAssemblies,
                properties,
                "frameworkAssemblies",
                isGacOrFrameworkReference: true);

            frameworkDependencies.AddRange(frameworkAssemblies);
            targetFrameworkInformation.Dependencies = frameworkDependencies;

            targetFrameworkInformation.WrappedProject = properties.GetValue<string>("wrappedProject");

            var binNode = properties["bin"];

            if (binNode != null)
            {
                targetFrameworkInformation.AssemblyPath = binNode.GetValue<string>("assembly");
                targetFrameworkInformation.PdbPath = binNode.GetValue<string>("pdb");
            }

            _compilationOptions[frameworkName] = compilerOptions;
            _targetFrameworks[frameworkName] = targetFrameworkInformation;

            return true;
        }

        private CompilerOptions GetCompilationOptions(JToken topLevelOrConfiguration)
        {
            var rawOptions = topLevelOrConfiguration["compilationOptions"];

            if (rawOptions == null)
            {
                return null;
            }

            return new CompilerOptions()
            {
                Defines = rawOptions.ValueAsArray<string>("define"),
                LanguageVersion = rawOptions.GetValue<string>("languageVersion"),
                AllowUnsafe = rawOptions.GetValue<bool?>("allowUnsafe"),
                Platform = rawOptions.GetValue<string>("platform"),
                WarningsAsErrors = rawOptions.GetValue<bool?>("warningsAsErrors"),
                Optimize = rawOptions.GetValue<bool?>("optimize"),
            };
        }
    }
}