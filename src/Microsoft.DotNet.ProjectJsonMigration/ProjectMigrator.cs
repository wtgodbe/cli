// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Construction;
using Microsoft.DotNet.ProjectModel;
using Microsoft.DotNet.ProjectModel.Graph;
using Microsoft.DotNet.Cli;
using System.Linq;
using System.IO;
using Microsoft.DotNet.ProjectJsonMigration.Rules;
using Microsoft.DotNet.Tools.Common;
using Newtonsoft.Json.Linq;

namespace Microsoft.DotNet.ProjectJsonMigration
{
    public class ProjectMigrator
    {
        // TODO: Migrate PackOptions
        // TODO: Migrate Multi-TFM projects
        // TODO: Tests
        // TODO: Out of Scope
        //     - Globs that resolve to directories: /some/path/**/somedir
        //     - Migrating Deprecated project.jsons

        private readonly IMigrationRule _ruleSet;
        private readonly HashSet<string> _migratedProjects = new HashSet<string>();

        public ProjectMigrator() : this(new DefaultMigrationRuleSet()) { }

        public ProjectMigrator(IMigrationRule ruleSet)
        {
            _ruleSet = ruleSet;
        }

        public void Migrate(MigrationSettings settings, bool skipProjectReferences = false)
        {
            if (settings == null)
            {
                throw new ArgumentNullException();
            }

            Queue<MigrationSettings> settingsQueue = new Queue<MigrationSettings>();
            settingsQueue.Enqueue(settings);

            while(settingsQueue.Count() != 0)
            {
                var migrationSettings = settingsQueue.Dequeue();
                var migrationRuleInputs = ComputeMigrationRuleInputs(migrationSettings);
                VerifyInputs(migrationRuleInputs, migrationSettings);

                SetupOutputDirectory(migrationSettings.ProjectDirectory, migrationSettings.OutputDirectory);

                _ruleSet.Apply(migrationSettings, migrationRuleInputs);
                _migratedProjects.Add(migrationSettings.ProjectDirectory);

                if (skipProjectReferences)
                {
                    continue;
                }

                var projectContext = migrationRuleInputs.DefaultProjectContext;
                var projectExports = projectContext.CreateExporter("_").GetDependencies(LibraryType.Project);

                foreach(var export in projectExports)
                {
                    var projectFile = ((ProjectDescription)export.Library).Project.ProjectFilePath;
                    var projectDir = Path.GetDirectoryName(projectFile);

                    if (_migratedProjects.Contains(projectDir))
                    {
                        continue;
                    }

                    var referenceMigrationSettings = new MigrationSettings(migrationSettings);
                    referenceMigrationSettings.ProjectDirectory = projectDir;
                    referenceMigrationSettings.OutputDirectory = projectDir;
                    settingsQueue.Enqueue(referenceMigrationSettings);
                }
            }
        }

        private MigrationRuleInputs ComputeMigrationRuleInputs(MigrationSettings migrationSettings)
        {
            var projectContexts = ProjectContext.CreateContextForEachFramework(migrationSettings.ProjectDirectory);

            var templateMSBuildProject = migrationSettings.MSBuildProjectTemplate;
            if (templateMSBuildProject == null)
            {
                throw new Exception("Expected non-null MSBuildProjectTemplate in MigrationSettings");
            }

            var propertyGroup = templateMSBuildProject.AddPropertyGroup();
            var itemGroup = templateMSBuildProject.AddItemGroup();

            return new MigrationRuleInputs(projectContexts, templateMSBuildProject, itemGroup, propertyGroup);
        }

        private void VerifyInputs(MigrationRuleInputs migrationRuleInputs, MigrationSettings migrationSettings)
        {
            VerifyProject(migrationRuleInputs.ProjectContexts, migrationSettings.ProjectDirectory);
        }

        private void VerifyProject(IEnumerable<ProjectContext> projectContexts, string projectDirectory)
        {
            if (!projectContexts.Any())
            {
                MigrationErrorCodes.MIGRATE1013($"No projects found in {projectDirectory}").Throw();
            }

            var defaultProjectContext = projectContexts.First();

            if (defaultProjectContext.LockFile == null)
            {
                MigrationErrorCodes.MIGRATE1012(
                    $"project.lock.json not found in {projectDirectory}, please run dotnet restore before doing migration").Throw();
            }

            var diagnostics = defaultProjectContext.ProjectFile.Diagnostics;
            if (diagnostics.Any())
            {
                MigrationErrorCodes.MIGRATE1011(
                        $"{projectDirectory}{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics.Select(d => FormatDiagnosticMessage(d)))}")
                    .Throw();
            }

            var compilerName =
                defaultProjectContext.ProjectFile.GetCompilerOptions(defaultProjectContext.TargetFramework, "_")
                    .CompilerName;
            if (!compilerName.Equals("csc", StringComparison.OrdinalIgnoreCase))
            {
                MigrationErrorCodes.MIGRATE20013(
                    $"Cannot migrate project {defaultProjectContext.ProjectFile.ProjectFilePath} using compiler {compilerName}").Throw();
            }
        }

        private string FormatDiagnosticMessage(DiagnosticMessage d)
        {
            return $"{d.Message} (line: {d.StartLine}, file: {d.SourceFilePath})";
        }

        private void SetupOutputDirectory(string projectDirectory, string outputDirectory)
        {
            if (!Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            if (projectDirectory != outputDirectory)
            {
                CopyProjectToOutputDirectory(projectDirectory, outputDirectory);
            }
        }

        private void CopyProjectToOutputDirectory(string projectDirectory, string outputDirectory)
        {
            var sourceFilePaths = Directory.EnumerateFiles(projectDirectory, "*", SearchOption.AllDirectories);

            foreach (var sourceFilePath in sourceFilePaths)
            {
                var relativeFilePath = PathUtility.GetRelativePath(projectDirectory, sourceFilePath);
                var destinationFilePath = Path.Combine(outputDirectory, relativeFilePath);
                var destinationDirectory = Path.GetDirectoryName(destinationFilePath);

                if (!Directory.Exists(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                File.Copy(sourceFilePath, destinationFilePath);
            }
        }

    }
}
