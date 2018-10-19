﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;

namespace Buildalyzer.Workspaces
{
    public static class AnalyzerResultExtensions
    {
        // Cache the project references for projects we've already seen to avoid rebuilding
        private static ConcurrentDictionary<ProjectId, string[]> _projectReferences = new ConcurrentDictionary<ProjectId, string[]>();

        /// <summary>
        /// Gets a Roslyn workspace for the analyzed results.
        /// </summary>
        /// <param name="analyzerResult">The results from building a Buildalyzer project analyzer.</param>
        /// <param name="addProjectReferences">
        /// <c>true</c> to add projects to the workspace for project references that exist in the same <see cref="AnalyzerManager"/>.
        /// If <c>true</c> this will trigger (re)building all referenced projects. Directly add <see cref="AnalyzerResult"/> instances instead if you already have them available. 
        /// </param>
        /// <returns>A Roslyn workspace.</returns>
        public static AdhocWorkspace GetWorkspace(this AnalyzerResult analyzerResult, bool addProjectReferences = false)
        {
            if (analyzerResult == null)
            {
                throw new ArgumentNullException(nameof(analyzerResult));
            }
            AdhocWorkspace workspace = new AdhocWorkspace();
            analyzerResult.AddToWorkspace(workspace, addProjectReferences);
            return workspace;
        }

        /// <summary>
        /// Adds a result to an existing Roslyn workspace.
        /// </summary>
        /// <param name="analyzerResult">The results from building a Buildalyzer project analyzer.</param>
        /// <param name="workspace">A Roslyn workspace.</param>
        /// <param name="addProjectReferences">
        /// <c>true</c> to add projects to the workspace for project references that exist in the same <see cref="AnalyzerManager"/>.
        /// If <c>true</c> this will trigger (re)building all referenced projects. Directly add <see cref="AnalyzerResult"/> instances instead if you already have them available. 
        /// </param>
        /// <returns>The newly added Roslyn project.</returns>
        public static Project AddToWorkspace(this AnalyzerResult analyzerResult, Workspace workspace, bool addProjectReferences = false)
        {
            if (analyzerResult == null)
            {
                throw new ArgumentNullException(nameof(analyzerResult));
            }
            if (workspace == null)
            {
                throw new ArgumentNullException(nameof(workspace));
            }

            // Get or create an ID for this project
            ProjectId projectId = ProjectId.CreateFromSerialized(analyzerResult.ProjectGuid);

            // Cache the project references
            _projectReferences.AddOrUpdate(projectId, _ => analyzerResult.ProjectReferences.ToArray(), (_, __) => analyzerResult.ProjectReferences.ToArray());

            // Create and add the project
            ProjectInfo projectInfo = GetProjectInfo(analyzerResult, workspace, projectId);
            Solution solution = workspace.CurrentSolution.AddProject(projectInfo);

            // Check if this project is referenced by any other projects in the workspace
            foreach (Project existingProject in solution.Projects.ToArray())
            {
                if(!existingProject.Id.Equals(projectId)
                    && _projectReferences.TryGetValue(existingProject.Id, out string[] existingReferences)
                    && existingReferences.Contains(analyzerResult.ProjectFilePath))
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
                foreach(ProjectAnalyzer referencedAnalyzer in GetReferencedAnalyzerProjects(analyzerResult))
                {
                    // Check if the workspace contains the project inside the loop since adding one might also add this one due to transitive references
                    if(!workspace.CurrentSolution.Projects.Any(x => x.FilePath == referencedAnalyzer.ProjectFile.Path))
                    {
                        referencedAnalyzer.AddToWorkspace(workspace, addProjectReferences);
                    }
                }
            }

            // Find and return this project
            return workspace.CurrentSolution.GetProject(projectId);
        }

        private static ProjectInfo GetProjectInfo(AnalyzerResult analyzerResult, Workspace workspace, ProjectId projectId)
        {
            string projectName = Path.GetFileNameWithoutExtension(analyzerResult.ProjectFilePath);
            string languageName = GetLanguageName(analyzerResult.ProjectFilePath);
            ProjectInfo projectInfo = ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                projectName,
                projectName,
                languageName,
                filePath: analyzerResult.ProjectFilePath,
                outputFilePath: analyzerResult.GetProperty("TargetPath"),
                documents: GetDocuments(analyzerResult, projectId),
                projectReferences: GetExistingProjectReferences(analyzerResult, workspace),
                metadataReferences: GetMetadataReferences(analyzerResult),
                compilationOptions: CreateCompilationOptions(analyzerResult, languageName),
                parseOptions: CreateParseOptions(analyzerResult, languageName)
                );
            return projectInfo;
        }

