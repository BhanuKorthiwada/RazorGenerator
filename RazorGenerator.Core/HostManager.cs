using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel.Composition.Hosting;
using System.ComponentModel.Composition.Primitives;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace RazorGenerator.Core
{
    public class HostManager : IDisposable
    {
        private readonly string _baseDirectory;
        private readonly bool _loadExtensions;
        private readonly string _assemblyDirectory;
        private static readonly Regex MvcHelperPathRegex = new Regex(@"(^|\\)Views(\\.*)+Helpers?", RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase);
        private bool? _isMvcProject;
        private ComposablePartCatalog _catalog;

        public HostManager(string baseDirectory)
            : this(baseDirectory, loadExtensions: true, assemblyDirectory: GetAssemblyDirectory())
        {
        }

        internal HostManager(string baseDirectory, bool loadExtensions, string assemblyDirectory)
        {
            _loadExtensions = loadExtensions;
            _baseDirectory = baseDirectory;
            _assemblyDirectory = assemblyDirectory;

            // Repurposing loadExtensions to mean unit-test scenarios. Don't bind to the AssemblyResolve in unit tests
            if (_loadExtensions)
            {
                AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
            }
        }

        public IRazorHost CreateHost(string fullPath, string projectRelativePath, string vsNamespace)
        {

            CodeLanguageUtil langutil = CodeLanguageUtil.GetLanguageUtilFromFileName(fullPath);

            using (var codeDomProvider = langutil.GetCodeDomProvider())
            {
                return CreateHost(fullPath, projectRelativePath, codeDomProvider, vsNamespace);
            }
        }

        public IRazorHost CreateHost(string fullPath, string projectRelativePath, CodeDomProvider codeDomProvider, string vsNamespace)
        {
            var directives = DirectivesParser.ParseDirectives(_baseDirectory, fullPath);
            directives["VsNamespace"] = vsNamespace;

            string guessedHost = null;
            GuessedHost value;
            if (TryGuessHost(projectRelativePath, out value))
            {
                guessedHost = value.Host;
            }

            string hostName;
            if (!directives.TryGetValue("Generator", out hostName))
            {
                // Determine the host and runtime from the file \ project
                hostName = guessedHost;
            }
            if (_catalog == null)
            {
                _catalog = InitCompositionCatalog(_baseDirectory, _loadExtensions);
            }

            using (var container = new CompositionContainer(_catalog))
            {
                var codeTransformer = GetRazorCodeTransformer(container, projectRelativePath, hostName);
                var host = container.GetExport<IHostProvider>().Value;
                return host.GetRazorHost(projectRelativePath, fullPath, codeTransformer, codeDomProvider, directives);
            }
        }

        private IRazorCodeTransformer GetRazorCodeTransformer(CompositionContainer container, string projectRelativePath, string hostName)
        {

            IRazorCodeTransformer codeTransformer = null;
            try
            {
                codeTransformer = container.GetExportedValue<IRazorCodeTransformer>(hostName);
            }
            catch (Exception exception)
            {
                string availableHosts = String.Join(", ", GetAvailableHosts(container));
                throw new InvalidOperationException(String.Format(CultureInfo.CurrentCulture, RazorGeneratorResources.GeneratorFailureMessage, projectRelativePath, availableHosts), exception);
            }

            if (codeTransformer == null)
            {
                throw new InvalidOperationException(String.Format(CultureInfo.CurrentCulture, RazorGeneratorResources.GeneratorError_UnknownGenerator, hostName));
            }
            return codeTransformer;
        }

        private ComposablePartCatalog InitCompositionCatalog(string baseDirectory, bool loadExtensions)
        {
            // Retrieve available hosts
            var hostsAssembly = GetAssembly();
            var catalog = new AggregateCatalog(new AssemblyCatalog(hostsAssembly));

            if (loadExtensions)
            {
                // We assume that the baseDirectory points to the project root. Look for the RazorHosts directory under the project root
                AddCatalogIfHostsDirectoryExists(catalog, baseDirectory);

                // Look for the Razor Hosts directory up to two directories above the baseDirectory. Hopefully this should cover the solution root.
                var solutionDirectory = Path.Combine(baseDirectory, @"..\");
                AddCatalogIfHostsDirectoryExists(catalog, solutionDirectory);

                solutionDirectory = Path.Combine(baseDirectory, @"..\..\");
                AddCatalogIfHostsDirectoryExists(catalog, solutionDirectory);
            }

            return catalog;
        }

        private static IEnumerable<string> GetAvailableHosts(CompositionContainer container)
        {
            // We need for a way to figure out what the exporting type is. This could return arbitrary exports that are not ISingleFileGenerators
            return from part in container.Catalog.Parts
                   from export in part.ExportDefinitions
                   where !String.IsNullOrEmpty(export.ContractName)
                   select export.ContractName;
        }

        private Assembly GetAssembly()
        {
            // TODO: Check if we can switch to using CodeBase instead of Location

            // Look for the assembly at v3\RazorGenerator.Core.v3.dll. If not, assume it is at RazorGenerator.Core.v3.dll
            string runtimeDirectory = Path.Combine(_assemblyDirectory, "v3");
            string assemblyName = "RazorGenerator.Core.v3.dll";
            string runtimeDirPath = Path.Combine(runtimeDirectory, assemblyName);
            if (File.Exists(runtimeDirPath))
            {
                Assembly assembly = Assembly.LoadFrom(runtimeDirPath);

                return assembly;
            }
            else
            {
                return Assembly.LoadFrom(Path.Combine(_assemblyDirectory, assemblyName));
            }
        }

        internal static bool TryGuessHost(string projectRoot, string projectRelativePath, out GuessedHost host)
        {
            bool isMvcProject = IsMvcProject(projectRoot) ?? false;
            return TryGuessHost(projectRelativePath, isMvcProject, out host);
        }

        private bool TryGuessHost(string projectRelativePath, out GuessedHost host)
        {
            if (!_isMvcProject.HasValue)
            {
                _isMvcProject = IsMvcProject(_baseDirectory) ?? false;
            }

            return TryGuessHost(projectRelativePath, _isMvcProject.Value, out host);
        }

        private static bool TryGuessHost(string projectRelativePath, bool isMvcProject, out GuessedHost host)
        {
            if (isMvcProject)
            {
                if (MvcHelperPathRegex.IsMatch(projectRelativePath))
                {
                    host = new GuessedHost("MvcHelper");
                    return true;
                }
                host = new GuessedHost("MvcView");
                return true;
            }

            host = default(GuessedHost);
            return false;
        }

        private static bool? IsMvcProject(string projectRoot)
        {
            try
            {
                var projectFile = Directory.EnumerateFiles(projectRoot, "*.csproj").FirstOrDefault();
                if (projectFile == null)
                {
                    projectFile = Directory.EnumerateFiles(projectRoot, "*.vbproj").FirstOrDefault();
                }
                if (projectFile != null)
                {
                    var content = File.ReadAllText(projectFile);
                    return content.IndexOf("System.Web.Mvc", StringComparison.OrdinalIgnoreCase) != -1;
                }
            }
            catch
            {
            }
            return null;
        }

        private static void AddCatalogIfHostsDirectoryExists(AggregateCatalog catalog, string directory)
        {
            var extensionsDirectory = Path.GetFullPath(Path.Combine(directory, "RazorHosts"));
            if (Directory.Exists(extensionsDirectory))
            {
                catalog.Catalogs.Add(new DirectoryCatalog(extensionsDirectory));
            }
        }

        private Assembly OnAssemblyResolve(object sender, ResolveEventArgs eventArgs)
        {
            var nameToResolve = new AssemblyName(eventArgs.Name);
            string path = Path.Combine(_assemblyDirectory, "v3", nameToResolve.Name) + ".dll";
            if (File.Exists(path))
            {
                return Assembly.LoadFrom(path);
            }
            path = Path.Combine(_assemblyDirectory, nameToResolve.Name) + ".dll";
            if (File.Exists(path))
            {
                return Assembly.LoadFrom(path);
            }
            return null;
        }

        /// <remarks>
        /// Attempts to locate where the RazorGenerator.Core assembly is being loaded from. This allows us to locate the v3 assembly and the corresponding
        /// System.Web.* binaries
        /// Assembly.CodeBase points to the original location when the file is shadow copied, so we'll attempt to use that first.
        /// </remarks>
        private static string GetAssemblyDirectory()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            Uri uri;
            if (Uri.TryCreate(assembly.CodeBase, UriKind.Absolute, out uri) && uri.IsFile)
            {
                return Path.GetDirectoryName(uri.LocalPath);
            }
            return Path.GetDirectoryName(assembly.Location);
        }

        public void Dispose()
        {
            if (_catalog != null)
            {
                _catalog.Dispose();
            }

            if (_loadExtensions)
            {
                AppDomain.CurrentDomain.AssemblyResolve -= OnAssemblyResolve;
            }
        }

        internal class GuessedHost
        {
            public GuessedHost(string host)
            {
                Host = host;
            }

            public string Host { get; private set; }
        }
    }
}
