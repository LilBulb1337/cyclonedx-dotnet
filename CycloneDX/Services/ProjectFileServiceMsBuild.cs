using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using CycloneDX.Interfaces;
using CycloneDX.Models;
using Microsoft.Build.Evaluation;

namespace CycloneDX.Services
{
    public class ProjectFileServiceMsBuild : IProjectFileService
    {
        private IFileSystem _fileSystem;
        private IDotnetUtilsService _dotnetUtilsService;
        private IPackagesFileService _packagesFileService;
        private IProjectAssetsFileService _projectAssetsFileService;
        private Dictionary<string, (string filename, string projectPath, string hash)> _projectDictionary = new();
        private SHA256 _sha256 = SHA256.Create();
        // TODO configure properties
        private Dictionary<string, string> _globalProperties = new() { { "Configuration", "Release" } };

        public ProjectFileServiceMsBuild(
            IFileSystem fileSystem,
            IDotnetUtilsService dotnetUtilsService,
            IPackagesFileService packagesFileService,
            IProjectAssetsFileService projectAssetsFileService)
        {
            _fileSystem = fileSystem;
            _dotnetUtilsService = dotnetUtilsService;
            _packagesFileService = packagesFileService;
            _projectAssetsFileService = projectAssetsFileService;
        }

        public bool IsTestProject(string projectFilePath)
        {
            if (!_fileSystem.File.Exists(projectFilePath))
            {
                return false;
            }

            using var projectCollection = new ProjectCollection(_globalProperties);
            var project = new Project(projectFilePath, _globalProperties, null, projectCollection);

            // Check for Microsoft.NET.Test.Sdk package reference
            var hasTestSdkReference = project.Items
                .Any(item => item.ItemType == "PackageReference" && item.EvaluatedInclude == "Microsoft.NET.Test.Sdk");

            // Check for IsTestProject property
            var isTestProjectProperty = project.GetProperty("IsTestProject")?.EvaluatedValue == "true";

            return hasTestSdkReference || isTestProjectProperty;
        }

        private (string name, string version) GetAssemblyNameAndVersion(string projectFilePath)
        {
            if (!_fileSystem.File.Exists(projectFilePath))
            {
                return (projectFilePath, "undefined");
            }

            using var projectCollection = new ProjectCollection(_globalProperties);

            var project = new Project(projectFilePath, _globalProperties, null, projectCollection);

            // Get AssemblyName
            var assemblyName = project.GetProperty("AssemblyName")?.EvaluatedValue
                ?? _fileSystem.Path.GetFileNameWithoutExtension(projectFilePath);

            // Get Version
            var version = project.GetProperty("Version")?.EvaluatedValue;

            return (assemblyName, version ?? "1.0.0");
        }

        static internal string GetProjectProperty(string projectFilePath, string baseIntermediateOutputPath)
        {
            if (string.IsNullOrEmpty(baseIntermediateOutputPath))
            {
                return Path.Combine(Path.GetDirectoryName(projectFilePath), "obj");
            }
            else
            {
                string folderName = Path.GetFileNameWithoutExtension(projectFilePath);
                return Path.Combine(baseIntermediateOutputPath, "obj", folderName);
            }
        }

        public bool DisablePackageRestore { get; set; }

        public async Task<HashSet<DotnetDependency>> GetProjectDotnetDependencysAsync(string projectFilePath, string baseIntermediateOutputPath, bool excludeTestProjects, string framework, string runtime)
        {
            if (!_fileSystem.File.Exists(projectFilePath))
            {
                Console.Error.WriteLine($"Project file \"{projectFilePath}\" does not exist");
                return new HashSet<DotnetDependency>();
            }

            var isTestProject = IsTestProject(projectFilePath);

            Console.WriteLine();
            Console.WriteLine($"» Analyzing: {projectFilePath}");

            if (excludeTestProjects && isTestProject)
            {
                Console.WriteLine($"Skipping: {projectFilePath}");
                return new HashSet<DotnetDependency>();
            }

            if (!DisablePackageRestore)
            {
                Console.WriteLine("  Attempting to restore packages");
                var restoreResult = _dotnetUtilsService.Restore(projectFilePath, framework, runtime);

                if (restoreResult.Success)
                {
                    Console.WriteLine("  Packages restored");
                }
                else
                {
                    Console.WriteLine("Dotnet restore failed:");
                    Console.WriteLine(restoreResult.ErrorMessage);
                    throw new DotnetRestoreException($"Dotnet restore failed with message: {restoreResult.ErrorMessage}");
                }
            }

            var assetsFilename = _fileSystem.Path.Combine(GetProjectProperty(projectFilePath, baseIntermediateOutputPath), "project.assets.json");
            if (!_fileSystem.File.Exists(assetsFilename))
            {
                Console.WriteLine($"File not found: \"{assetsFilename}\", \"{projectFilePath}\" ");
            }
            var packages = _projectAssetsFileService.GetDotnetDependencys(projectFilePath, assetsFilename, isTestProject);

            if (!packages.Any())
            {
                Console.WriteLine("  No packages found");
                var directoryPath = _fileSystem.Path.GetDirectoryName(projectFilePath);
                var packagesPath = _fileSystem.Path.Combine(directoryPath, "packages.config");
                if (_fileSystem.File.Exists(packagesPath))
                {
                    Console.WriteLine("  Found packages.config. Will attempt to process");
                    packages = await _packagesFileService.GetDotnetDependencysAsync(packagesPath).ConfigureAwait(false);
                }
            }

            return packages;
        }

