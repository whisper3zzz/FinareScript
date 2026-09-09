using System.Diagnostics;
using UnifiedScript.Cli;
using UnifiedScript.Core;

namespace UnifiedScript.Tests;

public sealed class SourceIdentityInitCommandTests
{
    [Fact]
    public void ProjectModeCompilesAllSourcesPreservesOtherSidecarsAndRejectsUnsafeTargets()
    {
        using Fixture fixture = Fixture.Create();
        byte[] originalOtherSource = File.ReadAllBytes(fixture.OtherSourcePath);
        byte[] originalProject = File.ReadAllBytes(fixture.ProjectPath);
        Assert.Equal(0, Program.Main([
            "identity-init",
            fixture.OtherSourcePath,
            "--project", fixture.ProjectPath,
            "--out", fixture.OtherSidecarPath,
            "--configuration", "Debug",
            "--no-restore",
            "--no-build",
            "--dotnet", Fixture.DotNetPath,
            "--reference", fixture.FriendAssemblyPath,
        ]));
        byte[] otherSidecar = File.ReadAllBytes(fixture.OtherSidecarPath);
        Assert.NotEmpty(IdentitySidecarJson.Deserialize(File.ReadAllText(fixture.OtherSidecarPath)).Entries);

        int exitCode = Program.Main([
            "identity-init",
            fixture.SelectedSourcePath,
            "--project", fixture.ProjectPath,
            "--out", fixture.SelectedSidecarPath,
            "--configuration", "Debug",
            "--no-restore",
            "--no-build",
            "--dotnet", Fixture.DotNetPath,
            "--reference", fixture.FriendAssemblyPath,
        ]);

        Assert.Equal(0, exitCode);
        IdentitySidecarDocument selected = IdentitySidecarJson.Deserialize(
            File.ReadAllText(fixture.SelectedSidecarPath));
        Assert.Equal("Classes/ProjectIdentityBase.cs", selected.SourcePath);
        Assert.NotEmpty(selected.Entries);
        IdentityEntry forward = Assert.Single(
            selected.Entries,
            static entry => entry.Kind == "function" && entry.Owner.Contains("Forward", StringComparison.Ordinal));
        Assert.True(forward.Anchor.Length > 0);
        IdentitySidecarDocument other = IdentitySidecarJson.Deserialize(
            File.ReadAllText(fixture.OtherSidecarPath));
        IdentityEntry readSeed = Assert.Single(
            other.Entries,
            static entry => entry.Kind == "function" && entry.Owner.Contains("ReadSeed", StringComparison.Ordinal));
        Assert.True(readSeed.Anchor.Length > 0);
        Assert.Equal(otherSidecar, File.ReadAllBytes(fixture.OtherSidecarPath));

        string reanalyzedSelectedPath = Path.Combine(fixture.Root, "ProjectIdentityBase.reanalyzed.cs.usids");
        Assert.Equal(0, Program.Main([
            "identity-init",
            fixture.SelectedSourcePath,
            "--project", fixture.ProjectPath,
            "--out", reanalyzedSelectedPath,
            "--configuration", "Debug",
            "--no-restore",
            "--no-build",
            "--dotnet", Fixture.DotNetPath,
            "--reference", fixture.FriendAssemblyPath,
        ]));
        IdentitySidecarDocument reanalyzedSelected = IdentitySidecarJson.Deserialize(
            File.ReadAllText(reanalyzedSelectedPath));
        Assert.Equal(
            selected.Entries.Select(static entry => entry.Id.Value).Order(StringComparer.Ordinal),
            reanalyzedSelected.Entries.Select(static entry => entry.Id.Value).Order(StringComparer.Ordinal));

        Assert.NotEqual(0, Program.Main([
            "identity-init",
            fixture.SelectedSourcePath,
            "--project", fixture.ProjectPath,
            "--out", fixture.OtherSourcePath,
            "--no-restore",
            "--no-build",
            "--dotnet", Fixture.DotNetPath,
            "--reference", fixture.FriendAssemblyPath,
        ]));
        Assert.Equal(originalOtherSource, File.ReadAllBytes(fixture.OtherSourcePath));
        Assert.Equal(originalProject, File.ReadAllBytes(fixture.ProjectPath));
        Assert.NotEqual(0, Program.Main([
            "identity-init",
            fixture.SelectedSourcePath,
            "--project", fixture.ProjectPath,
            "--out", fixture.ProjectPath,
            "--no-restore",
            "--no-build",
            "--dotnet", Fixture.DotNetPath,
            "--reference", fixture.FriendAssemblyPath,
        ]));
        Assert.Equal(originalOtherSource, File.ReadAllBytes(fixture.OtherSourcePath));
        Assert.Equal(originalProject, File.ReadAllBytes(fixture.ProjectPath));

        string unsafeOutput = fixture.OtherSidecarPath;
        Assert.NotEqual(0, Program.Main([
            "identity-init",
            fixture.SelectedSourcePath,
            "--project", fixture.ProjectPath,
            "--out", unsafeOutput,
            "--no-restore",
            "--no-build",
            "--dotnet", Fixture.DotNetPath,
            "--reference", fixture.FriendAssemblyPath,
        ]));
        Assert.Equal(otherSidecar, File.ReadAllBytes(fixture.OtherSidecarPath));

        string nonCompileSource = Path.Combine(fixture.Root, "NotCompile.cs");
        File.WriteAllText(nonCompileSource, "public sealed class NotCompile { }");
        string nonCompileOutput = nonCompileSource + ".usids";
        Assert.NotEqual(0, Program.Main([
            "identity-init",
            nonCompileSource,
            "--project", fixture.ProjectPath,
            "--out", nonCompileOutput,
            "--no-restore",
            "--no-build",
            "--dotnet", Fixture.DotNetPath,
        ]));
        Assert.False(File.Exists(nonCompileOutput));

        string singleFileOutput = Path.Combine(fixture.Root, "Single.cs.usids");
        Assert.NotEqual(0, Program.Main([
            "identity-init",
            fixture.SelectedSourcePath,
            "--out", singleFileOutput,
            "--no-build",
        ]));
        Assert.False(File.Exists(singleFileOutput));
    }