        private static CompilationOptions CreateCompilationOptions(AnalyzerResult analyzerResult, string languageName)
        {
            string outputType = analyzerResult.GetProperty("OutputType");
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

        private static Microsoft.CodeAnalysis.CSharp.LanguageVersion ParseLanguageVersion(string str)
        {
            if(str != null)
            {
                if("latest".Equals(str, StringComparison.OrdinalIgnoreCase))
                {
                    return Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest;
                }
                if(Version.TryParse(str, out Version version))
                {
                    if(version.Major == 7)
                    {
                        if(version.Minor > 0)
                        {
                            return (Microsoft.CodeAnalysis.CSharp.LanguageVersion)(version.Major * 100 + version.Minor);
                        }
                        else
                        {
                            return Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp7;
                        }
                    }
                    else
                    {
                        return (Microsoft.CodeAnalysis.CSharp.LanguageVersion)(version.Major * 100 + version.Minor);
                    }
                }
            }
            return Microsoft.CodeAnalysis.CSharp.LanguageVersion.Default;
        }

        private static ParseOptions CreateParseOptions(AnalyzerResult analyzerResult, string languageName)
        {
            if(languageName == LanguageNames.CSharp)
            {
                var constants = analyzerResult.GetProperty("DefineConstants");
                var langverprop = analyzerResult.GetProperty("LangVersion");
                var ret = new CSharpParseOptions().WithLanguageVersion(ParseLanguageVersion(langverprop));
                if(constants != null)
                {
                    return ret.WithPreprocessorSymbols(constants.Split(';'));
                }
                else
                {
                    return ret;
                }
            }
            else if(languageName == LanguageNames.VisualBasic)
            {
                var constants = analyzerResult.GetProperty("DefineConstants");
                var ret = new VisualBasicParseOptions();
                if(constants != null)
                {
                    return ret.WithPreprocessorSymbols(constants.Split(';')
                        .Select(kv => kv.Split(new char[]{'='}, 2))
                        .Select(kv => new KeyValuePair<string, object>(kv[0].Trim(), kv[1].Trim())))
                        ;
                }
                else
                {
                    return ret;
                }
            }
            else
            {
                return null;
            }
        }

        private static IEnumerable<ProjectReference> GetExistingProjectReferences(AnalyzerResult analyzerResult, Workspace workspace) =>
            analyzerResult.ProjectReferences
                .Select(x => workspace.CurrentSolution.Projects.FirstOrDefault(y => y.FilePath == x))
                .Where(x => x != null)
                .Select(x => new ProjectReference(x.Id))
            ?? Array.Empty<ProjectReference>();

        private static IEnumerable<ProjectAnalyzer> GetReferencedAnalyzerProjects(AnalyzerResult analyzerResult) =>
            analyzerResult.ProjectReferences
                .Select(x => analyzerResult.Manager.Projects.TryGetValue(x, out ProjectAnalyzer a) ? a : null)
                .Where(x => x != null)
            ?? Array.Empty<ProjectAnalyzer>();

        private static IEnumerable<DocumentInfo> GetDocuments(AnalyzerResult analyzerResult, ProjectId projectId) =>
            analyzerResult
                .SourceFiles                ?.Where(File.Exists)
                .Select(x => DocumentInfo.Create(
                    DocumentId.CreateNewId(projectId),
                    Path.GetFileName(x),
                    loader: TextLoader.From(
                        TextAndVersion.Create(
                            SourceText.From(File.ReadAllText(x), Encoding.Default), VersionStamp.Create())),
                    filePath: x))
            ?? Array.Empty<DocumentInfo>();

        private static IEnumerable<MetadataReference> GetMetadataReferences(AnalyzerResult analyzerResult) =>
            analyzerResult
                .References                ?.Where(File.Exists)
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
    }
}