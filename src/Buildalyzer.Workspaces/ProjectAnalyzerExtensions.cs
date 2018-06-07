﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Build.Execution;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using CS = Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;
using VB = Microsoft.CodeAnalysis.VisualBasic;

namespace Buildalyzer.Workspaces
{
    public static class ProjectAnalyzerExtensions
    {
        /// <summary>
        /// Gets a Roslyn workspace for the analyzed project.
        /// </summary>
        /// <param name="analyzer">The Buildalyzer project analyzer.</param>
        /// <param name="addProjectReferences"><c>true</c> to add projects to the workspace for project references that exist in the same <see cref="AnalyzerManager"/>.</param>
        /// <returns>A Roslyn workspace.</returns>
        public static AdhocWorkspace GetWorkspace(this ProjectAnalyzer analyzer, bool addProjectReferences = false)
        {
            if (analyzer == null)
            {
                throw new ArgumentNullException(nameof(analyzer));
            }
            AdhocWorkspace workspace = new AdhocWorkspace();
            AddToWorkspace(analyzer, workspace, addProjectReferences);
            return workspace;
        }

        /// <summary>
        /// Adds a project to an existing Roslyn workspace.
        /// </summary>
        /// <param name="analyzer">The Buildalyzer project analyzer.</param>
        /// <param name="workspace">A Roslyn workspace.</param>
        /// <param name="addProjectReferences"><c>true</c> to add projects to the workspace for project references that exist in the same <see cref="AnalyzerManager"/>.</param>
        /// <returns>The newly added Roslyn project.</returns>
        public static Project AddToWorkspace(this ProjectAnalyzer analyzer, Workspace workspace, bool addProjectReferences = false)
        {
            if (analyzer == null)
            {
                throw new ArgumentNullException(nameof(analyzer));
            }
            if (workspace == null)
            {
                throw new ArgumentNullException(nameof(workspace));
            }

            // Get or create an ID for this project
            string projectGuid = analyzer.CompiledProject?.GetPropertyValue("ProjectGuid");
            ProjectId projectId = !string.IsNullOrEmpty(projectGuid)
                && Guid.TryParse(analyzer.CompiledProject?.GetPropertyValue("ProjectGuid"), out var projectIdGuid) 
                ? ProjectId.CreateFromSerialized(projectIdGuid) 
                : ProjectId.CreateNewId();

            // Create and add the project
            ProjectInfo projectInfo = GetProjectInfo(analyzer, workspace, projectId);
            Solution solution = workspace.CurrentSolution.AddProject(projectInfo);

            // Check if this project is referenced by any other projects in the workspace
            foreach (Project existingProject in solution.Projects.ToArray())
            {
                if (!existingProject.Id.Equals(projectId)
                    && analyzer.Manager.Projects.TryGetValue(existingProject.FilePath, out ProjectAnalyzer existingAnalyzer)
                    && (existingAnalyzer.GetProjectReferences()?.Contains(analyzer.ProjectFilePath) ?? false))
                {
                    // Add the reference to the existing project
                    ProjectReference projectReference = new ProjectReference(projectId);
                    solution = solution.AddProjectReference(existingProject.Id, projectReference);
                }
            }

            // Apply solution changes
            if (!workspace.TryApplyChanges(solution))
            {
                throw new InvalidOperationException("Could not apply workspace solution changes");
            }

            // Add any project references not already added
            if(addProjectReferences)
            {
                foreach(ProjectAnalyzer referencedAnalyzer in GetReferencedAnalyzerProjects(analyzer))
                {
                    // Check if the workspace contains the project inside the loop since adding one might also add this one due to transitive references
                    if(!workspace.CurrentSolution.Projects.Any(x => x.FilePath == referencedAnalyzer.ProjectFilePath))
                    {
                        AddToWorkspace(referencedAnalyzer, workspace, addProjectReferences);
                    }
                }
            }

            // Find and return this project
            return workspace.CurrentSolution.GetProject(projectId);
        }

        private static ProjectInfo GetProjectInfo(ProjectAnalyzer analyzer, Workspace workspace, ProjectId projectId)
        {
            string projectName = Path.GetFileNameWithoutExtension(analyzer.ProjectFilePath);
            string languageName = GetLanguageName(analyzer.ProjectFilePath);
            ProjectInfo projectInfo = ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                projectName,
                projectName,
                languageName,
                filePath: analyzer.ProjectFilePath,
                outputFilePath: analyzer.CompiledProject?.GetPropertyValue("TargetPath"),
                documents: GetDocuments(analyzer, projectId),
                projectReferences: GetExistingProjectReferences(analyzer, workspace),
                metadataReferences: GetMetadataReferences(analyzer),
                compilationOptions: CreateCompilationOptions(analyzer.Project, languageName),
                parseOptions: CreateParseOptions(analyzer.Project, languageName));
            return projectInfo;
        }

        private static CompilationOptions CreateCompilationOptions(Microsoft.Build.Evaluation.Project project, string languageName)
        {
            string outputType = project.GetPropertyValue("OutputType");
            OutputKind? kind = null;
            switch (outputType)
            {
                case "Library":
                    kind = OutputKind.DynamicallyLinkedLibrary;
                    break;
                case "Exe":
                    kind = OutputKind.ConsoleApplication;
                    break;
                case "Module":
                    kind = OutputKind.NetModule;
                    break;
                case "Winexe":
                    kind = OutputKind.WindowsApplication;
                    break;
            }

            if (kind.HasValue)
            {
                if (languageName == LanguageNames.CSharp)
                {
                    return new CSharpCompilationOptions(kind.Value);
                }
                if (languageName == LanguageNames.VisualBasic)
                {
                    return new VisualBasicCompilationOptions(kind.Value);
                }
            }

            return null;
        }