    [Fact]
    public void SingleFileIdentityInitRemainsAvailable()
    {
        using Fixture fixture = Fixture.Create();
        string sourcePath = Path.Combine(fixture.Root, "Single.cs");
        string outputPath = sourcePath + ".usids";
        File.WriteAllText(sourcePath, """
            using UnifiedScript;

            [ScriptAsset("Game/SingleIdentity")]
            public sealed class SingleIdentity
            {
                [Expose]
                public int Seed { get; set; } = 3;
            }
            """);

        Assert.Equal(0, Program.Main([
            "identity-init",
            sourcePath,
            "--out", outputPath,
            "--assembly", "SingleIdentityFixture",
            "--reference", Fixture.CoreAssemblyPath,
        ]));
        Assert.NotEmpty(IdentitySidecarJson.Deserialize(File.ReadAllText(outputPath)).Entries);
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(
            string root,
            string projectPath,
            string selectedSourcePath,
            string otherSourcePath,
            string otherSidecarPath,
            string friendAssemblyPath)
        {
            Root = root;
            ProjectPath = projectPath;
            SelectedSourcePath = selectedSourcePath;
            SelectedSidecarPath = selectedSourcePath + ".usids";
            OtherSourcePath = otherSourcePath;
            OtherSidecarPath = otherSidecarPath;
            FriendAssemblyPath = friendAssemblyPath;
        }

        public static string DotNetPath { get; } =
            File.Exists("D:/UE_5.8/Engine/Binaries/ThirdParty/DotNet/10.0/win-x64/dotnet.exe")
                ? "D:/UE_5.8/Engine/Binaries/ThirdParty/DotNet/10.0/win-x64/dotnet.exe"
                : "dotnet";

        public static string CoreAssemblyPath => typeof(SemanticId).Assembly.Location;

        public string Root { get; }
        public string ProjectPath { get; }
        public string SelectedSourcePath { get; }
        public string SelectedSidecarPath { get; }
        public string OtherSourcePath { get; }
        public string OtherSidecarPath { get; }
        public string FriendAssemblyPath { get; }

        public static Fixture Create()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "UnifiedScriptSourceIdentityInit",
                Guid.NewGuid().ToString("N"));
            string classes = Path.Combine(root, "Classes");
            Directory.CreateDirectory(classes);
            string selected = Path.Combine(classes, "ProjectIdentityBase.cs");
            string other = Path.Combine(classes, "ProjectIdentityBase.Part.cs");
            string project = Path.Combine(root, "SourceIdentityInitFixture.csproj");
            string friendSource = Path.Combine(root, "FriendSupport.cs");
            string friendProject = Path.Combine(root, "FriendSupport.csproj");
            string friendAssembly = Path.Combine(root, "bin", "Debug", "net10.0", "FriendSupport.dll");

