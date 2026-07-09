using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace RazorGenerator.Core.Test
{
    public class CoreTest
    {
        private const string Version3OutputFolder = "Output_v3";

        private static readonly string[] TestNames = new[]
        {
            "WebPageTest",
            "WebPageHelperTest",
            "MvcViewTest",
            "MvcHelperTest",
            "TemplateTest",
            "_ViewStart",
            "DirectivesTest",
            "TemplateWithBaseTypeTest",
            "TemplateWithGenericParametersTest",
            "VirtualPathAttributeTest",
            "SuffixTransformerTest"
        };

        public static IEnumerable<object[]> Version3Tests
        {
            get
            {
                return TestNames.Select(testName => new object[] { testName });
            }
        }

        [Theory]
        [MemberData(nameof(Version3Tests))]
        public void TestTransformerType(string testName)
        {
            string workingDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            try
            {
                using (var razorGenerator = new HostManager(workingDirectory, loadExtensions: false, assemblyDirectory: Environment.CurrentDirectory))
                {
                    string inputFile = SaveInputFile(workingDirectory, testName);
                    var host = razorGenerator.CreateHost(inputFile, testName + ".cshtml", string.Empty);
                    host.DefaultNamespace = GetType().Namespace;
                    host.EnableLinePragmas = false;

                    var output = host.GenerateCode();
                    AssertOutput(testName, output);
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(workingDirectory);
                }
                catch
                {
                }
            }

        }

        [Theory]
        [InlineData(@"Views\Helpers\List.cshtml", "MvcHelper")]
        [InlineData(@"Views\Home\Index.cshtml", "MvcView")]
        public void GuessHostUsesMvcHelperForMvcHelperPaths(string projectRelativePath, string expectedHost)
        {
            string workingDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            try
            {
                Directory.CreateDirectory(workingDirectory);
                File.WriteAllText(Path.Combine(workingDirectory, "Sample.csproj"), "<Project><ItemGroup><Reference Include=\"System.Web.Mvc\" /></ItemGroup></Project>");

                HostManager.GuessedHost host;
                Assert.True(HostManager.TryGuessHost(workingDirectory, projectRelativePath, out host));
                Assert.Equal(expectedHost, host.Host);
            }
            finally
            {
                try
                {
                    Directory.Delete(workingDirectory, recursive: true);
                }
                catch
                {
                }
            }
        }

        private static string SaveInputFile(string outputDirectory, string testName)
        {
            if (!Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }
            string outputFile = Path.Combine(outputDirectory, testName + ".cshtml");
            File.WriteAllText(outputFile, GetManifestFileContent(testName, "Input"));
            return outputFile;
        }

        private static void AssertOutput(string testName, string output)
        {
            var expectedContent = GetManifestFileContent(testName, Version3OutputFolder);
            output = Regex.Replace(output, @"Runtime Version:[\d.]*", "Runtime Version:N.N.NNNNN.N")
                          .Replace(typeof(HostManager).Assembly.GetName().Version.ToString(), "v.v.v.v");

            Assert.Equal(expectedContent, output);
        }

        private static string GetManifestFileContent(string testName, string fileType)
        {
            var extension = fileType.Equals("Input", StringComparison.OrdinalIgnoreCase) ? "cshtml" : "txt";
            var resourceName = String.Join(".", "RazorGenerator.Core.Test.TestFiles", fileType, testName, extension);

            using (var reader = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)))
            {
                return reader.ReadToEnd();
            }
        }
    }
}