        private static IEnumerable<ProjectReference> GetExistingProjectReferences(ProjectAnalyzer analyzer, Workspace workspace) =>
            analyzer.GetProjectReferences()
                ?.Select(x => workspace.CurrentSolution.Projects.FirstOrDefault(y => y.FilePath == x))
                .Where(x => x != null)
                .Select(x => new ProjectReference(x.Id))
            ?? Array.Empty<ProjectReference>();

        private static IEnumerable<ProjectAnalyzer> GetReferencedAnalyzerProjects(ProjectAnalyzer analyzer) =>
            analyzer.GetProjectReferences()
                    ?.Select(x => analyzer.Manager.Projects.TryGetValue(x, out ProjectAnalyzer a) ? a : null)
                    .Where(x => x != null)
            ?? Array.Empty<ProjectAnalyzer>();

        private static IEnumerable<DocumentInfo> GetDocuments(ProjectAnalyzer analyzer, ProjectId projectId) => 
            analyzer
                .GetSourceFiles()
                ?.Where(File.Exists)
                .Select(x => DocumentInfo.Create(
                    DocumentId.CreateNewId(projectId),
                    Path.GetFileName(x),
                    loader: TextLoader.From(
                        TextAndVersion.Create(
                            SourceText.From(File.ReadAllText(x)), VersionStamp.Create())),
                    filePath: x))
            ?? Array.Empty<DocumentInfo>();

        private static IEnumerable<MetadataReference> GetMetadataReferences(ProjectAnalyzer analyzer) => 
            analyzer
                .GetReferences()
                ?.Where(File.Exists)
                .Select(x => MetadataReference.CreateFromFile(x))
            ?? (IEnumerable<MetadataReference>)Array.Empty<MetadataReference>();

        private static string GetLanguageName(string projectPath)
        {
            switch (Path.GetExtension(projectPath))
            {
                case ".csproj":
                    return LanguageNames.CSharp;
                case ".vbproj":
                    return LanguageNames.VisualBasic;
                default:
                    throw new InvalidOperationException("Could not determine supported language from project path");
            }
        }

        static readonly Dictionary<string, CS.LanguageVersion> CSLangVersionMap = Enum.GetValues(typeof(CS.LanguageVersion))
            .Cast<CS.LanguageVersion>()
            .ToDictionary(x => x.ToDisplayString().ToLower(), x => x)
            ;
        static readonly Dictionary<string, VB.LanguageVersion> VBLangVersionMap = Enum.GetValues(typeof(VB.LanguageVersion))
            .Cast<VB.LanguageVersion>()
            .ToDictionary(x => x.ToDisplayString().ToLower(), x => x)
            ;
        static CS.LanguageVersion ConvertToCSLanguageVersion(string versionString)
        {
            if(string.IsNullOrEmpty(versionString))
            {
                return CS.LanguageVersion.Default;
            }
            else
            {
                versionString = versionString.ToLower();
                if(CSLangVersionMap.TryGetValue(versionString, out var ret))
                {
                    return ret;
                }
                else
                {
                    return CS.LanguageVersion.Default;
                }
            }
        }
        static VB.LanguageVersion ConvertToVBLanguageVersion(string versionString)
        {
            if(string.IsNullOrEmpty(versionString))
            {
                return VB.LanguageVersion.Default;
            }
            else
            {
                versionString = versionString.ToLower();
                if(VBLangVersionMap.TryGetValue(versionString, out var ret))
                {
                    return ret;
                }
                else
                {
                    return VB.LanguageVersion.Default;
                }
            }
        }

        private static ParseOptions CreateParseOptions(Microsoft.Build.Evaluation.Project project, string languageName)
        {
            var features = project.GetPropertyValue("Features").Split(';').Select(x => x.Split(new []{'='}, 2))
                .Where(x => x.Length > 0)
                .ToDictionary(x => x[0], x => x.Length == 1 ? "true" : x[1]);
            if(languageName == LanguageNames.CSharp)
            {
                return CSharpParseOptions.Default.WithFeatures(features)
                    .WithPreprocessorSymbols(project.GetPropertyValue("DefineConstants").Split(';'))
                    .WithLanguageVersion(ConvertToCSLanguageVersion(project.GetPropertyValue("LangVersion")))
                    ;
            }
            else if(languageName == LanguageNames.VisualBasic)
            {
                var defineConstants = project.GetPropertyValue("DefineConstants").Split(';')
                    .Select(x => x.Split(new []{'='}))
                    .Where(x => x.Length > 0)
                    .ToDictionary(x => x[0], x => (object)(x.Length == 1 ? "true" : x[1]))
                    ;
                return VisualBasicParseOptions.Default.WithFeatures(features)
                    .WithPreprocessorSymbols(defineConstants)
                    .WithLanguageVersion(ConvertToVBLanguageVersion(project.GetPropertyValue("LangVersion")))
                    ;
            }
            else
            {
                return null;
            }
        }
    }
}