        public async Task<HashSet<string>> GetProjectReferencesAsync(string projectFilePath)
        {
            if (!_fileSystem.File.Exists(projectFilePath))
            {
                Console.Error.WriteLine($"Project file \"{projectFilePath}\" does not exist");
                return new HashSet<string>();
            }

            Console.WriteLine();
            Console.WriteLine($"» Analyzing: {projectFilePath}");
            Console.WriteLine("  Getting project references");

            var projectReferences = new HashSet<string>();

            using var projectCollection = new ProjectCollection(_globalProperties);

            var project = new Project(projectFilePath, _globalProperties, null, projectCollection);
            var targetPath = project.GetProperty("TargetPath").EvaluatedValue;
            var assemblyName = project.GetProperty("AssemblyName").EvaluatedValue;
            var hash = await _sha256.ComputeHashAsync(File.OpenRead(targetPath));
            var hashString = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();

            _projectDictionary.Add(assemblyName, (targetPath, projectFilePath, hashString));

            foreach (var projectReference in project.Items.Where(item => item.ItemType == "ProjectReference"))
            {
                var fullPath = _fileSystem.Path.GetFullPath(_fileSystem.Path.Combine(
                    _fileSystem.Path.GetDirectoryName(projectFilePath),
                    projectReference.EvaluatedInclude));
                projectReferences.Add(fullPath);
            }

            if (projectReferences.Count == 0)
            {
                Console.WriteLine("  No project references found");
            }

            return projectReferences;
        }

        public async Task<HashSet<DotnetDependency>> RecursivelyGetProjectDotnetDependencysAsync(
            string projectFilePath,
            string baseIntermediateOutputPath,
            bool excludeTestProjects,
            string framework,
            string runtime)
        {
            var dependencies = new HashSet<DotnetDependency>();
            var visitedProjects = new HashSet<string>();
            var projectQueue = new Queue<string>();

            projectQueue.Enqueue(projectFilePath);

            while (projectQueue.Count > 0)
            {
                var currentProject = projectQueue.Dequeue();

                if (visitedProjects.Contains(currentProject))
                {
                    continue;
                }

                visitedProjects.Add(currentProject);

                // Get dependencies for the current project
                var projectDependencies = await GetProjectDotnetDependencysAsync(
                    currentProject,
                    baseIntermediateOutputPath,
                    excludeTestProjects,
                    framework,
                    runtime).ConfigureAwait(false);

                dependencies.UnionWith(projectDependencies);

                // Get project references for the current project
                var projectReferences = await GetProjectReferencesAsync(currentProject).ConfigureAwait(false);

                foreach (var reference in projectReferences)
                {
                    if (!visitedProjects.Contains(reference))
                    {
                        projectQueue.Enqueue(reference);
                    }
                }
            }

            return dependencies;
        }

        public async Task<HashSet<DotnetDependency>> RecursivelyGetProjectReferencesAsync(string projectFilePath)
        {
            var projectReferences = new HashSet<DotnetDependency>();

            var files = new Queue<string>();
            files.Enqueue(_fileSystem.FileInfo.New(projectFilePath).FullName);

            var visitedProjectFiles = new HashSet<string>();

            while (files.Count > 0)
            {
                var currentFile = files.Dequeue();

                if (!Utils.IsSupportedProjectType(currentFile))
                {
                    continue;
                }

                var foundProjectReferences = await GetProjectReferencesAsync(currentFile).ConfigureAwait(false);

                var nameAndVersion = GetAssemblyNameAndVersion(currentFile);

                DotnetDependency dependency = new()
                {
                    Name = nameAndVersion.name,
                    Version = nameAndVersion.version ?? "1.0.0",
                    Path = currentFile,
                    Dependencies = foundProjectReferences
                        .Select(GetAssemblyNameAndVersion)
                        .ToDictionary(project => project.name, project => project.version ?? "1.0.0"),
                    Scope = Component.ComponentScope.Required,
                    DependencyType = DependencyType.Project
                };
                projectReferences.Add(dependency);

                foreach (string projectReferencePath in foundProjectReferences)
                {
                    if (!visitedProjectFiles.Contains(projectReferencePath))
                    {
                        files.Enqueue(projectReferencePath);
                    }
                }

                visitedProjectFiles.Add(currentFile);
            }

            return projectReferences;
        }

        public Component GetComponent(DotnetDependency dotnetDependency)
        {
            if (dotnetDependency?.DependencyType != DependencyType.Project)
            {
                return null;
            }
            var component = SetupComponent(dotnetDependency.Name, dotnetDependency.Version, Component.ComponentScope.Required);

            return component;
        }

        private Component SetupComponent(string name, string version, Component.ComponentScope? scope)
        {
            var specialData = _projectDictionary[name];
            // TODO vendor, productName and  productVersion need a configuration mechanism
            var vendor = Environment.GetEnvironmentVariable("VENDOR");
            var productName = Environment.GetEnvironmentVariable("PRODUCTNAME");
            var productVersion = Environment.GetEnvironmentVariable("PRODUCTVERSION");
            var component = new Component
            {
                Name = name,
                Version = version,
                Scope = scope,
                Type = Component.Classification.Library,
                BomRef = $"{name}@{version}",
                Cpe = Cpe.Create(part: "a", vendor: vendor, product: productName, version: productVersion),
                Properties = [ new Property { Name = "filename", Value = Path.GetFileName(specialData.filename) } ],
                Hashes = [ new Hash { Alg = Hash.HashAlgorithm.SHA3_256, Content = specialData.hash } ]
            };
            return component;
        }
    }
}