            File.WriteAllText(selected, """
                using UnifiedScript;

                [ScriptAsset("Game/ProjectIdentityBase")]
                public partial class ProjectIdentityBase
                {
                #if PROJECT_SOURCE_IDENTITY
                    [Expose]
                    public int Seed { get; set; } = 3;
                #endif

                    public int ReadOther()
                    {
                        return OtherValue + FriendSupport.FriendValues.Value;
                    }

                    [Visual]
                    public int Forward()
                    {
                        return ReadSeed();
                    }
                }
                """);
            File.WriteAllText(other, """
                using UnifiedScript;

                public partial class ProjectIdentityBase
                {
                #if PROJECT_SOURCE_IDENTITY
                    [Expose]
                    public int OtherValue { get; set; } = 4;
                #endif

                    [Visual]
                    public int ReadSeed()
                    {
                        return Seed;
                    }
                }
                """);
            File.WriteAllText(project, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <AssemblyName>SourceIdentityInitFixture</AssemblyName>
                    <RootNamespace>SourceIdentityInitFixture</RootNamespace>
                    <DefineConstants>PROJECT_SOURCE_IDENTITY</DefineConstants>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Classes/**/*.cs" />
                  </ItemGroup>
                  <ItemGroup>
                    <Reference Include="UnifiedScript.Core">
                      <HintPath>{EscapeXml(CoreAssemblyPath)}</HintPath>
                    </Reference>
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(friendSource, """
                using System.Runtime.CompilerServices;

                [assembly: InternalsVisibleTo("SourceIdentityInitFixture")]
                namespace FriendSupport
                {
                    internal static class FriendValues
                    {
                        internal const int Value = 7;
                    }
                }
                """);
            File.WriteAllText(friendProject, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <AssemblyName>FriendSupport</AssemblyName>
                    <RootNamespace>FriendSupport</RootNamespace>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="FriendSupport.cs" />
                  </ItemGroup>
                </Project>
                """);
            BuildFriend(friendProject, root);
            Restore(project, root);
            return new Fixture(root, project, selected, other, other + ".usids", friendAssembly);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Keep cleanup from obscuring the command assertions.
            }
        }

        private static void Restore(string projectPath, string workingDirectory)
        {
            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = DotNetPath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    "restore", projectPath, "--nologo", "--ignore-failed-sources",
                    "-p:UnifiedScriptSkipManagedHostStaging=true",
                },
            }) ?? throw new InvalidOperationException("Could not start dotnet restore.");
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(
                process.ExitCode == 0,
                $"Temporary source identity project restore failed: {output}{Environment.NewLine}{error}");
        }

        private static void BuildFriend(string projectPath, string workingDirectory)
        {
            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = DotNetPath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    "build", projectPath, "--configuration", "Debug", "--nologo",
                    "-p:UnifiedScriptSkipManagedHostStaging=true",
                },
            }) ?? throw new InvalidOperationException("Could not start friend assembly build.");
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(
                process.ExitCode == 0,
                $"Temporary friend assembly build failed: {output}{Environment.NewLine}{error}");
        }

        private static string EscapeXml(string value) =>
            value.Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal)
                .Replace("\"", "&quot;", StringComparison.Ordinal);
    }
}
